using EduApi.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/AppVersions
///  GET AppVersions/latest    (called with no auth requirement on app startup)
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AppVersionsController : ControllerBase
{
    private readonly AppDbContext _db;
    public AppVersionsController(AppDbContext db) => _db = db;

    // Real contract (confirmed in StudentHome.dart): { "version": "1.2.3", "downloadUrl": "https://..." }
    // Called with role "student" and a 3s client-side timeout; client fails open on error.
    [HttpGet("latest")]
    public async Task<IActionResult> GetLatest([FromQuery] string platform = "android")
    {
        var version = await _db.AppVersions.FirstOrDefaultAsync(v => v.Platform == platform);
        if (version == null)
            return Ok(new { version = "1.0.0", downloadUrl = "" });

        return Ok(new { version = version.Version, downloadUrl = version.DownloadUrl });
    }
}
