using EduApi.Common;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// "ليه غلطت؟" -- explains WHY the student got each question wrong, using plain rules (no AI,
/// no cost). It sits on top of "أخطائي": the mistakes themselves come from the real
/// MistakesController actions, so the "solutions stay hidden until the deadline" rules are
/// respected exactly as there.
///
/// Route: api/WhyWrong  (student only)
///  GET WhyWrong/course?kind=Unit|OnlineLesson|ExternalBook&amp;id=   every mistake of one "أخطائي" tab + its reason
///  GET WhyWrong/summary                                          reasons across ALL the student's tabs (dashboard card)
///
/// REASONS (first rule that matches wins, strongest evidence first):
///  unwatched  the lecture that explains it (an explanation link, or the lecture the exam/homework
///             belongs to) has not been watched -- only for online lectures that have a video
///  trap       the student picked the answer that most OTHER students who got it wrong also picked
///             (needs 5+ wrong answers from the class, share &gt;= 40%)
///  repeat     the same question was already wrong before (in an earlier attempt, or its review card lapsed)
///  time       three or more questions left blank in the same source (ran out of time)
///  skipped    left this one blank
///  partial    a written answer that earned part of the mark
///  careless   the student scored 75%+ in that source, so this one is most likely a slip
///  gap        otherwise: a concept that needs review (the default for a low score)
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.Student)]
public class WhyWrongController : ControllerBase
{
    private const int MinClassWrongAnswers = 5;
    private const double TrapShare = 0.40;
    private const int MaxSourcesForTraps = 30;
    private const int MaxCoursesInSummary = 8;

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public WhyWrongController(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    private int StudentId => User.GetUserId();

    private sealed record ReasonText(string Label, string Tip);

    private static readonly Dictionary<string, ReasonText> Reasons = new()
    {
        ["unwatched"] = new("لسه ما شفتش شرح الجزء ده", "شوف الشرح الأول وبعدين حل السؤال تاني."),
        ["trap"] = new("إجابة فخ", "اقرأ كل اختيار لآخره واستبعد الغلط واحد واحد قبل ما تختار."),
        ["repeat"] = new("غلطة بتتكرر", "السؤال ده غلطت فيه قبل كده. خليه في مراجعة أخطائك وراجعه كل كام يوم لحد ما يثبت."),
        ["time"] = new("الوقت خلص عليك", "سيبت كذا سؤال فاضي. ابدأ بالأسهل وماتقعدش كتير على سؤال واحد."),
        ["skipped"] = new("سبت السؤال فاضي", "حاول تجاوب كل الأسئلة حتى لو بتخمين، وارجع للأصعب في الآخر."),
        ["partial"] = new("إجابة ناقصة", "جبت جزء من الدرجة. قارن إجابتك بالإجابة النموذجية وشوف إيه النقط اللي ناقصة."),
        ["careless"] = new("سهو", "مستواك في الاختبار ده كويس، فغالبًا ده سهو. راجع إجابتك قبل التسليم."),
        ["gap"] = new("مفهوم محتاج مراجعة", "راجع شرح الجزء ده واتدرب عليه من بنك الأسئلة."),
    };

    private static readonly string[] ReasonOrder =
        { "unwatched", "trap", "repeat", "time", "skipped", "partial", "careless", "gap" };

    private static string Norm(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();

    private static bool Same(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && Norm(a) == Norm(b);

    private static bool IsChoiceLike(string type)
        => type.Equals("MCQ", StringComparison.OrdinalIgnoreCase)
           || type.Equals("Bubble", StringComparison.OrdinalIgnoreCase)
           || type.Equals("TrueFalse", StringComparison.OrdinalIgnoreCase);

    private MistakesController NewMistakes()
        => new MistakesController(_db) { ControllerContext = ControllerContext };

    // ───────────────────────── endpoints ─────────────────────────

    [HttpGet("course")]
    public async Task<IActionResult> Course([FromQuery] string? kind, [FromQuery] int id)
    {
        if (string.IsNullOrWhiteSpace(kind) || id <= 0)
            return BadRequest(new { message = "kind and id are required." });

        var details = await LoadDetailsAsync(kind!, id);
        if (details == null) return NotFound(new { message = "Course not found." });

        var analysis = await AnalyzeAsync(details);
        return Ok(analysis.ToCourseResponse(details));
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary()
    {
        var mistakes = NewMistakes();
        if ((await mistakes.GetCourses()) is not OkObjectResult { Value: List<MistakeCourseDto> courses })
            return Ok(new { totalMistakes = 0, reasons = Array.Empty<object>(), coaching = (string?)null, courses = Array.Empty<object>() });

        var overall = new Dictionary<string, int>();
        var perCourse = new List<object>();
        var total = 0;

        foreach (var c in courses.Take(MaxCoursesInSummary))
        {
            var details = await LoadDetailsAsync(c.Kind, c.Id);
            if (details == null || details.TotalMistakes == 0) continue;

            var analysis = await AnalyzeAsync(details);
            foreach (var kv in analysis.Counts) overall[kv.Key] = overall.GetValueOrDefault(kv.Key) + kv.Value;
            total += analysis.Total;

            var top = analysis.Counts.OrderByDescending(x => x.Value).FirstOrDefault();
            perCourse.Add(new
            {
                kind = c.Kind,
                id = c.Id,
                name = c.Name,
                mistakes = analysis.Total,
                topReason = top.Key,
                topReasonLabel = top.Key == null ? null : Reasons[top.Key].Label,
            });
        }

        return Ok(new
        {
            totalMistakes = total,
            reasons = BuildSummary(overall, total),
            coaching = Coaching(overall, total),
            courses = perCourse,
        });
    }

    // ───────────────────────── loading ─────────────────────────

    private async Task<MistakesCourseDetailsDto?> LoadDetailsAsync(string kind, int id)
    {
        var mistakes = NewMistakes();
        IActionResult result = kind switch
        {
            "Unit" => await mistakes.GetCourseMistakes(id),
            "OnlineLesson" => await mistakes.GetOnlineLessonMistakes(id),
            "ExternalBook" => await mistakes.GetExternalBookMistakes(id),
            _ => new BadRequestResult(),
        };
        return result is OkObjectResult { Value: MistakesCourseDetailsDto d } ? d : null;
    }

    // ───────────────────────── analysis ─────────────────────────

    private sealed record Dist(int WrongTotal, Dictionary<string, int> ByAnswer);

    private sealed class MistakeVerdict
    {
        public string Reason { get; init; } = "gap";
        public int? TrapPercent { get; init; }
        public int? ExplanationId { get; init; }
        public int? ExplanationLectureId { get; init; }
        public string? ExplanationLectureName { get; init; }
        public int? ExplanationStart { get; init; }
        public bool ExplanationLocked { get; init; }
    }

    private sealed class Analysis
    {
        public Dictionary<(string Type, int SourceId, int QuestionId), MistakeVerdict> Verdicts { get; } = new();
        public Dictionary<string, int> Counts { get; } = new();
        public int Total { get; set; }

        public object ToCourseResponse(MistakesCourseDetailsDto d) => new
        {
            kind = d.Kind,
            id = d.Id,
            name = d.Name,
            totalMistakes = Total,
            hiddenSources = d.HiddenSources,
            reasons = BuildSummary(Counts, Total),
            coaching = Coaching(Counts, Total),
            sources = d.Sources.Select(s => new
            {
                sourceType = s.SourceType,
                sourceId = s.SourceId,
                title = s.Title,
                lectureName = s.LectureName,
                externalBookName = s.ExternalBookName,
                date = s.Date,
                score = s.Score,
                totalMarks = s.TotalMarks,
                percent = s.Score.HasValue && s.TotalMarks is > 0
                    ? Math.Round(100.0 * s.Score.Value / s.TotalMarks.Value, 1)
                    : (double?)null,
                mistakes = s.Mistakes.Select(m =>
                {
                    Verdicts.TryGetValue((s.SourceType, s.SourceId, m.QuestionId), out var v);
                    var reason = v?.Reason ?? "gap";
                    return new
                    {
                        questionId = m.QuestionId,
                        text = m.Text,
                        imageUrl = m.ImageUrl,
                        questionType = m.QuestionType,
                        choices = m.Choices,
                        studentAnswer = m.StudentAnswer,
                        correctAnswer = m.CorrectAnswer,
                        mark = m.Mark,
                        markAwarded = m.MarkAwarded,
                        reason,
                        reasonLabel = Reasons[reason].Label,
                        tip = v?.TrapPercent != null && reason == "trap"
                            ? $"{v.TrapPercent}% من زمايلك اللي غلطوا اختاروا نفس إجابتك. " + Reasons[reason].Tip
                            : Reasons[reason].Tip,
                        trapPercent = v?.TrapPercent,
                        explanation = v?.ExplanationId == null ? null : new
                        {
                            id = v.ExplanationId,
                            lectureId = v.ExplanationLectureId,
                            lectureName = v.ExplanationLectureName,
                            startSeconds = v.ExplanationStart,
                            locked = v.ExplanationLocked,
                        },
                    };
                }).ToList(),
            }).ToList(),
        };
    }

    private static List<object> BuildSummary(Dictionary<string, int> counts, int total)
        => ReasonOrder
            .Where(r => counts.GetValueOrDefault(r) > 0)
            .OrderByDescending(r => counts[r])
            .Select(r => (object)new
            {
                reason = r,
                label = Reasons[r].Label,
                count = counts[r],
                percent = total == 0 ? 0 : Math.Round(100.0 * counts[r] / total, 1),
                advice = Reasons[r].Tip,
            })
            .ToList();

    private static string? Coaching(Dictionary<string, int> counts, int total)
    {
        if (total == 0) return null;
        var top = counts.OrderByDescending(x => x.Value).First();
        var pct = (int)Math.Round(100.0 * top.Value / total);
        return top.Key switch
        {
            "unwatched" => $"{pct}% من أخطائك في أجزاء لسه ما شفتش شرحها. ابدأ بالمحاضرات الناقصة.",
            "trap" => $"{pct}% من أخطائك وقعت فيها في اختيارات مضللة. بطّئ شوية واستبعد الاختيارات الغلط.",
            "repeat" => $"{pct}% من أخطائك بتتكرر. التزم بمراجعة أخطائك كل يوم لحد ما تثبت.",
            "time" => $"{pct}% من أخطائك بسبب الوقت. جرّب تحل الأسهل الأول وتوزع وقتك.",
            "skipped" => $"{pct}% من أخطائك أسئلة سيبتها فاضية. جاوب كل الأسئلة قبل ما تسلّم.",
            "partial" => $"{pct}% من أخطائك إجابات ناقصة. اهتم بكتابة كل الخطوات والنقط المطلوبة.",
            "careless" => $"{pct}% من أخطائك سهو. راجع إجابتك قبل التسليم.",
            _ => $"{pct}% من أخطائك مفاهيم محتاجة مراجعة. راجع الشرح واتدرب من بنك الأسئلة.",
        };
    }

    private async Task<Analysis> AnalyzeAsync(MistakesCourseDetailsDto details)
    {
        var studentId = StudentId;
        var result = new Analysis();

        // Normalised source type for each source of the details (BankAttempt -> Bank).
        string ExplType(string sourceType) => QuestionExplanationsController.NormalizeSource(sourceType) ?? sourceType;

        // ── 1) how many times each question shows up as a mistake (repeat rule) ──
        var occurrences = new Dictionary<(string, int), int>();
        foreach (var s in details.Sources)
            foreach (var m in s.Mistakes)
            {
                var k = (ExplType(s.SourceType), m.QuestionId);
                occurrences[k] = occurrences.GetValueOrDefault(k) + 1;
            }

        // ── 2) explanation links (one query) ──
        var allQids = details.Sources.SelectMany(s => s.Mistakes.Select(m => m.QuestionId)).Distinct().ToList();
        var explRows = allQids.Count == 0
            ? new List<QuestionExplanation>()
            : await _db.QuestionExplanations.AsNoTracking().Where(e => allQids.Contains(e.QuestionId)).ToListAsync();
        var explByKey = explRows.GroupBy(e => (e.SourceType, e.QuestionId)).ToDictionary(g => g.Key, g => g.First());

        // ── 3) lectures: the ones behind exams / homework + the explanation lectures ──
        var examIds = details.Sources.Where(s => s.SourceType == "LectureExam").Select(s => s.SourceId).Distinct().ToList();
        var asgIds = details.Sources.Where(s => s.SourceType == "LectureAssignment").Select(s => s.SourceId).Distinct().ToList();

        var lectureOfExam = examIds.Count == 0 ? new Dictionary<int, int>()
            : await _db.LectureExams.AsNoTracking().Where(e => examIds.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => e.LectureId);
        var lectureOfAsg = asgIds.Count == 0 ? new Dictionary<int, int>()
            : await _db.LectureAssignments.AsNoTracking().Where(a => asgIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.LectureId);

        var lectureIds = explRows.Select(e => e.LectureId)
            .Concat(lectureOfExam.Values).Concat(lectureOfAsg.Values).Distinct().ToList();

        var lectureInfo = lectureIds.Count == 0
            ? new Dictionary<int, (string Name, bool OnlineWithVideo)>()
            : (await _db.Lectures.AsNoTracking().Where(l => lectureIds.Contains(l.Id))
                .Select(l => new
                {
                    l.Id, l.Name, l.AttendanceMethod,
                    HasVideo = l.StorageFileKey != null || l.YoutubeLink != null,
                })
                .ToListAsync())
                .ToDictionary(l => l.Id, l => (Name: l.Name, OnlineWithVideo: l.AttendanceMethod == AttendanceMethod.Online && l.HasVideo));

        var positions = lectureIds.Count == 0
            ? new Dictionary<int, (bool Completed, int Position)>()
            : (await _db.LecturePlaybackPositions.AsNoTracking()
                .Where(p => p.StudentId == studentId && lectureIds.Contains(p.LectureId))
                .Select(p => new { p.LectureId, p.Completed, p.PositionSeconds })
                .ToListAsync())
                .ToDictionary(p => p.LectureId, p => (Completed: p.Completed, Position: p.PositionSeconds));

        // Can the student open each explanation lecture? (one check per lecture)
        var accessCache = new Dictionary<int, bool>();
        async Task<bool> CanOpenAsync(int lectureId)
        {
            if (accessCache.TryGetValue(lectureId, out var ok)) return ok;
            ok = await LectureAccess.GetAsync(_db, User, _tenant, lectureId) != null;
            accessCache[lectureId] = ok;
            return ok;
        }

        // ── 4) review cards that already lapsed (repeat rule) ──
        var lapsed = allQids.Count == 0
            ? new HashSet<(string, int)>()
            : (await _db.ReviewCards.AsNoTracking()
                .Where(c => c.StudentId == studentId && c.Lapses > 0 && allQids.Contains(c.QuestionId))
                .Select(c => new { c.SourceType, c.QuestionId })
                .ToListAsync())
                .Select(c => (ExplType(c.SourceType), c.QuestionId))
                .ToHashSet();

        // ── 5) what the rest of the class answered (trap rule) ──
        var dists = await LoadDistributionsAsync(details);

        // ── 6) decide ──
        foreach (var s in details.Sources)
        {
            var type = ExplType(s.SourceType);
            var blanks = s.Mistakes.Count(m => string.IsNullOrWhiteSpace(m.StudentAnswer));
            double? pct = s.Score.HasValue && s.TotalMarks is > 0 ? 100.0 * s.Score.Value / s.TotalMarks.Value : null;

            int? sourceLecture = s.SourceType switch
            {
                "LectureExam" => lectureOfExam.TryGetValue(s.SourceId, out var le) ? le : null,
                "LectureAssignment" => lectureOfAsg.TryGetValue(s.SourceId, out var la) ? la : null,
                _ => null,
            };

            foreach (var m in s.Mistakes)
            {
                explByKey.TryGetValue((type, m.QuestionId), out var expl);

                string reason;
                int? trapPct = null;

                // unwatched: the explanation lecture (reaching the linked minute counts as watched),
                // otherwise the lecture the exam / homework belongs to.
                var unwatched = false;
                if (expl != null && lectureInfo.TryGetValue(expl.LectureId, out var el) && el.OnlineWithVideo)
                {
                    positions.TryGetValue(expl.LectureId, out var pos);
                    unwatched = !pos.Completed && pos.Position < expl.StartSeconds;
                }
                else if (sourceLecture.HasValue && lectureInfo.TryGetValue(sourceLecture.Value, out var sl) && sl.OnlineWithVideo)
                {
                    positions.TryGetValue(sourceLecture.Value, out var pos);
                    unwatched = !pos.Completed;
                }

                var key = (s.SourceType, s.SourceId, m.QuestionId);
                var blank = string.IsNullOrWhiteSpace(m.StudentAnswer);

                if (unwatched) reason = "unwatched";
                else if (!blank && IsChoiceLike(m.QuestionType) && IsTrap(dists, details, s, m, out trapPct)) reason = "trap";
                else if (occurrences.GetValueOrDefault((type, m.QuestionId)) >= 2 || lapsed.Contains((type, m.QuestionId))) reason = "repeat";
                else if (blank && blanks >= 3) reason = "time";
                else if (blank) reason = "skipped";
                else if (m.MarkAwarded is > 0 && m.MarkAwarded < m.Mark) reason = "partial";
                else if (pct is >= 75) reason = "careless";
                else reason = "gap";

                var verdict = new MistakeVerdict
                {
                    Reason = reason,
                    TrapPercent = reason == "trap" ? trapPct : null,
                    ExplanationId = expl?.Id,
                    ExplanationLectureId = expl?.LectureId,
                    ExplanationLectureName = expl != null && lectureInfo.TryGetValue(expl.LectureId, out var li) ? li.Name : null,
                    ExplanationStart = expl?.StartSeconds,
                    ExplanationLocked = expl != null && !await CanOpenAsync(expl.LectureId),
                };
                result.Verdicts[key] = verdict;
                result.Counts[reason] = result.Counts.GetValueOrDefault(reason) + 1;
                result.Total++;
            }
        }

        return result;
    }

    // ───────────────────────── class answers (trap rule) ─────────────────────────

    private sealed record AnswerRow(int QuestionId, string? Answer, int? MarkAwarded);

    private static string DistKey(string sourceType, int sourceId, int questionId)
        => sourceType == "BankAttempt" ? $"Bank:0:{questionId}" : $"{sourceType}:{sourceId}:{questionId}";

    private async Task<Dictionary<string, Dist>> LoadDistributionsAsync(MistakesCourseDetailsDto details)
    {
        var dists = new Dictionary<string, Dist>();

        // The correct answer / mark of every question we may need to judge "wrong" for.
        var meta = new Dictionary<string, (string? Correct, int Mark)>();
        foreach (var s in details.Sources)
            foreach (var m in s.Mistakes)
                meta[DistKey(s.SourceType, s.SourceId, m.QuestionId)] = (m.CorrectAnswer, m.Mark);

        void Accumulate(string sourceType, int sourceId, IEnumerable<AnswerRow> rows)
        {
            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(r.Answer)) continue;
                var key = DistKey(sourceType, sourceId, r.QuestionId);
                if (!meta.TryGetValue(key, out var info)) continue;

                var wrong = r.MarkAwarded.HasValue && info.Mark > 0
                    ? r.MarkAwarded.Value < info.Mark
                    : !Same(r.Answer, info.Correct);
                if (!wrong) continue;

                if (!dists.TryGetValue(key, out var d))
                {
                    d = new Dist(0, new Dictionary<string, int>());
                    dists[key] = d;
                }
                var a = Norm(r.Answer);
                d.ByAnswer[a] = d.ByAnswer.GetValueOrDefault(a) + 1;
                dists[key] = d with { WrongTotal = d.WrongTotal + 1 };
            }
        }

        // One query per distinct source (most recent first, capped), one for every bank question.
        var sources = details.Sources
            .Where(s => s.SourceType != "BankAttempt" && s.SourceType != "AssignmentCenter"
                        && s.Mistakes.Any(m => IsChoiceLike(m.QuestionType) && !string.IsNullOrWhiteSpace(m.StudentAnswer)))
            .Take(MaxSourcesForTraps)
            .ToList();

        foreach (var s in sources)
        {
            var qids = s.Mistakes.Select(m => m.QuestionId).ToList();
            var sid = s.SourceId;
            List<AnswerRow> rows;
            switch (s.SourceType)
            {
                case "Quiz":
                    rows = await _db.QuizAnswers.AsNoTracking()
                        .Where(a => a.QuizResult != null && a.QuizResult.QuizId == sid && qids.Contains(a.QuestionId))
                        .Select(a => new AnswerRow(a.QuestionId, a.Answer, a.MarkAwarded)).Take(5000).ToListAsync();
                    break;
                case "LectureExam":
                    rows = await _db.LectureExamAnswers.AsNoTracking()
                        .Where(a => a.LectureExamResult != null && a.LectureExamResult.LectureExamId == sid && qids.Contains(a.QuestionId))
                        .Select(a => new AnswerRow(a.QuestionId, a.Answer, a.MarkAwarded)).Take(5000).ToListAsync();
                    break;
                case "LectureAssignment":
                    rows = await _db.LectureAssignmentAnswers.AsNoTracking()
                        .Where(a => a.LectureAssignmentResult != null && a.LectureAssignmentResult.LectureAssignmentId == sid && qids.Contains(a.QuestionId))
                        .Select(a => new AnswerRow(a.QuestionId, a.Answer, a.MarkAwarded)).Take(5000).ToListAsync();
                    break;
                case "Assignment":
                    rows = await _db.AssignmentAnswers.AsNoTracking()
                        .Where(a => a.AssignmentSubmission != null && a.AssignmentSubmission.AssignmentId == sid && qids.Contains(a.QuestionId))
                        .Select(a => new AnswerRow(a.QuestionId, a.Answer, a.MarkAwarded)).Take(5000).ToListAsync();
                    break;
                default:
                    continue;
            }
            Accumulate(s.SourceType, sid, rows);
        }

        var bankQids = details.Sources.Where(s => s.SourceType == "BankAttempt")
            .SelectMany(s => s.Mistakes.Where(m => IsChoiceLike(m.QuestionType) && !string.IsNullOrWhiteSpace(m.StudentAnswer)).Select(m => m.QuestionId))
            .Distinct().ToList();
        if (bankQids.Count > 0)
        {
            var rows = await _db.BankAttemptQuestions.AsNoTracking()
                .Where(aq => bankQids.Contains(aq.BankQuestionId) && aq.Attempt != null && aq.Attempt.SubmittedAt != null)
                .Select(aq => new AnswerRow(aq.BankQuestionId, aq.Answer, aq.MarkAwarded))
                .Take(8000).ToListAsync();
            Accumulate("BankAttempt", 0, rows);
        }

        return dists;
    }

    private static bool IsTrap(Dictionary<string, Dist> dists, MistakesCourseDetailsDto details,
        MistakeSourceDto s, MistakeQuestionDto m, out int? percent)
    {
        percent = null;
        if (!dists.TryGetValue(DistKey(s.SourceType, s.SourceId, m.QuestionId), out var d)) return false;
        if (d.WrongTotal < MinClassWrongAnswers) return false;

        var mine = Norm(m.StudentAnswer);
        if (!d.ByAnswer.TryGetValue(mine, out var count)) return false;

        // The student's wrong answer must be THE most popular wrong answer, and a big share of them.
        var share = (double)count / d.WrongTotal;
        if (share < TrapShare || count < d.ByAnswer.Values.Max()) return false;

        percent = (int)Math.Round(share * 100);
        return true;
    }
}
