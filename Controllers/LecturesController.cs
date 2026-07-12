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
///  POST   Lectures
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

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateLectureRequest request)
    {
        if (!Enum.TryParse<AttendanceMethod>(request.AttendanceMethod, true, out var method))
            return BadRequest(new { message = "attendanceMethod must be 'Center' or 'Online'." });

        // BUGFIX: SchoolYear used to be left unset (null) whenever UnitId was
        // omitted, so the lecture was saved fine but could never show up in
        // "Lectures/by-year" NOR in "Codes/codeables" (both filter by
        // SchoolYear — neither query ever matches a null value). Teacher JWTs
        // don't carry a "schoolYear" claim (only students do), so we can't
        // read it from the token either. When a UnitId is provided we derive
        // it from the Unit (Unit.SchoolYear is always set). Otherwise we used
        // to require the client to send "schoolYear" explicitly — but the
        // real client NEVER sends it for Center or Online lectures, so every
        // no-unit lecture kept landing with SchoolYear = null even though it
        // always carries at least one GroupId. Now we fall back to that
        // group's SchoolYear (a lecture's group always belongs to exactly one
        // school year), which is exactly how the teacher expects "the group's
        // year" to be inferred.
        int? schoolYear;
        if (request.UnitId.HasValue)
        {
            schoolYear = await _db.Units.Where(u => u.Id == request.UnitId.Value)
                .Select(u => (int?)u.SchoolYear).FirstOrDefaultAsync();
        }
        else if (request.SchoolYear.HasValue)
        {
            schoolYear = request.SchoolYear;
        }
        else
        {
            var firstGroupId = (request.GroupIds ?? new()).FirstOrDefault();
            schoolYear = firstGroupId != 0
                ? await _db.Groups.Where(g => g.Id == firstGroupId).Select(g => (int?)g.SchoolYear).FirstOrDefaultAsync()
                : null;
        }

        var lecture = new Lecture
        {
            Name = request.Name,
            AttendanceMethod = method,
            YoutubeLink = request.YoutubeLink,
            StorageFileKey = request.StorageFileKey,
            GroupIds = request.GroupIds ?? new(),
            UnitId = request.UnitId,
            LessonIndex = request.LessonIndex,
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

        // ToDto only reads Id/Name/AttendanceMethod/YoutubeLink/UnitId/LessonIndex/
        // GroupIdsCsv/CreatedAt -- project those columns at the SQL level instead
        // of materializing the full Lecture row (StorageFileKey/TeacherId/Months/
        // SchoolYear are never used here).
        var items = await query.OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.Name, l.AttendanceMethod, l.YoutubeLink, l.UnitId, l.LessonIndex, l.GroupIdsCsv, l.CreatedAt })
            .ToListAsync();
        return Ok(items.Select(l => new LectureListItem(
            l.Id, l.Name, l.AttendanceMethod.ToString(), l.YoutubeLink, l.UnitId, l.LessonIndex,
            l.GroupIdsCsv.Length == 0 ? new List<int>() : l.GroupIdsCsv.Split(',').Select(int.Parse).ToList(),
            l.CreatedAt)));
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
            .Select(l => new { l.Id, l.Name, l.AttendanceMethod, l.YoutubeLink, l.UnitId, l.LessonIndex, l.GroupIdsCsv, l.CreatedAt })
            .ToListAsync();

        return Ok(items.Select(l => new LectureListItem(
            l.Id, l.Name, l.AttendanceMethod.ToString(), l.YoutubeLink, l.UnitId, l.LessonIndex,
            l.GroupIdsCsv.Length == 0 ? new List<int>() : l.GroupIdsCsv.Split(',').Select(int.Parse).ToList(),
            l.CreatedAt)));
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

    private static LectureListItem ToDto(Lecture l) => new(
        l.Id, l.Name, l.AttendanceMethod.ToString(), l.YoutubeLink, l.UnitId, l.LessonIndex, l.GroupIds, l.CreatedAt);
}
