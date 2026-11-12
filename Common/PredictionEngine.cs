using EduApi.Data;
using EduApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Common;

/// <summary>One student's predicted result + risk, computed from existing data (no ML, fully explainable).</summary>
public sealed class StudentPrediction
{
    public int StudentId { get; set; }
    public int GroupId { get; set; }
    /// <summary>Graded assessments (quizzes, lecture exams, lecture homework, homework) in the last 180 days.</summary>
    public int Samples { get; set; }
    public double? Average { get; set; }
    /// <summary>Average where recent results weigh more (half-life 30 days).</summary>
    public double? RecencyAverage { get; set; }
    /// <summary>Change per assessment (percentage points) over the last 8.</summary>
    public double? Slope { get; set; }
    public double? AttendanceRate { get; set; }
    public int Missed { get; set; }
    public double? MissedRate { get; set; }
    public int? InactiveDays { get; set; }
    public double? Predicted { get; set; }
    /// <summary>low | medium | high -- how much data the prediction rests on.</summary>
    public string Confidence { get; set; } = "low";
    /// <summary>0..100, higher = more at risk.</summary>
    public int Risk { get; set; }
    /// <summary>low | medium | high | nodata (nothing graded yet)</summary>
    public string Level { get; set; } = "low";
    public List<string> Reasons { get; set; } = new();
    /// <summary>Last results (oldest first), percentages.</summary>
    public List<double> Recent { get; set; } = new();
}

/// <summary>
/// The model, in one sentence: weighted recent average, nudged by the trend, attendance and missed
/// assessments; the risk score adds how long the student has been silent.
///
///   predicted = recencyAverage + clamp(slope * 2, -12, +12) + (attendance - 0.8) * 10 - missedRate * 10
///   risk      = 45 * (1 - average)  [25 when there is no result yet]
///             + min(15, -slope * 3) [only when falling]
///             + 20 * (1 - attendance)
///             + 15 * missedRate
///             + min(5, inactiveDays / 14 * 5)
///   level     = high >= 55, medium >= 30, else low  (nodata when nothing has been graded yet)
/// </summary>
public static class PredictionEngine
{
    private const int HistoryDays = 180;
    private const int AttendanceDays = 45;
    private const double HalfLifeDays = 30.0;
    private const int TrendWindow = 8;

    private sealed record Sample(int StudentId, DateTime At, double Pct, bool Missed);

