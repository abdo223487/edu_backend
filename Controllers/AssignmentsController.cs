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
/// Route: api/Assignments — homework ("الواجب") feature.
///
/// Differences from Quizzes:
///  - No duration/timer, just a hard Deadline.
///  - Can target MULTIPLE units at once (UnitIds), not just one.
///  - The correct answer for a question is only ever revealed once the
///    Deadline has fully passed — never right after a student submits.
///  - A student can submit an assignment at most once.
///
///  GET  Assignments?schoolYear=..&p=..        (teacher list)
///  GET  Assignments?p=..                      (student: assignments for their group/units)
///  POST Assignments                           (multipart/form-data, teacher create)
///  GET  Assignments/as-teacher/{assignmentId}
///  GET  Assignments/as-student/{assignmentId}
///  POST Assignments/submit                    body: { assignmentId, answers:[{questionId, answer}] }
///  GET  Assignments/takers?assignmentId=..
///  GET  Assignments/student-answers?assignmentId=..&studentId=..
///  POST Assignments/change-mark               body: { assignmentId, questionId, studentId, mark }
///  POST Assignments/delete/{assignmentId}
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AssignmentsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _files;
    private readonly Common.ITenantContext _tenant;

    public AssignmentsController(AppDbContext db, IFileStorageService files, Common.ITenantContext tenant)
    {
        _db = db;
        _files = files;
        _tenant = tenant;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? schoolYear, [FromQuery] int? unitId, [FromQuery] int p = 1)
    {
        var query = _db.Assignments.AsNoTracking().AsQueryable();
        int? studentId = null;

        if (User.IsInRole(Roles.Student))
        {
            studentId = User.GetUserId();

            var groupId = User.GetGroupId(_tenant.CurrentTenantId);
            if (groupId.HasValue) query = query.Where(a => _db.AssignmentGroupLinks.Any(x => x.AssignmentId == a.Id && x.GroupId == groupId.Value));

            // Only assignments that cover at least one unit the student is subscribed to.
            var subscribedIds = User.GetUnitIds();
            query = query.Where(a => _db.AssignmentUnitLinks.Any(x => x.AssignmentId == a.Id && subscribedIds.Contains(x.UnitId)));
        }
        else if (schoolYear.HasValue)
        {
            query = query.Where(a => a.SchoolYear == schoolYear.Value);
        }

        if (unitId.HasValue) query = query.Where(a => _db.AssignmentUnitLinks.Any(x => x.AssignmentId == a.Id && x.UnitId == unitId.Value));

        var assignments = await query
            .OrderByDescending(a => a.Id)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .ToListAsync();

        var assignmentIds = assignments.Select(a => a.Id).ToList();

        var submittedIds = studentId.HasValue
            ? (await _db.AssignmentSubmissions.AsNoTracking()
                .Where(s => s.StudentId == studentId.Value && assignmentIds.Contains(s.AssignmentId))
                .Select(s => s.AssignmentId).ToListAsync()).ToHashSet()
            : new HashSet<int>();

        var items = assignments.Select(a => new AssignmentListItem(
            a.Id,
            a.Title,
            a.UnitIds,
            a.GroupIds,
            a.Deadline,
            a.SchoolYear,
            studentId.HasValue && submittedIds.Contains(a.Id)));

        return Ok(items);
    }

    // Multipart create — Title, GroupIds[i], UnitIds[i], Deadline,
    // Questions[i][type|text|answer|mark|choices[j]], Questions[i].image (file)
    [HttpPost]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Create()
    {
        var form = await Request.ReadFormAsync();

        var title = form["Title"].ToString();
        var deadline = DateTime.Parse(form["Deadline"].ToString(), null, DateTimeStyles.RoundtripKind);

        var groupIds = new List<int>();
        for (var i = 0; form.ContainsKey($"GroupIds[{i}]"); i++)
            groupIds.Add(int.Parse(form[$"GroupIds[{i}]"].ToString()));

        var unitIds = new List<int>();
        for (var i = 0; form.ContainsKey($"UnitIds[{i}]"); i++)
            unitIds.Add(int.Parse(form[$"UnitIds[{i}]"].ToString()));

        // Same fix as Quizzes: teacher tokens don't carry a "schoolYear" claim,
        // so derive it from one of the assignment's own units instead.
        int? schoolYear = null;
        if (unitIds.Count > 0)
            schoolYear = await _db.Units.Where(u => unitIds.Contains(u.Id))
                .Select(u => (int?)u.SchoolYear).FirstOrDefaultAsync();

        var assignment = new Assignment
        {
            Title = title,
            Deadline = deadline,
            GroupIds = groupIds,
            UnitIds = unitIds,
            SchoolYear = schoolYear,
            TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
        };

        for (var i = 0; form.ContainsKey($"Questions[{i}][type]"); i++)
        {
            var question = new AssignmentQuestion
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
                question.ImageUrl = await _files.SaveAsync(imageFile, "assignment-questions");

            assignment.Questions.Add(question);
        }

        _db.Assignments.Add(assignment);
        await _db.SaveChangesAsync();

        return Ok(new { id = assignment.Id, title = assignment.Title });
    }

    [HttpGet("as-teacher/{assignmentId:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetAsTeacher(int assignmentId)
    {
        var assignment = await _db.Assignments.AsNoTracking().Include(a => a.Questions).FirstOrDefaultAsync(a => a.Id == assignmentId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

        var items = assignment.Questions.Select(q => new AssignmentQuestionTeacherDto(
            q.Id, q.Type, q.Text, q.Choices, q.Answer, q.Mark, q.ImageUrl));

        return Ok(items);
    }

    // If the student already submitted, their own answers are echoed back, but
    // the correct answer is only included once the Deadline has fully passed —
    // submitting early never reveals the solution.
    [HttpGet("as-student/{assignmentId:int}")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> GetAsStudent(int assignmentId)
    {
        var assignment = await _db.Assignments.AsNoTracking().Include(a => a.Questions).FirstOrDefaultAsync(a => a.Id == assignmentId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

        if (!assignment.UnitIds.Any(id => User.GetUnitIds().Contains(id)))
            return StatusCode(403, new { message = "Not subscribed to any unit of this assignment." });

        var studentId = User.GetUserId();
        var submission = await _db.AssignmentSubmissions.AsNoTracking().Include(s => s.Answers)
            .FirstOrDefaultAsync(s => s.AssignmentId == assignmentId && s.StudentId == studentId);

        var deadlinePassed = DateTime.UtcNow > assignment.Deadline;
        var revealAnswers = submission != null && deadlinePassed;
        if (submission != null) Response.Headers["x-redirected-to"] = "review";

        var items = assignment.Questions.Select(q =>
        {
            var studentAnswer = submission?.Answers.FirstOrDefault(a => a.QuestionId == q.Id)?.Answer;
            return new
            {
                id = q.Id,
                text = q.Text,
                mark = q.Mark,
                imageUrl = q.ImageUrl,
                questionType = q.Type,
                choices = q.Choices,
                answer = submission != null ? studentAnswer : null,
                markAwarded = revealAnswers
                    ? submission!.Answers.FirstOrDefault(a => a.QuestionId == q.Id)?.MarkAwarded
                    : null,
                // The solution stays hidden until the deadline has fully passed,
                // no matter how early the student submitted.
                correctAnswer = revealAnswers ? q.Answer : null
            };
        });

        return Ok(new
        {
            deadline = assignment.Deadline,
            hasSubmitted = submission != null,
            deadlinePassed,
            score = revealAnswers ? submission!.Score : (int?)null,
            totalMarks = revealAnswers ? submission!.TotalMarks : (int?)null,
            questions = items
        });
    }

    [HttpPost("submit")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Submit([FromBody] SubmitAssignmentRequest request)
    {
        var assignment = await _db.Assignments.Include(a => a.Questions)
            .FirstOrDefaultAsync(a => a.Id == request.AssignmentId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

        if (!assignment.UnitIds.Any(id => User.GetUnitIds().Contains(id)))
            return StatusCode(403, new { message = "Not subscribed to any unit of this assignment." });

        var studentId = User.GetUserId();

        var alreadySubmitted = await _db.AssignmentSubmissions
            .AnyAsync(s => s.AssignmentId == assignment.Id && s.StudentId == studentId);
        if (alreadySubmitted)
            return Conflict(new { message = "تم تسليم هذا الواجب من قبل." });

        // No submissions accepted once the deadline has passed.
        if (DateTime.UtcNow > assignment.Deadline)
            return StatusCode(410, new { message = "انتهى وقت تسليم الواجب." });

        var totalMarks = assignment.Questions.Sum(q => q.Mark);
        var score = 0;

        var submission = new AssignmentSubmission
        {
            TeacherId = assignment.TeacherId,
            AssignmentId = assignment.Id,
            StudentId = studentId,
            TotalMarks = totalMarks
        };

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

            submission.Answers.Add(new AssignmentAnswer { QuestionId = submitted.QuestionId, Answer = submitted.Answer, MarkAwarded = awarded });
        }

        submission.Score = score;
        _db.AssignmentSubmissions.Add(submission);
        await _db.SaveChangesAsync();

        // Score/total are computed and stored immediately for grading purposes,
        // but the client should still not surface the solution to the student —
        // that's enforced separately in GetAsStudent/GetStudentAnswers.
        return Ok(new SubmitAssignmentResult(score, totalMarks));
    }

    [HttpGet("takers")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetTakers([FromQuery] int assignmentId)
    {
        var assignment = await _db.Assignments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assignmentId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

        var groupIds = assignment.GroupIds;
        // MULTI-TENANT: match via GroupMemberships (this tenant's groups), not the
        // legacy single GroupId, so students linked here from another teacher too
        // still show up correctly against THIS teacher's assignment groups.
        var students = await _db.Students.AsNoTracking().Include(s => s.Group)
            .Where(s => s.GroupMemberships.Any(m => groupIds.Contains(m.GroupId))).ToListAsync();
        var submissions = await _db.AssignmentSubmissions.AsNoTracking().Where(s => s.AssignmentId == assignmentId).ToListAsync();

        // Full roster (unlike Quiz's takers list), so the teacher can see who
        // HASN'T done the assignment yet, not just who has.
        var items = students.Select(s =>
        {
            var submission = submissions.FirstOrDefault(sub => sub.StudentId == s.Id);
            return new AssignmentTakerDto(
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

    // Teacher-only detail view for a specific student's submission — teachers
    // always see the correct answer, regardless of the Deadline.
    [HttpGet("student-answers")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetStudentAnswers([FromQuery] int assignmentId, [FromQuery] int studentId)
    {
        var assignment = await _db.Assignments.AsNoTracking().Include(a => a.Questions).FirstOrDefaultAsync(a => a.Id == assignmentId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

        var submission = await _db.AssignmentSubmissions.AsNoTracking().Include(s => s.Answers)
            .FirstOrDefaultAsync(s => s.AssignmentId == assignmentId && s.StudentId == studentId);

        var items = assignment.Questions.Select(q =>
        {
            var answer = submission?.Answers.FirstOrDefault(a => a.QuestionId == q.Id);
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
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangeMark([FromBody] ChangeAssignmentMarkRequest request)
    {
        var submission = await _db.AssignmentSubmissions.Include(s => s.Answers)
            .FirstOrDefaultAsync(s => s.AssignmentId == request.AssignmentId && s.StudentId == request.StudentId);
        if (submission == null) return NotFound(new { message = "Submission not found." });

        var answer = submission.Answers.FirstOrDefault(a => a.QuestionId == request.QuestionId);
        if (answer == null) return NotFound(new { message = "Answer not found." });

        var oldMark = answer.MarkAwarded ?? 0;
        answer.MarkAwarded = request.Mark;
        submission.Score = submission.Score - oldMark + request.Mark;

        await _db.SaveChangesAsync();
        return Ok(new { message = "Mark updated.", newScore = submission.Score });
    }

    [HttpPost("delete/{assignmentId:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Delete(int assignmentId)
    {
        var assignment = await _db.Assignments.FirstOrDefaultAsync(a => a.Id == assignmentId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

        _db.Assignments.Remove(assignment);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Assignment deleted." });
    }
}
