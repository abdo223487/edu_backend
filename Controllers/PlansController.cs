using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>Body for POST/PUT api/Plans — SuperAdmin only.</summary>
public record SavePlanRequest(
    string? Name,
    decimal Price,
    List<string>? FeatureKeys,
    List<string>? ExtraIncluded,
    List<string>? ExtraExcluded,
    int SwapLimit = 0);

/// <summary>
/// باقات الفيتشرز (feature plans).
///
///  GET    api/Plans                 Preview / Teacher / SuperAdmin -> every plan with its available + blocked features
///  GET    api/Plans/catalog         SuperAdmin -> the 21 switchable features (for the plan editor)
///  POST   api/Plans                 SuperAdmin -> create
///  PUT    api/Plans/{id}            SuperAdmin -> edit (name / price / features / extra lines)
///  POST   api/Plans/{id}/delete     SuperAdmin -> delete (teachers on it keep their CURRENT features as "custom")
///
/// Editing a plan applies LIVE to every teacher on it (their dashboard reads the plan each time it opens).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PlansController : ControllerBase
{
    private readonly AppDbContext _db;

    public PlansController(AppDbContext db) => _db = db;

    internal static object ToDto(FeaturePlan p)
    {
        var included = TeacherFeatures.Parse(p.FeatureKeys);
        var includedSet = included.ToHashSet();
        return new
        {
            id = p.Id,
            name = p.Name,
            price = p.Price,
            swapLimit = p.SwapLimit,
            featureKeys = included,
            // catalog features, labelled by the server so the app never hard-codes text
            available = TeacherFeatures.Catalog.Where(f => includedSet.Contains(f.Key))
                .Select(f => new { key = f.Key, label = f.Label, group = f.Group }),
            blocked = TeacherFeatures.Catalog.Where(f => !includedSet.Contains(f.Key))
                .Select(f => new { key = f.Key, label = f.Label, group = f.Group }),
            // free text the SuperAdmin wrote
            extraIncluded = TeacherFeatures.SplitLines(p.ExtraIncluded),
            extraExcluded = TeacherFeatures.SplitLines(p.ExtraExcluded),
        };
    }

    [HttpGet]
    [Authorize(Roles = $"{Roles.Preview},{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> GetAll()
    {
        var plans = await _db.FeaturePlans.AsNoTracking()
            .OrderBy(p => p.Price).ThenBy(p => p.Id)
            .ToListAsync();
        return Ok(plans.Select(ToDto));
    }

    [HttpGet("catalog")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public IActionResult Catalog() =>
        Ok(TeacherFeatures.Catalog.Select(f => new { key = f.Key, label = f.Label, group = f.Group }));

    [HttpPost]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> Create([FromBody] SavePlanRequest request)
    {
        var error = Validate(request, out var name, out var keys, out var inc, out var exc);
        if (error != null) return BadRequest(new { message = error });

        if (await _db.FeaturePlans.AnyAsync(p => p.Name.ToLower() == name.ToLower()))
            return Conflict(new { message = "فيه باقة بنفس الاسم." });

        var plan = new FeaturePlan
        {
            Name = name,
            Price = request.Price,
            SwapLimit = request.SwapLimit,
            FeatureKeys = TeacherFeatures.Serialize(keys),
            ExtraIncluded = TeacherFeatures.JoinLines(inc),
            ExtraExcluded = TeacherFeatures.JoinLines(exc),
        };
        _db.FeaturePlans.Add(plan);
        await _db.SaveChangesAsync();
        return StatusCode(201, ToDto(plan));
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> Update(int id, [FromBody] SavePlanRequest request)
    {
        var plan = await _db.FeaturePlans.FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null) return NotFound(new { message = "Plan not found." });

        var error = Validate(request, out var name, out var keys, out var inc, out var exc);
        if (error != null) return BadRequest(new { message = error });

        if (await _db.FeaturePlans.AnyAsync(p => p.Id != id && p.Name.ToLower() == name.ToLower()))
            return Conflict(new { message = "فيه باقة بنفس الاسم." });

        plan.Name = name;
        plan.Price = request.Price;
        plan.SwapLimit = request.SwapLimit;
        plan.FeatureKeys = TeacherFeatures.Serialize(keys);
        plan.ExtraIncluded = TeacherFeatures.JoinLines(inc);
        plan.ExtraExcluded = TeacherFeatures.JoinLines(exc);
        await _db.SaveChangesAsync();
        return Ok(ToDto(plan));
    }

    [HttpPost("{id:int}/delete")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> Delete(int id)
    {
        var plan = await _db.FeaturePlans.FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null) return NotFound(new { message = "Plan not found." });

        // Teachers on this plan must NOT suddenly lose/gain buttons: freeze what
        // they have right now as their manual ("custom") list, then detach.
        var teachers = await _db.Teachers.Where(t => t.PlanId == id).ToListAsync();
        foreach (var t in teachers)
        {
            // per teacher: his own swaps are part of what he has right now
            t.DisabledFeatures = TeacherFeatures.Serialize(
                TeacherFeatures.EffectiveDisabled(null, plan, t.SwapIn, t.SwapOut));
            t.PlanId = null;
            t.SwapIn = null;
            t.SwapOut = null;
        }

        _db.FeaturePlans.Remove(plan);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true, teachersMovedToCustom = teachers.Count });
    }

    private static string? Validate(SavePlanRequest r, out string name, out List<string> keys,
        out List<string> inc, out List<string> exc)
    {
        name = (r.Name ?? "").Trim();
        keys = new(); inc = new(); exc = new();

        if (name.Length == 0) return "اسم الباقة مطلوب.";
        if (name.Length > 80) return "اسم الباقة طويل (80 حرف كحد أقصى).";
        if (r.Price < 0 || r.Price > 100_000_000m) return "السعر غير صحيح.";
        if (r.SwapLimit < 0 || r.SwapLimit > TeacherFeatures.Catalog.Count) return "عدد التبديلات غير صحيح.";

        keys = (r.FeatureKeys ?? new()).Select(k => (k ?? "").Trim()).Where(k => k.Length > 0).Distinct().ToList();
        var unknown = keys.Where(k => !TeacherFeatures.IsValid(k)).ToList();
        if (unknown.Count > 0) return "Unknown feature key(s): " + string.Join(", ", unknown);

        inc = Clean(r.ExtraIncluded);
        exc = Clean(r.ExtraExcluded);
        if (inc.Count > 30 || exc.Count > 30) return "الحد الأقصى 30 سطر في كل قائمة.";
        if (inc.Concat(exc).Any(l => l.Length > 120)) return "السطر الواحد أقصاه 120 حرف.";
        return null;
    }

    private static List<string> Clean(List<string>? lines) =>
        (lines ?? new()).Select(l => (l ?? "").Replace("\n", " ").Trim()).Where(l => l.Length > 0).ToList();
}
