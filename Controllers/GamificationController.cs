using EduApi.Common;
using EduApi.Models;
using EduApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/Gamification  (student only; always scoped to the X-TenantId teacher)
///  GET Gamification/me                                  points, level, streak, badges (with progress), recent activity
///  GET Gamification/leaderboard?period=week|month|all&amp;take=20
///  GET Gamification/rules                               how points are earned
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.Student)]
public class GamificationController : ControllerBase
{
    private readonly GamificationService _svc;
    public GamificationController(GamificationService svc) => _svc = svc;

    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
        => Ok(await _svc.GetSummaryAsync(User.GetUserId(), ct));

    [HttpGet("leaderboard")]
    public async Task<IActionResult> Leaderboard([FromQuery] string? period, [FromQuery] int take = 20, CancellationToken ct = default)
        => Ok(await _svc.GetLeaderboardAsync(User.GetUserId(), period, User.GetSchoolYear(), take, ct));

    [HttpGet("rules")]
    public IActionResult Rules() => Ok(GamificationService.Rules());
}
