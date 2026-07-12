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
/// Route: api/Units. Mirrors Flutter calls:
///  GET    Units?schoolYear=..        (teacher)
///  GET    Units                      (student; year comes from JWT)
///  GET    Units/{id}
///  POST   Units                      (multipart, image REQUIRED)
///  POST   Units/edit                 (multipart)
///  POST   Units/delete?unitId=..
///  POST   Units/lessons              (multipart, image REQUIRED) -- add lesson
///  POST   Units/lessons/edit         (multipart)
///  POST   Units/lessons/delete?unitId=..&lessonIndex=..
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UnitsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _files;

    public UnitsController(AppDbContext db, IFileStorageService files)
    {
        _db = db;
        _files = files;
    }

    [HttpGet]
    public async Task<IActionResult> GetUnits([FromQuery] int? schoolYear)
    {
        var year = schoolYear ?? User.GetSchoolYear();
        var query = _db.Units.AsNoTracking().AsQueryable();
        if (year.HasValue) query = query.Where(u => u.SchoolYear == year.Value);

        // Students only ever see units they're actually subscribed to
        // (StudentUnitSubscription, snapshotted into the "unitIds" JWT claim at
        // login/refresh), OR a unit where they've redeemed a lecture-level
        // code for at least one lecture inside it — in that case they only
        // bought a single lesson's worth of content, not the whole unit, but
        // the unit itself still needs to show up so they can open it (see
        // GetUnit below, which trims it down to just that lesson). Teacher/
        // staff callers are unaffected (they manage the full catalog).
        if (User.IsInRole(Roles.Student))
        {
            var subscribedIds = User.GetUnitIds();
            var studentId = User.GetUserId();

            // BUGFIX: this used to be a single .Join(...) between
            // StudentLectureUnlocks and Lectures selecting a nullable
            // l.UnitId — that shape fails to translate reliably in EF Core
            // and was throwing at runtime, breaking EVERY student request to
            // this endpoint (and GetUnit below had the same pattern). Split
            // into two plain queries instead: first the unlocked lecture
            // ids, then which units those lectures belong to.
            var unlockedLectureIds = await _db.StudentLectureUnlocks.AsNoTracking()
                .Where(u => u.StudentId == studentId)
                .Select(u => u.LectureId)
                .ToListAsync();
            var unlockedUnitIds = await _db.Lectures.AsNoTracking()
                .Where(l => unlockedLectureIds.Contains(l.Id) && l.UnitId != null)
                .Select(l => l.UnitId!.Value)
                .Distinct()
                .ToListAsync();

            query = query.Where(u => subscribedIds.Contains(u.Id) || unlockedUnitIds.Contains(u.Id));
        }

        var units = await query
            .Select(u => new UnitListItem(u.Id, u.Name, u.SchoolYear, u.Month, u.ImageUrl))
            .ToListAsync();

        return Ok(units);
    }

    [HttpGet("{unitId:int}")]
    public async Task<IActionResult> GetUnit(int unitId)
    {
        // Same subscription gate as the list endpoint, plus the lecture-level
        // exception: a student who redeemed a code for one lecture inside
        // this unit (but never subscribed to the whole thing) is allowed in
        // too — see below, where the lessons list gets trimmed to match.
        HashSet<int>? unlockedLessonIndexes = null;
        if (User.IsInRole(Roles.Student) && !User.GetUnitIds().Contains(unitId))
        {
            var studentId = User.GetUserId();

            // Same fix as GetUnits above: two plain queries instead of a
            // Join across a nullable column, which was throwing at runtime.
            var unlockedLectureIds = await _db.StudentLectureUnlocks.AsNoTracking()
                .Where(u => u.StudentId == studentId)
                .Select(u => u.LectureId)
                .ToListAsync();
            unlockedLessonIndexes = (await _db.Lectures.AsNoTracking()
                    .Where(l => unlockedLectureIds.Contains(l.Id) && l.UnitId == unitId && l.LessonIndex != null)
                    .Select(l => l.LessonIndex!.Value)
                    .Distinct()
                    .ToListAsync())
                .ToHashSet();

            if (unlockedLessonIndexes.Count == 0)
                return StatusCode(403, new { message = "Not subscribed to this unit." });
        }

        var unit = await _db.Units.AsNoTracking().Include(u => u.Lessons)
            .FirstOrDefaultAsync(u => u.Id == unitId);
        if (unit == null) return NotFound(new { message = "Unit not found." });

        var lessons = unit.Lessons.OrderBy(l => l.LessonIndex).AsEnumerable();
        // Not a full subscriber — only expose the lesson(s) actually unlocked
        // by a redeemed code, not the whole unit's content.
        if (unlockedLessonIndexes != null)
            lessons = lessons.Where(l => unlockedLessonIndexes.Contains(l.LessonIndex));

        var dto = new UnitDetailDto(
            unit.Id, unit.Name, unit.SchoolYear, unit.Month, unit.ImageUrl,
            lessons.Select(l => new LessonDto(l.Id, l.LessonIndex, l.Name, l.ImageUrl)).ToList());

        return Ok(dto);
    }

    // Image is now MANDATORY when creating a Unit. Explicit [FromForm] params
    // (instead of manual Request.ReadFormAsync()) so Swagger renders the real
    // multipart form (fields + file picker) instead of an empty body.
    [HttpPost]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> CreateUnit(
        [FromForm] string name,
        [FromForm] int schoolYear,
        [FromForm] int? month,
        IFormFile image)
    {
        if (image == null || image.Length == 0)
            return BadRequest(new { message = "Image is required to create a Unit." });

        return await CreateUnitInternal(name, schoolYear, month, image);
    }

    private async Task<IActionResult> CreateUnitInternal(string name, int schoolYear, int? month, IFormFile image)
    {
        var unit = new Unit
        {
            Name = name,
            SchoolYear = schoolYear,
            Month = month,
            TeacherId = User.GetStaffTenantId()!.Value, // TENANT LAYER
            ImageUrl = await _files.SaveAsync(image, "units")
        };

        _db.Units.Add(unit);
        await _db.SaveChangesAsync();

        return StatusCode(201, new UnitListItem(unit.Id, unit.Name, unit.SchoolYear, unit.Month, unit.ImageUrl));
    }

    // Real client contract (confirmed from "edit Unit .dart"): ALWAYS multipart,
    // PascalCase fields, and a PARTIAL update — only "Name" is sent when editing
    // the name, only "Month"+"ClearMonth" when editing the month. The file field
    // is "Image" on some platforms and "image" on others (form lookups are
    // case-insensitive in ASP.NET Core, so both bind to the same parameter).
    [HttpPost("edit")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> EditUnit(
        [FromForm(Name = "Id")] int id,
        [FromForm(Name = "Name")] string? name,
        [FromForm(Name = "Month")] int? month,
        [FromForm(Name = "ClearMonth")] bool? clearMonth,
        IFormFile? image)
    {
        var unit = await _db.Units.FirstOrDefaultAsync(e => e.Id == id);
        if (unit == null) return NotFound(new { message = "Unit not found." });

        if (name != null) unit.Name = name;

        if (clearMonth == true) unit.Month = null;
        else if (month.HasValue) unit.Month = month.Value;

        if (image != null && image.Length > 0)
        {
            var oldImageUrl = unit.ImageUrl;

            // 1) Upload new file
            unit.ImageUrl = await _files.SaveAsync(image, "units");

            // 2) Update database
            await _db.SaveChangesAsync();

            // 3) Delete old file (only after the DB safely points to the new one)
            await _files.DeleteAsync(oldImageUrl);

            return Ok(new UnitListItem(unit.Id, unit.Name, unit.SchoolYear, unit.Month, unit.ImageUrl));
        }

        await _db.SaveChangesAsync();
        return Ok(new UnitListItem(unit.Id, unit.Name, unit.SchoolYear, unit.Month, unit.ImageUrl));
    }

    [HttpPost("delete")]
    public async Task<IActionResult> DeleteUnit([FromQuery] int unitId)
    {
        var unit = await _db.Units.FirstOrDefaultAsync(e => e.Id == (unitId));
        if (unit == null) return NotFound(new { message = "Unit not found." });

        _db.Units.Remove(unit);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Unit deleted." });
    }

    // Image is now MANDATORY when adding a Lesson, same as Units. Explicit
    // [FromForm] params so Swagger renders the real multipart form.
    [HttpPost("lessons")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> AddLesson(
        [FromForm(Name = "UnitId")] int unitId,
        [FromForm(Name = "Name")] string name,
        [FromForm(Name = "Index")] int? index,
        IFormFile image)
    {
        if (image == null || image.Length == 0)
            return BadRequest(new { message = "Image is required to add a Lesson." });

        var unit = await _db.Units.Include(u => u.Lessons).FirstOrDefaultAsync(u => u.Id == unitId);
        if (unit == null) return NotFound(new { message = "Unit not found." });

        int lessonIndex;
        if (index.HasValue)
        {
            if (unit.Lessons.Any(l => l.LessonIndex == index.Value))
                return Conflict(new { message = "This lesson number is already used." });
            lessonIndex = index.Value;
        }
        else
        {
            lessonIndex = unit.Lessons.Count == 0 ? 0 : unit.Lessons.Max(l => l.LessonIndex) + 1;
        }

        var imageUrl = await _files.SaveAsync(image, "lessons");
        var lesson = new Lesson { UnitId = unitId, LessonIndex = lessonIndex, Name = name, ImageUrl = imageUrl };
        _db.Lessons.Add(lesson);
        await _db.SaveChangesAsync();

        return StatusCode(201, new LessonDto(lesson.Id, lesson.LessonIndex, lesson.Name, lesson.ImageUrl));
    }

    // Real client contract (confirmed from "edit lesson .dart"): ALWAYS multipart,
    // PascalCase "UnitId"/"LessonIndex" always sent, "Name" only when editing the name.
    [HttpPost("lessons/edit")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> EditLesson(
        [FromForm(Name = "UnitId")] int unitId,
        [FromForm(Name = "LessonIndex")] int lessonIndex,
        [FromForm(Name = "Name")] string? name,
        IFormFile? image)
    {
        var lesson = await _db.Lessons.FirstOrDefaultAsync(l => l.UnitId == unitId && l.LessonIndex == lessonIndex);
        if (lesson == null) return NotFound(new { message = "Lesson not found." });

        if (name != null) lesson.Name = name;

        if (image != null && image.Length > 0)
        {
            var oldImageUrl = lesson.ImageUrl;
            lesson.ImageUrl = await _files.SaveAsync(image, "lessons");
            await _db.SaveChangesAsync();
            await _files.DeleteAsync(oldImageUrl);
            return Ok(new LessonDto(lesson.Id, lesson.LessonIndex, lesson.Name, lesson.ImageUrl));
        }

        await _db.SaveChangesAsync();
        return Ok(new LessonDto(lesson.Id, lesson.LessonIndex, lesson.Name, lesson.ImageUrl));
    }

    [HttpPost("lessons/delete")]
    public async Task<IActionResult> DeleteLesson([FromQuery] int unitId, [FromQuery] int lessonIndex)
    {
        var lesson = await _db.Lessons.FirstOrDefaultAsync(l => l.UnitId == unitId && l.LessonIndex == lessonIndex);
        if (lesson == null) return NotFound(new { message = "Lesson not found." });

        _db.Lessons.Remove(lesson);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Lesson deleted." });
    }
}
