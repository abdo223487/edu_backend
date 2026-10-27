using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record CheckGrantsRequest(List<OfflineItemRef>? Items);
public record OfflineItemRef(string ItemType, int ItemId);

/// <summary>
/// Offline downloads = a time-boxed license + the file URL.
///
/// Route: api/Offline  (student only)
///  POST Offline/lectures/{id}/grant      -> { url, title, expiresAt }   uploaded-video lectures only
///  POST Offline/materials/{id}/grant     -> { url, title, type, expiresAt }   PDFs / files attached to a lecture or unit
///  GET  Offline/grants                    -> every grant that is still valid
///  POST Offline/check                     body { items:[{itemType,itemId}] } -> which of them are still valid
///
/// RULES
///  - A lecture with a ViewLimit can NOT be downloaded (offline playback can't be counted, it would defeat the limit).
///  - YouTube lectures can't be downloaded.
///  - Link-locked lectures (RequireLinkExam/RequireLinkAssignment) are re-checked by the client before the
///    grant is requested (the same `locked` flag the lecture list already exposes).
///  - Paid notebooks (Material.NotebookId != null) can not be downloaded.
///  - Licence = 14 days. The app deletes expired items, and calls GET Offline/grants whenever it is online
///    to drop anything the server no longer recognises (e.g. subscription ended).
///  Files are encrypted at rest on the device by the app (see lib/services/offline_service.dart).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.Student)]
public class OfflineController : ControllerBase
{
    private static readonly TimeSpan LicenseDuration = TimeSpan.FromDays(14);

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public OfflineController(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    [HttpPost("lectures/{id:int}/grant")]
    public async Task<IActionResult> GrantLecture(int id)
    {
        var lecture = await LectureAccess.GetAsync(_db, User, _tenant, id);
        if (lecture == null) return NotFound(new { message = "Lecture not found." });

        if (lecture.ViewLimit.HasValue)
            return BadRequest(new { code = "view_limited", message = "المحاضرة دي لها عدد مشاهدات محدد، فمينفعش تتحمّل أوفلاين." });
        if (LectureAccess.SourceType(lecture) != "File")
            return BadRequest(new { code = "not_file", message = "المحاضرات اللي على يوتيوب مينفعش تتحمّل." });

        var grant = await UpsertGrantAsync("Lecture", id, lecture.TeacherId);
        return Ok(new { url = lecture.StorageFileKey, title = lecture.Name, expiresAt = grant.ExpiresAt });
    }

    [HttpPost("materials/{id:int}/grant")]
    public async Task<IActionResult> GrantMaterial(int id)
    {
        var m = await _db.Materials.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Id, x.Name, x.Type, x.Link, x.LectureId, x.UnitId, x.NotebookId, x.TeacherId })
            .FirstOrDefaultAsync();
        if (m == null) return NotFound(new { message = "Material not found." });
        if (m.NotebookId.HasValue)
            return BadRequest(new { code = "paid", message = "الملازم المدفوعة مينفعش تتحمّل." });

        if (m.LectureId.HasValue)
        {
            if (await LectureAccess.GetAsync(_db, User, _tenant, m.LectureId.Value) == null)
                return StatusCode(403, new { message = "Not allowed." });
        }
        else if (m.UnitId.HasValue)
        {
            var units = await StudentAccessHelpers.GetEffectiveUnitIdsAsync(_db, User, User.GetUserId());
            if (!units.Contains(m.UnitId.Value)) return StatusCode(403, new { message = "Not subscribed to this unit." });
        }
        else
        {
            return StatusCode(403, new { message = "Not allowed." });
        }

        if (string.IsNullOrWhiteSpace(m.Link) || !m.Link.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || m.Link.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || m.Link.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { code = "not_file", message = "المرفق ده مش ملف قابل للتحميل." });

        var grant = await UpsertGrantAsync("Material", id, m.TeacherId);
        return Ok(new { url = m.Link, title = m.Name, type = m.Type, expiresAt = grant.ExpiresAt });
    }

    private async Task<OfflineGrant> UpsertGrantAsync(string type, int itemId, int teacherId)
    {
        var studentId = User.GetUserId();
        var now = DateTime.UtcNow;
        var g = await _db.OfflineGrants.FirstOrDefaultAsync(x => x.StudentId == studentId && x.ItemType == type && x.ItemId == itemId);
        if (g == null)
        {
            g = new OfflineGrant { TeacherId = teacherId, StudentId = studentId, ItemType = type, ItemId = itemId };
            _db.OfflineGrants.Add(g);
        }
        g.GrantedAt = now;
        g.ExpiresAt = now.Add(LicenseDuration);
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            foreach (var e in _db.ChangeTracker.Entries<OfflineGrant>().Where(e => e.State == EntityState.Added).ToList())
                e.State = EntityState.Detached;
            g = await _db.OfflineGrants.FirstAsync(x => x.StudentId == studentId && x.ItemType == type && x.ItemId == itemId);
        }
        return g;
    }

    [HttpGet("grants")]
    public async Task<IActionResult> Grants()
    {
        var studentId = User.GetUserId();
        var now = DateTime.UtcNow;
        var rows = await _db.OfflineGrants.AsNoTracking()
            .Where(g => g.StudentId == studentId && g.ExpiresAt > now)
            .Select(g => new { itemType = g.ItemType, itemId = g.ItemId, expiresAt = g.ExpiresAt })
            .ToListAsync();
        return Ok(rows);
    }

    [HttpPost("check")]
    public async Task<IActionResult> Check([FromBody] CheckGrantsRequest request)
    {
        var studentId = User.GetUserId();
        var now = DateTime.UtcNow;
        var valid = await _db.OfflineGrants.AsNoTracking()
            .Where(g => g.StudentId == studentId && g.ExpiresAt > now)
            .Select(g => new { g.ItemType, g.ItemId, g.ExpiresAt })
            .ToListAsync();

        var items = (request.Items ?? new()).Take(500).Select(i =>
        {
            var hit = valid.FirstOrDefault(v => v.ItemType == i.ItemType && v.ItemId == i.ItemId);
            return new { itemType = i.ItemType, itemId = i.ItemId, valid = hit != null, expiresAt = hit?.ExpiresAt };
        });
        return Ok(items);
    }
}
