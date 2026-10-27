using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// "تحليل نقاط الضعف" -- where is the student weak, per Unit and per Lesson, and what
/// should they do about it.
///
/// Route: api/Insights  (student only)
///  GET Insights/weak-points?unitId=      accuracy per (unit, lesson) + prioritised recommendations
///
/// No new tables: everything is computed from the student's existing answers.
/// Sources that can be attributed to a lesson:
///   - lecture exams + lecture assignments  (lecture -> UnitId + LessonIndex)
///   - question-bank practice attempts      (bank question -> Lesson, or the whole Unit)
/// Sources that can only be attributed to a unit (lesson = null, "عام على الوحدة"):
///   - quizzes, and assignments that belong to exactly one unit.
///
/// A question counts as correct when the awarded mark reached the question's full mark
/// (this respects the teacher manually re-grading), or, when there is no awarded mark,
/// when the answer matches the model answer. An unanswered question counts as wrong.
///
/// CLASSIFICATION (needs at least 3 answered questions in the topic):
///   accuracy &lt; 50%  -> "weak",  50-74% -> "medium",  &gt;= 75% -> "strong".
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.Student)]
public class InsightsController : ControllerBase
{
    private const int MinSample = 3;

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public InsightsController(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    private sealed record Outcome(int UnitId, int? LessonIndex, bool Correct, DateTime At);
    private sealed record LecRef(int Id, string Name);

    private static bool Same(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a)
           && string.Equals(a.Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsCorrect(int mark, int? awarded, string? studentAnswer, string? correct)
    {
        if (awarded.HasValue && mark > 0) return awarded.Value >= mark;
        return Same(studentAnswer, correct);
    }

    [HttpGet("weak-points")]
    public async Task<IActionResult> WeakPoints([FromQuery] int? unitId = null)
    {
        var studentId = User.GetUserId();
        var unitIds = await StudentAccessHelpers.GetEffectiveUnitIdsAsync(_db, User, studentId);
        if (unitId.HasValue) unitIds = unitIds.Where(u => u == unitId.Value).ToHashSet();
        if (unitIds.Count == 0)
            return Ok(new { answered = 0, accuracy = (double?)null, units = Array.Empty<object>(), recommendations = Array.Empty<object>() });

        var outcomes = new List<Outcome>();

        // ───── lecture exams ─────
        var examResults = await _db.LectureExamResults.AsNoTracking()
            .Include(r => r.Answers)
            .Where(r => r.StudentId == studentId && r.Score > 0)
            .ToListAsync();
        var examIds = examResults.Select(r => r.LectureExamId).Distinct().ToList();
        var exams = await _db.LectureExams.AsNoTracking().Include(e => e.Questions)
            .Where(e => examIds.Contains(e.Id)).ToListAsync();

        // ───── lecture assignments ─────
        var lasgResults = await _db.LectureAssignmentResults.AsNoTracking()
            .Include(r => r.Answers)
            .Where(r => r.StudentId == studentId && r.Score > 0)
            .ToListAsync();
        var lasgIds = lasgResults.Select(r => r.LectureAssignmentId).Distinct().ToList();
        var lasgs = await _db.LectureAssignments.AsNoTracking().Include(e => e.Questions)
            .Where(e => lasgIds.Contains(e.Id)).ToListAsync();

        var lectureIds = exams.Select(e => e.LectureId).Concat(lasgs.Select(a => a.LectureId)).Distinct().ToList();
        var lectureMap = await _db.Lectures.AsNoTracking()
            .Where(l => lectureIds.Contains(l.Id) && l.UnitId != null)
            .Select(l => new { l.Id, UnitId = l.UnitId!.Value, l.LessonIndex })
            .ToDictionaryAsync(l => l.Id);

        foreach (var r in examResults)
        {
            var exam = exams.FirstOrDefault(e => e.Id == r.LectureExamId);
            if (exam == null || !lectureMap.TryGetValue(exam.LectureId, out var lec)) continue;
            foreach (var q in exam.Questions)
            {
                var a = r.Answers.FirstOrDefault(x => x.QuestionId == q.Id);
                outcomes.Add(new Outcome(lec.UnitId, lec.LessonIndex,
                    a != null && IsCorrect(q.Mark, a.MarkAwarded, a.Answer, q.Answer), r.GradedAt));
            }
        }

        foreach (var r in lasgResults)
        {
            var hw = lasgs.FirstOrDefault(e => e.Id == r.LectureAssignmentId);
            if (hw == null || !lectureMap.TryGetValue(hw.LectureId, out var lec)) continue;
            foreach (var q in hw.Questions)
            {
                var a = r.Answers.FirstOrDefault(x => x.QuestionId == q.Id);
                outcomes.Add(new Outcome(lec.UnitId, lec.LessonIndex,
                    a != null && IsCorrect(q.Mark, a.MarkAwarded, a.Answer, q.Answer), r.GradedAt));
            }
        }

        // ───── quizzes (unit level) ─────
        var quizResults = await _db.QuizResults.AsNoTracking()
            .Include(r => r.Answers)
            .Where(r => r.StudentId == studentId && !r.IsAutoSubmitted)
            .ToListAsync();
        var quizIds = quizResults.Select(r => r.QuizId).Distinct().ToList();
        var quizzes = await _db.Quizzes.AsNoTracking().Include(q => q.Questions)
            .Where(q => quizIds.Contains(q.Id)).ToListAsync();
        foreach (var r in quizResults)
        {
            var quiz = quizzes.FirstOrDefault(q => q.Id == r.QuizId);
            if (quiz == null) continue;
            foreach (var q in quiz.Questions)
            {
                var a = r.Answers.FirstOrDefault(x => x.QuestionId == q.Id);
                outcomes.Add(new Outcome(quiz.UnitId, null,
                    a != null && IsCorrect(q.Mark, a.MarkAwarded, a.Answer, q.Answer), r.GradedAt));
            }
        }

        // ───── assignments that belong to exactly one unit (unit level) ─────
        var submissions = await _db.AssignmentSubmissions.AsNoTracking()
            .Include(s => s.Answers)
            .Where(s => s.StudentId == studentId && s.Score > 0)
            .ToListAsync();
        var asgIds = submissions.Select(s => s.AssignmentId).Distinct().ToList();
        var assignments = await _db.Assignments.AsNoTracking().Include(a => a.Questions)
            .Where(a => asgIds.Contains(a.Id)).ToListAsync();
        foreach (var s in submissions)
        {
            var asg = assignments.FirstOrDefault(a => a.Id == s.AssignmentId);
            if (asg == null || asg.UnitIds.Count != 1) continue;
            foreach (var q in asg.Questions)
            {
                var a = s.Answers.FirstOrDefault(x => x.QuestionId == q.Id);
                outcomes.Add(new Outcome(asg.UnitIds[0], null,
                    a != null && IsCorrect(q.Mark, a.MarkAwarded, a.Answer, q.Answer), s.SubmittedAt));
            }
        }

        // ───── question-bank practice ─────
        var attempts = await _db.BankAttempts.AsNoTracking()
            .Include(a => a.Questions).ThenInclude(q => q.BankQuestion)
            .Where(a => a.StudentId == studentId && a.SubmittedAt != null)
            .ToListAsync();
        var bankLessonIds = attempts.SelectMany(a => a.Questions)
            .Where(q => q.BankQuestion?.LessonId != null)
            .Select(q => q.BankQuestion!.LessonId!.Value).Distinct().ToList();
        var bankLessons = await _db.Lessons.AsNoTracking()
            .Where(l => bankLessonIds.Contains(l.Id))
            .Select(l => new { l.Id, l.UnitId, l.LessonIndex })
            .ToDictionaryAsync(l => l.Id);
        foreach (var at in attempts)
        {
            foreach (var q in at.Questions)
            {
                var bq = q.BankQuestion;
                if (bq == null) continue;
                int? uId = null; int? lIdx = null;
                if (bq.LessonId.HasValue && bankLessons.TryGetValue(bq.LessonId.Value, out var les)) { uId = les.UnitId; lIdx = les.LessonIndex; }
                else if (bq.UnitId.HasValue) uId = bq.UnitId.Value;
                if (uId == null) continue;
                outcomes.Add(new Outcome(uId.Value, lIdx,
                    IsCorrect(bq.Mark, q.MarkAwarded, q.Answer, bq.Answer), at.SubmittedAt ?? at.StartedAt));
            }
        }

        outcomes = outcomes.Where(o => unitIds.Contains(o.UnitId)).ToList();
        if (outcomes.Count == 0)
            return Ok(new { answered = 0, accuracy = (double?)null, units = Array.Empty<object>(), recommendations = Array.Empty<object>() });

        // ───── names / supporting data ─────
        var usedUnitIds = outcomes.Select(o => o.UnitId).Distinct().ToList();
        var unitNames = await _db.Units.AsNoTracking()
            .Where(u => usedUnitIds.Contains(u.Id)).Select(u => new { u.Id, u.Name })
            .ToDictionaryAsync(u => u.Id, u => u.Name);
        var lessons = await _db.Lessons.AsNoTracking()
            .Where(l => usedUnitIds.Contains(l.UnitId))
            .Select(l => new { l.Id, l.UnitId, l.LessonIndex, l.Name })
            .ToListAsync();

        var gId = User.GetGroupId(_tenant.CurrentTenantId);
        var lectures = await _db.Lectures.AsNoTracking()
            .Where(l => l.UnitId != null && usedUnitIds.Contains(l.UnitId.Value) && l.LessonIndex != null
                        && (!gId.HasValue || _db.LectureGroupLinks.Any(x => x.LectureId == l.Id && x.GroupId == gId.Value)))
            .OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.Name, UnitId = l.UnitId!.Value, LessonIndex = l.LessonIndex!.Value })
            .ToListAsync();

        var lessonIdsForBank = lessons.Select(l => l.Id).ToList();
        var bankCountByLesson = await _db.BankQuestions.AsNoTracking()
            .Where(b => b.LessonId != null && lessonIdsForBank.Contains(b.LessonId.Value))
            .GroupBy(b => b.LessonId!.Value)
            .Select(g => new { LessonId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.LessonId, x => x.Count);
        var bankCountByUnit = await _db.BankQuestions.AsNoTracking()
            .Where(b => b.UnitId != null && usedUnitIds.Contains(b.UnitId.Value))
            .GroupBy(b => b.UnitId!.Value)
            .Select(g => new { UnitId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UnitId, x => x.Count);

        var openMistakesByUnit = await _db.ReviewCards.AsNoTracking()
            .Where(c => c.StudentId == studentId && c.CourseKind == "Unit" && !c.IsArchived && c.MasteredAt == null)
            .GroupBy(c => c.CourseId)
            .Select(g => new { UnitId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UnitId, x => x.Count);

        // ───── aggregate per topic ─────
        string Level(int total, double acc) => total < MinSample ? "unknown" : acc < 0.5 ? "weak" : acc < 0.75 ? "medium" : "strong";

        var topics = outcomes
            .GroupBy(o => new { o.UnitId, o.LessonIndex })
            .Select(g =>
            {
                var ordered = g.OrderBy(o => o.At).ToList();
                var total = ordered.Count;
                var correct = ordered.Count(o => o.Correct);
                var acc = total == 0 ? 0 : (double)correct / total;

                string trend = "flat";
                if (total >= 6)
                {
                    var recent = ordered.Skip(Math.Max(0, total - 5)).Count(o => o.Correct) / 5.0;
                    var older = ordered.Take(total - 5).Count(o => o.Correct) / (double)(total - 5);
                    trend = recent - older > 0.1 ? "up" : older - recent > 0.1 ? "down" : "flat";
                }

                var lesson = g.Key.LessonIndex.HasValue
                    ? lessons.FirstOrDefault(l => l.UnitId == g.Key.UnitId && l.LessonIndex == g.Key.LessonIndex.Value)
                    : null;

                var bankCount = lesson != null
                    ? bankCountByLesson.GetValueOrDefault(lesson.Id)
                    : bankCountByUnit.GetValueOrDefault(g.Key.UnitId);

                return new
                {
                    unitId = g.Key.UnitId,
                    unitName = unitNames.GetValueOrDefault(g.Key.UnitId, ""),
                    lessonIndex = g.Key.LessonIndex,
                    lessonId = lesson?.Id,
                    lessonName = g.Key.LessonIndex.HasValue ? (lesson?.Name ?? $"الدرس {g.Key.LessonIndex}") : "عام على الوحدة",
                    total,
                    correct,
                    accuracy = Math.Round(acc * 100, 1),
                    level = Level(total, acc),
                    trend,
                    bankQuestions = bankCount,
                    lectures = g.Key.LessonIndex.HasValue
                        ? lectures.Where(l => l.UnitId == g.Key.UnitId && l.LessonIndex == g.Key.LessonIndex.Value)
                            .Take(3).Select(l => new LecRef(l.Id, l.Name)).ToList()
                        : new List<LecRef>(),
                };
            })
            .ToList();

        var units = topics
            .GroupBy(t => t.unitId)
            .Select(g =>
            {
                var total = g.Sum(t => t.total);
                var correct = g.Sum(t => t.correct);
                return new
                {
                    unitId = g.Key,
                    name = g.First().unitName,
                    answered = total,
                    accuracy = total == 0 ? 0 : Math.Round(100.0 * correct / total, 1),
                    openMistakes = openMistakesByUnit.GetValueOrDefault(g.Key),
                    topics = g.OrderBy(t => t.level == "weak" ? 0 : t.level == "medium" ? 1 : t.level == "unknown" ? 2 : 3)
                              .ThenBy(t => t.accuracy).ToList(),
                };
            })
            .OrderBy(u => u.accuracy)
            .ToList();

        // ───── recommendations (most urgent first) ─────
        var recs = new List<object>();
        var weakTopics = topics
            .Where(t => t.level == "weak" || t.level == "medium")
            .OrderByDescending(t => (1 - t.accuracy / 100.0) * Math.Sqrt(t.total) * (t.level == "weak" ? 1.5 : 1.0))
            .Take(4)
            .ToList();

        var mistakesAdvised = new HashSet<int>();
        foreach (var t in weakTopics)
        {
            var pct = $"{t.accuracy:0}%";
            if (t.lectures.Count > 0)
            {
                var first = t.lectures[0];
                recs.Add(new
                {
                    type = "watch_lecture",
                    title = $"راجع شرح: {first.Name}",
                    subtitle = $"{t.unitName} — {t.lessonName} · نسبتك {pct} ({t.correct}/{t.total})",
                    unitId = t.unitId, lessonIndex = t.lessonIndex, lectureId = (int?)first.Id,
                });
            }
            if (t.bankQuestions >= 5)
            {
                recs.Add(new
                {
                    type = "practice_bank",
                    title = $"اتدرب على {t.lessonName}",
                    subtitle = $"{t.bankQuestions} سؤال في بنك الأسئلة · {t.unitName}",
                    unitId = t.unitId, lessonIndex = t.lessonIndex, lectureId = (int?)null,
                });
            }
            if (openMistakesByUnit.GetValueOrDefault(t.unitId) > 0 && mistakesAdvised.Add(t.unitId))
            {
                recs.Add(new
                {
                    type = "review_mistakes",
                    title = $"راجع أخطائك في {t.unitName}",
                    subtitle = $"{openMistakesByUnit[t.unitId]} سؤال لسه محتاج تثبّته",
                    unitId = t.unitId, lessonIndex = (int?)null, lectureId = (int?)null,
                });
            }
        }

        var answered = outcomes.Count;
        var okCount = outcomes.Count(o => o.Correct);
        return Ok(new
        {
            answered,
            accuracy = Math.Round(100.0 * okCount / answered, 1),
            weakCount = topics.Count(t => t.level == "weak"),
            strongCount = topics.Count(t => t.level == "strong"),
            units,
            recommendations = recs.Take(8),
        });
    }
}
