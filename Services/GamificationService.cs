using System.Collections.Concurrent;
using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Services;

public record BadgeDef(string Code, string Title, string Description, string Icon, string Tier, string Metric, int Target);

/// <summary>
/// Gamification engine: points, levels, daily streak, badges and leaderboard.
///
/// DESIGN: no existing controller is touched. Points are DERIVED from rows that
/// already exist (quiz results, submissions, attendance, ...) by an idempotent
/// "sync" (SyncAsync) that inserts one StudentPointEvent per source row, keyed by
/// RefKey, so the same quiz can never be paid twice. The sync runs lazily when the
/// student opens the app (GET Gamification/me) and is throttled per student.
///
/// POINT RULES (shown to the student via GET Gamification/rules):
///   quiz / lecture exam            10 + up to 20 by score %
///   assignment / lecture assignment 8 + up to 12 by score %
///   assignment center               8 + up to 12 by score %
///   question-bank practice          5 + up to 15 by score %
///   attendance                      5
///   finished a lecture video (90%)  5
///   correct spaced-repetition answer 2   (max 30 per day)
///   answer accepted in lecture Q&amp;A  5
///   streak milestones (3/7/14/30/60/100 days) 10/25/50/100/200/400
/// </summary>
public class GamificationService
{
    // Egypt: weeks/days are Cairo-local. Streak days are Cairo calendar days.
    private static readonly int[] StreakMilestones = { 3, 7, 14, 30, 60, 100 };
    private static readonly int[] StreakBonus = { 10, 25, 50, 100, 200, 400 };
    private const int MaxRewardedReviewsPerDay = 30;

    public static readonly IReadOnlyList<BadgeDef> Catalog = new List<BadgeDef>
    {
        new("first_step",   "أول خطوة",          "أنجزت أول نشاط ليك على المنصة",          "flag",           "bronze", "events", 1),
        new("quiz_10",      "مجتهد",             "سلّمت 10 اختبارات/واجبات",               "edit_note",      "bronze", "submissions", 10),
        new("quiz_50",      "متفوق",             "سلّمت 50 اختبار/واجب",                    "workspace_premium", "silver", "submissions", 50),
        new("quiz_150",     "ماكينة حل",         "سلّمت 150 اختبار/واجب",                   "military_tech",  "gold",   "submissions", 150),
        new("perfect_1",    "الدرجة النهائية",   "خدت الدرجة النهائية في اختبار أو واجب",   "star",           "silver", "perfect", 1),
        new("perfect_5",    "نجم النهائيات",     "خدت الدرجة النهائية 5 مرات",              "stars",          "gold",   "perfect", 5),
        new("streak_3",     "ثلاث أيام ورا بعض", "نشاط 3 أيام متتالية",                     "local_fire_department", "bronze", "longest_streak", 3),
        new("streak_7",     "أسبوع نار",         "نشاط 7 أيام متتالية",                     "local_fire_department", "silver", "longest_streak", 7),
        new("streak_30",    "شهر من الالتزام",   "نشاط 30 يوم متتالي",                      "local_fire_department", "gold",   "longest_streak", 30),
        new("points_100",   "100 نقطة",          "جمعت 100 نقطة",                           "bolt",           "bronze", "points", 100),
        new("points_500",   "500 نقطة",          "جمعت 500 نقطة",                           "bolt",           "silver", "points", 500),
        new("points_1000",  "1000 نقطة",         "جمعت 1000 نقطة",                          "bolt",           "gold",   "points", 1000),
        new("points_5000",  "أسطورة",            "جمعت 5000 نقطة",                          "emoji_events",   "gold",   "points", 5000),
        new("reviewer_50",  "مراجع شاطر",        "جاوبت 50 سؤال صح في مراجعة أخطائك",       "replay",         "bronze", "reviews_correct", 50),
        new("reviewer_200", "مصحّح أخطاء",       "جاوبت 200 سؤال صح في مراجعة أخطائك",      "psychology",     "gold",   "reviews_correct", 200),
        new("watcher_5",    "متابع",             "خلّصت 5 محاضرات فيديو",                   "play_circle",    "bronze", "lectures_done", 5),
        new("watcher_25",   "مدمن محاضرات",      "خلّصت 25 محاضرة فيديو",                   "ondemand_video", "silver", "lectures_done", 25),
        new("attendance_10","منتظم",             "حضرت 10 حصص",                             "event_available","silver", "attendance", 10),
        new("helper_3",     "مساعد زمايله",      "3 إجابات اتقبلت في أسئلة المحاضرات",      "volunteer_activism", "gold", "accepted_answers", 3),
    };

