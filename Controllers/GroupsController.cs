using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record CreateGroupRequest(int SchoolYear, string Name);

/// <summary>
/// Route: api/Groups
///  GET  Groups?schoolYear=..
///  POST Groups                       body: { schoolYear, name }
///  POST Groups/delete?groupId=..
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
public class GroupsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ICacheService _cache;
    public GroupsController(AppDbContext db, ITenantContext tenant, ICacheService cache)
    {
        _db = db;
        _tenant = tenant;
        _cache = cache;
    }

    // CACHING: prefix shared by every cached Groups list for this tenant
    // (any schoolYear filter) — RemoveByPrefixAsync(this) wipes them all
    // at once after a write.
    private string GroupsCachePrefix() => $"tenant:{_tenant.CurrentTenantId}:groups:list:";

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? schoolYear)
    {
        var cacheKey = $"{GroupsCachePrefix()}{schoolYear}";
        var groups = await _cache.GetOrCreateAsync(cacheKey, TimeSpan.FromSeconds(60), async () =>
        {
            var query = _db.Groups.AsNoTracking().AsQueryable();
            if (schoolYear.HasValue) query = query.Where(g => g.SchoolYear == schoolYear.Value);

            // COUNT FIX: g.Students is the LEGACY FK-only collection (Student.GroupId),
            // which misses anyone linked to this group only via StudentGroupMembership
            // (e.g. a student added while already belonging to another teacher). Count
            // distinct StudentIds in the membership table instead, which is the source
            // of truth for "who's actually in this group" post multi-tenant-membership.
            return await query
                .Select(g => new { groupId = g.Id, name = g.Name, schoolYear = g.SchoolYear, studentCount = _db.StudentGroupMemberships.Count(m => m.GroupId == g.Id) })
                .ToListAsync();
        });

        return Ok(groups);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateGroupRequest request)
    {
        if (await _db.Groups.AnyAsync(g => g.Name == request.Name && g.SchoolYear == request.SchoolYear))
            return Conflict(new { message = "A group with this name already exists for this year." });

        var group = new Group
        {
            Name = request.Name,
            SchoolYear = request.SchoolYear,
            TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
        };
        _db.Groups.Add(group);
        await _db.SaveChangesAsync();
        await _cache.RemoveByPrefixAsync(GroupsCachePrefix());

        return StatusCode(201, new { groupId = group.Id, name = group.Name, schoolYear = group.SchoolYear });
    }

    [HttpPost("delete")]
    public async Task<IActionResult> Delete([FromQuery] int groupId)
    {
        var group = await _db.Groups.FirstOrDefaultAsync(e => e.Id == (groupId));
        if (group == null) return NotFound(new { message = "Group not found." });

        _db.Groups.Remove(group);
        await _db.SaveChangesAsync();
        await _cache.RemoveByPrefixAsync(GroupsCachePrefix());

        return Ok(new { message = "Group deleted." });
    }
}
