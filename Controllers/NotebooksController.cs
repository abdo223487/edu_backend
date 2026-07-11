using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record CreateNotebookRequest(string Name, int SchoolYear, List<int> GroupIds, int Price, List<int>? UnitIds);

/// <summary>
/// Route: api/Notebooks
///  GET  Notebooks?schoolYear=..            -> list, includes aggregated "paid" and "createdAt"
///  POST Notebooks
///  GET  Notebooks/{id}                     -> details, "groups"/"units" hydrated with names
///  GET  Notebooks/{id}/payments            -> each payment includes "student" + "totalPaid"
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
        var query = _db.Notebooks.AsQueryable();
        if (schoolYear.HasValue) query = query.Where(n => n.SchoolYear == schoolYear.Value);

        var notebooks = await query.ToListAsync();
        var notebookIds = notebooks.Select(n => n.Id).ToList();

        // Sum of what students actually paid (DiscountedPrice if set, else full Price)
        // per notebook, computed in one query instead of N+1.
        var payments = await _db.NotebookPayments
            .Where(p => notebookIds.Contains(p.NotebookId))
            .ToListAsync();
        var paidByNotebook = payments
            .GroupBy(p => p.NotebookId)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.DiscountedPrice ?? p.Price));

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

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var notebook = await _db.Notebooks.FirstOrDefaultAsync(e => e.Id == id);
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        var groups = await _db.Groups
            .Where(g => notebook.GroupIds.Contains(g.Id))
            .Select(g => new { id = g.Id, name = g.Name, studentCount = g.Students.Count })
            .ToListAsync();

        var units = await _db.Units
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
        var payments = await _db.NotebookPayments
            .Where(p => p.NotebookId == id)
            .ToListAsync();

        var studentIds = payments.Select(p => p.StudentId).Distinct().ToList();
        var students = await _db.Students
            .Where(s => studentIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id);
        var groupNames = await _db.GetTenantGroupNamesAsync(studentIds);

        var result = payments.Select(p =>
        {
            students.TryGetValue(p.StudentId, out var s);
            var totalPaid = p.DiscountedPrice ?? p.Price;

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
                    status = s.IsCancelled ? "ملغي" : (s.IsSuspended ? "موقوف" : "نشط")
                }
            };
        });

        return Ok(result);
    }
}
