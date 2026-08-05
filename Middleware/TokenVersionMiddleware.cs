using System.Net;
using System.Text.Json;
using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Middleware;

/// <summary>
/// REVOCATION: enforces the "tokenVersion" JWT claim (snapshotted at
/// issuance, see TokenService.CreateAccessToken) against the account's
/// CURRENT Teacher.TokenVersion / Student.TokenVersion on every authenticated
/// request. A JWT stays cryptographically valid for up to Jwt:AccessTokenMinutes
/// after issuance no matter what — this is the only way to force an
/// already-issued access token to stop working before it naturally expires
/// (password reset, "log out everywhere", suspected compromise, etc.).
///
/// Runs AFTER app.UseAuthentication() (needs context.User populated) and
/// BEFORE app.UseAuthorization() — same slot as TenantSuspensionMiddleware,
/// so a stale token is rejected with a clean 401 before it ever reaches a
/// controller action. SuperAdmin is included (unlike TenantSuspensionMiddleware)
/// since a SuperAdmin account can itself be the one that needs revoking.
/// </summary>
public class TokenVersionMiddleware
{
    private readonly RequestDelegate _next;

    public TokenVersionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        var user = context.User;

        if (user?.Identity?.IsAuthenticated == true)
        {
            var userId = user.GetUserId();
            var tokenVersionClaim = user.FindFirst("tokenVersion")?.Value;

            if (userId != 0 && int.TryParse(tokenVersionClaim, out var tokenVersionInJwt))
            {
                var isStudent = user.IsInRole(Roles.Student);

                var currentVersion = isStudent
                    ? await db.Students.IgnoreQueryFilters().AsNoTracking()
                        .Where(s => s.Id == userId).Select(s => (int?)s.TokenVersion).FirstOrDefaultAsync()
                    : await db.Teachers.AsNoTracking()
                        .Where(t => t.Id == userId).Select(t => (int?)t.TokenVersion).FirstOrDefaultAsync();

                // currentVersion == null means the account no longer exists
                // (deleted) — reject just like a version mismatch would.
                if (currentVersion == null || currentVersion.Value != tokenVersionInJwt)
                {
                    context.Response.ContentType = "application/json";
                    context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        message = "This session is no longer valid. Please log in again."
                    }));
                    return;
                }
            }
        }

        await _next(context);
    }
}
