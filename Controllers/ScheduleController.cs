using EduApi.Common;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// The class schedule ("مواعيد الحصص") as a REAL calendar (month/year, like a
/// phone calendar) -- not a recurring weekly pattern. Each entry is one
/// specific date with a free-text lesson description. Route: api/Schedule.
///  GET  Schedule?schoolYear=X&amp;year=Y&amp;month=M   (teacher + student -- one month's dots)
///  POST Schedule/save                            (teacher/assistant -- upsert/clear one date)
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ScheduleController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly Common.ITenantContext _tenant;
    private readonly EduApi.Services.StudentPushNotifier _studentPush;

    public ScheduleController(AppDbContext db, Common.ITenantContext tenant, EduApi.Services.StudentPushNotifier studentPush)
    {
        _db = db;
        _tenant = tenant;
        _studentPush = studentPush;
    }

    // GET Schedule?schoolYear=10&year=2026&month=9
    // Only returns dates that actually have a lesson -- the calendar simply
    // shows nothing (or "no lesson") for any date not in the response.
    [HttpGet]
    public async Task<IActionResult> GetSchedule([FromQuery] int schoolYear, [FromQuery] int year, [FromQuery] int month)
    {
        if (month is < 1 or > 12) return BadRequest(new { message = "Invalid month." });

        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);

        var entries = await _db.ClassScheduleEntries.AsNoTracking()
            .Where(e => e.SchoolYear == schoolYear && e.Date >= first && e.Date <= last)
            .OrderBy(e => e.Date)
            .Select(e => new ClassScheduleEntryDto(e.Date, e.Text))
            .ToListAsync();

        return Ok(entries);
    }

    // POST Schedule/save  body: { schoolYear, date: "yyyy-MM-dd", text }
    // Upserts one date. An empty/whitespace Text deletes the row entirely
    // (that's how the teacher "clears" a day back to no-lesson from the UI).
    [HttpPost("save")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.Assistant}")]
    public async Task<IActionResult> SaveSchedule([FromBody] SaveScheduleEntryRequest request)
    {
        var teacherId = User.GetStaffTenantId();
        if (teacherId == null) return Forbid();

        var text = string.IsNullOrWhiteSpace(request.Text) ? null : request.Text.Trim();

        var existing = await _db.ClassScheduleEntries
            .FirstOrDefaultAsync(e => e.SchoolYear == request.SchoolYear && e.Date == request.Date);
        ClassScheduleEntry? saved = null;

        if (text == null)
        {
            if (existing != null) _db.ClassScheduleEntries.Remove(existing);
        }
        else if (existing != null)
        {
            existing.Text = text;
            saved = existing;
        }
        else
        {
            saved = new ClassScheduleEntry
            {
                TeacherId = teacherId.Value,
                SchoolYear = request.SchoolYear,
                Date = request.Date,
                Text = text,
            };
            _db.ClassScheduleEntries.Add(saved);
        }

        await _db.SaveChangesAsync();

        // The students are reminded the DAY BEFORE (ScheduleReminderJob, once a day at ReminderHourCairo).
        // If the teacher sets a lesson for TOMORROW after that time already passed today, send it right now.
        if (saved != null)
        {
            var cairoNow = PushTargets.ToCairo(DateTime.UtcNow);
            var tomorrow = DateOnly.FromDateTime(cairoNow.Date.AddDays(1));
            if (saved.Date == tomorrow && cairoNow.Hour >= EduApi.Services.ScheduleReminderJob.ReminderHourCairo)
                await _studentPush.ScheduleReminderAsync(saved.Id);
        }

        return Ok(new { message = "Schedule saved.", date = request.Date, text });
    }
}
