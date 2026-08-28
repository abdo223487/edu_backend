using EduApi.Common;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/LectureExams
///
/// A LECTURE EXAM is a completely separate exam kind from a normal Quiz --
/// same idea as a Material with a LectureId (see MaterialController): it's
/// attached directly to one Lecture (uploaded either while adding the
/// lecture's video, or later from the teacher's own player), doesn't touch
/// Quiz/QuizGroupLinks/QuizResults at all, and has no Unit/GroupIds/shared
/// Deadline of its own. Visibility is gated purely on being able to reach
/// the Lecture itself (same three-way check MaterialController.GetById
/// uses), and it never shows up in the normal per-unit exams list.
///
/// Timing is PERSONAL instead of a shared Deadline: the countdown starts
/// the moment a given student opens it (LectureExamStudentStart, created
/// lazily on their first GetAsStudent call) and runs for exactly
/// DurationInMinutes from that moment -- not from upload time.
///
///  GET  LectureExams/by-lecture/{lectureId}    (summary + this student's remaining time, or null if none)
///  POST LectureExams                            (multipart/form-data, see postLectureExamWithAuthMultipart)
///  GET  LectureExams/as-teacher/{lectureExamId}
///  GET  LectureExams/as-student/{lectureExamId} (starts the personal timer on first call)
///  POST LectureExams/grade                      body: { lectureExamId, answers:[{questionId, answer}] }
///  GET  LectureExams/takers?lectureExamId=..
///  GET  LectureExams/student-answers?lectureExamId=..&studentId=..
///  POST LectureExams/change-answer-mark        body: { lectureExamId, questionId, studentId, mark }
///  POST LectureExams/edit-question
///  POST LectureExams/edit
///  POST LectureExams/delete/{lectureExamId}
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class LectureExamsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _files;
    private readonly IWhatsAppService _whatsApp;
    private readonly ILogger<LectureExamsController> _logger;
    private readonly Common.ITenantContext _tenant;

    public LectureExamsController(AppDbContext db, IFileStorageService files, Common.ITenantContext tenant, IWhatsAppService whatsApp, ILogger<LectureExamsController> logger)
    {
        _db = db;
        _files = files;
        _tenant = tenant;
        _whatsApp = whatsApp;
        _logger = logger;
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    // ── Access gate ─────────────────────────────────────────────────────
    // Mirrors MaterialController.GetById's three-way gate exactly, just
    // keyed on a lectureId directly instead of on a Material row: full Unit
    // subscription OR a lecture-specific unlock when the lecture belongs to
    // a Unit; otherwise gated on unlocking the whole OnlineLesson container
    // it lives in, or (for a standalone lecture) a direct per-lecture unlock.
    private async Task<bool> StudentCanAccessLectureAsync(int studentId, int lectureId)
    {
        var lecture = await _db.Lectures.AsNoTracking()
            .Where(l => l.Id == lectureId)
            .Select(l => new { l.UnitId, l.OnlineLessonId })
            .FirstOrDefaultAsync();
        if (lecture == null) return false;

        if (lecture.UnitId.HasValue)
            return User.GetUnitIds().Contains(lecture.UnitId.Value)
                || await _db.StudentLectureUnlocks.AnyAsync(u => u.StudentId == studentId && u.LectureId == lectureId);

        return lecture.OnlineLessonId.HasValue
            ? await _db.StudentOnlineLessonUnlocks.AnyAsync(u =>
                u.StudentId == studentId && u.OnlineLessonId == lecture.OnlineLessonId.Value)
            : await _db.StudentLectureUnlocks.AnyAsync(u =>
                u.StudentId == studentId && u.LectureId == lectureId);
    }

    // Reverse of the gate above: every student who could reach this
    // lecture, used by GetTakers to build the roster (a lecture exam has no
    // GroupIds of its own to read from, unlike a standalone Quiz).
    private async Task<List<Student>> GetEligibleStudentsForLectureAsync(int lectureId)
    {
        var lecture = await _db.Lectures.AsNoTracking()
            .Where(l => l.Id == lectureId)
            .Select(l => new { l.UnitId, l.OnlineLessonId, l.GroupIdsCsv })
            .FirstOrDefaultAsync();
        if (lecture == null) return new();

        if (lecture.OnlineLessonId.HasValue)
        {
            var unlockedIds = await _db.StudentOnlineLessonUnlocks.AsNoTracking()
                .Where(u => u.OnlineLessonId == lecture.OnlineLessonId.Value)
                .Select(u => u.StudentId).ToListAsync();
            return await _db.Students.AsNoTracking().Include(s => s.Group)
                .Where(s => unlockedIds.Contains(s.Id)).ToListAsync();
        }

        var lectureGroupIds = string.IsNullOrEmpty(lecture.GroupIdsCsv)
            ? new List<int>() : lecture.GroupIdsCsv.Split(',').Select(int.Parse).ToList();

        var unlockedLectureIds = await _db.StudentLectureUnlocks.AsNoTracking()
            .Where(u => u.LectureId == lectureId).Select(u => u.StudentId).ToListAsync();

        return await _db.Students.AsNoTracking().Include(s => s.Group)
            .Where(s =>
                unlockedLectureIds.Contains(s.Id) ||
                (lecture.UnitId != null &&
                    s.UnitSubscriptions.Any(x => x.UnitId == lecture.UnitId.Value) &&
                    s.GroupMemberships.Any(m => lectureGroupIds.Contains(m.GroupId))))
            .ToListAsync();
    }

    // Get-or-lazily-create this student's personal start row. In normal use
    // this always succeeds and just returns the (possibly brand new) row.
    private async Task<LectureExamStudentStart> GetOrCreateStartAsync(LectureExam exam, int studentId)
    {
        var start = await _db.LectureExamStudentStarts
            .FirstOrDefaultAsync(s => s.LectureExamId == exam.Id && s.StudentId == studentId);
        if (start != null) return start;

        start = new LectureExamStudentStart
        {
            LectureExamId = exam.Id,
            StudentId = studentId,
            TeacherId = exam.TeacherId,
            StartedAt = DateTime.UtcNow
        };
        _db.LectureExamStudentStarts.Add(start);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // RACE-CONDITION FIX: same pattern as QuizResult's unique index --
            // two near-simultaneous first-opens for the same student+exam.
            // The unique (LectureExamId, StudentId) index rejects the second
            // insert; re-read whichever row actually won.
            _db.Entry(start).State = EntityState.Detached;
            start = await _db.LectureExamStudentStarts
                .FirstAsync(s => s.LectureExamId == exam.Id && s.StudentId == studentId);
        }

        return start;
    }

    // Personal deadline = the moment THIS student opened it + the exam's
    // configured duration. Full duration is shown/allowed if they haven't
    // opened it yet.
    private static TimeSpan RemainingFor(LectureExam exam, DateTime? startedAt, DateTime nowUtc)
    {
        var full = TimeSpan.FromMinutes(exam.DurationInMinutes);
        if (startedAt == null) return full;
        var remaining = startedAt.Value.AddMinutes(exam.DurationInMinutes) - nowUtc;
        if (remaining < TimeSpan.Zero) return TimeSpan.Zero;
        return remaining < full ? remaining : full;
    }

    // Used by the video player: "does this lecture have an exam attached,
    // and (for a student) what's my status/remaining time on it?" Returns
    // 200 with data:null when the lecture simply has no exam, so the client
    // can decide whether to show the "ابدأ الامتحان" button at all.
    [HttpGet("by-lecture/{lectureId:int}")]
    public async Task<IActionResult> GetByLecture(int lectureId)
    {
        var exam = await _db.LectureExams.AsNoTracking().Include(e => e.Questions)
            .FirstOrDefaultAsync(e => e.LectureId == lectureId);
        if (exam == null) return Ok(new { data = (object?)null });

        int? studentId = null;
        DateTime? startedAt = null;
        bool isTaken = false;
        int? score = null, totalMarks = null;

        if (User.IsInRole(Roles.Student))
        {
            studentId = User.GetUserId();
            if (!await StudentCanAccessLectureAsync(studentId.Value, lectureId))
                return StatusCode(403, new { message = "Not unlocked for this lecture." });

            var start = await _db.LectureExamStudentStarts.AsNoTracking()
                .FirstOrDefaultAsync(s => s.LectureExamId == exam.Id && s.StudentId == studentId.Value);
            startedAt = start?.StartedAt;

            var result = await _db.LectureExamResults.AsNoTracking()
                .FirstOrDefaultAsync(r => r.LectureExamId == exam.Id && r.StudentId == studentId.Value);
            if (result != null)
            {
                isTaken = true;
                score = result.Score;
                totalMarks = result.TotalMarks;
            }
        }

        var remaining = studentId.HasValue
            ? RemainingFor(exam, startedAt, DateTime.UtcNow)
            : TimeSpan.FromMinutes(exam.DurationInMinutes);

        return Ok(new
        {
            data = new
            {
                id = exam.Id,
                title = exam.Title,
                lectureId = exam.LectureId,
                durationInMinutes = (int)remaining.TotalMinutes,
                duration = FormatDuration(remaining),
                questionCount = exam.Questions.Count,
                started = startedAt != null,
                isTaken,
                score,
                totalMarks
            }
        });
    }

    // Multipart create — matches AuthService.postLectureExamWithAuthMultipart
    // field naming exactly: Title, LectureId, DurationInMinutes,
    // Questions[i][type|text|answer|mark|choices[j]], Questions[i].image (file)
    [HttpPost]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Create()
    {
        var form = await Request.ReadFormAsync();

        var title = form["Title"].ToString();
        var lectureId = int.Parse(form["LectureId"].ToString());
        var duration = int.Parse(form["DurationInMinutes"].ToString());

        var lectureExists = await _db.Lectures.AnyAsync(l => l.Id == lectureId);
        if (!lectureExists) return NotFound(new { message = "Lecture not found." });

        var exam = new LectureExam
        {
            Title = title,
            LectureId = lectureId,
            DurationInMinutes = duration,
            TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
        };

        for (var i = 0; form.ContainsKey($"Questions[{i}][type]"); i++)
        {
            var question = new LectureExamQuestion
            {
                Type = form[$"Questions[{i}][type]"].ToString(),
                Text = form[$"Questions[{i}][text]"].ToString(),
                Answer = form[$"Questions[{i}][answer]"].ToString(),
                Mark = int.Parse(form[$"Questions[{i}][mark]"].ToString())
            };

            var choices = new List<string>();
            for (var j = 0; form.ContainsKey($"Questions[{i}][choices][{j}]"); j++)
                choices.Add(form[$"Questions[{i}][choices][{j}]"].ToString());
            question.Choices = choices;

            var imageFile = form.Files[$"Questions[{i}].image"];
            if (imageFile != null)
                question.ImageUrl = await _files.SaveAsync(imageFile, "lecture-exam-questions");

            exam.Questions.Add(question);
        }

        _db.LectureExams.Add(exam);
        await _db.SaveChangesAsync();

        // CreateLectureExamPage only treats statusCode == 200 as success
        // (same convention as create_Exam.dart's CreateExamPage) so we
        // return 200 here instead of the more "correct" 201.
        return Ok(new { id = exam.Id, title = exam.Title, lectureId = exam.LectureId });
    }

    // Same raw-array contract as QuizzesController.GetAsTeacher.
    [HttpGet("as-teacher/{lectureExamId:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetAsTeacher(int lectureExamId)
    {
        var exam = await _db.LectureExams.AsNoTracking().Include(e => e.Questions)
            .FirstOrDefaultAsync(e => e.Id == lectureExamId);
        if (exam == null) return NotFound(new { message = "Lecture exam not found." });

        var items = exam.Questions.Select(q => new
        {
            id = q.Id,
            text = q.Text,
            mark = q.Mark,
            imageUrl = q.ImageUrl,
            questionType = q.Type,
            choices = q.Choices,
            correctAnswer = q.Answer
        });

        return Ok(items);
    }

    // Same raw-array + review-mode contract as QuizzesController.GetAsStudent,
    // except the "missed it" deadline case doesn't exist here in the same
    // way -- opening this endpoint for the first time IS what starts this
    // student's personal clock (see GetOrCreateStartAsync). Only a student
    // who opened it, let their personal window run out, and never submitted
    // gets the same auto-zero review treatment QuizzesController gives a
    // no-show after Deadline.
    [HttpGet("as-student/{lectureExamId:int}")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> GetAsStudent(int lectureExamId)
    {
        var exam = await _db.LectureExams.Include(e => e.Questions).FirstOrDefaultAsync(e => e.Id == lectureExamId);
        if (exam == null) return NotFound(new { message = "Lecture exam not found." });

        var studentId = User.GetUserId();
        if (!await StudentCanAccessLectureAsync(studentId, exam.LectureId))
            return StatusCode(403, new { message = "Not unlocked for this lecture." });

        var priorResult = await _db.LectureExamResults.Include(r => r.Answers)
            .FirstOrDefaultAsync(r => r.LectureExamId == lectureExamId && r.StudentId == studentId);

        if (priorResult == null)
        {
            // First time opening it (or re-opening before submitting) --
            // this is exactly what starts/continues this student's personal
            // countdown, unlike a normal Quiz's single shared Deadline.
            var start = await GetOrCreateStartAsync(exam, studentId);
            var personalDeadline = start.StartedAt.AddMinutes(exam.DurationInMinutes);

            if (DateTime.UtcNow > personalDeadline)
            {
                // Same auto-zero-review fallback QuizzesController.GetAsStudent
                // uses for a no-show past Deadline, just keyed on this
                // student's own window instead of a shared one.
                var totalMarks = exam.Questions.Sum(q => q.Mark);
                var autoZero = new LectureExamResult
                {
                    LectureExamId = exam.Id,
                    StudentId = studentId,
                    TotalMarks = totalMarks,
                    Score = 0,
                    TeacherId = exam.TeacherId
                };
                foreach (var q in exam.Questions)
                    autoZero.Answers.Add(new LectureExamAnswer { QuestionId = q.Id, Answer = "", MarkAwarded = 0 });

                _db.LectureExamResults.Add(autoZero);
                await _db.SaveChangesAsync();

                priorResult = autoZero;
            }
        }

        var reviewMode = priorResult != null;
        if (reviewMode) Response.Headers["x-redirected-to"] = "review";

        var items = exam.Questions.Select(q =>
        {
            var studentAnswer = priorResult?.Answers.FirstOrDefault(a => a.QuestionId == q.Id)?.Answer;
            return new
            {
                id = q.Id,
                text = q.Text,
                mark = q.Mark,
                imageUrl = q.ImageUrl,
                questionType = q.Type,
                choices = q.Choices,
                answer = reviewMode ? studentAnswer : null,
                correctAnswer = reviewMode ? q.Answer : null
            };
        });

        return Ok(items);
    }

    [HttpPost("grade")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Grade([FromBody] GradeLectureExamRequest request)
    {
        var exam = await _db.LectureExams.Include(e => e.Questions).FirstOrDefaultAsync(e => e.Id == request.LectureExamId);
        if (exam == null) return NotFound(new { message = "Lecture exam not found." });

        var studentId = User.GetUserId();
        if (!await StudentCanAccessLectureAsync(studentId, exam.LectureId))
            return StatusCode(403, new { message = "Not unlocked for this lecture." });

        var alreadySubmitted = await _db.LectureExamResults
            .AnyAsync(r => r.LectureExamId == exam.Id && r.StudentId == studentId);
        if (alreadySubmitted)
            return Conflict(new { message = "تم تسليم هذا الامتحان من قبل." });

        // Must have opened it through GetAsStudent first (that's what
        // starts the personal clock) -- a direct grade POST with no start
        // row is treated as "too late to even start", same spirit as
        // QuizzesController's Deadline check.
        var start = await _db.LectureExamStudentStarts
            .FirstOrDefaultAsync(s => s.LectureExamId == exam.Id && s.StudentId == studentId);
        if (start == null)
            return StatusCode(410, new { message = "انتهى وقت الامتحان." });

        var personalDeadline = start.StartedAt.AddMinutes(exam.DurationInMinutes);
        if (DateTime.UtcNow > personalDeadline)
            return StatusCode(410, new { message = "انتهى وقت الامتحان." });

        var totalMarks = exam.Questions.Sum(q => q.Mark);
        var score = 0;

        var result = new LectureExamResult { LectureExamId = exam.Id, StudentId = studentId, TotalMarks = totalMarks, TeacherId = exam.TeacherId };

        foreach (var submitted in request.Answers ?? new())
        {
            var question = exam.Questions.FirstOrDefault(q => q.Id == submitted.QuestionId);
            int? awarded = null;
            if (question != null && string.Equals(question.Answer, submitted.Answer, StringComparison.OrdinalIgnoreCase))
            {
                awarded = question.Mark;
                score += question.Mark;
            }
            else if (question != null)
            {
                awarded = 0;
            }

            result.Answers.Add(new LectureExamAnswer { QuestionId = submitted.QuestionId, Answer = submitted.Answer, MarkAwarded = awarded });
        }

        result.Score = score;
        _db.LectureExamResults.Add(result);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "تم تسليم هذا الامتحان من قبل." });
        }

        // Same WhatsApp parent notification QuizzesController.Grade sends --
        // never lets a failure here block the submission itself.
        try
        {
            var lecture = await _db.Lectures.AsNoTracking().FirstOrDefaultAsync(l => l.Id == exam.LectureId);
            var student = await _db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == studentId);
            var teacher = await _db.Teachers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == exam.TeacherId);
            if (student != null && teacher != null && !string.IsNullOrWhiteSpace(student.ParentPhoneNumber))
            {
                var examTitle = lecture != null ? $"{lecture.Name} - {exam.Title}" : exam.Title;
                var data = new ExamResultWhatsAppNotification(student.Name, teacher.Name, examTitle, score, totalMarks);
                await _whatsApp.SendQuizResultNotificationAsync(student.ParentPhoneNumber!, data);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send WhatsApp lecture-exam-result notification for LectureExamResult {ResultId}.", result.Id);
        }

        return Ok(new GradeQuizResult(score, totalMarks));
    }

    [HttpGet("takers")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetTakers([FromQuery] int lectureExamId)
    {
        var exam = await _db.LectureExams.AsNoTracking().FirstOrDefaultAsync(e => e.Id == lectureExamId);
        if (exam == null) return NotFound(new { message = "Lecture exam not found." });

        var students = await GetEligibleStudentsForLectureAsync(exam.LectureId);
        var results = await _db.LectureExamResults.AsNoTracking().Where(r => r.LectureExamId == lectureExamId).ToListAsync();

        var takers = students
            .Select(s => new { student = s, result = results.FirstOrDefault(r => r.StudentId == s.Id) })
            .Where(x => x.result != null)
            .Select(x => new TakerDto(
                x.student.Id.ToString(),
                x.student.Name,
                x.student.Group?.Name ?? "",
                true,
                x.result!.Score,
                x.result.TotalMarks,
                x.result.GradedAt));

        return Ok(takers);
    }

    [HttpGet("student-answers")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetStudentAnswers([FromQuery] int lectureExamId, [FromQuery] int studentId)
    {
        var exam = await _db.LectureExams.AsNoTracking().Include(e => e.Questions).FirstOrDefaultAsync(e => e.Id == lectureExamId);
        if (exam == null) return NotFound(new { message = "Lecture exam not found." });

        var result = await _db.LectureExamResults.AsNoTracking().Include(r => r.Answers)
            .FirstOrDefaultAsync(r => r.LectureExamId == lectureExamId && r.StudentId == studentId);

        var items = exam.Questions.Select(q =>
        {
            var answer = result?.Answers.FirstOrDefault(a => a.QuestionId == q.Id);
            return new
            {
                id = q.Id,
                text = q.Text,
                questionType = q.Type,
                choices = q.Choices,
                imageUrl = q.ImageUrl,
                correctAnswer = q.Answer,
                answer = answer?.Answer,
                mark = q.Mark,
                studentMark = answer?.MarkAwarded ?? 0
            };
        });

        return Ok(items);
    }

    [HttpPost("change-answer-mark")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangeAnswerMark([FromBody] ChangeLectureExamAnswerMarkRequest request)
    {
        var result = await _db.LectureExamResults.Include(r => r.Answers)
            .FirstOrDefaultAsync(r => r.LectureExamId == request.LectureExamId && r.StudentId == request.StudentId);
        if (result == null) return NotFound(new { message = "Result not found." });

        var answer = result.Answers.FirstOrDefault(a => a.QuestionId == request.QuestionId);
        if (answer == null) return NotFound(new { message = "Answer not found." });

        var oldMark = answer.MarkAwarded ?? 0;
        answer.MarkAwarded = request.Mark;
        result.Score = result.Score - oldMark + request.Mark;

        await _db.SaveChangesAsync();
        return Ok(new { message = "Mark updated.", newScore = result.Score });
    }

    [HttpPost("edit-question")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> EditQuestion([FromBody] EditLectureExamQuestionRequest request)
    {
        var exam = await _db.LectureExams.Include(e => e.Questions).FirstOrDefaultAsync(e => e.Id == request.LectureExamId);
        if (exam == null) return NotFound(new { message = "Lecture exam not found." });

        var question = exam.Questions.FirstOrDefault(q => q.Id == request.QuestionId);
        if (question == null) return NotFound(new { message = "Question not found." });

        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { message = "نص السؤال مطلوب." });
        if (request.Mark <= 0)
            return BadRequest(new { message = "درجة السؤال لازم تكون أكبر من صفر." });
        if (string.IsNullOrWhiteSpace(request.Answer))
            return BadRequest(new { message = "الإجابة الصحيحة مطلوبة." });

        question.Text = request.Text;
        question.Mark = request.Mark;
        question.Answer = request.Answer;
        if (request.Choices != null)
            question.Choices = request.Choices;

        await _db.SaveChangesAsync();

        return Ok(new
        {
            id = question.Id,
            text = question.Text,
            mark = question.Mark,
            imageUrl = question.ImageUrl,
            questionType = question.Type,
            choices = question.Choices,
            correctAnswer = question.Answer
        });
    }

    // Deliberately no group/unit/deadline to touch here (LectureExam has
    // none) -- just the same title/duration edit QuizzesController.EditQuiz
    // offers, minus AllowLateReview (a lecture exam always auto-reviews a
    // missed personal window, same as a Quiz with AllowLateReview left on).
    [HttpPost("edit")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> EditLectureExam([FromBody] EditLectureExamRequest request)
    {
        var exam = await _db.LectureExams.FirstOrDefaultAsync(e => e.Id == request.LectureExamId);
        if (exam == null) return NotFound(new { message = "Lecture exam not found." });

        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequest(new { message = "اسم الامتحان مطلوب." });
        if (request.DurationInMinutes <= 0)
            return BadRequest(new { message = "مدة الامتحان لازم تكون أكبر من صفر." });

        exam.Title = request.Title;
        exam.DurationInMinutes = request.DurationInMinutes;

        await _db.SaveChangesAsync();

        return Ok(new
        {
            id = exam.Id,
            title = exam.Title,
            durationInMinutes = exam.DurationInMinutes
        });
    }

    [HttpPost("delete/{lectureExamId:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Delete(int lectureExamId)
    {
        var exam = await _db.LectureExams.FirstOrDefaultAsync(e => e.Id == lectureExamId);
        if (exam == null) return NotFound(new { message = "Lecture exam not found." });

        _db.LectureExams.Remove(exam);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Lecture exam deleted." });
    }
}
