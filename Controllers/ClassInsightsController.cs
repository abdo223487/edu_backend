using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// "خريطة الفهم لكل فصل": for a whole class, which parts of the course do students understand and
/// which do they not -- per course and lesson, with the hardest questions and what to do about it.
/// Computed from the answers students already gave; no new table.
///
/// Route: api/ClassInsights  (teacher / assistants)
///  GET ClassInsights/understanding-map?groupId=&amp;unitId=&amp;days=120
///        topics (course + lesson) weakest first: accuracy, how many students are weak/medium/strong,
///        hardest questions, lectures to re-explain, and a plain-Arabic suggestion
///  GET ClassInsights/topic-students?unitId=&amp;lessonIndex=&amp;groupId=&amp;days=120
///        the students behind one topic, weakest first
///
/// Sources that can be pinned to a lesson: lecture exams, lecture homework (lecture -> lesson),
/// question-bank practice (question -> lesson). Quizzes and one-course homework count for the course
/// as a whole (lessonIndex = null, shown as "عام على الوحدة"). A question is correct when its awarded
/// mark reached the full mark, otherwise when the answer equals the model answer.
/// A student counts as weak in a topic below 50% (needs 3+ answers there), strong at 75%+.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.Assistant}")]
public class ClassInsightsController : ControllerBase
{
    private const int MinStudentSample = 3;
    private const int MinQuestionSample = 5;
    private const int MaxResultsPerSource = 4000;

    private readonly AppDbContext _db;

    public ClassInsightsController(AppDbContext db) => _db = db;

    private sealed record Outcome(
        int StudentId, int UnitId, int? LessonIndex, bool Correct,
        string Source, int QuestionId, string QuestionText);