    private static readonly Dictionary<string, string> KindLabels = new()
    {
        ["quiz"] = "كويز", ["lecture_exam"] = "امتحان محاضرة", ["lecture_assignment"] = "واجب محاضرة",
        ["assignment"] = "واجب", ["assignment_center"] = "واجب سنتر", ["bank"] = "تدريب من بنك الأسئلة",
        ["attendance"] = "حضور", ["lecture"] = "إنهاء محاضرة", ["review"] = "مراجعة أخطاء",
        ["qa"] = "إجابة مقبولة", ["streak"] = "مكافأة التزام",
    };

    private static readonly ConcurrentDictionary<(int Student, int Tenant), DateTime> LastSync = new();

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IPushService _push;

    public GamificationService(AppDbContext db, ITenantContext tenant, IPushService push)
    {
        _db = db;
        _tenant = tenant;
        _push = push;
    }

    // ───────────────────────── levels ─────────────────────────

    public static int LevelFor(int points) => 1 + (int)Math.Floor(Math.Sqrt(Math.Max(0, points) / 50.0));
    public static int PointsForLevel(int level) => level <= 1 ? 0 : 50 * (level - 1) * (level - 1);

    private static int Pct(int score, int total) => total > 0 ? Math.Clamp((int)Math.Round(score * 100.0 / total), 0, 100) : 0;
    private static DateOnly CairoDay(DateTime utc) => DateOnly.FromDateTime(PushTargets.ToCairo(utc));

    // ───────────────────────── sync ─────────────────────────

