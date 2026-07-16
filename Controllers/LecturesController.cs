using EduApi.Common;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/Lectures. Mirrors:
///  POST   Lectures                                (multipart/form-data — video file OR YoutubeLink)
///  PATCH  Lectures/{id}
///  POST   Lectures/delete?lectureId=..
///  GET    Lectures/by-group?groupId=..&attendanceMethod=..&unitId=..&lessonIndex=..&noUnitOnly=..&p=..
///  GET    Lectures/{id}/materials
///  POST   Lectures/{id}/materials/file       (multipart, field name "Files")
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class LecturesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _files;

    private readonly Common.ITenantContext _tenant;

    public LecturesController(AppDbContext db, IFileStorageService files, Common.ITenantContext tenant)
    {
        _db = db;
        _files = files;
        _tenant = tenant;
    }

    // Multipart create — supports an actual recorded-video upload (stored in
    // Cloudflare R2, same as quiz-question images and lecture materials) in
    // addition to a plain YouTube link.
    // fields: Name, AttendanceMethod, GroupIds[i], UnitId?, LessonIndex?,
    //         SchoolYear?, YoutubeLink? (text)
    // file:   VideoFile (optional, .mp4)
    [HttpPost]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Create()
    {
        var form = await Request.ReadFormAsync();

        var name = form["Name"].ToString();
        var attendanceMethodRaw = form["AttendanceMethod"].ToString();
        if (!Enum.TryParse<AttendanceMethod>(attendanceMethodRaw, true, out var method))
            return BadRequest(new { message = "attendanceMethod must be 'Center' or 'Online'." });

        var youtubeLink = form["YoutubeLink"].ToString();
        if (string.IsNullOrWhiteSpace(youtubeLink)) youtubeLink = null;

        var videoFile = form.Files["VideoFile"];

        // A recorded upload and a YouTube link both describing "the video for
        // this lecture" don't make sense together — pick exactly one so the
        // client never has to guess which one is actually meant to play.
        if (videoFile != null && youtubeLink != null)
            return BadRequest(new { message = "Provide either a video file or a YouTube link, not both." });

        string? storageFileUrl = null;
        if (videoFile != null)
        {
            var ext = Path.GetExtension(videoFile.FileName);
            if (!string.Equals(ext, ".mp4", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Only .mp4 video files are supported." });

            storageFileUrl = await _files.SaveAsync(videoFile, "lectures");
        }

        var groupIds = new List<int>();
        for (var i = 0; form.ContainsKey($"GroupIds[{i}]"); i++)
            groupIds.Add(int.Parse(form[$"GroupIds[{i}]"].ToString()));

        int? unitId = int.TryParse(form["UnitId"].ToString(), out var uid) ? uid : null;
        int? lessonIndex = int.TryParse(form["LessonIndex"].ToString(), out var li) ? li : null;
        int? requestSchoolYear = int.TryParse(form["SchoolYear"].ToString(), out var sy) ? sy : null;

        // Same SchoolYear-derivation rule as before: prefer the Unit's year,
        // fall back to an explicitly-sent SchoolYear, then to the first
        // group's year — a teacher JWT never carries a "schoolYear" claim.
        int? schoolYear;
        if (unitId.HasValue)
        {
            schoolYear = await _db.Units.Where(u => u.Id == unitId.Value)
                .Select(u => (int?)u.SchoolYear).FirstOrDefaultAsync();
        }
        else if (requestSchoolYear.HasValue)
        {
            schoolYear = requestSchoolYear;
        }
        else
        {
            var firstGroupId = groupIds.FirstOrDefault();
            schoolYear = firstGroupId != 0
                ? await _db.Groups.Where(g => g.Id == firstGroupId).Select(g => (int?)g.SchoolYear).FirstOrDefaultAsync()
                : null;
        }

        var lecture = new Lecture
        {
            Name = name,
            AttendanceMethod = method,
            YoutubeLink = youtubeLink,
            StorageFileKey = storageFileUrl,
            GroupIds = groupIds,
            UnitId = unitId,
            LessonIndex = lessonIndex,
            SchoolYear = schoolYear,
            TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
        };

        _db.Lectures.Add(lecture);
        await _db.SaveChangesAsync();

        return StatusCode(201, ToDto(lecture));
    }

    [HttpPatch("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateLectureRequest request)
    {
        var lecture = await _db.Lectures.FirstOrDefaultAsync(e => e.Id == (id));
        if (lecture == null) return NotFound(new { message = "Lecture not found." });

        if (request.Name != null) lecture.Name = request.Name;
        if (request.AttendanceMethod != null &&
            Enum.TryParse<AttendanceMethod>(request.AttendanceMethod, true, out var method))
            lecture.AttendanceMethod = method;
        if (request.SchoolYear.HasValue) lecture.SchoolYear = request.SchoolYear.Value;

        await _db.SaveChangesAsync();
        return Ok(ToDto(lecture));
    }

    [HttpPost("delete")]
    public async Task<IActionResult> Delete([FromQuery] int lectureId)
    {
        var lecture = await _db.Lectures.FirstOrDefaultAsync(e => e.Id == (lectureId));
        if (lecture == null) return NotFound(new { message = "Lecture not found." });

        // Best-effort: remove the recorded video from R2 too, so deleting a
        // lecture doesn't leave an orphaned file sitting in the bucket
        // forever. Does nothing if the lecture only had a YoutubeLink.
        await _files.DeleteAsync(lecture.StorageFileKey);

        _db.Lectures.Remove(lecture);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Lecture deleted." });
    }

    // GET Lectures/by-year?schoolYear=..&attendanceMethod=Center[&unitId=..]
    // No pagination — used by the teacher's center-lecture screen for a whole year.
    [HttpGet("by-year")]
    public async Task<IActionResult> ByYear(
        [FromQuery] int schoolYear,
        [FromQuery] string attendanceMethod,
        [FromQuery] int? unitId = null)
    {
        if (!Enum.TryParse<AttendanceMethod>(attendanceMethod, true, out var method))
            return BadRequest(new { message = "attendanceMethod must be 'Center' or 'Online'." });

        var query = _db.Lectures.AsNoTracking().Where(l => l.SchoolYear == schoolYear && l.AttendanceMethod == method);
        if (unitId.HasValue) query = query.Where(l => l.UnitId == unitId.Value);

        // Defensive: this action has no [Authorize(Roles=...)] restricting it to
        // staff, so apply the same subscription/unlock gate as ByGroup in case
        // a student token calls it directly.
        if (User.IsInRole(Roles.Student))
        {
            var studentId = User.GetUserId();
            var subscribedIds = User.GetUnitIds();
            var unlockedLectureIds = await _db.StudentLectureUnlocks.AsNoTracking()
                .Where(u => u.StudentId == studentId)
                .Select(u => u.LectureId)
                .ToListAsync();

            // A lecture inside a Unit is visible either because the student is
            // subscribed to the whole Unit, OR because they redeemed a code
            // that unlocked that ONE lecture specifically (see RedeemCode) —
            // previously only a full-unit subscription counted here, so a
            // lecture-level code for an in-unit lecture was granted but never
            // actually visible to the student.
            query = query.Where(l =>
                (l.UnitId != null && (subscribedIds.Contains(l.UnitId.Value) || unlockedLectureIds.Contains(l.Id))) ||
                (l.UnitId == null && unlockedLectureIds.Contains(l.Id)));
        }

        // ToDto only reads Id/Name/AttendanceMethod/YoutubeLink/StorageFileKey/
        // UnitId/LessonIndex/GroupIdsCsv/CreatedAt -- project those columns at
        // the SQL level instead of materializing the full Lecture row
        // (TeacherId/Months/SchoolYear are never used here).
        var items = await query.OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.Name, l.AttendanceMethod, l.YoutubeLink, l.StorageFileKey, l.UnitId, l.LessonIndex, l.GroupIdsCsv, l.CreatedAt })
            .ToListAsync();
        return Ok(items.Select(l =>
        {
            var (link, sourceType) = PlaybackInfo(l.StorageFileKey, l.YoutubeLink);
            return new LectureListItem(
                l.Id, l.Name, l.AttendanceMethod.ToString(), link, sourceType, l.UnitId, l.LessonIndex,
                l.GroupIdsCsv.Length == 0 ? new List<int>() : l.GroupIdsCsv.Split(',').Select(int.Parse).ToList(),
                l.CreatedAt);
        }));
    }

    [HttpGet("by-group")]
    public async Task<IActionResult> ByGroup(
        [FromQuery] int? groupId,
        [FromQuery] string? attendanceMethod,
        [FromQuery] int? unitId,
        [FromQuery] int? lessonIndex,
        [FromQuery] bool noUnitOnly = false,
        [FromQuery] int p = 1)
    {
        var gId = groupId ?? User.GetGroupId(_tenant.CurrentTenantId);
        var query = _db.Lectures.AsNoTracking().AsQueryable();

        if (gId.HasValue) query = query.Where(l => _db.LectureGroupLinks.Any(x => x.LectureId == l.Id && x.GroupId == gId.Value));
        if (!string.IsNullOrEmpty(attendanceMethod) &&
            Enum.TryParse<AttendanceMethod>(attendanceMethod, true, out var method))
            query = query.Where(l => l.AttendanceMethod == method);

        if (noUnitOnly) query = query.Where(l => l.UnitId == null);
        else if (unitId.HasValue) query = query.Where(l => l.UnitId == unitId.Value);

        // Same subscription gate as Units/Notifications: a lecture tied to a
        // specific unit is only visible to a student subscribed to that unit.
        //
        // BUGFIX: lectures with no UnitId (standalone/no-unit online lectures
        // meant to be unlocked by a plain code, e.g. via Codes/codeables ->
        // Codes POST with lectureIds) used to pass through completely
        // ungated — every student in the group could see them with no code
        // required at all. Now a standalone lecture only shows up once the
        // student has actually redeemed a code that unlocks it (see
        // StudentsController.RedeemCode, which now writes to
        // StudentLectureUnlocks). We query the DB directly (not a JWT claim)
        // so a lecture appears immediately after redeeming, without needing
        // to refresh/re-login first.
        if (User.IsInRole(Roles.Student))
        {
            var studentId = User.GetUserId();
            var subscribedIds = User.GetUnitIds();
            var unlockedLectureIds = await _db.StudentLectureUnlocks.AsNoTracking()
                .Where(u => u.StudentId == studentId)
                .Select(u => u.LectureId)
                .ToListAsync();

            // See identical note in ByYear: a lecture-level code unlock must
            // grant visibility to that lecture even when it belongs to a Unit
            // the student never subscribed to as a whole.
            query = query.Where(l =>
                (l.UnitId != null && (subscribedIds.Contains(l.UnitId.Value) || unlockedLectureIds.Contains(l.Id))) ||
                (l.UnitId == null && unlockedLectureIds.Contains(l.Id)));
        }

        if (lessonIndex.HasValue) query = query.Where(l => l.LessonIndex == lessonIndex.Value);

        var items = await query
            .OrderBy(l => l.Id)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .Select(l => new { l.Id, l.Name, l.AttendanceMethod, l.YoutubeLink, l.StorageFileKey, l.UnitId, l.LessonIndex, l.GroupIdsCsv, l.CreatedAt })
            .ToListAsync();

        return Ok(items.Select(l =>
        {
            var (link, sourceType) = PlaybackInfo(l.StorageFileKey, l.YoutubeLink);
            return new LectureListItem(
                l.Id, l.Name, l.AttendanceMethod.ToString(), link, sourceType, l.UnitId, l.LessonIndex,
                l.GroupIdsCsv.Length == 0 ? new List<int>() : l.GroupIdsCsv.Split(',').Select(int.Parse).ToList(),
                l.CreatedAt);
        }));
    }

    [HttpGet("{lectureId:int}/materials")]
    public async Task<IActionResult> GetMaterials(int lectureId)
    {
        // Same subscription gate: if this lecture belongs to a unit, a student
        // must be subscribed to that unit to see its materials, even though
        // they already knew the lectureId.
        if (User.IsInRole(Roles.Student))
        {
            var lecture = await _db.Lectures.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lectureId);
            if (lecture == null) return NotFound(new { message = "Lecture not found." });
            if (lecture.UnitId.HasValue && !User.GetUnitIds().Contains(lecture.UnitId.Value))
            {
                // Same lecture-level-unlock exception as ByGroup/ByYear: a
                // code redeemed for this one in-unit lecture should still let
                // the student open its materials without a full subscription.
                var studentId = User.GetUserId();
                var unlocked = await _db.StudentLectureUnlocks.AsNoTracking()
                    .AnyAsync(u => u.StudentId == studentId && u.LectureId == lectureId);
                if (!unlocked)
                    return StatusCode(403, new { message = "Not subscribed to this unit." });
            }
        }

        var materials = await _db.Materials.AsNoTracking().Where(m => m.LectureId == lectureId)
            .Select(m => new MaterialListItem(m.Id, m.Name, m.Type, m.Link))
            .ToListAsync();

        return Ok(materials);
    }

    [HttpPost("{lectureId:int}/materials/file")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadMaterials(
        int lectureId,
        List<IFormFile> files,
        [FromQuery] int? Months,
        [FromQuery] int? SchoolYear)
    {
        var lecture = await _db.Lectures.FirstOrDefaultAsync(e => e.Id == (lectureId));
        if (lecture == null) return NotFound(new { message = "Lecture not found." });

        var created = new List<MaterialListItem>();
        foreach (var file in files)
        {
            var url = await _files.SaveAsync(file, "materials");
            var material = new Material
            {
                Name = file.FileName,
                Type = "File",
                Link = url,
                LectureId = lectureId,
                UnitId = lecture.UnitId,
                Months = Months,
                SchoolYear = SchoolYear ?? lecture.SchoolYear,
                TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
            };
            _db.Materials.Add(material);
            created.Add(new MaterialListItem(material.Id, material.Name, material.Type, material.Link));
        }

        await _db.SaveChangesAsync();
        return Ok(created);
    }

    private static LectureListItem ToDto(Lecture l)
    {
        var (link, sourceType) = PlaybackInfo(l.StorageFileKey, l.YoutubeLink);
        return new(l.Id, l.Name, l.AttendanceMethod.ToString(), link, sourceType, l.UnitId, l.LessonIndex, l.GroupIds, l.CreatedAt);
    }

    /// <summary>
    /// A lecture has at most one actual video source: an uploaded file stored
    /// in R2 (StorageFileKey holds its public URL) or a YoutubeLink — never
    /// both (enforced in Create). This picks whichever is set and tells the
    /// client which kind it is, so it knows whether to open a YouTube player
    /// or its own video player pointed at the R2 URL.
    /// </summary>
    private static (string? Link, string? SourceType) PlaybackInfo(string? storageFileUrl, string? youtubeLink)
    {
        if (!string.IsNullOrWhiteSpace(storageFileUrl)) return (storageFileUrl, "File");
        if (!string.IsNullOrWhiteSpace(youtubeLink)) return (youtubeLink, "Youtube");
        return (null, null);
    }
}
