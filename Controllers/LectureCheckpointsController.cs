using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>A question for a checkpoint: either an existing bank question, or a new one typed on the spot.</summary>
/// <summary>ImageUrl (اختياري، للسؤال الجديد بس): الرابط اللي رجّعه POST teacher/image.</summary>
public record CheckpointQuestionInput(int? BankQuestionId, string? Text, List<string>? Choices, string? Answer, string? Difficulty, string? ImageUrl = null);
public record SaveCheckpointRequest(int LectureId, int Seconds, string? Title, int? MaxAttempts, bool? IsActive, List<CheckpointQuestionInput>? Questions);
public record CheckpointAnswerRequest(int QuestionId, string? Answer);

/// <summary>
/// "أسئلة وسط الفيديو" (Checkpoints). The teacher drops a checkpoint on a second of a lecture video
/// (YouTube or file) and attaches a question. While a student watches, when the playback reaches that
/// second the app pauses the video, the avatar says "استنى وحل السؤال ده الأول", and:
///   right answer  -> the video continues;
///   wrong answer  -> the avatar warns the student and asks ANOTHER question (the next one the teacher
///                    attached, else a question of the same lesson/course from the bank).
/// After MaxAttempts wrong answers in a row the video is allowed to continue (nobody gets stuck) and the
/// checkpoint asks again the next time the student watches. A passed checkpoint is not asked again.
///
/// Questions are normal question-bank questions (multiple choice), so they are reusable everywhere.
/// The correct answer is NEVER sent to the student while they can still retry; the server grades.
///
/// Route: api/LectureCheckpoints
///  TEACHER / ASSISTANT ADMIN
///   GET  LectureCheckpoints/teacher/lecture/{lectureId}   checkpoints + questions + statistics
///   POST LectureCheckpoints/teacher/image                 multipart: image  ->  { imageUrl }  (صورة سؤال جديد، زي بنك الأسئلة)
///   POST LectureCheckpoints/teacher                       body { lectureId, seconds, title?, maxAttempts?, questions: [...] }
///   PUT  LectureCheckpoints/teacher/{id}                  same body (lectureId ignored), replaces the questions
///   POST LectureCheckpoints/teacher/{id}/delete
///  STUDENT
///   GET  LectureCheckpoints/lecture/{lectureId}           the checkpoints of the lecture (no questions) + passed flag
///   POST LectureCheckpoints/{id}/question                 the question to ask now (shuffled choices, no answer)
///   POST LectureCheckpoints/{id}/answer                   body { questionId, answer }
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class LectureCheckpointsController : ControllerBase
{
    private const int MaxCheckpointsPerLecture = 30;
    private const int MaxQuestionsPerCheckpoint = 5;
    private const int MinGapSeconds = 3;
    private const int DefaultMaxAttempts = 3;

    private const long MaxImageBytes = 8 * 1024 * 1024;
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IFileStorageService _files;

    public LectureCheckpointsController(AppDbContext db, ITenantContext tenant, IFileStorageService files)
    {
        _db = db;
        _tenant = tenant;
        _files = files;
    }

    private static string Norm(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();

    private static string Label(int seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static List<int> ParseIds(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? new List<int>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x, out var v) ? v : 0).Where(v => v > 0).ToList();

    // ═════════════════════════ TEACHER ═════════════════════════

    private const string StaffWrite = $"{Roles.Teacher},{Roles.AssistantAdmin}";
    private const string StaffRead = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.Assistant}";

    [HttpGet("teacher/lecture/{lectureId:int}")]
    [Authorize(Roles = StaffRead)]
    public async Task<IActionResult> TeacherList(int lectureId)
    {
        var checkpoints = await _db.LectureCheckpoints.AsNoTracking()
            .Where(c => c.LectureId == lectureId).OrderBy(c => c.Seconds).ToListAsync();
        if (checkpoints.Count == 0) return Ok(Array.Empty<object>());

        var cpIds = checkpoints.Select(c => c.Id).ToList();
        var links = await _db.LectureCheckpointQuestions.AsNoTracking()
            .Where(l => cpIds.Contains(l.CheckpointId)).OrderBy(l => l.SortOrder).ToListAsync();
        var qIds = links.Select(l => l.BankQuestionId).Distinct().ToList();
        var questions = await _db.BankQuestions.AsNoTracking().Where(q => qIds.Contains(q.Id)).ToListAsync();

        var progress = await _db.LectureCheckpointProgresses.AsNoTracking()
            .Where(p => cpIds.Contains(p.CheckpointId))
            .Select(p => new { p.CheckpointId, p.Passed, p.PassedFirstTry, p.TotalWrong }).ToListAsync();
        var attempts = await _db.LectureCheckpointAttempts.AsNoTracking()
            .Where(a => cpIds.Contains(a.CheckpointId))
            .Select(a => new { a.CheckpointId, a.BankQuestionId, a.IsCorrect }).ToListAsync();

        return Ok(checkpoints.Select(c =>
        {
            var p = progress.Where(x => x.CheckpointId == c.Id).ToList();
            var a = attempts.Where(x => x.CheckpointId == c.Id).ToList();
            return new
            {
                id = c.Id,
                lectureId = c.LectureId,
                seconds = c.Seconds,
                timeLabel = Label(c.Seconds),
                title = c.Title,
                maxAttempts = c.MaxAttempts,
                isActive = c.IsActive,
                questions = links.Where(l => l.CheckpointId == c.Id)
                    .Select(l => questions.FirstOrDefault(q => q.Id == l.BankQuestionId))
                    .Where(q => q != null)
                    .Select(q => new
                    {
                        bankQuestionId = q!.Id,
                        text = q.Text,
                        imageUrl = q.ImageUrl,
                        choices = q.Choices,
                        answer = q.Answer,
                        attempts = a.Count(x => x.BankQuestionId == q.Id),
                        wrong = a.Count(x => x.BankQuestionId == q.Id && !x.IsCorrect),
                    }).ToList(),
                stats = new
                {
                    students = p.Count,
                    passed = p.Count(x => x.Passed),
                    passedFirstTry = p.Count(x => x.PassedFirstTry),
                    wrongAnswers = a.Count(x => !x.IsCorrect),
                    totalAnswers = a.Count,
                },
            };
        }));
    }

    /// <summary>Validates the questions and turns them into bank question ids (creating the new ones).</summary>
    private async Task<(List<int>? Ids, IActionResult? Error)> ResolveQuestionsAsync(int lectureId, List<CheckpointQuestionInput>? input)
    {
        if (input == null || input.Count == 0)
            return (null, BadRequest(new { message = "Add at least one question." }));
        if (input.Count > MaxQuestionsPerCheckpoint)
            return (null, BadRequest(new { message = $"At most {MaxQuestionsPerCheckpoint} questions per checkpoint." }));

        var tenantId = _tenant.CurrentTenantId;
        if (!tenantId.HasValue) return (null, Forbid());

        var ids = new List<int>();
        int? lessonId = null, unitId = null;
        var scopeResolved = false;

        foreach (var q in input)
        {
            if (q.BankQuestionId.HasValue)
            {
                var existing = await _db.BankQuestions.AsNoTracking().Where(b => b.Id == q.BankQuestionId.Value)
                    .Select(b => new { b.Id, b.Type, b.ChoicesCsv, b.Mark }).FirstOrDefaultAsync();
                if (existing == null) return (null, NotFound(new { message = $"Bank question {q.BankQuestionId} not found." }));
                if (!string.Equals(existing.Type, "MCQ", StringComparison.OrdinalIgnoreCase) || existing.ChoicesCsv.Length == 0)
                    return (null, BadRequest(new { message = "Only multiple-choice questions can be used in a video." }));
                if (!ids.Contains(existing.Id)) ids.Add(existing.Id);
                continue;
            }

            // A brand-new question typed by the teacher: it goes into the question bank (reusable later).
            var text = (q.Text ?? string.Empty).Trim();
            var choices = (q.Choices ?? new()).Select(c => (c ?? string.Empty).Trim()).Where(c => c.Length > 0).ToList();
            var answer = (q.Answer ?? string.Empty).Trim();
            if (text.Length == 0) return (null, BadRequest(new { message = "Question text is required." }));
            if (choices.Count < 2 || choices.Count > 6) return (null, BadRequest(new { message = "A question needs 2 to 6 choices." }));
            if (choices.Select(Norm).Distinct().Count() != choices.Count) return (null, BadRequest(new { message = "Choices must be different." }));
            if (!choices.Any(c => Norm(c) == Norm(answer))) return (null, BadRequest(new { message = "The correct answer must be one of the choices." }));

            if (!scopeResolved)
            {
                var lec = await _db.Lectures.AsNoTracking().Where(l => l.Id == lectureId)
                    .Select(l => new { l.UnitId, l.LessonIndex }).FirstOrDefaultAsync();
                if (lec?.UnitId != null)
                {
                    unitId = lec.UnitId;
                    if (lec.LessonIndex.HasValue)
                        lessonId = await _db.Lessons.AsNoTracking()
                            .Where(l => l.UnitId == lec.UnitId.Value && l.LessonIndex == lec.LessonIndex.Value)
                            .Select(l => (int?)l.Id).FirstOrDefaultAsync();
                }
                scopeResolved = true;
            }
            if (!lessonId.HasValue && !unitId.HasValue)
                return (null, BadRequest(new { message = "This lecture is not linked to a course, so a new question can't be saved. Pick questions from the bank instead." }));

            // الصورة (لو فيه) لازم تكون من اللي اترفع لنفس المدرس في مجلد بنك الأسئلة: "{tenant}/bank-questions/"
            // (على R2 الرابط {base}/{tenant}/bank-questions/{file}، وعلى القرص المحلي {base}/uploads/{tenant}/bank-questions/{file}).
            string? imageUrl = null;
            if (!string.IsNullOrWhiteSpace(q.ImageUrl))
            {
                var candidate = q.ImageUrl.Trim();
                if (!candidate.Contains($"/{tenantId.Value}/bank-questions/", StringComparison.OrdinalIgnoreCase))
                    return (null, BadRequest(new { message = "Invalid image. Upload it with POST LectureCheckpoints/teacher/image first." }));
                imageUrl = candidate;
            }

            var difficulty = q.Difficulty is "Easy" or "Medium" or "Hard" ? q.Difficulty : "Medium";
            var bank = new BankQuestion
            {
                TeacherId = tenantId.Value,
                // exactly one of LessonId / UnitId
                LessonId = lessonId,
                UnitId = lessonId.HasValue ? null : unitId,
                Type = "MCQ",
                Text = text,
                Answer = choices.First(c => Norm(c) == Norm(answer)),
                Mark = 1,
                Difficulty = difficulty!,
                ImageUrl = imageUrl,
            };
            bank.Choices = choices;
            _db.BankQuestions.Add(bank);
            await _db.SaveChangesAsync();
            ids.Add(bank.Id);
        }

        return (ids, null);
    }

    /// <summary>
    /// رفع صورة لسؤال جديد: نفس مكان وطريقة صور بنك الأسئلة (مجلد bank-questions الخاص بالمدرس).
    /// بيرجّع الرابط، والمدرس يبعته في ImageUrl بتاع السؤال وقت الحفظ.
    /// </summary>
    [HttpPost("teacher/image")]
    [Authorize(Roles = StaffWrite)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadImage()
    {
        var form = await Request.ReadFormAsync();
        var file = form.Files["image"] ?? form.Files.FirstOrDefault();
        if (file == null || file.Length == 0) return BadRequest(new { message = "No image was provided." });
        if (file.Length > MaxImageBytes) return BadRequest(new { message = "The image is too large (max 8 MB)." });

        var extension = Path.GetExtension(file.FileName ?? string.Empty).ToLowerInvariant();
        var looksLikeImage = (file.ContentType ?? string.Empty).StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                             || ImageExtensions.Contains(extension);
        if (!looksLikeImage) return BadRequest(new { message = "Only image files are allowed." });

        var url = await _files.SaveAsync(file, "bank-questions");
        return Ok(new { imageUrl = url });
    }

    [HttpPost("teacher")]
    [Authorize(Roles = StaffWrite)]
    public async Task<IActionResult> Create([FromBody] SaveCheckpointRequest request)
    {
        var tenantId = _tenant.CurrentTenantId;
        if (!tenantId.HasValue) return Forbid();
        if (request.LectureId <= 0) return BadRequest(new { message = "lectureId is required." });

        var lecture = await _db.Lectures.AsNoTracking().Where(l => l.Id == request.LectureId)
            .Select(l => new { l.Id, l.StorageFileKey, l.YoutubeLink }).FirstOrDefaultAsync();
        if (lecture == null) return NotFound(new { message = "Lecture not found." });
        if (string.IsNullOrWhiteSpace(lecture.StorageFileKey) && string.IsNullOrWhiteSpace(lecture.YoutubeLink))
            return BadRequest(new { message = "This lecture has no video." });

        var seconds = Math.Clamp(request.Seconds, 1, 60 * 60 * 24);
        var existing = await _db.LectureCheckpoints.AsNoTracking()
            .Where(c => c.LectureId == request.LectureId).Select(c => c.Seconds).ToListAsync();
        if (existing.Count >= MaxCheckpointsPerLecture)
            return BadRequest(new { message = $"At most {MaxCheckpointsPerLecture} checkpoints per lecture." });
        if (existing.Any(s => Math.Abs(s - seconds) < MinGapSeconds))
            return Conflict(new { message = "There is already a checkpoint at (almost) the same second." });

        var (ids, error) = await ResolveQuestionsAsync(request.LectureId, request.Questions);
        if (error != null) return error;

        var now = DateTime.UtcNow;
        var cp = new LectureCheckpoint
        {
            TeacherId = tenantId.Value,
            LectureId = request.LectureId,
            Seconds = seconds,
            Title = CleanTitle(request.Title),
            MaxAttempts = Math.Clamp(request.MaxAttempts ?? DefaultMaxAttempts, 1, 10),
            IsActive = request.IsActive ?? true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.LectureCheckpoints.Add(cp);
        await _db.SaveChangesAsync();

        for (var i = 0; i < ids!.Count; i++)
            _db.LectureCheckpointQuestions.Add(new LectureCheckpointQuestion
            {
                TeacherId = tenantId.Value, CheckpointId = cp.Id, BankQuestionId = ids[i], SortOrder = i,
            });
        await _db.SaveChangesAsync();

        return Ok(new { id = cp.Id, seconds = cp.Seconds, timeLabel = Label(cp.Seconds), questions = ids.Count });
    }

    [HttpPut("teacher/{id:int}")]
    [Authorize(Roles = StaffWrite)]
    public async Task<IActionResult> Update(int id, [FromBody] SaveCheckpointRequest request)
    {
        var tenantId = _tenant.CurrentTenantId;
        if (!tenantId.HasValue) return Forbid();

        var cp = await _db.LectureCheckpoints.FirstOrDefaultAsync(c => c.Id == id);
        if (cp == null) return NotFound(new { message = "Checkpoint not found." });

        var seconds = Math.Clamp(request.Seconds, 1, 60 * 60 * 24);
        var others = await _db.LectureCheckpoints.AsNoTracking()
            .Where(c => c.LectureId == cp.LectureId && c.Id != cp.Id).Select(c => c.Seconds).ToListAsync();
        if (others.Any(s => Math.Abs(s - seconds) < MinGapSeconds))
            return Conflict(new { message = "There is already a checkpoint at (almost) the same second." });

        var (ids, error) = await ResolveQuestionsAsync(cp.LectureId, request.Questions);
        if (error != null) return error;

        cp.Seconds = seconds;
        cp.Title = CleanTitle(request.Title);
        cp.MaxAttempts = Math.Clamp(request.MaxAttempts ?? cp.MaxAttempts, 1, 10);
        if (request.IsActive.HasValue) cp.IsActive = request.IsActive.Value;
        cp.UpdatedAt = DateTime.UtcNow;

        var oldLinks = await _db.LectureCheckpointQuestions.Where(l => l.CheckpointId == cp.Id).ToListAsync();
        _db.LectureCheckpointQuestions.RemoveRange(oldLinks);
        for (var i = 0; i < ids!.Count; i++)
            _db.LectureCheckpointQuestions.Add(new LectureCheckpointQuestion
            {
                TeacherId = tenantId.Value, CheckpointId = cp.Id, BankQuestionId = ids[i], SortOrder = i,
            });
        await _db.SaveChangesAsync();
        return Ok(new { id = cp.Id, seconds = cp.Seconds, timeLabel = Label(cp.Seconds), questions = ids.Count });
    }

    [HttpPost("teacher/{id:int}/delete")]
    [Authorize(Roles = StaffWrite)]
    public async Task<IActionResult> Delete(int id)
    {
        var cp = await _db.LectureCheckpoints.FirstOrDefaultAsync(c => c.Id == id);
        if (cp == null) return NotFound(new { message = "Checkpoint not found." });

        _db.LectureCheckpointAttempts.RemoveRange(await _db.LectureCheckpointAttempts.Where(a => a.CheckpointId == id).ToListAsync());
        _db.LectureCheckpointProgresses.RemoveRange(await _db.LectureCheckpointProgresses.Where(p => p.CheckpointId == id).ToListAsync());
        _db.LectureCheckpointQuestions.RemoveRange(await _db.LectureCheckpointQuestions.Where(l => l.CheckpointId == id).ToListAsync());
        _db.LectureCheckpoints.Remove(cp);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Deleted." });
    }

    private static string? CleanTitle(string? t)
    {
        var v = (t ?? string.Empty).Trim();
        if (v.Length == 0) return null;
        return v.Length > 120 ? v[..120] : v;
    }

    // ═════════════════════════ STUDENT ═════════════════════════

    [HttpGet("lecture/{lectureId:int}")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> ForLecture(int lectureId)
    {
        if (await LectureAccess.GetAsync(_db, User, _tenant, lectureId) == null)
            return NotFound(new { message = "Lecture not found." });

        var studentId = User.GetUserId();
        var cps = await _db.LectureCheckpoints.AsNoTracking()
            .Where(c => c.LectureId == lectureId && c.IsActive).OrderBy(c => c.Seconds).ToListAsync();
        if (cps.Count == 0) return Ok(new { checkpoints = Array.Empty<object>() });

        var cpIds = cps.Select(c => c.Id).ToList();
        // A checkpoint with no questions can't ask anything -- don't send it.
        var withQuestions = (await _db.LectureCheckpointQuestions.AsNoTracking()
            .Where(l => cpIds.Contains(l.CheckpointId)).Select(l => l.CheckpointId).Distinct().ToListAsync()).ToHashSet();
        var passed = (await _db.LectureCheckpointProgresses.AsNoTracking()
            .Where(p => p.StudentId == studentId && p.Passed && cpIds.Contains(p.CheckpointId))
            .Select(p => p.CheckpointId).ToListAsync()).ToHashSet();

        return Ok(new
        {
            checkpoints = cps.Where(c => withQuestions.Contains(c.Id)).Select(c => new
            {
                id = c.Id,
                seconds = c.Seconds,
                title = c.Title,
                maxAttempts = c.MaxAttempts,
                passed = passed.Contains(c.Id),
            }).ToList(),
        });
    }

    private async Task<(LectureCheckpoint? Cp, IActionResult? Error)> LoadCheckpointForStudentAsync(int id)
    {
        var cp = await _db.LectureCheckpoints.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.IsActive);
        if (cp == null) return (null, NotFound(new { message = "Checkpoint not found." }));
        if (await LectureAccess.GetAsync(_db, User, _tenant, cp.LectureId) == null)
            return (null, NotFound(new { message = "Lecture not found." }));
        return (cp, null);
    }

    private async Task<LectureCheckpointProgress> GetOrCreateProgressAsync(LectureCheckpoint cp, int studentId)
    {
        var p = await _db.LectureCheckpointProgresses.FirstOrDefaultAsync(x => x.CheckpointId == cp.Id && x.StudentId == studentId);
        if (p != null) return p;
        p = new LectureCheckpointProgress { TeacherId = cp.TeacherId, CheckpointId = cp.Id, StudentId = studentId };
        _db.LectureCheckpointProgresses.Add(p);
        return p;
    }

    private object QuestionDto(BankQuestion q, int studentId, int checkpointId, LectureCheckpointProgress p, int maxAttempts)
    {
        // Same shuffle for the same student and question (reopening the app doesn't reshuffle).
        var rnd = new Random(HashCode.Combine(q.Id, studentId, checkpointId, p.TotalAttempts));
        return new
        {
            questionId = q.Id,
            text = q.Text,
            imageUrl = q.ImageUrl,
            choices = q.Choices.OrderBy(_ => rnd.Next()).ToList(),
            attemptNo = p.WrongInRound + 1,
            maxAttempts,
            attemptsLeft = Math.Max(0, maxAttempts - p.WrongInRound),
        };
    }

    [HttpPost("{id:int}/question")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> NextQuestion(int id)
    {
        var (cp, error) = await LoadCheckpointForStudentAsync(id);
        if (error != null) return error;
        var studentId = User.GetUserId();

        var progress = await GetOrCreateProgressAsync(cp!, studentId);
        if (progress.Passed) return Ok(new { alreadyPassed = true });

        var linkIds = await _db.LectureCheckpointQuestions.AsNoTracking()
            .Where(l => l.CheckpointId == cp!.Id).OrderBy(l => l.SortOrder).Select(l => l.BankQuestionId).ToListAsync();
        if (linkIds.Count == 0) return Ok(new { alreadyPassed = true });

        // The student reopened the app in the middle of a question: give the same one back.
        int? pick = progress.CurrentQuestionId.HasValue && linkIds.Concat(ParseIds(progress.AskedCsv)).Contains(progress.CurrentQuestionId.Value)
            ? progress.CurrentQuestionId : null;

        if (pick == null)
        {
            var asked = ParseIds(progress.AskedCsv);

            // 1) the next question the teacher attached
            pick = linkIds.Cast<int?>().FirstOrDefault(q => !asked.Contains(q!.Value));

            // 2) another question of the same lesson / course from the bank
            if (pick == null)
            {
                var primary = await _db.BankQuestions.AsNoTracking().Where(b => b.Id == linkIds[0])
                    .Select(b => new { b.LessonId, b.UnitId }).FirstOrDefaultAsync();
                if (primary != null)
                {
                    var pool = _db.BankQuestions.AsNoTracking().Where(b => b.Type == "MCQ" && b.Mark > 0);
                    pool = primary.LessonId.HasValue
                        ? pool.Where(b => b.LessonId == primary.LessonId)
                        : pool.Where(b => b.UnitId == primary.UnitId);
                    var candidates = await pool.Select(b => b.Id).Take(300).ToListAsync();
                    var fresh = candidates.Where(c => !asked.Contains(c) && !linkIds.Contains(c)).ToList();
                    if (fresh.Count > 0) pick = fresh[new Random().Next(fresh.Count)];
                }
            }

            // 3) nothing new left: ask the teacher's questions again, round robin
            if (pick == null)
            {
                pick = linkIds[progress.TotalAttempts % linkIds.Count];
                progress.AskedCsv = string.Empty;
            }

            progress.CurrentQuestionId = pick;
            progress.AskedCsv = string.IsNullOrEmpty(progress.AskedCsv) ? pick.Value.ToString() : progress.AskedCsv + "," + pick.Value;
            progress.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        var q = await _db.BankQuestions.AsNoTracking().FirstOrDefaultAsync(b => b.Id == pick!.Value);
        if (q == null || q.Choices.Count < 2) return Ok(new { alreadyPassed = true });

        return Ok(new { alreadyPassed = false, question = QuestionDto(q, studentId, cp!.Id, progress, cp.MaxAttempts) });
    }

    [HttpPost("{id:int}/answer")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Answer(int id, [FromBody] CheckpointAnswerRequest request)
    {
        var (cp, error) = await LoadCheckpointForStudentAsync(id);
        if (error != null) return error;
        var studentId = User.GetUserId();

        var progress = await _db.LectureCheckpointProgresses
            .FirstOrDefaultAsync(p => p.CheckpointId == cp!.Id && p.StudentId == studentId);
        if (progress == null || progress.CurrentQuestionId != request.QuestionId)
            return Conflict(new { message = "Ask for the question first." });

        var q = await _db.BankQuestions.AsNoTracking().FirstOrDefaultAsync(b => b.Id == request.QuestionId);
        if (q == null) return NotFound(new { message = "Question not found." });

        var given = (request.Answer ?? string.Empty).Trim();
        var correct = given.Length > 0 && Norm(given) == Norm(q.Answer);
        var now = DateTime.UtcNow;

        _db.LectureCheckpointAttempts.Add(new LectureCheckpointAttempt
        {
            TeacherId = cp!.TeacherId, CheckpointId = cp.Id, StudentId = studentId, BankQuestionId = q.Id,
            Answer = given.Length > 500 ? given[..500] : given, IsCorrect = correct, CreatedAt = now,
        });

        progress.CurrentQuestionId = null;
        progress.TotalAttempts++;
        progress.UpdatedAt = now;

        if (correct)
        {
            progress.PassedFirstTry = progress.TotalWrong == 0;
            progress.Passed = true;
            progress.PassedAt = now;
            progress.WrongInRound = 0;
            progress.AskedCsv = string.Empty;
            await _db.SaveChangesAsync();
            return Ok(new { correct = true, passed = true, canContinue = true, firstTry = progress.PassedFirstTry });
        }

        progress.TotalWrong++;
        progress.WrongInRound++;

        if (progress.WrongInRound >= cp.MaxAttempts)
        {
            // Out of attempts: let the video go on, show the right answer so they learn, ask again next time.
            progress.WrongInRound = 0;
            progress.AskedCsv = string.Empty;
            await _db.SaveChangesAsync();
            return Ok(new { correct = false, passed = false, exhausted = true, canContinue = true, correctAnswer = q.Answer });
        }

        await _db.SaveChangesAsync();
        return Ok(new
        {
            correct = false, passed = false, exhausted = false, canContinue = false,
            hasNext = true, attemptsLeft = cp.MaxAttempts - progress.WrongInRound,
        });
    }
}
