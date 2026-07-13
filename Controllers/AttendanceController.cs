using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
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
    public AttendanceController(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
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
        await _db.SaveChangesAsync();

        return StatusCode(201, new { message = "Attendance recorded." });
    }

    [HttpPost("bulk")]
    public async Task<IActionResult> RecordBulk([FromQuery] int lectureId, [FromBody] List<BulkAttendanceItem> items)
    {
        if (_tenant.CurrentTenantId == null) return Forbid();

        var created = 0;
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
            created++;
        }

        await _db.SaveChangesAsync();
        return Ok(new { message = $"{created} attendance record(s) saved.", saved = created, total = items.Count });
    }

    // FEATURE: some Center lectures are named as a "subscription" lecture
    // (the teacher puts "اشتراك"/"أشتراك" in the lecture Name) — attending
    // one of these means the student is now subscribed to that lecture's
    // Unit, even if they weren't enrolled in it before. We just add the
    // pending StudentUnitSubscription row here; SaveChangesAsync at the end
    // of the calling action persists it together with the Attendance row.
    private async Task AutoSubscribeIfSubscriptionLectureAsync(int lectureId, int studentId)
    {
        var lecture = await _db.Lectures.AsNoTracking()
            .Where(l => l.Id == lectureId)
            .Select(l => new { l.UnitId, l.Name, l.TeacherId })
            .FirstOrDefaultAsync();

        if (lecture == null || lecture.UnitId == null) return;
        if (lecture.Name == null ||
            (!lecture.Name.Contains("اشتراك") && !lecture.Name.Contains("أشتراك")))
            return;

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

    private async Task<int?> ResolveStudentIdAsync(string encodedStudentId)
    {
        if (int.TryParse(encodedStudentId, out var id) && await _db.Students.AnyAsync(s => s.Id == id))
            return id;
        return null;
    }
}