    private static bool Same(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a)
           && string.Equals(a.Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsCorrect(int mark, int? awarded, string? studentAnswer, string? correct)
        => awarded.HasValue && mark > 0 ? awarded.Value >= mark : Same(studentAnswer, correct);

    private static string Short(string? s) => string.IsNullOrEmpty(s) ? "" : s.Length > 120 ? s[..120] + "…" : s;

    // ───────────────────────── data ─────────────────────────

    private async Task<List<Outcome>> CollectAsync(int? groupId, int? unitFilter, int days)
    {
        days = Math.Clamp(days, 7, 365);
        var since = DateTime.UtcNow.AddDays(-days);

        HashSet<int>? students = null;
        if (groupId.HasValue)
            students = (await _db.StudentGroupMemberships.AsNoTracking()
                    .Where(m => m.GroupId == groupId.Value && !m.IsSuspended && !m.IsCancelled)
                    .Select(m => m.StudentId).ToListAsync()).ToHashSet();

        bool Keep(int studentId, int unitId)
            => (students == null || students.Contains(studentId)) && (!unitFilter.HasValue || unitId == unitFilter.Value);

        var outcomes = new List<Outcome>();

        // ───── lecture exams ─────
        var examResults = await _db.LectureExamResults.AsNoTracking().Include(r => r.Answers)
            .Where(r => r.Score > 0 && r.GradedAt >= since)
            .OrderByDescending(r => r.GradedAt).Take(MaxResultsPerSource).ToListAsync();
        var examIds = examResults.Select(r => r.LectureExamId).Distinct().ToList();
        var exams = await _db.LectureExams.AsNoTracking().Include(e => e.Questions)
            .Where(e => examIds.Contains(e.Id)).ToListAsync();

        // ───── lecture homework ─────
        var lasgResults = await _db.LectureAssignmentResults.AsNoTracking().Include(r => r.Answers)
            .Where(r => r.Score > 0 && r.GradedAt >= since)
            .OrderByDescending(r => r.GradedAt).Take(MaxResultsPerSource).ToListAsync();
        var lasgIds = lasgResults.Select(r => r.LectureAssignmentId).Distinct().ToList();
        var lasgs = await _db.LectureAssignments.AsNoTracking().Include(a => a.Questions)
            .Where(a => lasgIds.Contains(a.Id)).ToListAsync();

        var lectureIds = exams.Select(e => e.LectureId).Concat(lasgs.Select(a => a.LectureId)).Distinct().ToList();
        var lectureMap = await _db.Lectures.AsNoTracking()
            .Where(l => lectureIds.Contains(l.Id) && l.UnitId != null)
            .Select(l => new { l.Id, UnitId = l.UnitId!.Value, l.LessonIndex })
            .ToDictionaryAsync(l => l.Id);

        foreach (var r in examResults)
        {
            var exam = exams.FirstOrDefault(e => e.Id == r.LectureExamId);
            if (exam == null || !lectureMap.TryGetValue(exam.LectureId, out var lec) || !Keep(r.StudentId, lec.UnitId)) continue;
            foreach (var q in exam.Questions)
            {
                var a = r.Answers.FirstOrDefault(x => x.QuestionId == q.Id);
                outcomes.Add(new Outcome(r.StudentId, lec.UnitId, lec.LessonIndex,
                    a != null && IsCorrect(q.Mark, a.MarkAwarded, a.Answer, q.Answer), "LectureExam", q.Id, Short(q.Text)));
            }
        }

        foreach (var r in lasgResults)
        {
            var hw = lasgs.FirstOrDefault(a => a.Id == r.LectureAssignmentId);
            if (hw == null || !lectureMap.TryGetValue(hw.LectureId, out var lec) || !Keep(r.StudentId, lec.UnitId)) continue;
            foreach (var q in hw.Questions)
            {
                var a = r.Answers.FirstOrDefault(x => x.QuestionId == q.Id);
                outcomes.Add(new Outcome(r.StudentId, lec.UnitId, lec.LessonIndex,
                    a != null && IsCorrect(q.Mark, a.MarkAwarded, a.Answer, q.Answer), "LectureAssignment", q.Id, Short(q.Text)));
            }
        }

        // ───── quizzes (course level) ─────
        var quizResults = await _db.QuizResults.AsNoTracking().Include(r => r.Answers)
            .Where(r => !r.IsAutoSubmitted && r.GradedAt >= since)
            .OrderByDescending(r => r.GradedAt).Take(MaxResultsPerSource).ToListAsync();
        var quizIds = quizResults.Select(r => r.QuizId).Distinct().ToList();
        var quizzes = await _db.Quizzes.AsNoTracking().Include(q => q.Questions)
            .Where(q => quizIds.Contains(q.Id)).ToListAsync();
        foreach (var r in quizResults)
        {
            var quiz = quizzes.FirstOrDefault(q => q.Id == r.QuizId);
            if (quiz == null || !Keep(r.StudentId, quiz.UnitId)) continue;
            foreach (var q in quiz.Questions)
            {
                var a = r.Answers.FirstOrDefault(x => x.QuestionId == q.Id);
                outcomes.Add(new Outcome(r.StudentId, quiz.UnitId, null,
                    a != null && IsCorrect(q.Mark, a.MarkAwarded, a.Answer, q.Answer), "Quiz", q.Id, Short(q.Text)));
            }
        }

        // ───── homework that belongs to exactly one course (course level) ─────
        var submissions = await _db.AssignmentSubmissions.AsNoTracking().Include(s => s.Answers)
            .Where(s => s.Score > 0 && s.SubmittedAt >= since)
            .OrderByDescending(s => s.SubmittedAt).Take(MaxResultsPerSource).ToListAsync();
        var asgIds = submissions.Select(s => s.AssignmentId).Distinct().ToList();
        var assignments = await _db.Assignments.AsNoTracking().Include(a => a.Questions)
            .Where(a => asgIds.Contains(a.Id)).ToListAsync();
        foreach (var s in submissions)
        {
            var asg = assignments.FirstOrDefault(a => a.Id == s.AssignmentId);
            if (asg == null || asg.UnitIds.Count != 1 || !Keep(s.StudentId, asg.UnitIds[0])) continue;
            foreach (var q in asg.Questions)
            {
                var a = s.Answers.FirstOrDefault(x => x.QuestionId == q.Id);
                outcomes.Add(new Outcome(s.StudentId, asg.UnitIds[0], null,
                    a != null && IsCorrect(q.Mark, a.MarkAwarded, a.Answer, q.Answer), "Assignment", q.Id, Short(q.Text)));
            }
        }

        // ───── question-bank practice ─────
        var attempts = await _db.BankAttempts.AsNoTracking()
            .Include(a => a.Questions).ThenInclude(q => q.BankQuestion)
            .Where(a => a.SubmittedAt != null && a.SubmittedAt >= since)
            .OrderByDescending(a => a.SubmittedAt).Take(MaxResultsPerSource).ToListAsync();
        var bankLessonIds = attempts.SelectMany(a => a.Questions)
            .Where(q => q.BankQuestion?.LessonId != null)
            .Select(q => q.BankQuestion!.LessonId!.Value).Distinct().ToList();
        var bankLessons = await _db.Lessons.AsNoTracking()
            .Where(l => bankLessonIds.Contains(l.Id))
            .Select(l => new { l.Id, l.UnitId, l.LessonIndex })
            .ToDictionaryAsync(l => l.Id);
        foreach (var at in attempts)
            foreach (var q in at.Questions)
            {
                var bq = q.BankQuestion;
                if (bq == null) continue;
                int? uId = null; int? lIdx = null;
                if (bq.LessonId.HasValue && bankLessons.TryGetValue(bq.LessonId.Value, out var les)) { uId = les.UnitId; lIdx = les.LessonIndex; }
                else if (bq.UnitId.HasValue) uId = bq.UnitId.Value;
                if (uId == null || !Keep(at.StudentId, uId.Value)) continue;
                outcomes.Add(new Outcome(at.StudentId, uId.Value, lIdx,
                    IsCorrect(bq.Mark, q.MarkAwarded, q.Answer, bq.Answer), "Bank", bq.Id, Short(bq.Text)));
            }

        return outcomes;
    }

    private static string Level(int answered, double acc)
        => answered < MinStudentSample ? "unknown" : acc < 0.5 ? "weak" : acc < 0.75 ? "medium" : "strong";

    // ───────────────────────── understanding map ─────────────────────────

    [HttpGet("understanding-map")]
    public async Task<IActionResult> UnderstandingMap([FromQuery] int? groupId, [FromQuery] int? unitId, [FromQuery] int days = 120)
    {
        var outcomes = await CollectAsync(groupId, unitId, days);
        if (outcomes.Count == 0)
            return Ok(new { students = 0, answers = 0, accuracy = (double?)null, topics = Array.Empty<object>() });

        var usedUnitIds = outcomes.Select(o => o.UnitId).Distinct().ToList();
        var unitNames = await _db.Units.AsNoTracking().Where(u => usedUnitIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Name }).ToDictionaryAsync(u => u.Id, u => u.Name);
        var lessons = await _db.Lessons.AsNoTracking().Where(l => usedUnitIds.Contains(l.UnitId))
            .Select(l => new { l.UnitId, l.LessonIndex, l.Name }).ToListAsync();
        var lectures = await _db.Lectures.AsNoTracking()
            .Where(l => l.UnitId != null && usedUnitIds.Contains(l.UnitId.Value) && l.LessonIndex != null)
            .OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.Name, UnitId = l.UnitId!.Value, LessonIndex = l.LessonIndex!.Value })
            .ToListAsync();

        // Which of the hardest questions already have an explanation video linked.
        var linked = (await _db.QuestionExplanations.AsNoTracking()
                .Select(e => new { e.SourceType, e.QuestionId }).ToListAsync())
            .Select(e => (e.SourceType, e.QuestionId)).ToHashSet();

        var topics = outcomes
            .GroupBy(o => new { o.UnitId, o.LessonIndex })
            .Select(g =>
            {
                var perStudent = g.GroupBy(o => o.StudentId)
                    .Select(s => new { Answered = s.Count(), Acc = s.Count(x => x.Correct) / (double)s.Count() })
                    .ToList();
                var counted = perStudent.Where(s => s.Answered >= MinStudentSample).ToList();
                var weak = counted.Count(s => s.Acc < 0.5);
                var medium = counted.Count(s => s.Acc >= 0.5 && s.Acc < 0.75);
                var strong = counted.Count(s => s.Acc >= 0.75);
                var weakPct = counted.Count == 0 ? 0 : 100.0 * weak / counted.Count;

                var total = g.Count();
                var acc = (double)g.Count(o => o.Correct) / total;

                var hardest = g.GroupBy(o => new { o.Source, o.QuestionId })
                    .Where(q => q.Count() >= MinQuestionSample)
                    .Select(q => new
                    {
                        source = q.Key.Source,
                        questionId = q.Key.QuestionId,
                        text = q.First().QuestionText,
                        answered = q.Count(),
                        wrongPercent = Math.Round(100.0 * q.Count(x => !x.Correct) / q.Count(), 1),
                        hasExplanation = linked.Contains((q.Key.Source, q.Key.QuestionId)),
                    })
                    .Where(q => q.wrongPercent >= 40)
                    .OrderByDescending(q => q.wrongPercent).ThenByDescending(q => q.answered)
                    .Take(3).ToList();

                var lesson = g.Key.LessonIndex.HasValue
                    ? lessons.FirstOrDefault(l => l.UnitId == g.Key.UnitId && l.LessonIndex == g.Key.LessonIndex.Value)
                    : null;

                string suggestion = counted.Count < 3 ? "no_data"
                    : weakPct >= 40 ? "reexplain"
                    : weakPct >= 25 ? "review_in_class"
                    : "ok";
                string suggestionText = suggestion switch
                {
                    "reexplain" => $"{(int)Math.Round(weakPct)}% من الطلبة ضعاف في الجزء ده. يستاهل تعيد شرحه (حصة مراجعة أو فيديو قصير).",
                    "review_in_class" => $"{(int)Math.Round(weakPct)}% من الطلبة ضعاف هنا. راجع أصعب الأسئلة في الحصة.",
                    "ok" => "مستوى الفصل كويس في الجزء ده.",
                    _ => "لسه مفيش إجابات كفاية على الجزء ده.",
                };

                return new
                {
                    unitId = g.Key.UnitId,
                    unitName = unitNames.GetValueOrDefault(g.Key.UnitId, ""),
                    lessonIndex = g.Key.LessonIndex,
                    lessonName = g.Key.LessonIndex.HasValue ? (lesson?.Name ?? $"الدرس {g.Key.LessonIndex}") : "عام على الوحدة",
                    students = perStudent.Count,
                    answers = total,
                    accuracy = Math.Round(acc * 100, 1),
                    level = Level(total, acc),
                    distribution = new { weak, medium, strong },
                    weakPercent = Math.Round(weakPct, 1),
                    hardestQuestions = hardest,
                    lectures = g.Key.LessonIndex.HasValue
                        ? lectures.Where(l => l.UnitId == g.Key.UnitId && l.LessonIndex == g.Key.LessonIndex.Value)
                            .Take(3).Select(l => new { id = l.Id, name = l.Name }).ToList<object>()
                        : new List<object>(),
                    suggestion,
                    suggestionText,
                };
            })
            .OrderByDescending(t => t.suggestion == "reexplain" ? 2 : t.suggestion == "review_in_class" ? 1 : 0)
            .ThenBy(t => t.accuracy)
            .ToList();

        var totalAnswers = outcomes.Count;
        return Ok(new
        {
            students = outcomes.Select(o => o.StudentId).Distinct().Count(),
            answers = totalAnswers,
            accuracy = Math.Round(100.0 * outcomes.Count(o => o.Correct) / totalAnswers, 1),
            needReexplain = topics.Count(t => t.suggestion == "reexplain"),
            topics,
        });
    }

    // ───────────────────────── who is behind a topic ─────────────────────────

    [HttpGet("topic-students")]
    public async Task<IActionResult> TopicStudents([FromQuery] int unitId, [FromQuery] int? lessonIndex,
        [FromQuery] int? groupId, [FromQuery] int days = 120)
    {
        if (unitId <= 0) return BadRequest(new { message = "unitId is required." });

        var outcomes = (await CollectAsync(groupId, unitId, days))
            .Where(o => o.LessonIndex == lessonIndex).ToList();

        var rows = outcomes.GroupBy(o => o.StudentId)
            .Select(g => new { StudentId = g.Key, Answered = g.Count(), Correct = g.Count(x => x.Correct) })
            .OrderBy(r => (double)r.Correct / r.Answered).ThenByDescending(r => r.Answered)
            .Take(200).ToList();

        var ids = rows.Select(r => r.StudentId).ToList();
        var names = await _db.Students.IgnoreQueryFilters().AsNoTracking()
            .Where(s => ids.Contains(s.Id)).Select(s => new { s.Id, s.Name })
            .ToDictionaryAsync(s => s.Id, s => s.Name);

        return Ok(rows.Select(r =>
        {
            var acc = (double)r.Correct / r.Answered;
            return new
            {
                studentId = r.StudentId,
                name = names.GetValueOrDefault(r.StudentId, ""),
                answered = r.Answered,
                correct = r.Correct,
                accuracy = Math.Round(acc * 100, 1),
                level = Level(r.Answered, acc),
            };
        }));
    }
}