    public async Task SyncAsync(int studentId, bool force = false, CancellationToken ct = default)
    {
        var tenantId = _tenant.CurrentTenantId;
        if (!tenantId.HasValue || studentId <= 0) return;

        var key = (studentId, tenantId.Value);
        var now = DateTime.UtcNow;
        if (!force && LastSync.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(20)) return;
        LastSync[key] = now;

        var existingRows = await _db.StudentPointEvents.AsNoTracking()
            .Where(e => e.StudentId == studentId)
            .Select(e => new { e.RefKey, e.OccurredAt })
            .ToListAsync(ct);
        var existing = existingRows.Select(e => e.RefKey).ToHashSet();

        var fresh = new List<StudentPointEvent>();
        void Add(string kind, string refKey, int points, DateTime at, int? percent = null)
        {
            if (points <= 0 || !existing.Add(refKey)) return;
            fresh.Add(new StudentPointEvent
            {
                TeacherId = tenantId.Value, StudentId = studentId, Kind = kind, RefKey = refKey,
                Points = points, Percent = percent, OccurredAt = at,
            });
        }

        // 1) graded sources
        foreach (var r in await _db.QuizResults.AsNoTracking()
                     .Where(r => r.StudentId == studentId && !r.IsAutoSubmitted)
                     .Select(r => new { r.Id, r.Score, r.TotalMarks, r.GradedAt }).ToListAsync(ct))
        {
            var p = Pct(r.Score, r.TotalMarks);
            Add("quiz", $"quiz:{r.Id}", 10 + (int)Math.Round(20 * p / 100.0), r.GradedAt, p);
        }

        foreach (var r in await _db.LectureExamResults.AsNoTracking()
                     .Where(r => r.StudentId == studentId && r.Score > 0)
                     .Select(r => new { r.Id, r.Score, r.TotalMarks, r.GradedAt }).ToListAsync(ct))
        {
            var p = Pct(r.Score, r.TotalMarks);
            Add("lecture_exam", $"lexam:{r.Id}", 10 + (int)Math.Round(20 * p / 100.0), r.GradedAt, p);
        }

        foreach (var r in await _db.LectureAssignmentResults.AsNoTracking()
                     .Where(r => r.StudentId == studentId && r.Score > 0)
                     .Select(r => new { r.Id, r.Score, r.TotalMarks, r.GradedAt }).ToListAsync(ct))
        {
            var p = Pct(r.Score, r.TotalMarks);
            Add("lecture_assignment", $"lasg:{r.Id}", 8 + (int)Math.Round(12 * p / 100.0), r.GradedAt, p);
        }

        foreach (var r in await _db.AssignmentSubmissions.AsNoTracking()
                     .Where(r => r.StudentId == studentId && r.Score > 0)
                     .Select(r => new { r.Id, r.Score, r.TotalMarks, r.SubmittedAt }).ToListAsync(ct))
        {
            var p = Pct(r.Score, r.TotalMarks);
            Add("assignment", $"asg:{r.Id}", 8 + (int)Math.Round(12 * p / 100.0), r.SubmittedAt, p);
        }

        foreach (var r in await _db.AssignmentCenterSubmissions.AsNoTracking()
                     .Where(r => r.StudentId == studentId && r.Score > 0)
                     .Select(r => new { r.Id, r.Score, r.TotalMarks, r.SubmittedAt }).ToListAsync(ct))
        {
            var p = Pct(r.Score, r.TotalMarks);
            Add("assignment_center", $"acenter:{r.Id}", 8 + (int)Math.Round(12 * p / 100.0), r.SubmittedAt, p);
        }

        foreach (var r in await _db.BankAttempts.AsNoTracking()
                     .Where(r => r.StudentId == studentId && r.SubmittedAt != null)
                     .Select(r => new { r.Id, r.Score, r.TotalMarks, r.SubmittedAt }).ToListAsync(ct))
        {
            var p = Pct(r.Score, r.TotalMarks);
            Add("bank", $"bank:{r.Id}", 5 + (int)Math.Round(15 * p / 100.0), r.SubmittedAt!.Value, p);
        }

        // 2) attendance
        foreach (var a in await _db.Attendances.AsNoTracking()
                     .Where(a => a.StudentId == studentId)
                     .Select(a => new { a.Id, a.Date }).ToListAsync(ct))
            Add("attendance", $"att:{a.Id}", 5, a.Date);

        // 3) finished lecture videos
        foreach (var p in await _db.LecturePlaybackPositions.AsNoTracking()
                     .Where(p => p.StudentId == studentId && p.Completed)
                     .Select(p => new { p.LectureId, p.CompletedAt, p.UpdatedAt }).ToListAsync(ct))
            Add("lecture", $"lec:{p.LectureId}", 5, p.CompletedAt ?? p.UpdatedAt);

        // 4) spaced repetition (all logs count for the streak; only correct ones pay, capped per day)
        var logs = await _db.ReviewLogs.AsNoTracking()
            .Where(l => l.StudentId == studentId)
            .Select(l => new { l.Id, l.Correct, l.ReviewedAt })
            .ToListAsync(ct);
        foreach (var day in logs.Where(l => l.Correct).GroupBy(l => CairoDay(l.ReviewedAt)))
            foreach (var l in day.OrderBy(x => x.ReviewedAt).ThenBy(x => x.Id).Take(MaxRewardedReviewsPerDay))
                Add("review", $"rev:{l.Id}", 2, l.ReviewedAt);

        // 5) accepted Q&A answers
        foreach (var c in await _db.LectureComments.AsNoTracking()
                     .Where(c => c.AuthorRole == "Student" && c.AuthorId == studentId && c.IsAccepted && !c.IsDeleted && c.ParentId != null)
                     .Select(c => new { c.Id, c.CreatedAt }).ToListAsync(ct))
            Add("qa", $"qa:{c.Id}", 5, c.CreatedAt);

        // 6) streak bonuses (computed from every activity day, retroactively)
        var activityDays = new HashSet<DateOnly>();
        foreach (var e in existingRows) activityDays.Add(CairoDay(e.OccurredAt));
        foreach (var e in fresh) activityDays.Add(CairoDay(e.OccurredAt));
        foreach (var l in logs) activityDays.Add(CairoDay(l.ReviewedAt));
        foreach (var u in await _db.LecturePlaybackPositions.AsNoTracking()
                     .Where(p => p.StudentId == studentId).Select(p => p.UpdatedAt).ToListAsync(ct))
            activityDays.Add(CairoDay(u));

        foreach (var run in StreakRuns(activityDays))
        {
            for (var i = 0; i < StreakMilestones.Length; i++)
            {
                var m = StreakMilestones[i];
                if (run.Length < m) break;
                var reachedOn = run.Start.AddDays(m - 1);
                // 10:00 UTC ~= noon in Cairo. Kind MUST be Utc: Npgsql refuses Unspecified for timestamptz.
                Add("streak", $"streak:{m}:{run.Start:yyyyMMdd}", StreakBonus[i],
                    DateTime.SpecifyKind(reachedOn.ToDateTime(new TimeOnly(10, 0)), DateTimeKind.Utc));
            }
        }

        if (fresh.Count > 0)
        {
            try
            {
                _db.StudentPointEvents.AddRange(fresh);
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // A concurrent sync inserted the same RefKeys first (unique index) -- harmless.
                foreach (var e in _db.ChangeTracker.Entries<StudentPointEvent>().ToList())
                    e.State = EntityState.Detached;
                return;
            }
        }

        await AwardBadgesAsync(studentId, tenantId.Value, ct);
    }

