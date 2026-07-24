using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record CreateBillingRequest(string Name, int SchoolYear, List<int> GroupIds, int Price, List<int>? UnitIds);
public record RenameBillingRequest(string Name);

/// <summary>
/// Route: api/Billings — exact mirror of NotebooksController (same endpoints,
/// same response shapes, same rules), used for the "monthly billing" item
/// instead of a notebook.
///  GET   Billings?schoolYear=..            -> list, includes aggregated "paid" and "createdAt"
///  POST  Billings
///  PATCH Billings/{id}                     -> rename
///  GET   Billings/{id}                     -> details, "groups"/"units" hydrated with names
///  GET   Billings/{id}/payments            -> each payment includes "student" + "totalPaid"
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
public class BillingsController : ControllerBase
{
    private readonly AppDbContext _db;
    public BillingsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? schoolYear)
    {
        var query = _db.Billings.AsNoTracking().AsQueryable();
        if (schoolYear.HasValue) query = query.Where(n => n.SchoolYear == schoolYear.Value);

        var billings = await query.ToListAsync();
        var billingIds = billings.Select(n => n.Id).ToList();

        // Same rule as NotebooksController: discount rows (DiscountedPrice
        // set) are a target marker, not cash received, so they must NOT be
        // summed here.
        var payments = await _db.BillingPayments.AsNoTracking()
            .Where(p => billingIds.Contains(p.BillingId))
            .Select(p => new { p.BillingId, p.DiscountedPrice, p.Price })
            .ToListAsync();
        var paidByBilling = payments
            .Where(p => !p.DiscountedPrice.HasValue)
            .GroupBy(p => p.BillingId)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Price));

        var result = billings.Select(n => new
        {
            id = n.Id,
            name = n.Name,
            schoolYear = n.SchoolYear,
            groupIds = n.GroupIds,
            price = n.Price,
            unitIds = n.UnitIds,
            createdAt = n.CreatedAt,
            paid = paidByBilling.TryGetValue(n.Id, out var total) ? total : 0
        });

        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBillingRequest request)
    {
        var billing = new Billing
        {
            Name = request.Name,
            SchoolYear = request.SchoolYear,
            GroupIds = request.GroupIds ?? new(),
            Price = request.Price,
            UnitIds = request.UnitIds ?? new(),
            TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
        };
        _db.Billings.Add(billing);
        await _db.SaveChangesAsync();

        return StatusCode(201, new
        {
            id = billing.Id,
            name = billing.Name,
            schoolYear = billing.SchoolYear,
            groupIds = billing.GroupIds,
            price = billing.Price,
            unitIds = billing.UnitIds,
            createdAt = billing.CreatedAt,
            paid = 0
        });
    }

    // PATCH Billings/{id}  body: { name }
    [HttpPatch("{id:int}")]
    public async Task<IActionResult> Rename(int id, [FromBody] RenameBillingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Name is required." });

        var billing = await _db.Billings.FirstOrDefaultAsync(e => e.Id == id);
        if (billing == null) return NotFound(new { message = "Billing not found." });

        billing.Name = request.Name.Trim();
        await _db.SaveChangesAsync();

        return Ok(new { id = billing.Id, name = billing.Name });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var billing = await _db.Billings.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
        if (billing == null) return NotFound(new { message = "Billing not found." });

        var groups = await _db.Groups.AsNoTracking()
            .Where(g => billing.GroupIds.Contains(g.Id))
            .Select(g => new { id = g.Id, name = g.Name, studentCount = _db.StudentGroupMemberships.Count(m => m.GroupId == g.Id) })
            .ToListAsync();

        var units = await _db.Units.AsNoTracking()
            .Where(u => billing.UnitIds.Contains(u.Id))
            .Select(u => new { id = u.Id, name = u.Name, month = u.Month })
            .ToListAsync();

        return Ok(new
        {
            id = billing.Id,
            name = billing.Name,
            schoolYear = billing.SchoolYear,
            price = billing.Price,
            createdAt = billing.CreatedAt,
            groups,
            units
        });
    }

    [HttpGet("{id:int}/payments")]
    public async Task<IActionResult> GetPayments(int id)
    {
        var payments = await _db.BillingPayments.AsNoTracking()
            .Where(p => p.BillingId == id && !p.DiscountedPrice.HasValue)
            .ToListAsync();

        var studentIds = payments.Select(p => p.StudentId).Distinct().ToList();
        var students = await _db.Students.AsNoTracking()
            .Where(s => studentIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, s.PhoneNumber })
            .ToDictionaryAsync(s => s.Id);
        var groupNames = await _db.GetTenantGroupNamesAsync(studentIds);
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
}
