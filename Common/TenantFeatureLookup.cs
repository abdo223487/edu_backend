using System.Collections.Concurrent;
using EduApi.Data;
using EduApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Common;

/// <summary>
/// "Which dashboard features are switched OFF for this tenant (teacher)?" for code that is
/// NOT the teacher himself — i.e. the student app and the endpoints it calls.
/// Same rule as GET Teachers/me/features: the tenant root's plan decides (plan - swapped out +
/// swapped in); a teacher without a plan uses the SuperAdmin's manual list.
/// Answers are cached for a few seconds so a busy endpoint doesn't hit the DB on every call.
/// </summary>
public static class TenantFeatureLookup
{
    private sealed record Entry(string[] Disabled, DateTime Expires);

    private static readonly ConcurrentDictionary<int, Entry> Cache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public static async Task<string[]> GetDisabledAsync(AppDbContext db, int tenantId, CancellationToken ct = default)
    {
        if (Cache.TryGetValue(tenantId, out var hit) && hit.Expires > DateTime.UtcNow)
            return hit.Disabled;

        var teacher = await db.Teachers.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Id, t.TenantOwnerId })
            .FirstOrDefaultAsync(ct);

        string[] disabled;
        if (teacher == null)
        {
            // Unknown tenant -> nothing student-facing is enabled.
            disabled = TeacherFeatures.StudentFacing.ToArray();
        }
        else
        {
            var rootId = teacher.TenantOwnerId ?? teacher.Id;
            var root = await db.Teachers.AsNoTracking()
                .Where(t => t.Id == rootId)
                .Select(t => new { t.DisabledFeatures, t.PlanId, t.SwapIn, t.SwapOut })
                .FirstOrDefaultAsync(ct);

            FeaturePlan? plan = null;
            if (root?.PlanId != null)
                plan = await db.FeaturePlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == root.PlanId, ct);

            disabled = TeacherFeatures.EffectiveDisabled(root?.DisabledFeatures, plan, root?.SwapIn, root?.SwapOut);
        }

        Cache[tenantId] = new Entry(disabled, DateTime.UtcNow + Ttl);
        return disabled;
    }

    public static async Task<bool> IsEnabledAsync(AppDbContext db, int tenantId, string key, CancellationToken ct = default)
        => !(await GetDisabledAsync(db, tenantId, ct)).Contains(key);
}