    public static async Task<List<StudentPrediction>> ComputeAsync(
        AppDbContext db, IReadOnlyDictionary<int, int> groupOfStudent, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var ids = groupOfStudent.Keys.ToList();
        var results = ids.ToDictionary(i => i, i => new StudentPrediction { StudentId = i, GroupId = groupOfStudent[i] });
        if (ids.Count == 0) return new List<StudentPrediction>();

        var since = now.AddDays(-HistoryDays);

        // ───── graded samples ─────
        var samples = new List<Sample>();

        foreach (var r in await db.QuizResults.AsNoTracking()
                     .Where(r => r.TotalMarks > 0 && r.GradedAt >= since && ids.Contains(r.StudentId))
                     .Select(r => new { r.StudentId, r.Score, r.TotalMarks, r.GradedAt, r.IsAutoSubmitted })
                     .ToListAsync(ct))
            samples.Add(new Sample(r.StudentId, r.GradedAt, 100.0 * r.Score / r.TotalMarks, r.IsAutoSubmitted));

        foreach (var r in await db.LectureExamResults.AsNoTracking()
                     .Where(r => r.TotalMarks > 0 && r.Score > 0 && r.GradedAt >= since && ids.Contains(r.StudentId))
                     .Select(r => new { r.StudentId, r.Score, r.TotalMarks, r.GradedAt })
                     .ToListAsync(ct))
            samples.Add(new Sample(r.StudentId, r.GradedAt, 100.0 * r.Score / r.TotalMarks, false));

        foreach (var r in await db.LectureAssignmentResults.AsNoTracking()
                     .Where(r => r.TotalMarks > 0 && r.Score > 0 && r.GradedAt >= since && ids.Contains(r.StudentId))
                     .Select(r => new { r.StudentId, r.Score, r.TotalMarks, r.GradedAt })
                     .ToListAsync(ct))
            samples.Add(new Sample(r.StudentId, r.GradedAt, 100.0 * r.Score / r.TotalMarks, false));

        foreach (var r in await db.AssignmentSubmissions.AsNoTracking()
                     .Where(r => r.TotalMarks > 0 && r.SubmittedAt >= since && ids.Contains(r.StudentId))
                     .Select(r => new { r.StudentId, r.Score, r.TotalMarks, r.SubmittedAt })
                     .ToListAsync(ct))
            samples.Add(new Sample(r.StudentId, r.SubmittedAt, 100.0 * r.Score / r.TotalMarks, false));

        // ───── attendance of in-person lectures the student's group had in the last 45 days ─────
        var attSince = now.AddDays(-AttendanceDays);
        var centerLectures = await db.Lectures.AsNoTracking()
            .Where(l => l.AttendanceMethod == AttendanceMethod.Center && l.CreatedAt >= attSince)
            .Select(l => l.Id).ToListAsync(ct);

        var groupIds = groupOfStudent.Values.Distinct().ToList();
        var heldByGroup = new Dictionary<int, HashSet<int>>();
        if (centerLectures.Count > 0)
        {
            var links = await db.LectureGroupLinks.AsNoTracking()
                .Where(x => centerLectures.Contains(x.LectureId) && groupIds.Contains(x.GroupId))
                .Select(x => new { x.LectureId, x.GroupId }).ToListAsync(ct);
            foreach (var g in links.GroupBy(x => x.GroupId))
                heldByGroup[g.Key] = g.Select(x => x.LectureId).ToHashSet();
        }

        var attendance = await db.Attendances.AsNoTracking()
            .Where(a => a.Date >= attSince && ids.Contains(a.StudentId))
            .Select(a => new { a.StudentId, a.LectureId, a.Date }).ToListAsync(ct);

        // ───── last activity (videos, reviews) ─────
        var lastPlayback = await db.LecturePlaybackPositions.AsNoTracking()
            .Where(p => p.UpdatedAt >= since && ids.Contains(p.StudentId))
            .GroupBy(p => p.StudentId).Select(g => new { StudentId = g.Key, Last = g.Max(x => x.UpdatedAt) })
            .ToListAsync(ct);
        var lastReview = await db.ReviewLogs.AsNoTracking()
            .Where(l => l.ReviewedAt >= since && ids.Contains(l.StudentId))
            .GroupBy(l => l.StudentId).Select(g => new { StudentId = g.Key, Last = g.Max(x => x.ReviewedAt) })
            .ToListAsync(ct);

        var lastActivity = new Dictionary<int, DateTime>();
        void Touch(int s, DateTime at)
        {
            if (!lastActivity.TryGetValue(s, out var cur) || at > cur) lastActivity[s] = at;
        }
        foreach (var s in samples) Touch(s.StudentId, s.At);
        foreach (var a in attendance) Touch(a.StudentId, a.Date);
        foreach (var p in lastPlayback) Touch(p.StudentId, p.Last);
        foreach (var l in lastReview) Touch(l.StudentId, l.Last);

        var samplesByStudent = samples.GroupBy(s => s.StudentId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.At).ToList());
        var attendedByStudent = attendance.GroupBy(a => a.StudentId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.LectureId).ToHashSet());

        foreach (var p in results.Values)
        {
            samplesByStudent.TryGetValue(p.StudentId, out var list);
            list ??= new List<Sample>();
            p.Samples = list.Count;
            p.Missed = list.Count(x => x.Missed);
            p.MissedRate = list.Count == 0 ? null : (double)p.Missed / list.Count;
            p.Recent = list.TakeLast(TrendWindow).Select(x => Math.Round(x.Pct, 1)).ToList();

            if (list.Count > 0)
            {
                p.Average = Math.Round(list.Average(x => x.Pct), 1);
                var weights = list.Select(x => Math.Pow(0.5, Math.Max(0, (now - x.At).TotalDays) / HalfLifeDays)).ToList();
                var wSum = weights.Sum();
                p.RecencyAverage = Math.Round(list.Select((x, i) => x.Pct * weights[i]).Sum() / wSum, 1);
            }

            var window = list.TakeLast(TrendWindow).ToList();
            if (window.Count >= 4) p.Slope = Math.Round(Slope(window.Select(x => x.Pct).ToList()), 2);

            if (heldByGroup.TryGetValue(p.GroupId, out var held) && held.Count > 0)
            {
                attendedByStudent.TryGetValue(p.StudentId, out var mine);
                var attended = mine == null ? 0 : held.Count(mine.Contains);
                p.AttendanceRate = Math.Round((double)attended / held.Count, 2);
            }

            if (lastActivity.TryGetValue(p.StudentId, out var last))
                p.InactiveDays = Math.Max(0, (int)(now - last).TotalDays);
            else if (samplesByStudent.ContainsKey(p.StudentId) == false)
                p.InactiveDays = HistoryDays;

            Score(p);
        }

        return results.Values.ToList();
    }

    private static double Slope(IReadOnlyList<double> y)
    {
        var n = y.Count;
        var meanX = (n - 1) / 2.0;
        var meanY = y.Average();
        double num = 0, den = 0;
        for (var i = 0; i < n; i++)
        {
            num += (i - meanX) * (y[i] - meanY);
            den += (i - meanX) * (i - meanX);
        }
        return den == 0 ? 0 : num / den;
    }

    private static void Score(StudentPrediction p)
    {
        p.Confidence = p.Samples >= 6 ? "high" : p.Samples >= 3 ? "medium" : "low";

        // Nothing graded yet: we can't say anything honest about risk, so don't flag the student.
        if (p.Samples == 0)
        {
            p.Risk = 0;
            p.Level = "nodata";
            p.Reasons = new List<string> { "لسه مفيش نتايج كفاية للتقييم" };
            return;
        }

        if (p.RecencyAverage.HasValue)
        {
            var v = p.RecencyAverage.Value;
            v += Math.Clamp((p.Slope ?? 0) * 2, -12, 12);
            if (p.AttendanceRate.HasValue) v += (p.AttendanceRate.Value - 0.8) * 10;
            if (p.MissedRate.HasValue) v -= p.MissedRate.Value * 10;
            p.Predicted = Math.Round(Math.Clamp(v, 0, 100), 1);
        }

        var scoreRisk = p.Average.HasValue ? 45 * (1 - p.Average.Value / 100.0) : 25;
        var trendRisk = p.Slope is < 0 ? Math.Min(15, -p.Slope.Value * 3) : 0;
        var attRisk = p.AttendanceRate.HasValue ? 20 * (1 - p.AttendanceRate.Value) : 0;
        var missRisk = p.MissedRate.HasValue ? 15 * p.MissedRate.Value : 0;
        var inactRisk = p.InactiveDays.HasValue ? Math.Min(5, p.InactiveDays.Value / 14.0 * 5) : 0;

        p.Risk = (int)Math.Round(Math.Clamp(scoreRisk + trendRisk + attRisk + missRisk + inactRisk, 0, 100));
        p.Level = p.Risk >= 55 ? "high" : p.Risk >= 30 ? "medium" : "low";

        var reasons = new List<(double Weight, string Text)>();
        if (!p.Average.HasValue) reasons.Add((25, "لسه مفيش نتايج كفاية للتقييم"));
        else if (p.Average < 60) reasons.Add((scoreRisk, $"متوسط درجاته {p.Average:0}%"));
        if (p.Slope is < -1) reasons.Add((trendRisk, "مستواه بينزل في آخر الاختبارات"));
        if (p.AttendanceRate is < 0.7) reasons.Add((attRisk, $"حضر {(int)Math.Round(p.AttendanceRate.Value * 100)}% بس من الحصص"));
        if (p.Missed > 0) reasons.Add((missRisk, $"فاته {p.Missed} امتحان/كويز ماسلّمهوش"));
        if (p.InactiveDays is >= 14) reasons.Add((inactRisk + 5, $"من {p.InactiveDays} يوم مفيش نشاط"));
        p.Reasons = reasons.OrderByDescending(r => r.Weight).Select(r => r.Text).Take(3).ToList();
    }
}
