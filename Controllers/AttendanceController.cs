using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

// POST Attendance?lectureId=..  body: { encodedStudentId } OR { studentId }, plus optional { date },
// plus optional { autoSubscribe } (default false).
// (single scan uses encodedStudentId; manual teacher entry uses studentId + date)
// "autoSubscribe": when true and the lecture has a UnitId, recording this
// student's attendance also subscribes them to that Unit (if not already
// subscribed). Decided per-request/per-student, not per-lecture.
// StudentIdentifier (optional): a hand-typed ID, phone number, or name for
// the manual-entry path -- see StudentIdentifierResolver. When present it
// takes priority over StudentId, which stays available for backward
// compatibility with older clients that only ever send the numeric id.
public record RecordAttendanceRequest(string? EncodedStudentId, int? StudentId, DateTime? Date, bool AutoSubscribe = false, string? StudentIdentifier = null);

// POST Attendance/bulk?lectureId=..  body: [ { encodedStudentId, date, autoSubscribe }, ... ]
// OR [ { studentId, date, autoSubscribe }, ... ] — same encodedStudentId/studentId
// duality as the single Record endpoint, decided per item.
// "autoSubscribe" is per-item, so a single bulk call can open the lecture's
// Unit for some students and not others.
public record BulkAttendanceItem(string? EncodedStudentId, int? StudentId, DateTime Date, bool AutoSubscribe = false, string? StudentIdentifier = null);

