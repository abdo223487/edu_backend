using System.Text.Json;
using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record PreviewAssistantInput(string? UserName, string? Password, string? Name);

/// <summary>
/// What a PREVIEW account can do (Role = "Preview"):
///  - GET  api/Plans             (see the plans)
///  - POST api/Preview/complete  pick a plan + create the REAL teacher account and
///                               its assistant-admin accounts, exactly like the
///                               SuperAdmin would. One shot: afterwards the preview
///                               account is closed and linked to the new teacher.
///
/// The app then logs the prospect out and he signs in with his real account.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.Preview)]
public class PreviewController : ControllerBase
{
    private const int MaxAssistants = 10;

    private readonly AppDbContext _db;
    private readonly IFileStorageService _files;

    public PreviewController(AppDbContext db, IFileStorageService files)
    {
        _db = db;
        _files = files;
    }

    // GET api/Preview/me
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var id = User.GetUserId();
        var me = await _db.Teachers.AsNoTracking()
            .Where(t => t.Id == id && t.Role == Roles.Preview)
            .Select(t => new { t.Id, t.Name, t.ConvertedTeacherId })
            .FirstOrDefaultAsync();
        if (me == null) return Unauthorized();
        return Ok(new { id = me.Id, name = me.Name, used = me.ConvertedTeacherId != null });
    }

    // POST api/Preview/complete   (multipart)
    // fields: planId, userName, password, name, subject?, image?, assistants? (JSON array string:
    //         [{"userName":"..","password":"..","name":".."}])
    [HttpPost("complete")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Complete(
        [FromForm] int planId,
        [FromForm] string? userName,
        [FromForm] string? password,
        [FromForm] string? name,
        [FromForm] string? subject,
        [FromForm] string? assistants,
        IFormFile? image)
    {
        var previewId = User.GetUserId();
        var preview = await _db.Teachers.FirstOrDefaultAsync(t => t.Id == previewId && t.Role == Roles.Preview);
        if (preview == null) return Unauthorized();
        if (preview.ConvertedTeacherId != null)
            return Conflict(new { message = "حساب المعاينة ده اتستخدم قبل كده. سجّل دخول بحسابك." });

        var plan = await _db.FeaturePlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId);
        if (plan == null) return BadRequest(new { message = "اختار باقة." });

        userName = (userName ?? "").Trim();
        name = (name ?? "").Trim();
        if (userName.Length == 0 || name.Length == 0 || string.IsNullOrWhiteSpace(password))
            return BadRequest(new { message = "اسم المستخدم وكلمة المرور والاسم الكامل مطلوبين." });

        // ── assistants ──
        var assistantInputs = new List<PreviewAssistantInput>();
        if (!string.IsNullOrWhiteSpace(assistants))
        {
            try
            {
                assistantInputs = JsonSerializer.Deserialize<List<PreviewAssistantInput>>(assistants,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            }
            catch (JsonException)
            {
                return BadRequest(new { message = "بيانات المساعدين غير صحيحة." });
            }
        }
        if (assistantInputs.Count > MaxAssistants)
            return BadRequest(new { message = $"أقصى عدد للمساعدين {MaxAssistants}." });

        foreach (var a in assistantInputs)
        {
            if (string.IsNullOrWhiteSpace(a.UserName) || string.IsNullOrWhiteSpace(a.Password) || string.IsNullOrWhiteSpace(a.Name))
                return BadRequest(new { message = "كل مساعد لازم يكون له اسم مستخدم وكلمة مرور واسم كامل." });
        }

        var allUserNames = new List<string> { userName };
        allUserNames.AddRange(assistantInputs.Select(a => a.UserName!.Trim()));
        if (allUserNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != allUserNames.Count)
            return Conflict(new { message = "أسماء المستخدمين لازم تكون مختلفة عن بعض." });

        var taken = await _db.Teachers.AsNoTracking()
            .Where(t => allUserNames.Contains(t.UserName))
            .Select(t => t.UserName)
            .ToListAsync();
        if (taken.Count > 0)
            return Conflict(new { message = "اسم المستخدم مستخدم قبل كده: " + string.Join("، ", taken) });

        // ── create everything atomically ──
        string? uploadedImageUrl = null;
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var teacher = new Teacher
            {
                UserName = userName,
                Name = name,
                Role = Roles.Teacher,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                TenantOwnerId = null,
                Subject = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim(),
                PlanId = plan.Id,
            };
            _db.Teachers.Add(teacher);
            await _db.SaveChangesAsync(); // need teacher.Id for the image folder + assistants

            if (image != null && image.Length > 0)
            {
                uploadedImageUrl = await _files.SaveAsync(image, "teachers", teacher.Id);
                teacher.ImageUrl = uploadedImageUrl;
            }

            foreach (var a in assistantInputs)
            {
                _db.Teachers.Add(new Teacher
                {
                    UserName = a.UserName!.Trim(),
                    Name = a.Name!.Trim(),
                    Role = Roles.AssistantAdmin,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(a.Password!),
                    TenantOwnerId = teacher.Id,
                });
            }

            // the preview account is spent: closed + linked, tokens revoked
            preview.ConvertedTeacherId = teacher.Id;
            preview.ClosedAt = DateTime.UtcNow;
            preview.RefreshToken = null;
            preview.TokenVersion++;

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            return StatusCode(201, new
            {
                teacherId = teacher.Id,
                userName = teacher.UserName,
                name = teacher.Name,
                planId = plan.Id,
                planName = plan.Name,
                assistants = assistantInputs.Count,
            });
        }
        catch
        {
            await tx.RollbackAsync();
            if (uploadedImageUrl != null)
            {
                try { await _files.DeleteAsync(uploadedImageUrl); } catch { /* best effort */ }
            }
            throw;
        }
    }
}
