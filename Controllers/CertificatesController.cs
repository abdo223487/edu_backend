using EduApi.Common;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/Certificates  ("شهاداتي" في بروفايل الطالب)
///
///  GET Certificates/mine   (student)
///
/// بيرجّع كل كورس (Unit) الطالب مشترك فيه عند المدرس الحالي (X-TenantId)، وحالة
/// شهادة التقدير بتاعته في كل كورس. مفيش جدول جديد ولا migration: كل حاجة
/// بتتحسب لحظيًا من الجداول الموجودة، وكلها أصلاً معزولة بالـ tenant عن طريق
/// الـ global query filters في AppDbContext.
///
/// الشهادة بتتفتح لما الشرطين دول يتحققوا مع بعض:
///
/// (1) الكورس "خلص" -- أي إن المدرس عمل كورس جديد (Unit أحدث) في نفس السنة
///     الدراسية. أول ما المدرس يعمل كورس جديد، كل الكورسات القديمة في السنة دي
///     بتتحسب منتهية وشهاداتها بتتوزع على المستحقين تلقائيًا (بما فيها الكورسات
///     القديمة اللي كانت موجودة قبل الفيتشر ده).
///
/// (2) الطالب خلّص كل المطلوب في الكورس ده (ولازم يكون فيه مطلوب واحد على الأقل):
///     - كل الكويزات بتاعة الكورس وجروب الطالب اتمتحنها فعلاً (مش auto-zero).
///     - كل امتحانات الحصص (LectureExam) على أي محاضرة/فيديو في الكورس.
///     - حضور كل محاضرات السنتر (AttendanceMethod = Center).
///     - تسليم كل الواجبات: واجبات المحاضرات (LectureAssignment) + الواجبات
///       العادية (Assignment) + واجبات السنتر (AssignmentCenter).
///     - فتح كل فيديو Online ليه عدد مشاهدات (ViewLimit) مرة واحدة على الأقل.
///       (الفيديوهات من غير ViewLimit مفيش لها أي تتبع أصلاً فمش بتدخل في الشرط.)
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.Student)]
public class CertificatesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public CertificatesController(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    /// <summary>عدّاد بسيط: كام مطلوب، كام منهم اتعمل، وتاريخ آخر واحد اتعمل.</summary>
    private sealed class Bucket
    {
        public int Total;
        public int Done;
        public DateTime? Last;

        public void Add(bool done, DateTime at)
        {
            Total++;
            if (!done) return;
            Done++;
            if (at != default && (Last == null || at > Last)) Last = at;
        }

        public CertificateProgressItem ToDto() => new(Done, Total);
    }

    private static Dictionary<int, DateTime> LatestBy<T>(
        IEnumerable<T> rows, Func<T, int> key, Func<T, DateTime> date)
        => rows.GroupBy(key).ToDictionary(g => g.Key, g => g.Max(date));

    [HttpGet("mine")]
    public async Task<IActionResult> GetMine()
    {
        var studentId = User.GetUserId();

        var student = await _db.Students.AsNoTracking()
            .Where(s => s.Id == studentId)
            .Select(s => new { s.Name, s.GroupId })
            .FirstOrDefaultAsync();
        if (student == null) return NotFound(new { message = "Student not found." });

        var teacherName = "";
        if (_tenant.CurrentTenantId.HasValue)
        {
            teacherName = await _db.Teachers.AsNoTracking()
                .Where(t => t.Id == _tenant.CurrentTenantId.Value)
                .Select(t => t.Name)
                .FirstOrDefaultAsync() ?? "";
        }

        // الكورسات اللي الطالب مشترك فيها عند المدرس الحالي (StudentUnitSubscriptions
        // و Units الاتنين tenant-filtered).
        var subscribedIds = await _db.StudentUnitSubscriptions.AsNoTracking()
            .Where(s => s.StudentId == studentId)
            .Select(s => s.UnitId)
            .Distinct()
            .ToListAsync();

        var units = await _db.Units.AsNoTracking()
            .Where(u => subscribedIds.Contains(u.Id))
            .OrderByDescending(u => u.Id)
            .Select(u => new { u.Id, u.Name, u.SchoolYear, u.Month, u.ImageUrl })
            .ToListAsync();

        if (units.Count == 0)
            return Ok(new MyCertificatesResponse(student.Name, teacherName, new List<CertificateCourseDto>()));

        var unitIds = units.Select(u => u.Id).ToList();

        // أحدث كورس (أكبر Id) لكل سنة دراسية عند المدرس ده -- لو فيه كورس أحدث
        // من الكورس الحالي في نفس السنة يبقى الكورس الحالي "خلص".
        var latestUnitIdByYear = (await _db.Units.AsNoTracking()
                .GroupBy(u => u.SchoolYear)
                .Select(g => new { Year = g.Key, MaxId = g.Max(u => u.Id) })
                .ToListAsync())
            .ToDictionary(x => x.Year, x => x.MaxId);

        // جروب الطالب عند المدرس ده: نفس منطق Students/attendance و Quizzes --
        // الـ membership الأول، وبعدين الـ GroupId القديم بس لو بيتبع نفس الـ tenant.
        var groupId = await _db.StudentGroupMemberships.AsNoTracking()
            .Where(m => m.StudentId == studentId)
            .Select(m => (int?)m.GroupId)
            .FirstOrDefaultAsync();
        if (groupId == null)
        {
            groupId = await _db.Groups.AsNoTracking()
                .Where(g => g.Id == student.GroupId && g.TeacherId == _tenant.CurrentTenantId)
                .Select(g => (int?)g.Id)
                .FirstOrDefaultAsync();
        }
        // -1 مش بيطابق أي جروب، فلو الطالب من غير جروب كل العناصر المربوطة
        // بالجروبات بترجع فاضية بدل ما نحتاج branching في كل query.
        var gid = groupId ?? -1;

        var quizB = unitIds.ToDictionary(id => id, _ => new Bucket());
        var examB = unitIds.ToDictionary(id => id, _ => new Bucket());
        var attB = unitIds.ToDictionary(id => id, _ => new Bucket());
        var asgB = unitIds.ToDictionary(id => id, _ => new Bucket());
        var vidB = unitIds.ToDictionary(id => id, _ => new Bucket());

        // ───────────── الكويزات ─────────────
        var quizzes = await _db.Quizzes.AsNoTracking()
            .Where(q => unitIds.Contains(q.UnitId) &&
                        _db.QuizGroupLinks.Any(x => x.QuizId == q.Id && x.GroupId == gid))
            .Select(q => new { q.Id, q.UnitId })
            .ToListAsync();
        var quizIds = quizzes.Select(q => q.Id).ToList();
        var quizDone = LatestBy(
            await _db.QuizResults.AsNoTracking()
                // IsAutoSubmitted = الطالب ماحلّش الكويز وخد صفر تلقائي -- مش بيتحسب.
                .Where(r => r.StudentId == studentId && !r.IsAutoSubmitted && quizIds.Contains(r.QuizId))
                .Select(r => new { r.QuizId, r.GradedAt })
                .ToListAsync(),
            r => r.QuizId, r => r.GradedAt);
        foreach (var q in quizzes)
        {
            var done = quizDone.TryGetValue(q.Id, out var at);
            quizB[q.UnitId].Add(done, at);
        }

        // ───────────── محاضرات الكورس (اللي الطالب شايفها: اشتراك + جروب) ─────────────
        var lectures = await _db.Lectures.AsNoTracking()
            .Where(l => l.UnitId != null && unitIds.Contains(l.UnitId.Value) &&
                        _db.LectureGroupLinks.Any(x => x.LectureId == l.Id && x.GroupId == gid))
            .Select(l => new
            {
                l.Id,
                UnitId = l.UnitId!.Value,
                IsCenter = l.AttendanceMethod == AttendanceMethod.Center,
                HasViewLimit = l.ViewLimit != null
            })
            .ToListAsync();
        var lectureIds = lectures.Select(l => l.Id).ToList();
        var unitOfLecture = lectures.ToDictionary(l => l.Id, l => l.UnitId);

        // حضور محاضرات السنتر
        var centerIds = lectures.Where(x => x.IsCenter).Select(x => x.Id).ToList();
        var attended = LatestBy(
            await _db.Attendances.AsNoTracking()
                .Where(a => a.StudentId == studentId && centerIds.Contains(a.LectureId))
                .Select(a => new { a.LectureId, a.Date })
                .ToListAsync(),
            a => a.LectureId, a => a.Date);
        foreach (var l in lectures.Where(x => x.IsCenter))
        {
            var done = attended.TryGetValue(l.Id, out var at);
            attB[l.UnitId].Add(done, at);
        }

        // فيديوهات Online ليها عدد مشاهدات: لازم تتفتح مرة واحدة على الأقل.
        // (StudentLectureViewUsage بيتكتب مع كل ConsumeView، وكمان LectureViewSession.)
        var limitedIds = lectures.Where(x => !x.IsCenter && x.HasViewLimit).Select(x => x.Id).ToList();
        var watched = new Dictionary<int, DateTime>();
        var usageRows = await _db.StudentLectureViewUsages.AsNoTracking()
            .Where(x => x.StudentId == studentId && limitedIds.Contains(x.LectureId) && x.ViewsUsed > 0)
            .Select(x => new { x.LectureId, x.LastViewedAt })
            .ToListAsync();
        foreach (var usage in usageRows)
        {
            var usageAt = usage.LastViewedAt ?? default;
            watched[usage.LectureId] =
                watched.TryGetValue(usage.LectureId, out var prevUsage) && prevUsage > usageAt ? prevUsage : usageAt;
        }
        var sessionRows = await _db.LectureViewSessions.AsNoTracking()
            .Where(x => x.StudentId == studentId && limitedIds.Contains(x.LectureId))
            .Select(x => new { x.LectureId, x.CreatedAt })
            .ToListAsync();
        foreach (var session in sessionRows)
        {
            watched[session.LectureId] =
                watched.TryGetValue(session.LectureId, out var prevSession) && prevSession > session.CreatedAt
                    ? prevSession : session.CreatedAt;
        }
        foreach (var l in lectures.Where(x => !x.IsCenter && x.HasViewLimit))
        {
            var done = watched.TryGetValue(l.Id, out var at);
            vidB[l.UnitId].Add(done, at);
        }

        // ───────────── امتحانات الحصص (على أي محاضرة/فيديو في الكورس) ─────────────
        var exams = await _db.LectureExams.AsNoTracking()
            .Where(e => lectureIds.Contains(e.LectureId))
            .Select(e => new { e.Id, e.LectureId })
            .ToListAsync();
        var examIds = exams.Select(e => e.Id).ToList();
        var examDone = LatestBy(
            await _db.LectureExamResults.AsNoTracking()
                .Where(r => r.StudentId == studentId && examIds.Contains(r.LectureExamId))
                .Select(r => new { r.LectureExamId, r.GradedAt })
                .ToListAsync(),
            r => r.LectureExamId, r => r.GradedAt);
        foreach (var e in exams)
        {
            var done = examDone.TryGetValue(e.Id, out var at);
            examB[unitOfLecture[e.LectureId]].Add(done, at);
        }

        // ───────────── الواجبات (1): واجبات المحاضرات ─────────────
        var lecAsgs = await _db.LectureAssignments.AsNoTracking()
            .Where(a => lectureIds.Contains(a.LectureId))
            .Select(a => new { a.Id, a.LectureId })
            .ToListAsync();
        var lecAsgIds = lecAsgs.Select(a => a.Id).ToList();
        var lecAsgDone = LatestBy(
            await _db.LectureAssignmentResults.AsNoTracking()
                .Where(r => r.StudentId == studentId && lecAsgIds.Contains(r.LectureAssignmentId))
                .Select(r => new { r.LectureAssignmentId, r.GradedAt })
                .ToListAsync(),
            r => r.LectureAssignmentId, r => r.GradedAt);
        foreach (var a in lecAsgs)
        {
            var done = lecAsgDone.TryGetValue(a.Id, out var at);
            asgB[unitOfLecture[a.LectureId]].Add(done, at);
        }

        // ───────────── الواجبات (2): الواجبات العادية ─────────────
        // الواجب ممكن يغطي أكتر من كورس (AssignmentUnitLink) فبيتحسب مطلوب في كل
        // كورس منهم. _db.Assignments.Any(...) هنا عشان فلتر الـ tenant (الـ links
        // نفسها مش tenant-filtered).
        var asgPairs = await _db.AssignmentUnitLinks.AsNoTracking()
            .Where(x => unitIds.Contains(x.UnitId) &&
                        _db.Assignments.Any(a => a.Id == x.AssignmentId) &&
                        _db.AssignmentGroupLinks.Any(g => g.AssignmentId == x.AssignmentId && g.GroupId == gid))
            .Select(x => new { x.AssignmentId, x.UnitId })
            .ToListAsync();
        var asgIds = asgPairs.Select(p => p.AssignmentId).Distinct().ToList();
        var asgDone = LatestBy(
            await _db.AssignmentSubmissions.AsNoTracking()
                .Where(s => s.StudentId == studentId && asgIds.Contains(s.AssignmentId))
                .Select(s => new { s.AssignmentId, s.SubmittedAt })
                .ToListAsync(),
            s => s.AssignmentId, s => s.SubmittedAt);
        foreach (var p in asgPairs)
        {
            var done = asgDone.TryGetValue(p.AssignmentId, out var at);
            asgB[p.UnitId].Add(done, at);
        }

        // ───────────── الواجبات (3): واجبات السنتر (البابل شيت) ─────────────
        var centerPairs = await _db.AssignmentCenterUnitLinks.AsNoTracking()
            .Where(x => unitIds.Contains(x.UnitId) &&
                        _db.AssignmentCenters.Any(a => a.Id == x.AssignmentCenterId) &&
                        _db.AssignmentCenterGroupLinks.Any(g => g.AssignmentCenterId == x.AssignmentCenterId && g.GroupId == gid))
            .Select(x => new { x.AssignmentCenterId, x.UnitId })
            .ToListAsync();
        var centerAsgIds = centerPairs.Select(p => p.AssignmentCenterId).Distinct().ToList();
        var centerAsgDone = LatestBy(
            await _db.AssignmentCenterSubmissions.AsNoTracking()
                .Where(s => s.StudentId == studentId && centerAsgIds.Contains(s.AssignmentCenterId))
                .Select(s => new { s.AssignmentCenterId, s.SubmittedAt })
                .ToListAsync(),
            s => s.AssignmentCenterId, s => s.SubmittedAt);
        foreach (var p in centerPairs)
        {
            var done = centerAsgDone.TryGetValue(p.AssignmentCenterId, out var at);
            asgB[p.UnitId].Add(done, at);
        }

        // ───────────── تجميع النتيجة لكل كورس ─────────────
        var courses = new List<CertificateCourseDto>();
        foreach (var u in units)
        {
            var buckets = new[] { quizB[u.Id], examB[u.Id], attB[u.Id], asgB[u.Id], vidB[u.Id] };
            var total = buckets.Sum(b => b.Total);
            var done = buckets.Sum(b => b.Done);

            // لازم يكون فيه مطلوب واحد على الأقل، عشان كورس فاضي مايدّيش شهادة
            // لكل المشتركين فيه من غير ما حد يعمل أي حاجة.
            var isEligible = total > 0 && done == total;
            var isClosed = latestUnitIdByYear.TryGetValue(u.SchoolYear, out var maxId) && maxId > u.Id;
            var percent = total == 0 ? 0 : (int)Math.Floor(done * 100.0 / total);

            DateTime? completedAt = null;
            if (isEligible)
            {
                var last = buckets.Where(b => b.Last.HasValue).Select(b => b.Last!.Value).ToList();
                if (last.Count > 0) completedAt = last.Max();
            }

            courses.Add(new CertificateCourseDto(
                u.Id, u.Name, u.SchoolYear, u.Month, u.ImageUrl,
                isClosed, isEligible, isClosed && isEligible,
                percent, completedAt,
                quizB[u.Id].ToDto(), examB[u.Id].ToDto(), attB[u.Id].ToDto(),
                asgB[u.Id].ToDto(), vidB[u.Id].ToDto()));
        }

        return Ok(new MyCertificatesResponse(student.Name, teacherName, courses));
    }
}
