using EduApi.Common;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record EnrollMistakesRequest(string? Kind, int? Id);
public record ReviewAnswerRequest(int CardId, string? Answer, bool? SelfCorrect);
public record ReviewRevealRequest(int CardId);

/// <summary>
/// "اختبر نفسي في أخطائي" -- spaced repetition (Leitner boxes) built on top of the
/// existing Mistakes feature.
///
/// Route: api/Review  (student only)
///  POST Review/enroll        body { kind, id }   turn the mistakes of ONE "أخطائي" tab into flash-cards
///  POST Review/enroll-all                         same, for every tab the student has
///  GET  Review/stats                              due / total / mastered / per-box counts
///  GET  Review/session?limit=15&amp;courseKind=&amp;courseId=   the cards that are due now (NO answers inside)
///  POST Review/answer        body { cardId, answer?, selfCorrect? }  grade a card + reschedule it
///  POST Review/reveal        body { cardId }     written cards only: show the model answer before self-rating
///  POST Review/cards/{id}/archive                 remove a card from the deck
///
/// SERVER-SIDE TRUTH: the mistakes are collected by calling the real MistakesController
/// actions (so the solution-hidden-until-deadline rules are respected) -- the client
/// never sends question content. Correct answers only ever leave the server AFTER the
/// student has answered (POST Review/answer).
///
/// SCHEDULE (Leitner): wrong -> box 1, due in 10 minutes. Correct -> next box:
///   box 1 = +1 day, 2 = +3 days, 3 = +7 days, 4 = +14 days, 5 = +30 days.
/// A correct answer from box 5 marks the card as mastered (it stays in the deck, 60 days out).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = Roles.Student)]
public class ReviewController : ControllerBase
{
    private static readonly int[] IntervalDays = { 0, 1, 3, 7, 14, 30 };
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public ReviewController(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    private int StudentId => User.GetUserId();

    private static bool IsChoiceType(string type)
        => string.Equals(type, "MCQ", StringComparison.OrdinalIgnoreCase);

    private static bool Same(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a)
           && string.Equals(a.Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    // ───────────────────────── enroll ─────────────────────────

    [HttpPost("enroll")]
    public async Task<IActionResult> Enroll([FromBody] EnrollMistakesRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Kind) || !request.Id.HasValue)
            return BadRequest(new { message = "kind and id are required." });

        var added = await EnrollTabAsync(request.Kind!, request.Id.Value);
        return Ok(new { added });
    }

    [HttpPost("enroll-all")]
    public async Task<IActionResult> EnrollAll()
    {
        var mistakes = NewMistakesController();
        if ((await mistakes.GetCourses()) is not OkObjectResult { Value: List<MistakeCourseDto> courses })
            return Ok(new { added = 0 });

        var total = 0;
        foreach (var c in courses)
            total += await EnrollTabAsync(c.Kind, c.Id);
        return Ok(new { added = total });
    }

    private MistakesController NewMistakesController()
        => new MistakesController(_db) { ControllerContext = ControllerContext };