/// <summary>
/// Route: api/Attendance
///  POST Attendance?lectureId=..
///  POST Attendance/bulk?lectureId=..
/// The "encodedStudentId" is the payload embedded in each student's QR code
/// (in this reconstruction: their numeric student id).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
public class AttendanceController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IWhatsAppService _whatsApp;
    private readonly ILogger<AttendanceController> _logger;

    public AttendanceController(AppDbContext db, ITenantContext tenant, IWhatsAppService whatsApp, ILogger<AttendanceController> logger)
    {
        _db = db;
        _tenant = tenant;
        _whatsApp = whatsApp;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Record([FromQuery] int lectureId, [FromBody] RecordAttendanceRequest request)
    {
        if (_tenant.CurrentTenantId == null) return Forbid();

        if (string.IsNullOrWhiteSpace(request.EncodedStudentId) && request.StudentId == null && string.IsNullOrWhiteSpace(request.StudentIdentifier))
            return BadRequest(new { message = "Either encodedStudentId or studentId/studentIdentifier must be provided." });

        int? studentId;
        string encodedStudentId;

        if (!string.IsNullOrWhiteSpace(request.EncodedStudentId))
        {
            // QR-scan path: resolve + validate the encoded payload as before.
            studentId = await ResolveStudentIdAsync(request.EncodedStudentId);
            encodedStudentId = request.EncodedStudentId;
        }
        else
        {
            // Manual teacher-entry path: StudentIdentifier (a hand-typed ID,
            // phone number, or name -- used by the offline attendance flow)
            // resolves via ResolveManualStudentIdAsync; older clients that
            // only send the numeric StudentId keep working via the
            // fallback. We still store the originally-typed value as the
            // "encoded" value too, since that column is required and a
            // plain id/phone/name is a valid QR payload shape.
            var identifier = !string.IsNullOrWhiteSpace(request.StudentIdentifier)
                ? request.StudentIdentifier
                : request.StudentId!.Value.ToString();
            studentId = await ResolveManualStudentIdAsync(identifier);
            encodedStudentId = identifier;
        }

        if (studentId == null) return NotFound(new { message = "Student not found for this code." });

        // VALIDATION: student's school year must match the lecture's school
        // year. If the lecture has no SchoolYear set, it's not year-specific
        // so no check is applied.
        var lectureSchoolYear = await _db.Lectures.Where(l => l.Id == lectureId).Select(l => l.SchoolYear).FirstOrDefaultAsync();
        if (lectureSchoolYear.HasValue)
        {
            var studentSchoolYear = await _db.Students.Where(s => s.Id == studentId.Value).Select(s => (int?)s.SchoolYear).FirstOrDefaultAsync();
            if (studentSchoolYear.HasValue && studentSchoolYear.Value != lectureSchoolYear.Value)
                return BadRequest(new { message = "Student's school year does not match the lecture's school year." });
        }

        // NOTE: the Flutter client (onlineScan.dart) checks `statusCode == 400`
        // specifically to show "الطالب مسجل مسبقًا" — it does NOT treat 409 as
        // that case. Must return 400 here, not Conflict(409), or the client
        // falls through to its generic error branch instead.
        if (await _db.Attendances.AnyAsync(a => a.LectureId == lectureId && a.StudentId == studentId.Value))
            return BadRequest(new { message = "Attendance already recorded." });

        var attendance = new Attendance
        {
            TeacherId = _tenant.CurrentTenantId.Value,
            LectureId = lectureId,
            StudentId = studentId.Value,
            EncodedStudentId = encodedStudentId,
        };
        if (request.Date.HasValue) attendance.Date = request.Date.Value;

        _db.Attendances.Add(attendance);
        if (request.AutoSubscribe)
            await AutoSubscribeIfSubscriptionLectureAsync(lectureId, studentId.Value);
        await IssueTriggeredCodesAsync(lectureId, studentId.Value);
        await _db.SaveChangesAsync();

        await SendAttendanceWhatsAppAsync(studentId.Value, attendance.Date);

        return StatusCode(201, new { message = "Attendance recorded." });
    }

    [HttpPost("bulk")]
    public async Task<IActionResult> RecordBulk([FromQuery] int lectureId, [FromBody] List<BulkAttendanceItem> items)
    {
        if (_tenant.CurrentTenantId == null) return Forbid();

        var created = 0;
        var notifyList = new List<(int StudentId, DateTime Date)>();
        // Per-item outcome so the caller (offline sync in the Flutter app)
        // can tell exactly which students actually got saved and which
        // didn't (and why) instead of only seeing an overall 200 OK — a
        // missing/duplicate student id here used to be silently skipped
        // and reported back to the teacher as "uploaded successfully".
        var savedStudentIds = new List<int>();
        // Same content as savedStudentIds but keyed to whatever the CALLER
        // originally sent for each saved item (their StudentIdentifier, or
        // numeric StudentId, or raw encoded payload -- same value as
        // "requestedId" below) instead of the resolved DB id. Needed because
        // a manually-typed identifier (phone/name) does not equal the
        // resolved Student.Id, so the offline client can't reconcile its
        // local cache against savedStudentIds alone once StudentIdentifier
        // is in use.
        var savedIdentifiers = new List<object>();
        var failed = new List<object>();

        // VALIDATION: same school-year check as the single Record endpoint,
        // fetched once here since lectureId is the same for the whole batch.
        var lectureSchoolYear = await _db.Lectures.Where(l => l.Id == lectureId).Select(l => l.SchoolYear).FirstOrDefaultAsync();

        foreach (var item in items)
        {
            // "requestedId" is whatever identifies this item to the caller
            // (their own StudentIdentifier/studentId if given, otherwise the
            // raw encoded payload) so a failure can still be reported back
            // even when we never manage to resolve it to a real student id.
            object requestedId = !string.IsNullOrWhiteSpace(item.StudentIdentifier)
                ? item.StudentIdentifier
                : item.StudentId.HasValue ? item.StudentId.Value : (item.EncodedStudentId ?? "");

            if (string.IsNullOrWhiteSpace(item.EncodedStudentId) && item.StudentId == null && string.IsNullOrWhiteSpace(item.StudentIdentifier))
            {
                failed.Add(new { studentId = requestedId, reason = "Missing encodedStudentId/studentId/studentIdentifier." });
                continue;
            }

            int? studentId;
            string encodedStudentId;

            if (!string.IsNullOrWhiteSpace(item.EncodedStudentId))
            {
                // QR-scan path: resolve + validate the encoded payload as before.
                studentId = await ResolveStudentIdAsync(item.EncodedStudentId);
                encodedStudentId = item.EncodedStudentId;
            }
            else
            {
                // Manual teacher-entry path: StudentIdentifier (a hand-typed
                // ID, phone number, or name -- used by the offline bulk sync
                // flow) resolves via ResolveManualStudentIdAsync; older
                // clients that only send the numeric StudentId keep working
                // via the fallback.
                var identifier = !string.IsNullOrWhiteSpace(item.StudentIdentifier)
                    ? item.StudentIdentifier
                    : item.StudentId!.Value.ToString();
                studentId = await ResolveManualStudentIdAsync(identifier);
                encodedStudentId = identifier;
            }

            if (studentId == null)
            {
                failed.Add(new { studentId = requestedId, reason = "Student not found." });
                continue;
            }

            if (lectureSchoolYear.HasValue)
            {
                var studentSchoolYear = await _db.Students.Where(s => s.Id == studentId.Value).Select(s => (int?)s.SchoolYear).FirstOrDefaultAsync();
                if (studentSchoolYear.HasValue && studentSchoolYear.Value != lectureSchoolYear.Value)
                {
                    failed.Add(new { studentId = requestedId, reason = "Student's school year does not match the lecture's school year." });
                    continue;
                }
            }

            if (await _db.Attendances.AnyAsync(a => a.LectureId == lectureId && a.StudentId == studentId.Value))
            {
                failed.Add(new { studentId = requestedId, reason = "Attendance already recorded." });
                continue;
            }

            _db.Attendances.Add(new Attendance
            {
                TeacherId = _tenant.CurrentTenantId.Value,
                LectureId = lectureId,
                StudentId = studentId.Value,
                EncodedStudentId = encodedStudentId,
                Date = item.Date
            });
            if (item.AutoSubscribe)
                await AutoSubscribeIfSubscriptionLectureAsync(lectureId, studentId.Value);
            await IssueTriggeredCodesAsync(lectureId, studentId.Value);
            notifyList.Add((studentId.Value, item.Date));
            savedStudentIds.Add(studentId.Value);
            savedIdentifiers.Add(requestedId);
            created++;
        }

        await _db.SaveChangesAsync();

        foreach (var (studentId, date) in notifyList)
            await SendAttendanceWhatsAppAsync(studentId, date);

        return Ok(new
        {
            message = $"{created} attendance record(s) saved.",
            saved = created,
            total = items.Count,
            savedStudentIds,
            savedIdentifiers,
            failed
        });
    }

    // FEATURE: "autoSubscribe" is now decided per attendance record (per
    // student, per scan/entry) instead of being a fixed flag on the
    // Lecture. The caller (Record / RecordBulk) only invokes this when the
    // request/item explicitly asked for it. If the lecture has a UnitId,
    // recording this student's attendance also subscribes them to that
    // Unit, even if they weren't enrolled in it before. We just add the
    // pending StudentUnitSubscription row here; SaveChangesAsync at the end
    // of the calling action persists it together with the Attendance row.
    private async Task AutoSubscribeIfSubscriptionLectureAsync(int lectureId, int studentId)
    {
        var lecture = await _db.Lectures.AsNoTracking()
            .Where(l => l.Id == lectureId)
            .Select(l => new { l.UnitId, l.TeacherId })
            .FirstOrDefaultAsync();

        if (lecture == null || lecture.UnitId == null) return;

        var unitId = lecture.UnitId.Value;
        var alreadySubscribed = await _db.StudentUnitSubscriptions
            .AnyAsync(s => s.StudentId == studentId && s.UnitId == unitId);
        if (alreadySubscribed) return;

        _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription
        {
            TeacherId = lecture.TeacherId,
            StudentId = studentId,
            UnitId = unitId
        });
    }

    // FEATURE: a Code can be created as a TEMPLATE tied to a Center lecture
    // (Code.IsTemplate + Code.TriggerLectureId — see CodesController.Generate).
    // Every time a student attends that lecture, we clone the template into a
    // brand-new, already-assigned Code row just for them (unique Value,
    // IsUsed = true, UsedByStudentId = them from the start) — so it behaves
    // exactly as if they'd redeemed a one-off code themselves, without ever
    // exposing a shared code string another student could grab. Visible to
    // the student via Students/codes (GetStudentCodes); never to anyone else.
    // FIX: manual entry (typed studentId, not scanned via QR) treated the
    // number as a hard student.Id lookup. In practice a teacher typing by
    // hand very often types the STUDENT'S PHONE NUMBER — or even their name
    // — instead of their internal id. This now goes through the shared
    // StudentIdentifierResolver (ID -> PhoneNumber -> Arabic-normalized
    // name; see its doc comment), called with ignoreTenantFilter: true to
    // preserve this method's original cross-tenant behavior (a manually
    // entered identifier can resolve to a student under ANY tenant, same
    // as the existing Id check always did here).
    private Task<int?> ResolveManualStudentIdAsync(string? identifier)
        => Common.StudentIdentifierResolver.ResolveAsync(_db, identifier, ignoreTenantFilter: true);

    private async Task IssueTriggeredCodesAsync(int lectureId, int studentId)
    {
        var templates = await _db.Codes
            .Where(c => c.IsTemplate && c.TriggerLectureId == lectureId)
            .ToListAsync();

        foreach (var template in templates)
        {
            // One clone per student per template, ever — re-attending (not
            // that duplicate Attendance rows are even possible, see the 400
            // check above) or attending a re-created lecture with the same
            // template must never mint a second code for the same student.
            var alreadyIssued = await _db.Codes.AnyAsync(c =>
                c.SourceCodeTemplateId == template.Id && c.UsedByStudentId == studentId);
            if (alreadyIssued) continue;

            var issued = new Code
            {
                Value = await CodeGenerator.GenerateUniqueAsync(_db),
                SchoolYear = template.SchoolYear,
                UnitIds = template.UnitIds,
                LectureIds = template.LectureIds,
                TeacherId = template.TeacherId,
                SourceCodeTemplateId = template.Id,
                IsUsed = true,
                UsedByStudentId = studentId,
                UsedAt = DateTime.UtcNow
            };
            _db.Codes.Add(issued);

            // Same unlock-granting behavior as StudentsController.RedeemCode —
            // the code is issued already "redeemed", so apply its effects
            // immediately instead of waiting for a redeem call that will
            // never come.
            foreach (var unitId in issued.UnitIds)
            {
                if (!await _db.StudentUnitSubscriptions.AnyAsync(s => s.StudentId == studentId && s.UnitId == unitId))
                    _db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = template.TeacherId, StudentId = studentId, UnitId = unitId });
            }
            foreach (var lecId in issued.LectureIds)
            {
                if (!await _db.StudentLectureUnlocks.AnyAsync(u => u.StudentId == studentId && u.LectureId == lecId))
                    _db.StudentLectureUnlocks.Add(new StudentLectureUnlock { TeacherId = template.TeacherId, StudentId = studentId, LectureId = lecId });
            }
        }
    }

    // NOTE: Students carry a tenant-scoped global query filter (visible only
    // if already linked to this teacher via Group/Unit/Lecture/OnlineLesson).
    // That filter must be bypassed here with IgnoreQueryFilters(), otherwise
    // a student who ISN'T subscribed yet can never be resolved -- which
    // breaks autoSubscribe entirely (its whole purpose is to subscribe
    // students who aren't subscribed yet). We only use this to confirm the
    // id is a real student; every downstream write (attendance row, the
    // auto-subscribe insert) is still scoped correctly to this teacher's
    // tenant.
    private async Task<int?> ResolveStudentIdAsync(string encodedStudentId)
    {
        if (int.TryParse(encodedStudentId, out var id) &&
            await _db.Students.IgnoreQueryFilters().AnyAsync(s => s.Id == id))
            return id;
        return null;
    }

    // FEATURE: fires the "attendance recorded" WhatsApp message to the
    // student's parent right after the Attendance row is saved. Best-effort
    // only: any failure here (missing phone, GreenApi down, etc.) is logged
    // and swallowed — it must never fail the attendance request itself,
    // since SaveChangesAsync has already committed by the time this runs.
    private async Task SendAttendanceWhatsAppAsync(int studentId, DateTime attendanceDate)
    {
        try
        {
            var student = await _db.Students.AsNoTracking()
                .Where(s => s.Id == studentId)
                .Select(s => new { s.Name, s.ParentPhoneNumber })
                .FirstOrDefaultAsync();

            if (student == null || string.IsNullOrWhiteSpace(student.ParentPhoneNumber))
            {
                _logger.LogInformation("Skipping WhatsApp notification for student {StudentId}: no parent phone number on file.", studentId);
                return;
            }

            var teacherName = await _db.Teachers.AsNoTracking()
                .Where(t => t.Id == _tenant.CurrentTenantId!.Value)
                .Select(t => t.Name)
                .FirstOrDefaultAsync() ?? "المدرس";

            // Last grade: from CenterQuizResults ONLY -- this is exactly the
            // "امتحان" column recorded via the teacher's "درجات الطالب"
            // screen (Students/quiz-results/center/add), same source
            // StudentGradesPage reads from. Deliberately NOT the online-quiz
            // QuizResults table -- that's a different feature (online exams
            // with the teacher), not what shows up on this grades screen.
            // TENANT FIX: also filtered by TeacherId == current tenant -- a
            // student can be enrolled with more than one teacher, and
            // CenterQuizResult/HomeworkResult both carry a TeacherId
            // specifically so one teacher's marks never leak into another's
            // attendance notification for the same shared student.
            var lastCenterQuizRaw = await _db.CenterQuizResults.AsNoTracking()
                .Where(q => q.StudentId == studentId && q.TeacherId == _tenant.CurrentTenantId!.Value)
                .OrderByDescending(q => q.Date)
                .Select(q => new { q.Marks, q.TotalMarks, q.Date })
                .FirstOrDefaultAsync();
            // Formatted in memory (not inside the SQL-translated Select above)
            // because MarksFormatter isn't translatable to SQL -- it just
            // trims a whole mark's trailing ".0" (9.0 -> "9", 9.5 stays "9.5").
            var lastGradeText = lastCenterQuizRaw == null
                ? "لا يوجد"
                : $"{MarksFormatter.Format(lastCenterQuizRaw.Marks)}/{lastCenterQuizRaw.TotalMarks}";

            // Last homework: from HomeworkResults ONLY -- the "الواجب" column
            // on the same "درجات الطالب" screen (Students/homework-results/add).
            // Deliberately NOT AssignmentSubmissions -- that's the separate
            // Assignment Centers feature, not this grades screen. Same
            // TeacherId tenant filter as above, same reason.
            var lastHomeworkRaw = await _db.HomeworkResults.AsNoTracking()
                .Where(h => h.StudentId == studentId && h.TeacherId == _tenant.CurrentTenantId!.Value)
                .OrderByDescending(h => h.Date)
                .Select(h => new { h.Marks, h.TotalMarks, h.Date })
                .FirstOrDefaultAsync();
            var lastHomeworkText = lastHomeworkRaw == null
                ? "لا يوجد"
                : $"{MarksFormatter.Format(lastHomeworkRaw.Marks)}/{lastHomeworkRaw.TotalMarks}";

            // Notebook status: only mention a notebook if the teacher actually
            // uploaded one for a group this student belongs to. Previously this
            // just checked NotebookPayments -- so a student with no notebook at
            // all (teacher never created one for their group) still silently
            // got "لم يتم الدفع", which is misleading. Now: resolve the
            // student's groups, find the teacher's most relevant notebook for
            // those groups (most recently created), and only then report a
            // paid/unpaid status that includes the notebook's name. If the
            // teacher hasn't uploaded/created a notebook for this student's
            // group(s) at all, leave the text empty -- nothing to report.
            var studentGroupIds = await _db.StudentGroupMemberships.AsNoTracking()
                .Where(m => m.StudentId == studentId && m.Group!.TeacherId == _tenant.CurrentTenantId!.Value)
                .Select(m => m.GroupId)
                .ToListAsync();

            var candidateNotebooks = await _db.Notebooks.AsNoTracking()
                .Where(n => n.TeacherId == _tenant.CurrentTenantId!.Value)
                .Select(n => new { n.Id, n.Name, n.GroupIdsCsv, n.CreatedAt })
                .ToListAsync();
            var relevantNotebook = candidateNotebooks
                .Where(n => n.GroupIdsCsv.Length > 0
                    && n.GroupIdsCsv.Split(',').Select(int.Parse).Any(studentGroupIds.Contains))
                .OrderByDescending(n => n.CreatedAt)
                .FirstOrDefault();

            // The WhatsApp template's last line is just {{notebook_line}} on
            // its own (no surrounding static text -- see WhatsAppOptions
            // doc comment), so we compose the WHOLE line here. It always ends
            // with the same encouraging closing sentence (with the teacher's
            // name baked in) -- with a notebook mention prepended only when
            // one actually exists. Never empty, so no need for the old
            // Meta-rejects-empty-string workaround.
            const string closingText =
                "🤝 احنا دايمًا مع حضرتك خطوة بخطوة، " +
                "و{0} دايمًا مع ابن/ة حضراتكم لحد باب الامتحان 🎯📚";
            var closing = string.Format(closingText, teacherName);

            string notebookLineText;
            if (relevantNotebook == null)
            {
                notebookLineText = closing;
            }
            else
            {
                var hasPaidNotebook = await _db.NotebookPayments.AsNoTracking()
                    .AnyAsync(p => p.StudentId == studentId && p.NotebookId == relevantNotebook.Id);
                var status = hasPaidNotebook ? "✅ تم الدفع" : "❌ لم يتم الدفع";
                notebookLineText = $"📒 حالة المذكرة: {relevantNotebook.Name} - {status}\n\n{closing}";
            }

            var data = new AttendanceWhatsAppNotification(
                StudentName: student.Name,
                TeacherName: teacherName,
                AttendanceLocalTime: attendanceDate,
                LastGradeText: lastGradeText,
                LastHomeworkText: lastHomeworkText,
                NotebookLineText: notebookLineText
            );

            var sent = await _whatsApp.SendAttendanceNotificationAsync(student.ParentPhoneNumber!, data);
            if (!sent)
                _logger.LogWarning("WhatsApp attendance notification not sent for student {StudentId}.", studentId);
        }
        catch (Exception ex)
        {
            // Belt-and-braces: IWhatsAppService implementations already swallow
            // their own errors, but never let this path take attendance down.
            _logger.LogWarning(ex, "Unexpected error while sending WhatsApp attendance notification for student {StudentId}.", studentId);
        }
    }
}
