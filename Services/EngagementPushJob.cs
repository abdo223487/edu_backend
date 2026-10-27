using EduApi.Common;
using EduApi.Data;
using EduApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Services;

/// <summary>
/// Background job (runs every 2 minutes) that sends the time-based pushes:
///   - new assignment / lecture exam / lecture assignment
///   - quiz opened (StartAt reached)
///   - deadline reminders (24h and 2h before) -- only to students who haven't submitted
///   - "results and solution are available" right after a quiz/assignment deadline
///   - daily spaced-repetition reminder (17:00 Cairo) for students with >= 5 due cards
///
/// It runs outside any HTTP request, so there is NO tenant: every query uses
/// IgnoreQueryFilters() and scopes itself by explicit ids. Idempotency is
/// guaranteed by PushDispatchLog (primary key = campaign key, claimed BEFORE
/// sending), so restarts / multiple instances never double-send.
/// </summary>
public class EngagementPushJob : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EngagementPushJob> _log;

    public EngagementPushJob(IServiceScopeFactory scopes, ILogger<EngagementPushJob> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting (migrations etc.) before the first tick.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var push = scope.ServiceProvider.GetRequiredService<IPushService>();
                if (push.Enabled)
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    await RunOnceAsync(db, push, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "EngagementPushJob tick failed.");
            }

            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunOnceAsync(AppDbContext db, IPushService push, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        await NewAssignmentsAsync(db, push, now, ct);
        await NewLectureExamsAndAssignmentsAsync(db, push, now, ct);
        await QuizzesAsync(db, push, now, ct);
        await AssignmentDeadlinesAsync(db, push, now, ct);
        await DailyReviewReminderAsync(db, push, now, ct);
    }

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>Atomically "claims" a campaign key. false = somebody already sent it.</summary>
    private static async Task<bool> ClaimAsync(AppDbContext db, string key, CancellationToken ct)
    {
        try
        {
            var entry = db.PushDispatchLogs.Add(new Models.PushDispatchLog { Key = key, SentAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            foreach (var e in db.ChangeTracker.Entries<Models.PushDispatchLog>().ToList())
                e.State = EntityState.Detached;
            return false;
        }
    }

    private static Dictionary<string, string> Data(string type, int id, int teacherId) => new()
    {
        ["type"] = type,
        ["id"] = id.ToString(),
        ["teacherId"] = teacherId.ToString(),
    };

    // ───────────────────────── new assignments ─────────────────────────

    private async Task NewAssignmentsAsync(AppDbContext db, IPushService push, DateTime now, CancellationToken ct)
    {
        var since = now.AddHours(-12);
        var items = await db.Assignments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.CreatedAt > since && a.Deadline > now)
            .Select(a => new { a.Id, a.Title, a.TeacherId, a.UnitIdsCsv })
            .ToListAsync(ct);

        foreach (var a in items)
        {
            if (!await ClaimAsync(db, $"asg-new:{a.Id}", ct)) continue;

            var groupIds = await db.AssignmentGroupLinks.AsNoTracking()
                .Where(x => x.AssignmentId == a.Id).Select(x => x.GroupId).ToListAsync(ct);
            var unitIds = ParseIds(a.UnitIdsCsv);
            var students = await PushTargets.StudentsInGroupsAsync(db, groupIds, unitIds, ct);

            await push.SendToStudentsAsync(students, "واجب جديد 📝", a.Title, Data("assignment", a.Id, a.TeacherId), ct);
        }
    }

    // ───────────────────────── lecture exams / assignments ─────────────────────────

    private async Task NewLectureExamsAndAssignmentsAsync(AppDbContext db, IPushService push, DateTime now, CancellationToken ct)
    {
        var since = now.AddHours(-12);

        var exams = await db.LectureExams.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.CreatedAt > since)
            .Select(e => new { e.Id, e.Title, e.LectureId, e.TeacherId })
            .ToListAsync(ct);
        foreach (var e in exams)
        {
            if (!await ClaimAsync(db, $"lexam-new:{e.Id}", ct)) continue;
            var students = await StudentsOfLectureAsync(db, e.LectureId, ct);
            await push.SendToStudentsAsync(students, "امتحان جديد على محاضرة 🧠", e.Title,
                Data("lecture_exam", e.Id, e.TeacherId), ct);
        }

        var homeworks = await db.LectureAssignments.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.CreatedAt > since)
            .Select(e => new { e.Id, e.Title, e.LectureId, e.TeacherId })
            .ToListAsync(ct);
        foreach (var h in homeworks)
        {
            if (!await ClaimAsync(db, $"lasg-new:{h.Id}", ct)) continue;
            var students = await StudentsOfLectureAsync(db, h.LectureId, ct);
            await push.SendToStudentsAsync(students, "واجب جديد على محاضرة 📚", h.Title,
                Data("lecture_assignment", h.Id, h.TeacherId), ct);
        }
    }

    private static async Task<List<int>> StudentsOfLectureAsync(AppDbContext db, int lectureId, CancellationToken ct)
    {
        var groupIds = await db.LectureGroupLinks.AsNoTracking()
            .Where(x => x.LectureId == lectureId).Select(x => x.GroupId).ToListAsync(ct);
        var unitId = await db.Lectures.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Id == lectureId).Select(l => l.UnitId).FirstOrDefaultAsync(ct);
        return await PushTargets.StudentsInGroupsAsync(db, groupIds,
            unitId.HasValue ? new[] { unitId.Value } : null, ct);
    }

    // ───────────────────────── quizzes ─────────────────────────

    private async Task QuizzesAsync(AppDbContext db, IPushService push, DateTime now, CancellationToken ct)
    {
        // Only quizzes that are relevant right now (started in the last 3 days or still running).
        var quizzes = await db.Quizzes.IgnoreQueryFilters().AsNoTracking()
            .Where(q => q.StartAt > now.AddDays(-3) && q.Deadline > now.AddHours(-2))
            .Select(q => new { q.Id, q.Title, q.TeacherId, q.UnitId, q.StartAt, q.Deadline })
            .ToListAsync(ct);

        foreach (var q in quizzes)
        {
            List<int>? groupStudents = null;
            async Task<List<int>> Group()
            {
                if (groupStudents != null) return groupStudents;
                var groupIds = await db.QuizGroupLinks.AsNoTracking()
                    .Where(x => x.QuizId == q.Id).Select(x => x.GroupId).ToListAsync(ct);
                groupStudents = await PushTargets.StudentsInGroupsAsync(db, groupIds, new[] { q.UnitId }, ct);
                return groupStudents;
            }

            async Task<List<int>> Submitted() => await db.QuizResults.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.QuizId == q.Id && !r.IsAutoSubmitted).Select(r => r.StudentId).ToListAsync(ct);

            // 1) quiz opened
            if (q.StartAt <= now && q.StartAt > now.AddMinutes(-30) && q.Deadline > now
                && await ClaimAsync(db, $"quiz-start:{q.Id}", ct))
            {
                await push.SendToStudentsAsync(await Group(), "الكويز اتفتح دلوقتي ⏱️", q.Title,
                    Data("quiz", q.Id, q.TeacherId), ct);
            }

            // 2) deadline reminders (only students who haven't submitted)
            if (q.Deadline > now)
            {
                var left = q.Deadline - now;

                if (left <= TimeSpan.FromHours(2) && await ClaimAsync(db, $"quiz-remind2:{q.Id}", ct))
                {
                    var todo = (await Group()).Except(await Submitted()).ToList();
                    await push.SendToStudentsAsync(todo, "باقي أقل من ساعتين ⏳", $"آخر ميعاد لكويز: {q.Title}",
                        Data("quiz", q.Id, q.TeacherId), ct);
                }
                else if (left <= TimeSpan.FromHours(24) && q.Deadline - q.StartAt > TimeSpan.FromHours(30)
                         && await ClaimAsync(db, $"quiz-remind24:{q.Id}", ct))
                {
                    var todo = (await Group()).Except(await Submitted()).ToList();
                    await push.SendToStudentsAsync(todo, "فاضل يوم على الكويز ⏳", q.Title,
                        Data("quiz", q.Id, q.TeacherId), ct);
                }
            }
            // 3) deadline just passed => result + solution are visible now
            else if (q.Deadline <= now && q.Deadline > now.AddHours(-1)
                     && await ClaimAsync(db, $"quiz-res:{q.Id}", ct))
            {
                await push.SendToStudentsAsync(await Submitted(), "نتيجة الكويز نزلت ✅", $"{q.Title} — شوف درجتك والحل",
                    Data("quiz_result", q.Id, q.TeacherId), ct);
            }
        }
    }

    // ───────────────────────── assignment deadlines / results ─────────────────────────

    private async Task AssignmentDeadlinesAsync(AppDbContext db, IPushService push, DateTime now, CancellationToken ct)
    {
        var items = await db.Assignments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Deadline > now.AddHours(-2) && a.Deadline < now.AddHours(25))
            .Select(a => new { a.Id, a.Title, a.TeacherId, a.UnitIdsCsv, a.CreatedAt, a.Deadline })
            .ToListAsync(ct);

        foreach (var a in items)
        {
            List<int>? groupStudents = null;
            async Task<List<int>> Group()
            {
                if (groupStudents != null) return groupStudents;
                var groupIds = await db.AssignmentGroupLinks.AsNoTracking()
                    .Where(x => x.AssignmentId == a.Id).Select(x => x.GroupId).ToListAsync(ct);
                groupStudents = await PushTargets.StudentsInGroupsAsync(db, groupIds, ParseIds(a.UnitIdsCsv), ct);
                return groupStudents;
            }
            async Task<List<int>> Submitted() => await db.AssignmentSubmissions.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.AssignmentId == a.Id).Select(s => s.StudentId).ToListAsync(ct);

            if (a.Deadline > now)
            {
                var left = a.Deadline - now;
                if (left <= TimeSpan.FromHours(2) && await ClaimAsync(db, $"asg-remind2:{a.Id}", ct))
                {
                    var todo = (await Group()).Except(await Submitted()).ToList();
                    await push.SendToStudentsAsync(todo, "باقي أقل من ساعتين ⏳", $"آخر ميعاد لواجب: {a.Title}",
                        Data("assignment", a.Id, a.TeacherId), ct);
                }
                else if (left <= TimeSpan.FromHours(24) && a.Deadline - a.CreatedAt > TimeSpan.FromHours(30)
                         && await ClaimAsync(db, $"asg-remind24:{a.Id}", ct))
                {
                    var todo = (await Group()).Except(await Submitted()).ToList();
                    await push.SendToStudentsAsync(todo, "فاضل يوم على تسليم الواجب ⏳", a.Title,
                        Data("assignment", a.Id, a.TeacherId), ct);
                }
            }
            else if (a.Deadline > now.AddHours(-1) && await ClaimAsync(db, $"asg-res:{a.Id}", ct))
            {
                await push.SendToStudentsAsync(await Submitted(), "نتيجة الواجب نزلت ✅", $"{a.Title} — شوف درجتك والحل",
                    Data("assignment_result", a.Id, a.TeacherId), ct);
            }
        }
    }

    // ───────────────────────── daily review reminder ─────────────────────────

    private async Task DailyReviewReminderAsync(AppDbContext db, IPushService push, DateTime now, CancellationToken ct)
    {
        var cairo = PushTargets.ToCairo(now);
        if (cairo.Hour != 17) return;
        var day = cairo.ToString("yyyyMMdd");

        var due = await db.ReviewCards.IgnoreQueryFilters().AsNoTracking()
            .Where(c => !c.IsArchived && c.DueAt <= now)
            .GroupBy(c => c.StudentId)
            .Select(g => new { StudentId = g.Key, Count = g.Count() })
            .Where(x => x.Count >= 5)
            .Take(500)
            .ToListAsync(ct);

        foreach (var d in due)
        {
            if (!await ClaimAsync(db, $"review-due:{d.StudentId}:{day}", ct)) continue;
            await push.SendToStudentsAsync(new[] { d.StudentId }, "وقت مراجعة أخطائك 🔁",
                $"عندك {d.Count} سؤال مستنيك — 5 دقايق بس وتثبّتهم",
                new Dictionary<string, string> { ["type"] = "review" }, ct);
        }
    }

    private static List<int> ParseIds(string csv)
        => string.IsNullOrWhiteSpace(csv)
            ? new List<int>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x, out var v) ? v : 0).Where(x => x > 0).ToList();
}
