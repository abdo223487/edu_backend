using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record CreateNotificationRequest(string Title, string Message, List<int> GroupIds, int? UnitId);

// PATCH Notifications/{id}  body: { title, message, unitId, groupIds }  (full edit, not a read-toggle)
public record EditNotificationRequest(string Title, string Message, int? UnitId, List<int> GroupIds);

/// <summary>
/// Route: api/Notifications
///  GET   Notifications?p=..&groupId=..     (teacher, groupId optional filter)
///  GET   Notifications?p=..                (student, own group inferred from JWT)
///  POST  Notifications                     body: { title, message, groupIds, unitId }
///  POST  Notifications/delete?notificationId=..
///  PATCH Notifications/{id}                body: { isRead }
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly Common.ITenantContext _tenant;
    public NotificationsController(AppDbContext db, Common.ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] int p = 1, [FromQuery] int? groupId = null,
        [FromQuery] int? schoolYear = null, [FromQuery] int? unitId = null)
    {
        var query = _db.Notifications.AsQueryable();

        if (User.IsInRole(Roles.Student))
        {
            var myGroup = User.GetGroupId(_tenant.CurrentTenantId);
            if (myGroup.HasValue) query = query.Where(n => n.GroupIdsCsv.Contains(myGroup.Value.ToString()));

            // Same subscription gate as Units: a notification tied to a specific
            // unit (UnitId != null) is only visible if the student is subscribed
            // to that unit. Notifications with no UnitId (general announcements)
            // are unaffected — they were never unit-scoped to begin with.
            var subscribedIds = User.GetUnitIds();
            query = query.Where(n => n.UnitId == null || subscribedIds.Contains(n.UnitId.Value));
        }
        else if (groupId.HasValue)
        {
            query = query.Where(n => n.GroupIdsCsv.Contains(groupId.Value.ToString()));
        }

        if (unitId.HasValue) query = query.Where(n => n.UnitId == unitId.Value);

        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .Select(n => new
            {
                id = n.Id,
                title = n.Title,
                message = n.Message,
                groupIds = n.GroupIds,
                unitId = n.UnitId,
                isRead = n.IsRead,
                type = n.Type,
                date = n.CreatedAt
            })
            .ToListAsync();

        return Ok(items);
    }

    [HttpPost]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Create([FromBody] CreateNotificationRequest request)
    {
        var notification = new Notification
        {
            Title = request.Title,
            Message = request.Message,
            GroupIds = request.GroupIds ?? new(),
            UnitId = request.UnitId,
            TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
        };
        _db.Notifications.Add(notification);
        await _db.SaveChangesAsync();

        return StatusCode(201, new { id = notification.Id, message = "Notification sent." });
    }

    [HttpPost("delete")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Delete([FromQuery] int notificationId)
    {
        var notification = await _db.Notifications.FirstOrDefaultAsync(e => e.Id == (notificationId));
        if (notification == null) return NotFound(new { message = "Notification not found." });

        _db.Notifications.Remove(notification);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Notification deleted." });
    }

    [HttpPatch("{id:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Edit(int id, [FromBody] EditNotificationRequest request)
    {
        var notification = await _db.Notifications.FirstOrDefaultAsync(e => e.Id == (id));
        if (notification == null) return NotFound(new { message = "Notification not found." });

        notification.Title = request.Title;
        notification.Message = request.Message;
        notification.UnitId = request.UnitId;
        notification.GroupIds = request.GroupIds ?? new();

        await _db.SaveChangesAsync();
        return Ok(new { message = "Notification updated." });
    }
}
