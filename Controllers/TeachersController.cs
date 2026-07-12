using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/Teachers.
///  GET  Teachers        (client call: getWithAuth("Teachers", "student"))
///  POST Teachers        -- was missing entirely; nothing in the original app
///                           could create a Teacher account, only DbSeeder could.
///  POST Teachers/edit    -- was missing entirely; the only way to set Subject/
///                           ImageUrl (both read directly by SelectTeacherPage's
///                           _TeacherCard, with NO null-check on imageUrl).
/// </summary>
public record CreateTeacherRequest(string UserName, string Password, string Name, string? Role, string? Subject, string? ImageUrl);

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TeachersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _files;
    public TeachersController(AppDbContext db, IFileStorageService files)
    {
        _db = db;
        _files = files;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        // TENANT LAYER: "subscribed" = the requesting student's own Group belongs
        // to that teacher. A student only ever belongs to exactly one tenant, so
        // exactly one entry (at most) will come back with isSubscribedTo == true.
        // Only students in this multi-tenant flow; teacher/staff callers see it
        // as false for everyone since the concept doesn't apply to them.
        // MULTI-TENANT: a student can now be subscribed to SEVERAL teachers at
        // once (StudentGroupMembership), so "isSubscribedTo" is true for every
        // teacher the student actually has a group under -- not just one.
        List<int> mySubscribedTeacherIds = new();
        if (User.IsInRole(Roles.Student))
        {
            var studentId = User.GetUserId();
            mySubscribedTeacherIds = await _db.Students.AsNoTracking().IgnoreQueryFilters()
                .Where(s => s.Id == studentId)
                .SelectMany(s => s.GroupMemberships.Select(m => m.Group!.TeacherId))
                .Distinct()
                .ToListAsync();
        }

        var teachers = await _db.Teachers.AsNoTracking()
            .Where(t => t.TenantOwnerId == null) // only real tenant-root teachers are "selectable"
            .Select(t => new
            {
                id = t.Id,
                name = t.Name,
                userName = t.UserName,
                role = t.Role,
                subject = t.Subject ?? "",
                // NEVER null: _TeacherCard does Image.network(teacher['imageUrl'])
                // with no null-check, which throws a runtime TypeError on a null
                // src. Fall back to a generated placeholder avatar instead.
                imageUrl = t.ImageUrl ?? PlaceholderAvatar(t.Name),
                isSubscribedTo = mySubscribedTeacherIds.Contains(t.Id)
            })
            .ToListAsync();

        return Ok(teachers);
    }

    // POST api/Teachers
    // Two behaviors depending on the CALLER, decided server-side (never trusted
    // from the request body) so nobody can self-grant elevated roles:
    //
    //  1) Called by an already-authenticated Teacher/AssistantAdmin
    //     -> creates STAFF (Assistant or AssistantAdmin) under the CALLER's own
    //        tenant. TenantOwnerId = caller's tenant. "Role" in the body may pick
    //        Assistant or AssistantAdmin; anything else defaults to Assistant.
    //
    //  2) Called with no valid staff session (anonymous, or a Student token)
    //     -> bootstraps a brand-new, INDEPENDENT tenant-root Teacher (exactly
    //        like "admin" / "teacher2" in DbSeeder). Role is always forced to
    //        "Teacher" and TenantOwnerId is always null in this path, regardless
    //        of what the body asks for.
    //
    // Subject/ImageUrl are optional here (plain URL string) — only meaningful for
    // a tenant-root teacher, since only those show up on SelectTeacherPage. Use
    // POST Teachers/edit afterwards to upload an actual image file instead of a URL.
    //
    // Response 201: { id, userName, name, role, tenantId }
    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Create([FromBody] CreateTeacherRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserName) ||
            string.IsNullOrWhiteSpace(request.Password) ||
            string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "userName, password and name are required." });

        if (await _db.Teachers.AnyAsync(t => t.UserName == request.UserName))
            return Conflict(new { message = "Username already taken." });

        string role;
        int? tenantOwnerId;

        if (User.Identity?.IsAuthenticated == true && User.IsInAnyRole(Roles.Teacher, Roles.AssistantAdmin))
        {
            tenantOwnerId = User.GetStaffTenantId();
            if (tenantOwnerId == null)
                return BadRequest(new { message = "Could not resolve caller's tenant." });

            role = request.Role == Roles.AssistantAdmin ? Roles.AssistantAdmin : Roles.Assistant;
        }
        else
        {
            role = Roles.Teacher;
            tenantOwnerId = null;
        }

        var teacher = new Teacher
        {
            UserName = request.UserName,
            Name = request.Name,
            Role = role,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            TenantOwnerId = tenantOwnerId,
            Subject = request.Subject,
            ImageUrl = request.ImageUrl
        };

        _db.Teachers.Add(teacher);
        await _db.SaveChangesAsync();

        return StatusCode(201, new
        {
            id = teacher.Id,
            userName = teacher.UserName,
            name = teacher.Name,
            role = teacher.Role,
            subject = teacher.Subject ?? "",
            imageUrl = teacher.ImageUrl ?? PlaceholderAvatar(teacher.Name),
            tenantId = teacher.TenantOwnerId ?? teacher.Id
        });
    }

    // POST api/Teachers/edit  (multipart) — lets a logged-in Teacher/AssistantAdmin
    // update THEIR OWN account: Name?, Subject?, +Image file (field "Image" or
    // "image", same case-insensitive convention as Units/edit). Always edits the
    // caller's own row (User.GetUserId()) — there is no "edit someone else's
    // teacher profile" here, by design.
    [HttpPost("edit")]
    [Consumes("multipart/form-data")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Edit(
        [FromForm(Name = "Name")] string? name,
        [FromForm(Name = "Subject")] string? subject,
        IFormFile? image)
    {
        var myId = User.GetUserId();
        var teacher = await _db.Teachers.FirstOrDefaultAsync(t => t.Id == myId);
        if (teacher == null) return NotFound(new { message = "Teacher not found." });

        if (name != null) teacher.Name = name;
        if (subject != null) teacher.Subject = subject;

        if (image != null && image.Length > 0)
        {
            var oldImageUrl = teacher.ImageUrl;

            // 1) Upload new file
            teacher.ImageUrl = await _files.SaveAsync(image, "teachers");

            // 2) Update database
            await _db.SaveChangesAsync();

            // 3) Delete old file
            await _files.DeleteAsync(oldImageUrl);

            return Ok(new
            {
                id = teacher.Id,
                name = teacher.Name,
                subject = teacher.Subject ?? "",
                imageUrl = teacher.ImageUrl ?? PlaceholderAvatar(teacher.Name)
            });
        }

        await _db.SaveChangesAsync();

        return Ok(new
        {
            id = teacher.Id,
            name = teacher.Name,
            subject = teacher.Subject ?? "",
            imageUrl = teacher.ImageUrl ?? PlaceholderAvatar(teacher.Name)
        });
    }

    private static string PlaceholderAvatar(string name) =>
        $"https://ui-avatars.com/api/?name={Uri.EscapeDataString(name)}&background=0B2C44&color=D4AF37";
}
