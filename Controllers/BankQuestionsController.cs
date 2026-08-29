using EduApi.Common;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/BankQuestions — question bank ("بنك الأسئلة").
///
///  Teacher side:
///   POST BankQuestions                          (multipart, one question at a time: LessonId, Type, Text, Answer, Mark, Difficulty, Choices[], image)
///   POST BankQuestions/bulk                     (multipart, like the single Create above but N questions + optional per-question image — all-or-nothing batch import)
///   POST BankQuestions/sync-from-history        (auto-imports questions from every past quiz/assignment not already in the bank)
///   GET  BankQuestions?lessonId=..&difficulty=..&p=..
///   POST BankQuestions/delete/{id}
///   GET  BankQuestions/attempts?p=..             (roster of every student practice attempt)
///   GET  BankQuestions/attempts/{attemptId}      (full detail of one attempt)
///
///  Student side:
///   GET  BankQuestions/scope                     (their subscribed units → lessons → question counts per difficulty)
///   POST BankQuestions/start                     body: { unitIds, lessonIds?, difficulty?, count, durationMinutes }
///   GET  BankQuestions/my-attempts?p=..           (their own practice-attempt history)
///   GET  BankQuestions/attempt/{attemptId}        (solve before deadline / review after submit or deadline)
///   POST BankQuestions/submit                     body: { attemptId, answers:[{questionId, answer}] }
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class BankQuestionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _files;

    public BankQuestionsController(AppDbContext db, IFileStorageService files)
    {
        _db = db;
        _files = files;
    }

    // ════════════════════════════════════════════
    //  TEACHER — manage bank questions
    // ════════════════════════════════════════════

    [HttpPost]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Create()
    {
        var form = await Request.ReadFormAsync();

        var lessonIdRaw = form["LessonId"].ToString();
        var unitIdRaw = form["UnitId"].ToString();
        var hasLessonId = int.TryParse(lessonIdRaw, out var lessonId);
        var hasUnitId = int.TryParse(unitIdRaw, out var unitId);

        if (hasLessonId == hasUnitId)
            return BadRequest(new { message = "Provide exactly one of LessonId or UnitId." });

        var question = new BankQuestion
        {
            Type = form["Type"].ToString(),
            Text = form["Text"].ToString(),
            Answer = form["Answer"].ToString(),
            Mark = int.Parse(form["Mark"].ToString()),
            Difficulty = form["Difficulty"].ToString(),
            TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
        };

        if (hasLessonId)
        {
            var lesson = await _db.Lessons.FirstOrDefaultAsync(l => l.Id == lessonId);
            if (lesson == null) return NotFound(new { message = "Lesson not found." });
            question.LessonId = lessonId;
        }
        else
        {
            var unit = await _db.Units.FirstOrDefaultAsync(u => u.Id == unitId);
            if (unit == null) return NotFound(new { message = "Unit not found." });
            question.UnitId = unitId;
        }

        var choices = new List<string>();
        for (var j = 0; form.ContainsKey($"Choices[{j}]"); j++)
            choices.Add(form[$"Choices[{j}]"].ToString());
        question.Choices = choices;

        var imageFile = form.Files["image"];
        if (imageFile != null)
            question.ImageUrl = await _files.SaveAsync(imageFile, "bank-questions");

        _db.BankQuestions.Add(question);
        await _db.SaveChangesAsync();

        return Ok(new { id = question.Id });
    }

    // multipart/form-data, exactly like the single-question Create() above, but
    // for N questions at once. Fields are indexed per question:
    //   LessonId or UnitId                          (shared by every question, top-level)
    //   Questions[0].Type / .Text / .Answer / .Mark / .Difficulty
    //   Questions[0].Choices[0], Questions[0].Choices[1], ...
    //   Questions[0].Image                           (file, optional — same as "image" in Create())
    [HttpPost("bulk")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> CreateBulk()
    {
        var form = await Request.ReadFormAsync();

        var lessonIdRaw = form["LessonId"].ToString();
        var unitIdRaw = form["UnitId"].ToString();
        var hasLessonId = int.TryParse(lessonIdRaw, out var lessonId);
        var hasUnitId = int.TryParse(unitIdRaw, out var unitId);

        if (hasLessonId == hasUnitId)
            return BadRequest(new { message = "Provide exactly one of LessonId or UnitId." });

        if (hasLessonId)
        {
            var lesson = await _db.Lessons.FirstOrDefaultAsync(l => l.Id == lessonId);
            if (lesson == null) return NotFound(new { message = "Lesson not found." });
        }
        else
        {
            var unit = await _db.Units.FirstOrDefaultAsync(u => u.Id == unitId);
            if (unit == null) return NotFound(new { message = "Unit not found." });
        }

        // Parse every "Questions[i].*" field found in the form, stopping at
        // the first index with no Text field (marks the end of the list).
        var parsed = new List<(string Type, string Text, string Answer, int Mark,
            string Difficulty, List<string> Choices, IFormFile? Image)>();

        for (var i = 0; form.ContainsKey($"Questions[{i}].Text"); i++)
        {
            var prefix = $"Questions[{i}]";
            var choices = new List<string>();
            for (var j = 0; form.ContainsKey($"{prefix}.Choices[{j}]"); j++)
                choices.Add(form[$"{prefix}.Choices[{j}]"].ToString());

            int.TryParse(form[$"{prefix}.Mark"].ToString(), out var mark);

            parsed.Add((
                Type: form[$"{prefix}.Type"].ToString(),
                Text: form[$"{prefix}.Text"].ToString(),
                Answer: form[$"{prefix}.Answer"].ToString(),
                Mark: mark,
                Difficulty: form[$"{prefix}.Difficulty"].ToString(),
                Choices: choices,
                Image: form.Files[$"{prefix}.Image"]
            ));
        }

        if (parsed.Count == 0)
            return BadRequest(new { message = "No questions found in the request." });

        // Validate every item up front so the batch is all-or-nothing —
        // no half-imported bank left behind if one question is malformed.
        var errors = new List<string>();
        for (var i = 0; i < parsed.Count; i++)
        {
            var q = parsed[i];
            if (string.IsNullOrWhiteSpace(q.Text)) errors.Add($"Question {i + 1}: Text is required.");
            if (string.IsNullOrWhiteSpace(q.Answer)) errors.Add($"Question {i + 1}: Answer is required.");
            if (string.IsNullOrWhiteSpace(q.Type)) errors.Add($"Question {i + 1}: Type is required.");
            if (string.IsNullOrWhiteSpace(q.Difficulty)) errors.Add($"Question {i + 1}: Difficulty is required.");
            if (q.Mark <= 0) errors.Add($"Question {i + 1}: Mark must be greater than 0.");
            if (string.Equals(q.Type, "MCQ", StringComparison.OrdinalIgnoreCase) && q.Choices.Count < 2)
                errors.Add($"Question {i + 1}: MCQ questions need at least 2 choices.");
        }
        if (errors.Count > 0)
            return BadRequest(new { message = "Some questions failed validation.", errors });

        var teacherId = User.GetStaffTenantId()!.Value; // TENANT LAYER
        var entities = new List<BankQuestion>();

        // Images are uploaded one at a time (each is its own await), same as
        // Create() above — the storage service has no batch-upload variant.
        foreach (var q in parsed)
        {
            var question = new BankQuestion
            {
                LessonId = hasLessonId ? lessonId : null,
                UnitId = hasUnitId ? unitId : null,
                Type = q.Type,
                Text = q.Text,
                Answer = q.Answer,
                Mark = q.Mark,
                Difficulty = q.Difficulty,
                Choices = q.Choices,
                TeacherId = teacherId
            };

            if (q.Image != null)
                question.ImageUrl = await _files.SaveAsync(q.Image, "bank-questions");

            entities.Add(question);
        }

        _db.BankQuestions.AddRange(entities);
        await _db.SaveChangesAsync();

        return Ok(new BulkCreateBankQuestionsResult(entities.Count, entities.Select(e => e.Id).ToList()));
    }

    // Scans EVERY past (Deadline already passed) quiz and assignment the
    // teacher owns, and auto-imports any question that isn't already in the
    // bank (tracked via SourceQuizId/SourceAssignmentId + SourceQuestionId, so
    // it's always safe to call again later — already-imported questions and
    // still-upcoming quizzes/assignments are just skipped).
    //
    // Placement: a unit with lessons -> its LAST lesson (by LessonIndex); a
    // unit with no lessons -> attached directly to the unit. An assignment
    // covering more than one unit is skipped (a question isn't tagged with
    // which of the assignment's units it belongs to, so it can't be placed
    // automatically) and listed under "skippedMultiUnitAssignments".
    //
    // Difficulty: a question the majority of takers got wrong (MarkAwarded ==
    // 0 for more than half the graded answers) is forced to "Hard"; otherwise
    // it's assigned at random across Easy/Medium/Hard.
    [HttpPost("sync-from-history")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> SyncFromHistory()
    {
        var teacherId = User.GetStaffTenantId()!.Value; // TENANT LAYER
        var now = DateTime.UtcNow;
        var created = new List<BankQuestion>();
        var skippedMultiUnitAssignments = new List<int>();

        // Cache unit -> (lessonId, unitId) placement lookups so a repeated
        // unit across many quizzes only hits the DB once.
        var placementCache = new Dictionary<int, (int? lessonId, int? unitId)>();
        async Task<(int? lessonId, int? unitId)?> GetPlacementAsync(int unitId)
        {
            if (placementCache.TryGetValue(unitId, out var cached)) return cached;
            var unit = await _db.Units.Include(u => u.Lessons).FirstOrDefaultAsync(u => u.Id == unitId);
            if (unit == null) return null;
            var placement = unit.Lessons.Count > 0
                ? ((int?)unit.Lessons.OrderByDescending(l => l.LessonIndex).First().Id, (int?)null)
                : ((int?)null, (int?)unit.Id);
            placementCache[unitId] = placement;
            return placement;
        }

        // ── Quizzes ──
        var pastQuizzes = await _db.Quizzes.Include(q => q.Questions)
            .Where(q => q.Deadline < now).ToListAsync();
        var importedQuizQuestionIds = (await _db.BankQuestions
                .Where(bq => bq.SourceQuizId != null)
                .Select(bq => new { bq.SourceQuizId, bq.SourceQuestionId })
                .ToListAsync())
            .Select(x => (x.SourceQuizId, x.SourceQuestionId)).ToHashSet();

        foreach (var quiz in pastQuizzes)
        {
            var placement = await GetPlacementAsync(quiz.UnitId);
            if (placement == null) continue;

            foreach (var q in quiz.Questions)
            {
                if (importedQuizQuestionIds.Contains((quiz.Id, q.Id))) continue;

                var answers = await _db.QuizAnswers
                    .Where(a => a.QuestionId == q.Id && a.QuizResult!.QuizId == quiz.Id)
                    .Select(a => a.MarkAwarded)
                    .ToListAsync();
                var graded = answers.Where(m => m.HasValue).Select(m => m!.Value).ToList();
                var wrongRate = graded.Count > 0 ? graded.Count(m => m == 0) / (double)graded.Count : 0;

                created.Add(new BankQuestion
                {
                    LessonId = placement.Value.lessonId,
                    UnitId = placement.Value.unitId,
                    Type = q.Type,
                    Text = q.Text,
                    Answer = q.Answer,
                    Mark = q.Mark,
                    Choices = q.Choices,
                    ImageUrl = q.ImageUrl,
                    Difficulty = QuizzesController.PickDifficulty(wrongRate),
                    TeacherId = teacherId,
                    SourceQuizId = quiz.Id,
                    SourceQuestionId = q.Id
                });
            }
        }

        // ── Assignments ──
        var pastAssignments = await _db.Assignments.Include(a => a.Questions)
            .Where(a => a.Deadline < now).ToListAsync();
        var importedAssignmentQuestionIds = (await _db.BankQuestions
                .Where(bq => bq.SourceAssignmentId != null)
                .Select(bq => new { bq.SourceAssignmentId, bq.SourceQuestionId })
                .ToListAsync())
            .Select(x => (x.SourceAssignmentId, x.SourceQuestionId)).ToHashSet();

        foreach (var assignment in pastAssignments)
        {
            var unitIds = assignment.UnitIds;
            if (unitIds.Count != 1)
            {
                if (assignment.Questions.Count > 0) skippedMultiUnitAssignments.Add(assignment.Id);
                continue;
            }

            var placement = await GetPlacementAsync(unitIds[0]);
            if (placement == null) continue;

            foreach (var q in assignment.Questions)
            {
                if (importedAssignmentQuestionIds.Contains((assignment.Id, q.Id))) continue;

                var answers = await _db.AssignmentAnswers
                    .Where(a => a.QuestionId == q.Id && a.AssignmentSubmission!.AssignmentId == assignment.Id)
                    .Select(a => a.MarkAwarded)
                    .ToListAsync();
                var graded = answers.Where(m => m.HasValue).Select(m => m!.Value).ToList();
                var wrongRate = graded.Count > 0 ? graded.Count(m => m == 0) / (double)graded.Count : 0;

                created.Add(new BankQuestion
                {
                    LessonId = placement.Value.lessonId,
                    UnitId = placement.Value.unitId,
                    Type = q.Type,
                    Text = q.Text,
                    Answer = q.Answer,
                    Mark = q.Mark,
                    Choices = q.Choices,
                    ImageUrl = q.ImageUrl,
                    Difficulty = QuizzesController.PickDifficulty(wrongRate),
                    TeacherId = teacherId,
                    SourceAssignmentId = assignment.Id,
                    SourceQuestionId = q.Id
                });
            }
        }

        if (created.Count > 0)
        {
            _db.BankQuestions.AddRange(created);
            await _db.SaveChangesAsync();
        }

        return Ok(new
        {
            imported = created.Count,
            fromQuizzes = created.Count(c => c.SourceQuizId != null),
            fromAssignments = created.Count(c => c.SourceAssignmentId != null),
            skippedMultiUnitAssignments
        });
    }

    [HttpGet]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetAll([FromQuery] int? lessonId, [FromQuery] int? unitId,
        [FromQuery] string? difficulty, [FromQuery] int? schoolYear, [FromQuery] int p = 1)
    {
        var query = _db.BankQuestions.AsNoTracking()
            .Include(q => q.Lesson).ThenInclude(l => l!.Unit)
            .Include(q => q.Unit)
            .AsQueryable();

        if (lessonId.HasValue) query = query.Where(q => q.LessonId == lessonId.Value);
        if (unitId.HasValue)
            query = query.Where(q => q.Lesson!.UnitId == unitId.Value || q.UnitId == unitId.Value);
        if (!string.IsNullOrEmpty(difficulty)) query = query.Where(q => q.Difficulty == difficulty);
        // BUGFIX: without this, "GetAll" for a given year returned every
        // question the teacher ever uploaded across ALL school years (as long
        // as lessonId/unitId weren't explicitly picked) — filter by the
        // question's unit's SchoolYear (via Lesson or direct), same as
        // everywhere else in the app.
        if (schoolYear.HasValue)
            query = query.Where(q =>
                (q.Lesson != null && q.Lesson.Unit!.SchoolYear == schoolYear.Value) ||
                (q.Unit != null && q.Unit.SchoolYear == schoolYear.Value));

        var questions = await query
            .OrderByDescending(q => q.Id)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .ToListAsync();

        var items = questions.Select(q => new BankQuestionDto(
            q.Id, q.LessonId, q.Lesson?.Name,
            q.Lesson?.UnitId ?? q.UnitId ?? 0, q.Lesson?.Unit?.Name ?? q.Unit?.Name ?? "",
            q.Type, q.Text, q.Choices, q.Answer, q.Mark, q.ImageUrl, q.Difficulty));

        return Ok(items);
    }

    [HttpGet("stats")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetStats([FromQuery] int? lessonId, [FromQuery] int? unitId, [FromQuery] int p = 1)
    {
        var query = _db.BankQuestions.AsNoTracking().Include(q => q.Lesson).Include(q => q.Unit).AsQueryable();
        if (lessonId.HasValue) query = query.Where(q => q.LessonId == lessonId.Value);
        if (unitId.HasValue)
            query = query.Where(q => q.Lesson!.UnitId == unitId.Value || q.UnitId == unitId.Value);

        // Only Id/Text/Difficulty/Lesson name are read below -- project those
        // columns instead of the full question row (Choices/Answer/Mark/
        // ImageUrl/TeacherId are never used in this endpoint).
        var questions = await query
            .Select(q => new {
                q.Id, q.Text, q.Difficulty,
                LessonName = q.Lesson != null ? q.Lesson.Name : (q.Unit != null ? q.Unit.Name : null)
            })
            .ToListAsync();
        var questionIds = questions.Select(q => q.Id).ToList();

        // Every time one of these questions was actually answered inside an attempt.
        var answered = await _db.BankAttemptQuestions.AsNoTracking()
            .Where(aq => questionIds.Contains(aq.BankQuestionId) && aq.Answer != null)
            .Select(aq => new { aq.BankQuestionId, aq.MarkAwarded, StudentId = aq.Attempt!.StudentId })
            .ToListAsync();

        var items = questions.Select(q =>
        {
            var attemptsForQ = answered.Where(a => a.BankQuestionId == q.Id).ToList();
            var timesAnswered = attemptsForQ.Count;
            var correctCount = attemptsForQ.Count(a => (a.MarkAwarded ?? 0) > 0);
            var incorrectCount = timesAnswered - correctCount;
            var studentsWrongCount = attemptsForQ.Where(a => (a.MarkAwarded ?? 0) == 0)
                .Select(a => a.StudentId).Distinct().Count();

            return new BankQuestionStatsDto(
                q.Id, q.Text, q.Difficulty, q.LessonName ?? "",
                timesAnswered, correctCount, incorrectCount,
                timesAnswered > 0 ? Math.Round(correctCount * 100.0 / timesAnswered, 1) : 0,
                studentsWrongCount);
        })
        // Hardest / most-missed questions first — the most useful view for a teacher at a glance.
        .OrderBy(s => s.CorrectPercentage)
        // Same p convention as BankQuestions (GetAll) -- this used to return
        // every question's stats in one response, which doesn't scale once a
        // teacher's bank has hundreds of questions.
        .Skip((p - 1) * PagingDefaults.PageSize)
        .Take(PagingDefaults.PageSize);

        return Ok(items);
    }

    // Fix a bank question's text/mark/choices/correct-answer in place. Note this
    // is deliberately NOT scoped by TeacherId here (same as Delete above) —
    // matches the existing tenant behavior of this endpoint family.
    [HttpPost("edit")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> EditQuestion([FromBody] EditBankQuestionRequest request)
    {
        var question = await _db.BankQuestions.Include(q => q.Lesson).ThenInclude(l => l!.Unit)
            .Include(q => q.Unit).FirstOrDefaultAsync(q => q.Id == request.QuestionId);
        if (question == null) return NotFound(new { message = "Question not found." });

        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { message = "نص السؤال مطلوب." });
        if (request.Mark <= 0)
            return BadRequest(new { message = "درجة السؤال لازم تكون أكبر من صفر." });
        if (string.IsNullOrWhiteSpace(request.Answer))
            return BadRequest(new { message = "الإجابة الصحيحة مطلوبة." });

        question.Text = request.Text;
        question.Mark = request.Mark;
        question.Answer = request.Answer;
        if (request.Choices != null)
            question.Choices = request.Choices;

        await _db.SaveChangesAsync();

        return Ok(new BankQuestionDto(
            question.Id, question.LessonId, question.Lesson?.Name,
            question.Lesson?.UnitId ?? question.UnitId ?? 0, question.Lesson?.Unit?.Name ?? question.Unit?.Name ?? "",
            question.Type, question.Text, question.Choices, question.Answer, question.Mark, question.ImageUrl, question.Difficulty));
    }

    [HttpPost("delete/{id:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> Delete(int id)
    {
        var question = await _db.BankQuestions.FirstOrDefaultAsync(q => q.Id == id);
        if (question == null) return NotFound(new { message = "Question not found." });

        _db.BankQuestions.Remove(question);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Question deleted." });
    }

    [HttpGet("attempts")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetAttempts([FromQuery] int p = 1)
    {
        var attempts = await _db.BankAttempts.AsNoTracking().Include(a => a.Questions)
            .OrderByDescending(a => a.Id)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .ToListAsync();

        var studentIds = attempts.Select(a => a.StudentId).Distinct().ToList();
        // Only Id/Name are read below.
        var students = await _db.Students.AsNoTracking().Where(s => studentIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name })
            .ToListAsync();
        var groupNames = await _db.GetTenantGroupNamesAsync(studentIds);

        var items = attempts.Select(a =>
        {
            var student = students.FirstOrDefault(s => s.Id == a.StudentId);
            return new BankAttemptListItemDto(
                a.Id, a.StudentId, student?.Name ?? "", groupNames.GetValueOrDefault(a.StudentId, ""),
                a.StartedAt, a.Deadline, a.SubmittedAt != null,
                a.SubmittedAt != null ? a.Score : null,
                a.SubmittedAt != null ? a.TotalMarks : null,
                a.Difficulty, a.Questions.Count);
        });

        return Ok(items);
    }

    [HttpGet("attempts/{attemptId:int}")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
    public async Task<IActionResult> GetAttemptDetails(int attemptId)
    {
        var attempt = await _db.BankAttempts.AsNoTracking().Include(a => a.Questions).ThenInclude(q => q.BankQuestion)
            .FirstOrDefaultAsync(a => a.Id == attemptId);
        if (attempt == null) return NotFound(new { message = "Attempt not found." });

        var student = await _db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == attempt.StudentId);
        var groupName = await _db.GetTenantGroupNameAsync(attempt.StudentId);

        var items = attempt.Questions.Select(aq => new
        {
            id = aq.BankQuestionId,
            text = aq.BankQuestion?.Text,
            questionType = aq.BankQuestion?.Type,
            choices = aq.ShuffledChoices.Count > 0 ? aq.ShuffledChoices : aq.BankQuestion?.Choices,
            imageUrl = aq.BankQuestion?.ImageUrl,
            difficulty = aq.BankQuestion?.Difficulty,
            mark = aq.BankQuestion?.Mark,
            correctAnswer = aq.BankQuestion?.Answer,
            answer = aq.Answer,
            markAwarded = aq.MarkAwarded
        });

        return Ok(new
        {
            studentName = student?.Name ?? "",
            groupName = groupName ?? "",
            startedAt = attempt.StartedAt,
            deadline = attempt.Deadline,
            submittedAt = attempt.SubmittedAt,
            score = attempt.Score,
            totalMarks = attempt.TotalMarks,
            questions = items
        });
    }

    // ════════════════════════════════════════════
    //  STUDENT — scope picker + practice attempts
    // ════════════════════════════════════════════

    [HttpGet("scope")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> GetScope()
    {
        var subscribedUnitIds = User.GetUnitIds();
        if (subscribedUnitIds.Count == 0) return Ok(new List<BankUnitScopeDto>());

        var units = await _db.Units.AsNoTracking().Where(u => subscribedUnitIds.Contains(u.Id))
            .Include(u => u.Lessons).ToListAsync();
        var unitIds = units.Select(u => u.Id).ToList();

        var lessonIds = units.SelectMany(u => u.Lessons).Select(l => l.Id).ToList();
        var counts = await _db.BankQuestions.AsNoTracking().Where(q => q.LessonId != null && lessonIds.Contains(q.LessonId!.Value))
            .GroupBy(q => new { q.LessonId, q.Difficulty })
            .Select(g => new { g.Key.LessonId, g.Key.Difficulty, Count = g.Count() })
            .ToListAsync();

        // Questions the teacher attached directly to the unit (no lesson picked).
        var directCounts = await _db.BankQuestions.AsNoTracking().Where(q => q.UnitId != null && unitIds.Contains(q.UnitId!.Value))
            .GroupBy(q => new { q.UnitId, q.Difficulty })
            .Select(g => new { g.Key.UnitId, g.Key.Difficulty, Count = g.Count() })
            .ToListAsync();

        var result = units.Select(u => new BankUnitScopeDto(
            u.Id, u.Name,
            u.Lessons.Select(l => new BankLessonScopeDto(
                l.Id, l.Name,
                counts.Where(c => c.LessonId == l.Id && c.Difficulty == "Easy").Sum(c => c.Count),
                counts.Where(c => c.LessonId == l.Id && c.Difficulty == "Medium").Sum(c => c.Count),
                counts.Where(c => c.LessonId == l.Id && c.Difficulty == "Hard").Sum(c => c.Count)
            )).ToList(),
            directCounts.Where(c => c.UnitId == u.Id && c.Difficulty == "Easy").Sum(c => c.Count),
            directCounts.Where(c => c.UnitId == u.Id && c.Difficulty == "Medium").Sum(c => c.Count),
            directCounts.Where(c => c.UnitId == u.Id && c.Difficulty == "Hard").Sum(c => c.Count)
        ));

        return Ok(result);
    }

    [HttpGet("my-attempts")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> GetMyAttempts([FromQuery] int p = 1)
    {
        var studentId = User.GetUserId();

        var attempts = await _db.BankAttempts.AsNoTracking().Include(a => a.Questions)
            .Where(a => a.StudentId == studentId)
            .OrderByDescending(a => a.Id)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .ToListAsync();

        var now = DateTime.UtcNow;

        // Same reveal rule as GetAttempt below: score is only shown once the
        // student has submitted, or the attempt's own Deadline has passed —
        // never expose the score for a still-in-progress attempt.
        var items = attempts.Select(a =>
        {
            var revealed = a.SubmittedAt != null || now > a.Deadline;
            return new MyBankAttemptListItemDto(
                a.Id, a.StartedAt, a.Deadline, a.SubmittedAt != null,
                revealed ? a.Score : null,
                revealed ? a.TotalMarks : null,
                a.Difficulty, a.Questions.Count);
        });

        return Ok(items);
    }

    [HttpPost("start")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> StartAttempt([FromBody] StartBankAttemptRequest request)
    {
        var subscribedUnitIds = User.GetUnitIds();
        if (request.UnitIds.Any(id => !subscribedUnitIds.Contains(id)))
            return StatusCode(403, new { message = "Not subscribed to one or more of the selected units." });

        if (request.Count <= 0 || request.DurationMinutes <= 0)
            return BadRequest(new { message = "Count and duration must be positive." });

        var studentId = User.GetUserId();
        var excludeSolved = request.ExcludeSolved == true;
        var weakOnly = request.WeakOnly == true;
        var useMixed = request.UseMixedDifficulty == true;

        var basePool = _db.BankQuestions.Where(q =>
            (q.LessonId != null && request.UnitIds.Contains(q.Lesson!.UnitId)) ||
            (q.UnitId != null && request.UnitIds.Contains(q.UnitId!.Value)));
        if (request.LessonIds != null && request.LessonIds.Count > 0)
            basePool = basePool.Where(q => q.LessonId != null && request.LessonIds.Contains(q.LessonId!.Value));

        // Every BankQuestionId this student has ever answered (right or wrong), across all attempts.
        var answeredQuestionIds = await _db.BankAttemptQuestions
            .Where(aq => aq.Attempt!.StudentId == studentId && aq.Answer != null)
            .Select(aq => aq.BankQuestionId)
            .Distinct()
            .ToListAsync();

        // Only the ones this student got WRONG (MarkAwarded == 0).
        var wrongQuestionIds = await _db.BankAttemptQuestions
            .Where(aq => aq.Attempt!.StudentId == studentId && aq.Answer != null && aq.MarkAwarded == 0)
            .Select(aq => aq.BankQuestionId)
            .Distinct()
            .ToListAsync();

        if (weakOnly)
            basePool = basePool.Where(q => wrongQuestionIds.Contains(q.Id));
        else if (excludeSolved)
            basePool = basePool.Where(q => !answeredQuestionIds.Contains(q.Id));

        var random = new Random();
        List<BankQuestion> selected;

        if (useMixed)
        {
            // 30% Easy / 40% Medium / 30% Hard, adjusted so the three buckets always add up to Count exactly.
            var easyCount = (int)Math.Round(request.Count * 0.3);
            var mediumCount = (int)Math.Round(request.Count * 0.4);
            var hardCount = request.Count - easyCount - mediumCount;

            var easyPool = await basePool.Where(q => q.Difficulty == "Easy").ToListAsync();
            var mediumPool = await basePool.Where(q => q.Difficulty == "Medium").ToListAsync();
            var hardPool = await basePool.Where(q => q.Difficulty == "Hard").ToListAsync();

            if (easyPool.Count < easyCount || mediumPool.Count < mediumCount || hardPool.Count < hardCount)
                return BadRequest(new
                {
                    message = "Not enough questions available for the requested mixed-difficulty split.",
                    required = new { easy = easyCount, medium = mediumCount, hard = hardCount },
                    available = new { easy = easyPool.Count, medium = mediumPool.Count, hard = hardPool.Count }
                });

            selected = easyPool.OrderBy(_ => random.Next()).Take(easyCount)
                .Concat(mediumPool.OrderBy(_ => random.Next()).Take(mediumCount))
                .Concat(hardPool.OrderBy(_ => random.Next()).Take(hardCount))
                .OrderBy(_ => random.Next()) // shuffle the combined set so difficulties aren't grouped
                .ToList();
        }
        else
        {
            if (!string.IsNullOrEmpty(request.Difficulty))
                basePool = basePool.Where(q => q.Difficulty == request.Difficulty);

            var available = await basePool.ToListAsync();
            if (available.Count < request.Count)
                return BadRequest(new { message = $"Only {available.Count} question(s) match this selection, but {request.Count} were requested." });

            selected = available.OrderBy(_ => random.Next()).Take(request.Count).ToList();
        }

        var now = DateTime.UtcNow;
        var attempt = new BankAttempt
        {
            StudentId = studentId,
            TeacherId = selected[0].TeacherId,
            UnitIds = request.UnitIds,
            LessonIds = request.LessonIds ?? new List<int>(),
            Difficulty = useMixed ? "Mixed" : request.Difficulty,
            DurationMinutes = request.DurationMinutes,
            StartedAt = now,
            Deadline = now.AddMinutes(request.DurationMinutes),
            TotalMarks = selected.Sum(q => q.Mark)
        };

        foreach (var q in selected)
        {
            var attemptQuestion = new BankAttemptQuestion { BankQuestionId = q.Id };
            // Randomize the on-screen order of MCQ choices, snapshotted so it stays
            // stable across repeated GETs of the same attempt.
            // FIX: this used to be a case-sensitive `q.Type == "MCQ"` check, but
            // Type is stored verbatim from whatever the client sent (see Create()
            // validation above, which already accepts "MCQ" case-insensitively)
            // and the Flutter app currently sends the type in lowercase ("mcq").
            // That mismatch meant MCQ choices silently never got shuffled for any
            // question whose Type wasn't the exact string "MCQ". Compare
            // case-insensitively here too, consistent with the validation check.
            if (string.Equals(q.Type, "MCQ", StringComparison.OrdinalIgnoreCase) && q.Choices.Count > 0)
                attemptQuestion.ShuffledChoices = q.Choices.OrderBy(_ => random.Next()).ToList();
            attempt.Questions.Add(attemptQuestion);
        }

        _db.BankAttempts.Add(attempt);
        await _db.SaveChangesAsync();

        return Ok(new StartBankAttemptResult(attempt.Id, attempt.Deadline, selected.Count, attempt.TotalMarks));
    }

    [HttpGet("attempt/{attemptId:int}")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> GetAttempt(int attemptId)
    {
        var studentId = User.GetUserId();
        var attempt = await _db.BankAttempts.AsNoTracking().Include(a => a.Questions).ThenInclude(q => q.BankQuestion)
            .FirstOrDefaultAsync(a => a.Id == attemptId && a.StudentId == studentId);
        if (attempt == null) return NotFound(new { message = "Attempt not found." });

        var deadlinePassed = DateTime.UtcNow > attempt.Deadline;
        var isSubmitted = attempt.SubmittedAt != null;
        // Like Quizzes: correction shows right after submitting, or once the
        // deadline runs out even without submitting.
        var revealAnswers = isSubmitted || deadlinePassed;

        var items = attempt.Questions.Select(aq => new
        {
            id = aq.BankQuestionId,
            text = aq.BankQuestion?.Text,
            questionType = aq.BankQuestion?.Type,
            choices = aq.ShuffledChoices.Count > 0 ? aq.ShuffledChoices : aq.BankQuestion?.Choices,
            imageUrl = aq.BankQuestion?.ImageUrl,
            mark = aq.BankQuestion?.Mark,
            difficulty = aq.BankQuestion?.Difficulty,
            answer = aq.Answer,
            markAwarded = revealAnswers ? aq.MarkAwarded : null,
            correctAnswer = revealAnswers ? aq.BankQuestion?.Answer : null
        });

        return Ok(new
        {
            deadline = attempt.Deadline,
            startedAt = attempt.StartedAt,
            isSubmitted,
            deadlinePassed,
            score = revealAnswers ? attempt.Score : (int?)null,
            totalMarks = attempt.TotalMarks,
            questions = items
        });
    }

    [HttpPost("submit")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> SubmitAttempt([FromBody] SubmitBankAttemptRequest request)
    {
        var studentId = User.GetUserId();
        var attempt = await _db.BankAttempts.Include(a => a.Questions).ThenInclude(q => q.BankQuestion)
            .FirstOrDefaultAsync(a => a.Id == request.AttemptId && a.StudentId == studentId);
        if (attempt == null) return NotFound(new { message = "Attempt not found." });

        if (attempt.SubmittedAt != null)
            return Conflict(new { message = "تم تسليم بنك الأسئلة ده قبل كده." });

        // Auto-zero if the timer had already run out (mirrors Quiz's auto-submit-at-0 behavior).
        var expired = DateTime.UtcNow > attempt.Deadline;

        var score = 0;
        if (!expired)
        {
            foreach (var submitted in request.Answers ?? new())
            {
                var aq = attempt.Questions.FirstOrDefault(q => q.BankQuestionId == submitted.QuestionId);
                if (aq?.BankQuestion == null) continue;

                aq.Answer = submitted.Answer;
                if (string.Equals(aq.BankQuestion.Answer, submitted.Answer, StringComparison.OrdinalIgnoreCase))
                {
                    aq.MarkAwarded = aq.BankQuestion.Mark;
                    score += aq.BankQuestion.Mark;
                }
                else
                {
                    aq.MarkAwarded = 0;
                }
            }
        }
        else
        {
            foreach (var aq in attempt.Questions) aq.MarkAwarded = 0;
        }

        attempt.Score = score;
        attempt.SubmittedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new SubmitBankAttemptResult(score, attempt.TotalMarks));
    }
}
