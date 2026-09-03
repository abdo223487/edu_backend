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
    private readonly IWhatsAppService _whatsApp;
    private readonly ILogger<AssignmentsController> _logger;

    public AssignmentsController(AppDbContext db, IFileStorageService files, Common.ITenantContext tenant, IWhatsAppService whatsApp, ILogger<AssignmentsController> logger)
    {
        _db = db;
        _files = files;
        _tenant = tenant;
        _whatsApp = whatsApp;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? schoolYear, [FromQuery] int? unitId, [FromQuery] int? studentId, [FromQuery] int p = 1)
    {
        var query = _db.Assignments.AsNoTracking().AsQueryable();
        int? effectiveStudentId = null;

        if (User.IsInRole(Roles.Student))
        {
            effectiveStudentId = User.GetUserId();

            var groupId = User.GetGroupId(_tenant.CurrentTenantId);
            if (groupId.HasValue) query = query.Where(a => _db.AssignmentGroupLinks.Any(x => x.AssignmentId == a.Id && x.GroupId == groupId.Value));

            // Only assignments that cover at least one unit the student is subscribed to.
            // Merged with live subscriptions so a fresh teacher subscribe is visible immediately.
            var subscribedIds = await Common.StudentAccessHelpers.GetEffectiveUnitIdsAsync(_db, User, effectiveStudentId.Value);
            query = query.Where(a => _db.AssignmentUnitLinks.Any(x => x.AssignmentId == a.Id && subscribedIds.Contains(x.UnitId)));
        }
        else if (studentId.HasValue)
        {
            // TEACHER VIEWING A SPECIFIC STUDENT'S ASSIGNMENT LIST — same idea
            // as QuizzesController.GetAll's studentId-for-teacher branch, see
            // there for the full explanation.
            effectiveStudentId = studentId.Value;

            var targetGroupId = await _db.StudentGroupMemberships.AsNoTracking()
                .Where(m => m.StudentId == studentId.Value)
                .Select(m => (int?)m.GroupId)
                .FirstOrDefaultAsync();
            if (targetGroupId == null)
            {
                var student = await _db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == studentId.Value);
                if (student != null && await _db.Groups.AsNoTracking().AnyAsync(g => g.Id == student.GroupId && g.TeacherId == _tenant.CurrentTenantId))
                    targetGroupId = student.GroupId;
            }
            if (targetGroupId.HasValue) query = query.Where(a => _db.AssignmentGroupLinks.Any(x => x.AssignmentId == a.Id && x.GroupId == targetGroupId.Value));

            var targetUnitIds = await _db.StudentUnitSubscriptions.AsNoTracking()
                .Where(su => su.StudentId == studentId.Value)
                .Select(su => su.UnitId)
                .ToListAsync();
            query = query.Where(a => _db.AssignmentUnitLinks.Any(x => x.AssignmentId == a.Id && targetUnitIds.Contains(x.UnitId)));
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

        var submissionsById = effectiveStudentId.HasValue
            ? await _db.AssignmentSubmissions.AsNoTracking()
                .Where(s => s.StudentId == effectiveStudentId.Value && assignmentIds.Contains(s.AssignmentId))
                .ToDictionaryAsync(s => s.AssignmentId, s => (Score: s.Score, TotalMarks: s.TotalMarks))
            : new Dictionary<int, (int Score, int TotalMarks)>();

        // Per-student teacher overrides — same idea/shape as QuizStudentOverride.
        var overridesById = effectiveStudentId.HasValue
            ? await _db.AssignmentStudentOverrides.AsNoTracking()
                .Where(o => o.StudentId == effectiveStudentId.Value && assignmentIds.Contains(o.AssignmentId))
                .ToDictionaryAsync(o => o.AssignmentId)
            : new Dictionary<int, AssignmentStudentOverride>();

        var nowUtc = DateTime.UtcNow;

        var items = assignments.Select(a =>
        {
            overridesById.TryGetValue(a.Id, out var ov);
            var reopenActive = ov?.ReopenExpiresAt != null && ov.ReopenExpiresAt.Value > nowUtc;
            var effectiveDeadline = reopenActive ? ov!.ReopenExpiresAt!.Value : a.Deadline;
            var result = submissionsById.TryGetValue(a.Id, out var r) ? r : ((int Score, int TotalMarks)?)null;

            return new AssignmentListItem(
                a.Id,
                a.Title,
                a.UnitIds,
                a.GroupIds,
                effectiveDeadline,
                a.SchoolYear,
                effectiveStudentId.HasValue && submissionsById.ContainsKey(a.Id),
                (ov?.ForceReview == true) || a.AllowLateReview,
                result?.Score,
                result?.TotalMarks);
        });

        return Ok(items);
    }

    // Multipart create — Title, GroupIds[i], UnitIds[i], Deadline,
    // Questions[i][type|text|answer|mark|choices[j]], Questions[i].image (file)
    //
    // SUPERADMIN: also allowed here so a SuperAdmin can create an assignment
    // "on behalf of" a specific teacher (e.g. from a Google Form the teacher
    // sent instead of filling the questions in-app themselves). Which teacher
    // is picked the same way it is for Material: the SuperAdmin sends
    // X-TenantId = that teacher's id, resolved via ITenantContext below —
    // NOT via User.GetStaffTenantId(), which is only ever populated for
    // Teacher/Assistant/AssistantAdmin tokens and is always null for
    // SuperAdmin (see ClaimsExtensions.GetStaffTenantId).
    [HttpPost]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Create()
    {
        // Resolve the target teacher up front so we can 400 cleanly instead of
        // crashing on ".Value" — a SuperAdmin who forgets to send X-TenantId
        // (or sends a bad one) has CurrentTenantId == null, same as any other
        // unresolved-tenant request.
        if (_tenant.CurrentTenantId == null)
            return BadRequest(new { message = "No teacher selected. Send X-TenantId with the target teacher's id." });

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

        // Same idea as Quizzes: whether a student who never submits can still
        // open this assignment in review mode once the Deadline passes.
        // Missing/unparseable -> true, matching the previous hard-coded
        // "always let them peek once the deadline's passed" behavior.
        var allowLateReview = !bool.TryParse(form["AllowLateReview"].ToString(), out var allowLateReviewParsed)
            || allowLateReviewParsed;

        var assignment = new Assignment
        {
            Title = title,
            Deadline = deadline,
            GroupIds = groupIds,
            UnitIds = unitIds,
            SchoolYear = schoolYear,
            AllowLateReview = allowLateReview,
            // TENANT LAYER: _tenant.CurrentTenantId is the correct source for
            // BOTH cases now — for Teacher/AssistantAdmin it's the same value
            // GetStaffTenantId() would give (ITenantContext falls back to it
            // internally), and for SuperAdmin it's whichever teacher was
            // picked via X-TenantId (GetStaffTenantId() would be null here).
            TeacherId = _tenant.CurrentTenantId!.Value
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
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
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

        var studentId = User.GetUserId();
        var effectiveUnitIds = await Common.StudentAccessHelpers.GetEffectiveUnitIdsAsync(_db, User, studentId);
        if (!assignment.UnitIds.Any(id => effectiveUnitIds.Contains(id)))
            return StatusCode(403, new { message = "Not subscribed to any unit of this assignment." });

        var submission = await _db.AssignmentSubmissions.AsNoTracking().Include(s => s.Answers)
            .FirstOrDefaultAsync(s => s.AssignmentId == assignmentId && s.StudentId == studentId);

        var overrideRow = await _db.AssignmentStudentOverrides.AsNoTracking()
            .FirstOrDefaultAsync(o => o.AssignmentId == assignmentId && o.StudentId == studentId);
        var hasForceReview = overrideRow?.ForceReview == true;
        var hasActiveReopen = overrideRow?.ReopenExpiresAt != null && overrideRow.ReopenExpiresAt.Value > DateTime.UtcNow;

        // TEACHER OVERRIDE: an active reopen window makes the assignment
        // behave, for this student only, as if it hadn't reached its deadline
        // yet — even if the real Deadline has already passed.
        var deadlinePassed = hasActiveReopen ? false : DateTime.UtcNow > assignment.Deadline;

        // A student who never submitted and shows up after the deadline is
        // blocked entirely (410) if the teacher turned off late-review access
        // for this assignment — same policy as Quiz.AllowLateReview. If it's
        // on (default), they fall through to review mode below exactly like
        // a student who did submit, just with every answer blank.
        //
        // TEACHER OVERRIDE: hasForceReview always lets them through here,
        // regardless of the assignment's own AllowLateReview policy — the
        // teacher explicitly asked for this via the "افتح كمراجعة" action.
        if (submission == null && deadlinePassed && !assignment.AllowLateReview && !hasForceReview)
            return StatusCode(410, new { message = "انتهى وقت تسليم الواجب." });

        // The solution is revealed once the deadline has passed — either
        // because the student actually submitted, or (when AllowLateReview
        // is on) because they're being let into review mode without ever
        // having submitted at all. A forced review always reveals it,
        // regardless of the real deadline.
        var revealAnswers = hasForceReview || (deadlinePassed && (submission != null || assignment.AllowLateReview));
        if (submission != null || revealAnswers) Response.Headers["x-redirected-to"] = "review";

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
                markAwarded = submission != null && revealAnswers
                    ? submission.Answers.FirstOrDefault(a => a.QuestionId == q.Id)?.MarkAwarded
                    : null,
                // The solution stays hidden until the deadline has fully passed,
                // no matter how early the student submitted.
                correctAnswer = revealAnswers ? q.Answer : null
            };
        });

        return Ok(new
        {
            deadline = hasActiveReopen ? overrideRow!.ReopenExpiresAt!.Value : assignment.Deadline,
            hasSubmitted = submission != null,
            deadlinePassed,
            score = submission != null && revealAnswers ? submission.Score : (int?)null,
            totalMarks = submission != null && revealAnswers ? submission.TotalMarks : (int?)null,
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

        var studentId = User.GetUserId();
        var effectiveUnitIds = await Common.StudentAccessHelpers.GetEffectiveUnitIdsAsync(_db, User, studentId);
        if (!assignment.UnitIds.Any(id => effectiveUnitIds.Contains(id)))
            return StatusCode(403, new { message = "Not subscribed to any unit of this assignment." });

        var alreadySubmitted = await _db.AssignmentSubmissions
            .AnyAsync(s => s.AssignmentId == assignment.Id && s.StudentId == studentId);
        if (alreadySubmitted)
            return Conflict(new { message = "تم تسليم هذا الواجب من قبل." });

        // TEACHER OVERRIDE: an active reopen window (see Reopen below) lets
        // the student submit past the assignment's own Deadline.
        var hasActiveReopen = await _db.AssignmentStudentOverrides.AsNoTracking()
            .AnyAsync(o => o.AssignmentId == assignment.Id && o.StudentId == studentId && o.ReopenExpiresAt != null && o.ReopenExpiresAt.Value > DateTime.UtcNow);

        // No submissions accepted once the deadline has passed.
        if (!hasActiveReopen && DateTime.UtcNow > assignment.Deadline)
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

        // Notify the parent on WhatsApp that the student finished this
        // assignment. Never let this block/fail the submission itself.
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
            _logger.LogWarning(ex, "Failed to send WhatsApp assignment-result notification for AssignmentSubmission {SubmissionId}.", submission.Id);
        }

        // Score/total are computed and stored immediately for grading purposes,
        // but the client should still not surface the solution to the student —
        // that's enforced separately in GetAsStudent/GetStudentAnswers.
        return Ok(new SubmitAssignmentResult(score, totalMarks));
    }

    // GET Assignments/takers?assignmentId=..&p=..&q=..&submitted=..
    // Same paging convention as Students?p=..&q=.. -- submitted=true/false
    // narrows to only submitters or only non-submitters; omitted returns
    // the full roster, same as before.
    [HttpGet("takers")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> GetTakers(
        [FromQuery] int assignmentId, [FromQuery] int p = 1, [FromQuery] string? q = null, [FromQuery] bool? submitted = null)
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

        var trimmedQ = q?.Trim();

        IEnumerable<Student> filtered = students;
        if (!string.IsNullOrWhiteSpace(trimmedQ))
            filtered = filtered.Where(s =>
                s.Name.Contains(trimmedQ, StringComparison.OrdinalIgnoreCase) ||
                (s.Group?.Name != null && s.Group.Name.Contains(trimmedQ, StringComparison.OrdinalIgnoreCase)));

        // Full roster (unlike Quiz's takers list), so the teacher can see who
        // HASN'T done the assignment yet, not just who has.
        var items = filtered
            .Select(s => new { student = s, submission = submissions.FirstOrDefault(sub => sub.StudentId == s.Id) })
            .Where(x => submitted == null || (submitted.Value ? x.submission != null : x.submission == null))
            .OrderBy(x => x.student.Name)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .Select(x => new AssignmentTakerDto(
                x.student.Id,
                x.student.Name,
                x.student.Group?.Name ?? "",
                x.submission != null,
                x.submission?.Score,
                x.submission?.TotalMarks,
                x.submission?.SubmittedAt));

        return Ok(items);
    }

    // Teacher-only detail view for a specific student's submission — teachers
    // always see the correct answer, regardless of the Deadline.
    [HttpGet("student-answers")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
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
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
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

    // Same fix-a-question-in-place capability as Quizzes/edit-question.
    [HttpPost("edit-question")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> EditQuestion([FromBody] EditAssignmentQuestionRequest request)
    {
        var assignment = await _db.Assignments.Include(a => a.Questions).FirstOrDefaultAsync(a => a.Id == request.AssignmentId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

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

        return Ok(new AssignmentQuestionTeacherDto(
            question.Id, question.Type, question.Text, question.Choices, question.Answer, question.Mark, question.ImageUrl));
    }

    // Lets a teacher fix the assignment's own basic info (name/deadline/
    // late-review policy) after creation — separate from edit-question
    // above, which only touches individual questions. Deliberately does NOT
    // let group/unit be changed here, since that affects who the assignment
    // is even visible to.
    [HttpPost("edit")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> EditAssignment([FromBody] EditAssignmentRequest request)
    {
        var assignment = await _db.Assignments.FirstOrDefaultAsync(a => a.Id == request.AssignmentId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequest(new { message = "اسم الواجب مطلوب." });

        assignment.Title = request.Title;
        assignment.Deadline = request.Deadline;
        assignment.AllowLateReview = request.AllowLateReview;

        await _db.SaveChangesAsync();

        return Ok(new
        {
            id = assignment.Id,
            title = assignment.Title,
            deadline = assignment.Deadline,
            allowLateReview = assignment.AllowLateReview
        });
    }

    // Teacher-only — same idea as QuizzesController.ForceReview, from a
    // student's "الواجبات" quick action list. See QuizStudentOverride /
    // AssignmentStudentOverride for the full explanation.
    [HttpPost("force-review")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ForceReview([FromBody] ForceAssignmentReviewRequest request)
    {
        var assignment = await _db.Assignments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.AssignmentId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

        var alreadySubmitted = await _db.AssignmentSubmissions
            .AnyAsync(s => s.AssignmentId == request.AssignmentId && s.StudentId == request.StudentId);
        if (alreadySubmitted)
            return Conflict(new { message = "الطالب سلّم الواجب بالفعل." });

        var overrideRow = await _db.AssignmentStudentOverrides
            .FirstOrDefaultAsync(o => o.AssignmentId == request.AssignmentId && o.StudentId == request.StudentId);
        if (overrideRow == null)
        {
            overrideRow = new AssignmentStudentOverride
            {
                AssignmentId = request.AssignmentId,
                StudentId = request.StudentId,
                TeacherId = assignment.TeacherId
            };
            _db.AssignmentStudentOverrides.Add(overrideRow);
        }

        overrideRow.ForceReview = true;
        overrideRow.ReopenExpiresAt = null;

        await _db.SaveChangesAsync();
        return Ok(new { message = "تم فتح الواجب للطالب كمراجعة." });
    }

    // Teacher-only — same idea as QuizzesController.Reopen.
    [HttpPost("reopen")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Reopen([FromBody] ReopenAssignmentRequest request)
    {
        if (request.Minutes <= 0)
            return BadRequest(new { message = "عدد الدقايق لازم يكون أكبر من صفر." });

        var assignment = await _db.Assignments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.AssignmentId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

        var priorSubmissions = await _db.AssignmentSubmissions
            .Where(s => s.AssignmentId == request.AssignmentId && s.StudentId == request.StudentId)
            .ToListAsync();
        if (priorSubmissions.Count > 0) _db.AssignmentSubmissions.RemoveRange(priorSubmissions);

        var overrideRow = await _db.AssignmentStudentOverrides
            .FirstOrDefaultAsync(o => o.AssignmentId == request.AssignmentId && o.StudentId == request.StudentId);
        if (overrideRow == null)
        {
            overrideRow = new AssignmentStudentOverride
            {
                AssignmentId = request.AssignmentId,
                StudentId = request.StudentId,
                TeacherId = assignment.TeacherId
            };
            _db.AssignmentStudentOverrides.Add(overrideRow);
        }

        overrideRow.ForceReview = false;
        overrideRow.ReopenExpiresAt = DateTime.UtcNow.AddMinutes(request.Minutes);

        await _db.SaveChangesAsync();
        return Ok(new { message = "تم إعادة فتح الواجب للطالب.", reopenExpiresAt = overrideRow.ReopenExpiresAt });
    }

    [HttpPost("delete/{assignmentId:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> Delete(int assignmentId)
    {
        var assignment = await _db.Assignments.FirstOrDefaultAsync(a => a.Id == assignmentId);
        if (assignment == null) return NotFound(new { message = "Assignment not found." });

        _db.Assignments.Remove(assignment);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Assignment deleted." });
    }
}
