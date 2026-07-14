using System.Net;
using System.Text.Json;
using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Middleware;

/// <summary>
/// SUPERADMIN CONTROL: enforces Teacher.IsSuspended on every authenticated
/// request, not just at login. A JWT stays valid for up to Jwt:AccessTokenMinutes
/// after issuance, so without this a teacher/staff/student the SuperAdmin just
/// suspended could keep using the API until their token naturally expired.
///
/// Runs AFTER app.UseAuthentication() (needs context.User populated) and BEFORE
/// app.UseAuthorization(), so a suspended tenant's request is rejected with a
/// clean 403 before it ever reaches a controller action -- no per-controller
/// checks needed, same "one place, applies everywhere" pattern as the tenant
/// query filters in AppDbContext.
///
/// SuperAdmin itself is never blocked (it owns no tenant to suspend). Resolves
/// the SAME effective tenant id as HttpTenantContext:
///  - Teacher/Assistant/AssistantAdmin -> the "tenantId" JWT claim.
///  - Student -> the X-TenantId header, validated against their own "groupIds"
///    claim (a student can't be blocked by forging a header for a teacher they
///    aren't actually linked to).
/// The suspension flag itself always lives on the TENANT-ROOT teacher row
/// (TenantOwnerId == null); staff share their root's suspension automatically
/// since the "tenantId" claim IS the root's id for staff accounts too.
/// </summary>
public class TenantSuspensionMiddleware
{
    private readonly RequestDelegate _next;

    public TenantSuspensionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        var user = context.User;

        if (user?.Identity?.IsAuthenticated == true && !user.IsInRole(Roles.SuperAdmin))
        {
            int? tenantIdToCheck = null;

            if (user.IsInAnyRole(Roles.Teacher, Roles.AssistantAdmin, Roles.Assistant))
            {
                tenantIdToCheck = user.GetStaffTenantId();
            }
            else if (user.IsInRole(Roles.Student))
            {
                var header = context.Request.Headers["X-TenantId"].ToString();
                if (int.TryParse(header, out var fromHeader) && user.GetTeacherIds().Contains(fromHeader))
                    tenantIdToCheck = fromHeader;
            }

            if (tenantIdToCheck.HasValue)
            {
                // Teacher has no global query filter, so a plain query already
                // reaches across tenants -- no IgnoreQueryFilters() needed.
                var isSuspended = await db.Teachers.AsNoTracking()
                    .Where(t => t.Id == tenantIdToCheck.Value)
                    .Select(t => t.IsSuspended)
                    .FirstOrDefaultAsync();

                if (isSuspended)
                {
                    context.Response.ContentType = "application/json";
                    context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        message = "This teacher's account has been suspended by the administrator. No requests can be processed until it's reactivated."
                    }));
                    return;
                }
            }
        }

        await _next(context);
    }
}
