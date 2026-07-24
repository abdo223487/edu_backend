using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

// POST Dismissals  body: { title, unitId, groupId, date }
// "title" is the lesson/lecture name shown in the WhatsApp message ("بعنوان / ..").
// "date" is the teacher-picked date+time the lesson finished (used verbatim in
// the message, NOT DateTime.UtcNow) -- same client convention as everywhere
// else that already sends a Date from a date+time picker.
public record CreateDismissalRequest(string Title, int UnitId, int GroupId, DateTime Date);

/// <summary>
/// Route: api/Dismissals — "الانصراف" feature.
///
/// One-shot, group-wide announcement: the teacher marks a lesson (given a
/// title, tied to one Unit, taught to one Group) as finished, and every
/// parent of every (active) student currently in that Group gets a single
/// WhatsApp message saying so. Unlike Attendance (per-student, QR-driven,
/// with per-student tracking rows), this has no per-student bookkeeping --
/// just one Dismissal row for the record, and a broadcast.
///
///  POST Dismissals
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
public class DismissalsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IWhatsAppService _whatsApp;
    private readonly ILogger<DismissalsController> _logger;

    public DismissalsController(AppDbContext db, ITenantContext tenant, IWhatsAppService whatsApp, ILogger<DismissalsController> logger)
    {
        _db = db;
        _tenant = tenant;
        _whatsApp = whatsApp;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDismissalRequest request)
    {
        if (_tenant.CurrentTenantId == null) return Forbid();

        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequest(new { message = "Title is required." });

        // Both scoped to the current tenant via the global query filters on
        // Unit/Group, so a teacher can never fire this against another
        // teacher's unit/group.
        var unitExists = await _db.Units.AsNoTracking().AnyAsync(u => u.Id == request.UnitId);
        if (!unitExists) return NotFound(new { message = "Unit not found." });

        var group = await _db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == request.GroupId);
        if (group == null) return NotFound(new { message = "Group not found." });

        var dismissal = new Dismissal
        {
            TeacherId = _tenant.CurrentTenantId.Value,
            Title = request.Title,
            UnitId = request.UnitId,
            GroupId = request.GroupId,
            Date = request.Date
        };
        _db.Dismissals.Add(dismissal);
        await _db.SaveChangesAsync();

        var sentCount = await SendDismissalWhatsAppAsync(group.Id, group.Name, request.Title, request.Date);

        return StatusCode(201, new { id = dismissal.Id, message = "Dismissal recorded.", notified = sentCount });
    }

    // FEATURE: fires the "lesson finished" WhatsApp message to every active
    // (not suspended/cancelled) student's parent in the Group, right after
    // the Dismissal row is saved. Best-effort per recipient: one parent's
    // failure (missing/invalid number, WhatsApp API down, etc.) never stops
    // the rest of the group from being notified, and never fails the
    // request itself -- SaveChangesAsync has already committed by this point.
    private async Task<int> SendDismissalWhatsAppAsync(int groupId, string groupName, string lessonTitle, DateTime date)
    {
        var sent = 0;
        try
        {
            var teacherName = await _db.Teachers.AsNoTracking()
                .Where(t => t.Id == _tenant.CurrentTenantId!.Value)
                .Select(t => t.Name)
                .FirstOrDefaultAsync() ?? "المدرس";

            // Active members of this Group under the current tenant, via the
            // membership join table (a student's legacy Student.GroupId isn't
            // tenant-scoped, so it's not used here) -- same reasoning as every
            // other group-wide feature (e.g. AssignmentsController.GetAll).
            var parentPhoneNumbers = await _db.StudentGroupMemberships.AsNoTracking()
                .Where(m => m.GroupId == groupId && !m.IsSuspended && !m.IsCancelled)
                .Select(m => m.Student!.ParentPhoneNumber)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct()
                .ToListAsync();

            var data = new DismissalWhatsAppNotification(
                TeacherName: teacherName,
                LessonTitle: lessonTitle,
                GroupName: groupName,
                DismissalLocalTime: date
            );

            foreach (var phone in parentPhoneNumbers)
            {
                var ok = await _whatsApp.SendDismissalNotificationAsync(phone!, data);
                if (ok) sent++;
                else _logger.LogWarning("WhatsApp dismissal notification not sent to {Phone} for group {GroupId}.", phone, groupId);
            }
        }
        catch (Exception ex)
        {
            // Belt-and-braces: IWhatsAppService implementations already swallow
            // their own errors, but never let this path take the request down.
            _logger.LogWarning(ex, "Unexpected error while sending WhatsApp dismissal notifications for group {GroupId}.", groupId);
        }

        return sent;
    }
}
