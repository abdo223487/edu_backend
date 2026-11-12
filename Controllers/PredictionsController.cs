using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// "توقّع الدرجة والطلاب في خطر": a transparent statistical model (see PredictionEngine) built from
/// results, trend, attendance, missed assessments and silence -- no AI, no new table.
///
/// Route: api/Predictions
///  TEACHER / ASSISTANT
///   GET Predictions/summary?groupId=                 counts of high / medium / low risk students
///   GET Predictions/at-risk?groupId=&amp;level=&amp;p=   students sorted by risk, worst first (level: high | medium | low | nodata | all)
///   GET Predictions/student/{studentId}              one student's full picture
///  STUDENT
///   GET Predictions/me?target=90                    my predicted result + "what do I need to reach my target"
///
/// The teacher lists are cached for 5 minutes per tenant (+ group), so opening the screen repeatedly
/// does not recompute everything.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PredictionsController : ControllerBase
{
    private const string StaffRoles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.Assistant}";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ICacheService _cache;

    public PredictionsController(AppDbContext db, ITenantContext tenant, ICacheService cache)
    {
        _db = db;
        _tenant = tenant;
        _cache = cache;
    }

    /// <summary>Active students of this tenant (optionally of one group) -> their group.</summary>
    private async Task<Dictionary<int, int>> StudentGroupsAsync(int? groupId)
    {
        var q = _db.StudentGroupMemberships.AsNoTracking().Where(m => !m.IsSuspended && !m.IsCancelled);
        if (groupId.HasValue) q = q.Where(m => m.GroupId == groupId.Value);
        var rows = await q.Select(m => new { m.StudentId, m.GroupId }).ToListAsync();
        return rows.GroupBy(r => r.StudentId).ToDictionary(g => g.Key, g => g.First().GroupId);
    }

    private async Task<List<StudentPrediction>> ComputeCachedAsync(int? groupId)
    {
        var tenantId = _tenant.CurrentTenantId ?? 0;
        return await _cache.GetOrCreateAsync(
            $"tenant:{tenantId}:predictions:{groupId?.ToString() ?? "all"}", CacheTtl,
            async () => await PredictionEngine.ComputeAsync(_db, await StudentGroupsAsync(groupId)));
    }

    private async Task<Dictionary<int, string>> NamesAsync(IEnumerable<int> ids)
    {
        var list = ids.Distinct().ToList();
        return await _db.Students.IgnoreQueryFilters().AsNoTracking()
            .Where(s => list.Contains(s.Id)).Select(s => new { s.Id, s.Name })
            .ToDictionaryAsync(s => s.Id, s => s.Name);
    }

    private static object ToDto(StudentPrediction p, Dictionary<int, string>? names = null) => new
    {
        studentId = p.StudentId,
        name = names != null ? names.GetValueOrDefault(p.StudentId, "") : null,
        groupId = p.GroupId,
        predicted = p.Predicted,
        confidence = p.Confidence,
        risk = p.Risk,
        level = p.Level,
        reasons = p.Reasons,
        average = p.Average,
        slope = p.Slope,
        attendanceRate = p.AttendanceRate,
        samples = p.Samples,
        missed = p.Missed,
        inactiveDays = p.InactiveDays,
        recent = p.Recent,
    };

    // ═════════════════════════ TEACHER ═════════════════════════

    [HttpGet("summary")]
    [Authorize(Roles = StaffRoles)]
    public async Task<IActionResult> Summary([FromQuery] int? groupId)
    {
        var all = await ComputeCachedAsync(groupId);
        var withData = all.Where(p => p.Samples > 0).ToList();
        return Ok(new
        {
            students = all.Count,
            high = all.Count(p => p.Level == "high"),
            medium = all.Count(p => p.Level == "medium"),
            low = all.Count(p => p.Level == "low"),
            noData = all.Count(p => p.Level == "nodata"),
            averagePredicted = withData.Count == 0 ? (double?)null : Math.Round(withData.Where(p => p.Predicted.HasValue).Average(p => p.Predicted!.Value), 1),
        });
    }

    [HttpGet("at-risk")]
    [Authorize(Roles = StaffRoles)]
    public async Task<IActionResult> AtRisk([FromQuery] int? groupId, [FromQuery] string? level, [FromQuery] int p = 1)
    {
        p = Math.Max(1, p);
        var all = await ComputeCachedAsync(groupId);

        IEnumerable<StudentPrediction> filtered = all;
        if (!string.IsNullOrWhiteSpace(level) && !level.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var l = level.Trim().ToLowerInvariant();
            filtered = filtered.Where(x => x.Level == l);
        }
        else
        {
            // Default view = the ones that need attention.
            filtered = filtered.Where(x => x.Level == "high" || x.Level == "medium");
        }

        var page = filtered.OrderByDescending(x => x.Risk).ThenBy(x => x.Predicted ?? 0)
            .Skip((p - 1) * PagingDefaults.PageSize).Take(PagingDefaults.PageSize).ToList();
        var names = await NamesAsync(page.Select(x => x.StudentId));
        return Ok(page.Select(x => ToDto(x, names)));
    }

    [HttpGet("student/{studentId:int}")]
    [Authorize(Roles = StaffRoles)]
    public async Task<IActionResult> Student(int studentId)
    {
        var groups = await StudentGroupsAsync(null);
        if (!groups.TryGetValue(studentId, out var gid))
            return NotFound(new { message = "Student not found." });

        var one = await PredictionEngine.ComputeAsync(_db, new Dictionary<int, int> { [studentId] = gid });
        var names = await NamesAsync(new[] { studentId });
        return Ok(ToDto(one.First(), names));
    }

    // ═════════════════════════ STUDENT ═════════════════════════

    [HttpGet("me")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Me([FromQuery] int target = 90)
    {
        target = Math.Clamp(target, 50, 100);
        var me = User.GetUserId();
        var gid = User.GetGroupId(_tenant.CurrentTenantId) ?? 0;

        var p = (await PredictionEngine.ComputeAsync(_db, new Dictionary<int, int> { [me] = gid })).First();

        // "What do I need in the next 3 assessments to reach my target average?"
        const int next = 3;
        double? needed = null;
        bool? achievable = null;
        if (p.Average.HasValue && p.Samples > 0)
        {
            var sum = p.Average.Value * p.Samples;
            needed = Math.Round((target * (p.Samples + next) - sum) / next, 1);
            achievable = needed <= 100;
            if (needed < 0) needed = 0;
        }

        string status = p.Predicted switch
        {
            null => "no_data",
            >= 85 => "excellent",
            >= 70 => "on_track",
            >= 50 => "needs_attention",
            _ => "at_risk",
        };

        string message = status switch
        {
            "no_data" => "لسه مفيش نتايج كفاية نتوقع منها. حل أول امتحان وهتظهر توقعاتك.",
            "excellent" => "مستواك ممتاز، كمّل على نفس النظام 💪",
            "on_track" => "ماشي كويس. شوية تركيز كمان وهتوصل للي عايزه.",
            "needs_attention" => "محتاج تشد حيلك شوية. ابدأ بمراجعة أخطائك وبنك الأسئلة.",
            _ => "مستواك محتاج دعم دلوقتي. راجع الشرح واتدرب كل يوم، وكلّم مدرسك لو محتاج مساعدة.",
        };

        return Ok(new
        {
            predicted = p.Predicted,
            confidence = p.Confidence,
            status,
            message,
            average = p.Average,
            trend = p.Slope is null ? "unknown" : p.Slope > 1 ? "up" : p.Slope < -1 ? "down" : "flat",
            attendanceRate = p.AttendanceRate,
            recent = p.Recent,
            target,
            neededAverageInNext3 = needed,
            achievable,
        });
    }
}
