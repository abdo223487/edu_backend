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
/// Route: api/LectureAssignments
///
/// A LECTURE ASSIGNMENT is the homework counterpart to a LectureExam --
/// same idea, same access gate, attached directly to one Lecture, doesn't
/// touch Quiz/AssignmentCenters/GroupIds/shared Deadline at all.
///
/// The ONLY difference from LectureExamsController: there is NO timing.
/// No DurationInMinutes, no per-student "start" row, no personal countdown.
/// A student can open it and submit whenever they like, exactly once --
/// everything else (grading, takers roster, teacher review/edit) mirrors
/// LectureExamsController field-for-field.
///
///  GET  LectureAssignments/by-lecture/{lectureId}   (list of all assignments attached + this student's status on each, or [] if none)
///  POST LectureAssignments                           (multipart/form-data, same field naming as postLectureExamWithAuthMultipart minus DurationInMinutes)
///  GET  LectureAssignments/as-teacher/{lectureAssignmentId}
///  GET  LectureAssignments/as-student/{lectureAssignmentId}
///  POST LectureAssignments/grade                      body: { lectureAssignmentId, answers:[{questionId, answer}] }
///  GET  LectureAssignments/takers?lectureAssignmentId=..
///  GET  LectureAssignments/student-answers?lectureAssignmentId=..&studentId=..
///  POST LectureAssignments/change-answer-mark        body: { lectureAssignmentId, questionId, studentId, mark }
///  POST LectureAssignments/edit-question
///  POST LectureAssignments/edit
///  POST LectureAssignments/delete/{lectureAssignmentId}
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class LectureAssignmentsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _files;
    private readonly IWhatsAppService _whatsApp;
    private readonly ILogger<LectureAssignmentsController> _logger;
    private readonly Common.ITenantContext _tenant;

    public LectureAssignmentsController(AppDbContext db, IFileStorageService files, Common.ITenantContext tenant, IWhatsAppService whatsApp, ILogger<LectureAssignmentsController> logger)
    {
        _db = db;
        _files = files;
        _tenant = tenant;
        _whatsApp = whatsApp;
        _logger = logger;
    }

    // ── Access gate ─────────────────────────────────────────────────────
    // Field-for-field copy of LectureExamsController.StudentCanAccessLectureAsync.
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

    // Field-for-field copy of LectureExamsController.GetEligibleStudentsForLectureAsync.
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

    // Used by the video player: "does this lecture have assignment(s)
    // attached, and (for a student) have I submitted each one?" Returns 200
    // with data:[] when the lecture simply has none, so the client can
    // decide whether to show the "الواجب" button at all.
    [HttpGet("by-lecture/{lectureId:int}")]
    public async Task<IActionResult> GetByLecture(int lectureId)
    {
        var assignments = await _db.LectureAssignments.AsNoTracking().Include(a => a.Questions)
            .Where(a => a.LectureId == lectureId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();

        if (assignments.Count == 0) return Ok(new { data = Array.Empty<object>() });

        int? studentId = null;
        if (User.IsInRole(Roles.Student))
        {
            studentId = User.GetUserId();
            if (!await StudentCanAccessLectureAsync(studentId.Value, lectureId))
                return StatusCode(403, new { message = "Not unlocked for this lecture." });
        }

        var assignmentIds = assignments.Select(a => a.Id).ToList();

        var resultsByAssignment = studentId.HasValue
            ? await _db.LectureAssignmentResults.AsNoTracking()
                .Where(r => assignmentIds.Contains(r.LectureAssignmentId) && r.StudentId == studentId.Value)
                .ToDictionaryAsync(r => r.LectureAssignmentId, r => r)
            : new Dictionary<int, LectureAssignmentResult>();

        var data = assignments.Select(assignment =>
        {
            var result = studentId.HasValue && resultsByAssignment.TryGetValue(assignment.Id, out var r) ? r : null;

            return new
            {
                id = assignment.Id,
                title = assignment.Title,
                lectureId = assignment.LectureId,
                questionCount = assignment.Questions.Count,
                isTaken = result != null,
                score = result?.Score,
                totalMarks = result?.TotalMarks
            };
        });

        return Ok(new { data });
    }

    // Multipart create — same field naming as postLectureExamWithAuthMultipart
    // minus DurationInMinutes: Title, LectureId,
    // Questions[i][type|text|answer|mark|choices[j]], Questions[i].image (file)
    [HttpPost]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Create()
    {
        var form = await Request.ReadFormAsync();

        var title = form["Title"].ToString();
        var lectureId = int.Parse(form["LectureId"].ToString());

        var lectureExists = await _db.Lectures.AnyAsync(l => l.Id == lectureId);
        if (!lectureExists) return NotFound(new { message = "Lecture not found." });

        var assignment = new LectureAssignment
        {
            Title = title,
            LectureId = lectureId,
            TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
        };

        for (var i = 0; form.ContainsKey($"Questions[{i}][type]"); i++)
        {
            var question = new LectureAssignmentQuestion
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
                question.ImageUrl = await _files.SaveAsync(imageFile, "lecture-assignment-questions");

            assignment.Questions.Add(question);
        }

        _db.LectureAssignments.Add(assignment);
        await _db.SaveChangesAsync();

        // CreateLectureAssignmentPage only treats statusCode == 200 as
        // success (same convention as CreateLectureExamPage) so we return
        // 200 here instead of the more "correct" 201.
        return Ok(new { id = assignment.Id, title = assignment.Title, lectureId = assignment.LectureId });
    }

    [HttpGet("as-teacher/{lectureAssignmentId:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetAsTeacher(int lectureAssignmentId)
    {
        var assignment = await _db.LectureAssignments.AsNoTracking().Include(a => a.Questions)
            .FirstOrDefaultAsync(a => a.Id == lectureAssignmentId);
        if (assignment == null) return NotFound(new { message = "Lecture assignment not found." });

        var items = assignment.Questions.Select(q => new
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

    // Same raw-array + review-mode contract as LectureExamsController.GetAsStudent,
    // minus the personal-clock/auto-zero logic -- there's no time window
    // here, so the only two states are "hasn't submitted yet" (fresh
    // questions, no answers) and "already submitted" (review mode).
    [HttpGet("as-student/{lectureAssignmentId:int}")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> GetAsStudent(int lectureAssignmentId)
    {
        var assignment = await _db.LectureAssignments.Include(a => a.Questions).FirstOrDefaultAsync(a => a.Id == lectureAssignmentId);
        if (assignment == null) return NotFound(new { message = "Lecture assignment not found." });

        var studentId = User.GetUserId();
        if (!await StudentCanAccessLectureAsync(studentId, assignment.LectureId))
            return StatusCode(403, new { message = "Not unlocked for this lecture." });

        var priorResult = await _db.LectureAssignmentResults.Include(r => r.Answers)
            .FirstOrDefaultAsync(r => r.LectureAssignmentId == lectureAssignmentId && r.StudentId == studentId);

        var reviewMode = priorResult != null;
        if (reviewMode) Response.Headers["x-redirected-to"] = "review";

        var items = assignment.Questions.Select(q =>
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
    public async Task<IActionResult> Grade([FromBody] GradeLectureAssignmentRequest request)
    {
        var assignment = await _db.LectureAssignments.Include(a => a.Questions).FirstOrDefaultAsync(a => a.Id == request.LectureAssignmentId);
        if (assignment == null) return NotFound(new { message = "Lecture assignment not found." });

        var studentId = User.GetUserId();
        if (!await StudentCanAccessLectureAsync(studentId, assignment.LectureId))
            return StatusCode(403, new { message = "Not unlocked for this lecture." });

        var alreadySubmitted = await _db.LectureAssignmentResults
            .AnyAsync(r => r.LectureAssignmentId == assignment.Id && r.StudentId == studentId);
        if (alreadySubmitted)
            return Conflict(new { message = "تم تسليم هذا الواجب من قبل." });

        var totalMarks = assignment.Questions.Sum(q => q.Mark);
        var score = 0;

        var result = new LectureAssignmentResult { LectureAssignmentId = assignment.Id, StudentId = studentId, TotalMarks = totalMarks, TeacherId = assignment.TeacherId };

        foreach (var submitted in request.Answers ?? new())
        {
            var question = assignment.Questions.FirstOrDefault(q => q.Id == submitted.QuestionId);
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

            result.Answers.Add(new LectureAssignmentAnswer { QuestionId = submitted.QuestionId, Answer = submitted.Answer, MarkAwarded = awarded });
        }

        result.Score = score;
        _db.LectureAssignmentResults.Add(result);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Conflict(new { message = "تم تسليم هذا الواجب من قبل." });
        }

        // Same WhatsApp parent notification AssignmentsController.Submit
        // sends -- never lets a failure here block the submission itself.
        try
        {
            var lecture = await _db.Lectures.AsNoTracking().FirstOrDefaultAsync(l => l.Id == assignment.LectureId);
            var student = await _db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == studentId);
            var teacher = await _db.Teachers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == assignment.TeacherId);
            if (student != null && teacher != null && !string.IsNullOrWhiteSpace(student.ParentPhoneNumber))
            {
                var assignmentTitle = lecture != null ? $"{lecture.Name} - {assignment.Title}" : assignment.Title;
                var data = new ExamResultWhatsAppNotification(student.Name, teacher.Name, assignmentTitle, score, totalMarks);
                await _whatsApp.SendAssignmentResultNotificationAsync(student.ParentPhoneNumber!, data);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send WhatsApp lecture-assignment-result notification for LectureAssignmentResult {ResultId}.", result.Id);
        }

        return Ok(new GradeQuizResult(score, totalMarks));
    }

    // GET LectureAssignments/takers?lectureAssignmentId=..&p=..&q=..&submitted=..
    // Same convention as LectureExams/takers: full eligible roster instead
    // of submitters only, p/q paging, submitted=true/false narrows to only
    // takers or only non-takers.
    [HttpGet("takers")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetTakers(
        [FromQuery] int lectureAssignmentId, [FromQuery] int p = 1, [FromQuery] string? q = null, [FromQuery] bool? submitted = null)
    {
        var assignment = await _db.LectureAssignments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == lectureAssignmentId);
        if (assignment == null) return NotFound(new { message = "Lecture assignment not found." });

        var students = await GetEligibleStudentsForLectureAsync(assignment.LectureId);
        var results = await _db.LectureAssignmentResults.AsNoTracking().Where(r => r.LectureAssignmentId == lectureAssignmentId).ToListAsync();

        var trimmedQ = q?.Trim();

        IEnumerable<Student> filtered = students;
        if (!string.IsNullOrWhiteSpace(trimmedQ))
            filtered = filtered.Where(s =>
                s.Name.Contains(trimmedQ, StringComparison.OrdinalIgnoreCase) ||
                (s.Group?.Name != null && s.Group.Name.Contains(trimmedQ, StringComparison.OrdinalIgnoreCase)));

        var takers = filtered
            .Select(s => new { student = s, result = results.FirstOrDefault(r => r.StudentId == s.Id) })
            .Where(x => submitted == null || (submitted.Value ? x.result != null : x.result == null))
            .OrderBy(x => x.student.Name)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .Select(x => new TakerDto(
                x.student.Id.ToString(),
                x.student.Name,
                x.student.Group?.Name ?? "",
                x.result != null,
                x.result?.Score,
                x.result?.TotalMarks ?? 0,
                x.result?.GradedAt));

        return Ok(takers);
    }

    [HttpGet("student-answers")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetStudentAnswers([FromQuery] int lectureAssignmentId, [FromQuery] int studentId)
    {
        var assignment = await _db.LectureAssignments.AsNoTracking().Include(a => a.Questions).FirstOrDefaultAsync(a => a.Id == lectureAssignmentId);
        if (assignment == null) return NotFound(new { message = "Lecture assignment not found." });

        var result = await _db.LectureAssignmentResults.AsNoTracking().Include(r => r.Answers)
            .FirstOrDefaultAsync(r => r.LectureAssignmentId == lectureAssignmentId && r.StudentId == studentId);

        var items = assignment.Questions.Select(q =>
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
    public async Task<IActionResult> ChangeAnswerMark([FromBody] ChangeLectureAssignmentAnswerMarkRequest request)
    {
        var result = await _db.LectureAssignmentResults.Include(r => r.Answers)
            .FirstOrDefaultAsync(r => r.LectureAssignmentId == request.LectureAssignmentId && r.StudentId == request.StudentId);
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
    public async Task<IActionResult> EditQuestion([FromBody] EditLectureAssignmentQuestionRequest request)
    {
        var assignment = await _db.LectureAssignments.Include(a => a.Questions).FirstOrDefaultAsync(a => a.Id == request.LectureAssignmentId);
        if (assignment == null) return NotFound(new { message = "Lecture assignment not found." });

        var question = assignment.Questions.FirstOrDefault(q => q.Id == request.QuestionId);
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

    // Deliberately no group/unit/deadline/duration to touch here (a
    // LectureAssignment has none) -- just the title.
    [HttpPost("edit")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> EditLectureAssignment([FromBody] EditLectureAssignmentRequest request)
    {
        var assignment = await _db.LectureAssignments.FirstOrDefaultAsync(a => a.Id == request.LectureAssignmentId);
        if (assignment == null) return NotFound(new { message = "Lecture assignment not found." });

        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequest(new { message = "اسم الواجب مطلوب." });

        assignment.Title = request.Title;

        await _db.SaveChangesAsync();

        return Ok(new
        {
            id = assignment.Id,
            title = assignment.Title
        });
    }

    [HttpPost("delete/{lectureAssignmentId:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Delete(int lectureAssignmentId)
    {
        var assignment = await _db.LectureAssignments.FirstOrDefaultAsync(a => a.Id == lectureAssignmentId);
        if (assignment == null) return NotFound(new { message = "Lecture assignment not found." });

        _db.LectureAssignments.Remove(assignment);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Lecture assignment deleted." });
    }
}
