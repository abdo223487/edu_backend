using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record CreateNotebookRequest(string Name, int SchoolYear, List<int> GroupIds, int Price, List<int>? UnitIds);
public record RenameNotebookRequest(string Name);

/// <summary>
/// Route: api/Notebooks
///  GET   Notebooks?schoolYear=..            -> list, includes aggregated "paid" and "createdAt"
///  POST  Notebooks
///  PATCH Notebooks/{id}                     -> rename
///  GET   Notebooks/{id}                     -> details, "groups"/"units" hydrated with names
///  GET   Notebooks/{id}/payments            -> each payment includes "student" + "totalPaid"
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
public class NotebooksController : ControllerBase
{
    private readonly AppDbContext _db;
    public NotebooksController(AppDbContext db) => _db = db;

    [HttpGet]
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
    public async Task<IActionResult> Create([FromBody] CreateNotebookRequest request)
    {
        var notebook = new Notebook
        {
            Name = request.Name,
            SchoolYear = request.SchoolYear,
            GroupIds = request.GroupIds ?? new(),
            Price = request.Price,
            UnitIds = request.UnitIds ?? new(),
            TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
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
    public async Task<IActionResult> GetPayments(int id)
    {
        var payments = await _db.NotebookPayments.AsNoTracking()
            .Where(p => p.NotebookId == id)
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
            var totalPaid = p.DiscountedPrice.HasValue ? 0 : p.Price;

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
}
