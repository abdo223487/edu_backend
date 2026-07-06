using EduApi.Auth;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/Auth  (matches Flutter: "$baseUrl/Auth/login", "$baseUrl/Auth/refresh")
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TokenService _tokens;
    private readonly IConfiguration _config;

    public AuthController(AppDbContext db, TokenService tokens, IConfiguration config)
    {
        _db = db;
        _tokens = tokens;
        _config = config;
    }

    // POST api/Auth/login
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { message = "Username and password are required." });

        // Try teacher / assistant admin accounts first.
        // NOTE: Teachers table is never tenant-filtered (staff accounts are global),
        // so no IgnoreQueryFilters() needed here.
        var teacher = await _db.Teachers.FirstOrDefaultAsync(t => t.UserName == request.Username);
        if (teacher != null && BCryptVerify(request.Password, teacher.PasswordHash))
        {
            var roles = teacher.Role == Roles.AssistantAdmin
                ? new[] { Roles.AssistantAdmin, Roles.Teacher }
                : new[] { teacher.Role };

            // TENANT LAYER: effective tenant = TenantOwnerId (if this account is staff
            // working for another teacher) or the account's own id (if it IS the tenant).
            var tenantId = teacher.TenantOwnerId ?? teacher.Id;

            var access = _tokens.CreateAccessToken(teacher.Id.ToString(), teacher.UserName, roles, tenantId: tenantId);
            var refresh = _tokens.CreateRefreshToken();
            teacher.RefreshToken = refresh;
            teacher.RefreshTokenExpiry = DateTime.UtcNow.AddDays(double.Parse(_config["Jwt:RefreshTokenDays"] ?? "30"));
            await _db.SaveChangesAsync();

            return Ok(new AuthResponse(access, refresh));
        }

        // Then students, who log in with phone number OR a chosen username.
        // IgnoreQueryFilters() is required here: at login time there is no tenant
        // context yet (no JWT / X-TenantId sent), so the tenant-scoped global query
        // filter on Student (via Group.TeacherId) would otherwise match nothing.
        var student = await _db.Students.IgnoreQueryFilters().FirstOrDefaultAsync(s =>
            s.UserName == request.Username || s.PhoneNumber == request.Username);

        if (student != null && BCryptVerify(request.Password, student.PasswordHash))
        {
            if (student.IsSuspended || student.IsCancelled)
                return Unauthorized(new { message = "Account is suspended or cancelled." });

            var unitIds = await _db.StudentUnitSubscriptions
                .Where(s => s.StudentId == student.Id)
                .Select(s => s.UnitId)
                .ToListAsync();

            var access = _tokens.CreateAccessToken(
                student.Id.ToString(),
                student.UserName ?? student.PhoneNumber,
                new[] { Roles.Student },
                student.GroupId,
                student.SchoolYear,
                unitIds: unitIds);

            var refresh = _tokens.CreateRefreshToken();
            student.RefreshToken = refresh;
            student.RefreshTokenExpiry = DateTime.UtcNow.AddDays(double.Parse(_config["Jwt:RefreshTokenDays"] ?? "30"));
            await _db.SaveChangesAsync();

            return Ok(new AuthResponse(access, refresh));
        }

        return Unauthorized(new { message = "Invalid username or password." });
    }

    // POST api/Auth/refresh
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request)
    {
        var teacher = await _db.Teachers.FirstOrDefaultAsync(t => t.RefreshToken == request.RefreshToken);
        if (teacher != null)
        {
            if (teacher.RefreshTokenExpiry < DateTime.UtcNow)
                return Unauthorized(new { message = "Refresh token expired." });

            var roles = teacher.Role == Roles.AssistantAdmin
                ? new[] { Roles.AssistantAdmin, Roles.Teacher }
                : new[] { teacher.Role };

            var tenantId = teacher.TenantOwnerId ?? teacher.Id;

            var access = _tokens.CreateAccessToken(teacher.Id.ToString(), teacher.UserName, roles, tenantId: tenantId);
            var newRefresh = _tokens.CreateRefreshToken();
            teacher.RefreshToken = newRefresh;
            teacher.RefreshTokenExpiry = DateTime.UtcNow.AddDays(double.Parse(_config["Jwt:RefreshTokenDays"] ?? "30"));
            await _db.SaveChangesAsync();
            return Ok(new AuthResponse(access, newRefresh));
        }

        // IgnoreQueryFilters(): refresh happens with an expired/near-expired access
        // token, and we can't rely on the (possibly stale) X-TenantId header alone
        // to find the student — look them up by refresh token directly.
        var student = await _db.Students.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.RefreshToken == request.RefreshToken);
        if (student != null)
        {
            if (student.RefreshTokenExpiry < DateTime.UtcNow)
                return Unauthorized(new { message = "Refresh token expired." });

            var unitIds = await _db.StudentUnitSubscriptions
                .Where(s => s.StudentId == student.Id)
                .Select(s => s.UnitId)
                .ToListAsync();

            var access = _tokens.CreateAccessToken(
                student.Id.ToString(),
                student.UserName ?? student.PhoneNumber,
                new[] { Roles.Student },
                student.GroupId,
                student.SchoolYear,
                unitIds: unitIds);
            var newRefresh = _tokens.CreateRefreshToken();
            student.RefreshToken = newRefresh;
            student.RefreshTokenExpiry = DateTime.UtcNow.AddDays(double.Parse(_config["Jwt:RefreshTokenDays"] ?? "30"));
            await _db.SaveChangesAsync();
            return Ok(new AuthResponse(access, newRefresh));
        }

        return Unauthorized(new { message = "Invalid refresh token." });
    }

    private static bool BCryptVerify(string plain, string hash)
    {
        try { return BCrypt.Net.BCrypt.Verify(plain, hash); }
        catch { return false; }
    }
}
