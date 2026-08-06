using System.Net;
using System.Text.Json;

namespace EduApi.Middleware;

public class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;

    public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception while processing {Path}", context.Request.Path);

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

            var payload = JsonSerializer.Serialize(new
            {
                message = "An unexpected error occurred.",
                detail = context.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment()
                    ? ex.Message
                    : null
            });

            // SUPERADMIN LOGS FEATURE: persist this 500 so it shows up in the
            // SuperAdmin's Logs screen (see RequestLoggingMiddleware, which
            // handles every OTHER status code -- this is the one exception
            // path it never sees, since the exception propagates past it).
            var db = context.RequestServices.GetService<EduApi.Data.AppDbContext>();
            if (db != null)
            {
                await EduApi.Middleware.RequestLoggingMiddleware.LogAsync(
                    context, db, context.Response.StatusCode, ex.Message);
            }

            await context.Response.WriteAsync(payload);
        }
    }
}
