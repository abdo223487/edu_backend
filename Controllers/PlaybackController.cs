using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record SavePositionRequest(int PositionSeconds, int? DurationSeconds, DateTime? ClientTime);
public record SyncPositionItem(int LectureId, int PositionSeconds, int? DurationSeconds, DateTime? ClientTime);
public record SyncPositionsRequest(List<SyncPositionItem>? Items);
public record AddBookmarkRequest(int Seconds, string? Note);

/// <summary>
/// Resume position + bookmarks for EVERY lecture (YouTube or file, with or without a view limit).
///
/// Route: api/Playback  (student only)
///  GET  Playback/{lectureId}/position
///  PUT  Playback/{lectureId}/position        body { positionSeconds, durationSeconds?, clientTime? }
///  POST Playback/sync                        body { items: [...] }  positions queued while offline
///  GET  Playback/continue                    "كمّل من حيث وقفت": recent unfinished lectures
///  GET  Playback/{lectureId}/bookmarks
///  POST Playback/{lectureId}/bookmarks       body { seconds, note? }
///  POST Playback/{lectureId}/bookmarks/{id}/delete
///
/// Last-write-wins by time: a position coming from the offline queue only overwrites the
/// stored one if its clientTime is newer, so an old offline session can't rewind a newer one.
/// A lecture is "completed" the first time the playhead reaches 90% of its duration.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.Student)]
public class PlaybackController : ControllerBase
{
    private const int MaxBookmarksPerLecture = 50;

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public PlaybackController(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    private static DateTime ToUtc(DateTime? t, DateTime fallback)
    {
        if (!t.HasValue) return fallback;
        var v = t.Value;
        var utc = v.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : v.ToUniversalTime();
        // Never accept a time from the future (bad device clock).
        return utc > fallback ? fallback : utc;
    }

    // ───────────────────────── position ─────────────────────────

    [HttpGet("{lectureId:int}/position")]
    public async Task<IActionResult> GetPosition(int lectureId)
    {
        var studentId = User.GetUserId();
        var p = await _db.LecturePlaybackPositions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.StudentId == studentId && x.LectureId == lectureId);

        return Ok(new
        {
            positionSeconds = p?.PositionSeconds ?? 0,
            durationSeconds = p?.DurationSeconds,
            completed = p?.Completed ?? false,
            updatedAt = p?.UpdatedAt,
        });
    }

