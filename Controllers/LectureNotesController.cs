using System.Text;
using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record SaveLectureNoteRequest(int Seconds, string? Body, bool? IsStarred);
public record EditLectureNoteRequest(string? Body, int? Seconds, bool? IsStarred);

/// <summary>
/// "ملاحظات الفيديو": the student's own timestamped notes on a lecture (private, never shown to
/// the teacher). Each note is pinned to a second of the video, so tapping it jumps there.
/// Bookmarks (Playback/.../bookmarks) stay as quick flags; notes are the longer free text.
///
/// Route: api/LectureNotes  (student only)
///  GET  LectureNotes/{lectureId}                    my notes of one lecture, in video order
///  POST LectureNotes/{lectureId}                    body { seconds, body, isStarred? }
///  POST LectureNotes/note/{id}/edit                 body { body?, seconds?, isStarred? }
///  POST LectureNotes/note/{id}/delete
///  GET  LectureNotes/all?unitId=&amp;q=&amp;starred=&amp;p=     my notes across ALL lectures, newest first (searchable)
///  GET  LectureNotes/{lectureId}/summary            the lecture's notes + bookmarks as one ready-to-share text,
///                                                   in video order ("ملخصي"), no AI involved
///
/// Limits: 200 notes per lecture, 1000 characters per note.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.Student)]
public class LectureNotesController : ControllerBase
{
    private const int MaxNotesPerLecture = 200;
    private const int MaxBodyLength = 1000;

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public LectureNotesController(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    private int StudentId => User.GetUserId();

    private static string Label(int seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static object ToDto(LectureNote n) => new
    {
        id = n.Id,
        lectureId = n.LectureId,
        seconds = n.Seconds,
        timeLabel = Label(n.Seconds),
        body = n.Body,
        isStarred = n.IsStarred,
        createdAt = n.CreatedAt,
        updatedAt = n.UpdatedAt,
    };

    [HttpGet("{lectureId:int}")]
    public async Task<IActionResult> ForLecture(int lectureId)
    {
        if (await LectureAccess.GetAsync(_db, User, _tenant, lectureId) == null)
            return NotFound(new { message = "Lecture not found." });

        var studentId = StudentId;
        var notes = await _db.LectureNotes.AsNoTracking()
            .Where(n => n.StudentId == studentId && n.LectureId == lectureId)
            .OrderBy(n => n.Seconds).ThenBy(n => n.Id)
            .ToListAsync();
        return Ok(notes.Select(ToDto));
    }

    [HttpPost("{lectureId:int}")]
    public async Task<IActionResult> Create(int lectureId, [FromBody] SaveLectureNoteRequest request)
    {
        var body = (request.Body ?? string.Empty).Trim();
        if (body.Length == 0) return BadRequest(new { message = "Note text is required." });
        if (body.Length > MaxBodyLength) return BadRequest(new { message = $"Note is too long (max {MaxBodyLength} characters)." });

        var lecture = await LectureAccess.GetAsync(_db, User, _tenant, lectureId);
        if (lecture == null) return NotFound(new { message = "Lecture not found." });

        var studentId = StudentId;
        var count = await _db.LectureNotes.CountAsync(n => n.StudentId == studentId && n.LectureId == lectureId);
        if (count >= MaxNotesPerLecture)
            return BadRequest(new { message = $"You reached the maximum of {MaxNotesPerLecture} notes for this lecture." });

        var now = DateTime.UtcNow;
        var note = new LectureNote
        {
            TeacherId = lecture.TeacherId,
            StudentId = studentId,
            LectureId = lectureId,
            Seconds = Math.Clamp(request.Seconds, 0, 60 * 60 * 24),
            Body = body,
            IsStarred = request.IsStarred ?? false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.LectureNotes.Add(note);
        await _db.SaveChangesAsync();
        return Ok(ToDto(note));
    }

    [HttpPost("note/{id:int}/edit")]
    public async Task<IActionResult> Edit(int id, [FromBody] EditLectureNoteRequest request)
    {
        var studentId = StudentId;
        var note = await _db.LectureNotes.FirstOrDefaultAsync(n => n.Id == id && n.StudentId == studentId);
        if (note == null) return NotFound(new { message = "Note not found." });

        if (request.Body != null)
        {
            var body = request.Body.Trim();
            if (body.Length == 0) return BadRequest(new { message = "Note text can't be empty." });
            if (body.Length > MaxBodyLength) return BadRequest(new { message = $"Note is too long (max {MaxBodyLength} characters)." });
            note.Body = body;
        }
        if (request.Seconds.HasValue) note.Seconds = Math.Clamp(request.Seconds.Value, 0, 60 * 60 * 24);
        if (request.IsStarred.HasValue) note.IsStarred = request.IsStarred.Value;
        note.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(note));
    }

    [HttpPost("note/{id:int}/delete")]
    public async Task<IActionResult> Delete(int id)
    {
        var studentId = StudentId;
        var note = await _db.LectureNotes.FirstOrDefaultAsync(n => n.Id == id && n.StudentId == studentId);
        if (note == null) return NotFound(new { message = "Note not found." });
        _db.LectureNotes.Remove(note);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Deleted." });
    }

    [HttpGet("all")]
    public async Task<IActionResult> All([FromQuery] int? unitId, [FromQuery] string? q,
        [FromQuery] bool? starred, [FromQuery] int p = 1)
    {
        p = Math.Max(1, p);
        var studentId = StudentId;

        var query = _db.LectureNotes.AsNoTracking().Where(n => n.StudentId == studentId);
        if (starred == true) query = query.Where(n => n.IsStarred);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            var lowered = term.ToLower();
            query = query.Where(n => n.Body.ToLower().Contains(lowered));
        }
        if (unitId.HasValue)
            query = query.Where(n => _db.Lectures.Any(l => l.Id == n.LectureId && l.UnitId == unitId.Value));

        var notes = await query
            .OrderByDescending(n => n.UpdatedAt)
            .Skip((p - 1) * PagingDefaults.PageSize).Take(PagingDefaults.PageSize)
            .ToListAsync();

        var lectureIds = notes.Select(n => n.LectureId).Distinct().ToList();
        var lectures = await _db.Lectures.AsNoTracking()
            .Where(l => lectureIds.Contains(l.Id))
            .Select(l => new { l.Id, l.Name, l.UnitId })
            .ToDictionaryAsync(l => l.Id);

        return Ok(notes.Select(n => new
        {
            id = n.Id,
            lectureId = n.LectureId,
            lectureName = lectures.TryGetValue(n.LectureId, out var l) ? l.Name : "",
            unitId = lectures.TryGetValue(n.LectureId, out var l2) ? l2.UnitId : null,
            seconds = n.Seconds,
            timeLabel = Label(n.Seconds),
            body = n.Body,
            isStarred = n.IsStarred,
            updatedAt = n.UpdatedAt,
        }));
    }

    [HttpGet("{lectureId:int}/summary")]
    public async Task<IActionResult> Summary(int lectureId)
    {
        var lecture = await LectureAccess.GetAsync(_db, User, _tenant, lectureId);
        if (lecture == null) return NotFound(new { message = "Lecture not found." });

        var studentId = StudentId;
        var notes = await _db.LectureNotes.AsNoTracking()
            .Where(n => n.StudentId == studentId && n.LectureId == lectureId)
            .ToListAsync();
        var bookmarks = await _db.LectureBookmarks.AsNoTracking()
            .Where(b => b.StudentId == studentId && b.LectureId == lectureId)
            .ToListAsync();

        var items = notes.Select(n => new { n.Seconds, Text = n.Body, n.IsStarred, IsBookmark = false })
            .Concat(bookmarks.Select(b => new { b.Seconds, Text = string.IsNullOrWhiteSpace(b.Note) ? "علامة" : b.Note!, IsStarred = false, IsBookmark = true }))
            .OrderBy(i => i.Seconds)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"ملخصي: {lecture.Name}");
        sb.AppendLine();
        foreach (var i in items)
        {
            var mark = i.IsBookmark ? "🔖" : i.IsStarred ? "⭐" : "•";
            sb.AppendLine($"{mark} [{Label(i.Seconds)}] {i.Text}");
        }
        if (items.Count == 0) sb.AppendLine("لسه ما كتبتش ملاحظات على المحاضرة دي.");

        return Ok(new
        {
            lectureId,
            lectureName = lecture.Name,
            count = items.Count,
            text = sb.ToString().TrimEnd(),
            items = items.Select(i => new
            {
                seconds = i.Seconds,
                timeLabel = Label(i.Seconds),
                text = i.Text,
                isStarred = i.IsStarred,
                isBookmark = i.IsBookmark,
            }),
        });
    }
}
