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
    public AttendanceController(AppDbContext db) => _db = db;

    [HttpPost]
    public async Task<IActionResult> Record([FromQuery] int lectureId, [FromBody] RecordAttendanceRequest request)
    {
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
            LectureId = lectureId,
            StudentId = studentId.Value,
            EncodedStudentId = encodedStudentId,
        };
        if (request.Date.HasValue) attendance.Date = request.Date.Value;

        _db.Attendances.Add(attendance);
        await _db.SaveChangesAsync();

        return StatusCode(201, new { message = "Attendance recorded." });
    }

    [HttpPost("bulk")]
    public async Task<IActionResult> RecordBulk([FromQuery] int lectureId, [FromBody] List<BulkAttendanceItem> items)
    {
        var created = 0;
        foreach (var item in items)
        {
            var studentId = await ResolveStudentIdAsync(item.EncodedStudentId);
            if (studentId == null) continue;
            if (await _db.Attendances.AnyAsync(a => a.LectureId == lectureId && a.StudentId == studentId.Value)) continue;

            _db.Attendances.Add(new Attendance
            {
                LectureId = lectureId,
                StudentId = studentId.Value,
                EncodedStudentId = item.EncodedStudentId,
                Date = item.Date
            });
            created++;
        }

        await _db.SaveChangesAsync();
        return Ok(new { message = $"{created} attendance record(s) saved.", saved = created, total = items.Count });
    }

    private async Task<int?> ResolveStudentIdAsync(string encodedStudentId)
    {
        if (int.TryParse(encodedStudentId, out var id) && await _db.Students.AnyAsync(s => s.Id == id))
            return id;
        return null;
    }
}
