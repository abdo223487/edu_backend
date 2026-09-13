using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record CreateGoogleDriveMaterialRequest(string? Name, string Link, int? SchoolYear, int? UnitId, int? Months, List<int>? GroupIds);

/// <summary>Body for POST Material/direct-upload -- see that endpoint's doc comment.</summary>
public record CreateDirectUploadMaterialRequest(string? Name, string Link, int? SchoolYear, int? UnitId, int? Months, List<int>? GroupIds);

/// <summary>Body for POST Material/{id}/groups -- see that endpoint's doc comment.</summary>
public record UpdateMaterialGroupsRequest(List<int>? GroupIds);

/// <summary>
/// Route: api/Material (singular, matching Flutter's "Material" endpoint - NOT renamed to "Materials").
///  GET    Material?schoolYear=..&unitId=..     (teacher) -> each item includes "unitName", "groupIds", "groupNames"
///  GET    Material                             (student, unfiltered)
///  GET    Material/{id}
///  POST   Material/google-drive                (optional GroupIds -- see doc comment on Material.GroupIds)
///  POST   Material/file                        (multipart, field name "Files", supports multiple; optional "GroupIds" CSV form field)
///  GET    Material/pdf-upload-url               (presigned R2 PUT URL for direct client-side upload)
///  POST   Material/direct-upload                (create a Material row from an already-R2-uploaded file; optional GroupIds)
///  POST   Material/{id}/groups                  (edit which groups an existing Drive link/PDF is restricted to)
///  POST   Material/delete?materialId=..
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MaterialController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _files;
    private readonly ITenantContext _tenant;

    public MaterialController(AppDbContext db, IFileStorageService files, ITenantContext tenant)
    {
        _db = db;
        _files = files;
        _tenant = tenant;
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

        // Notebook attachments live in this same table but are payment-gated
        // (a student must have fully paid for the notebook to see them) via
        // Notebooks/{id}/materials — they must never leak into this
        // unrestricted list/detail endpoint.
        var query = _db.Materials.AsNoTracking().Where(m => m.NotebookId == null).AsQueryable();
        if (year.HasValue) query = query.Where(m => m.SchoolYear == year.Value);
        if (unitId.HasValue) query = query.Where(m => m.UnitId == unitId.Value);

        // Same subscription gate as Units/Lectures/Notifications: a material
        // tied to a specific unit is only visible to a student subscribed to
        // that unit. Materials with no UnitId pass through untouched.
        if (User.IsInRole(Roles.Student))
        {
            var subscribedIds = await Common.StudentAccessHelpers.GetEffectiveUnitIdsAsync(_db, User, User.GetUserId());
            query = query.Where(m => m.UnitId == null || subscribedIds.Contains(m.UnitId.Value));
        }

        var materials = await query.ToListAsync();

        // GROUP SCOPING: a material with GroupIds set is only for students
        // who are a member of at least one of those groups -- lets a
        // teacher upload one Drive link/PDF for a course but restrict it to
        // one/some of that course's groups instead of every group. A
        // material with no GroupIds keeps the old "everyone" behavior.
        if (User.IsInRole(Roles.Student))
        {
            var studentId = User.GetUserId();
            var studentGroupIds = await _db.StudentGroupMemberships.AsNoTracking()
                .Where(g => g.StudentId == studentId).Select(g => g.GroupId).ToListAsync();
            materials = materials.Where(m => m.GroupIds.Count == 0 || m.GroupIds.Any(studentGroupIds.Contains)).ToList();
        }

        // Hydrate unitName (Material.UnitId is a plain int?, no navigation
        // property) so Drive/PDF list screens can show the real unit name
        // instead of a hardcoded "Unit" placeholder.
        var unitIds = materials.Where(m => m.UnitId.HasValue).Select(m => m.UnitId!.Value).Distinct().ToList();
        var unitNames = await _db.Units.AsNoTracking().Where(u => unitIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Name);

        // Hydrate groupNames for teacher-facing list/edit UI (same idea as
        // unitNames above -- GroupIds are just ids, client needs the names
        // to render the current selection when editing).
        var allGroupIds = materials.SelectMany(m => m.GroupIds).Distinct().ToList();
        var groupNames = await _db.Groups.AsNoTracking().Where(g => allGroupIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => g.Name);

        var result = materials.Select(m => new
        {
            id = m.Id,
            name = m.Name,
            type = m.Type,
            link = m.Link,
            unitId = m.UnitId,
            unitName = m.UnitId.HasValue && unitNames.TryGetValue(m.UnitId.Value, out var n) ? n : null,
            groupIds = m.GroupIds,
            groupNames = m.GroupIds.Select(gid => groupNames.TryGetValue(gid, out var gn) ? gn : $"#{gid}").ToList()
        });

        return Ok(result);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var material = await _db.Materials.AsNoTracking().FirstOrDefaultAsync(e => e.Id == (id) && e.NotebookId == null);
        if (material == null) return NotFound(new { message = "Material not found." });

        if (User.IsInRole(Roles.Student))
        {
            var studentId = User.GetUserId();

            // GROUP SCOPING: same rule as GetAll -- a material restricted to
            // specific groups must not be reachable by id either, or the
            // group check on the list endpoint would be trivially bypassed
            // by a student who already knows/guesses the Material id.
            if (material.GroupIds.Count > 0)
            {
                var studentGroupIds = await _db.StudentGroupMemberships.AsNoTracking()
                    .Where(g => g.StudentId == studentId).Select(g => g.GroupId).ToListAsync();
                if (!material.GroupIds.Any(studentGroupIds.Contains))
                    return StatusCode(403, new { message = "Not available for your group." });
            }

            if (material.UnitId.HasValue)
            {
                // Same gate as LecturesController.ByGroup: visible via a
                // full Unit subscription OR a lecture-specific unlock.
                if (!(await Common.StudentAccessHelpers.GetEffectiveUnitIdsAsync(_db, User, studentId)).Contains(material.UnitId.Value) &&
                    !await _db.StudentLectureUnlocks.AnyAsync(u => u.StudentId == studentId && u.LectureId == material.LectureId))
                    return StatusCode(403, new { message = "Not subscribed to this unit." });
            }
            else if (material.LectureId.HasValue)
            {
                // SECURITY FIX: this branch didn't exist before -- a
                // Material tied only to a LectureId (no UnitId) skipped the
                // check above entirely (material.UnitId.HasValue was false),
                // so ANY authenticated student could fetch the file just by
                // knowing/guessing the Material id, completely bypassing
                // whatever code-based unlock was supposed to gate that
                // lecture (see StudentsController.RedeemCode and
                // LecturesController.ByGroup, which both treat a
                // no-UnitId lecture as locked until a StudentLectureUnlock
                // or StudentOnlineLessonUnlock row exists). Now mirrors the
                // exact same three-way gate those use:
                var lecture = await _db.Lectures.AsNoTracking()
                    .Where(l => l.Id == material.LectureId.Value)
                    .Select(l => new { l.OnlineLessonId })
                    .FirstOrDefaultAsync();

                var unlocked = lecture?.OnlineLessonId.HasValue == true
                    // Lecture lives inside an OnlineLesson container -- gated
                    // on unlocking the WHOLE container, not the lecture itself
                    // (see OnlineLessonsController and the Lecture.OnlineLessonId
                    // doc comment in Models/Entities.cs).
                    ? await _db.StudentOnlineLessonUnlocks.AnyAsync(u =>
                        u.StudentId == studentId && u.OnlineLessonId == lecture!.OnlineLessonId.Value)
                    // Standalone lecture (no Unit, no OnlineLesson) -- gated
                    // directly on a per-lecture unlock.
                    : await _db.StudentLectureUnlocks.AnyAsync(u =>
                        u.StudentId == studentId && u.LectureId == material.LectureId.Value);

                if (!unlocked)
                    return StatusCode(403, new { message = "Not unlocked for this lecture." });
            }
        }

        string? unitName = null;
        if (material.UnitId.HasValue)
        {
            unitName = await _db.Units.AsNoTracking().Where(u => u.Id == material.UnitId.Value)
                .Select(u => u.Name).FirstOrDefaultAsync();
        }

        var groupNames = material.GroupIds.Count > 0
            ? await _db.Groups.AsNoTracking().Where(g => material.GroupIds.Contains(g.Id))
                .Select(g => new { g.Id, g.Name }).ToDictionaryAsync(g => g.Id, g => g.Name)
            : new Dictionary<int, string>();

        return Ok(new
        {
            id = material.Id,
            name = material.Name,
            type = material.Type,
            link = material.Link,
            unitId = material.UnitId,
            unitName,
            groupIds = material.GroupIds,
            groupNames = material.GroupIds.Select(gid => groupNames.TryGetValue(gid, out var gn) ? gn : $"#{gid}").ToList()
        });
    }

    [HttpPost("google-drive")]
    public async Task<IActionResult> CreateGoogleDrive([FromBody] CreateGoogleDriveMaterialRequest request)
    {
        if (_tenant.CurrentTenantId == null) return Forbid();

        var material = new Material
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Google Drive Material" : request.Name,
            Type = "GoogleDrive",
            Link = request.Link,
            SchoolYear = request.SchoolYear,
            UnitId = request.UnitId,
            Months = request.Months,
            GroupIds = request.GroupIds?.Distinct().ToList() ?? new(),
            TeacherId = _tenant.CurrentTenantId.Value // TENANT LAYER
        };
        _db.Materials.Add(material);
        await _db.SaveChangesAsync();

        var unitName = request.UnitId.HasValue
            ? await _db.Units.Where(u => u.Id == request.UnitId.Value).Select(u => u.Name).FirstOrDefaultAsync()
            : null;
        var groupNames = await GetGroupNamesAsync(material.GroupIds);

        return StatusCode(201, new
        {
            id = material.Id,
            name = material.Name,
            type = material.Type,
            link = material.Link,
            unitId = material.UnitId,
            unitName,
            groupIds = material.GroupIds,
            groupNames
        });
    }

    // GET Material/pdf-upload-url?extension=.pdf&contentType=application/pdf
    // Same pattern as Lectures/video-upload-url: returns a short-lived
    // presigned R2 PUT URL so a PDF can be uploaded straight from the device
    // to Cloudflare R2, never through this API server. Flow: 1) call this to
    // get {uploadUrl, publicUrl}; 2) PUT the raw PDF bytes to uploadUrl with
    // the SAME Content-Type header sent here; 3) call POST Material/direct-upload
    // with Link = publicUrl to actually create the Material row.
    //
    // Used primarily by the SuperAdmin "رفع ماتريال مباشر" (optional, direct
    // upload) flow -- it deliberately skips the multipart Material/file
    // endpoint entirely so large PDFs never round-trip through the API
    // server's own bandwidth/memory.
    [HttpGet("pdf-upload-url")]
    public IActionResult GetPdfUploadUrl([FromQuery] string extension = ".pdf", [FromQuery] string contentType = "application/pdf")
    {
        if (!string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Only .pdf files are supported here." });

        try
        {
            var (uploadUrl, publicUrl) = _files.GetPresignedUploadUrl("materials", extension, contentType);
            return Ok(new { uploadUrl, publicUrl });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return StatusCode(501, new { message = ex.Message });
        }
    }

    // POST Material/direct-upload -- creates a Material row (Type = "File",
    // exactly like a real multipart upload would) for a file that was
    // already PUT directly to R2 via the presigned URL from pdf-upload-url
    // above. The file itself never touches this endpoint -- only its
    // already-hosted public URL does.
    [HttpPost("direct-upload")]
    public async Task<IActionResult> CreateFromDirectUpload([FromBody] CreateDirectUploadMaterialRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Link))
            return BadRequest(new { message = "Link is required." });

        if (_tenant.CurrentTenantId == null) return Forbid();

        var material = new Material
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Material" : request.Name,
            Type = "File",
            Link = request.Link,
            SchoolYear = request.SchoolYear,
            UnitId = request.UnitId,
            Months = request.Months,
            GroupIds = request.GroupIds?.Distinct().ToList() ?? new(),
            TeacherId = _tenant.CurrentTenantId.Value // TENANT LAYER
        };
        _db.Materials.Add(material);
        await _db.SaveChangesAsync();

        var unitName = request.UnitId.HasValue
            ? await _db.Units.Where(u => u.Id == request.UnitId.Value).Select(u => u.Name).FirstOrDefaultAsync()
            : null;
        var groupNames = await GetGroupNamesAsync(material.GroupIds);

        return StatusCode(201, new
        {
            id = material.Id,
            name = material.Name,
            type = material.Type,
            link = material.Link,
            unitId = material.UnitId,
            unitName,
            groupIds = material.GroupIds,
            groupNames
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
        [FromForm] int? unitId,
        // Multipart form fields can't bind a List<int> directly, so the
        // client sends a comma-separated string here (e.g. "3,5,9"), same
        // shape as every *IdsCsv column on the server side. Optional --
        // omitted/empty means "visible to every group" like before.
        [FromForm] string? groupIds)
    {
        if (files == null || files.Count == 0)
            return BadRequest(new { message = "At least one file is required." });

        if (_tenant.CurrentTenantId == null) return Forbid();

        var parsedGroupIds = string.IsNullOrWhiteSpace(groupIds)
            ? new List<int>()
            : groupIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse).Distinct().ToList();

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
                GroupIds = parsedGroupIds,
                TeacherId = _tenant.CurrentTenantId.Value // TENANT LAYER
            };
            _db.Materials.Add(material);
            created.Add(material);
        }

        await _db.SaveChangesAsync();

        var unitName = unitId.HasValue
            ? await _db.Units.Where(u => u.Id == unitId.Value).Select(u => u.Name).FirstOrDefaultAsync()
            : null;
        var groupNames = await GetGroupNamesAsync(parsedGroupIds);

        var result = created.Cast<Material>().Select(material => new
        {
            id = material.Id,
            name = material.Name,
            type = material.Type,
            link = material.Link,
            unitId = material.UnitId,
            unitName,
            groupIds = material.GroupIds,
            groupNames
        }).ToList();

        // Single-file uploads keep returning one object (back-compat with any
        // caller expecting that shape); multi-file uploads return an array.
        return StatusCode(201, result.Count == 1 ? result[0] : result);
    }

    private async Task<List<string>> GetGroupNamesAsync(List<int> groupIds)
    {
        if (groupIds.Count == 0) return new();
        var names = await _db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => g.Name);
        return groupIds.Select(gid => names.TryGetValue(gid, out var n) ? n : $"#{gid}").ToList();
    }

    // POST Material/{id}/groups -- the "تعديل" (edit) button on a Drive
    // link/PDF card: lets a teacher change which groups it's restricted to
    // AFTER it was already created, without needing to delete and re-upload
    // it. Pass an empty/omitted GroupIds to make it visible to every group
    // again. Returns the material with its refreshed groupIds/groupNames so
    // the edit dialog can immediately show the new selection.
    [HttpPost("{id:int}/groups")]
    public async Task<IActionResult> UpdateGroups(int id, [FromBody] UpdateMaterialGroupsRequest request)
    {
        var material = await _db.Materials.FirstOrDefaultAsync(m => m.Id == id && m.NotebookId == null);
        if (material == null) return NotFound(new { message = "Material not found." });

        material.GroupIds = request.GroupIds?.Distinct().ToList() ?? new();
        await _db.SaveChangesAsync();

        var groupNames = await GetGroupNamesAsync(material.GroupIds);

        return Ok(new
        {
            id = material.Id,
            name = material.Name,
            type = material.Type,
            link = material.Link,
            unitId = material.UnitId,
            groupIds = material.GroupIds,
            groupNames
        });
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
