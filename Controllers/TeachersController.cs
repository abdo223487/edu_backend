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

/// <summary>Body for POST Teachers/superadmin/teachers/{id}/suspend — SuperAdmin only.</summary>
public record SuspendTeacherRequest(string? Reason);

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
            .Select(t => new { t.Id, t.Name, t.UserName, t.IsSuspended })
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
            isSuspended = t.IsSuspended,
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

    // POST api/Teachers/superadmin/teachers/{id}/suspend  body: { reason? }
    // Blocks this tenant-root teacher (and all of their staff, since staff act
    // on the root's tenant) from doing ANYTHING -- login fails from this point
    // on, and TenantSuspensionMiddleware rejects every request against a still
    // -valid JWT/X-TenantId for this tenant, for both staff AND students of
    // this teacher, with a 403. None of the teacher's data is touched -- this
    // is fully reversible via .../reactivate.
    [HttpPost("superadmin/teachers/{id:int}/suspend")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> SuperAdminSuspendTeacher(int id, [FromBody] SuspendTeacherRequest? request)
    {
        var teacher = await _db.Teachers.FirstOrDefaultAsync(t => t.Id == id && t.TenantOwnerId == null);
        if (teacher == null) return NotFound(new { message = "Tenant-root teacher not found." });

        teacher.IsSuspended = true;
        teacher.SuspendedAt = DateTime.UtcNow;
        teacher.SuspensionReason = request?.Reason;
        await _db.SaveChangesAsync();

        return Ok(new { id = teacher.Id, name = teacher.Name, isSuspended = teacher.IsSuspended, suspendedAt = teacher.SuspendedAt, suspensionReason = teacher.SuspensionReason });
    }

    // POST api/Teachers/superadmin/teachers/{id}/reactivate
    [HttpPost("superadmin/teachers/{id:int}/reactivate")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> SuperAdminReactivateTeacher(int id)
    {
        var teacher = await _db.Teachers.FirstOrDefaultAsync(t => t.Id == id && t.TenantOwnerId == null);
        if (teacher == null) return NotFound(new { message = "Tenant-root teacher not found." });

        teacher.IsSuspended = false;
        teacher.SuspendedAt = null;
        teacher.SuspensionReason = null;
        await _db.SaveChangesAsync();

        return Ok(new { id = teacher.Id, name = teacher.Name, isSuspended = teacher.IsSuspended });
    }

    // DELETE api/Teachers/superadmin/teachers/{id}
    // Permanently deletes a tenant-root teacher AND everything that belongs to
    // their tenant: staff (Assistant/AssistantAdmin) accounts, Groups, Units/
    // Lessons, Lectures, Materials, Notebooks, Codes, Notifications, Quizzes/
    // Questions/Results, Assignments, BankQuestions/Attempts, Attendance, and
    // every per-student activity row tied to this teacher (quiz/homework/
    // center results, submissions, unlocks, subscriptions, payments).
    //
    // STUDENTS need special handling because of MULTI-TENANT MEMBERSHIP: a
    // student can be subscribed to several teachers at once. For each student
    // touched by this teacher:
    //   - if they have NO other teacher relationship left afterwards, the
    //     whole student (and their remaining activity/history rows) is deleted
    //     too, since nothing else legitimately owns that account;
    //   - otherwise they're kept, only this teacher's membership is removed,
    //     and their legacy GroupId is re-pointed at one of their remaining
    //     groups if it used to point at this teacher's group (Student.GroupId
    //     has a Restrict FK to Group, so a dangling reference would otherwise
    //     block deleting this teacher's Groups).
    // This is irreversible -- unlike /suspend, there is no undo.
    [HttpDelete("superadmin/teachers/{id:int}")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public Task<IActionResult> SuperAdminDeleteTeacher(int id) => DeleteTeacherCore(id);

    // POST api/Teachers/superadmin/teachers/{id}/delete
    // Same as the DELETE endpoint above, exposed as a POST too since some
    // HTTP clients don't have a convenient "send DELETE with auth" helper.
    [HttpPost("superadmin/teachers/{id:int}/delete")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public Task<IActionResult> SuperAdminDeleteTeacherPost(int id) => DeleteTeacherCore(id);

    private async Task<IActionResult> DeleteTeacherCore(int id)
    {
        var teacher = await _db.Teachers.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == id && t.TenantOwnerId == null);
        if (teacher == null) return NotFound(new { message = "Tenant-root teacher not found." });

        // SAFETY GUARD: this endpoint deletes any tenant-root teacher (TenantOwnerId
        // == null), and a SuperAdmin also has TenantOwnerId == null, so without this
        // check a SuperAdmin (even themselves) could delete the account -- including
        // the last remaining one -- with zero recovery path other than manual DB/SQL
        // surgery, since DbSeeder only (re)creates "admin" when the Teachers table is
        // completely empty (see DbSeeder.Seed).
        if (teacher.Role == Roles.SuperAdmin)
            return BadRequest(new { message = "Cannot delete a SuperAdmin account." });

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // 1) Staff (Assistant/AssistantAdmin) accounts under this tenant.
            await _db.Teachers.IgnoreQueryFilters().Where(t => t.TenantOwnerId == id).ExecuteDeleteAsync();

            // 2) Which groups this teacher owns.
            var groupIds = await _db.Groups.IgnoreQueryFilters().Where(g => g.TeacherId == id).Select(g => g.Id).ToListAsync();

            // 3) Students tied to this teacher (legacy GroupId or a membership row).
            var affectedStudentIds = await _db.Students.IgnoreQueryFilters()
                .Where(s => groupIds.Contains(s.GroupId) || s.GroupMemberships.Any(m => groupIds.Contains(m.GroupId)))
                .Select(s => s.Id)
                .Distinct()
                .ToListAsync();

            var deletedStudentIds = new List<int>();

            foreach (var studentId in affectedStudentIds)
            {
                var student = await _db.Students.IgnoreQueryFilters()
                    .Include(s => s.GroupMemberships)
                    .FirstAsync(s => s.Id == studentId);

                var thisTeacherMemberships = student.GroupMemberships.Where(m => groupIds.Contains(m.GroupId)).ToList();
                var remainingMemberships = student.GroupMemberships.Where(m => !groupIds.Contains(m.GroupId)).ToList();

                _db.StudentGroupMemberships.RemoveRange(thisTeacherMemberships);

                if (remainingMemberships.Count == 0)
                {
                    // No ties left to any other teacher -- delete the account entirely.
                    deletedStudentIds.Add(student.Id);
                    _db.Students.Remove(student);
                }
                else if (groupIds.Contains(student.GroupId))
                {
                    // Legacy GroupId pointed at a group we're about to delete --
                    // re-point it at one of the student's remaining groups so the
                    // Student.GroupId -> Group Restrict FK doesn't block us later.
                    student.GroupId = remainingMemberships[0].GroupId;
                }
            }
            await _db.SaveChangesAsync();

            // 4) Orphaned history rows for students that got deleted above
            //    (StateHistoryEntry has no TeacherId column of its own).
            if (deletedStudentIds.Count > 0)
                await _db.StateHistoryEntries.Where(h => deletedStudentIds.Contains(h.StudentId)).ExecuteDeleteAsync();

            // 5) Every per-student activity row scoped to this teacher, regardless
            //    of whether the student themselves survived (their marks/attendance
            //    from a now-deleted teacher are meaningless either way).
            await _db.QuizResults.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();
            await _db.CenterQuizResults.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();
            await _db.HomeworkResults.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();
            await _db.Attendances.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();
            await _db.AssignmentSubmissions.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();
            await _db.NotebookPayments.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();
            await _db.StudentLectureUnlocks.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();
            await _db.StudentUnitSubscriptions.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();

            // 6) BankAttempts before BankQuestions: BankAttemptQuestion has a
            //    Restrict FK to BankQuestion, but a Cascade FK to BankAttempt --
            //    deleting the attempts first clears those rows automatically.
            await _db.BankAttempts.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();
            await _db.BankQuestions.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();

            // 7) Content the teacher owns. Each of these cascades its own
            //    children (Questions/AssignmentQuestions/*GroupLink tables/etc.)
            //    via the FK Cascade rules already configured in AppDbContext.
            await _db.Quizzes.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();
            await _db.Assignments.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();
            await _db.Notifications.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();
            await _db.Codes.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();
            await _db.Notebooks.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();
            await _db.Lectures.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();
            await _db.Materials.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();
            await _db.Units.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();

            // 8) Groups last -- every student that referenced them via legacy
            //    GroupId was either deleted or re-pointed in step 3 above, so
            //    the Restrict FK on Student.GroupId no longer blocks this.
            await _db.Groups.IgnoreQueryFilters().Where(x => x.TeacherId == id).ExecuteDeleteAsync();

            // 9) The teacher row itself.
            _db.Teachers.Remove(teacher);
            await _db.SaveChangesAsync();

            await tx.CommitAsync();

            return Ok(new
            {
                message = $"Teacher '{teacher.Name}' and all of their data were permanently deleted.",
                deletedTeacherId = id,
                studentsFullyDeleted = deletedStudentIds.Count,
                studentsUnlinkedOnly = affectedStudentIds.Count - deletedStudentIds.Count
            });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return StatusCode(500, new { message = "Failed to delete teacher and their data.", detail = ex.Message });
        }
    }

    private static string PlaceholderAvatar(string name) =>
        $"https://ui-avatars.com/api/?name={Uri.EscapeDataString(name)}&background=0B2C44&color=D4AF37";
}
