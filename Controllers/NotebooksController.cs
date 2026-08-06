using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record CreateNotebookRequest(string Name, int SchoolYear, List<int> GroupIds, int Price, List<int>? UnitIds);
public record RenameNotebookRequest(string Name);
public record AddNotebookMaterialLinkRequest(string? Name, string Link);
/// <summary>Body for POST Notebooks/{id}/materials/direct-upload -- see that endpoint's doc comment.</summary>
public record CreateNotebookDirectUploadMaterialRequest(string? Name, string Link);

/// <summary>
/// Route: api/Notebooks
///  GET   Notebooks?schoolYear=..            -> list, includes aggregated "paid" and "createdAt"
///  POST  Notebooks
///  PATCH Notebooks/{id}                     -> rename
///  GET   Notebooks/{id}                     -> details, "groups"/"units" hydrated with names
///  GET   Notebooks/{id}/payments            -> each payment includes "student" + "totalPaid"
///  GET   Notebooks/{id}/materials           (teacher/admin: all; student: only if fully paid)
///  POST  Notebooks/{id}/materials/file      (teacher/admin, multipart, field "Files", optional/multiple)
///  POST  Notebooks/{id}/materials/link      (teacher/admin, JSON {name, link} — e.g. a ready Cloudflare R2 URL)
///  GET   Notebooks/{id}/materials/pdf-upload-url  (SuperAdmin direct-upload flow -- presigned R2 PUT URL)
///  POST  Notebooks/{id}/materials/direct-upload   (SuperAdmin direct-upload flow -- register the R2 URL)
///
/// SuperAdmin is allowed on GetAll/GetById/GetMaterials/pdf-upload-url/direct-upload
/// so it can drive the same "رفع ماتريال مباشر" flow used for regular Materials,
/// but scoped to a specific teacher's notebook (acting on behalf of that teacher
/// via the X-TenantId header, same mechanism as everywhere else -- see ITenantContext).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize] // any authenticated user; each action below narrows further.
public class NotebooksController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _files;
    private readonly ITenantContext _tenant;
    public NotebooksController(AppDbContext db, IFileStorageService files, ITenantContext tenant)
    {
        _db = db;
        _files = files;
        _tenant = tenant;
    }

    [HttpGet]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> GetAll([FromQuery] int? schoolYear)
    {
        var query = _db.Notebooks.AsNoTracking().AsQueryable();
        if (schoolYear.HasValue) query = query.Where(n => n.SchoolYear == schoolYear.Value);

        var notebooks = await query.ToListAsync();
        var notebookIds = notebooks.Select(n => n.Id).ToList();

        // Sum of what students actually paid — real payment rows only.
        // Discount rows (DiscountedPrice set) are a target marker, not cash
        // received, so they must NOT be summed here or every discount
        // application/re-application would add the notebook's full price to
        // the "paid" total.
        var payments = await _db.NotebookPayments.AsNoTracking()
            .Where(p => notebookIds.Contains(p.NotebookId))
            .Select(p => new { p.NotebookId, p.DiscountedPrice, p.Price })
            .ToListAsync();
        var paidByNotebook = payments
            .Where(p => !p.DiscountedPrice.HasValue)
            .GroupBy(p => p.NotebookId)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Price));

        var result = notebooks.Select(n => new
        {
            id = n.Id,
            name = n.Name,
            schoolYear = n.SchoolYear,
            groupIds = n.GroupIds,
            price = n.Price,
            unitIds = n.UnitIds,
            createdAt = n.CreatedAt,
            paid = paidByNotebook.TryGetValue(n.Id, out var total) ? total : 0
        });

        return Ok(result);
    }

    [HttpPost]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Create([FromBody] CreateNotebookRequest request)
    {
        if (_tenant.CurrentTenantId == null) return Forbid();

        var notebook = new Notebook
        {
            Name = request.Name,
            SchoolYear = request.SchoolYear,
            GroupIds = request.GroupIds ?? new(),
            Price = request.Price,
            UnitIds = request.UnitIds ?? new(),
            TeacherId = _tenant.CurrentTenantId.Value // TENANT LAYER
        };
        _db.Notebooks.Add(notebook);
        await _db.SaveChangesAsync();

        return StatusCode(201, new
        {
            id = notebook.Id,
            name = notebook.Name,
            schoolYear = notebook.SchoolYear,
            groupIds = notebook.GroupIds,
            price = notebook.Price,
            unitIds = notebook.UnitIds,
            createdAt = notebook.CreatedAt,
            paid = 0
        });
    }

    // PATCH Notebooks/{id}  body: { name }
    [HttpPatch("{id:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Rename(int id, [FromBody] RenameNotebookRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Name is required." });

        var notebook = await _db.Notebooks.FirstOrDefaultAsync(e => e.Id == id);
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        notebook.Name = request.Name.Trim();
        await _db.SaveChangesAsync();

        return Ok(new { id = notebook.Id, name = notebook.Name });
    }

    [HttpGet("{id:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> GetById(int id)
    {
        var notebook = await _db.Notebooks.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        var groups = await _db.Groups.AsNoTracking()
            .Where(g => notebook.GroupIds.Contains(g.Id))
            // COUNT FIX: see GroupsController -- use the membership join table, not the
            // legacy Group.Students FK-only collection, so students linked to this group
            // only via StudentGroupMembership are actually counted.
            .Select(g => new { id = g.Id, name = g.Name, studentCount = _db.StudentGroupMemberships.Count(m => m.GroupId == g.Id) })
            .ToListAsync();

        var units = await _db.Units.AsNoTracking()
            .Where(u => notebook.UnitIds.Contains(u.Id))
            .Select(u => new { id = u.Id, name = u.Name, month = u.Month })
            .ToListAsync();

        return Ok(new
        {
            id = notebook.Id,
            name = notebook.Name,
            schoolYear = notebook.SchoolYear,
            price = notebook.Price,
            createdAt = notebook.CreatedAt,
            groups,
            units
        });
    }

    [HttpGet("{id:int}/payments")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetPayments(int id)
    {
        var payments = await _db.NotebookPayments.AsNoTracking()
            .Where(p => p.NotebookId == id && !p.DiscountedPrice.HasValue)
            .ToListAsync();

        var studentIds = payments.Select(p => p.StudentId).Distinct().ToList();
        var students = await _db.Students.AsNoTracking()
            .Where(s => studentIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, s.PhoneNumber })
            .ToDictionaryAsync(s => s.Id);
        var groupNames = await _db.GetTenantGroupNamesAsync(studentIds);
        // PER-TENANT FIX: status (suspended/cancelled) is now per teacher-group
        // membership, not a global flag on Student -- look it up scoped to the
        // caller's own tenant (StudentGroupMemberships is already tenant-filtered).
        var statusByStudent = await _db.StudentGroupMemberships.AsNoTracking()
            .Where(m => studentIds.Contains(m.StudentId))
            .Select(m => new { m.StudentId, m.IsSuspended, m.IsCancelled })
            .ToDictionaryAsync(m => m.StudentId);

        var result = payments.Select(p =>
        {
            students.TryGetValue(p.StudentId, out var s);
            statusByStudent.TryGetValue(p.StudentId, out var st);
            var totalPaid = p.Price;

            return new
            {
                id = p.Id,
                studentId = p.StudentId,
                price = p.Price,
                discountedPrice = p.DiscountedPrice,
                totalPaid,
                date = p.Date,
                student = s == null ? null : new
                {
                    id = s.Id,
                    name = s.Name,
                    groupName = groupNames.GetValueOrDefault(s.Id),
                    phoneNumber = s.PhoneNumber,
                    status = st?.IsCancelled == true ? "ملغي" : (st?.IsSuspended == true ? "موقوف" : "نشط")
                }
            };
        });

        return Ok(result);
    }

    // GET Notebooks/{id}/materials
    // Teacher/AssistantAdmin: always see every attachment (used by the "add
    // notebook" screen to review what's uploaded).
    // Student: only unlocked once they've fully paid for this notebook —
    // matches the same "paid" rule used in GetAll (a real NotebookPayment
    // row with no DiscountedPrice). Attaching a PDF is optional, so an
    // unpaid/no-attachment notebook simply returns an empty list.
    [HttpGet("{id:int}/materials")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.Student},{Roles.SuperAdmin}")]
    public async Task<IActionResult> GetMaterials(int id)
    {
        var notebook = await _db.Notebooks.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id);
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        if (User.IsInRole(Roles.Student))
        {
            var studentId = User.GetUserId();
            var hasPaid = await _db.NotebookPayments.AsNoTracking()
                .AnyAsync(p => p.NotebookId == id && p.StudentId == studentId && !p.DiscountedPrice.HasValue);
            if (!hasPaid)
                return StatusCode(403, new { message = "لازم تدفع تمن النوتبوك كامل الأول عشان تشوف المذكرات." });
        }

        var materials = await _db.Materials.AsNoTracking().Where(m => m.NotebookId == id)
            .Select(m => new { id = m.Id, name = m.Name, type = m.Type, link = m.Link })
            .ToListAsync();

        return Ok(materials);
    }

    // POST Notebooks/{id}/materials/file  (multipart, field "Files", supports multiple)
    // Attaching notebook PDFs/images is entirely optional — same UX as
    // Lectures/{id}/materials/file — called right after Create() when the
    // teacher toggled "إرفاق ملف PDF / صور" while adding the notebook.
    [HttpPost("{id:int}/materials/file")]
    [Consumes("multipart/form-data")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> UploadMaterials(int id, [FromForm(Name = "Files")] List<IFormFile> files)
    {
        var notebook = await _db.Notebooks.FirstOrDefaultAsync(n => n.Id == id);
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        if (files == null || files.Count == 0)
            return BadRequest(new { message = "At least one file is required." });

        if (_tenant.CurrentTenantId == null) return Forbid();

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
                NotebookId = id,
                SchoolYear = notebook.SchoolYear,
                TeacherId = _tenant.CurrentTenantId.Value // TENANT LAYER
            };
            _db.Materials.Add(material);
            created.Add(material);
        }

        await _db.SaveChangesAsync();

        var result = created.Cast<Material>()
            .Select(m => new { id = m.Id, name = m.Name, type = m.Type, link = m.Link })
            .ToList();

        return StatusCode(201, result);
    }

    // POST Notebooks/{id}/materials/link  body: { name, link }
    // For a file already hosted somewhere (e.g. a Cloudflare R2 object the
    // teacher uploaded outside the app) — no upload needed, we just store
    // the ready URL. Same "File" type as an uploaded PDF/image (a student
    // that has paid opens it the exact same way, straight off the link) so
    // the client doesn't need to special-case it.
    [HttpPost("{id:int}/materials/link")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> AddMaterialLink(int id, [FromBody] AddNotebookMaterialLinkRequest request)
    {
        var notebook = await _db.Notebooks.FirstOrDefaultAsync(n => n.Id == id);
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        if (string.IsNullOrWhiteSpace(request.Link))
            return BadRequest(new { message = "Link is required." });

        if (_tenant.CurrentTenantId == null) return Forbid();

        var material = new Material
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? "مذكرة" : request.Name,
            Type = "File",
            Link = request.Link,
            NotebookId = id,
            SchoolYear = notebook.SchoolYear,
            TeacherId = _tenant.CurrentTenantId.Value // TENANT LAYER
        };
        _db.Materials.Add(material);
        await _db.SaveChangesAsync();

        return StatusCode(201, new { id = material.Id, name = material.Name, type = material.Type, link = material.Link });
    }

    // GET Notebooks/{id}/materials/pdf-upload-url?extension=.pdf&contentType=application/pdf
    // Same "رفع ماتريال مباشر" pattern as Material/pdf-upload-url: returns a
    // short-lived presigned R2 PUT URL so a PDF can be uploaded straight from
    // the SuperAdmin's device to Cloudflare R2, never through this API server.
    // Flow: 1) call this to get {uploadUrl, publicUrl}; 2) PUT the raw PDF
    // bytes to uploadUrl with the SAME Content-Type header sent here; 3) call
    // POST Notebooks/{id}/materials/direct-upload with Link = publicUrl.
    [HttpGet("{id:int}/materials/pdf-upload-url")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> GetPdfUploadUrl(int id, [FromQuery] string extension = ".pdf", [FromQuery] string contentType = "application/pdf")
    {
        var notebookExists = await _db.Notebooks.AsNoTracking().AnyAsync(n => n.Id == id);
        if (!notebookExists) return NotFound(new { message = "Notebook not found." });

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

    // POST Notebooks/{id}/materials/direct-upload -- creates a Material row
    // (Type = "File", NotebookId = id) for a file that was already PUT
    // directly to R2 via the presigned URL from pdf-upload-url above.
    [HttpPost("{id:int}/materials/direct-upload")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> CreateFromDirectUpload(int id, [FromBody] CreateNotebookDirectUploadMaterialRequest request)
    {
        var notebook = await _db.Notebooks.FirstOrDefaultAsync(n => n.Id == id);
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        if (string.IsNullOrWhiteSpace(request.Link))
            return BadRequest(new { message = "Link is required." });

        if (_tenant.CurrentTenantId == null) return Forbid();

        var material = new Material
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? "مذكرة" : request.Name,
            Type = "File",
            Link = request.Link,
            NotebookId = id,
            SchoolYear = notebook.SchoolYear,
            TeacherId = _tenant.CurrentTenantId.Value // TENANT LAYER
        };
        _db.Materials.Add(material);
        await _db.SaveChangesAsync();

        return StatusCode(201, new { id = material.Id, name = material.Name, type = material.Type, link = material.Link });
    }
}
