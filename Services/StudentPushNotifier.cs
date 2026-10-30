using EduApi.Common;
using EduApi.Data;
using EduApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Services;

/// <summary>
/// Event-driven pushes, fired right after the thing happens (inside the request that did it):
///   - new video (lecture) uploaded     -> students who can open it
///   - new quiz / assignment / assignment-center / lecture exam / lecture assignment
///                                      -> the students it was created for (and who are subscribed)
///   - attendance recorded      ("تم تسجيل حضورك" + the time it was recorded)
///   - code redeemed            (what it unlocked)
///   - notebook / monthly-invoice payment recorded
/// There is NO background job: nothing here polls the database.
///
/// Everything here is best effort and NEVER throws: a failed push (or a failed name lookup) must
/// not fail the attendance / redeem / payment that triggered it. Name lookups use
/// IgnoreQueryFilters() with explicit ids, so they don't depend on which tenant the caller is in.
/// Time-based / bulk pushes (new videos, exams, homework, certificates...) live in EngagementPushJob.
/// </summary>
public class StudentPushNotifier
{
    private readonly AppDbContext _db;
    private readonly IPushService _push;
    private readonly ILogger<StudentPushNotifier> _log;

    public StudentPushNotifier(AppDbContext db, IPushService push, ILogger<StudentPushNotifier> log)
    {
        _db = db;
        _push = push;
        _log = log;
    }

    private static Dictionary<string, string> Data(string type, int id, int teacherId) => new()
    {
        ["type"] = type,
        ["id"] = id.ToString(),
        ["teacherId"] = teacherId.ToString(),
    };

    // ───────────────────────── new content (video / exam / homework) ─────────────────────────

