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
    int SwapLimit = 0,
    Dictionary<string, string>? FeatureNotes = null);

/// <summary>Body for POST api/Plans/feature-notes — the COMPLETE set of global notes.</summary>
public record SaveFeatureNotesRequest(Dictionary<string, string>? Notes);

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

    internal static object ToDto(FeaturePlan p, IDictionary<string, string> globalNotes)
    {
        var included = TeacherFeatures.Parse(p.FeatureKeys);
        var includedSet = included.ToHashSet();

        // note shown next to a feature: this plan's own note, else the global one
        var planNotes = TeacherFeatures.ParseNotes(p.FeatureNotesJson);
        string? NoteFor(string key) =>
            planNotes.TryGetValue(key, out var n) ? n
            : globalNotes.TryGetValue(key, out var g) ? g
            : null;
        return new
        {
            id = p.Id,
            name = p.Name,
            price = p.Price,
            swapLimit = p.SwapLimit,
            featureKeys = included,
            // catalog features, labelled by the server so the app never hard-codes text
            available = TeacherFeatures.Catalog.Where(f => includedSet.Contains(f.Key))
                .Select(f => new { key = f.Key, label = f.Label, group = f.Group, note = NoteFor(f.Key) }),
            blocked = TeacherFeatures.Catalog.Where(f => !includedSet.Contains(f.Key))
                .Select(f => new { key = f.Key, label = f.Label, group = f.Group, note = NoteFor(f.Key) }),
            // raw notes of THIS plan only (the plan editor needs them separate from the global ones)
            featureNotes = planNotes,
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
        var global = await LoadGlobalNotes();
        return Ok(plans.Select(p => ToDto(p, global)));
    }

    private async Task<Dictionary<string, string>> LoadGlobalNotes() =>
        await _db.FeatureNotes.AsNoTracking().ToDictionaryAsync(n => n.Key, n => n.Note);

    // GET api/Plans/catalog  — the switchable features + their GLOBAL note
    [HttpGet("catalog")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> Catalog()
    {
        var global = await LoadGlobalNotes();
        return Ok(TeacherFeatures.Catalog.Select(f => new
        {
            key = f.Key,
            label = f.Label,
            group = f.Group,
            note = global.TryGetValue(f.Key, out var n) ? n : null,
        }));
    }

    // GET api/Plans/feature-notes — same list, for the "ملاحظات الفيتشرز" screen
    [HttpGet("feature-notes")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public Task<IActionResult> GetFeatureNotes() => Catalog();

    // POST api/Plans/feature-notes  body: { notes: { "my_students": "حتى 500 طالب", ... } }
    // Replaces ALL global notes (a feature missing / blank = no global note).
    [HttpPost("feature-notes")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> SaveFeatureNotes([FromBody] SaveFeatureNotesRequest request)
    {
        var clean = CleanNotes(request.Notes, out var error);
        if (error != null) return BadRequest(new { message = error });

        _db.FeatureNotes.RemoveRange(await _db.FeatureNotes.ToListAsync());
        foreach (var (key, note) in clean)
            _db.FeatureNotes.Add(new FeatureNote { Key = key, Note = note });
        await _db.SaveChangesAsync();
        return Ok(new { saved = clean.Count });
    }

    [HttpPost]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> Create([FromBody] SavePlanRequest request)
    {
        var error = Validate(request, out var name, out var keys, out var inc, out var exc, out var notes);
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
            FeatureNotesJson = TeacherFeatures.SerializeNotes(notes),
        };
        _db.FeaturePlans.Add(plan);
        await _db.SaveChangesAsync();
        return StatusCode(201, ToDto(plan, await LoadGlobalNotes()));
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> Update(int id, [FromBody] SavePlanRequest request)
    {
        var plan = await _db.FeaturePlans.FirstOrDefaultAsync(p => p.Id == id);
        if (plan == null) return NotFound(new { message = "Plan not found." });

        var error = Validate(request, out var name, out var keys, out var inc, out var exc, out var notes);
        if (error != null) return BadRequest(new { message = error });

        if (await _db.FeaturePlans.AnyAsync(p => p.Id != id && p.Name.ToLower() == name.ToLower()))
            return Conflict(new { message = "فيه باقة بنفس الاسم." });

        plan.Name = name;
        plan.Price = request.Price;
        plan.SwapLimit = request.SwapLimit;
        plan.FeatureKeys = TeacherFeatures.Serialize(keys);
        plan.ExtraIncluded = TeacherFeatures.JoinLines(inc);
        plan.ExtraExcluded = TeacherFeatures.JoinLines(exc);
        plan.FeatureNotesJson = TeacherFeatures.SerializeNotes(notes);
        await _db.SaveChangesAsync();
        return Ok(ToDto(plan, await LoadGlobalNotes()));
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
        out List<string> inc, out List<string> exc, out Dictionary<string, string> notes)
    {
        name = (r.Name ?? "").Trim();
        keys = new(); inc = new(); exc = new(); notes = new();

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

        notes = CleanNotes(r.FeatureNotes, out var notesError);
        return notesError;
    }

    /// <summary>Trims, drops blanks, rejects unknown keys and notes over 5000 chars.</summary>
    private static Dictionary<string, string> CleanNotes(Dictionary<string, string>? raw, out string? error)
    {
        error = null;
        var clean = new Dictionary<string, string>();
        foreach (var (key, value) in raw ?? new Dictionary<string, string>())
        {
            var note = (value ?? "").Trim();
            if (note.Length == 0) continue;
            if (!TeacherFeatures.IsValid(key)) { error = "Unknown feature key: " + key; break; }
            if (note.Length > 5000) { error = "الملاحظة الواحدة أقصاها 5000 حرف."; break; }
            clean[key] = note;
        }
        return clean;
    }

    private static List<string> Clean(List<string>? lines) =>
        (lines ?? new()).Select(l => (l ?? "").Replace("\n", " ").Trim()).Where(l => l.Length > 0).ToList();
}
