using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/Logs. SuperAdmin-only. Backs the "اللوجز" (Logs) button on
/// SuperAdminDashboard: pick a teacher -> see either that teacher's OWN
/// error logs (their staff account causing 4xx/5xx), or their STUDENTS'
/// error logs grouped into hourly buckets so a SuperAdmin can spot e.g.
/// "a wave of 500s between 4 and 5 PM" and drill into the reasons.
///
/// Deliberately filters RequestErrorLog by an explicit {teacherId} route
/// parameter rather than relying on AppDbContext's tenant query filters
/// (RequestErrorLog has none) -- a SuperAdmin has no single active tenant,
/// and this feature is specifically about inspecting ANY teacher on demand.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.SuperAdmin)]
public class LogsController : ControllerBase
{
    private readonly AppDbContext _db;
    public LogsController(AppDbContext db)
    {
        _db = db;
    }

    private static readonly string[] StaffRoles =
        { Roles.Teacher, Roles.AssistantAdmin, Roles.Assistant };

    /// <summary>GET Logs/teachers/{teacherId}/summary -- quick counts for the
    /// "teacher logs vs student logs" choice screen.</summary>
    [HttpGet("teachers/{teacherId:int}/summary")]
    public async Task<IActionResult> GetSummary(int teacherId)
    {
        var teacherExists = await _db.Teachers.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(t => t.Id == teacherId);
        if (!teacherExists) return NotFound(new { message = "Teacher not found." });

        var baseQuery = _db.RequestErrorLogs.AsNoTracking().Where(l => l.TenantId == teacherId);

        var teacherLogsCount = await baseQuery.Where(l => StaffRoles.Contains(l.Role)).CountAsync();
        var studentLogsCount = await baseQuery.Where(l => l.Role == Roles.Student).CountAsync();

        return Ok(new
        {
            teacherId,
            teacherLogsCount,
            studentLogsCount
        });
    }

    /// <summary>GET Logs/teachers/{teacherId}/teacher-logs?p=.. -- this teacher's
    /// (and their assistants') own 4xx/5xx requests, newest first.</summary>
    [HttpGet("teachers/{teacherId:int}/teacher-logs")]
    public async Task<IActionResult> GetTeacherLogs(int teacherId, [FromQuery] int p = 1)
    {
        var logs = await _db.RequestErrorLogs.AsNoTracking()
            .Where(l => l.TenantId == teacherId && StaffRoles.Contains(l.Role))
            .OrderByDescending(l => l.CreatedAtUtc)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .Select(l => new
            {
                l.Id,
                l.Role,
                l.UserId,
                l.Method,
                l.Path,
                l.StatusCode,
                l.Message,
                createdAtUtc = l.CreatedAtUtc
            })
            .ToListAsync();

        return Ok(logs);
    }

    /// <summary>
    /// GET Logs/teachers/{teacherId}/student-error-buckets?date=yyyy-MM-dd
    /// Groups this teacher's STUDENTS' 4xx/5xx errors into hourly buckets for
    /// the given calendar day (server/UTC-based "date"; defaults to today),
    /// each with a separate 400-range and 500-range count -- e.g. one card
    /// per "4 PM - 5 PM" slot, so a SuperAdmin can see spikes at a glance.
    /// </summary>
    [HttpGet("teachers/{teacherId:int}/student-error-buckets")]
    public async Task<IActionResult> GetStudentErrorBuckets(int teacherId, [FromQuery] DateTime? date = null)
    {
        var day = (date ?? DateTime.UtcNow).Date;
        var nextDay = day.AddDays(1);

        var dayLogs = await _db.RequestErrorLogs.AsNoTracking()
            .Where(l => l.TenantId == teacherId
                        && l.Role == Roles.Student
                        && l.CreatedAtUtc >= day && l.CreatedAtUtc < nextDay)
            .Select(l => new { l.CreatedAtUtc, l.StatusCode })
            .ToListAsync();

        var buckets = dayLogs
            .GroupBy(l => l.CreatedAtUtc.Hour)
            .Select(g => new
            {
                hour = g.Key,
                hourLabel = $"{g.Key:00}:00 - {(g.Key + 1) % 24:00}:00",
                count4xx = g.Count(x => x.StatusCode is >= 400 and < 500),
                count5xx = g.Count(x => x.StatusCode is >= 500 and < 600),
                total = g.Count()
            })
            .OrderBy(b => b.hour)
            .ToList();

        return Ok(new
        {
            teacherId,
            date = day.ToString("yyyy-MM-dd"),
            buckets
        });
    }

    /// <summary>
    /// GET Logs/teachers/{teacherId}/student-error-buckets/details?date=..&hour=..
    /// The individual errors inside one hourly bucket -- path, status code,
    /// reason/message, which student, and the exact time -- for the "why"
    /// behind a card's numbers.
    /// </summary>
    [HttpGet("teachers/{teacherId:int}/student-error-buckets/details")]
    public async Task<IActionResult> GetStudentErrorBucketDetails(
        int teacherId, [FromQuery] DateTime date, [FromQuery] int hour, [FromQuery] int p = 1)
    {
        if (hour is < 0 or > 23) return BadRequest(new { message = "hour must be between 0 and 23." });

        var bucketStart = date.Date.AddHours(hour);
        var bucketEnd = bucketStart.AddHours(1);

        var query = _db.RequestErrorLogs.AsNoTracking()
            .Where(l => l.TenantId == teacherId
                        && l.Role == Roles.Student
                        && l.CreatedAtUtc >= bucketStart && l.CreatedAtUtc < bucketEnd)
            .OrderByDescending(l => l.CreatedAtUtc);

        var pageLogs = await query
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .Select(l => new { l.Id, l.UserId, l.Method, l.Path, l.StatusCode, l.Message, createdAtUtc = l.CreatedAtUtc })
            .ToListAsync();

        // Resolve student names for display (IgnoreQueryFilters: a SuperAdmin
        // has no active tenant, and Student itself carries no tenant filter,
        // but being explicit here matches the pattern used elsewhere).
        var studentIds = pageLogs.Where(l => l.UserId.HasValue).Select(l => l.UserId!.Value).Distinct().ToList();
        var studentNames = await _db.Students.AsNoTracking().IgnoreQueryFilters()
            .Where(s => studentIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name })
            .ToDictionaryAsync(s => s.Id, s => s.Name);

        var result = pageLogs.Select(l => new
        {
            l.Id,
            studentId = l.UserId,
            studentName = l.UserId.HasValue && studentNames.TryGetValue(l.UserId.Value, out var n) ? n : null,
            l.Method,
            l.Path,
            l.StatusCode,
            reason = l.Message,
            l.createdAtUtc
        });

        return Ok(result);
    }
}