    private static List<int> ParseIds(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? new List<int>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x, out var v) ? v : 0).Where(x => x > 0).ToList();

    /// <summary>"كورس X" / "كتاب X" / "ليسون أونلاين X" for a lecture's container (null when it has none).</summary>
    private async Task<string?> ContainerNameAsync(int? unitId, int? externalBookId, int? onlineLessonId)
    {
        if (unitId.HasValue)
        {
            var id = unitId.Value;
            var n = await _db.Units.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.Id == id).Select(u => u.Name).FirstOrDefaultAsync();
            return n == null ? null : $"كورس {n}";
        }
        if (externalBookId.HasValue)
        {
            var id = externalBookId.Value;
            var n = await _db.ExternalBooks.IgnoreQueryFilters().AsNoTracking()
                .Where(b => b.Id == id).Select(b => b.Name).FirstOrDefaultAsync();
            return n == null ? null : $"كتاب {n}";
        }
        if (onlineLessonId.HasValue)
        {
            var id = onlineLessonId.Value;
            var n = await _db.OnlineLessons.IgnoreQueryFilters().AsNoTracking()
                .Where(o => o.Id == id).Select(o => o.Name).FirstOrDefaultAsync();
            return n == null ? null : $"ليسون أونلاين {n}";
        }
        return null;
    }

    private async Task<string?> LectureNameAsync(int lectureId)
        => await _db.Lectures.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Id == lectureId).Select(l => l.Name).FirstOrDefaultAsync();

    /// <summary>
    /// "تم رفع حصة ..." -- call right after a lecture is created (course / external book / online lesson, or
    /// a lesson inside them). Only students who can actually open it are notified
    /// (PushTargets.StudentsOfLectureAsync). A lecture with no video (Center / attendance-only) is skipped.
    /// </summary>
    public async Task LectureUploadedAsync(int lectureId)
    {
        try
        {
            var l = await _db.Lectures.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == lectureId)
                .Select(x => new { x.Id, x.Name, x.TeacherId, x.UnitId, x.ExternalBookId, x.OnlineLessonId, x.StorageFileKey, x.YoutubeLink })
                .FirstOrDefaultAsync();
            if (l == null) return;
            if (string.IsNullOrWhiteSpace(l.StorageFileKey) && string.IsNullOrWhiteSpace(l.YoutubeLink)) return;

            var students = await PushTargets.StudentsOfLectureAsync(_db, l.Id);
            if (students.Count == 0) return;

            var where = await ContainerNameAsync(l.UnitId, l.ExternalBookId, l.OnlineLessonId);
            var body = where == null ? $"تم رفع حصة {l.Name}" : $"تم رفع حصة {l.Name} — {where}";
            await _push.SendToStudentsAsync(students, "حصة جديدة 🎬", body, Data("lecture", l.Id, l.TeacherId));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "New-lecture push failed (lecture {LectureId}).", lectureId);
        }
    }

    /// <summary>New course quiz: students in its groups who are subscribed to its course.</summary>
    public async Task QuizCreatedAsync(int quizId)
    {
        try
        {
            var q = await _db.Quizzes.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == quizId)
                .Select(x => new { x.Id, x.Title, x.TeacherId, x.UnitId, x.StartAt })
                .FirstOrDefaultAsync();
            if (q == null) return;

            var groupIds = await _db.QuizGroupLinks.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.QuizId == quizId).Select(x => x.GroupId).ToListAsync();
            var students = await PushTargets.StudentsInGroupsAsync(_db, groupIds, new[] { q.UnitId });

            var body = q.StartAt > DateTime.UtcNow
                ? $"{q.Title} — يبدأ {PushTargets.CairoDateTimeAr(q.StartAt)}"
                : q.Title;
            await _push.SendToStudentsAsync(students, "امتحان جديد 🧠", body, Data("quiz", q.Id, q.TeacherId));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "New-quiz push failed (quiz {QuizId}).", quizId);
        }
    }

    /// <summary>New course assignment: students in its groups who are subscribed to one of its courses.</summary>
    public async Task AssignmentCreatedAsync(int assignmentId)
    {
        try
        {
            var a = await _db.Assignments.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == assignmentId)
                .Select(x => new { x.Id, x.Title, x.TeacherId, x.UnitIdsCsv })
                .FirstOrDefaultAsync();
            if (a == null) return;

            var groupIds = await _db.AssignmentGroupLinks.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.AssignmentId == assignmentId).Select(x => x.GroupId).ToListAsync();
            var students = await PushTargets.StudentsInGroupsAsync(_db, groupIds, ParseIds(a.UnitIdsCsv));

            await _push.SendToStudentsAsync(students, "واجب جديد 📝", a.Title, Data("assignment", a.Id, a.TeacherId));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "New-assignment push failed (assignment {AssignmentId}).", assignmentId);
        }
    }

    /// <summary>New assignment-center (bubble-sheet) homework.</summary>
    public async Task AssignmentCenterCreatedAsync(int assignmentCenterId)
    {
        try
        {
            var a = await _db.AssignmentCenters.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == assignmentCenterId)
                .Select(x => new { x.Id, x.Title, x.TeacherId, x.UnitIdsCsv })
                .FirstOrDefaultAsync();
            if (a == null) return;

            var groupIds = await _db.AssignmentCenterGroupLinks.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.AssignmentCenterId == assignmentCenterId).Select(x => x.GroupId).ToListAsync();
            var students = await PushTargets.StudentsInGroupsAsync(_db, groupIds, ParseIds(a.UnitIdsCsv));

            await _push.SendToStudentsAsync(students, "واجب سنتر جديد 📝", a.Title, Data("assignment_center", a.Id, a.TeacherId));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "New-assignment-center push failed ({Id}).", assignmentCenterId);
        }
    }

    /// <summary>New exam on a lecture (works for lectures inside a course, an external book or an online lesson).</summary>
    public async Task LectureExamCreatedAsync(int lectureExamId)
    {
        try
        {
            var e = await _db.LectureExams.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == lectureExamId)
                .Select(x => new { x.Id, x.Title, x.LectureId, x.TeacherId })
                .FirstOrDefaultAsync();
            if (e == null) return;

            var students = await PushTargets.StudentsOfLectureAsync(_db, e.LectureId);
            var lectureName = await LectureNameAsync(e.LectureId);

            await _push.SendToStudentsAsync(students, "امتحان جديد على حصة 🧠",
                lectureName == null ? e.Title : $"{e.Title} — {lectureName}",
                Data("lecture_exam", e.Id, e.TeacherId));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "New-lecture-exam push failed ({Id}).", lectureExamId);
        }
    }

    /// <summary>New homework on a lecture (works for lectures inside a course, an external book or an online lesson).</summary>
    public async Task LectureAssignmentCreatedAsync(int lectureAssignmentId)
    {
        try
        {
            var h = await _db.LectureAssignments.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == lectureAssignmentId)
                .Select(x => new { x.Id, x.Title, x.LectureId, x.TeacherId })
                .FirstOrDefaultAsync();
            if (h == null) return;

            var students = await PushTargets.StudentsOfLectureAsync(_db, h.LectureId);
            var lectureName = await LectureNameAsync(h.LectureId);

            await _push.SendToStudentsAsync(students, "واجب جديد على حصة 📚",
                lectureName == null ? h.Title : $"{h.Title} — {lectureName}",
                Data("lecture_assignment", h.Id, h.TeacherId));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "New-lecture-assignment push failed ({Id}).", lectureAssignmentId);
        }
    }

    // ───────────────────────── attendance ─────────────────────────

    /// <param name="attendanceUtc">Attendance.Date (UTC; an offline-synced record keeps its ORIGINAL time).</param>
    public async Task AttendanceAsync(int studentId, int teacherId, int lectureId, DateTime attendanceUtc)
    {
        try
        {
            var lectureName = await _db.Lectures.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.Id == lectureId)
                .Select(l => l.Name)
                .FirstOrDefaultAsync();

            // "اليوم الساعة 5:30 م" when it's today, otherwise "29/10 الساعة 5:30 م".
            var sameDay = PushTargets.ToCairo(attendanceUtc).Date == PushTargets.ToCairo(DateTime.UtcNow).Date;
            var whenText = sameDay
                ? $"اليوم الساعة {PushTargets.CairoTimeAr(attendanceUtc)}"
                : PushTargets.CairoDateTimeAr(attendanceUtc);

            var body = string.IsNullOrWhiteSpace(lectureName)
                ? $"تم تسجيل حضورك {whenText}"
                : $"تم تسجيل حضورك في «{lectureName}» {whenText}";

            await _push.SendToStudentsAsync(new[] { studentId }, "تم تسجيل حضورك ✅", body,
                Data("attendance", lectureId, teacherId));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Attendance push failed (student {StudentId}, lecture {LectureId}).", studentId, lectureId);
        }
    }

    // ───────────────────────── code redeemed ─────────────────────────

    public async Task CodeRedeemedAsync(
        int studentId, int teacherId,
        IReadOnlyCollection<int> unitIds, IReadOnlyCollection<int> lectureIds,
        IReadOnlyCollection<int> onlineLessonIds, IReadOnlyCollection<int> externalBookIds)
    {
        try
        {
            var parts = new List<string>();

            if (unitIds.Count > 0)
            {
                var names = await _db.Units.IgnoreQueryFilters().AsNoTracking()
                    .Where(u => unitIds.Contains(u.Id)).Select(u => u.Name).ToListAsync();
                parts.AddRange(names.Select(n => $"كورس {n}"));
            }
            if (externalBookIds.Count > 0)
            {
                var names = await _db.ExternalBooks.IgnoreQueryFilters().AsNoTracking()
                    .Where(b => externalBookIds.Contains(b.Id)).Select(b => b.Name).ToListAsync();
                parts.AddRange(names.Select(n => $"كتاب {n}"));
            }
            if (onlineLessonIds.Count > 0)
            {
                var names = await _db.OnlineLessons.IgnoreQueryFilters().AsNoTracking()
                    .Where(o => onlineLessonIds.Contains(o.Id)).Select(o => o.Name).ToListAsync();
                parts.AddRange(names.Select(n => $"ليسون أونلاين {n}"));
            }
            if (lectureIds.Count > 0)
            {
                var names = await _db.Lectures.IgnoreQueryFilters().AsNoTracking()
                    .Where(l => lectureIds.Contains(l.Id)).Select(l => l.Name).ToListAsync();
                parts.AddRange(names.Select(n => $"حصة {n}"));
            }

            string body;
            if (parts.Count == 0)
            {
                body = "تم تفعيل الكود بنجاح";
            }
            else
            {
                var shown = parts.Take(3).ToList();
                var more = parts.Count - shown.Count;
                body = "اتفتحلك: " + string.Join("، ", shown) + (more > 0 ? $" و{more} أخرى" : string.Empty);
            }

            await _push.SendToStudentsAsync(new[] { studentId }, "تم تفعيل الكود 🔓", body,
                Data("code", 0, teacherId));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Code-redeemed push failed (student {StudentId}).", studentId);
        }
    }

    // ───────────────────────── payments ─────────────────────────

    /// <param name="remainingAfter">What's still owed on this notebook after this payment.</param>
    public Task NotebookPaymentAsync(int studentId, int teacherId, int notebookId, string notebookName, int amount, int remainingAfter)
        => PaymentAsync(studentId, teacherId, notebookId, $"مذكرة «{notebookName}»", amount, remainingAfter);

    /// <param name="remainingAfter">What's still owed on this monthly invoice after this payment.</param>
    public Task BillingPaymentAsync(int studentId, int teacherId, int billingId, string billingName, int amount, int remainingAfter)
        => PaymentAsync(studentId, teacherId, billingId, $"فاتورة «{billingName}»", amount, remainingAfter);

    private async Task PaymentAsync(int studentId, int teacherId, int itemId, string itemText, int amount, int remainingAfter)
    {
        try
        {
            var body = remainingAfter > 0
                ? $"تم تسجيل دفع {amount} جنيه لـ{itemText} — المتبقي {remainingAfter} جنيه"
                : $"تم تسجيل دفع {amount} جنيه لـ{itemText} — تم السداد بالكامل ✅";

            await _push.SendToStudentsAsync(new[] { studentId }, "تم تسجيل دفعك 💰", body,
                Data("payment", itemId, teacherId));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Payment push failed (student {StudentId}).", studentId);
        }
    }
}
