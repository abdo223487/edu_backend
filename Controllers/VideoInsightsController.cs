using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// "تحليل أماكن الملل في الفيديو": where students stop watching, where they get stuck, and which
/// lectures lose people. Everything is derived from data the app already reports (the last playback
/// position and the view sessions) plus bookmarks, lecture Q&amp;A questions that point at a second,
/// wrong answers at video checkpoints and "شوف الشرح" taps -- so it needs no new table and no app change. Students' private notes are NOT used.
///
/// Route: api/VideoInsights  (teacher / assistants)
///  GET VideoInsights/{lectureId}                    full picture of one lecture
///  GET VideoInsights/overview?unitId=&amp;p=            the lectures of the tenant, the ones losing the most people first
///
/// HOW TO READ IT
///  retention[i]   % of viewers who got at least to the start of slice i (slice = 1/20 of the video)
///  stopPoints     slices where the most viewers stopped without finishing (= where they gave up)
///  hotspots       slices with the most bookmarks, questions and explanation taps (= where they got stuck)
///  skipRate       sessions where the student jumped around instead of watching (watched &lt; 40% of the
///                 distance reached)
/// LIMIT: we only know where each student stopped / how far they got, not every rewind, so "gave up"
/// and "got stuck" are good signals, not a second-by-second heat map.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.Assistant}")]
public class VideoInsightsController : ControllerBase
{
    private const int Slices = 20;
    private const int MinViewersForVerdict = 3;

    private readonly AppDbContext _db;

    public VideoInsightsController(AppDbContext db) => _db = db;

    private sealed class LectureStats
    {
        public int LectureId { get; init; }
        public int Viewers { get; set; }
        public int Completed { get; set; }
        public int DurationSeconds { get; set; }
        public double AvgReachPercent { get; set; }
        public double[] Retention { get; set; } = new double[Slices];
        public int[] Stops { get; set; } = new int[Slices];
        public int[] Hot { get; set; } = new int[Slices];
        public int Sessions { get; set; }
        public int SkipSessions { get; set; }
        public double AvgWatchedPercent { get; set; }
    }

