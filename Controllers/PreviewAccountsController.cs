using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record CreatePreviewAccountRequest(string? Name, string? UserName, string? Password);

/// <summary>
/// SuperAdmin management of PREVIEW (معاينة) accounts.
///
///  status: active | suspended (إيقاف مؤقت) | closed (مقفول) | converted (اتحول لمدرس حقيقي)
///
///  GET  api/PreviewAccounts
///  POST api/PreviewAccounts                    { name, userName, password }
///  POST api/PreviewAccounts/{id}/suspend       temporary hold      (reversible)
///  POST api/PreviewAccounts/{id}/reactivate    undo suspend / close
///  POST api/PreviewAccounts/{id}/close         ended, kept on record (reversible)
///  POST api/PreviewAccounts/{id}/delete        إلغاء: removes the account for good
///
/// A converted account (prospect already created his real account) can't be
/// reactivated; deleting it never touches the real teacher it produced.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.SuperAdmin)]
public class PreviewAccountsController : ControllerBase
{
    private readonly AppDbContext _db;

    public PreviewAccountsController(AppDbContext db) => _db = db;

    private static string StatusOf(Teacher t) =>
        t.ConvertedTeacherId != null ? "converted"
        : t.ClosedAt != null ? "closed"
        : t.IsSuspended ? "suspended"
        : "active";

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var rows = await _db.Teachers.AsNoTracking()
            .Where(t => t.Role == Roles.Preview)
            .OrderByDescending(t => t.Id)
            .ToListAsync();

        var convertedIds = rows.Where(r => r.ConvertedTeacherId != null).Select(r => r.ConvertedTeacherId!.Value).ToList();
        var convertedNames = await _db.Teachers.AsNoTracking()
            .Where(t => convertedIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name);

        return Ok(rows.Select(t => new
        {
            id = t.Id,
            name = t.Name,
            userName = t.UserName,
            status = StatusOf(t),
            closedAt = t.ClosedAt,
            convertedTeacherId = t.ConvertedTeacherId,
            convertedTeacherName = t.ConvertedTeacherId != null && convertedNames.TryGetValue(t.ConvertedTeacherId.Value, out var n) ? n : null,
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePreviewAccountRequest request)
    {
        var name = (request.Name ?? "").Trim();
        var userName = (request.UserName ?? "").Trim();
        var password = request.Password ?? "";

        if (name.Length == 0 || userName.Length == 0 || password.Trim().Length == 0)
            return BadRequest(new { message = "name, userName and password are required." });

        if (await _db.Teachers.AnyAsync(t => t.UserName == userName))
            return Conflict(new { message = "Username already taken." });

        var acc = new Teacher
        {
            UserName = userName,
            Name = name,
            Role = Roles.Preview,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            TenantOwnerId = null,
        };
        _db.Teachers.Add(acc);
        await _db.SaveChangesAsync();
        return StatusCode(201, new { id = acc.Id, name = acc.Name, userName = acc.UserName, status = StatusOf(acc) });
    }

    private Task<Teacher?> Find(int id) =>
        _db.Teachers.FirstOrDefaultAsync(t => t.Id == id && t.Role == Roles.Preview);

    [HttpPost("{id:int}/suspend")]
    public async Task<IActionResult> Suspend(int id)
    {
        var t = await Find(id);
        if (t == null) return NotFound(new { message = "Preview account not found." });
        if (t.ConvertedTeacherId != null) return BadRequest(new { message = "الحساب ده اتحول لمدرس حقيقي." });

        t.IsSuspended = true;
        t.SuspendedAt = DateTime.UtcNow;
        t.RefreshToken = null;
        await _db.SaveChangesAsync();
        return Ok(new { id = t.Id, status = StatusOf(t) });
    }

    [HttpPost("{id:int}/close")]
    public async Task<IActionResult> Close(int id)
    {
        var t = await Find(id);
        if (t == null) return NotFound(new { message = "Preview account not found." });
        if (t.ConvertedTeacherId != null) return BadRequest(new { message = "الحساب ده اتحول لمدرس حقيقي." });

        t.ClosedAt = DateTime.UtcNow;
        t.IsSuspended = false;
        t.SuspendedAt = null;
        t.RefreshToken = null;
        await _db.SaveChangesAsync();
        return Ok(new { id = t.Id, status = StatusOf(t) });
    }

    [HttpPost("{id:int}/reactivate")]
    public async Task<IActionResult> Reactivate(int id)
    {
        var t = await Find(id);
        if (t == null) return NotFound(new { message = "Preview account not found." });
        if (t.ConvertedTeacherId != null) return BadRequest(new { message = "الحساب ده اتحول لمدرس حقيقي ومينفعش يتفعل تاني." });

        t.IsSuspended = false;
        t.SuspendedAt = null;
        t.ClosedAt = null;
        await _db.SaveChangesAsync();
        return Ok(new { id = t.Id, status = StatusOf(t) });
    }

    [HttpPost("{id:int}/delete")]
    public async Task<IActionResult> Delete(int id)
    {
        var t = await Find(id);
        if (t == null) return NotFound(new { message = "Preview account not found." });

        _db.Teachers.Remove(t);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }
}
