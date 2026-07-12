using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
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
[Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
public class GroupsController : ControllerBase
{
    private readonly AppDbContext _db;
    public GroupsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? schoolYear)
    {
        var query = _db.Groups.AsNoTracking().AsQueryable();
        if (schoolYear.HasValue) query = query.Where(g => g.SchoolYear == schoolYear.Value);

        var groups = await query
            .Select(g => new { groupId = g.Id, name = g.Name, schoolYear = g.SchoolYear, studentCount = g.Students.Count })
            .ToListAsync();

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

        return StatusCode(201, new { groupId = group.Id, name = group.Name, schoolYear = group.SchoolYear });
    }

    [HttpPost("delete")]
    public async Task<IActionResult> Delete([FromQuery] int groupId)
    {
        var group = await _db.Groups.FirstOrDefaultAsync(e => e.Id == (groupId));
        if (group == null) return NotFound(new { message = "Group not found." });

        _db.Groups.Remove(group);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Group deleted." });
    }
}
