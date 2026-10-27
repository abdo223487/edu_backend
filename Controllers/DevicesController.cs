using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record RegisterDeviceRequest(string Token, string? Platform);
public record UnregisterDeviceRequest(string Token);

/// <summary>
/// Route: api/Devices
///  POST Devices/register    body: { token, platform }   (any logged-in user)
///  POST Devices/unregister  body: { token }             (call on logout)
///
/// One physical phone = one FCM token. If the same phone logs into a different
/// account, the token row is simply re-assigned to the new user, so the previous
/// account stops receiving pushes on that phone.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DevicesController : ControllerBase
{
    private readonly AppDbContext _db;
    public DevicesController(AppDbContext db) => _db = db;

    private string UserType => User.IsInRole(Roles.Student) ? "Student" : "Staff";

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterDeviceRequest request)
    {
        var token = (request.Token ?? string.Empty).Trim();
        if (token.Length < 20 || token.Length > 512)
            return BadRequest(new { message = "Invalid token." });

        var userId = User.GetUserId();
        if (userId <= 0) return Unauthorized();

        var platform = string.IsNullOrWhiteSpace(request.Platform) ? "android" : request.Platform!.Trim().ToLowerInvariant();
        if (platform.Length > 20) platform = platform[..20];

        var existing = await _db.DeviceTokens.FirstOrDefaultAsync(d => d.Token == token);
        if (existing == null)
        {
            _db.DeviceTokens.Add(new DeviceToken
            {
                UserType = UserType,
                UserId = userId,
                Token = token,
                Platform = platform,
            });
        }
        else
        {
            existing.UserType = UserType;
            existing.UserId = userId;
            existing.Platform = platform;
            existing.LastSeenAt = DateTime.UtcNow;
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Two near-simultaneous registers of the same token: the unique index
            // makes the loser fail -- the token IS registered, so that's fine.
        }

        return Ok(new { message = "Registered." });
    }

    [HttpPost("unregister")]
    public async Task<IActionResult> Unregister([FromBody] UnregisterDeviceRequest request)
    {
        var token = (request.Token ?? string.Empty).Trim();
        var userId = User.GetUserId();
        if (token.Length == 0) return Ok(new { message = "Nothing to do." });

        // Only remove the token if it still belongs to THIS user (it may already have
        // been re-assigned to whoever logged in next on the same phone).
        await _db.DeviceTokens
            .Where(d => d.Token == token && d.UserId == userId && d.UserType == UserType)
            .ExecuteDeleteAsync();

        return Ok(new { message = "Unregistered." });
    }
}
