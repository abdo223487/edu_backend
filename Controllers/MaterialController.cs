using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record CreateGoogleDriveMaterialRequest(string? Name, string Link, int? SchoolYear, int? UnitId, int? Months);

/// <summary>
/// Route: api/Material (singular, matching Flutter's "Material" endpoint - NOT renamed to "Materials").
///  GET    Material?schoolYear=..&unitId=..     (teacher) -> each item includes "unitName"
///  GET    Material                             (student, unfiltered)
///  GET    Material/{id}
///  POST   Material/google-drive
///  POST   Material/file                        (multipart, field name "Files", supports multiple)
///  POST   Material/delete?materialId=..
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MaterialController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _files;

    public MaterialController(AppDbContext db, IFileStorageService files)
    {
        _db = db;
        _files = files;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? schoolYear, [FromQuery] int? unitId)
    {
        // Tenant isolation (which teacher this material belongs to) is already
        // enforced automatically by AppDbContext's global query filter on
        // Material.TeacherId == ITenantContext.CurrentTenantId — no extra code
        // needed here for that part.
        //
        // Materials don't carry a GroupId (they're scoped by Unit/SchoolYear,
        // not by group), so there's no "group" filter to add. What WAS missing:
        // a student who sends no schoolYear (the normal case, per the docs)
        // could see materials from every school year within the same tenant.
        // Default to the caller's own school year from the JWT, same pattern
        // already used in UnitsController.
        var year = schoolYear ?? (User.IsInRole(Roles.Student) ? User.GetSchoolYear() : null);

        var query = _db.Materials.AsNoTracking().AsQueryable();
        if (year.HasValue) query = query.Where(m => m.SchoolYear == year.Value);
        if (unitId.HasValue) query = query.Where(m => m.UnitId == unitId.Value);

        // Same subscription gate as Units/Lectures/Notifications: a material
        // tied to a specific unit is only visible to a student subscribed to
        // that unit. Materials with no UnitId pass through untouched.
        if (User.IsInRole(Roles.Student))
        {
            var subscribedIds = User.GetUnitIds();
            query = query.Where(m => m.UnitId == null || subscribedIds.Contains(m.UnitId.Value));
        }

        var materials = await query.ToListAsync();

        // Hydrate unitName (Material.UnitId is a plain int?, no navigation
        // property) so Drive/PDF list screens can show the real unit name
        // instead of a hardcoded "Unit" placeholder.
        var unitIds = materials.Where(m => m.UnitId.HasValue).Select(m => m.UnitId!.Value).Distinct().ToList();
        var unitNames = await _db.Units.AsNoTracking().Where(u => unitIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Name);

        var result = materials.Select(m => new
        {
            id = m.Id,
            name = m.Name,
            type = m.Type,
            link = m.Link,
            unitId = m.UnitId,
            unitName = m.UnitId.HasValue && unitNames.TryGetValue(m.UnitId.Value, out var n) ? n : null
        });

        return Ok(result);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var material = await _db.Materials.AsNoTracking().FirstOrDefaultAsync(e => e.Id == (id));
        if (material == null) return NotFound(new { message = "Material not found." });

        if (User.IsInRole(Roles.Student) && material.UnitId.HasValue &&
            !User.GetUnitIds().Contains(material.UnitId.Value))
            return StatusCode(403, new { message = "Not subscribed to this unit." });

        string? unitName = null;
        if (material.UnitId.HasValue)
        {
            unitName = await _db.Units.AsNoTracking().Where(u => u.Id == material.UnitId.Value)
                .Select(u => u.Name).FirstOrDefaultAsync();
        }

        return Ok(new
        {
            id = material.Id,
            name = material.Name,
            type = material.Type,
            link = material.Link,
            unitId = material.UnitId,
            unitName
        });
    }

    [HttpPost("google-drive")]
    public async Task<IActionResult> CreateGoogleDrive([FromBody] CreateGoogleDriveMaterialRequest request)
    {
        var material = new Material
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Google Drive Material" : request.Name,
            Type = "GoogleDrive",
            Link = request.Link,
            SchoolYear = request.SchoolYear,
            UnitId = request.UnitId,
            Months = request.Months,
            TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
        };
        _db.Materials.Add(material);
        await _db.SaveChangesAsync();

        var unitName = request.UnitId.HasValue
            ? await _db.Units.Where(u => u.Id == request.UnitId.Value).Select(u => u.Name).FirstOrDefaultAsync()
            : null;

        return StatusCode(201, new
        {
            id = material.Id,
            name = material.Name,
            type = material.Type,
            link = material.Link,
            unitId = material.UnitId,
            unitName
        });
    }

    // Real client contract (confirmed from "Pdfs(upload,read..etc)" page): ALWAYS
    // multipart, multiple files sent under the SAME field name "Files" (plural),
    // with "SchoolYear"/"UnitId" as extra form fields. One Material row is
    // created per uploaded file.
    [HttpPost("file")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadFile(
        [FromForm(Name = "Files")] List<IFormFile> files,
        [FromForm] int? schoolYear,
        [FromForm] int? unitId)
    {
        if (files == null || files.Count == 0)
            return BadRequest(new { message = "At least one file is required." });

        var created = new List<object>();

        foreach (var file in files)
        {
            if (file.Length == 0) continue;

            var url = await _files.SaveAsync(file, "materials");
            var material = new Material
            {
                Name = file.FileName,
                Type = "File",
                Link = url,
                SchoolYear = schoolYear,
                UnitId = unitId,
                TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
            };
            _db.Materials.Add(material);
            created.Add(material);
        }

        await _db.SaveChangesAsync();

        var unitName = unitId.HasValue
            ? await _db.Units.Where(u => u.Id == unitId.Value).Select(u => u.Name).FirstOrDefaultAsync()
            : null;

        var result = created.Cast<Material>().Select(material => new
        {
            id = material.Id,
            name = material.Name,
            type = material.Type,
            link = material.Link,
            unitId = material.UnitId,
            unitName
        }).ToList();

        // Single-file uploads keep returning one object (back-compat with any
        // caller expecting that shape); multi-file uploads return an array.
        return StatusCode(201, result.Count == 1 ? result[0] : result);
    }

    [HttpPost("delete")]
    public async Task<IActionResult> Delete([FromQuery] int materialId)
    {
        var material = await _db.Materials.FirstOrDefaultAsync(e => e.Id == (materialId));
        if (material == null) return NotFound(new { message = "Material not found." });

        _db.Materials.Remove(material);
        await _db.SaveChangesAsync();

        // Only "File" materials have an actual uploaded object to clean up;
        // "GoogleDrive" materials store an external link in the same field and
        // DeleteAsync already ignores anything that isn't one of our own URLs.
        if (material.Type == "File")
            await _files.DeleteAsync(material.Link);

        return Ok(new { message = "Material deleted." });
    }
}
