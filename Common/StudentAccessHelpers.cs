using System.Security.Claims;
using EduApi.Data;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Common;

/// <summary>
/// User.GetUnitIds() only reflects the "unitIds" JWT claim, which is a
/// SNAPSHOT taken at login/refresh (see the doc comment on GetUnitIds in
/// ClaimsExtensions.cs). If a teacher subscribes a student to a Unit while
/// that student's access token is still valid, the student won't see the
/// new access until they log out/in or the token refreshes -- which reads
/// to the student like the subscription "didn't work".
///
/// This helper closes that gap: it always unions the JWT snapshot with a
/// live read of StudentUnitSubscriptions, so a fresh subscription is
/// visible on the student's very next request, no re-login required.
/// Every student-facing authorization check that used to call
/// User.GetUnitIds() alone should go through this instead.
/// </summary>
public static class StudentAccessHelpers
{
    public static async Task<HashSet<int>> GetEffectiveUnitIdsAsync(AppDbContext db, ClaimsPrincipal user, int studentId)
    {
        var unitIds = user.GetUnitIds().ToHashSet();
        var liveUnitIds = await db.StudentUnitSubscriptions.AsNoTracking()
            .Where(s => s.StudentId == studentId)
            .Select(s => s.UnitId)
            .ToListAsync();
        unitIds.UnionWith(liveUnitIds);
        return unitIds;
    }

    /// <summary>
    /// External Book ids the given student can currently reach: a direct
    /// redeemed-code subscription, plus any book whose optional UnitId is
    /// one the student is subscribed to (effective unit ids, JWT snapshot
    /// unioned with a live read -- see GetEffectiveUnitIdsAsync above).
    /// Mirrors LecturesController.GetAccessibleExternalBookIdsAsync /
    /// ExternalBooksController.IsSubscribedAsync so every place that gates
    /// access on ExternalBookId agrees on the same rule.
    /// </summary>
    public static async Task<HashSet<int>> GetEffectiveExternalBookIdsAsync(AppDbContext db, ClaimsPrincipal user, int studentId)
    {
        var subscribedUnitIds = await GetEffectiveUnitIdsAsync(db, user, studentId);

        var directIds = await db.StudentExternalBookSubscriptions.AsNoTracking()
            .Where(s => s.StudentId == studentId).Select(s => s.ExternalBookId).ToListAsync();
        var viaUnitIds = await db.ExternalBooks.AsNoTracking()
            .Where(e => e.UnitId != null && subscribedUnitIds.Contains(e.UnitId.Value))
            .Select(e => e.Id).ToListAsync();

        var result = directIds.ToHashSet();
        result.UnionWith(viaUnitIds);
        return result;
    }
}
