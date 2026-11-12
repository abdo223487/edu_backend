using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record UpsertExplanationRequest(string SourceType, int QuestionId, int LectureId, int StartSeconds, int? EndSeconds, string? Note);
public record ExplanationLookupItem(string SourceType, int QuestionId);
public record ExplanationLookupBatchRequest(List<ExplanationLookupItem>? Items);

/// <summary>
/// "السؤال مربوط بدقيقة الشرح": the teacher links a question to the second of a lecture video
/// that explains it; a student who got the question wrong taps "شوف الشرح" and the lecture opens
/// at exactly that second. Linked ONCE by the teacher, useful for every student afterwards.
///
/// Route: api/QuestionExplanations
///  TEACHER / ASSISTANT ADMIN
///   PUT  QuestionExplanations                      body { sourceType, questionId, lectureId, startSeconds, endSeconds?, note? }  (create or replace)
///   POST QuestionExplanations/{id}/delete
///   GET  QuestionExplanations?lectureId=&amp;sourceType=&amp;p=      the links the teacher already made
///   GET  QuestionExplanations/unlinked?unitId=&amp;lessonId=&amp;p=   bank questions WITHOUT a link, hardest first (what to link next)
///  STUDENT
///   GET  QuestionExplanations/lookup?sourceType=&amp;questionId=
///   POST QuestionExplanations/lookup-batch         body { items: [{ sourceType, questionId }] }  (max 60)
///   POST QuestionExplanations/{id}/opened          call when the student taps the button (usage counter)
///
/// sourceType: Bank | Quiz | LectureExam | LectureAssignment | Assignment
/// ("BankAttempt", which "أخطائي" uses for practice attempts, is accepted and treated as Bank).
///
/// ANSWER-LEAK GUARD: a student only ever receives an explanation for a question they have ALREADY
/// answered (and, for quizzes / assignments, only once the deadline has passed), the same rule
/// "أخطائي" applies to solutions. Otherwise the video could reveal the answer mid-exam.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class QuestionExplanationsController : ControllerBase
{
    private const int MaxBatch = 60;

    private static readonly HashSet<string> SourceTypes = new(StringComparer.Ordinal)
    {
        "Bank", "Quiz", "LectureExam", "LectureAssignment", "Assignment",
    };

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public QuestionExplanationsController(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    /// <summary>Maps the many spellings (BankAttempt, bank, quiz...) onto the stored value; null when unsupported.</summary>
    public static string? NormalizeSource(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();
        if (v.Equals("BankAttempt", StringComparison.OrdinalIgnoreCase)) return "Bank";
        foreach (var t in SourceTypes)
            if (t.Equals(v, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    private async Task<bool> QuestionExistsAsync(string type, int id)
    {
        var tenantId = _tenant.CurrentTenantId;
        switch (type)
        {
            case "Bank":
                return await _db.BankQuestions.AsNoTracking().AnyAsync(q => q.Id == id);
            case "Quiz":
                return await _db.Questions.AsNoTracking()
                    .AnyAsync(q => q.Id == id && q.Quiz != null && q.Quiz.TeacherId == tenantId);
            case "LectureExam":
                return await _db.LectureExamQuestions.AsNoTracking()
                    .AnyAsync(q => q.Id == id && q.LectureExam != null && q.LectureExam.TeacherId == tenantId);
            case "LectureAssignment":
                return await _db.LectureAssignmentQuestions.AsNoTracking()
                    .AnyAsync(q => q.Id == id && q.LectureAssignment != null && q.LectureAssignment.TeacherId == tenantId);
            case "Assignment":
                return await _db.AssignmentQuestions.AsNoTracking()
                    .AnyAsync(q => q.Id == id && q.Assignment != null && q.Assignment.TeacherId == tenantId);
            default:
                return false;
        }
    }

    private static string FormatTime(int seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    // ═════════════════════════ TEACHER ═════════════════════════

    [HttpPut]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Upsert([FromBody] UpsertExplanationRequest request)
    {
        var type = NormalizeSource(request.SourceType);
        if (type == null)
            return BadRequest(new { message = "sourceType must be Bank, Quiz, LectureExam, LectureAssignment or Assignment." });
        if (request.QuestionId <= 0 || request.LectureId <= 0)
            return BadRequest(new { message = "questionId and lectureId are required." });

        var start = Math.Clamp(request.StartSeconds, 0, 60 * 60 * 24);
        int? end = request.EndSeconds.HasValue ? Math.Clamp(request.EndSeconds.Value, 0, 60 * 60 * 24) : null;
        if (end.HasValue && end.Value <= start) end = null;

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note != null && note.Length > 300) note = note[..300];

        if (!await QuestionExistsAsync(type, request.QuestionId))
            return NotFound(new { message = "Question not found." });

        var lecture = await _db.Lectures.AsNoTracking()
            .Where(l => l.Id == request.LectureId)
            .Select(l => new { l.Id, l.Name, l.StorageFileKey, l.YoutubeLink })
            .FirstOrDefaultAsync();
        if (lecture == null) return NotFound(new { message = "Lecture not found." });
        if (string.IsNullOrWhiteSpace(lecture.StorageFileKey) && string.IsNullOrWhiteSpace(lecture.YoutubeLink))
            return BadRequest(new { message = "This lecture has no video to link to." });

        var tenantId = _tenant.CurrentTenantId;
        if (!tenantId.HasValue) return Forbid();

        var row = await _db.QuestionExplanations
            .FirstOrDefaultAsync(e => e.SourceType == type && e.QuestionId == request.QuestionId);
        var now = DateTime.UtcNow;
        if (row == null)
        {
            row = new QuestionExplanation
            {
                TeacherId = tenantId.Value,
                SourceType = type,
                QuestionId = request.QuestionId,
                CreatedAt = now,
            };
            _db.QuestionExplanations.Add(row);
        }
        row.LectureId = request.LectureId;
        row.StartSeconds = start;
        row.EndSeconds = end;
        row.Note = note;
        row.UpdatedAt = now;
        await _db.SaveChangesAsync();

        return Ok(new
        {
            id = row.Id,
            sourceType = row.SourceType,
            questionId = row.QuestionId,
            lectureId = row.LectureId,
            lectureName = lecture.Name,
            startSeconds = row.StartSeconds,
            startLabel = FormatTime(row.StartSeconds),
            endSeconds = row.EndSeconds,
            note = row.Note,
        });
    }

    [HttpPost("{id:int}/delete")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Delete(int id)
    {
        var row = await _db.QuestionExplanations.FirstOrDefaultAsync(e => e.Id == id);
        if (row == null) return NotFound(new { message = "Explanation not found." });
        _db.QuestionExplanations.Remove(row);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Deleted." });
    }

    [HttpGet]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> List([FromQuery] int? lectureId, [FromQuery] string? sourceType, [FromQuery] int p = 1)
    {
        p = Math.Max(1, p);
        var query = _db.QuestionExplanations.AsNoTracking().AsQueryable();
        if (lectureId.HasValue) query = query.Where(e => e.LectureId == lectureId.Value);
        var type = NormalizeSource(sourceType);
        if (type != null) query = query.Where(e => e.SourceType == type);

        var rows = await query
            .OrderByDescending(e => e.UpdatedAt)
            .Skip((p - 1) * PagingDefaults.PageSize).Take(PagingDefaults.PageSize)
            .ToListAsync();

        var lectureIds = rows.Select(r => r.LectureId).Distinct().ToList();
        var names = await _db.Lectures.AsNoTracking()
            .Where(l => lectureIds.Contains(l.Id)).Select(l => new { l.Id, l.Name })
            .ToDictionaryAsync(l => l.Id, l => l.Name);

        return Ok(rows.Select(r => new
        {
            id = r.Id,
            sourceType = r.SourceType,
            questionId = r.QuestionId,
            lectureId = r.LectureId,
            lectureName = names.GetValueOrDefault(r.LectureId, ""),
            startSeconds = r.StartSeconds,
            startLabel = FormatTime(r.StartSeconds),
            endSeconds = r.EndSeconds,
            note = r.Note,
            openCount = r.OpenCount,
            updatedAt = r.UpdatedAt,
        }));
    }

    /// <summary>
    /// "اربط إيه الأول؟": bank questions that still have NO explanation, the ones students get wrong
    /// the most first, each with candidate lectures (same course + lesson) to link to.
    /// </summary>
    [HttpGet("unlinked")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Unlinked([FromQuery] int? unitId, [FromQuery] int? lessonId, [FromQuery] int p = 1)
    {
        p = Math.Max(1, p);

        var linked = _db.QuestionExplanations.AsNoTracking()
            .Where(e => e.SourceType == "Bank").Select(e => e.QuestionId);

        var query = _db.BankQuestions.AsNoTracking().Include(q => q.Lesson)
            .Where(q => !linked.Contains(q.Id));
        if (lessonId.HasValue) query = query.Where(q => q.LessonId == lessonId.Value);
        if (unitId.HasValue)
            query = query.Where(q => q.UnitId == unitId.Value || (q.Lesson != null && q.Lesson.UnitId == unitId.Value));

        var candidates = await query.OrderByDescending(q => q.Id).Take(600).ToListAsync();
        if (candidates.Count == 0) return Ok(Array.Empty<object>());

        var ids = candidates.Select(c => c.Id).ToList();
        var answers = await _db.BankAttemptQuestions.AsNoTracking()
            .Where(aq => ids.Contains(aq.BankQuestionId) && aq.Attempt != null && aq.Attempt.SubmittedAt != null)
            .Select(aq => new { aq.BankQuestionId, aq.MarkAwarded, Mark = aq.BankQuestion!.Mark })
            .ToListAsync();
        var stats = answers.GroupBy(a => a.BankQuestionId).ToDictionary(
            g => g.Key,
            g => (Total: g.Count(), Wrong: g.Count(x => (x.MarkAwarded ?? 0) < x.Mark)));

        var ranked = candidates
            .Select(c =>
            {
                stats.TryGetValue(c.Id, out var s);
                var wrongRate = s.Total == 0 ? 0.0 : (double)s.Wrong / s.Total;
                return new { Q = c, s.Total, s.Wrong, WrongRate = wrongRate, Score = wrongRate * Math.Sqrt(s.Total) };
            })
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Total)
            .Skip((p - 1) * PagingDefaults.PageSize).Take(PagingDefaults.PageSize)
            .ToList();

        // Candidate lectures: same unit (+ same lesson when the question has one), with a video.
        var unitIds = ranked.Select(r => r.Q.UnitId ?? r.Q.Lesson?.UnitId).Where(u => u.HasValue).Select(u => u!.Value).Distinct().ToList();
        var lectures = await _db.Lectures.AsNoTracking()
            .Where(l => l.UnitId != null && unitIds.Contains(l.UnitId.Value)
                        && (l.StorageFileKey != null || l.YoutubeLink != null))
            .OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.Name, UnitId = l.UnitId!.Value, l.LessonIndex })
            .ToListAsync();

        return Ok(ranked.Select(r =>
        {
            var uId = r.Q.UnitId ?? r.Q.Lesson?.UnitId;
            var lessonIndex = r.Q.Lesson?.LessonIndex;
            var sameLesson = lectures.Where(l => l.UnitId == uId && lessonIndex.HasValue && l.LessonIndex == lessonIndex).ToList();
            var pool = sameLesson.Count > 0 ? sameLesson : lectures.Where(l => l.UnitId == uId).ToList();
            return new
            {
                questionId = r.Q.Id,
                text = r.Q.Text.Length > 140 ? r.Q.Text[..140] + "…" : r.Q.Text,
                difficulty = r.Q.Difficulty,
                lessonName = r.Q.Lesson?.Name,
                unitId = uId,
                answered = r.Total,
                wrong = r.Wrong,
                wrongPercent = Math.Round(r.WrongRate * 100, 1),
                candidateLectures = pool.Take(5).Select(l => new { id = l.Id, name = l.Name }).ToList(),
            };
        }));
    }

    // ═════════════════════════ STUDENT ═════════════════════════

    private sealed record ExplanationView(
        int Id, string SourceType, int QuestionId, int LectureId, string LectureName,
        int StartSeconds, int? EndSeconds, string? Note, bool Locked, int PositionSeconds, bool Completed);

    /// <summary>The answer-leak guard (see class summary).</summary>
    private async Task<bool> StudentMayViewAsync(int studentId, string type, int questionId)
    {
        var now = DateTime.UtcNow;
        switch (type)
        {
            case "Bank":
                return await _db.BankAttemptQuestions.AsNoTracking()
                    .AnyAsync(aq => aq.BankQuestionId == questionId && aq.Attempt != null
                                    && aq.Attempt.StudentId == studentId && aq.Attempt.SubmittedAt != null);

            case "Quiz":
            {
                var q = await _db.Questions.AsNoTracking().Where(x => x.Id == questionId)
                    .Select(x => new { x.QuizId, Deadline = x.Quiz!.Deadline }).FirstOrDefaultAsync();
                if (q == null || now <= q.Deadline) return false;
                return await _db.QuizResults.AsNoTracking()
                    .AnyAsync(r => r.StudentId == studentId && r.QuizId == q.QuizId && !r.IsAutoSubmitted);
            }

            case "Assignment":
            {
                var q = await _db.AssignmentQuestions.AsNoTracking().Where(x => x.Id == questionId)
                    .Select(x => new { x.AssignmentId, Deadline = x.Assignment!.Deadline }).FirstOrDefaultAsync();
                if (q == null || now <= q.Deadline) return false;
                return await _db.AssignmentSubmissions.AsNoTracking()
                    .AnyAsync(s => s.StudentId == studentId && s.AssignmentId == q.AssignmentId);
            }

            case "LectureExam":
            {
                var examId = await _db.LectureExamQuestions.AsNoTracking().Where(x => x.Id == questionId)
                    .Select(x => (int?)x.LectureExamId).FirstOrDefaultAsync();
                if (!examId.HasValue) return false;
                return await _db.LectureExamResults.AsNoTracking()
                    .AnyAsync(r => r.StudentId == studentId && r.LectureExamId == examId.Value);
            }

            case "LectureAssignment":
            {
                var asgId = await _db.LectureAssignmentQuestions.AsNoTracking().Where(x => x.Id == questionId)
                    .Select(x => (int?)x.LectureAssignmentId).FirstOrDefaultAsync();
                if (!asgId.HasValue) return false;
                return await _db.LectureAssignmentResults.AsNoTracking()
                    .AnyAsync(r => r.StudentId == studentId && r.LectureAssignmentId == asgId.Value);
            }

            default:
                return false;
        }
    }

    private async Task<ExplanationView?> BuildViewAsync(
        int studentId, string type, int questionId, Dictionary<int, LectureInfo?> lectureCache)
    {
        var row = await _db.QuestionExplanations.AsNoTracking()
            .FirstOrDefaultAsync(e => e.SourceType == type && e.QuestionId == questionId);
        if (row == null) return null;
        if (!await StudentMayViewAsync(studentId, type, questionId)) return null;

        if (!lectureCache.TryGetValue(row.LectureId, out var lecture))
        {
            lecture = await LectureAccess.GetAsync(_db, User, _tenant, row.LectureId);
            lectureCache[row.LectureId] = lecture;
        }

        // The student may not be able to open that lecture (not subscribed / not in its group):
        // tell them there IS an explanation but keep the video details hidden.
        if (lecture == null)
        {
            var name = await _db.Lectures.AsNoTracking().Where(l => l.Id == row.LectureId)
                .Select(l => l.Name).FirstOrDefaultAsync() ?? "";
            return new ExplanationView(row.Id, type, questionId, row.LectureId, name,
                row.StartSeconds, row.EndSeconds, row.Note, true, 0, false);
        }

        var pos = await _db.LecturePlaybackPositions.AsNoTracking()
            .Where(x => x.StudentId == studentId && x.LectureId == row.LectureId)
            .Select(x => new { x.PositionSeconds, x.Completed }).FirstOrDefaultAsync();

        return new ExplanationView(row.Id, type, questionId, row.LectureId, lecture.Name,
            row.StartSeconds, row.EndSeconds, row.Note, false, pos?.PositionSeconds ?? 0, pos?.Completed ?? false);
    }

    private static object ToDto(ExplanationView v) => new
    {
        id = v.Id,
        sourceType = v.SourceType,
        questionId = v.QuestionId,
        lectureId = v.LectureId,
        lectureName = v.LectureName,
        startSeconds = v.StartSeconds,
        startLabel = FormatTime(v.StartSeconds),
        endSeconds = v.EndSeconds,
        note = v.Note,
        locked = v.Locked,
        studentPositionSeconds = v.PositionSeconds,
        lectureCompleted = v.Completed,
    };

    [HttpGet("lookup")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Lookup([FromQuery] string? sourceType, [FromQuery] int questionId)
    {
        var type = NormalizeSource(sourceType);
        if (type == null || questionId <= 0)
            return Ok(new { hasExplanation = false, explanation = (object?)null });

        var view = await BuildViewAsync(User.GetUserId(), type, questionId, new Dictionary<int, LectureInfo?>());
        return Ok(new { hasExplanation = view != null, explanation = view == null ? null : ToDto(view) });
    }

    [HttpPost("lookup-batch")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> LookupBatch([FromBody] ExplanationLookupBatchRequest request)
    {
        var studentId = User.GetUserId();
        var cache = new Dictionary<int, LectureInfo?>();
        var results = new List<object>();

        var seen = new HashSet<string>();
        foreach (var item in (request.Items ?? new()).Take(MaxBatch))
        {
            var type = NormalizeSource(item.SourceType);
            if (type == null || item.QuestionId <= 0 || !seen.Add($"{type}:{item.QuestionId}")) continue;

            var view = await BuildViewAsync(studentId, type, item.QuestionId, cache);
            if (view != null) results.Add(ToDto(view));
        }
        return Ok(new { explanations = results });
    }

    [HttpPost("{id:int}/opened")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Opened(int id)
    {
        await _db.QuestionExplanations
            .Where(e => e.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.OpenCount, e => e.OpenCount + 1));
        return Ok(new { message = "OK" });
    }
}