    private async Task<int> EnrollTabAsync(string kind, int id)
    {
        var tenantId = _tenant.CurrentTenantId;
        if (!tenantId.HasValue) return 0;

        var mistakes = NewMistakesController();
        IActionResult result = kind switch
        {
            "Unit" => await mistakes.GetCourseMistakes(id),
            "OnlineLesson" => await mistakes.GetOnlineLessonMistakes(id),
            "ExternalBook" => await mistakes.GetExternalBookMistakes(id),
            _ => new BadRequestResult(),
        };
        if (result is not OkObjectResult { Value: MistakesCourseDetailsDto details }) return 0;

        var studentId = StudentId;
        var existing = (await _db.ReviewCards.AsNoTracking()
                .Where(c => c.StudentId == studentId)
                .Select(c => new { c.SourceType, c.SourceId, c.QuestionId })
                .ToListAsync())
            .Select(c => (c.SourceType, c.SourceId, c.QuestionId))
            .ToHashSet();

        var now = DateTime.UtcNow;
        var fresh = new List<ReviewCard>();
        foreach (var src in details.Sources)
        {
            foreach (var q in src.Mistakes)
            {
                if (string.IsNullOrWhiteSpace(q.CorrectAnswer)) continue;
                if (!existing.Add((src.SourceType, src.SourceId, q.QuestionId))) continue;

                var title = src.LectureName != null ? $"{src.Title} — {src.LectureName}" : src.Title;
                fresh.Add(new ReviewCard
                {
                    TeacherId = tenantId.Value,
                    StudentId = studentId,
                    SourceType = src.SourceType,
                    SourceId = src.SourceId,
                    QuestionId = q.QuestionId,
                    CourseKind = details.Kind,
                    CourseId = details.Id,
                    CourseName = Trunc(details.Name, 200),
                    SourceTitle = Trunc(title, 200),
                    Text = q.Text,
                    ImageUrl = q.ImageUrl,
                    QuestionType = Trunc(q.QuestionType, 20),
                    Choices = q.Choices ?? new List<string>(),
                    CorrectAnswer = q.CorrectAnswer!,
                    Box = 0,
                    DueAt = now,
                });
            }
        }

        if (fresh.Count == 0) return 0;
        try
        {
            _db.ReviewCards.AddRange(fresh);
            await _db.SaveChangesAsync();
            return fresh.Count;
        }
        catch (DbUpdateException)
        {
            // Concurrent enroll of the same cards (unique index) -- cards exist anyway.
            foreach (var e in _db.ChangeTracker.Entries<ReviewCard>().ToList()) e.State = EntityState.Detached;
            return 0;
        }
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];

    // ───────────────────────── stats ─────────────────────────

    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        var studentId = StudentId;
        var now = DateTime.UtcNow;

        var cards = await _db.ReviewCards.AsNoTracking()
            .Where(c => c.StudentId == studentId && !c.IsArchived)
            .Select(c => new { c.Box, c.DueAt, c.MasteredAt, c.CourseKind, c.CourseId, c.CourseName })
            .ToListAsync();

        var today = now.Date;
        var reviewedToday = await _db.ReviewLogs.AsNoTracking()
            .CountAsync(l => l.StudentId == studentId && l.ReviewedAt >= today);

        var nextDue = cards.Where(c => c.DueAt > now).Select(c => (DateTime?)c.DueAt).OrderBy(d => d).FirstOrDefault();