    private record Run(DateOnly Start, int Length);

    private static List<Run> StreakRuns(HashSet<DateOnly> days)
    {
        var runs = new List<Run>();
        if (days.Count == 0) return runs;
        var sorted = days.OrderBy(d => d).ToList();
        var start = sorted[0];
        var prev = sorted[0];
        var len = 1;
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] == prev.AddDays(1)) { len++; }
            else { runs.Add(new Run(start, len)); start = sorted[i]; len = 1; }
            prev = sorted[i];
        }
        runs.Add(new Run(start, len));
        return runs;
    }

    // ───────────────────────── stats / badges ─────────────────────────

    private async Task<(Dictionary<string, int> Stats, int CurrentStreak, int LongestStreak, bool ActiveToday)>
        ComputeStatsAsync(int studentId, CancellationToken ct)
    {
        var events = await _db.StudentPointEvents.AsNoTracking()
            .Where(e => e.StudentId == studentId)
            .Select(e => new { e.Kind, e.Points, e.Percent, e.OccurredAt })
            .ToListAsync(ct);

        var days = events.Select(e => CairoDay(e.OccurredAt)).ToHashSet();
        foreach (var l in await _db.ReviewLogs.AsNoTracking().Where(l => l.StudentId == studentId)
                     .Select(l => l.ReviewedAt).ToListAsync(ct))
            days.Add(CairoDay(l));
        foreach (var u in await _db.LecturePlaybackPositions.AsNoTracking().Where(p => p.StudentId == studentId)
                     .Select(p => p.UpdatedAt).ToListAsync(ct))
            days.Add(CairoDay(u));

        var runs = StreakRuns(days);
        var today = CairoDay(DateTime.UtcNow);
        var current = 0;
        var activeToday = days.Contains(today);
        var last = runs.LastOrDefault();
        if (last != null)
        {
            var end = last.Start.AddDays(last.Length - 1);
            if (end == today || end == today.AddDays(-1)) current = last.Length;
        }
        var longest = runs.Count == 0 ? 0 : runs.Max(r => r.Length);

        var submissionKinds = new[] { "quiz", "lecture_exam", "lecture_assignment", "assignment", "assignment_center", "bank" };
        var stats = new Dictionary<string, int>
        {
            ["events"] = events.Count,
            ["points"] = events.Sum(e => e.Points),
            ["submissions"] = events.Count(e => submissionKinds.Contains(e.Kind)),
            ["perfect"] = events.Count(e => e.Percent == 100 && submissionKinds.Contains(e.Kind)),
            ["longest_streak"] = longest,
            ["reviews_correct"] = events.Count(e => e.Kind == "review"),
            ["lectures_done"] = events.Count(e => e.Kind == "lecture"),
            ["attendance"] = events.Count(e => e.Kind == "attendance"),
            ["accepted_answers"] = events.Count(e => e.Kind == "qa"),
        };
        return (stats, current, longest, activeToday);
    }

    private async Task AwardBadgesAsync(int studentId, int tenantId, CancellationToken ct)
    {
        var (stats, _, _, _) = await ComputeStatsAsync(studentId, ct);
        var owned = (await _db.StudentBadges.AsNoTracking().Where(b => b.StudentId == studentId)
            .Select(b => b.Code).ToListAsync(ct)).ToHashSet();

        var earned = Catalog.Where(b => !owned.Contains(b.Code) && stats.GetValueOrDefault(b.Metric) >= b.Target).ToList();
        if (earned.Count == 0) return;

        try
        {
            _db.StudentBadges.AddRange(earned.Select(b => new StudentBadge
            {
                TeacherId = tenantId, StudentId = studentId, Code = b.Code,
            }));
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            foreach (var e in _db.ChangeTracker.Entries<StudentBadge>().ToList()) e.State = EntityState.Detached;
            return;
        }

        var title = earned.Count == 1 ? $"بادج جديد 🏅 {earned[0].Title}" : $"حصلت على {earned.Count} بادجات جديدة 🏅";
        var body = earned.Count == 1 ? earned[0].Description : string.Join(" • ", earned.Take(3).Select(e => e.Title));
        await _push.SendToStudentsAsync(new[] { studentId }, title, body,
            new Dictionary<string, string> { ["type"] = "badge", ["teacherId"] = tenantId.ToString() }, ct);
    }

    // ───────────────────────── read models ─────────────────────────

    public async Task<object> GetSummaryAsync(int studentId, CancellationToken ct = default)
    {
        await SyncAsync(studentId, false, ct);

        var (stats, current, longest, activeToday) = await ComputeStatsAsync(studentId, ct);
        var points = stats["points"];
        var level = LevelFor(points);
        var floor = PointsForLevel(level);
        var next = PointsForLevel(level + 1);

        var now = DateTime.UtcNow;
        var week = await _db.StudentPointEvents.AsNoTracking()
            .Where(e => e.StudentId == studentId && e.OccurredAt >= now.AddDays(-7)).SumAsync(e => (int?)e.Points, ct) ?? 0;

        var recent = (await _db.StudentPointEvents.AsNoTracking()
                .Where(e => e.StudentId == studentId)
                .OrderByDescending(e => e.OccurredAt).Take(10)
                .Select(e => new { e.Kind, e.Points, e.OccurredAt }).ToListAsync(ct))
            .Select(e => new { kind = e.Kind, label = KindLabels.GetValueOrDefault(e.Kind, e.Kind), points = e.Points, at = e.OccurredAt })
            .ToList();

        var owned = await _db.StudentBadges.AsNoTracking().Where(b => b.StudentId == studentId)
            .ToDictionaryAsync(b => b.Code, b => b.AwardedAt, ct);

        var badges = Catalog.Select(b => new
        {
            code = b.Code, title = b.Title, description = b.Description, icon = b.Icon, tier = b.Tier,
            earned = owned.ContainsKey(b.Code),
            awardedAt = owned.TryGetValue(b.Code, out var at) ? (DateTime?)at : null,
            progress = Math.Min(stats.GetValueOrDefault(b.Metric), b.Target),
            target = b.Target,
        }).ToList();

        return new
        {
            points,
            level,
            levelFloor = floor,
            nextLevelAt = next,
            weekPoints = week,
            streak = new { current, longest, activeToday },
            badgesEarned = owned.Count,
            badgesTotal = Catalog.Count,
            badges,
            recent,
        };
    }

    /// <summary>
    /// period: "week" (last 7 days) | "month" (last 30 days) | anything else = all time.
    /// Ranking is among students of the same school year under the current teacher.
    /// </summary>
    public async Task<object> GetLeaderboardAsync(int studentId, string? period, int? schoolYear, int take, CancellationToken ct = default)
    {
        await SyncAsync(studentId, false, ct);

        var now = DateTime.UtcNow;
        DateTime? since = period switch
        {
            "week" => now.AddDays(-7),
            "month" => now.AddDays(-30),
            _ => null,
        };

        var q = _db.StudentPointEvents.AsNoTracking().AsQueryable();
        if (since.HasValue) q = q.Where(e => e.OccurredAt >= since.Value);

        var totals = await q.GroupBy(e => e.StudentId)
            .Select(g => new { StudentId = g.Key, Points = g.Sum(x => x.Points) })
            .ToListAsync(ct);

        var ids = totals.Select(t => t.StudentId).ToList();
        var people = await _db.Students.IgnoreQueryFilters().AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, s.SchoolYear })
            .ToDictionaryAsync(s => s.Id, ct);

        var ranked = totals
            .Where(t => people.ContainsKey(t.StudentId) && (!schoolYear.HasValue || people[t.StudentId].SchoolYear == schoolYear.Value))
            .OrderByDescending(t => t.Points).ThenBy(t => t.StudentId)
            .Select((t, i) => new { rank = i + 1, studentId = t.StudentId, name = people[t.StudentId].Name, points = t.Points })
            .ToList();

        var me = ranked.FirstOrDefault(r => r.studentId == studentId);
        return new
        {
            period = period ?? "all",
            total = ranked.Count,
            me = me == null ? null : new { me.rank, me.points },
            top = ranked.Take(Math.Clamp(take, 1, 50))
                .Select(r => new { r.rank, r.name, r.points, isMe = r.studentId == studentId })
                .ToList(),
        };
    }

    public static object Rules() => new
    {
        points = new object[]
        {
            new { label = "كويز / امتحان محاضرة", value = "10 + حتى 20 حسب درجتك" },
            new { label = "واجب / واجب محاضرة / واجب سنتر", value = "8 + حتى 12 حسب درجتك" },
            new { label = "تدريب من بنك الأسئلة", value = "5 + حتى 15 حسب درجتك" },
            new { label = "حضور حصة", value = "5" },
            new { label = "إنهاء محاضرة فيديو (90%)", value = "5" },
            new { label = "إجابة صح في مراجعة أخطائك", value = "2 (بحد أقصى 30 يوميًا)" },
            new { label = "إجابتك اتقبلت في سؤال محاضرة", value = "5" },
            new { label = "التزام 3 / 7 / 14 / 30 / 60 / 100 يوم", value = "10 / 25 / 50 / 100 / 200 / 400" },
        },
        levels = "المستوى = 1 + جذر (نقاطك ÷ 50)، مثال: 50 نقطة = مستوى 2، 200 = مستوى 3، 450 = مستوى 4",
    };
}
