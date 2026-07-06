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

    public LecturesController(AppDbContext db, IFileStorageService files)
    {
        _db = db;
        _files = files;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateLectureRequest request)
    {
        if (!Enum.TryParse<AttendanceMethod>(request.AttendanceMethod, true, out var method))
            return BadRequest(new { message = "attendanceMethod must be 'Center' or 'Online'." });

        var lecture = new Lecture
        {
            Name = request.Name,
            AttendanceMethod = method,
            YoutubeLink = request.YoutubeLink,
            StorageFileKey = request.StorageFileKey,
            GroupIds = request.GroupIds ?? new(),
            UnitId = request.UnitId,
            LessonIndex = request.LessonIndex,
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

        var query = _db.Lectures.Where(l => l.SchoolYear == schoolYear && l.AttendanceMethod == method);
        if (unitId.HasValue) query = query.Where(l => l.UnitId == unitId.Value);

        // Defensive: this action has no [Authorize(Roles=...)] restricting it to
        // staff, so apply the same subscription gate in case a student token
        // calls it directly.
        if (User.IsInRole(Roles.Student))
        {
            var subscribedIds = User.GetUnitIds();
            query = query.Where(l => l.UnitId == null || subscribedIds.Contains(l.UnitId.Value));
        }

        var items = await query.OrderBy(l => l.Id).ToListAsync();
        return Ok(items.Select(ToDto));
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
        var gId = groupId ?? User.GetGroupId();
        var query = _db.Lectures.AsQueryable();

        if (gId.HasValue) query = query.Where(l => l.GroupIdsCsv.Contains(gId.Value.ToString()));
        if (!string.IsNullOrEmpty(attendanceMethod) &&
            Enum.TryParse<AttendanceMethod>(attendanceMethod, true, out var method))
            query = query.Where(l => l.AttendanceMethod == method);

        if (noUnitOnly) query = query.Where(l => l.UnitId == null);
        else if (unitId.HasValue) query = query.Where(l => l.UnitId == unitId.Value);

        // Same subscription gate as Units/Notifications: a lecture tied to a
        // specific unit is only visible to a student subscribed to that unit.
        // Lectures with no UnitId (general center/online sessions) pass through
        // untouched — they were never unit-scoped.
        if (User.IsInRole(Roles.Student))
        {
            var subscribedIds = User.GetUnitIds();
            query = query.Where(l => l.UnitId == null || subscribedIds.Contains(l.UnitId.Value));
        }

        if (lessonIndex.HasValue) query = query.Where(l => l.LessonIndex == lessonIndex.Value);

        var items = await query
            .OrderBy(l => l.Id)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .ToListAsync();

        return Ok(items.Select(ToDto));
    }

    [HttpGet("{lectureId:int}/materials")]
    public async Task<IActionResult> GetMaterials(int lectureId)
    {
        // Same subscription gate: if this lecture belongs to a unit, a student
        // must be subscribed to that unit to see its materials, even though
        // they already knew the lectureId.
        if (User.IsInRole(Roles.Student))
        {
            var lecture = await _db.Lectures.FirstOrDefaultAsync(l => l.Id == lectureId);
            if (lecture == null) return NotFound(new { message = "Lecture not found." });
            if (lecture.UnitId.HasValue && !User.GetUnitIds().Contains(lecture.UnitId.Value))
                return StatusCode(403, new { message = "Not subscribed to this unit." });
        }

        var materials = await _db.Materials.Where(m => m.LectureId == lectureId)
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