        return Ok(new
        {
            total = cards.Count,
            due = cards.Count(c => c.DueAt <= now),
            mastered = cards.Count(c => c.MasteredAt != null),
            newCards = cards.Count(c => c.Box == 0),
            reviewedToday,
            nextDueAt = nextDue,
            boxes = Enumerable.Range(0, 6).Select(b => new { box = b, count = cards.Count(c => c.Box == b) }),
            courses = cards
                .GroupBy(c => new { c.CourseKind, c.CourseId, c.CourseName })
                .Select(g => new
                {
                    kind = g.Key.CourseKind, id = g.Key.CourseId, name = g.Key.CourseName,
                    total = g.Count(), due = g.Count(c => c.DueAt <= now),
                })
                .OrderByDescending(x => x.due).ThenByDescending(x => x.total),
        });
    }

    // ───────────────────────── session ─────────────────────────

    [HttpGet("session")]
    public async Task<IActionResult> Session(
        [FromQuery] int limit = 15, [FromQuery] string? courseKind = null, [FromQuery] int? courseId = null)
    {
        var studentId = StudentId;
        var now = DateTime.UtcNow;
        limit = Math.Clamp(limit, 1, 50);

        var q = _db.ReviewCards.AsNoTracking()
            .Where(c => c.StudentId == studentId && !c.IsArchived && c.DueAt <= now);
        if (!string.IsNullOrWhiteSpace(courseKind) && courseId.HasValue)
            q = q.Where(c => c.CourseKind == courseKind && c.CourseId == courseId.Value);

        var dueTotal = await q.CountAsync();
        // Oldest-due first, with the weakest boxes (most-failed) slightly prioritised by ordering on Box.
        var cards = await q.OrderBy(c => c.DueAt).ThenBy(c => c.Box).Take(limit).ToListAsync();

        return Ok(new
        {
            dueTotal,
            cards = cards.Select(c => new
            {
                id = c.Id,
                text = c.Text,
                imageUrl = c.ImageUrl,
                questionType = c.QuestionType,
                isChoice = IsChoiceType(c.QuestionType),
                choices = c.Choices,
                courseName = c.CourseName,
                sourceTitle = c.SourceTitle,
                box = c.Box,
                lapses = c.Lapses,
            }),
        });
    }

    // ───────────────────────── answer ─────────────────────────

    [HttpPost("answer")]
    public async Task<IActionResult> Answer([FromBody] ReviewAnswerRequest request)
    {
        var studentId = StudentId;
        var card = await _db.ReviewCards.FirstOrDefaultAsync(c => c.Id == request.CardId && c.StudentId == studentId);
        if (card == null) return NotFound(new { message = "Card not found." });

        bool correct;
        if (IsChoiceType(card.QuestionType))
        {
            // Multiple choice: the server decides.
            correct = Same(request.Answer, card.CorrectAnswer);
        }
        else
        {
            // Written / free-text: the student compares with the model answer and
            // rates themselves. (If they typed an exact match we still trust the match.)
            correct = request.SelfCorrect ?? Same(request.Answer, card.CorrectAnswer);
        }

        var now = DateTime.UtcNow;
        var before = card.Box;
        var masteredNow = false;

        card.Reps += 1;
        card.LastReviewedAt = now;

        if (correct)
        {
            var next = Math.Min(5, Math.Max(1, before + 1));
            if (before >= 5)
            {
                // Passed the longest interval again => mastered.
                if (card.MasteredAt == null) { card.MasteredAt = now; masteredNow = true; }
                card.DueAt = now.AddDays(60);
                next = 5;
            }
            else
            {
                card.DueAt = now.AddDays(IntervalDays[next]);
            }
            card.Box = next;
        }
        else
        {
            card.Lapses += 1;
            card.Box = 1;
            card.MasteredAt = null;
            card.DueAt = now.Add(RetryDelay);
        }

        _db.ReviewLogs.Add(new ReviewLog
        {
            TeacherId = card.TeacherId,
            StudentId = studentId,
            CardId = card.Id,
            Correct = correct,
            BoxBefore = before,
            BoxAfter = card.Box,
            ReviewedAt = now,
        });
        await _db.SaveChangesAsync();

        var dueLeft = await _db.ReviewCards.AsNoTracking()
            .CountAsync(c => c.StudentId == studentId && !c.IsArchived && c.DueAt <= now);

        return Ok(new
        {
            correct,
            correctAnswer = card.CorrectAnswer,
            box = card.Box,
            nextDueAt = card.DueAt,
            masteredNow,
            dueLeft,
        });
    }

    /// <summary>
    /// Written (non multiple-choice) cards only: show the model answer so the student can
    /// rate themselves, then POST Review/answer with selfCorrect. Multiple-choice cards are
    /// graded by the server and can NOT be revealed this way.
    /// </summary>
    [HttpPost("reveal")]
    public async Task<IActionResult> Reveal([FromBody] ReviewRevealRequest request)
    {
        var studentId = StudentId;
        var card = await _db.ReviewCards.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CardId && c.StudentId == studentId);
        if (card == null) return NotFound(new { message = "Card not found." });
        if (IsChoiceType(card.QuestionType))
            return BadRequest(new { message = "Answer this card to see the solution." });
        return Ok(new { correctAnswer = card.CorrectAnswer });
    }

    [HttpPost("cards/{id:int}/archive")]
    public async Task<IActionResult> Archive(int id)
    {
        var studentId = StudentId;
        var card = await _db.ReviewCards.FirstOrDefaultAsync(c => c.Id == id && c.StudentId == studentId);
        if (card == null) return NotFound(new { message = "Card not found." });
        card.IsArchived = true;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Removed from your review deck." });
    }
}
