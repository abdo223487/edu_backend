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
/// Route: api/AssignmentCenters — "سنتر اسايمنت" bubble-sheet homework feature.
///
/// Same shape as Assignments (title/year/groups/units/deadline, correct answer
/// hidden until the deadline passes, one submission per student), but every
/// question is a fixed 4-choice bubble (أ/ب/ج/د) picked from a dropdown when
/// the teacher creates it — no free text choices, no attached images/files,
/// and JSON body instead of multipart since there's nothing to upload.
///
///  GET  AssignmentCenters?schoolYear=..&p=..        (teacher list)
///  GET  AssignmentCenters?p=..                      (student: for their group/units)
///  POST AssignmentCenters                           (JSON, teacher create)
///  GET  AssignmentCenters/as-teacher/{id}
///  GET  AssignmentCenters/as-student/{id}
///  POST AssignmentCenters/submit                    body: { assignmentCenterId, answers:[{questionId, answer}] }
///  GET  AssignmentCenters/takers?assignmentCenterId=..
///  GET  AssignmentCenters/student-answers?assignmentCenterId=..&studentId=..
///  POST AssignmentCenters/change-mark                body: { assignmentCenterId, questionId, studentId, mark }
///  POST AssignmentCenters/delete/{id}
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AssignmentCentersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly Common.ITenantContext _tenant;
    private readonly IWhatsAppService _whatsApp;
    private readonly ILogger<AssignmentCentersController> _logger;

    public AssignmentCentersController(AppDbContext db, Common.ITenantContext tenant, IWhatsAppService whatsApp, ILogger<AssignmentCentersController> logger)
    {
        _db = db;
        _tenant = tenant;
        _whatsApp = whatsApp;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? schoolYear, [FromQuery] int? unitId, [FromQuery] int p = 1)
    {
        var query = _db.AssignmentCenters.AsNoTracking().AsQueryable();
        int? studentId = null;

        if (User.IsInRole(Roles.Student))
        {
            studentId = User.GetUserId();

            var groupId = User.GetGroupId(_tenant.CurrentTenantId);
            if (groupId.HasValue) query = query.Where(a => _db.AssignmentCenterGroupLinks.Any(x => x.AssignmentCenterId == a.Id && x.GroupId == groupId.Value));

            var subscribedIds = User.GetUnitIds();
            query = query.Where(a => _db.AssignmentCenterUnitLinks.Any(x => x.AssignmentCenterId == a.Id && subscribedIds.Contains(x.UnitId)));
        }
        else if (schoolYear.HasValue)
        {
            query = query.Where(a => a.SchoolYear == schoolYear.Value);
        }

        if (unitId.HasValue) query = query.Where(a => _db.AssignmentCenterUnitLinks.Any(x => x.AssignmentCenterId == a.Id && x.UnitId == unitId.Value));

        var assignments = await query
            .OrderByDescending(a => a.Id)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .ToListAsync();

        var ids = assignments.Select(a => a.Id).ToList();

        var submittedIds = studentId.HasValue
            ? (await _db.AssignmentCenterSubmissions.AsNoTracking()
                .Where(s => s.StudentId == studentId.Value && ids.Contains(s.AssignmentCenterId))
                .Select(s => s.AssignmentCenterId).ToListAsync()).ToHashSet()
            : new HashSet<int>();

        var items = assignments.Select(a => new AssignmentCenterListItem(
            a.Id,
            a.Title,
            a.UnitIds,
            a.GroupIds,
            a.Deadline,
            a.SchoolYear,
            studentId.HasValue && submittedIds.Contains(a.Id),
            a.AllowLateReview));

        return Ok(items);
    }

    // JSON create — no files/images in this feature, so plain [FromBody] is enough.
    // SUPERADMIN: same "on behalf of a teacher via X-TenantId" support as Assignments.
    [HttpPost]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> Create([FromBody] CreateAssignmentCenterRequest request)
    {
        if (_tenant.CurrentTenantId == null)
            return BadRequest(new { message = "No teacher selected. Send X-TenantId with the target teacher's id." });

        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequest(new { message = "العنوان مطلوب." });
        if (request.GroupIds == null || request.GroupIds.Count == 0)
            return BadRequest(new { message = "اختر مجموعة واحدة على الأقل." });
        if (request.UnitIds == null || request.UnitIds.Count == 0)
            return BadRequest(new { message = "اختر وحدة واحدة على الأقل." });
        if (request.Questions == null || request.Questions.Count == 0)
            return BadRequest(new { message = "أضف سؤال واحد على الأقل." });
        foreach (var q in request.Questions)
        {
            if (string.IsNullOrWhiteSpace(q.Text))
                return BadRequest(new { message = "كل سؤال لازم يكون له نص." });
            if (!AssignmentCenterChoices.IsValid(q.Answer))
                return BadRequest(new { message = "اختر الإجابة الصحيحة (أ/ب/ج/د) لكل سؤال." });
            if (q.Mark <= 0)
                return BadRequest(new { message = "درجة السؤال لازم تكون أكبر من صفر." });
        }

        int? schoolYear = await _db.Units.Where(u => request.UnitIds.Contains(u.Id))
            .Select(u => (int?)u.SchoolYear).FirstOrDefaultAsync();

        var assignment = new AssignmentCenter
        {
            Title = request.Title,
            Deadline = request.Deadline,
            GroupIds = request.GroupIds,
            UnitIds = request.UnitIds,
            SchoolYear = schoolYear,
            // Missing (null) -> true, matching the old hard-coded "always
            // let them peek once the deadline's passed" behavior.
            AllowLateReview = request.AllowLateReview ?? true,
            TeacherId = _tenant.CurrentTenantId!.Value
        };

        foreach (var q in request.Questions)
        {
            assignment.Questions.Add(new AssignmentCenterQuestion
            {
                Text = q.Text,
                Answer = q.Answer,
                Mark = q.Mark
            });
        }

        _db.AssignmentCenters.Add(assignment);
        await _db.SaveChangesAsync();

        return Ok(new { id = assignment.Id, title = assignment.Title });
    }

    [HttpGet("as-teacher/{assignmentCenterId:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> GetAsTeacher(int assignmentCenterId)
    {
        var assignment = await _db.AssignmentCenters.AsNoTracking().Include(a => a.Questions).FirstOrDefaultAsync(a => a.Id == assignmentCenterId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

        var items = assignment.Questions.Select(q => new AssignmentCenterQuestionTeacherDto(q.Id, q.Text, q.Answer, q.Mark));
        return Ok(items);
    }

    [HttpGet("as-student/{assignmentCenterId:int}")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> GetAsStudent(int assignmentCenterId)
    {
        var assignment = await _db.AssignmentCenters.AsNoTracking().Include(a => a.Questions).FirstOrDefaultAsync(a => a.Id == assignmentCenterId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

        if (!assignment.UnitIds.Any(id => User.GetUnitIds().Contains(id)))
            return StatusCode(403, new { message = "Not subscribed to any unit of this assignment." });

        var studentId = User.GetUserId();
        var submission = await _db.AssignmentCenterSubmissions.AsNoTracking().Include(s => s.Answers)
            .FirstOrDefaultAsync(s => s.AssignmentCenterId == assignmentCenterId && s.StudentId == studentId);

        var deadlinePassed = DateTime.UtcNow > assignment.Deadline;

        // Same policy as Assignments/as-student: a student who never
        // submitted is blocked entirely (410) once the deadline passes, if
        // the teacher turned off late-review access for this assignment.
        if (submission == null && deadlinePassed && !assignment.AllowLateReview)
            return StatusCode(410, new { message = "انتهى وقت تسليم الواجب." });

        var revealAnswers = deadlinePassed && (submission != null || assignment.AllowLateReview);
        if (submission != null || revealAnswers) Response.Headers["x-redirected-to"] = "review";

        var items = assignment.Questions.Select(q =>
        {
            var studentAnswer = submission?.Answers.FirstOrDefault(a => a.QuestionId == q.Id)?.Answer;
            return new
            {
                id = q.Id,
                text = q.Text,
                mark = q.Mark,
                choices = AssignmentCenterChoices.Letters,
                answer = submission != null ? studentAnswer : null,
                markAwarded = submission != null && revealAnswers
                    ? submission.Answers.FirstOrDefault(a => a.QuestionId == q.Id)?.MarkAwarded
                    : null,
                correctAnswer = revealAnswers ? q.Answer : null
            };
        });

        return Ok(new
        {
            deadline = assignment.Deadline,
            hasSubmitted = submission != null,
            deadlinePassed,
            score = submission != null && revealAnswers ? submission.Score : (int?)null,
            totalMarks = submission != null && revealAnswers ? submission.TotalMarks : (int?)null,
            questions = items
        });
    }

    [HttpPost("submit")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Submit([FromBody] SubmitAssignmentCenterRequest request)
    {
        var assignment = await _db.AssignmentCenters.Include(a => a.Questions)
            .FirstOrDefaultAsync(a => a.Id == request.AssignmentCenterId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

        if (!assignment.UnitIds.Any(id => User.GetUnitIds().Contains(id)))
            return StatusCode(403, new { message = "Not subscribed to any unit of this assignment." });

        var studentId = User.GetUserId();

        var alreadySubmitted = await _db.AssignmentCenterSubmissions
            .AnyAsync(s => s.AssignmentCenterId == assignment.Id && s.StudentId == studentId);
        if (alreadySubmitted)
            return Conflict(new { message = "تم تسليم هذا الواجب من قبل." });

        if (DateTime.UtcNow > assignment.Deadline)
            return StatusCode(410, new { message = "انتهى وقت تسليم الواجب." });

        var totalMarks = assignment.Questions.Sum(q => q.Mark);
        var score = 0;

        var submission = new AssignmentCenterSubmission
        {
            TeacherId = assignment.TeacherId,
            AssignmentCenterId = assignment.Id,
            StudentId = studentId,
            TotalMarks = totalMarks
        };

        foreach (var submitted in request.Answers ?? new())
        {
            var question = assignment.Questions.FirstOrDefault(q => q.Id == submitted.QuestionId);
            int? awarded = null;
            if (question != null && string.Equals(question.Answer, submitted.Answer, StringComparison.Ordinal))
            {
                awarded = question.Mark;
                score += question.Mark;
            }
            else if (question != null)
            {
                awarded = 0;
            }

            submission.Answers.Add(new AssignmentCenterAnswer { QuestionId = submitted.QuestionId, Answer = submitted.Answer, MarkAwarded = awarded });
        }

        submission.Score = score;
        _db.AssignmentCenterSubmissions.Add(submission);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // RACE-CONDITION FIX: the AnyAsync check above already covers
            // the common case; this catches the rare genuine race (two
            // near-simultaneous submissions for the same student+assignment)
            // that the unique (AssignmentCenterId, StudentId) index (see
            // AppDbContext) rejects at the database level.
            return Conflict(new { message = "تم تسليم هذا الواجب من قبل." });
        }

        try
        {
            var student = await _db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == studentId);
            var teacher = await _db.Teachers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == assignment.TeacherId);
            if (student != null && teacher != null && !string.IsNullOrWhiteSpace(student.ParentPhoneNumber))
            {
                var data = new ExamResultWhatsAppNotification(student.Name, teacher.Name, assignment.Title, score, totalMarks);
                await _whatsApp.SendAssignmentResultNotificationAsync(student.ParentPhoneNumber!, data);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send WhatsApp assignment-center-result notification for AssignmentCenterSubmission {SubmissionId}.", submission.Id);
        }

        return Ok(new SubmitAssignmentCenterResult(score, totalMarks));
    }

    [HttpGet("takers")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> GetTakers([FromQuery] int assignmentCenterId)
    {
        var assignment = await _db.AssignmentCenters.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assignmentCenterId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

        var groupIds = assignment.GroupIds;
        var students = await _db.Students.AsNoTracking().Include(s => s.Group)
            .Where(s => s.GroupMemberships.Any(m => groupIds.Contains(m.GroupId))).ToListAsync();
        var submissions = await _db.AssignmentCenterSubmissions.AsNoTracking().Where(s => s.AssignmentCenterId == assignmentCenterId).ToListAsync();

        var items = students.Select(s =>
        {
            var submission = submissions.FirstOrDefault(sub => sub.StudentId == s.Id);
            return new AssignmentCenterTakerDto(
                s.Id,
                s.Name,
                s.Group?.Name ?? "",
                submission != null,
                submission?.Score,
                submission?.TotalMarks,
                submission?.SubmittedAt);
        });

        return Ok(items);
    }

    [HttpGet("student-answers")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> GetStudentAnswers([FromQuery] int assignmentCenterId, [FromQuery] int studentId)
    {
        var assignment = await _db.AssignmentCenters.AsNoTracking().Include(a => a.Questions).FirstOrDefaultAsync(a => a.Id == assignmentCenterId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

        var submission = await _db.AssignmentCenterSubmissions.AsNoTracking().Include(s => s.Answers)
            .FirstOrDefaultAsync(s => s.AssignmentCenterId == assignmentCenterId && s.StudentId == studentId);

        var items = assignment.Questions.Select(q =>
        {
            var answer = submission?.Answers.FirstOrDefault(a => a.QuestionId == q.Id);
            return new
            {
                id = q.Id,
                text = q.Text,
                choices = AssignmentCenterChoices.Letters,
                correctAnswer = q.Answer,
                answer = answer?.Answer,
                mark = q.Mark,
                studentMark = answer?.MarkAwarded ?? 0
            };
        });

        return Ok(new
        {
            hasSubmitted = submission != null,
            score = submission?.Score,
            totalMarks = submission?.TotalMarks,
            submittedAt = submission?.SubmittedAt,
            questions = items
        });
    }

    [HttpPost("change-mark")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> ChangeMark([FromBody] ChangeAssignmentCenterMarkRequest request)
    {
        var submission = await _db.AssignmentCenterSubmissions.Include(s => s.Answers)
            .FirstOrDefaultAsync(s => s.AssignmentCenterId == request.AssignmentCenterId && s.StudentId == request.StudentId);
        if (submission == null) return NotFound(new { message = "Submission not found." });

        var answer = submission.Answers.FirstOrDefault(a => a.QuestionId == request.QuestionId);
        if (answer == null) return NotFound(new { message = "Answer not found." });

        var oldMark = answer.MarkAwarded ?? 0;
        answer.MarkAwarded = request.Mark;
        submission.Score = submission.Score - oldMark + request.Mark;

        await _db.SaveChangesAsync();
        return Ok(new { message = "Mark updated.", newScore = submission.Score });
    }

    // Same fix-a-question-in-place capability as Assignments/edit-question,
    // but the answer must stay one of the 4 fixed bubble letters.
    [HttpPost("edit-question")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> EditQuestion([FromBody] EditAssignmentCenterQuestionRequest request)
    {
        var assignment = await _db.AssignmentCenters.Include(a => a.Questions).FirstOrDefaultAsync(a => a.Id == request.AssignmentCenterId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

        var question = assignment.Questions.FirstOrDefault(q => q.Id == request.QuestionId);
        if (question == null) return NotFound(new { message = "Question not found." });

        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { message = "نص السؤال مطلوب." });
        if (request.Mark <= 0)
            return BadRequest(new { message = "درجة السؤال لازم تكون أكبر من صفر." });
        if (!AssignmentCenterChoices.IsValid(request.Answer))
            return BadRequest(new { message = "اختر الإجابة الصحيحة (أ/ب/ج/د)." });

        question.Text = request.Text;
        question.Mark = request.Mark;
        question.Answer = request.Answer;

        await _db.SaveChangesAsync();

        return Ok(new AssignmentCenterQuestionTeacherDto(question.Id, question.Text, question.Answer, question.Mark));
    }

    [HttpPost("delete/{assignmentCenterId:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> Delete(int assignmentCenterId)
    {
        var assignment = await _db.AssignmentCenters.FirstOrDefaultAsync(a => a.Id == assignmentCenterId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

        _db.AssignmentCenters.Remove(assignment);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Assignment deleted." });
    }
}