    [HttpPut("{lectureId:int}/position")]
    public async Task<IActionResult> SavePosition(int lectureId, [FromBody] SavePositionRequest request)
    {
        var lecture = await LectureAccess.GetAsync(_db, User, _tenant, lectureId);
        if (lecture == null) return NotFound(new { message = "Lecture not found." });

        await UpsertAsync(User.GetUserId(), lecture.TeacherId, lectureId,
            request.PositionSeconds, request.DurationSeconds, ToUtc(request.ClientTime, DateTime.UtcNow));
        return Ok(new { message = "Saved." });
    }

    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromBody] SyncPositionsRequest request)
    {
        var studentId = User.GetUserId();
        var items = (request.Items ?? new()).Take(200).ToList();
        var now = DateTime.UtcNow;
        var saved = 0;

        // Latest queued entry per lecture only.
        foreach (var item in items.GroupBy(i => i.LectureId).Select(g => g.OrderByDescending(i => i.ClientTime ?? now).First()))
        {
            var lecture = await LectureAccess.GetAsync(_db, User, _tenant, item.LectureId);
            if (lecture == null) continue;
            if (await UpsertAsync(studentId, lecture.TeacherId, item.LectureId,
                    item.PositionSeconds, item.DurationSeconds, ToUtc(item.ClientTime, now)))
                saved++;
        }
        return Ok(new { saved });
    }

    private async Task<bool> UpsertAsync(int studentId, int teacherId, int lectureId,
        int position, int? duration, DateTime at)
    {
        position = Math.Clamp(position, 0, 60 * 60 * 24);
        if (duration.HasValue) duration = Math.Clamp(duration.Value, 0, 60 * 60 * 24);

        var row = await _db.LecturePlaybackPositions
            .FirstOrDefaultAsync(x => x.StudentId == studentId && x.LectureId == lectureId);

        if (row == null)
        {
            row = new LecturePlaybackPosition { TeacherId = teacherId, StudentId = studentId, LectureId = lectureId };
            _db.LecturePlaybackPositions.Add(row);
        }
        else if (at < row.UpdatedAt)
        {
            return false; // older than what we already have
        }

        row.PositionSeconds = position;
        if (duration.HasValue && duration.Value > 0) row.DurationSeconds = duration;
        row.UpdatedAt = at;

        if (!row.Completed && row.DurationSeconds is > 0 && position >= row.DurationSeconds.Value * 0.9)
        {
            row.Completed = true;
            row.CompletedAt = at;
        }

        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            // Two saves of a brand-new row raced (unique index). The other one wins; fine.
            foreach (var e in _db.ChangeTracker.Entries<LecturePlaybackPosition>().ToList()) e.State = EntityState.Detached;
            return false;
        }
        return true;
    }

    [HttpGet("continue")]
    public async Task<IActionResult> Continue([FromQuery] int take = 5)
    {
        var studentId = User.GetUserId();
        take = Math.Clamp(take, 1, 20);

        var rows = await _db.LecturePlaybackPositions.AsNoTracking()
            .Where(p => p.StudentId == studentId && !p.Completed && p.PositionSeconds >= 15)
            .OrderByDescending(p => p.UpdatedAt)
            .Take(take * 3)
            .ToListAsync();

        var ids = rows.Select(r => r.LectureId).ToList();
        var lectures = await _db.Lectures.AsNoTracking()
            .Where(l => ids.Contains(l.Id))
            .Select(l => new { l.Id, l.Name, l.ThumbnailUrl, l.UnitId, l.LessonIndex })
            .ToDictionaryAsync(l => l.Id);

        // Lesson + unit cover images so the app can open the lesson page with its real header
        // (before this the "continue watching" card opened it with no image => grey header).
        var unitIds = lectures.Values.Where(l => l.UnitId != null).Select(l => l.UnitId!.Value).Distinct().ToList();
        var unitImages = await _db.Units.AsNoTracking()
            .Where(u => unitIds.Contains(u.Id))
            .Select(u => new { u.Id, u.ImageUrl })
            .ToDictionaryAsync(u => u.Id, u => u.ImageUrl);
        var lessonRows = await _db.Lessons.AsNoTracking()
            .Where(x => unitIds.Contains(x.UnitId))
            .Select(x => new { x.UnitId, x.LessonIndex, x.Name, x.ImageUrl })
            .ToListAsync();

        var items = new List<object>();
        foreach (var r in rows)
        {
            if (!lectures.TryGetValue(r.LectureId, out var l)) continue;
            // Re-check access so a revoked subscription doesn't leak a lecture name.
            if (await LectureAccess.GetAsync(_db, User, _tenant, r.LectureId) == null) continue;

            var lesson = (l.UnitId != null && l.LessonIndex != null)
                ? lessonRows.FirstOrDefault(x => x.UnitId == l.UnitId && x.LessonIndex == l.LessonIndex)
                : null;
            string? unitImage = null;
            if (l.UnitId != null) unitImages.TryGetValue(l.UnitId.Value, out unitImage);

            items.Add(new
            {
                lectureId = l.Id,
                name = l.Name,
                thumbnailUrl = l.ThumbnailUrl,
                unitId = l.UnitId,
                lessonIndex = l.LessonIndex,
                lessonName = lesson?.Name,
                lessonImageUrl = lesson?.ImageUrl,
                unitImageUrl = unitImage,
                positionSeconds = r.PositionSeconds,
                durationSeconds = r.DurationSeconds,
                progress = r.DurationSeconds is > 0 ? Math.Round(100.0 * r.PositionSeconds / r.DurationSeconds.Value, 0) : (double?)null,
                updatedAt = r.UpdatedAt,
            });
            if (items.Count >= take) break;
        }
        return Ok(items);
    }

    // ───────────────────────── bookmarks ─────────────────────────

    [HttpGet("{lectureId:int}/bookmarks")]
    public async Task<IActionResult> Bookmarks(int lectureId)
    {
        var studentId = User.GetUserId();
        var items = await _db.LectureBookmarks.AsNoTracking()
            .Where(b => b.StudentId == studentId && b.LectureId == lectureId)
            .OrderBy(b => b.Seconds)
            .Select(b => new { id = b.Id, seconds = b.Seconds, note = b.Note, createdAt = b.CreatedAt })
            .ToListAsync();
        return Ok(items);
    }

    [HttpPost("{lectureId:int}/bookmarks")]
    public async Task<IActionResult> AddBookmark(int lectureId, [FromBody] AddBookmarkRequest request)
    {
        var lecture = await LectureAccess.GetAsync(_db, User, _tenant, lectureId);
        if (lecture == null) return NotFound(new { message = "Lecture not found." });

        var studentId = User.GetUserId();
        var seconds = Math.Clamp(request.Seconds, 0, 60 * 60 * 24);

        var count = await _db.LectureBookmarks.CountAsync(b => b.StudentId == studentId && b.LectureId == lectureId);
        if (count >= MaxBookmarksPerLecture)
            return BadRequest(new { message = $"الحد الأقصى {MaxBookmarksPerLecture} علامة لكل محاضرة." });

        // Same second (±2s) => update the note instead of stacking duplicates.
        var near = await _db.LectureBookmarks.FirstOrDefaultAsync(b =>
            b.StudentId == studentId && b.LectureId == lectureId && b.Seconds >= seconds - 2 && b.Seconds <= seconds + 2);

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note!.Trim();
        if (note != null && note.Length > 200) note = note[..200];

        if (near != null)
        {
            if (note != null) near.Note = note;
            await _db.SaveChangesAsync();
            return Ok(new { id = near.Id, seconds = near.Seconds, note = near.Note, createdAt = near.CreatedAt });
        }

        var b = new LectureBookmark
        {
            TeacherId = lecture.TeacherId, StudentId = studentId, LectureId = lectureId,
            Seconds = seconds, Note = note,
        };
        _db.LectureBookmarks.Add(b);
        await _db.SaveChangesAsync();
        return StatusCode(201, new { id = b.Id, seconds = b.Seconds, note = b.Note, createdAt = b.CreatedAt });
    }

    [HttpPost("{lectureId:int}/bookmarks/{id:int}/delete")]
    public async Task<IActionResult> DeleteBookmark(int lectureId, int id)
    {
        var studentId = User.GetUserId();
        var b = await _db.LectureBookmarks
            .FirstOrDefaultAsync(x => x.Id == id && x.LectureId == lectureId && x.StudentId == studentId);
        if (b == null) return NotFound(new { message = "Bookmark not found." });
        _db.LectureBookmarks.Remove(b);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Deleted." });
    }
}
