using System.Security.Claims;

namespace EduApi.Common;

public static class ClaimsExtensions
{
    public static int GetUserId(this ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(sub, out var id) ? id : 0;
    }

    /// <summary>
    /// Legacy single-tenant lookup — kept only for staff tokens / old callers.
    /// Students are now multi-tenant: use GetGroupId(tenantId) instead, which
    /// resolves the RIGHT group for whichever teacher the request is for.
    /// </summary>
    public static int? GetGroupId(this ClaimsPrincipal user)
    {
        var v = user.FindFirstValue("groupId");
        return int.TryParse(v, out var id) ? id : null;
    }

    /// <summary>
    /// MULTI-TENANT: student-only. Parses the "groupIds" claim, a snapshot of
    /// "teacherId:groupId" pairs (comma-separated) taken from the student's
    /// StudentGroupMembership rows at login/refresh, and returns the GroupId
    /// that matches the given tenant (the teacher the current request is for,
    /// i.e. ITenantContext.CurrentTenantId). Returns null if the student has no
    /// group under that tenant, or if tenantId is null.
    /// </summary>
    public static int? GetGroupId(this ClaimsPrincipal user, int? tenantId)
    {
        if (!tenantId.HasValue) return null;

        var raw = user.FindFirstValue("groupIds");
        if (string.IsNullOrEmpty(raw)) return null;

        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var teacherId)
                && int.TryParse(parts[1], out var groupId)
                && teacherId == tenantId.Value)
            {
                return groupId;
            }
        }

        return null;
    }

    /// <summary>
    /// MULTI-TENANT: student-only. All TeacherIds the student currently has a
    /// group membership under (snapshotted at login/refresh, same "groupIds"
    /// claim as GetGroupId(tenantId)). Used by HttpTenantContext to validate
    /// an incoming X-TenantId header actually belongs to this student, instead
    /// of trusting it blindly.
    /// </summary>
    public static List<int> GetTeacherIds(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue("groupIds");
        if (string.IsNullOrEmpty(raw)) return new List<int>();

        var result = new List<int>();
        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var teacherId))
                result.Add(teacherId);
        }
        return result;
    }

    public static int? GetSchoolYear(this ClaimsPrincipal user)
    {
        var v = user.FindFirstValue("schoolYear");
        return int.TryParse(v, out var y) ? y : null;
    }

    /// <summary>
    /// TENANT LAYER: the effective tenant id embedded in staff (Teacher/Assistant/
    /// AssistantAdmin) JWTs as the "tenantId" claim (= TenantOwnerId ?? own Id,
    /// computed once at login/refresh). Students do NOT carry this claim — their
    /// tenant comes from the X-TenantId header instead (see ITenantContext).
    /// </summary>
    public static int? GetStaffTenantId(this ClaimsPrincipal user)
    {
        var v = user.FindFirstValue("tenantId");
        return int.TryParse(v, out var id) ? id : null;
    }

    /// <summary>
    /// Student-only: the UnitIds from their StudentUnitSubscription rows, snapshotted
    /// into the "unitIds" claim at login/refresh (comma-separated). Empty list if the
    /// claim is absent (student not subscribed to anything, or a non-student token).
    /// NOTE: this is a snapshot — if a teacher subscribes/unsubscribes this student
    /// to a unit while their access token is still valid (up to Jwt:AccessTokenMinutes),
    /// they won't see the change until they refresh or log in again. Same tradeoff
    /// already accepted for groupId/schoolYear.
    /// </summary>
    public static List<int> GetUnitIds(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue("unitIds");
        if (string.IsNullOrEmpty(raw)) return new List<int>();

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var v) ? v : (int?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();
    }

    public static bool IsInAnyRole(this ClaimsPrincipal user, params string[] roles)
        => roles.Any(user.IsInRole);
}
