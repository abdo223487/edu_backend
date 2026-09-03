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
}
