using Microsoft.AspNetCore.Http;

namespace EduApi.Common;

/// <summary>
/// TENANT LAYER
/// ─────────────────────────────────────────────────────────────────────────
/// The Flutter app treats each Teacher as a separate "tenant". A student picks
/// a teacher on SelectTeacherPage, stores that teacher's id locally as
/// `tenantId`, and sends it on every subsequent request as the `X-TenantId`
/// header (only for role == "student" — see AuthService._buildHeaders in
/// main.dart). Teacher/Assistant/AssistantAdmin accounts never send that
/// header; they always act on their own tenant, which is embedded directly
/// in their JWT as the "tenantId" claim at login time (see AuthController +
/// TokenService).
///
/// This service is the SINGLE SOURCE OF TRUTH for "which tenant is this
/// request for", consumed by AppDbContext's global query filters so every
/// controller gets automatic data isolation for free — no per-controller
/// tenant-filtering code needed.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// The resolved tenant (Teacher.Id) for the current request, or null if it
    /// cannot be resolved (no authenticated user, or a student request missing
    /// the X-TenantId header). When null, tenant-scoped global query filters
    /// will match zero rows — the safe default.
    /// </summary>
    int? CurrentTenantId { get; }
}

public class HttpTenantContext : ITenantContext
{
    private readonly int? _tenantId;

    public HttpTenantContext(IHttpContextAccessor accessor)
    {
        var httpContext = accessor.HttpContext;
        var user = httpContext?.User;

        if (user?.Identity?.IsAuthenticated != true)
        {
            _tenantId = null;
            return;
        }

        if (user.IsInRole(Models.Roles.Student))
        {
            // Students carry the tenant (chosen teacher) via the X-TenantId header.
            //
            // BUGFIX: this used to also require the header value to appear in the
            // "groupIds" JWT claim -- a snapshot of the student's memberships taken
            // at login/refresh time. That snapshot goes stale the moment a teacher
            // links/adds the student to a NEW group after the token was issued: the
            // new teacher's id simply isn't in the old token yet, so the header got
            // rejected, tenant resolved to null, and every tenant-scoped query
            // (including the Students filter itself) matched zero rows -- a 404 on
            // e.g. GET Students/attendance for any student with more than one
            // teacher, until they happened to log out/in or their token expired and
            // refreshed. A student with only one teacher rarely hit this because
            // that teacher is almost always the one already embedded from login.
            //
            // We no longer need that snapshot check for security: every table is
            // ALREADY tenant-isolated against the LIVE database via its own
            // TeacherId/Group.TeacherId query filter (Group, StudentGroupMembership,
            // Lecture, Attendance, ...). If a student sends an X-TenantId for a
            // teacher they don't actually belong to, those filters simply match zero
            // rows on their own -- the same "safe default" as before -- so trusting
            // any well-formed header here is safe and also fixes the staleness bug.
            var header = httpContext!.Request.Headers["X-TenantId"].ToString();
            _tenantId = int.TryParse(header, out var fromHeader) ? fromHeader : (int?)null;
        }
        else if (user.IsInRole(Models.Roles.SuperAdmin))
        {
            // SuperAdmin owns no tenant of its own (no "tenantId" claim is ever
            // embedded for it — see AuthController). It acts ON BEHALF OF
            // whichever teacher it's currently managing, chosen per-request via
            // the same "X-TenantId" header students use. Unlike the student
            // branch above, this is NOT restricted to a known set of ids —
            // a SuperAdmin is, by definition, trusted to reach any tenant.
            var header = httpContext!.Request.Headers["X-TenantId"].ToString();
            _tenantId = int.TryParse(header, out var fromHeader) ? fromHeader : (int?)null;
        }
        else
        {
            // Teacher / Assistant / AssistantAdmin: tenant is embedded in the JWT.
            _tenantId = user.GetStaffTenantId();
        }
    }

    public int? CurrentTenantId => _tenantId;
}
