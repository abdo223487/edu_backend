using EduApi.Common;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/Mistakes  ("أخطائي" في بروفايل الطالب)
///
///  GET Mistakes/courses                (student) التابس: الكورسات + الـ Online Lessons + الكتب الخارجية
///  GET Mistakes/course/{unitId}        (student) أسئلة كورس
///  GET Mistakes/online-lesson/{id}     (student) أسئلة Online Lesson (امتحانات وواجبات محاضراته)
///  GET Mistakes/external-book/{id}     (student) أسئلة كتاب خارجي (امتحانات وواجبات محاضراته)
///
/// الكتاب الخارجي المربوط بكورس الطالب مشترك فيه بيظهر جوه تاب الكورس نفسه، وبياخد
/// تاب لوحده بس لو مش مربوط بكورس (أو مربوط بكورس الطالب مش مشترك فيه).
///
/// مفيش جدول جديد ولا migration: كل حاجة بتتحسب لحظيًا من نتايج الطالب الموجودة
/// (كلها معزولة بالـ tenant عن طريق الـ global query filters في AppDbContext).
///
/// المصادر اللي بتتجمع لكل كورس (Unit):
///  1. الكويزات (Quiz)                            -> UnitId
///  2. امتحانات المحاضرات (LectureExam)           -> المحاضرة في الكورس
///  3. واجبات المحاضرات (LectureAssignment)       -> المحاضرة في الكورس
///  4. الواجبات (Assignment)                      -> AssignmentUnitLink
///  5. واجبات السنتر (AssignmentCenter)           -> AssignmentCenterUnitLink
///  6. محاضرات الكتب الخارجية المربوطة بالكورس    -> ExternalBook.UnitId
///     (امتحاناتها وواجباتها بتدخل في 2 و 3)
///  7. محاولات بنك الأسئلة (BankAttempt)          -> سؤال البنك نفسه تابع للكورس
///
/// قواعد مهمة:
///  - السؤال "غلط" لو الدرجة اللي خدها أقل من درجته الكاملة (بتحترم تعديل
///    المدرس للدرجة)، أو لو الطالب سابه من غير إجابة.
///  - الكويز/الواجب/واجب السنتر: الحل مبيظهرش إلا بعد ما الديدلاين يخلص (نفس
///    قاعدة صفحات الامتحان والواجب)، فالأسئلة دي بتتخبّى قبل كده ويتعد
///    HiddenSources. وكمان لو المدرس فاتح للطالب "إعادة فتح" شغالة دلوقتي.
///  - النتايج التلقائية بصفر (الطالب ماحلّش أصلاً / المدرس فتحله كمراجعة) مش
///    بتتحسب أخطاء -- هي مش محاولة حقيقية.
///  - محاضرات الـ Online Lessons ومحاضرات الكتب الخارجية اللي مش مربوطة
///    بكورس مش بتظهر هنا لأنها مش تابعة لأي كورس.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.Student)]
public class MistakesController : ControllerBase
{
    private readonly AppDbContext _db;

    public MistakesController(AppDbContext db)
    {
        _db = db;
    }

    // ───────────── أدوات مساعدة ─────────────

    private sealed record QInfo(
        int Id,
        string Text,
        string? ImageUrl,
        string Type,
        List<string> Choices,
        string Correct,
        int Mark);

