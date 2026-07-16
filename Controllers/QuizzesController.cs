using System.Globalization;
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
/// Route: api/Quizzes
///  GET  Quizzes?schoolYear=..&p=..      (teacher list)
///  GET  Quizzes?p=..                    (student: quizzes available for their group)
///  POST Quizzes                         (multipart/form-data, see postExamWithAuthMultipart)
///  GET  Quizzes/as-teacher/{quizId}
///  GET  Quizzes/as-student/{quizId}
///  POST Quizzes/grade                   body: { quizId, answers:[{questionId, answer}] }
///  GET  Quizzes/takers?quizId=..
///  GET  Quizzes/student-answers?quizId=..&studentId=..
///  POST Quizzes/change-answer-mark      body: { quizId, questionId, studentId, mark }
///  POST Quizzes/delete/{quizId}
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class QuizzesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _files;

    private readonly Common.ITenantContext _tenant;

    public QuizzesController(AppDbContext db, IFileStorageService files, Common.ITenantContext tenant)
    {
        _db = db;
        _files = files;
        _tenant = tenant;
    }

    private static string FormatDuration(int minutes) => FormatDuration(TimeSpan.FromMinutes(minutes));

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    // BUGFIX: a student who opens the exam late used to still get the FULL
    // DurationInMinutes as their countdown (e.g. 10 minutes), even if only
    // 5 minutes were actually left before the Deadline — because the client
    // reads "duration"/"durationInMinutes" straight from this list endpoint
    // and starts its own timer from that value. Fix: for students, return
    // the ACTUAL remaining time (whichever is smaller — the full exam
    // duration, or however long is left until the Deadline) instead of the
    // fixed original duration. Teachers still see the exam's real configured
    // duration, since they're not taking a timed countdown.
    private static TimeSpan EffectiveRemaining(Quiz q, DateTime nowUtc)
    {
        var full = TimeSpan.FromMinutes(q.DurationInMinutes);
        var untilDeadline = q.Deadline - nowUtc;
        if (untilDeadline < TimeSpan.Zero) return TimeSpan.Zero;
        return untilDeadline < full ? untilDeadline : full;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? schoolYear, [FromQuery] int? unitId, [FromQuery] int p = 1)
    {
        var query = _db.Quizzes.AsNoTracking().AsQueryable();
        int? studentId = null;

        if (User.IsInRole(Roles.Student))
        {
            studentId = User.GetUserId();

            var groupId = User.GetGroupId(_tenant.CurrentTenantId);
            if (groupId.HasValue) query = query.Where(q => _db.QuizGroupLinks.Any(x => x.QuizId == q.Id && x.GroupId == groupId.Value));

            // Same subscription gate as Units/Lectures/Material/Notifications.
            // Quiz.UnitId is non-nullable (every quiz belongs to a unit), so
            // there's no "unscoped quiz" passthrough case here unlike the others.
            var subscribedIds = User.GetUnitIds();
            query = query.Where(q => subscribedIds.Contains(q.UnitId));
        }
        else if (schoolYear.HasValue)
        {
            query = query.Where(q => q.SchoolYear == schoolYear.Value);
        }

        if (unitId.HasValue) query = query.Where(q => q.UnitId == unitId.Value);

        var quizzes = await query
            .OrderByDescending(q => q.Id)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .ToListAsync();

        var quizIds = quizzes.Select(q => q.Id).ToList();
        var unitIds = quizzes.Select(q => q.UnitId).Distinct().ToList();
        var allGroupIds = quizzes.SelectMany(q => q.GroupIds).Distinct().ToList();

        var unitsById = await _db.Units.AsNoTracking().Where(u => unitIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Month);

        var groupNamesById = await _db.Groups.AsNoTracking().Where(g => allGroupIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => g.Name);

        var takenQuizIds = studentId.HasValue
            ? (await _db.QuizResults.AsNoTracking().Where(r => r.StudentId == studentId.Value && quizIds.Contains(r.QuizId))
                .Select(r => r.QuizId).ToListAsync()).ToHashSet()
            : new HashSet<int>();

        var nowUtc = DateTime.UtcNow;

        var items = quizzes.Select(q =>
        {
            // Students get the real remaining time (capped by the Deadline);
            // teachers/admins keep seeing the exam's actual configured duration.
            var effectiveDuration = studentId.HasValue
                ? EffectiveRemaining(q, nowUtc)
                : TimeSpan.FromMinutes(q.DurationInMinutes);

            return new
            {
                id = q.Id,
                title = q.Title,
                unitId = q.UnitId,
                durationInMinutes = (int)effectiveDuration.TotalMinutes,
                // Flutter's ExamDetailPage expects duration as "HH:MM:SS" (it does
                // widget.duration.split(":")), so format it here instead of sending
                // a raw minutes count.
                duration = FormatDuration(effectiveDuration),
                deadline = q.Deadline,
                groupIds = q.GroupIds,
                groups = q.GroupIds.Select(gid => groupNamesById.TryGetValue(gid, out var n) ? n : gid.ToString()).ToList(),
                unit = new { month = unitsById.TryGetValue(q.UnitId, out var m) ? m : (int?)null },
                grade = q.SchoolYear,
                isTaken = studentId.HasValue && takenQuizIds.Contains(q.Id)
            };
        });

        return Ok(items);
    }

    // Multipart create — matches AuthService.postExamWithAuthMultipart field naming exactly:
    // Title, GroupIds[i], UnitId, DurationInMinutes, Deadline,
    // Questions[i][type|text|answer|mark|choices[j]], Questions[i].image (file)
    [HttpPost]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Create()
    {
        var form = await Request.ReadFormAsync();

        var title = form["Title"].ToString();
        var unitId = int.Parse(form["UnitId"].ToString());
        var duration = int.Parse(form["DurationInMinutes"].ToString());
        var deadline = DateTime.Parse(form["Deadline"].ToString(), null, DateTimeStyles.RoundtripKind);

        var groupIds = new List<int>();
        for (var i = 0; form.ContainsKey($"GroupIds[{i}]"); i++)
            groupIds.Add(int.Parse(form[$"GroupIds[{i}]"].ToString()));

        // BUGFIX: SchoolYear was read from User.GetSchoolYear(), which reads the
        // "schoolYear" JWT claim — but that claim is only ever added for STUDENT
        // tokens (see TokenService/AuthController), never for Teacher tokens. So
        // every quiz created by a teacher was saved with SchoolYear = null, and
        // the teacher's own quiz list ("Quizzes?schoolYear=X") filters by exact
        // SchoolYear match, so the quiz they just created could never show up.
        // Fix: derive it from the quiz's own Unit instead (Unit.SchoolYear is
        // always set, and every quiz requires a UnitId).
        var schoolYear = await _db.Units.Where(u => u.Id == unitId)
            .Select(u => (int?)u.SchoolYear).FirstOrDefaultAsync();

        var quiz = new Quiz
        {
            Title = title,
            UnitId = unitId,
            DurationInMinutes = duration,
            Deadline = deadline,
            GroupIds = groupIds,
            SchoolYear = schoolYear,
            TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
        };

        for (var i = 0; form.ContainsKey($"Questions[{i}][type]"); i++)
        {
            var question = new Question
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
                question.ImageUrl = await _files.SaveAsync(imageFile, "quiz-questions");

            quiz.Questions.Add(question);
        }

        _db.Quizzes.Add(quiz);
        await _db.SaveChangesAsync();

        // create_Exam.dart only treats statusCode == 200 as success (it does
        // NOT check for 201), so we return 200 here instead of the more
        // "correct" 201 to match that check exactly.
        return Ok(new { id = quiz.Id, title = quiz.Title });
    }

    // Returns a RAW ARRAY of questions (not a wrapped quiz object) — the client
    // does `List<Map>.from(jsonDecode(response.body))` directly. Title/duration/
    // deadline are already known to the client from the Quizzes list screen.
    [HttpGet("as-teacher/{quizId:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetAsTeacher(int quizId)
    {
        var quiz = await _db.Quizzes.AsNoTracking().Include(q => q.Questions).FirstOrDefaultAsync(q => q.Id == quizId);
        if (quiz == null) return NotFound(new { message = "Quiz not found." });

        var items = quiz.Questions.Select(q => new
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

    // Same raw-array contract as as-teacher. If the student already submitted this
    // quiz, the response includes their answer + the correct answer per question,
    // and the "x-redirected-to" header is set so the client switches to review mode.
    [HttpGet("as-student/{quizId:int}")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> GetAsStudent(int quizId)
    {
        var quiz = await _db.Quizzes.Include(q => q.Questions).FirstOrDefaultAsync(q => q.Id == quizId);
        if (quiz == null) return NotFound(new { message = "Quiz not found." });

        if (!User.GetUnitIds().Contains(quiz.UnitId))
            return StatusCode(403, new { message = "Not subscribed to this unit." });

        var studentId = User.GetUserId();
        var priorResult = await _db.QuizResults.Include(r => r.Answers)
            .FirstOrDefaultAsync(r => r.QuizId == quizId && r.StudentId == studentId);

        // A student who never opened the exam and shows up after the deadline
        // doesn't get blocked out anymore — they get dropped straight into
        // review mode like anyone who actually took it, except every question
        // is unanswered and awarded 0. This auto-creates their QuizResult (0
        // score) right here so the teacher's "takers" list / student-answers
        // screen shows this student exactly like a real (zero-scoring) taker
        // — same DB row shape, same Score/TotalMarks/Answers as a genuine
        // submission, just with blank answers and MarkAwarded = 0 each.
        if (priorResult == null && DateTime.UtcNow > quiz.Deadline)
        {
            var totalMarks = quiz.Questions.Sum(q => q.Mark);
            var autoZero = new QuizResult
            {
                QuizId = quiz.Id,
                StudentId = studentId,
                TotalMarks = totalMarks,
                Score = 0,
                TeacherId = quiz.TeacherId
            };
            foreach (var q in quiz.Questions)
                autoZero.Answers.Add(new QuizAnswer { QuestionId = q.Id, Answer = "", MarkAwarded = 0 });

            _db.QuizResults.Add(autoZero);
            await _db.SaveChangesAsync();

            priorResult = autoZero;
        }

        var reviewMode = priorResult != null;
        if (reviewMode) Response.Headers["x-redirected-to"] = "review";

        var items = quiz.Questions.Select(q =>
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
    public async Task<IActionResult> Grade([FromBody] GradeQuizRequest request)
    {
        var quiz = await _db.Quizzes.Include(q => q.Questions).FirstOrDefaultAsync(q => q.Id == request.QuizId);
        if (quiz == null) return NotFound(new { message = "Quiz not found." });

        if (!User.GetUnitIds().Contains(quiz.UnitId))
            return StatusCode(403, new { message = "Not subscribed to this unit." });

        var studentId = User.GetUserId();

        // BUGFIX: same deadline gap as GetAsStudent — without this, a student
        // could still POST a grade after the exam window closed even if the
        // "get questions" step were fixed, since this is a separate endpoint.
        var alreadySubmitted = await _db.QuizResults
            .AnyAsync(r => r.QuizId == quiz.Id && r.StudentId == studentId);
        if (!alreadySubmitted && DateTime.UtcNow > quiz.Deadline)
            return StatusCode(410, new { message = "انتهى وقت الامتحان." });

        var totalMarks = quiz.Questions.Sum(q => q.Mark);
        var score = 0;

        var result = new QuizResult { QuizId = quiz.Id, StudentId = studentId, TotalMarks = totalMarks, TeacherId = quiz.TeacherId };

        foreach (var submitted in request.Answers ?? new())
        {
            var question = quiz.Questions.FirstOrDefault(q => q.Id == submitted.QuestionId);
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

            result.Answers.Add(new QuizAnswer { QuestionId = submitted.QuestionId, Answer = submitted.Answer, MarkAwarded = awarded });
        }

        result.Score = score;
        _db.QuizResults.Add(result);
        await _db.SaveChangesAsync();

        return Ok(new GradeQuizResult(score, totalMarks));
    }

    [HttpGet("takers")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetTakers([FromQuery] int quizId)
    {
        var quiz = await _db.Quizzes.AsNoTracking().FirstOrDefaultAsync(e => e.Id == (quizId));
        if (quiz == null) return NotFound(new { message = "Quiz not found." });

        var groupIds = quiz.GroupIds;
        // MULTI-TENANT: match via GroupMemberships, not the legacy single GroupId.
        var students = await _db.Students.AsNoTracking().Include(s => s.Group)
            .Where(s => s.GroupMemberships.Any(m => groupIds.Contains(m.GroupId))).ToListAsync();
        var results = await _db.QuizResults.AsNoTracking().Where(r => r.QuizId == quizId).ToListAsync();

        // Takers.dart's UI (score bar, pct calc) assumes every returned taker has
        // actually submitted, so only include students with a result — matches
        // "الممتحنين" (those who took the exam), not the full roster.
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
    public async Task<IActionResult> GetStudentAnswers([FromQuery] int quizId, [FromQuery] int studentId)
    {
        var quiz = await _db.Quizzes.AsNoTracking().Include(q => q.Questions).FirstOrDefaultAsync(q => q.Id == quizId);
        if (quiz == null) return NotFound(new { message = "Quiz not found." });

        var result = await _db.QuizResults.AsNoTracking().Include(r => r.Answers)
            .FirstOrDefaultAsync(r => r.QuizId == quizId && r.StudentId == studentId);

        var items = quiz.Questions.Select(q =>
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
    public async Task<IActionResult> ChangeAnswerMark([FromBody] ChangeAnswerMarkRequest request)
    {
        var result = await _db.QuizResults.Include(r => r.Answers)
            .FirstOrDefaultAsync(r => r.QuizId == request.QuizId && r.StudentId == request.StudentId);
        if (result == null) return NotFound(new { message = "Result not found." });

        var answer = result.Answers.FirstOrDefault(a => a.QuestionId == request.QuestionId);
        if (answer == null) return NotFound(new { message = "Answer not found." });

        var oldMark = answer.MarkAwarded ?? 0;
        answer.MarkAwarded = request.Mark;
        result.Score = result.Score - oldMark + request.Mark;

        await _db.SaveChangesAsync();
        return Ok(new { message = "Mark updated.", newScore = result.Score });
    }

    [HttpPost("delete/{quizId:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Delete(int quizId)
    {
        var quiz = await _db.Quizzes.FirstOrDefaultAsync(e => e.Id == (quizId));
        if (quiz == null) return NotFound(new { message = "Quiz not found." });

        _db.Quizzes.Remove(quiz);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Quiz deleted." });
    }
}
