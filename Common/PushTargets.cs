using System.Globalization;
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

    /// <summary>
    /// Who should hear about something that belongs to ONE lecture (a new video, a lecture exam,
    /// a lecture assignment), whatever the lecture lives inside:
    ///   - a Unit (a course, or a lesson inside a course) -> in one of the lecture's groups AND subscribed to the unit
    ///   - an External Book (or a lesson inside it)       -> in one of the lecture's groups AND able to reach the book
    ///                                                       (direct code subscription, or subscribed to the book's unit)
    ///   - an Online Lesson                               -> students who redeemed a code for that online lesson
    /// plus anyone who unlocked this single lecture with a code.
    /// Same rules as LectureAccess / LecturesController.ByGroup, just computed for ALL students at once.
    /// </summary>
    public static async Task<List<int>> StudentsOfLectureAsync(AppDbContext db, int lectureId, CancellationToken ct = default)
    {
        var l = await db.Lectures.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == lectureId)
            .Select(x => new { x.UnitId, x.ExternalBookId, x.OnlineLessonId })
            .FirstOrDefaultAsync(ct);
        if (l == null) return new List<int>();

        var direct = await db.StudentLectureUnlocks.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.LectureId == lectureId)
            .Select(u => u.StudentId)
            .ToListAsync(ct);

        List<int> ids;
        if (l.OnlineLessonId.HasValue)
        {
            var onlineLessonId = l.OnlineLessonId.Value;
            ids = await db.StudentOnlineLessonUnlocks.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.OnlineLessonId == onlineLessonId)
                .Select(u => u.StudentId)
                .Distinct()
                .ToListAsync(ct);
        }
        else
        {
            var groupIds = await db.LectureGroupLinks.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.LectureId == lectureId)
                .Select(x => x.GroupId)
                .ToListAsync(ct);

            if (l.UnitId.HasValue)
            {
                ids = await StudentsInGroupsAsync(db, groupIds, new[] { l.UnitId.Value }, ct);
            }
            else if (l.ExternalBookId.HasValue)
            {
                var inGroups = await StudentsInGroupsAsync(db, groupIds, null, ct);
                ids = await WhoCanReachExternalBookAsync(db, inGroups, l.ExternalBookId.Value, ct);
            }
            else
            {
                ids = new List<int>();
            }
        }

        return ids.Concat(direct).Distinct().ToList();
    }

    /// <summary>
    /// Keeps only the students who can open the given external book: a direct (redeemed-code)
    /// subscription, or a subscription to the book's linked Unit -- the same rule as
    /// StudentAccessHelpers.GetEffectiveExternalBookIdsAsync.
    /// </summary>
    public static async Task<List<int>> WhoCanReachExternalBookAsync(
        AppDbContext db, IReadOnlyCollection<int> studentIds, int externalBookId, CancellationToken ct = default)
    {
        if (studentIds.Count == 0) return new List<int>();

        var direct = await db.StudentExternalBookSubscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.ExternalBookId == externalBookId && studentIds.Contains(s.StudentId))
            .Select(s => s.StudentId)
            .ToListAsync(ct);

        var bookUnitId = await db.ExternalBooks.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.Id == externalBookId)
            .Select(b => b.UnitId)
            .FirstOrDefaultAsync(ct);

        var viaUnit = new List<int>();
        if (bookUnitId.HasValue)
        {
            var unitId = bookUnitId.Value;
            viaUnit = await db.StudentUnitSubscriptions.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.UnitId == unitId && studentIds.Contains(s.StudentId))
                .Select(s => s.StudentId)
                .ToListAsync(ct);
        }

        return direct.Concat(viaUnit).Distinct().ToList();
    }

    /// <summary>"5:30 م" -- Cairo time of a UTC instant, 12-hour with Arabic ص/م.</summary>
    public static string CairoTimeAr(DateTime utc)
    {
        var c = ToCairo(utc);
        var h = c.Hour % 12;
        if (h == 0) h = 12;
        return $"{h}:{c.Minute:00} {(c.Hour >= 12 ? "م" : "ص")}";
    }

    /// <summary>"29/10 الساعة 5:30 م" -- Cairo date + time of a UTC instant.</summary>
    public static string CairoDateTimeAr(DateTime utc)
    {
        var c = ToCairo(utc);
        return $"{c.ToString("dd/MM", CultureInfo.InvariantCulture)} الساعة {CairoTimeAr(utc)}";
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
