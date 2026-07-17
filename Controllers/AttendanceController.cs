using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

// POST Attendance?lectureId=..  body: { encodedStudentId } OR { studentId }, plus optional { date }
// (single scan uses encodedStudentId; manual teacher entry uses studentId + date)
public record RecordAttendanceRequest(string? EncodedStudentId, int? StudentId, DateTime? Date);

// POST Attendance/bulk?lectureId=..  body: [ { encodedStudentId, date }, ... ]
public record BulkAttendanceItem(string EncodedStudentId, DateTime Date);

/// <summary>
/// Route: api/Attendance
///  POST Attendance?lectureId=..
///  POST Attendance/bulk?lectureId=..
/// The "encodedStudentId" is the payload embedded in each student's QR code
/// (in this reconstruction: their numeric student id).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
public class AttendanceController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IWhatsAppService _whatsApp;
    private readonly ILogger<AttendanceController> _logger;

    public AttendanceController(AppDbContext db, ITenantContext tenant, IWhatsAppService whatsApp, ILogger<AttendanceController> logger)
    {
        _db = db;
        _tenant = tenant;
        _whatsApp = whatsApp;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Record([FromQuery] int lectureId, [FromBody] RecordAttendanceRequest request)
    {
        if (_tenant.CurrentTenantId == null) return Forbid();

        if (string.IsNullOrWhiteSpace(request.EncodedStudentId) && request.StudentId == null)
            return BadRequest(new { message = "Either encodedStudentId or studentId must be provided." });

        int? studentId;
        string encodedStudentId;

        if (!string.IsNullOrWhiteSpace(request.EncodedStudentId))
        {
            // QR-scan path: resolve + validate the encoded payload as before.
            studentId = await ResolveStudentIdAsync(request.EncodedStudentId);
            encodedStudentId = request.EncodedStudentId;
        }
        else
        {
            // Manual teacher-entry path: studentId is given directly, so just
            // confirm it exists. We still store it as the "encoded" value too,
            // since that column is required and a plain id is a valid QR payload.
            var id = request.StudentId!.Value;
            studentId = await _db.Students.AnyAsync(s => s.Id == id) ? id : null;
            encodedStudentId = id.ToString();
        }

        if (studentId == null) return NotFound(new { message = "Student not found for this code." });

        // NOTE: the Flutter client (onlineScan.dart) checks `statusCode == 400`
        // specifically to show "الطالب مسجل مسبقًا" — it does NOT treat 409 as
        // that case. Must return 400 here, not Conflict(409), or the client
        // falls through to its generic error branch instead.
        if (await _db.Attendances.AnyAsync(a => a.LectureId == lectureId && a.StudentId == studentId.Value))
            return BadRequest(new { message = "Attendance already recorded." });

        var attendance = new Attendance
        {
            TeacherId = _tenant.CurrentTenantId.Value,
            LectureId = lectureId,
            StudentId = studentId.Value,
            EncodedStudentId = encodedStudentId,
        };
        if (request.Date.HasValue) attendance.Date = request.Date.Value;

        _db.Attendances.Add(attendance);
        await AutoSubscribeIfSubscriptionLectureAsync(lectureId, studentId.Value);
        await IssueTriggeredCodesAsync(lectureId, studentId.Value);
        await _db.SaveChangesAsync();

        await SendAttendanceWhatsAppAsync(studentId.Value, attendance.Date);

        return StatusCode(201, new { message = "Attendance recorded." });
    }

    [HttpPost("bulk")]
    public async Task<IActionResult> RecordBulk([FromQuery] int lectureId, [FromBody] List<BulkAttendanceItem> items)
    {
        if (_tenant.CurrentTenantId == null) return Forbid();

        var created = 0;
        var notifyList = new List<(int StudentId, DateTime Date)>();
        foreach (var item in items)
        {
            var studentId = await ResolveStudentIdAsync(item.EncodedStudentId);
            if (studentId == null) continue;
            if (await _db.Attendances.AnyAsync(a => a.LectureId == lectureId && a.StudentId == studentId.Value)) continue;

            _db.Attendances.Add(new Attendance
            {
                TeacherId = _tenant.CurrentTenantId.Value,
                LectureId = lectureId,
                StudentId = studentId.Value,
                EncodedStudentId = item.EncodedStudentId,
                Date = item.Date
            });
            await AutoSubscribeIfSubscriptionLectureAsync(lectureId, studentId.Value);
            await IssueTriggeredCodesAsync(lectureId, studentId.Value);
            notifyList.Add((studentId.Value, item.Date));
            created++;
        }

        await _db.SaveChangesAsync();

        foreach (var (studentId, date) in notifyList)
            await SendAttendanceWhatsAppAsync(studentId, date);

        return Ok(new { message = $"{created} attendance record(s) saved.", saved = created, total = items.Count });
    }

    // FEATURE: a lecture can be flagged with AutoSubscribe = true (teacher
    // sets this explicitly on the lecture, no more name-based heuristic).
    // Attending such a lecture means the student is now subscribed to that
    // lecture's Unit, even if they weren't enrolled in it before. We just
    // add the pending StudentUnitSubscription row here; SaveChangesAsync at
    // the end of the calling action persists it together with the
    // Attendance row.
    private async Task AutoSubscribeIfSubscriptionLectureAsync(int lectureId, int studentId)
    {
        var lecture = await _db.Lectures.AsNoTracking()
            .Where(l => l.Id == lectureId)
            .Select(l => new { l.UnitId, l.AutoSubscribe, l.TeacherId })
            .FirstOrDefaultAsync();

        if (lecture == null || lecture.UnitId == null) return;
        if (!lecture.AutoSubscribe) return;

        var unitId = lecture.UnitId.Value;
        var alreadySubscribed = await _db.StudentUnitSubscriptions
            .AnyAsync(s => s.StudentId == studentId && s.UnitId == unitId);
        if (alreadySubscribed) return;

        _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription
        {
            TeacherId = lecture.TeacherId,
            StudentId = studentId,
            UnitId = unitId
        });
    }

    // FEATURE: a Code can be created as a TEMPLATE tied to a Center lecture
    // (Code.IsTemplate + Code.TriggerLectureId — see CodesController.Generate).
    // Every time a student attends that lecture, we clone the template into a
    // brand-new, already-assigned Code row just for them (unique Value,
    // IsUsed = true, UsedByStudentId = them from the start) — so it behaves
    // exactly as if they'd redeemed a one-off code themselves, without ever
    // exposing a shared code string another student could grab. Visible to
    // the student via Students/codes (GetStudentCodes); never to anyone else.
    private async Task IssueTriggeredCodesAsync(int lectureId, int studentId)
    {
        var templates = await _db.Codes
            .Where(c => c.IsTemplate && c.TriggerLectureId == lectureId)
            .ToListAsync();

        foreach (var template in templates)
        {
            // One clone per student per template, ever — re-attending (not
            // that duplicate Attendance rows are even possible, see the 400
            // check above) or attending a re-created lecture with the same
            // template must never mint a second code for the same student.
            var alreadyIssued = await _db.Codes.AnyAsync(c =>
                c.SourceCodeTemplateId == template.Id && c.UsedByStudentId == studentId);
            if (alreadyIssued) continue;

            var issued = new Code
            {
                Value = await CodeGenerator.GenerateUniqueAsync(_db),
                SchoolYear = template.SchoolYear,
                UnitIds = template.UnitIds,
                LectureIds = template.LectureIds,
                TeacherId = template.TeacherId,
                SourceCodeTemplateId = template.Id,
                IsUsed = true,
                UsedByStudentId = studentId,
                UsedAt = DateTime.UtcNow
            };
            _db.Codes.Add(issued);

            // Same unlock-granting behavior as StudentsController.RedeemCode —
            // the code is issued already "redeemed", so apply its effects
            // immediately instead of waiting for a redeem call that will
            // never come.
            foreach (var unitId in issued.UnitIds)
            {
                if (!await _db.StudentUnitSubscriptions.AnyAsync(s => s.StudentId == studentId && s.UnitId == unitId))
                    _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = template.TeacherId, StudentId = studentId, UnitId = unitId });
            }
            foreach (var lecId in issued.LectureIds)
            {
                if (!await _db.StudentLectureUnlocks.AnyAsync(u => u.StudentId == studentId && u.LectureId == lecId))
                    _db.StudentLectureUnlocks.Add(new StudentLectureUnlock { TeacherId = template.TeacherId, StudentId = studentId, LectureId = lecId });
            }
        }
    }

    private async Task<int?> ResolveStudentIdAsync(string encodedStudentId)
    {
        if (int.TryParse(encodedStudentId, out var id) && await _db.Students.AnyAsync(s => s.Id == id))
            return id;
        return null;
    }

    // FEATURE: fires the "attendance recorded" WhatsApp message to the
    // student's parent right after the Attendance row is saved. Best-effort
    // only: any failure here (missing phone, GreenApi down, etc.) is logged
    // and swallowed — it must never fail the attendance request itself,
    // since SaveChangesAsync has already committed by the time this runs.
    private async Task SendAttendanceWhatsAppAsync(int studentId, DateTime attendanceDate)
    {
        try
        {
            var student = await _db.Students.AsNoTracking()
                .Where(s => s.Id == studentId)
                .Select(s => new { s.Name, s.ParentPhoneNumber })
                .FirstOrDefaultAsync();

            if (student == null || string.IsNullOrWhiteSpace(student.ParentPhoneNumber))
            {
                _logger.LogInformation("Skipping WhatsApp notification for student {StudentId}: no parent phone number on file.", studentId);
                return;
            }

            var teacherName = await _db.Teachers.AsNoTracking()
                .Where(t => t.Id == _tenant.CurrentTenantId!.Value)
                .Select(t => t.Name)
                .FirstOrDefaultAsync() ?? "المدرس";

            // Last grade: most recent of Quiz / CenterQuiz / Assignment results for this student.
            var lastQuiz = await _db.QuizResults.AsNoTracking()
                .Where(q => q.StudentId == studentId)
                .OrderByDescending(q => q.GradedAt)
                .Select(q => new { Text = $"{q.Score}/{q.TotalMarks}", When = q.GradedAt })
                .FirstOrDefaultAsync();
            var lastCenterQuiz = await _db.CenterQuizResults.AsNoTracking()
                .Where(q => q.StudentId == studentId)
                .OrderByDescending(q => q.Date)
                .Select(q => new { Text = $"{q.Title}: {q.Marks}/{q.TotalMarks}", When = q.Date })
                .FirstOrDefaultAsync();
            var lastGradeCandidates = new[] { lastQuiz, lastCenterQuiz }
                .Where(x => x != null)
                .OrderByDescending(x => x!.When)
                .ToList();
            var lastGradeText = lastGradeCandidates.FirstOrDefault()?.Text ?? "لا يوجد";

            // Last homework: most recent of HomeworkResult / AssignmentSubmission.
            var lastHomework = await _db.HomeworkResults.AsNoTracking()
                .Where(h => h.StudentId == studentId)
                .OrderByDescending(h => h.Date)
                .Select(h => new { Text = $"{h.Title}: {h.Marks}/{h.TotalMarks}", When = h.Date })
                .FirstOrDefaultAsync();
            var lastAssignment = await _db.AssignmentSubmissions.AsNoTracking()
                .Where(a => a.StudentId == studentId)
                .OrderByDescending(a => a.SubmittedAt)
                .Select(a => new { Text = $"{a.Score}/{a.TotalMarks}", When = a.SubmittedAt })
                .FirstOrDefaultAsync();
            var lastHomeworkCandidates = new[] { lastHomework, lastAssignment }
                .Where(x => x != null)
                .OrderByDescending(x => x!.When)
                .ToList();
            var lastHomeworkText = lastHomeworkCandidates.FirstOrDefault()?.Text ?? "لا يوجد";

            // Notebook status: whether the student has paid for at least one notebook.
            var hasPaidNotebook = await _db.NotebookPayments.AsNoTracking()
                .AnyAsync(p => p.StudentId == studentId);
            var notebookStatusText = hasPaidNotebook ? "تم الدفع" : "لم يتم الدفع";

            var data = new AttendanceWhatsAppNotification(
                StudentName: student.Name,
                TeacherName: teacherName,
                AttendanceLocalTime: attendanceDate,
                LastGradeText: lastGradeText,
                LastHomeworkText: lastHomeworkText,
                NotebookStatusText: notebookStatusText
            );

            var sent = await _whatsApp.SendAttendanceNotificationAsync(student.ParentPhoneNumber!, data);
            if (!sent)
                _logger.LogWarning("WhatsApp attendance notification not sent for student {StudentId}.", studentId);
        }
        catch (Exception ex)
        {
            // Belt-and-braces: IWhatsAppService implementations already swallow
            // their own errors, but never let this path take attendance down.
            _logger.LogWarning(ex, "Unexpected error while sending WhatsApp attendance notification for student {StudentId}.", studentId);
        }
    }
}
