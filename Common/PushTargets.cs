using EduApi.Data;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Common;

/// <summary>
/// Resolves "which students should get this push" OUTSIDE of the tenant query
/// filters (the push job has no HTTP request, hence no tenant), so every query
/// here uses IgnoreQueryFilters() and scopes itself by explicit group / unit ids.
/// Suspended or cancelled memberships never receive pushes.
/// </summary>
public static class PushTargets
{
    public static async Task<List<int>> StudentsInGroupsAsync(
        AppDbContext db, IReadOnlyCollection<int> groupIds, IReadOnlyCollection<int>? unitIds = null, CancellationToken ct = default)
    {
        if (groupIds.Count == 0) return new List<int>();

        var viaMembership = await db.StudentGroupMemberships.IgnoreQueryFilters().AsNoTracking()
            .Where(m => groupIds.Contains(m.GroupId) && !m.IsSuspended && !m.IsCancelled)
            .Select(m => m.StudentId)
            .ToListAsync(ct);

        // Legacy single-group students (created before memberships existed).
        var viaLegacy = await db.Students.IgnoreQueryFilters().AsNoTracking()
            .Where(s => groupIds.Contains(s.GroupId))
            .Select(s => s.Id)
            .ToListAsync(ct);

        var ids = viaMembership.Concat(viaLegacy).Distinct().ToList();

        if (unitIds is { Count: > 0 } && ids.Count > 0)
        {
            var subscribed = await db.StudentUnitSubscriptions.IgnoreQueryFilters().AsNoTracking()
                .Where(s => unitIds.Contains(s.UnitId) && ids.Contains(s.StudentId))
                .Select(s => s.StudentId)
                .ToListAsync(ct);
            ids = subscribed.Distinct().ToList();
        }

        return ids;
    }

    /// <summary>Cairo local time (the platform's audience); falls back to UTC+2 if tz data is missing.</summary>
    public static DateTime ToCairo(DateTime utc)
    {
        try
        {
            TimeZoneInfo tz;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo"); }
            catch { tz = TimeZoneInfo.FindSystemTimeZoneById("Egypt Standard Time"); }
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        }
        catch
        {
            return utc.AddHours(2);
        }
    }
}
