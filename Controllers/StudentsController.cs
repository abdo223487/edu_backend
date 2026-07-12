using System.Text;
using EduApi.Common;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/Students. This is the largest surface in the app; see class-level
/// method XML docs for the exact endpoint each action reconstructs.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class StudentsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly Common.ITenantContext _tenant;
    public StudentsController(AppDbContext db, Common.ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    // POST Students
    // MULTI-TENANT DEDUPE FIX: previously this always inserted a brand new
    // Student row with zero regard for whether the phone number already
    // belonged to someone else's student — a teacher adding a student who
    // already had an account with a DIFFERENT teacher silently ended up with
    // TWO separate accounts (two different Ids, two different
    // usernames/passwords) for the same real person, instead of one shared
    // account visible under both tenants via StudentGroupMembership. Now this
    // does the same phone-number lookup Students/link does, and if a match is
    // found, folds into that existing account (adds a membership + any
    // requested unit subscriptions) instead of creating a duplicate — the
    // teacher doesn't need to know or care whether the student already
    // exists elsewhere; any UserName/Password passed in the request is
    // ignored in that case since the original account's login must win.
    [HttpPost]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Create([FromBody] CreateStudentRequest request)
    {
        var group = await _db.Groups.FirstOrDefaultAsync(e => e.Id == (request.GroupId));
        if (group == null) return BadRequest(new { message = "Group not found." });

        // Cross-tenant lookup by design — this student may already exist under
        // a completely different teacher's tenant (see Students/link).
        var existing = await _db.Students.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.PhoneNumber == request.PhoneNumber);

        if (existing != null)
        {
            if (existing.IsSuspended || existing.IsCancelled)
                return BadRequest(new { message = "A student with this phone number already exists and their account is suspended or cancelled." });

            var alreadyLinked = await _db.StudentGroupMemberships.IgnoreQueryFilters()
                .AnyAsync(m => m.StudentId == existing.Id && m.Group!.TeacherId == group.TeacherId);
            if (!alreadyLinked)
                _db.StudentGroupMemberships.Add(new StudentGroupMembership { StudentId = existing.Id, GroupId = group.Id });

            foreach (var unitId in request.UnitIds ?? new())
            {
                if (!await _db.StudentUnitSubscriptions
                        .AnyAsync(s => s.StudentId == existing.Id && s.UnitId == unitId && s.TeacherId == group.TeacherId))
                    _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = group.TeacherId, StudentId = existing.Id, UnitId = unitId });
            }
            await _db.SaveChangesAsync();

            // NOTE: same tradeoff as Students/link — if this student is
            // already logged in elsewhere, their current access token won't
            // include this new membership until they log in again / refresh.
            return Ok(new
            {
                message = "A student with this phone number already existed, so they were linked to your group instead of creating a duplicate account.",
                student = ToListItem(existing, group.Name)
            });
        }

        var student = new Student
        {
            Name = request.Name,
            PhoneNumber = request.PhoneNumber,
            ParentPhoneNumber = request.ParentPhoneNumber,
            UserName = request.UserName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(
                string.IsNullOrEmpty(request.Password) ? GenerateTempPassword() : request.Password),
            GroupId = request.GroupId,
            SchoolYear = group.SchoolYear
        };

        _db.Students.Add(student);
        await _db.SaveChangesAsync();

        // MULTI-TENANT: keep the membership table in sync with the legacy GroupId
        // from day one, so every student (old or new) is queryable the same way.
        _db.StudentGroupMemberships.Add(new StudentGroupMembership { StudentId = student.Id, GroupId = group.Id });

        foreach (var unitId in request.UnitIds ?? new())
            _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = group.TeacherId, StudentId = student.Id, UnitId = unitId });
        await _db.SaveChangesAsync();

        return StatusCode(201, ToListItem(student, group.Name));
    }

    // POST Students/link  body: { phoneNumber, groupId }
    // MULTI-TENANT: lets a Teacher/AssistantAdmin subscribe an EXISTING student
    // (already registered under a DIFFERENT teacher) to their OWN tenant, by
    // looking them up via phone number and creating a StudentGroupMembership
    // under one of the caller's own Groups. The student keeps their single
    // login/password and can now switch between all their teachers via
    // X-TenantId. Idempotent: linking twice to the same teacher just no-ops.
    // NOTE: Students/Create (POST Students) now does this exact same lookup
    // and merge automatically, so a teacher never has to know in advance
    // whether a student already exists elsewhere — this endpoint is kept as
    // an explicit alternative (e.g. a dedicated "link existing student" UI
    // flow that doesn't collect a Name/etc.) but is no longer required to
    // avoid duplicate accounts.
    [HttpPost("link")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> LinkExistingStudent([FromBody] LinkStudentRequest request)
    {
        var group = await _db.Groups.FirstOrDefaultAsync(g => g.Id == request.GroupId);
        if (group == null) return BadRequest(new { message = "Group not found." });

        // Cross-tenant lookup by design — this student may not belong to the
        // caller's tenant yet, so the normal tenant-scoped filter must be bypassed.
        var student = await _db.Students.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.PhoneNumber == request.PhoneNumber);
        if (student == null)
            return NotFound(new { message = "No student with this phone number exists yet. Use Create instead." });
        if (student.IsSuspended || student.IsCancelled)
            return BadRequest(new { message = "This student's account is suspended or cancelled." });

        var alreadyLinked = await _db.StudentGroupMemberships.IgnoreQueryFilters()
            .AnyAsync(m => m.StudentId == student.Id && m.Group!.TeacherId == group.TeacherId);
        if (!alreadyLinked)
        {
            _db.StudentGroupMemberships.Add(new StudentGroupMembership { StudentId = student.Id, GroupId = group.Id });
            await _db.SaveChangesAsync();
        }

        foreach (var unitId in request.UnitIds ?? new())
        {
            if (!await _db.StudentUnitSubscriptions
                    .AnyAsync(s => s.StudentId == student.Id && s.UnitId == unitId && s.TeacherId == group.TeacherId))
                _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = group.TeacherId, StudentId = student.Id, UnitId = unitId });
        }
        await _db.SaveChangesAsync();

        // NOTE: the student's CURRENT access token won't include this new
        // membership until they log in again or hit /Auth/refresh (same
        // snapshot tradeoff already accepted for groupId/unitIds).
        return Ok(new { message = "Student linked to your account.", studentId = student.Id, studentName = student.Name });
    }

    // GET Students?schoolYear=..&groupId=..&p=..&q=..
    [HttpGet]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetAll(
        [FromQuery] int? schoolYear, [FromQuery] int? groupId,
        [FromQuery] int p = 1, [FromQuery] string? q = null)
        => await ListStudents(s => !s.IsSuspended && !s.IsCancelled, schoolYear, groupId, p, q);

    // GET Students/suspended?schoolYear=..&p=..&q=..
    [HttpGet("suspended")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetSuspended(
        [FromQuery] int? schoolYear, [FromQuery] int p = 1, [FromQuery] string? q = null)
        => await ListStudents(s => s.IsSuspended, schoolYear, null, p, q);

    // GET Students/cancelled?schoolYear=..&p=..&q=..
    [HttpGet("cancelled")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetCancelled(
        [FromQuery] int? schoolYear, [FromQuery] int p = 1, [FromQuery] string? q = null)
        => await ListStudents(s => s.IsCancelled, schoolYear, null, p, q);

    private async Task<IActionResult> ListStudents(
        System.Linq.Expressions.Expression<Func<Student, bool>> statusFilter,
        int? schoolYear, int? groupId, int p, string? q)
    {
        // MULTI-TENANT: filter/display by the Group under the CURRENT tenant, not
        // necessarily the student's legacy single Group (which may belong to a
        // different teacher if the student is also linked elsewhere).
        var query = _db.Students.AsNoTracking().Where(statusFilter);
        if (groupId.HasValue)
            query = query.Where(s => s.GroupMemberships.Any(m => m.GroupId == groupId.Value));
        else if (schoolYear.HasValue) query = query.Where(s => s.SchoolYear == schoolYear.Value);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(s => s.Name.Contains(q) || s.PhoneNumber.Contains(q));

        var students = await query
            .OrderBy(s => s.Name)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .ToListAsync();

        var tenantGroupNames = await _db.GetTenantGroupNamesAsync(students.Select(s => s.Id));
        return Ok(students.Select(s => ToListItem(s, tenantGroupNames.GetValueOrDefault(s.Id))));
    }

    // GET Students/count
    [HttpGet("count")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Count()
        => Ok(await _db.Students.CountAsync(s => !s.IsCancelled));

    // GET Students/{id}
    [HttpGet("{id:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetById(int id)
    {
        var student = await _db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (student == null) return NotFound(new { message = "Student not found." });

        var subs = await _db.StudentUnitSubscriptions.AsNoTracking().Where(u => u.StudentId == id).Select(u => u.UnitId).ToListAsync();

        // MULTI-TENANT: this student's Group/GroupId under the CALLER's own
        // tenant specifically, not necessarily their legacy single Group.
        var tenantMembership = await _db.StudentGroupMemberships.AsNoTracking()
            .Where(m => m.StudentId == id)
            .Select(m => new { m.GroupId, GroupName = m.Group!.Name })
            .FirstOrDefaultAsync();

        return Ok(new
        {
            id = student.Id,
            name = student.Name,
            phoneNumber = student.PhoneNumber,
            parentPhoneNumber = student.ParentPhoneNumber,
            // NOTE: intentionally lowercase "username" (not "userName") — the
            // Flutter profile page reads _student!["username"] exactly.
            username = student.UserName,
            groupId = tenantMembership?.GroupId ?? student.GroupId,
            groupName = tenantMembership?.GroupName,
            schoolYear = student.SchoolYear,
            isSuspended = student.IsSuspended,
            isCancelled = student.IsCancelled,
            createdAt = student.CreatedAt,
            // Client reads this as `data['subscribedUnits'] as List` then `unit['id']` per item.
            subscribedUnits = subs.Select(unitId => new { id = unitId })
        });
    }

    // GET Students/{id}/state-history
    [HttpGet("{id:int}/state-history")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetStateHistory(int id)
    {
        var history = await _db.StateHistoryEntries.AsNoTracking().Where(h => h.StudentId == id)
            .OrderByDescending(h => h.Date)
            .Select(h => new StateHistoryItem(h.Action, null, h.Reason, h.Date))
            .ToListAsync();

        return Ok(history);
    }

    // PUT Students/{id}/group  body: { groupId }
    [HttpPut("{id:int}/group")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangeGroup(int id, [FromBody] ChangeGroupRequest request)
    {
        // `_db.Students`/`_db.Groups` are both tenant-filtered, so this already
        // only matches a student/group that belong to the CALLER's own tenant —
        // safe even now that a student can have groups under several tenants.
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (id));
        if (student == null) return NotFound(new { message = "Student not found." });

        var group = await _db.Groups.FirstOrDefaultAsync(e => e.Id == (request.GroupId));
        if (group == null) return BadRequest(new { message = "Group not found." });

        // MULTI-TENANT: only touch the legacy single GroupId if it currently
        // points at THIS tenant (or is unset) — never let one teacher's group
        // change stomp on the student's group under a different teacher.
        if (student.GroupId == 0 || _tenant.CurrentTenantId == group.TeacherId && !await _db.Groups.IgnoreQueryFilters()
                .AnyAsync(g => g.Id == student.GroupId && g.TeacherId != _tenant.CurrentTenantId))
        {
            student.GroupId = request.GroupId;
        }

        var membership = await _db.StudentGroupMemberships.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.StudentId == id && m.Group!.TeacherId == group.TeacherId);
        if (membership == null)
            _db.StudentGroupMemberships.Add(new StudentGroupMembership { StudentId = id, GroupId = request.GroupId });
        else
            membership.GroupId = request.GroupId;

        await _db.SaveChangesAsync();
        return Ok(new { message = "Group updated." });
    }

    // POST Students/change/password?studentId=..  -> generates + returns a new password
    [HttpPost("change/password")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ResetPassword([FromQuery] int studentId)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (studentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        var newPassword = GenerateTempPassword();
        student.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        await _db.SaveChangesAsync();

        return Ok(new { newPassword });
    }

    // POST Students/change/phone-number?studentId=..&newNumber=..
    [HttpPost("change/phone-number")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangePhoneNumber([FromQuery] int studentId, [FromQuery] string newNumber)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (studentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        student.PhoneNumber = newNumber;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Phone number updated." });
    }

    // POST Students/change/parent-phone-number?studentId=..&newParentPhoneNumber=..
    [HttpPost("change/parent-phone-number")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangeParentPhoneNumber([FromQuery] int studentId, [FromQuery] string newParentPhoneNumber)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (studentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        student.ParentPhoneNumber = newParentPhoneNumber;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Parent phone number updated." });
    }

    // POST Students/change/name?studentId=..&newName=..
    [HttpPost("change/name")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangeName([FromQuery] int studentId, [FromQuery] string newName)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (studentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        student.Name = newName;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Name updated." });
    }

    // POST Students/suspend  body: { studentId, comment }
    [HttpPost("suspend")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Suspend([FromBody] SuspendRequest request)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (request.StudentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        student.IsSuspended = true;
        _db.StateHistoryEntries.Add(new StateHistoryEntry { StudentId = student.Id, Action = "Suspended", Reason = request.Comment });
        await _db.SaveChangesAsync();
        return Ok(new { message = "Student suspended." });
    }

    // POST Students/reactivate  body: { studentId, comment }
    [HttpPost("reactivate")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Reactivate([FromBody] ReactivateRequest request)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (request.StudentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        student.IsSuspended = false;
        student.IsCancelled = false;
        _db.StateHistoryEntries.Add(new StateHistoryEntry { StudentId = student.Id, Action = "Reactivated", Reason = request.Comment });
        await _db.SaveChangesAsync();
        return Ok(new { message = "Student reactivated." });
    }

    // POST Students/delete?studentId=..
    [HttpPost("delete")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Delete([FromQuery] int studentId)
    {
        var student = await _db.Students.FirstOrDefaultAsync(e => e.Id == (studentId));
        if (student == null) return NotFound(new { message = "Student not found." });

        _db.Students.Remove(student);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Student deleted." });
    }

    // POST Students/codes  body: { code }  (student redeems a code)
    [HttpPost("codes")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> RedeemCode([FromBody] RedeemCodeRequest request)
    {
        var code = await _db.Codes.FirstOrDefaultAsync(c => c.Value == request.Code);
        if (code == null) return NotFound(new { message = "Invalid code." });
        if (code.IsUsed) return Conflict(new { message = "Code already used." });

        var studentId = User.GetUserId();
        code.IsUsed = true;
        code.UsedByStudentId = studentId;
        code.UsedAt = DateTime.UtcNow;

        foreach (var unitId in code.UnitIds)
        {
            if (!await _db.StudentUnitSubscriptions.AnyAsync(s => s.StudentId == studentId && s.UnitId == unitId))
                _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = code.TeacherId, StudentId = studentId, UnitId = unitId });
        }

        // BUGFIX: code.LectureIds (standalone/no-unit lectures, e.g. an online
        // lecture created without a Unit) used to be returned in the response
        // but never actually persisted anywhere — the standalone lecture never
        // became tied to this student. Meanwhile LecturesController.ByGroup let
        // EVERY noUnitOnly lecture through untouched for students, no gate at
        // all, so codes for standalone lectures never restricted anything.
        // Now we record the unlock here, and ByGroup gates on it below.
        foreach (var lectureId in code.LectureIds)
        {
            if (!await _db.StudentLectureUnlocks.AnyAsync(u => u.StudentId == studentId && u.LectureId == lectureId))
                _db.StudentLectureUnlocks.Add(new StudentLectureUnlock { TeacherId = code.TeacherId, StudentId = studentId, LectureId = lectureId });
        }

        await _db.SaveChangesAsync();
        return Ok(new { message = "Code redeemed successfully.", unitIds = code.UnitIds, lectureIds = code.LectureIds });
    }

    // GET Students/attendance?studentId=..&unitId=..  (teacher, for a specific student)
    // GET Students/attendance?unitId=..                (student, own attendance)
    //
    // Response: one row PER LECTURE the student's group (optionally narrowed to a
    // unit) is scheduled for, each with `isAttended: true|false` — NOT just the
    // lectures the student actually attended. The Flutter client reads exactly
    // `lec['lectureName']` and `lec['isAttended'] ?? false` and computes
    // attended/absent counts locally by filtering this array, so an absent
    // lecture still needs to appear here with isAttended == false.
    [HttpGet("attendance")]
    public async Task<IActionResult> GetAttendance([FromQuery] int? studentId, [FromQuery] int? unitId)
    {
        var sid = User.IsInRole(Roles.Student) ? User.GetUserId() : studentId;
        if (sid == null) return BadRequest(new { message = "studentId is required." });

        var student = await _db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid.Value);
        if (student == null) return NotFound(new { message = "Student not found." });

        // Lectures scheduled for this student's group. Attendance-taking only
        // makes sense for Center (on-site) lectures — Online lectures are
        // watched on-demand and were never meant to show up here at all, but
        // this query used to return both, so a student's attendance summary
        // counted Online lectures as "absent" even though nobody ever takes
        // attendance for them.
        // MULTI-TENANT: lectures live under a specific tenant already (_db.Lectures
        // is tenant-filtered), so we need THIS student's group under the CURRENT
        // tenant specifically -- not their legacy single GroupId, which may
        // belong to a different teacher entirely.
        var tenantGroupId = await _db.StudentGroupMemberships.AsNoTracking()
            .Where(m => m.StudentId == sid.Value)
            .Select(m => (int?)m.GroupId)
            .FirstOrDefaultAsync() ?? student.GroupId;

        var lecturesQuery = _db.Lectures.AsNoTracking()
            .Where(l => _db.LectureGroupLinks.Any(x => x.LectureId == l.Id && x.GroupId == tenantGroupId)
                && l.AttendanceMethod == AttendanceMethod.Center);
        if (unitId.HasValue) lecturesQuery = lecturesQuery.Where(l => l.UnitId == unitId.Value);

        var lectures = await lecturesQuery.OrderBy(l => l.CreatedAt).ToListAsync();

        var attendedLookup = await _db.Attendances.AsNoTracking()
            .Where(a => a.StudentId == sid.Value)
            .ToDictionaryAsync(a => a.LectureId, a => a.Date);

        var result = lectures.Select(l => new AttendanceRecordDto(
            l.Id,
            l.Name,
            attendedLookup.ContainsKey(l.Id),
            attendedLookup.TryGetValue(l.Id, out var date) ? date : null));

        return Ok(result);
    }

    // GET Students/qr  (student) -> returns a quoted base64-encoded PNG QR string
    [HttpGet("qr")]
    [Authorize(Roles = Roles.Student)]
    public IActionResult GetQr()
    {
        var studentId = User.GetUserId();
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(studentId.ToString(), QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(data);
        var bytes = qrCode.GetGraphic(10);
        var base64 = Convert.ToBase64String(bytes);

        // Client does: response.body.trim().replaceAll('"', '') then base64Decode(...)
        return Content($"\"{base64}\"", "application/json", Encoding.UTF8);
    }

    // POST Students/subscribe/unit  body: { studentId, unitId }
    [HttpPost("subscribe/unit")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> SubscribeUnit([FromBody] UnitSubscriptionRequest request)
    {
        // SECURITY FIX: previously this had NO check that the Unit even belongs
        // to the caller's own tenant, nor that the student is actually linked to
        // that teacher — a caller could subscribe ANY student to ANY unit from
        // ANY teacher. Both are now enforced. `_db.Units` is already
        // tenant-filtered, so a unitId from another tenant simply won't be found.
        var unit = await _db.Units.FirstOrDefaultAsync(u => u.Id == request.UnitId);
        if (unit == null) return NotFound(new { message = "Unit not found." });

        var isLinked = await _db.StudentGroupMemberships.IgnoreQueryFilters()
            .AnyAsync(m => m.StudentId == request.StudentId && m.Group!.TeacherId == unit.TeacherId);
        if (!isLinked)
            return BadRequest(new
            {
                message = "This student isn't linked to your account yet. Use POST Students/link first."
            });

        if (!await _db.StudentUnitSubscriptions
                .AnyAsync(s => s.StudentId == request.StudentId && s.UnitId == request.UnitId))
        {
            _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = unit.TeacherId, StudentId = request.StudentId, UnitId = request.UnitId });
            await _db.SaveChangesAsync();
        }
        return Ok(new { message = "Subscribed." });
    }

    // POST Students/unsubscribe/unit  body: { studentId, unitId }
    [HttpPost("unsubscribe/unit")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> UnsubscribeUnit([FromBody] UnitSubscriptionRequest request)
    {
        var sub = await _db.StudentUnitSubscriptions
            .FirstOrDefaultAsync(s => s.StudentId == request.StudentId && s.UnitId == request.UnitId);
        if (sub != null)
        {
            _db.StudentUnitSubscriptions.Remove(sub);
            await _db.SaveChangesAsync();
        }
        return Ok(new { message = "Unsubscribed." });
    }

    // GET Students/{notebookId}/payments?studentId=..
    [HttpGet("{notebookId:int}/payments")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetNotebookPaymentsForStudent(int notebookId, [FromQuery] int studentId)
    {
        var payments = await _db.NotebookPayments.AsNoTracking()
            .Where(p => p.NotebookId == notebookId && p.StudentId == studentId)
            .Select(p => new { id = p.Id, price = p.Price, discountedPrice = p.DiscountedPrice, date = p.Date })
            .ToListAsync();

        return Ok(payments);
    }

    // POST Students/{notebookId}/pay  body: { studentId, amount, lectureId }
    [HttpPost("{notebookId:int}/pay")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> PayNotebook(int notebookId, [FromBody] PayNotebookRequest request)
    {
        var notebook = await _db.Notebooks.FirstOrDefaultAsync(e => e.Id == (notebookId));
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        var payment = new NotebookPayment
        {
            TeacherId = notebook.TeacherId,
            NotebookId = notebookId,
            StudentId = request.StudentId,
            Price = request.Amount
        };
        _db.NotebookPayments.Add(payment);
        await _db.SaveChangesAsync();

        return StatusCode(201, new { id = payment.Id, message = "Payment recorded." });
    }

    // GET Students/{notebookId}/discount?studentId=..
    // Returns the notebook's info + this student's payment summary for it.
    // Added alongside the existing POST of the same route (which applies a
    // discount) — the client fetches this first to show notebook details.
    [HttpGet("{notebookId:int}/discount")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetNotebookDiscountInfo(int notebookId, [FromQuery] int studentId)
    {
        var notebook = await _db.Notebooks.AsNoTracking().FirstOrDefaultAsync(e => e.Id == (notebookId));
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        var payments = await _db.NotebookPayments.AsNoTracking()
            .Where(p => p.NotebookId == notebookId && p.StudentId == studentId)
            .OrderByDescending(p => p.Date)
            .ToListAsync();

        var paid = payments.Sum(p => p.Price);
        var discountedPrice = payments.FirstOrDefault(p => p.DiscountedPrice.HasValue)?.DiscountedPrice
                               ?? notebook.Price;

        return Ok(new
        {
            notebook = new { name = notebook.Name, price = notebook.Price, paid },
            discountedPrice
        });
    }

    // POST Students/{notebookId}/discount?studentId=..&discountedPrice=..
    [HttpPost("{notebookId:int}/discount")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ApplyDiscount(int notebookId, [FromQuery] int studentId, [FromQuery] int discountedPrice)
    {
        var notebook = await _db.Notebooks.FirstOrDefaultAsync(e => e.Id == (notebookId));
        if (notebook == null) return NotFound(new { message = "Notebook not found." });

        var payment = new NotebookPayment
        {
            TeacherId = notebook.TeacherId,
            NotebookId = notebookId,
            StudentId = studentId,
            Price = notebook.Price,
            DiscountedPrice = discountedPrice
        };
        _db.NotebookPayments.Add(payment);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Discount applied.", discountedPrice });
    }

    // GET Students/quiz-results/center?studentId=..  (teacher) / no params (student, own)
    [HttpGet("quiz-results/center")]
    public async Task<IActionResult> GetCenterQuizResults([FromQuery] int? studentId)
    {
        var sid = User.IsInRole(Roles.Student) ? User.GetUserId() : studentId;
        if (sid == null) return BadRequest(new { message = "studentId is required." });

        var results = await _db.CenterQuizResults.AsNoTracking().Where(r => r.StudentId == sid.Value)
            .Select(r => new { id = r.Id, title = r.Title, mark = r.Marks, quizTotalMarks = r.TotalMarks, date = r.Date })
            .ToListAsync();

        return Ok(results);
    }

    // POST Students/quiz-results/center/add  body: { studentId, quizTotalMarks, mark }
    [HttpPost("quiz-results/center/add")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> AddCenterQuizResult([FromBody] AddCenterQuizResultRequest request)
    {
        if (_tenant.CurrentTenantId == null) return Forbid();

        var result = new CenterQuizResult
        {
            StudentId = request.StudentId,
            Title = "Center Quiz",
            Marks = request.Mark,
            TotalMarks = request.QuizTotalMarks,
            TeacherId = _tenant.CurrentTenantId.Value
        };
        _db.CenterQuizResults.Add(result);
        await _db.SaveChangesAsync();
        return StatusCode(201, new { id = result.Id });
    }

    // POST Students/quiz-results/change?studentId=..&quizResultId=..&newMark=..  body: {}
    [HttpPost("quiz-results/change")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangeCenterQuizMark([FromQuery] int studentId, [FromQuery] int quizResultId, [FromQuery] int newMark)
    {
        var result = await _db.CenterQuizResults.FirstOrDefaultAsync(r => r.Id == quizResultId && r.StudentId == studentId);
        if (result == null) return NotFound(new { message = "Result not found." });

        result.Marks = newMark;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Mark updated." });
    }

    // POST Students/quiz-results/delete?studentId=..&quizResultId=..
    [HttpPost("quiz-results/delete")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> DeleteCenterQuizResult([FromQuery] int studentId, [FromQuery] int quizResultId)
    {
        var result = await _db.CenterQuizResults.FirstOrDefaultAsync(r => r.Id == quizResultId && r.StudentId == studentId);
        if (result == null) return NotFound(new { message = "Result not found." });

        _db.CenterQuizResults.Remove(result);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Result deleted." });
    }

    // GET Students/homework-results?studentId=..  (teacher) / no params (student, own)
    [HttpGet("homework-results")]
    public async Task<IActionResult> GetHomeworkResults([FromQuery] int? studentId)
    {
        var sid = User.IsInRole(Roles.Student) ? User.GetUserId() : studentId;
        if (sid == null) return BadRequest(new { message = "studentId is required." });

        var results = await _db.HomeworkResults.AsNoTracking().Where(r => r.StudentId == sid.Value)
            .Select(r => new { id = r.Id, title = r.Title, mark = r.Marks, totalMarks = r.TotalMarks, date = r.Date })
            .ToListAsync();

        return Ok(results);
    }

    // POST Students/homework-results/add  body: { studentId, totalMarks, mark }
    [HttpPost("homework-results/add")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> AddHomeworkResult([FromBody] AddHomeworkResultRequest request)
    {
        if (_tenant.CurrentTenantId == null) return Forbid();

        var result = new HomeworkResult
        {
            StudentId = request.StudentId,
            Title = "Homework",
            Marks = request.Mark,
            TotalMarks = request.TotalMarks,
            TeacherId = _tenant.CurrentTenantId.Value
        };
        _db.HomeworkResults.Add(result);
        await _db.SaveChangesAsync();
        return StatusCode(201, new { id = result.Id });
    }

    // POST Students/homework-results/change?studentId=..&homeworkResultId=..&newMark=..  body: {}
    [HttpPost("homework-results/change")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> ChangeHomeworkMark([FromQuery] int studentId, [FromQuery] int homeworkResultId, [FromQuery] int newMark)
    {
        var result = await _db.HomeworkResults.FirstOrDefaultAsync(r => r.Id == homeworkResultId && r.StudentId == studentId);
        if (result == null) return NotFound(new { message = "Result not found." });

        result.Marks = newMark;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Mark updated." });
    }

    // POST Students/homework-results/delete?studentId=..&homeworkResultId=..
    [HttpPost("homework-results/delete")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> DeleteHomeworkResult([FromQuery] int studentId, [FromQuery] int homeworkResultId)
    {
        var result = await _db.HomeworkResults.FirstOrDefaultAsync(r => r.Id == homeworkResultId && r.StudentId == studentId);
        if (result == null) return NotFound(new { message = "Result not found." });

        _db.HomeworkResults.Remove(result);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Result deleted." });
    }

    // GET Students/quiz-results/online  (student, own online-quiz history)
    [HttpGet("quiz-results/online")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> GetOnlineQuizResults()
    {
        var studentId = User.GetUserId();
        // PERFORMANCE: no .Include(r => r.Answers) here -- the projection below
        // only reads scalar columns off QuizResult itself, so pulling in the
        // Answers collection would mean a needless extra join/round trip for
        // data that's thrown away immediately.
        var results = await _db.QuizResults.AsNoTracking()
            .Where(r => r.StudentId == studentId)
            .Select(r => new { id = r.Id, quizId = r.QuizId, mark = r.Score, quizTotalMarks = r.TotalMarks, date = r.GradedAt })
            .ToListAsync();

        return Ok(results);
    }

    private static string GenerateTempPassword() => Guid.NewGuid().ToString("N")[..8];

    private static StudentListItem ToListItem(Student s, string? groupName)
    {
        var status = s.IsCancelled ? "Cancelled" : s.IsSuspended ? "Suspended" : "Active";
        return new(
            s.Id, s.Name, s.PhoneNumber, s.UserName, s.GroupId, groupName, s.IsSuspended, s.IsCancelled,
            status, null);
    }
}
