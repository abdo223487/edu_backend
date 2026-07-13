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

/// <summary>Body for POST Teachers/superadmin/assistants — SuperAdmin only.</summary>
public record CreateAssistantForTeacherRequest(int TeacherId, string UserName, string Password, string Name, string? Role);

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

        // SUPERADMIN BOOTSTRAP: creating (or re-creating, since the username
        // check above already rejects a duplicate) an account with the exact
        // credentials userName == "admin" / password == "admin" always
        // produces a SuperAdmin, no matter who is calling or what "role"/
        // tenant the rest of the request implies. This is intentionally
        // checked BEFORE the normal staff/anonymous branches below so it can
        // never be shadowed by them.
        if (string.Equals(request.UserName, "admin", StringComparison.OrdinalIgnoreCase)
            && request.Password == "admin")
        {
            role = Roles.SuperAdmin;
            tenantOwnerId = null; // SuperAdmin owns no tenant of its own — see Roles.SuperAdmin.
        }
        else if (User.Identity?.IsAuthenticated == true && User.IsInAnyRole(Roles.Teacher, Roles.AssistantAdmin))
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

    // ─────────────────────────────────────────────────────────────────────
    // SUPERADMIN-ONLY ENDPOINTS
    // ─────────────────────────────────────────────────────────────────────

    // POST api/Teachers/superadmin/teachers  (multipart/form-data)
    // Creates a brand-new tenant-root Teacher (TenantOwnerId = null), same as
    // the anonymous path of POST Teachers, EXCEPT the cover photo is an
    // uploaded FILE ("Image") instead of a plain ImageUrl string — stored the
    // exact same way Units/Lessons/Materials/BankQuestions images are
    // (IFileStorageService, folder "teachers"). Since the new teacher has no
    // tenant context of its own yet at upload time, this uses the
    // tenant-override SaveAsync(file, subFolder, tenantId) with the teacher's
    // freshly-created Id.
    [HttpPost("superadmin/teachers")]
    [Consumes("multipart/form-data")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> SuperAdminCreateTeacher(
        [FromForm] string userName,
        [FromForm] string password,
        [FromForm] string name,
        [FromForm] string? subject,
        IFormFile? image)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "userName, password and name are required." });

        if (await _db.Teachers.AnyAsync(t => t.UserName == userName))
            return Conflict(new { message = "Username already taken." });

        var teacher = new Teacher
        {
            UserName = userName,
            Name = name,
            Role = Roles.Teacher,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            TenantOwnerId = null,
            Subject = subject
        };

        _db.Teachers.Add(teacher);
        await _db.SaveChangesAsync(); // need teacher.Id before uploading/stamping the image

        if (image != null && image.Length > 0)
        {
            teacher.ImageUrl = await _files.SaveAsync(image, "teachers", teacher.Id);
            await _db.SaveChangesAsync();
        }

        return StatusCode(201, new
        {
            id = teacher.Id,
            userName = teacher.UserName,
            name = teacher.Name,
            role = teacher.Role,
            subject = teacher.Subject ?? "",
            imageUrl = teacher.ImageUrl ?? PlaceholderAvatar(teacher.Name),
            tenantId = teacher.Id
        });
    }

    // POST api/Teachers/superadmin/assistants  body: { teacherId, userName, password, name, role? }
    // Creates staff (Assistant, or AssistantAdmin if role == "AssistantAdmin")
    // under the GIVEN teacherId's tenant — the SuperAdmin equivalent of the
    // authenticated-staff branch of POST Teachers, except the target tenant
    // is picked explicitly instead of taken from the caller's own JWT (a
    // SuperAdmin has none).
    [HttpPost("superadmin/assistants")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> SuperAdminCreateAssistant([FromBody] CreateAssistantForTeacherRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserName) ||
            string.IsNullOrWhiteSpace(request.Password) ||
            string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "userName, password and name are required." });

        if (await _db.Teachers.AnyAsync(t => t.UserName == request.UserName))
            return Conflict(new { message = "Username already taken." });

        // Must target an existing tenant-root teacher (never another staff
        // account) — mirrors Groups/Units/etc. always being owned by a root.
        var targetTeacher = await _db.Teachers.FirstOrDefaultAsync(t => t.Id == request.TeacherId && t.TenantOwnerId == null);
        if (targetTeacher == null)
            return BadRequest(new { message = "TeacherId does not refer to an existing tenant-root teacher." });

        var role = request.Role == Roles.AssistantAdmin ? Roles.AssistantAdmin : Roles.Assistant;

        var assistant = new Teacher
        {
            UserName = request.UserName,
            Name = request.Name,
            Role = role,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            TenantOwnerId = targetTeacher.Id
        };

        _db.Teachers.Add(assistant);
        await _db.SaveChangesAsync();

        return StatusCode(201, new
        {
            id = assistant.Id,
            userName = assistant.UserName,
            name = assistant.Name,
            role = assistant.Role,
            teacherId = targetTeacher.Id,
            teacherName = targetTeacher.Name
        });
    }

    // GET api/Teachers/superadmin/student-counts
    // Total student count across every teacher, PLUS a per-teacher breakdown.
    // Only tenant-root teachers (TenantOwnerId == null) are listed — staff
    // accounts don't own students of their own. A student counts under every
    // teacher they actually have a group under (StudentGroupMembership),
    // which is why "totalAcrossTeachers" (sum of the per-teacher counts) can
    // be higher than "totalUniqueStudents" (distinct real students) when the
    // same student is subscribed to more than one teacher.
    [HttpGet("superadmin/student-counts")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> SuperAdminStudentCounts()
    {
        var teachers = await _db.Teachers.AsNoTracking()
            .Where(t => t.TenantOwnerId == null)
            .Select(t => new { t.Id, t.Name, t.UserName })
            .ToListAsync();

        // IgnoreQueryFilters(): a SuperAdmin has no single active tenant — this
        // report spans every tenant at once by design.
        var membershipCounts = await _db.StudentGroupMemberships.AsNoTracking().IgnoreQueryFilters()
            .GroupBy(m => m.Group!.TeacherId)
            .Select(g => new { TeacherId = g.Key, Count = g.Select(m => m.StudentId).Distinct().Count() })
            .ToDictionaryAsync(g => g.TeacherId, g => g.Count);

        var perTeacher = teachers.Select(t => new
        {
            teacherId = t.Id,
            teacherName = t.Name,
            userName = t.UserName,
            studentCount = membershipCounts.TryGetValue(t.Id, out var c) ? c : 0
        })
        .OrderByDescending(t => t.studentCount)
        .ToList();

        var totalUniqueStudents = await _db.Students.AsNoTracking().IgnoreQueryFilters().CountAsync();
        var totalAcrossTeachers = perTeacher.Sum(t => t.studentCount);

        return Ok(new
        {
            totalUniqueStudents,
            totalAcrossTeachers,
            teacherCount = perTeacher.Count,
            perTeacher
        });
    }

    private static string PlaceholderAvatar(string name) =>
        $"https://ui-avatars.com/api/?name={Uri.EscapeDataString(name)}&background=0B2C44&color=D4AF37";
}
