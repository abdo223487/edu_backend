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
            .Select(l => new { l.UnitId, l.ExternalBookId, l.OnlineLessonId })
            .FirstOrDefaultAsync();
        if (lecture == null) return false;

        if (lecture.UnitId.HasValue)
            return (await Common.StudentAccessHelpers.GetEffectiveUnitIdsAsync(_db, User, studentId)).Contains(lecture.UnitId.Value)
                || await _db.StudentLectureUnlocks.AnyAsync(u => u.StudentId == studentId && u.LectureId == lectureId);

        if (lecture.ExternalBookId.HasValue)
            return (await Common.StudentAccessHelpers.GetEffectiveExternalBookIdsAsync(_db, User, studentId)).Contains(lecture.ExternalBookId.Value)
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
            .Select(l => new { l.UnitId, l.ExternalBookId, l.OnlineLessonId, l.GroupIdsCsv })
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

        // External-book lectures have no GroupIds concept of their own, so
        // any student who can reach the book gets the assignment/exam --
        // direct redeemed-code subscription OR access via the book's linked Unit.
        if (lecture.ExternalBookId.HasValue)
        {
            var bookUnitId = await _db.ExternalBooks.AsNoTracking()
                .Where(e => e.Id == lecture.ExternalBookId.Value)
                .Select(e => e.UnitId).FirstOrDefaultAsync();

            var unlockedIds = await _db.StudentLectureUnlocks.AsNoTracking()
                .Where(u => u.LectureId == lectureId).Select(u => u.StudentId).ToListAsync();
            var directBookIds = await _db.StudentExternalBookSubscriptions.AsNoTracking()
                .Where(s => s.ExternalBookId == lecture.ExternalBookId.Value)
                .Select(s => s.StudentId).ToListAsync();

            return await _db.Students.AsNoTracking().Include(s => s.Group)
                .Where(s =>
                    unlockedIds.Contains(s.Id) ||
                    directBookIds.Contains(s.Id) ||
                    (bookUnitId != null && s.UnitSubscriptions.Any(x => x.UnitId == bookUnitId.Value)))
                .ToListAsync();
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

    // Field-for-field copy of LectureExamsController.GetAccessibleLectureIdsForStudentAsync.
    private async Task<List<int>> GetAccessibleLectureIdsForStudentAsync(int studentId)
    {
        var subscribedUnitIds = await _db.StudentUnitSubscriptions.AsNoTracking()
            .Where(su => su.StudentId == studentId)
            .Select(su => su.UnitId)
            .ToListAsync();

        var directlyUnlockedLectureIds = await _db.StudentLectureUnlocks.AsNoTracking()
            .Where(u => u.StudentId == studentId)
            .Select(u => u.LectureId)
            .ToListAsync();

        var unlockedOnlineLessonIds = await _db.StudentOnlineLessonUnlocks.AsNoTracking()
            .Where(u => u.StudentId == studentId)
            .Select(u => u.OnlineLessonId)
            .ToListAsync();

        // Same "direct code subscription OR access via the book's linked
        // Unit" rule as ExternalBooksController.IsSubscribedAsync.
        var accessibleBookIds = (await Common.StudentAccessHelpers
            .GetEffectiveExternalBookIdsAsync(_db, User, studentId)).ToList();

        return await _db.Lectures.AsNoTracking()
            .Where(l =>
                (l.UnitId != null && subscribedUnitIds.Contains(l.UnitId.Value)) ||
                directlyUnlockedLectureIds.Contains(l.Id) ||
                (l.OnlineLessonId != null && unlockedOnlineLessonIds.Contains(l.OnlineLessonId.Value)) ||
                (l.ExternalBookId != null && accessibleBookIds.Contains(l.ExternalBookId.Value)))
            .Select(l => l.Id)
            .ToListAsync();
    }

    // GET LectureAssignments?studentId=..&p=.. — teacher viewing a specific
    // student's lecture-assignment list, feeding TeacherStudentAssignmentsPage's
    // "واجبات الحصص" tab. Same idea as LectureExamsController.GetAll.
    [HttpGet]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetAll([FromQuery] int studentId, [FromQuery] int p = 1)
    {
        var accessibleLectureIds = await GetAccessibleLectureIdsForStudentAsync(studentId);

        var assignments = await _db.LectureAssignments.AsNoTracking()
            .Where(a => accessibleLectureIds.Contains(a.LectureId))
            .OrderByDescending(a => a.Id)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .ToListAsync();

        var assignmentIds = assignments.Select(a => a.Id).ToList();
        var lectureIds = assignments.Select(a => a.LectureId).Distinct().ToList();

        var lectureNamesById = await _db.Lectures.AsNoTracking()
            .Where(l => lectureIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l.Name);

        var resultsByAssignment = await _db.LectureAssignmentResults.AsNoTracking()
            .Where(r => assignmentIds.Contains(r.LectureAssignmentId) && r.StudentId == studentId)
            .ToDictionaryAsync(r => r.LectureAssignmentId, r => r);

        var overridesByAssignment = await _db.LectureAssignmentStudentOverrides.AsNoTracking()
            .Where(o => o.StudentId == studentId && assignmentIds.Contains(o.LectureAssignmentId))
            .ToDictionaryAsync(o => o.LectureAssignmentId);

        var nowUtc = DateTime.UtcNow;

        var items = assignments.Select(a =>
        {
            overridesByAssignment.TryGetValue(a.Id, out var ov);
            var reopenActive = ov?.ReopenExpiresAt != null && ov.ReopenExpiresAt.Value > nowUtc;
            var result = resultsByAssignment.TryGetValue(a.Id, out var r) ? r : null;

            return new LectureAssignmentListItem(
                a.Id,
                a.Title,
                a.LectureId,
                lectureNamesById.TryGetValue(a.LectureId, out var name) ? name : "",
                result != null,
                result?.Score,
                result?.TotalMarks,
                reopenActive,
                ov?.ForceReview == true);
        });

        return Ok(items);
    }

    // Teacher-only — same idea as AssignmentsController.ForceReview, from a
    // student's "واجبات الحصص" quick action list.
    [HttpPost("force-review")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ForceReview([FromBody] ForceLectureAssignmentReviewRequest request)
    {
        var assignment = await _db.LectureAssignments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.LectureAssignmentId);
        if (assignment == null) return NotFound(new { message = "Lecture assignment not found." });

        var alreadySubmitted = await _db.LectureAssignmentResults
            .AnyAsync(r => r.LectureAssignmentId == request.LectureAssignmentId && r.StudentId == request.StudentId);
        if (alreadySubmitted)
            return Conflict(new { message = "الطالب سلّم الواجب بالفعل." });

        var overrideRow = await _db.LectureAssignmentStudentOverrides
            .FirstOrDefaultAsync(o => o.LectureAssignmentId == request.LectureAssignmentId && o.StudentId == request.StudentId);
        if (overrideRow == null)
        {
            overrideRow = new LectureAssignmentStudentOverride
            {
                LectureAssignmentId = request.LectureAssignmentId,
                StudentId = request.StudentId,
                TeacherId = assignment.TeacherId
            };
            _db.LectureAssignmentStudentOverrides.Add(overrideRow);
        }

        overrideRow.ForceReview = true;
        overrideRow.ReopenExpiresAt = null;

        await _db.SaveChangesAsync();
        return Ok(new { message = "تم فتح الواجب للطالب كمراجعة." });
    }

    // Teacher-only — same idea as AssignmentsController.Reopen. Wipes any
    // prior submission so the student gets a genuinely clean re-attempt.
    // ReopenExpiresAt here is mostly informational (a LectureAssignment has
    // no deadline to bypass) but is still honored by GetAsStudent/Grade so
    // the "متاح له إعادة فتح شغالة دلوقتي" banner and the takers list stay
    // consistent with Quizzes/Assignments.
    [HttpPost("reopen")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Reopen([FromBody] ReopenLectureAssignmentRequest request)
    {
        if (request.Minutes <= 0)
            return BadRequest(new { message = "عدد الدقايق لازم يكون أكبر من صفر." });

        var assignment = await _db.LectureAssignments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.LectureAssignmentId);
        if (assignment == null) return NotFound(new { message = "Lecture assignment not found." });

        var priorResults = await _db.LectureAssignmentResults
            .Where(r => r.LectureAssignmentId == request.LectureAssignmentId && r.StudentId == request.StudentId)
            .ToListAsync();
        if (priorResults.Count > 0) _db.LectureAssignmentResults.RemoveRange(priorResults);

        var overrideRow = await _db.LectureAssignmentStudentOverrides
            .FirstOrDefaultAsync(o => o.LectureAssignmentId == request.LectureAssignmentId && o.StudentId == request.StudentId);
        if (overrideRow == null)
        {
            overrideRow = new LectureAssignmentStudentOverride
            {
                LectureAssignmentId = request.LectureAssignmentId,
                StudentId = request.StudentId,
                TeacherId = assignment.TeacherId
            };
            _db.LectureAssignmentStudentOverrides.Add(overrideRow);
        }

        overrideRow.ForceReview = false;
        overrideRow.ReopenExpiresAt = DateTime.UtcNow.AddMinutes(request.Minutes);

        await _db.SaveChangesAsync();
        return Ok(new { message = "تم إعادة فتح الواجب للطالب.", reopenExpiresAt = overrideRow.ReopenExpiresAt });
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

        // TEACHER OVERRIDE: the teacher used "افتح كمراجعة" on this
        // student's lecture-assignment list (see ForceReview above). Drop
        // them into review mode right now with an auto-zero result, exactly
        // like AssignmentsController/QuizzesController's ForceReview path —
        // a reopen (see Reopen above) needs no special handling here since
        // it already wiped the prior result, so the student simply lands
        // back in the normal "hasn't submitted yet" branch below.
        if (priorResult == null)
        {
            var overrideRow = await _db.LectureAssignmentStudentOverrides
                .FirstOrDefaultAsync(o => o.LectureAssignmentId == lectureAssignmentId && o.StudentId == studentId);

            if (overrideRow?.ForceReview == true)
            {
                var totalMarks = assignment.Questions.Sum(q => q.Mark);
                var forcedReview = new LectureAssignmentResult
                {
                    LectureAssignmentId = assignment.Id,
                    StudentId = studentId,
                    TotalMarks = totalMarks,
                    Score = 0,
                    TeacherId = assignment.TeacherId
                };
                foreach (var q in assignment.Questions)
                    forcedReview.Answers.Add(new LectureAssignmentAnswer { QuestionId = q.Id, Answer = "", MarkAwarded = 0 });

                _db.LectureAssignmentResults.Add(forcedReview);
                await _db.SaveChangesAsync();

                priorResult = forcedReview;
            }
        }

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

    /// <summary>
    /// Teacher-facing "كارت الواجبات" screen: every subscribed/unlocked
    /// student for ONE container -- a Unit (course), an OnlineLesson, or an
    /// ExternalBook (exactly one of unitId/onlineLessonId/externalBookId is
    /// required) -- tagged with how many of that container's homework items
    /// they've completed. "Homework" here is the union of THREE separate
    /// features that all count toward the same total:
    ///   - LectureAssignment: attached directly to one lecture (completion =
    ///     LectureAssignmentResult) -- available for any container type.
    ///   - Assignment ("اساينمنت"): Unit-wide, free-text/MCQ/True-False/
    ///     Written questions (completion = AssignmentSubmission) -- Unit
    ///     containers only, since Assignment.UnitIds links to Units, never
    ///     an OnlineLesson/ExternalBook.
    ///   - AssignmentCenter ("سنتر اسايمنت"): Unit-wide bubble-sheet
    ///     (completion = AssignmentCenterSubmission) -- Unit containers only,
    ///     same reasoning.
    /// Status is "Full" (completed every one), "Partial" (completed some),
    /// "None" (completed none), or "NoHomework" (nothing to grade yet).
    /// Paged + searched by name/phone/id, same shape as
    /// LectureExams/summary and Attendance/summary (which this mirrors
    /// closely -- see there for the per-Group-vs-flat-total split and the
    /// multi-tenant StudentGroupMembership note).
    /// </summary>
    private record HomeworkStudentRow(int Id, string Name, string? PhoneNumber, int GroupId);

    // GET LectureAssignments/summary?unitId=..           (Center course)
    //     LectureAssignments/summary?onlineLessonId=..    (Online lesson)
    //     LectureAssignments/summary?externalBookId=..    (External book)
    //     ...&groupId=..&p=..&q=.. (groupId only applies to unitId/externalBookId)
    [HttpGet("summary")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.Assistant}")]
    public async Task<IActionResult> GetHomeworkSummary(
        [FromQuery] int? unitId, [FromQuery] int? onlineLessonId, [FromQuery] int? externalBookId,
        [FromQuery] int? groupId, [FromQuery] int p = 1, [FromQuery] string? q = null)
    {
        var sourcesGiven = new[] { unitId.HasValue, onlineLessonId.HasValue, externalBookId.HasValue }.Count(x => x);
        if (sourcesGiven != 1)
            return BadRequest(new { message = "Pass exactly one of unitId, onlineLessonId, or externalBookId." });

        string containerName;
        List<int> reachableStudentIds;
        List<int> containerLectureIds;
        bool isGroupScoped = unitId.HasValue || externalBookId.HasValue;

        if (unitId.HasValue)
        {
            var unit = await _db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.Id == unitId.Value);
            if (unit == null) return NotFound(new { message = "Unit not found." });
            containerName = unit.Name;

            reachableStudentIds = await _db.StudentUnitSubscriptions.AsNoTracking()
                .Where(s => s.UnitId == unitId.Value).Select(s => s.StudentId).ToListAsync();
            containerLectureIds = await _db.Lectures.AsNoTracking()
                .Where(l => l.UnitId == unitId.Value).Select(l => l.Id).ToListAsync();
        }
        else if (onlineLessonId.HasValue)
        {
            var onlineLesson = await _db.OnlineLessons.AsNoTracking().FirstOrDefaultAsync(o => o.Id == onlineLessonId.Value);
            if (onlineLesson == null) return NotFound(new { message = "Online lesson not found." });
            containerName = onlineLesson.Name;

            reachableStudentIds = await _db.StudentOnlineLessonUnlocks.AsNoTracking()
                .Where(u => u.OnlineLessonId == onlineLessonId.Value).Select(u => u.StudentId).Distinct().ToListAsync();
            containerLectureIds = await _db.Lectures.AsNoTracking()
                .Where(l => l.OnlineLessonId == onlineLessonId.Value).Select(l => l.Id).ToListAsync();
        }
        else
        {
            var book = await _db.ExternalBooks.AsNoTracking().FirstOrDefaultAsync(b => b.Id == externalBookId!.Value);
            if (book == null) return NotFound(new { message = "External book not found." });
            containerName = book.Name;

            reachableStudentIds = await _db.StudentExternalBookSubscriptions.AsNoTracking()
                .Where(s => s.ExternalBookId == externalBookId!.Value).Select(s => s.StudentId).ToListAsync();
            containerLectureIds = await _db.Lectures.AsNoTracking()
                .Where(l => l.ExternalBookId == externalBookId!.Value).Select(l => l.Id).ToListAsync();
        }

        // MULTI-TENANT: see the identical note on LectureExamsController.GetExamsSummary.
        List<HomeworkStudentRow> candidates;
        if (isGroupScoped)
        {
            var tenantId = _tenant.CurrentTenantId;
            var membershipQuery = _db.StudentGroupMemberships.AsNoTracking()
                .Where(m => reachableStudentIds.Contains(m.StudentId) && m.Group!.TeacherId == tenantId);
            if (groupId.HasValue) membershipQuery = membershipQuery.Where(m => m.GroupId == groupId.Value);

            candidates = await membershipQuery
                .Select(m => new HomeworkStudentRow(m.StudentId, m.Student!.Name, m.Student!.PhoneNumber, m.GroupId))
                .ToListAsync();
        }
        else
        {
            candidates = await _db.Students.AsNoTracking()
                .Where(s => reachableStudentIds.Contains(s.Id))
                .Select(s => new HomeworkStudentRow(s.Id, s.Name, s.PhoneNumber, 0))
                .ToListAsync();
        }

        var trimmedQ = q?.Trim();
        var isNumericQuery = !string.IsNullOrEmpty(trimmedQ) && trimmedQ.All(char.IsDigit);
        var isIdLikeQuery = isNumericQuery && trimmedQ!.Length <= 5 && !trimmedQ.StartsWith('0');

        IEnumerable<HomeworkStudentRow> filtered = candidates;
        if (!string.IsNullOrWhiteSpace(trimmedQ))
        {
            var normalizedQ = StudentIdentifierResolver.NormalizeArabic(trimmedQ);
            filtered = candidates.Where(s =>
                isIdLikeQuery
                    ? s.Id.ToString().Contains(trimmedQ)
                    : StudentIdentifierResolver.NormalizeArabic(s.Name).Contains(normalizedQ, StringComparison.OrdinalIgnoreCase) ||
                      (s.PhoneNumber != null && s.PhoneNumber.Contains(trimmedQ, StringComparison.OrdinalIgnoreCase)) ||
                      (isNumericQuery && s.Id.ToString().Contains(trimmedQ)));
        }

        var paged = filtered.OrderBy(s => s.Name)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .ToList();

        var pagedIds = paged.Select(s => s.Id).ToList();
        var groupNames = await _db.GetTenantGroupNamesAsync(pagedIds);

        // LectureAssignments attached to a lecture of this container.
        var lectureAssignmentsInContainer = await _db.LectureAssignments.AsNoTracking()
            .Where(a => containerLectureIds.Contains(a.LectureId))
            .Select(a => new { a.Id, a.LectureId })
            .ToListAsync();
        var lectureAssignmentIds = lectureAssignmentsInContainer.Select(a => a.Id).ToList();

        // Unit-wide Assignment / AssignmentCenter items -- only exist for a
        // Unit container (see class doc comment above).
        var assignmentsInUnit = unitId.HasValue
            ? await _db.AssignmentUnitLinks.AsNoTracking()
                .Where(x => x.UnitId == unitId.Value).Select(x => x.AssignmentId).Distinct().ToListAsync()
            : new List<int>();
        var assignmentCentersInUnit = unitId.HasValue
            ? await _db.AssignmentCenterUnitLinks.AsNoTracking()
                .Where(x => x.UnitId == unitId.Value).Select(x => x.AssignmentCenterId).Distinct().ToListAsync()
            : new List<int>();

        Dictionary<int, int> totalByGroup;
        int flatTotal = 0;
        if (isGroupScoped)
        {
            totalByGroup = new Dictionary<int, int>();
            var pagedGroupIds = paged.Select(s => s.GroupId).Distinct().ToList();
            foreach (var gid in pagedGroupIds)
            {
                var lectureIdsForGroup = await _db.LectureGroupLinks.AsNoTracking()
                    .Where(x => x.GroupId == gid && containerLectureIds.Contains(x.LectureId))
                    .Select(x => x.LectureId)
                    .ToListAsync();
                var lectureAssignmentCount = lectureAssignmentsInContainer.Count(a => lectureIdsForGroup.Contains(a.LectureId));

                var assignmentCount = assignmentsInUnit.Count == 0
                    ? 0
                    : await _db.AssignmentGroupLinks.AsNoTracking()
                        .Where(x => x.GroupId == gid && assignmentsInUnit.Contains(x.AssignmentId))
                        .Select(x => x.AssignmentId).Distinct().CountAsync();

                var assignmentCenterCount = assignmentCentersInUnit.Count == 0
                    ? 0
                    : await _db.AssignmentCenterGroupLinks.AsNoTracking()
                        .Where(x => x.GroupId == gid && assignmentCentersInUnit.Contains(x.AssignmentCenterId))
                        .Select(x => x.AssignmentCenterId).Distinct().CountAsync();

                totalByGroup[gid] = lectureAssignmentCount + assignmentCount + assignmentCenterCount;
            }
        }
        else
        {
            // OnlineLesson: no Group-targeting and no Unit-wide items either
            // (see class doc comment) -- just the LectureAssignments.
            totalByGroup = new Dictionary<int, int>();
            flatTotal = lectureAssignmentIds.Count;
        }

        // LectureAssignmentResult completion (distinct-guarded, same
        // reasoning as LectureExamResult in LectureExamsController).
        var completedCounts = await _db.LectureAssignmentResults.AsNoTracking()
            .Where(r => pagedIds.Contains(r.StudentId) && lectureAssignmentIds.Contains(r.LectureAssignmentId))
            .Select(r => new { r.StudentId, r.LectureAssignmentId })
            .Distinct()
            .GroupBy(r => r.StudentId)
            .Select(g => new { StudentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.StudentId, x => x.Count);

        if (assignmentsInUnit.Count > 0)
        {
            var assignmentCompletedCounts = await _db.AssignmentSubmissions.AsNoTracking()
                .Where(r => pagedIds.Contains(r.StudentId) && assignmentsInUnit.Contains(r.AssignmentId))
                .Select(r => new { r.StudentId, r.AssignmentId })
                .Distinct()
                .GroupBy(r => r.StudentId)
                .Select(g => new { StudentId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.StudentId, x => x.Count);
            foreach (var (studentId, count) in assignmentCompletedCounts)
                completedCounts[studentId] = completedCounts.GetValueOrDefault(studentId, 0) + count;
        }

        if (assignmentCentersInUnit.Count > 0)
        {
            var centerCompletedCounts = await _db.AssignmentCenterSubmissions.AsNoTracking()
                .Where(r => pagedIds.Contains(r.StudentId) && assignmentCentersInUnit.Contains(r.AssignmentCenterId))
                .Select(r => new { r.StudentId, r.AssignmentCenterId })
                .Distinct()
                .GroupBy(r => r.StudentId)
                .Select(g => new { StudentId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.StudentId, x => x.Count);
            foreach (var (studentId, count) in centerCompletedCounts)
                completedCounts[studentId] = completedCounts.GetValueOrDefault(studentId, 0) + count;
        }

        var result = paged.Select(s =>
        {
            var total = isGroupScoped ? totalByGroup.GetValueOrDefault(s.GroupId, 0) : flatTotal;
            var completed = completedCounts.GetValueOrDefault(s.Id, 0);
            var status = total == 0 ? "NoHomework" : completed == 0 ? "None" : completed >= total ? "Full" : "Partial";
            return new HomeworkStudentItem(
                s.Id, s.Name, s.PhoneNumber, groupNames.GetValueOrDefault(s.Id) ?? "", completed, total, status);
        }).ToList();

        return Ok(new HomeworkSummaryResponse(containerName, result));
    }
}

// One row per student in the LectureAssignments/summary response.
// "Completed"/"Total" cover ALL THREE homework kinds a student can face:
// LectureAssignment, Assignment ("اساينمنت"), and AssignmentCenter ("سنتر
// اسايمنت") -- see GetHomeworkSummary's doc comment. Status is one of "Full"
// (completed every one), "Partial" (completed some), "None" (completed
// none), or "NoHomework" (nothing to grade yet).
public record HomeworkStudentItem(
    int StudentId,
    string Name,
    string? PhoneNumber,
    string GroupName,
    int CompletedCount,
    int TotalHomework,
    string Status);

public record HomeworkSummaryResponse(string ContainerName, List<HomeworkStudentItem> Students);
