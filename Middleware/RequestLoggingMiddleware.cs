using System.Security.Claims;
using System.Text.Json;
using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.Extensions.DependencyInjection;

namespace EduApi.Middleware;

/// <summary>
/// SUPERADMIN LOGS FEATURE: records every response with StatusCode >= 400
/// into RequestErrorLog (path/method/status/who/when + a best-effort reason
/// message), so a SuperAdmin can see "what's been going wrong" per teacher
/// without needing external log tooling (see LogsController).
///
/// Exceptions (-> 500) are NOT logged here: they propagate up past this
/// middleware to the outer ExceptionMiddleware, which logs them itself
/// (it already has the exception object and its message for free). This
/// middleware only ever sees non-exception status codes (400, 401, 403,
/// 404, validation errors, deliberate 5xx from a controller, etc).
///
/// Placement matters: registered AFTER UseAuthentication/TokenVersionMiddleware/
/// TenantSuspensionMiddleware (so context.User is populated) but BEFORE
/// UseAuthorization (so 401/403 responses -- which short-circuit inside the
/// built-in authorization middleware -- still pass back through this one on
/// their way out and get logged too).
///
/// TRADE-OFF: the response body is buffered into a MemoryStream for every
/// request so we can inspect it after the fact if the status turns out to be
/// an error. For a small/medium tutoring-center-scale API this is an
/// acceptable memory cost; if large file-download endpoints ever grow much
/// bigger, exclude their paths here.
/// </summary>
public class RequestLoggingMiddleware
{
    private const int MaxMessageLength = 500;
    private readonly RequestDelegate _next;

    public RequestLoggingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);

            if (context.Response.StatusCode >= 400)
            {
                buffer.Seek(0, SeekOrigin.Begin);
                var bodyText = await new StreamReader(buffer).ReadToEndAsync();
                await LogAsync(context, db, context.Response.StatusCode, ExtractMessage(bodyText));
            }
        }
        finally
        {
            buffer.Seek(0, SeekOrigin.Begin);
            await buffer.CopyToAsync(originalBody);
            context.Response.Body = originalBody;
        }
    }

    private static string? ExtractMessage(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText)) return null;

        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    return Truncate(m.GetString());
                // ASP.NET's built-in [ApiController] validation problem-details shape.
                if (doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                    return Truncate(t.GetString());
            }
        }
        catch (JsonException)
        {
            // Not JSON -- fall through and store the raw (truncated) text.
        }

        return Truncate(bodyText);
    }

    private static string? Truncate(string? s)
        => string.IsNullOrEmpty(s) ? s : (s.Length > MaxMessageLength ? s[..MaxMessageLength] : s);

    /// <summary>Shared by both this middleware (4xx) and ExceptionMiddleware (5xx).
    /// Never throws -- logging must never break the actual request/response.
    ///
    /// BUGFIX: this used to reuse the SAME AppDbContext instance that was
    /// active when the error happened. For a 500 caused by SaveChangesAsync
    /// itself failing (e.g. a DB constraint violation like deleting a Lecture
    /// that still has Materials pointing at it), Npgsql leaves that
    /// connection's transaction in an "aborted" state -- Postgres refuses ANY
    /// further command on it until a ROLLBACK. Reusing that same (poisoned)
    /// context to then INSERT the RequestErrorLog row silently failed too
    /// (swallowed by the catch below), so the very errors this feature most
    /// needs to surface -- ones from failed writes -- never made it into the
    /// SuperAdmin's Logs screen at all.
    ///
    /// Fix: always log through a BRAND NEW scope/DbContext, completely
    /// independent of whatever connection/transaction state the original
    /// request left behind.</summary>
    public static async Task LogAsync(HttpContext context, AppDbContext _, int statusCode, string? message)
    {
        try
        {
            var user = context.User;
            var role = "Anonymous";
            int? userId = null;

            if (user?.Identity?.IsAuthenticated == true)
            {
                role = user.FindFirstValue(ClaimTypes.Role) ?? "Unknown";
                userId = user.GetUserId();
            }

            // Fresh scope -> fresh ITenantContext -> fresh AppDbContext, all
            // on a brand new connection. Deliberately NOT the AppDbContext
            // passed in by the caller (see the BUGFIX note above) or its
            // ITenantContext -- either could belong to the same broken scope.
            using var scope = context.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var freshDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tenant = scope.ServiceProvider.GetService<ITenantContext>();

            freshDb.RequestErrorLogs.Add(new RequestErrorLog
            {
                TenantId = tenant?.CurrentTenantId,
                Role = role,
                UserId = userId,
                Method = context.Request.Method,
                Path = context.Request.Path.Value ?? "",
                StatusCode = statusCode,
                Message = message,
                CreatedAtUtc = DateTime.UtcNow
            });
            await freshDb.SaveChangesAsync();
        }
        catch
        {
            // Best-effort only -- never let logging take down the real response.
        }
    }
}
