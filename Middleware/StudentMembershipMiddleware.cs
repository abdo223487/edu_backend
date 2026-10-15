using System.Net;
using System.Text.Json;
using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Middleware;

/// <summary>
/// PER-TENANT STUDENT ACCESS: enforces, on EVERY authenticated student request,
/// that the student's relationship with the teacher the request is for
/// (X-TenantId) has not been cancelled ("deleted") or suspended by that
/// teacher.
///
/// WHY THIS EXISTS: cancel/suspend lives on StudentGroupMembership (one row
/// per student/teacher), so it is a per-teacher decision. At login/refresh it
/// is already respected (AuthController.GetGroupMembershipsAsync leaves such
/// teachers out of the "groupIds" snapshot), but HttpTenantContext no longer
/// validates the X-TenantId header against that stale snapshot (it trusts the
/// header and relies on the tenant query filters instead). Result: a student
/// shared between two teachers who got deleted by teacher A kept using A's
/// content normally -- the app still had A's id stored as tenantId and a still
/// valid access token, and nothing checked the LIVE membership row.
///
/// This middleware closes that gap with one cheap indexed query. It only
/// BLOCKS when the student has membership row(s) under that teacher and NONE
/// of them is active. It deliberately lets the request through when there is
/// no membership row at all (legacy students who predate memberships, or a
/// student tied to the teacher only through a redeemed code), exactly like
/// AuthController does at login -- so nothing that works today breaks.
///
/// Runs AFTER UseAuthentication (needs context.User) and BEFORE
/// UseAuthorization, right after TenantSuspensionMiddleware. The 403 body
/// carries code = "TENANT_ACCESS_REVOKED" so the Flutter app can clear its
/// stored tenantId and send the student back to the teacher-selection screen.
/// </summary>
public class StudentMembershipMiddleware
{
    private readonly RequestDelegate _next;

    // Endpoints that must keep working even with a stale/revoked X-TenantId,
    // otherwise the student could never reach teacher selection or re-login.
    private static readonly string[] ExemptPaths =
    {
        "/api/auth",
        "/api/appversions",
        "/api/students/me",
    };

    public StudentMembershipMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    private static bool IsExempt(HttpRequest request)
    {
        var path = (request.Path.Value ?? string.Empty).TrimEnd('/');

        foreach (var p in ExemptPaths)
        {
            if (path.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // GET api/Teachers = the teacher-selection list; must stay reachable.
        if (HttpMethods.IsGet(request.Method) &&
            path.Equals("/api/teachers", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        var user = context.User;

        if (user?.Identity?.IsAuthenticated == true &&
            user.IsInRole(Roles.Student) &&
            !IsExempt(context.Request))
        {
            var studentId = user.GetUserId();
            var header = context.Request.Headers["X-TenantId"].ToString();

            if (studentId != 0 && int.TryParse(header, out var tenantId))
            {
                // IgnoreQueryFilters(): we resolve the tenant ourselves from the
                // header, and the membership filter would otherwise depend on
                // the very tenant context we're validating.
                var rows = await db.StudentGroupMemberships.IgnoreQueryFilters().AsNoTracking()
                    .Where(m => m.StudentId == studentId && m.Group!.TeacherId == tenantId)
                    .Select(m => new { m.IsSuspended, m.IsCancelled })
                    .ToListAsync();

                if (rows.Count > 0 && !rows.Any(r => !r.IsSuspended && !r.IsCancelled))
                {
                    var cancelled = rows.All(r => r.IsCancelled);

                    context.Response.ContentType = "application/json; charset=utf-8";
                    context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        code = "TENANT_ACCESS_REVOKED",
                        status = cancelled ? "cancelled" : "suspended",
                        teacherId = tenantId,
                        message = cancelled
                            ? "تم إلغاء حسابك مع هذا المدرس. تواصل مع المدرس لإعادة تفعيله."
                            : "تم إيقاف حسابك مع هذا المدرس. تواصل مع المدرس لإعادة تفعيله."
                    }));
                    return;
                }
            }
        }

        await _next(context);
    }
}