    private static string Label(int seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static int SliceOf(int seconds, int duration)
        => duration <= 0 ? 0 : Math.Clamp((int)((long)seconds * Slices / duration), 0, Slices - 1);

    // ───────────────────────── computation ─────────────────────────

    private async Task<Dictionary<int, LectureStats>> ComputeAsync(List<int> lectureIds)
    {
        var result = lectureIds.ToDictionary(i => i, i => new LectureStats { LectureId = i });
        if (lectureIds.Count == 0) return result;

        var positions = await _db.LecturePlaybackPositions.AsNoTracking()
            .Where(p => lectureIds.Contains(p.LectureId))
            .Select(p => new { p.LectureId, p.StudentId, p.PositionSeconds, p.DurationSeconds, p.Completed })
            .ToListAsync();
        var sessions = await _db.LectureViewSessions.AsNoTracking()
            .Where(s => lectureIds.Contains(s.LectureId))
            .Select(s => new { s.LectureId, s.StudentId, s.StoppedAtSeconds, s.WatchedSeconds })
            .ToListAsync();
        var bookmarks = await _db.LectureBookmarks.AsNoTracking()
            .Where(b => lectureIds.Contains(b.LectureId))
            .Select(b => new { b.LectureId, b.Seconds }).ToListAsync();
        var questions = await _db.LectureComments.AsNoTracking()
            .Where(c => lectureIds.Contains(c.LectureId) && c.ParentId == null && !c.IsDeleted && c.VideoSeconds != null)
            .Select(c => new { c.LectureId, Seconds = c.VideoSeconds!.Value }).ToListAsync();
        // Checkpoints: every wrong answer at a checkpoint is a "got stuck here" signal.
        var checkpoints = await _db.LectureCheckpoints.AsNoTracking()
            .Where(c => lectureIds.Contains(c.LectureId))
            .Select(c => new { c.Id, c.LectureId, c.Seconds }).ToListAsync();
        var cpIds = checkpoints.Select(c => c.Id).ToList();
        var cpWrong = cpIds.Count == 0
            ? new Dictionary<int, int>()
            : (await _db.LectureCheckpointAttempts.AsNoTracking()
                .Where(a => !a.IsCorrect && cpIds.Contains(a.CheckpointId))
                .Select(a => a.CheckpointId).ToListAsync())
                .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var explanations = await _db.QuestionExplanations.AsNoTracking()
            .Where(e => lectureIds.Contains(e.LectureId))
            .Select(e => new { e.LectureId, e.StartSeconds, e.OpenCount }).ToListAsync();

        foreach (var id in lectureIds)
        {
            var s = result[id];
            var pos = positions.Where(p => p.LectureId == id).ToList();
            var ses = sessions.Where(x => x.LectureId == id).ToList();

            // How far did each viewer get?
            var reach = new Dictionary<int, int>();
            var completed = new HashSet<int>();
            foreach (var p in pos)
            {
                reach[p.StudentId] = Math.Max(reach.GetValueOrDefault(p.StudentId), p.PositionSeconds);
                if (p.Completed) completed.Add(p.StudentId);
            }
            foreach (var x in ses)
                reach[x.StudentId] = Math.Max(reach.GetValueOrDefault(x.StudentId), x.StoppedAtSeconds ?? 0);

            var duration = pos.Where(p => p.DurationSeconds.HasValue).Select(p => p.DurationSeconds!.Value).DefaultIfEmpty(0).Max();
            if (duration <= 0 && reach.Count > 0) duration = reach.Values.Max();
            s.DurationSeconds = duration;
            s.Viewers = reach.Count;
            s.Completed = completed.Count;
            if (s.Viewers == 0 || duration <= 0) continue;

            s.AvgReachPercent = Math.Round(reach.Values.Average(v => Math.Min(100.0, 100.0 * v / duration)), 1);

            // Completed viewers reached the end by definition.
            for (var i = 0; i < Slices; i++)
            {
                var start = (long)duration * i / Slices;
                var reached = reach.Count(kv => completed.Contains(kv.Key) || kv.Value >= start);
                s.Retention[i] = Math.Round(100.0 * reached / s.Viewers, 1);
            }

            foreach (var kv in reach.Where(kv => !completed.Contains(kv.Key)))
                s.Stops[SliceOf(kv.Value, duration)]++;

            foreach (var b in bookmarks.Where(b => b.LectureId == id)) s.Hot[SliceOf(b.Seconds, duration)]++;
            foreach (var q in questions.Where(q => q.LectureId == id)) s.Hot[SliceOf(q.Seconds, duration)] += 2;
            foreach (var c in checkpoints.Where(c => c.LectureId == id))
                s.Hot[SliceOf(c.Seconds, duration)] += cpWrong.GetValueOrDefault(c.Id);
            foreach (var e in explanations.Where(e => e.LectureId == id))
                s.Hot[SliceOf(e.StartSeconds, duration)] += Math.Max(1, e.OpenCount);

            var measured = ses.Where(x => (x.StoppedAtSeconds ?? 0) >= 60).ToList();
            s.Sessions = measured.Count;
            s.SkipSessions = measured.Count(x => x.WatchedSeconds < 0.4 * (x.StoppedAtSeconds ?? 0));
            if (measured.Count > 0)
                s.AvgWatchedPercent = Math.Round(measured.Average(x => Math.Min(100.0, 100.0 * x.WatchedSeconds / duration)), 1);
        }

        return result;
    }

    private static object Describe(LectureStats s, string name)
    {
        var width = s.DurationSeconds <= 0 ? 0 : s.DurationSeconds / Slices;

        object Slice(int i) => new
        {
            index = i,
            fromSeconds = width * i,
            toSeconds = width * (i + 1),
            label = $"{Label(width * i)} - {Label(width * (i + 1))}",
        };

        // Where did viewers give up? Slices with the most stops (at least 2 people and 10% of viewers).
        var minStops = Math.Max(2, (int)Math.Ceiling(s.Viewers * 0.10));
        var stopPoints = Enumerable.Range(0, Slices)
            .Where(i => s.Stops[i] >= minStops)
            .OrderByDescending(i => s.Stops[i]).Take(3)
            .Select(i => new
            {
                slice = Slice(i),
                students = s.Stops[i],
                percentOfViewers = Math.Round(100.0 * s.Stops[i] / Math.Max(1, s.Viewers), 1),
            }).ToList();

        // Where did they get stuck? Slices with the most signals.
        var hotspots = Enumerable.Range(0, Slices)
            .Where(i => s.Hot[i] >= 3)
            .OrderByDescending(i => s.Hot[i]).Take(3)
            .Select(i => new { slice = Slice(i), signals = s.Hot[i] }).ToList();

        var topStopIndex = Enumerable.Range(0, Slices).Where(i => s.Stops[i] >= minStops)
            .OrderByDescending(i => s.Stops[i]).Select(i => (int?)i).FirstOrDefault() ?? -1;
        var topHotIndex = Enumerable.Range(0, Slices).Where(i => s.Hot[i] >= 3)
            .OrderByDescending(i => s.Hot[i]).Select(i => (int?)i).FirstOrDefault() ?? -1;

        // Steepest single-slice fall of the retention curve.
        int steepest = -1; double steepestDrop = 0;
        for (var i = 0; i < Slices - 1; i++)
        {
            var drop = s.Retention[i] - s.Retention[i + 1];
            if (drop > steepestDrop) { steepestDrop = drop; steepest = i + 1; }
        }

        var completionRate = s.Viewers == 0 ? 0 : Math.Round(100.0 * s.Completed / s.Viewers, 1);
        var skipRate = s.Sessions == 0 ? 0 : Math.Round(100.0 * s.SkipSessions / s.Sessions, 1);

        var verdicts = new List<string>();
        if (s.Viewers < MinViewersForVerdict)
            verdicts.Add("عدد المشاهدين لسه قليل، التحليل هيبقى أدق مع مشاهدات أكتر.");
        else
        {
            if (completionRate < 40) verdicts.Add($"{completionRate:0}% بس من المشاهدين خلّصوا المحاضرة. دي نسبة ضعيفة.");
            if (steepest >= 0 && steepestDrop >= 15)
                verdicts.Add($"أكبر هبوط عند {Label(width * steepest)}: {steepestDrop:0}% من المشاهدين سابوا عند الجزء ده.");
            if (topStopIndex >= 0)
                verdicts.Add($"أكتر مكان الطلبة بيقفوا عنده: {Label(width * topStopIndex)} - {Label(width * (topStopIndex + 1))}.");
            if (topHotIndex >= 0)
                verdicts.Add($"الطلبة بيتعثروا عند {Label(width * topHotIndex)} - {Label(width * (topHotIndex + 1))} (علامات وأسئلة وطلب شرح). فكّر تبسّط الجزء ده أو تعيده.");
            if (skipRate >= 40) verdicts.Add($"{skipRate:0}% من المشاهدات بتتعدى بالسحب بدل المشاهدة. الفيديو ممكن يكون طويل أو فيه حشو.");
            if (verdicts.Count == 0) verdicts.Add("المحاضرة ماشية كويس، مفيش مكان واضح بيخسر الطلبة.");
        }

        return new
        {
            lectureId = s.LectureId,
            name,
            durationSeconds = s.DurationSeconds,
            durationLabel = Label(s.DurationSeconds),
            viewers = s.Viewers,
            completed = s.Completed,
            completionRate,
            avgReachPercent = s.AvgReachPercent,
            avgWatchedPercent = s.AvgWatchedPercent,
            skipRate,
            retention = s.Retention.Select((v, i) => new { slice = i, fromSeconds = width * i, percent = v }).ToList(),
            stopPoints,
            hotspots,
            steepestDrop = steepest < 0 ? null : new { atSeconds = width * steepest, label = Label(width * steepest), dropPercent = Math.Round(steepestDrop, 1) },
            verdicts,
        };
    }

    // ───────────────────────── endpoints ─────────────────────────

    [HttpGet("{lectureId:int}")]
    public async Task<IActionResult> One(int lectureId)
    {
        var lecture = await _db.Lectures.AsNoTracking().Where(l => l.Id == lectureId)
            .Select(l => new { l.Id, l.Name }).FirstOrDefaultAsync();
        if (lecture == null) return NotFound(new { message = "Lecture not found." });

        var stats = await ComputeAsync(new List<int> { lectureId });
        return Ok(Describe(stats[lectureId], lecture.Name));
    }

    [HttpGet("overview")]
    public async Task<IActionResult> Overview([FromQuery] int? unitId, [FromQuery] int p = 1)
    {
        p = Math.Max(1, p);

        var query = _db.Lectures.AsNoTracking()
            .Where(l => l.AttendanceMethod == AttendanceMethod.Online && (l.StorageFileKey != null || l.YoutubeLink != null));
        if (unitId.HasValue) query = query.Where(l => l.UnitId == unitId.Value);

        var lectures = await query.OrderByDescending(l => l.CreatedAt).Take(300)
            .Select(l => new { l.Id, l.Name, l.UnitId }).ToListAsync();
        var stats = await ComputeAsync(lectures.Select(l => l.Id).ToList());

        var rows = lectures
            .Select(l =>
            {
                var s = stats[l.Id];
                var completionRate = s.Viewers == 0 ? (double?)null : Math.Round(100.0 * s.Completed / s.Viewers, 1);
                var worst = Enumerable.Range(0, Slices - 1)
                    .Select(i => new { Slice = i + 1, Drop = s.Retention[i] - s.Retention[i + 1] })
                    .OrderByDescending(x => x.Drop).First();
                var width = s.DurationSeconds <= 0 ? 0 : s.DurationSeconds / Slices;
                return new
                {
                    lectureId = l.Id,
                    name = l.Name,
                    unitId = l.UnitId,
                    viewers = s.Viewers,
                    completionRate,
                    avgReachPercent = s.Viewers == 0 ? (double?)null : s.AvgReachPercent,
                    worstDropAtSeconds = s.Viewers >= MinViewersForVerdict && worst.Drop >= 10 ? (int?)(width * worst.Slice) : null,
                    worstDropPercent = s.Viewers >= MinViewersForVerdict && worst.Drop >= 10 ? Math.Round(worst.Drop, 1) : (double?)null,
                    needsAttention = s.Viewers >= MinViewersForVerdict && (completionRate < 40 || worst.Drop >= 25),
                };
            })
            // Lectures with enough viewers first, the worst completion first.
            .OrderByDescending(r => r.viewers >= MinViewersForVerdict)
            .ThenBy(r => r.completionRate ?? 101)
            .Skip((p - 1) * PagingDefaults.PageSize).Take(PagingDefaults.PageSize)
            .ToList();

        return Ok(rows);
    }
}
