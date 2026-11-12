using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// "خطة المذاكرة اليومية": builds today's to-do list for the student from data that already exists,
/// no AI and no new table:
///   - review cards that are due (spaced repetition on the student's mistakes)
///   - quizzes / homework that open or close in the next 3 days and are not done yet
///   - lectures the student started and left unfinished
///   - the newest lectures they have not opened (only plain ones: no view limit, no exam/homework gate)
///   - practice on the course where they have the most open mistakes
/// Tasks are ordered by urgency and trimmed to the student's time budget.
///
/// Route: api/StudyPlan  (student only)
///  GET StudyPlan/today?minutes=45     (minutes: 15..180, default 45)
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.Student)]
public class StudyPlanController : ControllerBase
{
    private const double MinutesPerReviewCard = 0.6;
    private const int MaxReviewCardsPerPlan = 20;
    private static readonly TimeSpan Horizon = TimeSpan.FromHours(72);

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public StudyPlanController(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    private sealed record PlanTask(string Id, string Type, string Title, string Subtitle, int Minutes, int Priority, object Action);

    private static string InHours(DateTime utc, DateTime now)
    {
        var h = (utc - now).TotalHours;
        if (h < 1) return "في خلال أقل من ساعة";
        if (h < 24) return $"بعد {(int)Math.Round(h)} ساعة";
        return $"بعد {(int)Math.Round(h / 24)} يوم";
    }

    [HttpGet("today")]
    public async Task<IActionResult> Today([FromQuery] int minutes = 45)
    {
        minutes = Math.Clamp(minutes, 15, 180);
        var studentId = User.GetUserId();
        var now = DateTime.UtcNow;
        var cairoNow = PushTargets.ToCairo(now);
        var todayStartUtc = now - (cairoNow - cairoNow.Date);

        var unitIds = await StudentAccessHelpers.GetEffectiveUnitIdsAsync(_db, User, studentId);
        var gId = User.GetGroupId(_tenant.CurrentTenantId);

        var tasks = new List<PlanTask>();

        // ───── 1) quizzes ─────
        var quizQuery = _db.Quizzes.AsNoTracking().Where(q => unitIds.Contains(q.UnitId) && q.Deadline > now && q.StartAt <= now + Horizon);
        if (gId.HasValue) quizQuery = quizQuery.Where(q => _db.QuizGroupLinks.Any(x => x.QuizId == q.Id && x.GroupId == gId.Value));
        var quizzes = await quizQuery.OrderBy(q => q.StartAt).Take(10)
            .Select(q => new { q.Id, q.Title, q.UnitId, q.StartAt, q.Deadline }).ToListAsync();
        if (quizzes.Count > 0)
        {
            var qIds = quizzes.Select(q => q.Id).ToList();
            var taken = (await _db.QuizResults.AsNoTracking()
                    .Where(r => r.StudentId == studentId && !r.IsAutoSubmitted && qIds.Contains(r.QuizId))
                    .Select(r => r.QuizId).ToListAsync()).ToHashSet();
            foreach (var q in quizzes.Where(q => !taken.Contains(q.Id)))
            {
                var open = q.StartAt <= now;
                tasks.Add(new PlanTask(
                    $"quiz:{q.Id}", open ? "quiz_open" : "quiz_soon",
                    open ? $"حل كويز: {q.Title}" : $"استعد لكويز: {q.Title}",
                    open ? $"بيقفل {InHours(q.Deadline, now)}" : $"بيفتح {InHours(q.StartAt, now)}",
                    open ? 25 : 20, open ? 1 : 2,
                    new { kind = open ? "open_quiz" : "review_for_quiz", quizId = q.Id, unitId = q.UnitId }));
            }
        }

        // ───── 2) homework ─────
        var asgQuery = _db.Assignments.AsNoTracking()
            .Where(a => a.Deadline > now && a.Deadline <= now + Horizon
                        && _db.AssignmentUnitLinks.Any(x => x.AssignmentId == a.Id && unitIds.Contains(x.UnitId)));
        if (gId.HasValue) asgQuery = asgQuery.Where(a => _db.AssignmentGroupLinks.Any(x => x.AssignmentId == a.Id && x.GroupId == gId.Value));
        var assignments = await asgQuery.OrderBy(a => a.Deadline).Take(10)
            .Select(a => new { a.Id, a.Title, a.Deadline }).ToListAsync();
        if (assignments.Count > 0)
        {
            var aIds = assignments.Select(a => a.Id).ToList();
            var done = (await _db.AssignmentSubmissions.AsNoTracking()
                    .Where(s => s.StudentId == studentId && aIds.Contains(s.AssignmentId))
                    .Select(s => s.AssignmentId).ToListAsync()).ToHashSet();
            foreach (var a in assignments.Where(a => !done.Contains(a.Id)))
            {
                var urgent = (a.Deadline - now).TotalHours <= 24;
                tasks.Add(new PlanTask(
                    $"asg:{a.Id}", "assignment", $"سلّم واجب: {a.Title}",
                    $"التسليم {InHours(a.Deadline, now)}", 25, urgent ? 1 : 2,
                    new { kind = "open_assignment", assignmentId = a.Id }));
            }
        }

        // ───── 3) spaced-repetition cards that are due ─────
        var dueQuery = _db.ReviewCards.AsNoTracking()
            .Where(c => c.StudentId == studentId && !c.IsArchived && c.DueAt <= now);
        var dueCount = await dueQuery.CountAsync();
        var reviewsToday = await _db.ReviewLogs.AsNoTracking()
            .CountAsync(l => l.StudentId == studentId && l.ReviewedAt >= todayStartUtc);
        if (dueCount > 0)
        {
            var planned = Math.Min(dueCount, MaxReviewCardsPerPlan);
            tasks.Add(new PlanTask(
                "review", "review", $"راجع {planned} سؤال من أخطائك",
                dueCount > planned ? $"عندك {dueCount} سؤال مستحق، ابدأ بـ {planned}" : "أسئلة غلطت فيها ومحتاجة تتثبت",
                Math.Max(3, (int)Math.Ceiling(planned * MinutesPerReviewCard)),
                dueCount >= 10 ? 2 : 3,
                new { kind = "review_session", limit = planned }));
        }

        // ───── 4) lectures left unfinished ─────
        var positions = await _db.LecturePlaybackPositions.AsNoTracking()
            .Where(p => p.StudentId == studentId)
            .Select(p => new { p.LectureId, p.PositionSeconds, p.DurationSeconds, p.Completed, p.UpdatedAt })
            .ToListAsync();
        var startedIds = positions.Select(p => p.LectureId).ToHashSet();

        var unfinished = positions
            .Where(p => !p.Completed && p.PositionSeconds >= 30 && p.UpdatedAt >= now.AddDays(-14))
            .OrderByDescending(p => p.UpdatedAt).Take(2).ToList();
        if (unfinished.Count > 0)
        {
            var ids = unfinished.Select(u => u.LectureId).ToList();
            var names = await _db.Lectures.AsNoTracking().Where(l => ids.Contains(l.Id))
                .Select(l => new { l.Id, l.Name }).ToDictionaryAsync(l => l.Id, l => l.Name);
            foreach (var u in unfinished)
            {
                if (!names.TryGetValue(u.LectureId, out var name)) continue;
                var left = u.DurationSeconds.HasValue ? Math.Max(1, (u.DurationSeconds.Value - u.PositionSeconds) / 60) : 15;
                tasks.Add(new PlanTask(
                    $"cont:{u.LectureId}", "continue_lecture", $"كمّل محاضرة: {name}",
                    u.DurationSeconds.HasValue ? $"فاضل حوالي {left} دقيقة" : "وقفت في النص، كمّل من مكانك",
                    Math.Min(30, left), 3,
                    new { kind = "open_lecture", lectureId = u.LectureId, startSeconds = u.PositionSeconds }));
            }
        }

        // ───── 5) newest lectures not opened yet ─────
        var lectureQuery = _db.Lectures.AsNoTracking().Where(l =>
            l.UnitId != null && unitIds.Contains(l.UnitId.Value)
            && l.AttendanceMethod == AttendanceMethod.Online
            && (l.StorageFileKey != null || l.YoutubeLink != null)
            && l.ViewLimit == null && !l.RequireLinkExam && !l.RequireLinkAssignment);
        if (gId.HasValue) lectureQuery = lectureQuery.Where(l => _db.LectureGroupLinks.Any(x => x.LectureId == l.Id && x.GroupId == gId.Value));
        var fresh = (await lectureQuery.OrderByDescending(l => l.CreatedAt).Take(15)
                .Select(l => new { l.Id, l.Name, l.UnitId }).ToListAsync())
            .Where(l => !startedIds.Contains(l.Id)).Take(2).ToList();
        foreach (var l in fresh)
            tasks.Add(new PlanTask(
                $"new:{l.Id}", "new_lecture", $"شوف محاضرة جديدة: {l.Name}", "لسه ما بدأتهاش", 25, 4,
                new { kind = "open_lecture", lectureId = l.Id, startSeconds = 0 }));

        // ───── 6) practice where the open mistakes pile up ─────
        var weak = await _db.ReviewCards.AsNoTracking()
            .Where(c => c.StudentId == studentId && c.CourseKind == "Unit" && !c.IsArchived && c.MasteredAt == null)
            .GroupBy(c => new { c.CourseId, c.CourseName })
            .Select(g => new { g.Key.CourseId, g.Key.CourseName, Count = g.Count() })
            .OrderByDescending(x => x.Count).Take(1).ToListAsync();
        foreach (var w in weak.Where(x => x.Count >= 5))
            tasks.Add(new PlanTask(
                $"weak:{w.CourseId}", "practice_weak", $"تدرّب على نقط ضعفك في {w.CourseName}",
                $"{w.Count} سؤال لسه محتاج تثبّته", 15, 3,
                new { kind = "bank_practice", unitIds = new[] { w.CourseId }, weakOnly = true, count = 10, durationMinutes = 15 }));

        // ───── fit into the time budget ─────
        var ordered = tasks.OrderBy(t => t.Priority).ThenBy(t => t.Minutes).ToList();
        var used = 0;
        var result = new List<object>();
        foreach (var t in ordered)
        {
            var fits = used + t.Minutes <= minutes || used == 0;
            if (fits) used += t.Minutes;
            result.Add(new
            {
                id = t.Id, type = t.Type, title = t.Title, subtitle = t.Subtitle,
                minutes = t.Minutes, priority = t.Priority, optional = !fits, action = t.Action,
            });
        }

        string headline;
        var top = ordered.FirstOrDefault();
        if (top == null) headline = "مفيش حاجة مستعجلة النهاردة. استغل الوقت وراجع أو اتدرب من بنك الأسئلة 👌";
        else if (top.Priority == 1) headline = "في حاجة مستعجلة النهاردة، ابدأ بيها الأول ⏰";
        else headline = $"خطة النهاردة جاهزة: حوالي {used} دقيقة";

        return Ok(new
        {
            date = cairoNow.Date.ToString("yyyy-MM-dd"),
            budgetMinutes = minutes,
            plannedMinutes = used,
            headline,
            tasks = result,
            doneToday = new { reviews = reviewsToday },
            counts = new { dueReviewCards = dueCount, openTasks = ordered.Count },
        });
    }
}
