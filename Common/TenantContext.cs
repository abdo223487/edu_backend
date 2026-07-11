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
            // MULTI-TENANT SECURITY FIX: this header is fully client-controlled, so
            // it must be validated against the student's OWN teacherIds (from the
            // "groupIds" JWT claim, a snapshot of their real StudentGroupMembership
            // rows) before being trusted. A student can no longer read another
            // teacher's data just by sending an arbitrary X-TenantId — if the
            // header doesn't match one of their real memberships, the tenant
            // resolves to null and every tenant-scoped query filter matches zero
            // rows (the existing safe default).
            var header = httpContext!.Request.Headers["X-TenantId"].ToString();
            var parsed = int.TryParse(header, out var fromHeader) ? fromHeader : (int?)null;
            _tenantId = parsed.HasValue && user.GetTeacherIds().Contains(parsed.Value)
                ? parsed
                : null;
        }
        else
        {
            // Teacher / Assistant / AssistantAdmin: tenant is embedded in the JWT.
            _tenantId = user.GetStaffTenantId();
        }
    }

    public int? CurrentTenantId => _tenantId;
}
