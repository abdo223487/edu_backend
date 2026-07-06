using System.Text;
using EduApi.Common;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/Students. This is the largest surface in the app; see class-level
/// method XML docs for the exact endpoint each action reconstructs.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class StudentsController : ControllerBase
{
    private readonly AppDbContext _db;
    public StudentsController(AppDbContext db) => _db = db;

    // POST Students
    [HttpPost]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Create([FromBody] CreateStudentRequest request)
    {
        var group = await _db.Groups.FirstOrDefaultAsync(e => e.Id == (request.GroupId));
        if (group == null) return BadRequest(new { message = "Group not found." });

        var student = new Student
        {
            Name = request.Name,
            PhoneNumber = request.PhoneNumber,
            ParentPhoneNumber = request.ParentPhoneNumber,
            UserName = request.UserName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(
                string.IsNullOrEmpty(request.Password) ? GenerateTempPassword() : request.Password),
            GroupId = request.GroupId,
            SchoolYear = group.SchoolYear
        };

        _db.Students.Add(student);
        await _db.SaveChangesAsync();

        foreach (var unitId in request.UnitIds ?? new())
            _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { StudentId = student.Id, UnitId = unitId });
        await _db.SaveChangesAsync();

        return StatusCode(201, ToListItem(student, group.Name));
    }

    // GET Students?schoolYear=..&groupId=..&p=..&q=..
    [HttpGet]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetAll(
        [FromQuery] int? schoolYear, [FromQuery] int? groupId,
        [FromQuery] int p = 1, [FromQuery] string? q = null)
        => await ListStudents(s => !s.IsSuspended && !s.IsCancelled, schoolYear, groupId, p, q);

    // GET Students/suspended?schoolYear=..&p=..&q=..
    [HttpGet("suspended")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetSuspended(
        [FromQuery] int? schoolYear, [FromQuery] int p = 1, [FromQuery] string? q = null)
        => await ListStudents(s => s.IsSuspended, schoolYear, null, p, q);

    // GET Students/cancelled?schoolYear=..&p=..&q=..
    [HttpGet("cancelled")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetCancelled(
        [FromQuery] int? schoolYear, [FromQuery] int p = 1, [FromQuery] string? q = null)
        => await ListStudents(s => s.IsCancelled, schoolYear, null, p, q);

    private async Task<IActionResult> ListStudents(
        System.Linq.Expressions.Expression<Func<Student, bool>> statusFilter,
        int? schoolYear, int? groupId, int p, string? q)
    {
        var query = _db.Students.Include(s => s.Group).Where(statusFilter);
        if (groupId.HasValue) query = query.Where(s => s.GroupId == groupId.Value);
        else if (schoolYear.HasValue) query = query.Where(s => s.SchoolYear == schoolYear.Value);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(s => s.Name.Contains(q) || s.PhoneNumber.Contains(q));

        var students = await query
            .OrderBy(s => s.Name)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .ToListAsync();

        return Ok(students.Select(s => ToListItem(s, s.Group?.Name)));
    }

    // GET Students/count
    [HttpGet("count")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Count()
        => Ok(await _db.Students.CountAsync(s => !s.IsCancelled));

    // GET Students/{id}
    [HttpGet("{id:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetById(int id)
    {
        var student = await _db.Students.Include(s => s.Group)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (student == null) return NotFound(new { message = "Student not found." });

        var subs = await _db.StudentUnitSubscriptions.Where(u => u.StudentId == id).Select(u => u.UnitId).ToListAsync();

        return Ok(new
        {
            id = student.Id,
            name = student.Name,
            phoneNumber = student.PhoneNumber,
            parentPhoneNumber = student.ParentPhoneNumber,
            // NOTE: intentionally lowercase "username" (not "userName") — the
            // Flutter profile page reads _student!["username"] exactly.
            username = student.UserName,
            groupId = student.GroupId,
            groupName = student.Group?.Name,
            schoolYear = student.SchoolYear,
            isSuspended = student.IsSuspended,
            isCancelled = student.IsCancelled,
            createdAt = student.CreatedAt,
            // Client reads this as `data['subscribedUnits'] as List` then `unit['id']` per item.
            subscribedUnits = subs.Select(unitId => new { id = unitId })
        });
    }

    // GET Students/{id}/state-history
    [HttpGet("{id:int}/state-history")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetStateHistory(int id)
    {
        var history = await _db.StateHistoryEntries.Where(h => h.StudentId == id)
            .OrderByDescending(h => h.Date)
            .Select(h => new StateHistoryItem(h.Action, null, h.Reason, h.Date))
            .ToListAsync();

        return Ok(history);
    }

    // PUT Students/{id}/group  body: { groupId }
    [HttpPut("{id:int}/group")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangeGroup(int id, [FromBody] ChangeGroupRequest request)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (id));
        if (student == null) return NotFound(new { message = "Student not found." });

        var group = await _db.Groups.FirstOrDefaultAsync(e => e.Id == (request.GroupId));
        if (group == null) return BadRequest(new { message = "Group not found." });

        student.GroupId = request.GroupId;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Group updated." });
    }

    // POST Students/change/password?studentId=..  -> generates + returns a new password
    [HttpPost("change/password")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ResetPassword([FromQuery] int studentId)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (studentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        var newPassword = GenerateTempPassword();
        student.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        await _db.SaveChangesAsync();

        return Ok(new { newPassword });
    }

    // POST Students/change/phone-number?studentId=..&newNumber=..
    [HttpPost("change/phone-number")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangePhoneNumber([FromQuery] int studentId, [FromQuery] string newNumber)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (studentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        student.PhoneNumber = newNumber;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Phone number updated." });
    }

    // POST Students/change/parent-phone-number?studentId=..&newParentPhoneNumber=..
    [HttpPost("change/parent-phone-number")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangeParentPhoneNumber([FromQuery] int studentId, [FromQuery] string newParentPhoneNumber)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (studentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        student.ParentPhoneNumber = newParentPhoneNumber;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Parent phone number updated." });
    }

    // POST Students/change/name?studentId=..&newName=..
    [HttpPost("change/name")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangeName([FromQuery] int studentId, [FromQuery] string newName)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (studentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        student.Name = newName;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Name updated." });
    }

    // POST Students/suspend  body: { studentId, comment }
    [HttpPost("suspend")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Suspend([FromBody] SuspendRequest request)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (request.StudentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        student.IsSuspended = true;
        _db.StateHistoryEntries.Add(new StateHistoryEntry { StudentId = student.Id, Action = "Suspended", Reason = request.Comment });
        await _db.SaveChangesAsync();
        return Ok(new { message = "Student suspended." });
    }

    // POST Students/reactivate  body: { studentId, comment }
    [HttpPost("reactivate")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Reactivate([FromBody] ReactivateRequest request)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (request.StudentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        student.IsSuspended = false;
        student.IsCancelled = false;
        _db.StateHistoryEntries.Add(new StateHistoryEntry { StudentId = student.Id, Action = "Reactivated", Reason = request.Comment });
        await _db.SaveChangesAsync();
        return Ok(new { message = "Student reactivated." });
    }

    // POST Students/delete?studentId=..
    [HttpPost("delete")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Delete([FromQuery] int studentId)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (studentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        _db.Students.Remove(student);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Student deleted." });
    }

    // POST Students/codes  body: { code }  (student redeems a code)
    [HttpPost("codes")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> RedeemCode([FromBody] RedeemCodeRequest request)
    {
        var code = await _db.Codes.FirstOrDefaultAsync(c => c.Value == request.Code);
        if (code == null) return NotFound(new { message = "Invalid code." });
        if (code.IsUsed) return Conflict(new { message = "Code already used." });

        var studentId = User.GetUserId();
        code.IsUsed = true;
        code.UsedByStudentId = studentId;
        code.UsedAt = DateTime.UtcNow;

        foreach (var unitId in code.UnitIds)
        {
            if (!await _db.StudentUnitSubscriptions.AnyAsync(s => s.StudentId == studentId && s.UnitId == unitId))
                _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { StudentId = studentId, UnitId = unitId });
        }

        await _db.SaveChangesAsync();
        return Ok(new { message = "Code redeemed successfully.", unitIds = code.UnitIds, lectureIds = code.LectureIds });
    }

    // GET Students/attendance?studentId=..&unitId=..  (teacher, for a specific student)
    // GET Students/attendance?unitId=..                (student, own attendance)
    //
    // Response: one row PER LECTURE the student's group (optionally narrowed to a
    // unit) is scheduled for, each with `isAttended: true|false` — NOT just the
    // lectures the student actually attended. The Flutter client reads exactly
    // `lec['lectureName']` and `lec['isAttended'] ?? false` and computes
    // attended/absent counts locally by filtering this array, so an absent
    // lecture still needs to appear here with isAttended == false.
    [HttpGet("attendance")]
    public async Task<IActionResult> GetAttendance([FromQuery] int? studentId, [FromQuery] int? unitId)
    {
        var sid = User.IsInRole(Roles.Student) ? User.GetUserId() : studentId;
        if (sid == null) return BadRequest(new { message = "studentId is required." });

        var student = await _db.Students.FirstOrDefaultAsync(s => s.Id == sid.Value);
        if (student == null) return NotFound(new { message = "Student not found." });

        // Lectures scheduled for this student's group (Center attendance-taking only
        // makes sense for Center lectures, but we don't hard-filter on that here since
        // the client never distinguishes by attendanceMethod for this endpoint).
        var lecturesQuery = _db.Lectures
            .Where(l => l.GroupIdsCsv.Contains(student.GroupId.ToString()));
        if (unitId.HasValue) lecturesQuery = lecturesQuery.Where(l => l.UnitId == unitId.Value);

        var lectures = await lecturesQuery.OrderBy(l => l.CreatedAt).ToListAsync();

        var attendedLookup = await _db.Attendances
            .Where(a => a.StudentId == sid.Value)
            .ToDictionaryAsync(a => a.LectureId, a => a.Date);

        var result = lectures.Select(l => new AttendanceRecordDto(
            l.Id,
            l.Name,
            attendedLookup.ContainsKey(l.Id),
            attendedLookup.TryGetValue(l.Id, out var date) ? date : null));

        return Ok(result);
    }

    // GET Students/qr  (student) -> returns a quoted base64-encoded PNG QR string
    [HttpGet("qr")]
    [Authorize(Roles = Roles.Student)]
    public IActionResult GetQr()
    {
        var studentId = User.GetUserId();
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(studentId.ToString(), QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(data);
        var bytes = qrCode.GetGraphic(10);
        var base64 = Convert.ToBase64String(bytes);

        // Client does: response.body.trim().replaceAll('"', '') then base64Decode(...)
        return Content($"\"{base64}\"", "application/json", Encoding.UTF8);
    }

    // POST Students/subscribe/unit  body: { studentId, unitId }
    [HttpPost("subscribe/unit")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> SubscribeUnit([FromBody] UnitSubscriptionRequest request)
    {
        if (!await _db.StudentUnitSubscriptions.AnyAsync(s => s.StudentId == request.StudentId && s.UnitId == request.UnitId))
        {
            _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { StudentId = request.StudentId, UnitId = request.UnitId });
            await _db.SaveChangesAsync();
        }
        return Ok(new { message = "Subscribed." });
    }

    // POST Students/unsubscribe/unit  body: { studentId, unitId }
    [HttpPost("unsubscribe/unit")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> UnsubscribeUnit([FromBody] UnitSubscriptionRequest request)
    {
        var sub = await _db.StudentUnitSubscriptions
            .FirstOrDefaultAsync(s => s.StudentId == request.StudentId && s.UnitId == request.UnitId);
        if (sub != null)
        {
            _db.StudentUnitSubscriptions.Remove(sub);
            await _db.SaveChangesAsync();
        }
        return Ok(new { message = "Unsubscribed." });
    }

    // GET Students/{notebookId}/payments?studentId=..
    [HttpGet("{notebookId:int}/payments")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetNotebookPaymentsForStudent(int notebookId, [FromQuery] int studentId)
    {
        var payments = await _db.NotebookPayments
            .Where(p => p.NotebookId == notebookId && p.StudentId == studentId)
            .Select(p => new { id = p.Id, price = p.Price, discountedPrice = p.DiscountedPrice, date = p.Date })
            .ToListAsync();

        return Ok(payments);
    }

    // POST Students/{notebookId}/pay  body: { studentId, amount, lectureId }
    [HttpPost("{notebookId:int}/pay")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> PayNotebook(int notebookId, [FromBody] PayNotebookRequest request)
    {
        var notebook = await _db.Notebooks.FirstOrDefaultAsync(e => e.Id == (notebookId));
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        var payment = new NotebookPayment
        {
            NotebookId = notebookId,
            StudentId = request.StudentId,
            Price = request.Amount
        };
        _db.NotebookPayments.Add(payment);
        await _db.SaveChangesAsync();

        return StatusCode(201, new { id = payment.Id, message = "Payment recorded." });
    }

    // GET Students/{notebookId}/discount?studentId=..
    // Returns the notebook's info + this student's payment summary for it.
    // Added alongside the existing POST of the same route (which applies a
    // discount) — the client fetches this first to show notebook details.
    [HttpGet("{notebookId:int}/discount")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetNotebookDiscountInfo(int notebookId, [FromQuery] int studentId)
    {
        var notebook = await _db.Notebooks.FirstOrDefaultAsync(e => e.Id == (notebookId));
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        var payments = await _db.NotebookPayments
            .Where(p => p.NotebookId == notebookId && p.StudentId == studentId)
            .OrderByDescending(p => p.Date)
            .ToListAsync();

        var paid = payments.Sum(p => p.Price);
        var discountedPrice = payments.FirstOrDefault(p => p.DiscountedPrice.HasValue)?.DiscountedPrice
                               ?? notebook.Price;

        return Ok(new
        {
            notebook = new { name = notebook.Name, price = notebook.Price, paid },
            discountedPrice
        });
    }

    // POST Students/{notebookId}/discount?studentId=..&discountedPrice=..
    [HttpPost("{notebookId:int}/discount")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ApplyDiscount(int notebookId, [FromQuery] int studentId, [FromQuery] int discountedPrice)
    {
        var notebook = await _db.Notebooks.FirstOrDefaultAsync(e => e.Id == (notebookId));
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        var payment = new NotebookPayment
        {
            NotebookId = notebookId,
            StudentId = studentId,
            Price = notebook.Price,
            DiscountedPrice = discountedPrice
        };
        _db.NotebookPayments.Add(payment);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Discount applied.", discountedPrice });
    }

    // GET Students/quiz-results/center?studentId=..  (teacher) / no params (student, own)
    [HttpGet("quiz-results/center")]
    public async Task<IActionResult> GetCenterQuizResults([FromQuery] int? studentId)
    {
        var sid = User.IsInRole(Roles.Student) ? User.GetUserId() : studentId;
        if (sid == null) return BadRequest(new { message = "studentId is required." });

        var results = await _db.CenterQuizResults.Where(r => r.StudentId == sid.Value)
            .Select(r => new { id = r.Id, title = r.Title, mark = r.Marks, quizTotalMarks = r.TotalMarks, date = r.Date })
            .ToListAsync();

        return Ok(results);
    }

    // POST Students/quiz-results/center/add  body: { studentId, quizTotalMarks, mark }
    [HttpPost("quiz-results/center/add")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> AddCenterQuizResult([FromBody] AddCenterQuizResultRequest request)
    {
        var result = new CenterQuizResult
        {
            StudentId = request.StudentId,
            Title = "Center Quiz",
            Marks = request.Mark,
            TotalMarks = request.QuizTotalMarks
        };
        _db.CenterQuizResults.Add(result);
        await _db.SaveChangesAsync();
        return StatusCode(201, new { id = result.Id });
    }

    // POST Students/quiz-results/change?studentId=..&quizResultId=..&newMark=..  body: {}
    [HttpPost("quiz-results/change")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangeCenterQuizMark([FromQuery] int studentId, [FromQuery] int quizResultId, [FromQuery] int newMark)
    {
        var result = await _db.CenterQuizResults.FirstOrDefaultAsync(r => r.Id == quizResultId && r.StudentId == studentId);
        if (result == null) return NotFound(new { message = "Result not found." });

        result.Marks = newMark;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Mark updated." });
    }

    // POST Students/quiz-results/delete?studentId=..&quizResultId=..
    [HttpPost("quiz-results/delete")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> DeleteCenterQuizResult([FromQuery] int studentId, [FromQuery] int quizResultId)
    {
        var result = await _db.CenterQuizResults.FirstOrDefaultAsync(r => r.Id == quizResultId && r.StudentId == studentId);
        if (result == null) return NotFound(new { message = "Result not found." });

        _db.CenterQuizResults.Remove(result);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Result deleted." });
    }

    // GET Students/homework-results?studentId=..  (teacher) / no params (student, own)
    [HttpGet("homework-results")]
    public async Task<IActionResult> GetHomeworkResults([FromQuery] int? studentId)
    {
        var sid = User.IsInRole(Roles.Student) ? User.GetUserId() : studentId;
        if (sid == null) return BadRequest(new { message = "studentId is required." });

        var results = await _db.HomeworkResults.Where(r => r.StudentId == sid.Value)
            .Select(r => new { id = r.Id, title = r.Title, mark = r.Marks, totalMarks = r.TotalMarks, date = r.Date })
            .ToListAsync();

        return Ok(results);
    }

    // POST Students/homework-results/add  body: { studentId, totalMarks, mark }
    [HttpPost("homework-results/add")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> AddHomeworkResult([FromBody] AddHomeworkResultRequest request)
    {
        var result = new HomeworkResult
        {
            StudentId = request.StudentId,
            Title = "Homework",
            Marks = request.Mark,
            TotalMarks = request.TotalMarks
        };
        _db.HomeworkResults.Add(result);
        await _db.SaveChangesAsync();
        return StatusCode(201, new { id = result.Id });
    }

    // POST Students/homework-results/change?studentId=..&homeworkResultId=..&newMark=..  body: {}
    [HttpPost("homework-results/change")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangeHomeworkMark([FromQuery] int studentId, [FromQuery] int homeworkResultId, [FromQuery] int newMark)
    {
        var result = await _db.HomeworkResults.FirstOrDefaultAsync(r => r.Id == homeworkResultId && r.StudentId == studentId);
        if (result == null) return NotFound(new { message = "Result not found." });

        result.Marks = newMark;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Mark updated." });
    }

    // POST Students/homework-results/delete?studentId=..&homeworkResultId=..
    [HttpPost("homework-results/delete")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> DeleteHomeworkResult([FromQuery] int studentId, [FromQuery] int homeworkResultId)
    {
        var result = await _db.HomeworkResults.FirstOrDefaultAsync(r => r.Id == homeworkResultId && r.StudentId == studentId);
        if (result == null) return NotFound(new { message = "Result not found." });

        _db.HomeworkResults.Remove(result);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Result deleted." });
    }

    // GET Students/quiz-results/online  (student, own online-quiz history)
    [HttpGet("quiz-results/online")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> GetOnlineQuizResults()
    {
        var studentId = User.GetUserId();
        var results = await _db.QuizResults.Include(r => r.Answers)
            .Where(r => r.StudentId == studentId)
            .Select(r => new { id = r.Id, quizId = r.QuizId, mark = r.Score, quizTotalMarks = r.TotalMarks, date = r.GradedAt })
            .ToListAsync();

        return Ok(results);
    }

    private static string GenerateTempPassword() => Guid.NewGuid().ToString("N")[..8];

    private static StudentListItem ToListItem(Student s, string? groupName)
    {
        var status = s.IsCancelled ? "Cancelled" : s.IsSuspended ? "Suspended" : "Active";
        return new(
            s.Id, s.Name, s.PhoneNumber, s.UserName, s.GroupId, groupName, s.IsSuspended, s.IsCancelled,
            status, null);
    }
}
