using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

// Body for POST Registration/register.
public record SubmitRegistrationRequest(
    string Name,
    string PhoneNumber,
    string ParentPhoneNumber,
    string UserName,
    string Password,
    int TeacherId,
    // Confirmation step: the teacher's numeric platform ID as given to the
    // student offline (e.g. verbally / on a poster), typed in by hand as a
    // sanity check that they picked the right teacher from the dropdown.
    int TeacherIdConfirm,
    int SchoolYear,
    int GroupId
);

public record ApproveRegistrationRequest(List<int>? UnitIds);
public record RejectRegistrationRequest(string? Reason);

/// <summary>
/// Route: api/Registration
/// Public (anonymous) side — used by a student BEFORE they have any account:
///   GET  Registration/teachers                         list of pickable teachers
///   GET  Registration/groups?teacherId=&amp;schoolYear=      groups for that teacher/year
///   POST Registration/register                         submit a join request
///   GET  Registration/status/{id}?code=..               poll review status
///
/// Teacher-authorized side — reviewing requests aimed at their own tenant:
///   GET  Registration/pending
///   POST Registration/{id}/approve   body: { unitIds }
///   POST Registration/{id}/reject    body: { reason }
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class RegistrationController : ControllerBase
{
    private readonly AppDbContext _db;

    public RegistrationController(AppDbContext db)
    {
        _db = db;
    }

    // GET Registration/teachers — same shape/eligibility as TeachersController.GetAll
    // (root teachers only), but reachable with no auth at all since the student
    // has no token yet at this point in the flow.
    [HttpGet("teachers")]
    [AllowAnonymous]
    public async Task<IActionResult> GetTeachers()
    {
        var teachers = await _db.Teachers.AsNoTracking()
            .Where(t => t.TenantOwnerId == null && t.Role == Roles.Teacher && !t.IsSuspended)
            .Select(t => new
            {
                id = t.Id,
                name = t.Name,
                subject = t.Subject ?? "",
                imageUrl = t.ImageUrl ?? ""
            })
            .OrderBy(t => t.name)
            .ToListAsync();

        return Ok(teachers);
    }

    // GET Registration/groups?teacherId=&schoolYear=
    [HttpGet("groups")]
    [AllowAnonymous]
    public async Task<IActionResult> GetGroups([FromQuery] int teacherId, [FromQuery] int schoolYear)
    {
        var groups = await _db.Groups.AsNoTracking().IgnoreQueryFilters()
            .Where(g => g.TeacherId == teacherId && g.SchoolYear == schoolYear)
            .Select(g => new { id = g.Id, name = g.Name })
            .OrderBy(g => g.name)
            .ToListAsync();

        return Ok(groups);
    }

    // POST Registration/register
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] SubmitRegistrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.PhoneNumber) ||
            string.IsNullOrWhiteSpace(request.ParentPhoneNumber) || string.IsNullOrWhiteSpace(request.UserName) ||
            string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { message = "من فضلك أكمل كل البيانات المطلوبة." });

        var teacher = await _db.Teachers.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TeacherId && t.TenantOwnerId == null && t.Role == Roles.Teacher);
        if (teacher == null)
            return NotFound(new { message = "المدرس غير موجود." });

        if (teacher.IsSuspended)
            return BadRequest(new { message = "هذا الحساب متوقف حاليًا." });

        // Confirmation step (see SubmitRegistrationRequest.TeacherIdConfirm doc).
        if (request.TeacherIdConfirm != teacher.Id)
            return BadRequest(new { message = "رقم المدرس اللي دخلته مش مطابق للمدرس المختار." });

        var group = await _db.Groups.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => g.Id == request.GroupId && g.TeacherId == request.TeacherId && g.SchoolYear == request.SchoolYear);
        if (group == null)
            return BadRequest(new { message = "المجموعة غير موجودة." });

        // Username must be free both among real students (global identity)
        // and among other still-pending requests, so two people can't queue
        // up for the same username at once.
        var userNameTaken = await _db.Students.IgnoreQueryFilters().AnyAsync(s => s.UserName == request.UserName)
            || await _db.StudentRegistrationRequests.IgnoreQueryFilters()
                .AnyAsync(r => r.UserName == request.UserName && r.Status == RegistrationStatus.Pending);
        if (userNameTaken)
            return Conflict(new { message = "اسم المستخدم ده مستخدم بالفعل." });

        // Don't let the same phone re-queue a second pending request at the
        // same teacher while one is already awaiting review.
        var alreadyPending = await _db.StudentRegistrationRequests.IgnoreQueryFilters()
            .AnyAsync(r => r.PhoneNumber == request.PhoneNumber && r.TeacherId == request.TeacherId && r.Status == RegistrationStatus.Pending);
        if (alreadyPending)
            return Conflict(new { message = "عندك طلب تسجيل قيد المراجعة بالفعل عند هذا المدرس." });

        var reg = new StudentRegistrationRequest
        {
            Name = request.Name.Trim(),
            PhoneNumber = request.PhoneNumber.Trim(),
            ParentPhoneNumber = request.ParentPhoneNumber.Trim(),
            UserName = request.UserName.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            TeacherId = request.TeacherId,
            SchoolYear = request.SchoolYear,
            GroupId = request.GroupId,
            AccessCode = Guid.NewGuid().ToString("N"),
            Status = RegistrationStatus.Pending
        };
        _db.StudentRegistrationRequests.Add(reg);
        await _db.SaveChangesAsync();

        return StatusCode(201, new { requestId = reg.Id, accessCode = reg.AccessCode, status = reg.Status });
    }

    // GET Registration/status/{id}?code=..
    [HttpGet("status/{id:int}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStatus(int id, [FromQuery] string code)
    {
        var reg = await _db.StudentRegistrationRequests.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == id && r.AccessCode == code);
        if (reg == null) return NotFound(new { message = "الطلب غير موجود." });

        var teacherName = await _db.Teachers.AsNoTracking().Where(t => t.Id == reg.TeacherId).Select(t => t.Name).FirstOrDefaultAsync();

        return Ok(new
        {
            status = reg.Status,
            teacherName,
            rejectionReason = reg.RejectionReason,
            userName = reg.UserName
        });
    }

    // ─────────────────────────────────────────
    // Teacher-side review
    // ─────────────────────────────────────────

    // GET Registration/pending
    [HttpGet("pending")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> GetPending()
    {
        var pending = await _db.StudentRegistrationRequests.AsNoTracking()
            .Where(r => r.Status == RegistrationStatus.Pending)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new
            {
                id = r.Id,
                name = r.Name,
                phoneNumber = r.PhoneNumber,
                parentPhoneNumber = r.ParentPhoneNumber,
                userName = r.UserName,
                schoolYear = r.SchoolYear,
                groupId = r.GroupId,
                groupName = _db.Groups.IgnoreQueryFilters().Where(g => g.Id == r.GroupId).Select(g => g.Name).FirstOrDefault(),
                createdAt = r.CreatedAt
            })
            .ToListAsync();

        return Ok(pending);
    }

    // POST Registration/{id}/approve  body: { unitIds }
    [HttpPost("{id:int}/approve")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> Approve(int id, [FromBody] ApproveRegistrationRequest request)
    {
        var reg = await _db.StudentRegistrationRequests.FirstOrDefaultAsync(r => r.Id == id);
        if (reg == null) return NotFound(new { message = "الطلب غير موجود." });
        if (reg.Status != RegistrationStatus.Pending)
            return BadRequest(new { message = "تمت مراجعة هذا الطلب بالفعل." });

        var group = await _db.Groups.IgnoreQueryFilters().FirstOrDefaultAsync(g => g.Id == reg.GroupId);
        if (group == null) return BadRequest(new { message = "المجموعة لم تعد موجودة." });

        // Same cross-tenant dedupe StudentsController.Create uses: this phone
        // number may already have an account (e.g. registered with a
        // different teacher earlier) — fold in instead of creating a
        // duplicate login.
        var existing = await _db.Students.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.PhoneNumber == reg.PhoneNumber);

        int studentId;
        if (existing != null)
        {
            var membership = await _db.StudentGroupMemberships.IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.StudentId == existing.Id && m.Group!.TeacherId == group.TeacherId);
            if (membership == null)
                _db.StudentGroupMemberships.Add(new StudentGroupMembership { StudentId = existing.Id, GroupId = group.Id });
            else
            {
                membership.GroupId = group.Id;
                membership.IsCancelled = false;
                membership.IsSuspended = false;
            }
            studentId = existing.Id;
        }
        else
        {
            var student = new Student
            {
                Name = reg.Name,
                PhoneNumber = reg.PhoneNumber,
                ParentPhoneNumber = reg.ParentPhoneNumber,
                UserName = reg.UserName,
                PasswordHash = reg.PasswordHash,
                GroupId = reg.GroupId,
                SchoolYear = reg.SchoolYear
            };
            _db.Students.Add(student);
            await _db.SaveChangesAsync();

            _db.StudentGroupMemberships.Add(new StudentGroupMembership { StudentId = student.Id, GroupId = group.Id });
            studentId = student.Id;
        }

        foreach (var unitId in request.UnitIds ?? new())
        {
            if (!await _db.StudentUnitSubscriptions.IgnoreQueryFilters()
                    .AnyAsync(s => s.StudentId == studentId && s.UnitId == unitId && s.TeacherId == group.TeacherId))
                _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = group.TeacherId, StudentId = studentId, UnitId = unitId });
        }

        reg.Status = RegistrationStatus.Approved;
        reg.CreatedStudentId = studentId;
        reg.ReviewedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { message = "تم قبول الطالب.", studentId });
    }

    // POST Registration/{id}/reject  body: { reason }
    [HttpPost("{id:int}/reject")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.SuperAdmin}")]
    public async Task<IActionResult> Reject(int id, [FromBody] RejectRegistrationRequest request)
    {
        var reg = await _db.StudentRegistrationRequests.FirstOrDefaultAsync(r => r.Id == id);
        if (reg == null) return NotFound(new { message = "الطلب غير موجود." });
        if (reg.Status != RegistrationStatus.Pending)
            return BadRequest(new { message = "تمت مراجعة هذا الطلب بالفعل." });

        reg.Status = RegistrationStatus.Rejected;
        reg.RejectionReason = request.Reason;
        reg.ReviewedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { message = "تم رفض الطلب." });
    }
}