    private static bool SameAnswer(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a)
           && string.Equals(a.Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// غلط لو الدرجة الممنوحة أقل من درجة السؤال. لو مفيش درجة ممنوحة (أو السؤال
    /// درجته 0) بنقارن الإجابة بالصح. سؤال من غير إجابة = غلط.
    /// </summary>
    private static bool IsWrong(int mark, int? awarded, string? studentAnswer, string correct)
    {
        if (awarded.HasValue && mark > 0) return awarded.Value < mark;
        return !SameAnswer(studentAnswer, correct);
    }

    private static List<MistakeQuestionDto> CollectMistakes(
        IEnumerable<QInfo> questions,
        IDictionary<int, (string? Answer, int? Awarded)> answers)
    {
        var list = new List<MistakeQuestionDto>();
        foreach (var q in questions.OrderBy(x => x.Id))
        {
            answers.TryGetValue(q.Id, out var a);
            if (!IsWrong(q.Mark, a.Awarded, a.Answer, q.Correct)) continue;

            var studentAnswer = string.IsNullOrWhiteSpace(a.Answer) ? null : a.Answer;
            list.Add(new MistakeQuestionDto(
                q.Id, q.Text, q.ImageUrl, q.Type, q.Choices,
                studentAnswer, q.Correct, q.Mark, a.Awarded));
        }
        return list;
    }

    private static Dictionary<int, (string? Answer, int? Awarded)> ToAnswerMap<T>(
        IEnumerable<T> rows, Func<T, int> qid, Func<T, string?> ans, Func<T, int?> awarded)
    {
        var map = new Dictionary<int, (string? Answer, int? Awarded)>();
        foreach (var r in rows) map[qid(r)] = (ans(r), awarded(r));
        return map;
    }

    /// <summary>
    /// نتيجة "اتعملت أوتوماتيك" (فتح كمراجعة من المدرس) = كل إجاباتها فاضية.
    /// بتُستخدم في امتحانات/واجبات المحاضرات اللي مفيهاش علامة IsAutoSubmitted.
    /// </summary>
    private static bool AllBlank(IEnumerable<string?> answers)
    {
        var any = false;
        foreach (var a in answers)
        {
            any = true;
            if (!string.IsNullOrWhiteSpace(a)) return false;
        }
        return any;
    }

    // ───────────── GET Mistakes/courses ─────────────

    [HttpGet("courses")]
    public async Task<IActionResult> GetCourses()
    {
        var studentId = User.GetUserId();
        var effectiveUnitIds = await StudentAccessHelpers.GetEffectiveUnitIdsAsync(_db, User, studentId);
        var result = new List<MistakeCourseDto>();

        // Units عليها tenant filter، فأي Unit تبع مدرس تاني بتتشال لوحدها.
        var units = await _db.Units.AsNoTracking()
            .Where(u => effectiveUnitIds.Contains(u.Id))
            .OrderByDescending(u => u.Id)
            .Select(u => new { u.Id, u.Name, u.SchoolYear, u.Month, u.ImageUrl })
            .ToListAsync();
        result.AddRange(units.Select(u =>
            new MistakeCourseDto("Unit", u.Id, u.Name, u.SchoolYear, u.Month, u.ImageUrl)));

        // Online Lessons اللي الطالب فتحها بكود.
        var onlineIds = await _db.StudentOnlineLessonUnlocks.AsNoTracking()
            .Where(x => x.StudentId == studentId)
            .Select(x => x.OnlineLessonId)
            .Distinct()
            .ToListAsync();
        var onlineLessons = await _db.OnlineLessons.AsNoTracking()
            .Where(o => onlineIds.Contains(o.Id))
            .OrderByDescending(o => o.Id)
            .Select(o => new { o.Id, o.Name, o.SchoolYear, o.ImageUrl })
            .ToListAsync();
        result.AddRange(onlineLessons.Select(o =>
            new MistakeCourseDto("OnlineLesson", o.Id, o.Name, o.SchoolYear, null, o.ImageUrl)));

        // الكتب الخارجية اللي الطالب يقدر يوصلها، ما عدا اللي مربوطة بكورس هو
        // مشترك فيه (دي بتظهر جوه تاب الكورس).
        var bookIds = await StudentAccessHelpers.GetEffectiveExternalBookIdsAsync(_db, User, studentId);
        var books = await _db.ExternalBooks.AsNoTracking()
            .Where(b => bookIds.Contains(b.Id) &&
                        (b.UnitId == null || !effectiveUnitIds.Contains(b.UnitId.Value)))
            .OrderByDescending(b => b.Id)
            .Select(b => new { b.Id, b.Name, b.SchoolYear, b.Month, b.ImageUrl })
            .ToListAsync();
        result.AddRange(books.Select(b =>
            new MistakeCourseDto("ExternalBook", b.Id, b.Name, b.SchoolYear, b.Month, b.ImageUrl)));

        return Ok(result);
    }

    // ───────────── GET Mistakes/online-lesson/{id} ─────────────

    [HttpGet("online-lesson/{id:int}")]
    public async Task<IActionResult> GetOnlineLessonMistakes(int id)
    {
        var studentId = User.GetUserId();
        var unlocked = await _db.StudentOnlineLessonUnlocks.AsNoTracking()
            .AnyAsync(x => x.StudentId == studentId && x.OnlineLessonId == id);
        if (!unlocked)
            return StatusCode(403, new { message = "Not unlocked for this online lesson." });

        var ol = await _db.OnlineLessons.AsNoTracking()
            .Where(o => o.Id == id).Select(o => new { o.Id, o.Name }).FirstOrDefaultAsync();
        if (ol == null) return NotFound(new { message = "Online lesson not found." });

        return Ok(await BuildDetailsAsync(studentId, "OnlineLesson", ol.Id, ol.Name, null, ol.Id, null));
    }

    // ───────────── GET Mistakes/external-book/{id} ─────────────

    [HttpGet("external-book/{id:int}")]
    public async Task<IActionResult> GetExternalBookMistakes(int id)
    {
        var studentId = User.GetUserId();
        var bookIds = await StudentAccessHelpers.GetEffectiveExternalBookIdsAsync(_db, User, studentId);
        if (!bookIds.Contains(id))
            return StatusCode(403, new { message = "Not subscribed to this external book." });

        var book = await _db.ExternalBooks.AsNoTracking()
            .Where(b => b.Id == id).Select(b => new { b.Id, b.Name }).FirstOrDefaultAsync();
        if (book == null) return NotFound(new { message = "External book not found." });

        return Ok(await BuildDetailsAsync(studentId, "ExternalBook", book.Id, book.Name, null, null, book.Id));
    }

    // ───────────── GET Mistakes/course/{unitId} ─────────────

    [HttpGet("course/{unitId:int}")]
    public async Task<IActionResult> GetCourseMistakes(int unitId)
    {
        var studentId = User.GetUserId();
        var effectiveUnitIds = await StudentAccessHelpers.GetEffectiveUnitIdsAsync(_db, User, studentId);
        if (!effectiveUnitIds.Contains(unitId))
            return StatusCode(403, new { message = "Not subscribed to this unit." });

        var unit = await _db.Units.AsNoTracking()
            .Where(u => u.Id == unitId)
            .Select(u => new { u.Id, u.Name })
            .FirstOrDefaultAsync();
        if (unit == null) return NotFound(new { message = "Unit not found." });

        return Ok(await BuildDetailsAsync(studentId, "Unit", unit.Id, unit.Name, unit.Id, null, null));
    }

    /// <summary>
    /// الجسم المشترك لكل أنواع التابس. unitId بس هو اللي بيفعّل الكويزات والواجبات
    /// وواجبات السنتر وبنك الأسئلة (دي كلها بتتبع Unit). المحاضرات: محاضرات الكورس +
    /// الكتب الخارجية المربوطة بيه، أو محاضرات Online Lesson، أو محاضرات كتاب خارجي.
    /// </summary>
    private async Task<MistakesCourseDetailsDto> BuildDetailsAsync(
        int studentId, string tabKind, int tabId, string tabName,
        int? unitIdOrNull, int? onlineLessonId, int? externalBookId)
    {
        // -1 مش بيطابق أي Unit، فاستعلامات الكورس بترجع فاضية لو التاب مش كورس.
        var unitId = unitIdOrNull ?? -1;

        var now = DateTime.UtcNow;
        var sources = new List<MistakeSourceDto>();
        var hidden = 0;

        void AddSource(
            string type, int id, string title, string? lectureName, string? bookName,
            DateTime? date, int? score, int? total, List<MistakeQuestionDto> mistakes)
        {
            if (mistakes.Count == 0) return;
            sources.Add(new MistakeSourceDto(type, id, title, lectureName, bookName, date, score, total, mistakes));
        }

        // ───────────── 1) الكويزات ─────────────
        var quizzes = await _db.Quizzes.AsNoTracking()
            .Include(q => q.Questions)
            .Where(q => q.UnitId == unitId)
            .ToListAsync();
        var quizIds = quizzes.Select(q => q.Id).ToList();

        if (quizIds.Count > 0)
        {
            var quizResults = await _db.QuizResults.AsNoTracking()
                .Include(r => r.Answers)
                // IsAutoSubmitted = الطالب ماحلّش الكويز (أو اتفتحله كمراجعة) -- مش محاولة حقيقية.
                .Where(r => r.StudentId == studentId && !r.IsAutoSubmitted && quizIds.Contains(r.QuizId))
                .ToListAsync();

            var quizReopen = (await _db.QuizStudentOverrides.AsNoTracking()
                    .Where(o => o.StudentId == studentId && quizIds.Contains(o.QuizId)
                                && o.ReopenExpiresAt != null && o.ReopenExpiresAt > now)
                    .Select(o => o.QuizId)
                    .ToListAsync())
                .ToHashSet();

            foreach (var r in quizResults)
            {
                var quiz = quizzes.First(q => q.Id == r.QuizId);

                // الحل مخفي لحد ما الديدلاين يخلص (أو طول ما فيه إعادة فتح شغالة).
                if (now <= quiz.Deadline || quizReopen.Contains(quiz.Id))
                {
                    hidden++;
                    continue;
                }

                var qs = quiz.Questions.Select(q => new QInfo(
                    q.Id, q.Text, q.ImageUrl, q.Type, q.Choices, q.Answer, q.Mark));
                var answers = ToAnswerMap(r.Answers, a => a.QuestionId, a => a.Answer, a => a.MarkAwarded);

                AddSource("Quiz", quiz.Id, quiz.Title, null, null,
                    r.GradedAt, r.Score, r.TotalMarks, CollectMistakes(qs, answers));
            }
        }

        // ───────────── 2) الواجبات العادية ─────────────
        var asgIds = await _db.AssignmentUnitLinks.AsNoTracking()
            .Where(x => x.UnitId == unitId)
            .Select(x => x.AssignmentId)
            .Distinct()
            .ToListAsync();

        if (asgIds.Count > 0)
        {
            var assignments = await _db.Assignments.AsNoTracking()
                .Include(a => a.Questions)
                .Where(a => asgIds.Contains(a.Id))
                .ToListAsync();
            var visibleAsgIds = assignments.Select(a => a.Id).ToList();

            var submissions = await _db.AssignmentSubmissions.AsNoTracking()
                .Include(s => s.Answers)
                .Where(s => s.StudentId == studentId && visibleAsgIds.Contains(s.AssignmentId))
                .ToListAsync();

            var asgReopen = (await _db.AssignmentStudentOverrides.AsNoTracking()
                    .Where(o => o.StudentId == studentId && visibleAsgIds.Contains(o.AssignmentId)
                                && o.ReopenExpiresAt != null && o.ReopenExpiresAt > now)
                    .Select(o => o.AssignmentId)
                    .ToListAsync())
                .ToHashSet();

            foreach (var s in submissions)
            {
                var asg = assignments.First(a => a.Id == s.AssignmentId);

                if (now <= asg.Deadline || asgReopen.Contains(asg.Id))
                {
                    hidden++;
                    continue;
                }

                var qs = asg.Questions.Select(q => new QInfo(
                    q.Id, q.Text, q.ImageUrl, q.Type, q.Choices, q.Answer, q.Mark));
                var answers = ToAnswerMap(s.Answers, a => a.QuestionId, a => a.Answer, a => a.MarkAwarded);

                AddSource("Assignment", asg.Id, asg.Title, null, null,
                    s.SubmittedAt, s.Score, s.TotalMarks, CollectMistakes(qs, answers));
            }
        }

        // ───────────── 3) واجبات السنتر (البابل شيت) ─────────────
        var centerIds = await _db.AssignmentCenterUnitLinks.AsNoTracking()
            .Where(x => x.UnitId == unitId)
            .Select(x => x.AssignmentCenterId)
            .Distinct()
            .ToListAsync();

        if (centerIds.Count > 0)
        {
            var centers = await _db.AssignmentCenters.AsNoTracking()
                .Include(a => a.Questions)
                .Where(a => centerIds.Contains(a.Id))
                .ToListAsync();
            var visibleCenterIds = centers.Select(a => a.Id).ToList();

            var centerSubs = await _db.AssignmentCenterSubmissions.AsNoTracking()
                .Include(s => s.Answers)
                .Where(s => s.StudentId == studentId && visibleCenterIds.Contains(s.AssignmentCenterId))
                .ToListAsync();

            var centerReopen = (await _db.AssignmentCenterStudentOverrides.AsNoTracking()
                    .Where(o => o.StudentId == studentId && visibleCenterIds.Contains(o.AssignmentCenterId)
                                && o.ReopenExpiresAt != null && o.ReopenExpiresAt > now)
                    .Select(o => o.AssignmentCenterId)
                    .ToListAsync())
                .ToHashSet();

            var letters = AssignmentCenterChoices.Letters.ToList();

            foreach (var s in centerSubs)
            {
                var asg = centers.First(a => a.Id == s.AssignmentCenterId);

                if (now <= asg.Deadline || centerReopen.Contains(asg.Id))
                {
                    hidden++;
                    continue;
                }

                // السؤال هنا دايرة (أ/ب/ج/د) من غير صورة ولا اختيارات نصية.
                var qs = asg.Questions.Select(q => new QInfo(
                    q.Id, q.Text, null, "Bubble", letters, q.Answer, q.Mark));
                var answers = ToAnswerMap(s.Answers, a => a.QuestionId, a => a.Answer, a => a.MarkAwarded);

                AddSource("AssignmentCenter", asg.Id, asg.Title, null, null,
                    s.SubmittedAt, s.Score, s.TotalMarks, CollectMistakes(qs, answers));
            }
        }

        // ───────────── 4) امتحانات وواجبات المحاضرات ─────────────
        // محاضرات الكورس نفسه + محاضرات أي كتاب خارجي مربوط بالكورس ده.
        var booksQuery = _db.ExternalBooks.AsNoTracking().AsQueryable();
        booksQuery = externalBookId.HasValue
            ? booksQuery.Where(e => e.Id == externalBookId.Value)
            : booksQuery.Where(e => e.UnitId == unitId);
        var books = await booksQuery.Select(e => new { e.Id, e.Name }).ToListAsync();
        var bookNames = books.ToDictionary(b => b.Id, b => b.Name);
        var bookIds = books.Select(b => b.Id).ToList();

        var byUnit = unitIdOrNull.HasValue;
        var lectures = await _db.Lectures.AsNoTracking()
            .Where(l => (byUnit && l.UnitId == unitId) ||
                        (l.ExternalBookId != null && bookIds.Contains(l.ExternalBookId.Value)) ||
                        (onlineLessonId != null && l.OnlineLessonId == onlineLessonId))
            .Select(l => new { l.Id, l.Name, l.ExternalBookId })
            .ToListAsync();
        var lectureById = lectures.ToDictionary(l => l.Id);
        var lectureIds = lectures.Select(l => l.Id).ToList();

        string? BookNameOf(int lectureId)
        {
            if (!lectureById.TryGetValue(lectureId, out var l)) return null;
            return l.ExternalBookId != null && bookNames.TryGetValue(l.ExternalBookId.Value, out var n) ? n : null;
        }

        if (lectureIds.Count > 0)
        {
            // امتحانات المحاضرات
            var exams = await _db.LectureExams.AsNoTracking()
                .Include(e => e.Questions)
                .Where(e => lectureIds.Contains(e.LectureId))
                .ToListAsync();
            var examIds = exams.Select(e => e.Id).ToList();

            if (examIds.Count > 0)
            {
                var examResults = await _db.LectureExamResults.AsNoTracking()
                    .Include(r => r.Answers)
                    .Where(r => r.StudentId == studentId && examIds.Contains(r.LectureExamId))
                    .ToListAsync();

                foreach (var r in examResults)
                {
                    // امتحان المحاضرة معندوش ديدلاين -- الحل بيظهر على طول بعد التسليم.
                    // نتيجة كل إجاباتها فاضية = المدرس فتحه كمراجعة، مش محاولة حقيقية.
                    if (AllBlank(r.Answers.Select(a => a.Answer))) continue;

                    var exam = exams.First(e => e.Id == r.LectureExamId);
                    var qs = exam.Questions.Select(q => new QInfo(
                        q.Id, q.Text, q.ImageUrl, q.Type, q.Choices, q.Answer, q.Mark));
                    var answers = ToAnswerMap(r.Answers, a => a.QuestionId, a => a.Answer, a => a.MarkAwarded);

                    AddSource("LectureExam", exam.Id, exam.Title,
                        lectureById[exam.LectureId].Name, BookNameOf(exam.LectureId),
                        r.GradedAt, r.Score, r.TotalMarks, CollectMistakes(qs, answers));
                }
            }

            // واجبات المحاضرات
            var lecAsgs = await _db.LectureAssignments.AsNoTracking()
                .Include(a => a.Questions)
                .Where(a => lectureIds.Contains(a.LectureId))
                .ToListAsync();
            var lecAsgIds = lecAsgs.Select(a => a.Id).ToList();

            if (lecAsgIds.Count > 0)
            {
                var lecAsgResults = await _db.LectureAssignmentResults.AsNoTracking()
                    .Include(r => r.Answers)
                    .Where(r => r.StudentId == studentId && lecAsgIds.Contains(r.LectureAssignmentId))
                    .ToListAsync();

                foreach (var r in lecAsgResults)
                {
                    if (AllBlank(r.Answers.Select(a => a.Answer))) continue;

                    var asg = lecAsgs.First(a => a.Id == r.LectureAssignmentId);
                    var qs = asg.Questions.Select(q => new QInfo(
                        q.Id, q.Text, q.ImageUrl, q.Type, q.Choices, q.Answer, q.Mark));
                    var answers = ToAnswerMap(r.Answers, a => a.QuestionId, a => a.Answer, a => a.MarkAwarded);

                    AddSource("LectureAssignment", asg.Id, asg.Title,
                        lectureById[asg.LectureId].Name, BookNameOf(asg.LectureId),
                        r.GradedAt, r.Score, r.TotalMarks, CollectMistakes(qs, answers));
                }
            }
        }

        // ───────────── 5) محاولات بنك الأسئلة ─────────────
        // السؤال بيتبع الكورس لو UnitId بتاعه = الكورس، أو لو الدرس بتاعه تابع للكورس.
        // بنحسب بس المحاولات اللي اتسلّمت في وقتها (SubmittedAt <= Deadline) --
        // اللي اتسلّمت بعد انتهاء الوقت بتتصفّر أوتوماتيك فمش محاولة حقيقية.
        var bankRows = await _db.BankAttemptQuestions.AsNoTracking()
            .Where(aq => aq.Attempt != null
                         && aq.Attempt.StudentId == studentId
                         && aq.Attempt.SubmittedAt != null
                         && aq.Attempt.SubmittedAt <= aq.Attempt.Deadline
                         && aq.BankQuestion != null
                         && (aq.BankQuestion.UnitId == unitId
                             || (aq.BankQuestion.Lesson != null && aq.BankQuestion.Lesson.UnitId == unitId)))
            .Select(aq => new
            {
                aq.AttemptId,
                SubmittedAt = aq.Attempt!.SubmittedAt,
                AttemptScore = aq.Attempt.Score,
                AttemptTotal = aq.Attempt.TotalMarks,
                QuestionId = aq.BankQuestionId,
                aq.Answer,
                aq.MarkAwarded,
                aq.ShuffledChoicesCsv,
                Text = aq.BankQuestion!.Text,
                Type = aq.BankQuestion.Type,
                ChoicesCsv = aq.BankQuestion.ChoicesCsv,
                ImageUrl = aq.BankQuestion.ImageUrl,
                Correct = aq.BankQuestion.Answer,
                Mark = aq.BankQuestion.Mark
            })
            .ToListAsync();

        foreach (var g in bankRows.GroupBy(x => x.AttemptId))
        {
            var mistakes = new List<MistakeQuestionDto>();
            foreach (var row in g.OrderBy(x => x.QuestionId))
            {
                if (!IsWrong(row.Mark, row.MarkAwarded, row.Answer, row.Correct)) continue;

                // نفس ترتيب الاختيارات اللي الطالب شافه وقت المحاولة (متلخبطة وثابتة).
                var csv = row.ShuffledChoicesCsv.Length > 0 ? row.ShuffledChoicesCsv : row.ChoicesCsv;
                var choices = csv.Length == 0 ? new List<string>() : csv.Split('\u001F').ToList();

                mistakes.Add(new MistakeQuestionDto(
                    row.QuestionId, row.Text, row.ImageUrl, row.Type, choices,
                    string.IsNullOrWhiteSpace(row.Answer) ? null : row.Answer,
                    row.Correct, row.Mark, row.MarkAwarded));
            }

            var first = g.First();
            AddSource("BankAttempt", g.Key, "", null, null,
                first.SubmittedAt, first.AttemptScore, first.AttemptTotal, mistakes);
        }

        // الأحدث الأول.
        var ordered = sources
            .OrderByDescending(s => s.Date ?? DateTime.MinValue)
            .ToList();

        return new MistakesCourseDetailsDto(
            tabKind, tabId, tabName, ordered.Sum(s => s.Mistakes.Count), hidden, ordered);
    }
}
