using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record CreateChallengeRequest(int OpponentId, int? UnitId, int? LessonId, string? Difficulty, int? QuestionCount);
public record ChallengeAnswerItem(int QuestionId, string? Answer);
public record SubmitChallengeRequest(List<ChallengeAnswerItem>? Answers);

/// <summary>
/// "التحديات بين الطلبة": a 1v1 quiz duel between two classmates of the same teacher, on
/// question-bank questions. The server picks the questions, grades the answers and decides the
/// winner (higher score, then faster). The client is never trusted with anything but the answers.
///
/// Route: api/Challenges
///  STUDENT
///   GET  Challenges/opponents?q=            classmates (same group) I can challenge
///   POST Challenges                         body { opponentId, unitId?, lessonId?, difficulty?, questionCount? (5..15, default 10) }
///   GET  Challenges?status=&amp;p=              my challenges (Pending | Active | Completed | all)
///   GET  Challenges/{id}                    one challenge (+ result once I finished)
///   POST Challenges/{id}/accept | /decline  opponent answers the invitation
///   POST Challenges/{id}/cancel             challenger withdraws a Pending invitation
///   POST Challenges/{id}/start              my questions (NO answers inside); starts MY clock, once
///   POST Challenges/{id}/submit             body { answers: [{ questionId, answer }] }
///   GET  Challenges/leaderboard             wins in the last 30 days, this teacher's students
///  STAFF
///   GET  Challenges/teacher/summary         how much the class is using it + top duelists
///
/// LIFE CYCLE (resolved lazily on every read -- no background job):
///   Pending   -> opponent has 48h to accept, else Expired.
///   Active    -> both players have 24h after acceptance to play, each can play ONCE.
///   Completed -> both finished, or the 24h ended (whoever finished wins; nobody finished = draw).
///
/// Correct answers are revealed to a player only AFTER the challenge is completed, so a player who
/// finished first cannot pass the answers to the other. Points (via GamificationService):
/// win 20, draw 10, loss 5.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ChallengesController : ControllerBase
{
    private const int MinQuestions = 5;
    private const int MaxQuestions = 15;
    private const int DefaultQuestions = 10;
    private const int MaxOpenChallenges = 5;
    private const int MaxCreatedPerDay = 10;
    private static readonly TimeSpan AcceptWindow = TimeSpan.FromHours(48);
    private static readonly TimeSpan PlayWindow = TimeSpan.FromHours(24);

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IPushService _push;

    public ChallengesController(AppDbContext db, ITenantContext tenant, IPushService push)
    {
        _db = db;
        _tenant = tenant;
        _push = push;
    }

    private int StudentId => User.GetUserId();

    private static bool IsMcq(string type) => string.Equals(type, "MCQ", StringComparison.OrdinalIgnoreCase);
    private static string Norm(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();

    private static List<int> ParseIds(string csv)
        => string.IsNullOrWhiteSpace(csv)
            ? new List<int>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x, out var v) ? v : 0).Where(v => v > 0).ToList();

    // ───────────────────────── life cycle ─────────────────────────

    /// <summary>Moves a challenge forward when its time ran out. Saves when something changed.</summary>
    private async Task<StudentChallenge> ResolveAsync(StudentChallenge c)
    {
        var now = DateTime.UtcNow;
        var changed = false;

        if (c.Status == "Pending" && now > c.ExpiresAt)
        {
            c.Status = "Expired";
            c.CompletedAt = c.ExpiresAt;
            changed = true;
        }
        else if (c.Status == "Active" && c.PlayUntil.HasValue && now > c.PlayUntil.Value)
        {
            // A player who started but never finished scores nothing.
            if (c.ChallengerStartedAt.HasValue && !c.ChallengerFinishedAt.HasValue)
            {
                c.ChallengerFinishedAt = c.PlayUntil; c.ChallengerScore = 0; c.ChallengerSeconds = null;
            }
            if (c.OpponentStartedAt.HasValue && !c.OpponentFinishedAt.HasValue)
            {
                c.OpponentFinishedAt = c.PlayUntil; c.OpponentScore = 0; c.OpponentSeconds = null;
            }
            Finish(c, c.PlayUntil.Value);
            changed = true;
        }

        if (changed) await _db.SaveChangesAsync();
        return c;
    }

    /// <summary>Decides the winner and closes the challenge.</summary>
    private static void Finish(StudentChallenge c, DateTime at)
    {
        var a = c.ChallengerFinishedAt.HasValue ? c.ChallengerScore : null;
        var b = c.OpponentFinishedAt.HasValue ? c.OpponentScore : null;

        int? winner = null;
        if (a.HasValue && !b.HasValue) winner = c.ChallengerId;
        else if (!a.HasValue && b.HasValue) winner = c.OpponentId;
        else if (a.HasValue && b.HasValue)
        {
            if (a.Value != b.Value) winner = a.Value > b.Value ? c.ChallengerId : c.OpponentId;
            else if (c.ChallengerSeconds.HasValue && c.OpponentSeconds.HasValue && c.ChallengerSeconds != c.OpponentSeconds)
                winner = c.ChallengerSeconds < c.OpponentSeconds ? c.ChallengerId : c.OpponentId;
        }

        c.WinnerId = winner;
        c.Status = "Completed";
        c.CompletedAt = at;
    }

    private async Task<Dictionary<int, string>> NamesAsync(IEnumerable<int> studentIds)
    {
        var ids = studentIds.Distinct().ToList();
        // Names of classmates of the same teacher: ignore filters so a student who left the group still has a name.
        return await _db.Students.IgnoreQueryFilters().AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.Name })
            .ToDictionaryAsync(s => s.Id, s => s.Name);
    }

    private async Task<StudentChallenge?> LoadMineAsync(int id)
    {
        var me = StudentId;
        var c = await _db.StudentChallenges.FirstOrDefaultAsync(x => x.Id == id && (x.ChallengerId == me || x.OpponentId == me));
        return c == null ? null : await ResolveAsync(c);
    }

    private object ToDto(StudentChallenge c, Dictionary<int, string> names, int me)
    {
        var iAmChallenger = c.ChallengerId == me;
        var otherId = iAmChallenger ? c.OpponentId : c.ChallengerId;
        var myFinished = iAmChallenger ? c.ChallengerFinishedAt : c.OpponentFinishedAt;
        var theirFinished = iAmChallenger ? c.OpponentFinishedAt : c.ChallengerFinishedAt;
        var completed = c.Status == "Completed";

        string outcome = !completed ? "none"
            : c.WinnerId == null ? "draw"
            : c.WinnerId == me ? "win" : "loss";

        return new
        {
            id = c.Id,
            status = c.Status,
            questionCount = ParseIds(c.QuestionIdsCsv).Count,
            totalMarks = c.TotalMarks,
            unitId = c.UnitId,
            lessonId = c.LessonId,
            difficulty = c.Difficulty,
            iAmChallenger,
            opponent = new { id = otherId, name = names.GetValueOrDefault(otherId, "") },
            createdAt = c.CreatedAt,
            expiresAt = c.ExpiresAt,
            playUntil = c.PlayUntil,
            iFinished = myFinished.HasValue,
            theyFinished = theirFinished.HasValue,
            canStart = c.Status == "Active" && !(iAmChallenger ? c.ChallengerStartedAt : c.OpponentStartedAt).HasValue,
            canAccept = c.Status == "Pending" && !iAmChallenger,
            canCancel = c.Status == "Pending" && iAmChallenger,
            // Scores are shown only once everything is settled (or for my own result right after I finish).
            myScore = myFinished.HasValue ? (iAmChallenger ? c.ChallengerScore : c.OpponentScore) : null,
            mySeconds = myFinished.HasValue ? (iAmChallenger ? c.ChallengerSeconds : c.OpponentSeconds) : null,
            theirScore = completed ? (iAmChallenger ? c.OpponentScore : c.ChallengerScore) : null,
            theirSeconds = completed ? (iAmChallenger ? c.OpponentSeconds : c.ChallengerSeconds) : null,
            outcome,
            completedAt = c.CompletedAt,
        };
    }

    // ───────────────────────── opponents ─────────────────────────

    [HttpGet("opponents")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Opponents([FromQuery] string? q)
    {
        var me = StudentId;
        var groupId = User.GetGroupId(_tenant.CurrentTenantId);
        if (!groupId.HasValue) return Ok(Array.Empty<object>());

        var ids = await _db.StudentGroupMemberships.AsNoTracking()
            .Where(m => m.GroupId == groupId.Value && m.StudentId != me && !m.IsSuspended && !m.IsCancelled)
            .Select(m => m.StudentId)
            .ToListAsync();
        if (ids.Count == 0) return Ok(Array.Empty<object>());

        var names = await NamesAsync(ids);
        var list = names.Select(kv => new { id = kv.Key, name = kv.Value });
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            list = list.Where(x => x.name.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        return Ok(list.OrderBy(x => x.name).Take(60));
    }

    // ───────────────────────── create ─────────────────────────

    [HttpPost]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Create([FromBody] CreateChallengeRequest request)
    {
        var me = StudentId;
        var tenantId = _tenant.CurrentTenantId;
        if (!tenantId.HasValue) return Forbid();
        if (request.OpponentId <= 0 || request.OpponentId == me)
            return BadRequest(new { message = "Pick a classmate to challenge." });

        var count = request.QuestionCount ?? DefaultQuestions;
        if (count < MinQuestions || count > MaxQuestions)
            return BadRequest(new { message = $"Question count must be between {MinQuestions} and {MaxQuestions}." });

        // The opponent has to be an active classmate (same group of this teacher).
        var groupId = User.GetGroupId(tenantId);
        var isClassmate = groupId.HasValue && await _db.StudentGroupMemberships.AsNoTracking()
            .AnyAsync(m => m.GroupId == groupId.Value && m.StudentId == request.OpponentId && !m.IsSuspended && !m.IsCancelled);
        if (!isClassmate) return BadRequest(new { message = "You can only challenge students of your own group." });

        var now = DateTime.UtcNow;
        var dayStart = now - (PushTargets.ToCairo(now) - PushTargets.ToCairo(now).Date);

        var open = await _db.StudentChallenges.AsNoTracking()
            .CountAsync(c => (c.ChallengerId == me || c.OpponentId == me) && (c.Status == "Pending" || c.Status == "Active"));
        if (open >= MaxOpenChallenges)
            return BadRequest(new { message = $"You already have {MaxOpenChallenges} open challenges. Finish one first." });

        var today = await _db.StudentChallenges.AsNoTracking()
            .CountAsync(c => c.ChallengerId == me && c.CreatedAt >= dayStart);
        if (today >= MaxCreatedPerDay)
            return BadRequest(new { message = "You reached today's challenge limit. Try again tomorrow." });

        var duplicate = await _db.StudentChallenges.AsNoTracking()
            .AnyAsync(c => (c.Status == "Pending" || c.Status == "Active")
                           && ((c.ChallengerId == me && c.OpponentId == request.OpponentId)
                               || (c.ChallengerId == request.OpponentId && c.OpponentId == me)));
        if (duplicate) return Conflict(new { message = "There is already an open challenge between you two." });

        // Units both players are subscribed to.
        var myUnits = await StudentAccessHelpers.GetEffectiveUnitIdsAsync(_db, User, me);
        var theirUnits = (await _db.StudentUnitSubscriptions.AsNoTracking()
                .Where(s => s.StudentId == request.OpponentId).Select(s => s.UnitId).ToListAsync()).ToHashSet();
        var shared = myUnits.Where(theirUnits.Contains).ToList();

        int? unitId = request.UnitId;
        int? lessonId = request.LessonId;
        if (lessonId.HasValue)
        {
            var lessonUnit = await _db.Lessons.AsNoTracking().Where(l => l.Id == lessonId.Value)
                .Select(l => (int?)l.UnitId).FirstOrDefaultAsync();
            if (!lessonUnit.HasValue) return NotFound(new { message = "Lesson not found." });
            unitId = lessonUnit;
        }
        List<int> unitScope;
        if (unitId.HasValue)
        {
            if (!shared.Contains(unitId.Value))
                return BadRequest(new { message = "Both of you must be subscribed to the chosen course." });
            unitScope = new List<int> { unitId.Value };
        }
        else
        {
            if (shared.Count == 0)
                return BadRequest(new { message = "You and your classmate have no course in common." });
            unitScope = shared;
        }

        var pool = _db.BankQuestions.AsNoTracking().Where(q =>
            (q.LessonId != null && unitScope.Contains(q.Lesson!.UnitId)) ||
            (q.UnitId != null && unitScope.Contains(q.UnitId!.Value)));
        if (lessonId.HasValue) pool = pool.Where(q => q.LessonId == lessonId.Value);
        if (!string.IsNullOrWhiteSpace(request.Difficulty)) pool = pool.Where(q => q.Difficulty == request.Difficulty);

        // Only auto-gradable questions make a fair duel.
        var candidates = (await pool.Select(q => new { q.Id, q.Type, q.Mark, q.ChoicesCsv }).ToListAsync())
            .Where(q => IsMcq(q.Type) && q.ChoicesCsv.Length > 0 && q.Mark > 0)
            .ToList();
        if (candidates.Count < count)
            return BadRequest(new { message = $"Only {candidates.Count} multiple-choice question(s) match this selection, but {count} were requested." });

        var rnd = new Random();
        var picked = candidates.OrderBy(_ => rnd.Next()).Take(count).ToList();

        var challenge = new StudentChallenge
        {
            TeacherId = tenantId.Value,
            ChallengerId = me,
            OpponentId = request.OpponentId,
            UnitId = unitId,
            LessonId = lessonId,
            Difficulty = string.IsNullOrWhiteSpace(request.Difficulty) ? null : request.Difficulty,
            QuestionIdsCsv = string.Join(',', picked.Select(x => x.Id)),
            TotalMarks = picked.Sum(x => x.Mark),
            Status = "Pending",
            CreatedAt = now,
            ExpiresAt = now + AcceptWindow,
        };
        _db.StudentChallenges.Add(challenge);
        await _db.SaveChangesAsync();

        var names = await NamesAsync(new[] { me, request.OpponentId });
        await _push.SendToStudentsAsync(new[] { request.OpponentId },
            "تحدي جديد ⚔️",
            $"{names.GetValueOrDefault(me, "زميلك")} بيتحداك في {count} أسئلة. تقبل التحدي؟",
            new Dictionary<string, string>
            {
                ["type"] = "challenge", ["id"] = challenge.Id.ToString(), ["teacherId"] = tenantId.Value.ToString(),
            });

        return Ok(ToDto(challenge, names, me));
    }

    // ───────────────────────── read ─────────────────────────

    [HttpGet]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Mine([FromQuery] string? status, [FromQuery] int p = 1)
    {
        p = Math.Max(1, p);
        var me = StudentId;

        // Settle anything that timed out before listing it.
        var stale = await _db.StudentChallenges
            .Where(c => (c.ChallengerId == me || c.OpponentId == me) && (c.Status == "Pending" || c.Status == "Active"))
            .ToListAsync();
        foreach (var c in stale) await ResolveAsync(c);

        var query = _db.StudentChallenges.AsNoTracking().Where(c => c.ChallengerId == me || c.OpponentId == me);
        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var s = status.Trim();
            query = s.Equals("Completed", StringComparison.OrdinalIgnoreCase)
                ? query.Where(c => c.Status == "Completed" || c.Status == "Declined" || c.Status == "Cancelled" || c.Status == "Expired")
                : query.Where(c => c.Status == s);
        }

        var rows = await query.OrderByDescending(c => c.CreatedAt)
            .Skip((p - 1) * PagingDefaults.PageSize).Take(PagingDefaults.PageSize).ToListAsync();
        var names = await NamesAsync(rows.SelectMany(r => new[] { r.ChallengerId, r.OpponentId }));
        return Ok(rows.Select(r => ToDto(r, names, me)));
    }

    [HttpGet("{id:int}")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Get(int id)
    {
        var c = await LoadMineAsync(id);
        if (c == null) return NotFound(new { message = "Challenge not found." });
        var me = StudentId;
        var names = await NamesAsync(new[] { c.ChallengerId, c.OpponentId });
        var dto = ToDto(c, names, me);

        // Once settled, show the questions with the right answers for review.
        if (c.Status != "Completed") return Ok(new { challenge = dto, review = (object?)null });

        var ids = ParseIds(c.QuestionIdsCsv);
        var qs = await _db.BankQuestions.AsNoTracking().Where(q => ids.Contains(q.Id)).ToListAsync();
        var review = ids.Select(i => qs.FirstOrDefault(q => q.Id == i)).Where(q => q != null)
            .Select(q => new { questionId = q!.Id, text = q.Text, imageUrl = q.ImageUrl, choices = q.Choices, correctAnswer = q.Answer, mark = q.Mark })
            .ToList();
        return Ok(new { challenge = dto, review });
    }

    // ───────────────────────── answer the invitation ─────────────────────────

    [HttpPost("{id:int}/accept")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Accept(int id)
    {
        var c = await LoadMineAsync(id);
        if (c == null) return NotFound(new { message = "Challenge not found." });
        var me = StudentId;
        if (c.OpponentId != me) return Forbid();
        if (c.Status != "Pending") return Conflict(new { message = $"This challenge is {c.Status}." });

        var now = DateTime.UtcNow;
        c.Status = "Active";
        c.AcceptedAt = now;
        c.PlayUntil = now + PlayWindow;
        await _db.SaveChangesAsync();

        var names = await NamesAsync(new[] { c.ChallengerId, c.OpponentId });
        await _push.SendToStudentsAsync(new[] { c.ChallengerId }, "اتقبل التحدي 🔥",
            $"{names.GetValueOrDefault(me, "زميلك")} قبل التحدي. قدامك 24 ساعة.",
            new Dictionary<string, string> { ["type"] = "challenge", ["id"] = c.Id.ToString(), ["teacherId"] = c.TeacherId.ToString() });
        return Ok(ToDto(c, names, me));
    }

    [HttpPost("{id:int}/decline")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Decline(int id)
    {
        var c = await LoadMineAsync(id);
        if (c == null) return NotFound(new { message = "Challenge not found." });
        var me = StudentId;
        if (c.OpponentId != me) return Forbid();
        if (c.Status != "Pending") return Conflict(new { message = $"This challenge is {c.Status}." });

        c.Status = "Declined";
        c.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Declined." });
    }

    [HttpPost("{id:int}/cancel")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Cancel(int id)
    {
        var c = await LoadMineAsync(id);
        if (c == null) return NotFound(new { message = "Challenge not found." });
        if (c.ChallengerId != StudentId) return Forbid();
        if (c.Status != "Pending") return Conflict(new { message = $"This challenge is {c.Status}." });

        c.Status = "Cancelled";
        c.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Cancelled." });
    }

    // ───────────────────────── play ─────────────────────────

    [HttpPost("{id:int}/start")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Start(int id)
    {
        var c = await LoadMineAsync(id);
        if (c == null) return NotFound(new { message = "Challenge not found." });
        if (c.Status != "Active") return Conflict(new { message = $"This challenge is {c.Status}." });

        var me = StudentId;
        var iAmChallenger = c.ChallengerId == me;
        var startedAt = iAmChallenger ? c.ChallengerStartedAt : c.OpponentStartedAt;
        if (startedAt.HasValue)
            return Conflict(new { message = "You already started this challenge. Each player plays once." });

        var now = DateTime.UtcNow;
        if (iAmChallenger) c.ChallengerStartedAt = now; else c.OpponentStartedAt = now;
        await _db.SaveChangesAsync();

        var ids = ParseIds(c.QuestionIdsCsv);
        var qs = await _db.BankQuestions.AsNoTracking().Where(q => ids.Contains(q.Id)).ToListAsync();

        // Same shuffle for the same player (seeded), different between the two players.
        var rnd = new Random(HashCode.Combine(c.Id, me));
        var questions = ids.Select(i => qs.FirstOrDefault(q => q.Id == i)).Where(q => q != null)
            .Select(q => new
            {
                questionId = q!.Id,
                text = q.Text,
                imageUrl = q.ImageUrl,
                choices = q.Choices.OrderBy(_ => rnd.Next()).ToList(),
                mark = q.Mark,
            })
            .ToList();

        return Ok(new { challengeId = c.Id, startedAt = now, playUntil = c.PlayUntil, totalMarks = c.TotalMarks, questions });
    }

    [HttpPost("{id:int}/submit")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Submit(int id, [FromBody] SubmitChallengeRequest request)
    {
        var c = await LoadMineAsync(id);
        if (c == null) return NotFound(new { message = "Challenge not found." });
        var me = StudentId;
        var iAmChallenger = c.ChallengerId == me;

        var startedAt = iAmChallenger ? c.ChallengerStartedAt : c.OpponentStartedAt;
        var finishedAt = iAmChallenger ? c.ChallengerFinishedAt : c.OpponentFinishedAt;
        if (!startedAt.HasValue) return Conflict(new { message = "Start the challenge first." });
        if (finishedAt.HasValue) return Conflict(new { message = "You already submitted this challenge." });
        if (c.Status != "Active")
            return Conflict(new { message = $"This challenge is {c.Status}." });

        var ids = ParseIds(c.QuestionIdsCsv);
        var qs = await _db.BankQuestions.AsNoTracking().Where(q => ids.Contains(q.Id)).ToListAsync();

        var given = (request.Answers ?? new())
            .Where(a => ids.Contains(a.QuestionId))
            .GroupBy(a => a.QuestionId).ToDictionary(g => g.Key, g => g.Last().Answer);

        var score = 0;
        foreach (var q in qs)
            if (given.TryGetValue(q.Id, out var ans) && !string.IsNullOrWhiteSpace(ans) && Norm(ans) == Norm(q.Answer))
                score += q.Mark;

        var now = DateTime.UtcNow;
        var seconds = (int)Math.Clamp((now - startedAt.Value).TotalSeconds, 0, 60 * 60 * 24);

        if (iAmChallenger)
        {
            c.ChallengerFinishedAt = now; c.ChallengerScore = score; c.ChallengerSeconds = seconds;
        }
        else
        {
            c.OpponentFinishedAt = now; c.OpponentScore = score; c.OpponentSeconds = seconds;
        }

        var bothDone = c.ChallengerFinishedAt.HasValue && c.OpponentFinishedAt.HasValue;
        if (bothDone) Finish(c, now);
        await _db.SaveChangesAsync();

        var otherId = iAmChallenger ? c.OpponentId : c.ChallengerId;
        var names = await NamesAsync(new[] { c.ChallengerId, c.OpponentId });
        if (bothDone)
        {
            var title = "نتيجة التحدي 🏁";
            string BodyFor(int viewer)
                => c.WinnerId == null ? "تعادل! مفيش غالب ولا مغلوب."
                    : c.WinnerId == viewer ? "كسبت التحدي 🎉" : "للأسف خسرت المرة دي. جرّب تاني!";
            var data = new Dictionary<string, string> { ["type"] = "challenge", ["id"] = c.Id.ToString(), ["teacherId"] = c.TeacherId.ToString() };
            await _push.SendToStudentsAsync(new[] { c.ChallengerId }, title, BodyFor(c.ChallengerId), data);
            await _push.SendToStudentsAsync(new[] { c.OpponentId }, title, BodyFor(c.OpponentId), data);
        }
        else
        {
            await _push.SendToStudentsAsync(new[] { otherId }, "زميلك خلّص التحدي ⏱️",
                $"{names.GetValueOrDefault(me, "زميلك")} خلّص التحدي. دورك!",
                new Dictionary<string, string> { ["type"] = "challenge", ["id"] = c.Id.ToString(), ["teacherId"] = c.TeacherId.ToString() });
        }

        return Ok(new { challenge = ToDto(c, names, me), score, totalMarks = c.TotalMarks, seconds, completed = bothDone });
    }

    // ───────────────────────── leaderboard ─────────────────────────

    [HttpGet("leaderboard")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Leaderboard()
    {
        var me = StudentId;
        var since = DateTime.UtcNow.AddDays(-30);
        var rows = await _db.StudentChallenges.AsNoTracking()
            .Where(c => c.Status == "Completed" && c.CompletedAt >= since
                        && c.ChallengerFinishedAt != null && c.OpponentFinishedAt != null)
            .Select(c => new { c.ChallengerId, c.OpponentId, c.WinnerId })
            .ToListAsync();

        var board = BuildBoard(rows.Select(r => (r.ChallengerId, r.OpponentId, r.WinnerId)));
        var names = await NamesAsync(board.Select(b => b.StudentId));
        var ranked = board.Take(20).Select((b, i) => new
        {
            rank = i + 1,
            studentId = b.StudentId,
            name = names.GetValueOrDefault(b.StudentId, ""),
            wins = b.Wins, losses = b.Losses, draws = b.Draws,
            isMe = b.StudentId == me,
        }).ToList();

        var mine = board.FindIndex(b => b.StudentId == me);
        return Ok(new { top = ranked, myRank = mine >= 0 ? mine + 1 : (int?)null });
    }

    private sealed record Row(int StudentId, int Wins, int Losses, int Draws);

    private static List<Row> BuildBoard(IEnumerable<(int ChallengerId, int OpponentId, int? WinnerId)> games)
    {
        var wins = new Dictionary<int, int>();
        var losses = new Dictionary<int, int>();
        var draws = new Dictionary<int, int>();
        foreach (var g in games)
        {
            foreach (var p in new[] { g.ChallengerId, g.OpponentId })
            {
                if (g.WinnerId == null) draws[p] = draws.GetValueOrDefault(p) + 1;
                else if (g.WinnerId == p) wins[p] = wins.GetValueOrDefault(p) + 1;
                else losses[p] = losses.GetValueOrDefault(p) + 1;
            }
        }
        return wins.Keys.Concat(losses.Keys).Concat(draws.Keys).Distinct()
            .Select(s => new Row(s, wins.GetValueOrDefault(s), losses.GetValueOrDefault(s), draws.GetValueOrDefault(s)))
            .OrderByDescending(r => r.Wins).ThenBy(r => r.Losses).ThenByDescending(r => r.Draws)
            .ToList();
    }

    // ───────────────────────── teacher ─────────────────────────

    [HttpGet("teacher/summary")]
    [Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.Assistant}")]
    public async Task<IActionResult> TeacherSummary([FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 180);
        var since = DateTime.UtcNow.AddDays(-days);
        var rows = await _db.StudentChallenges.AsNoTracking()
            .Where(c => c.CreatedAt >= since)
            .Select(c => new { c.ChallengerId, c.OpponentId, c.WinnerId, c.Status })
            .ToListAsync();

        var completed = rows.Where(r => r.Status == "Completed").ToList();
        var board = BuildBoard(completed.Select(r => (r.ChallengerId, r.OpponentId, r.WinnerId)));
        var names = await NamesAsync(board.Take(10).Select(b => b.StudentId));
        var players = rows.SelectMany(r => new[] { r.ChallengerId, r.OpponentId }).Distinct().Count();

        return Ok(new
        {
            days,
            total = rows.Count,
            completed = completed.Count,
            pending = rows.Count(r => r.Status == "Pending"),
            active = rows.Count(r => r.Status == "Active"),
            declinedOrExpired = rows.Count(r => r.Status is "Declined" or "Expired" or "Cancelled"),
            uniquePlayers = players,
            top = board.Take(10).Select((b, i) => new
            {
                rank = i + 1, studentId = b.StudentId, name = names.GetValueOrDefault(b.StudentId, ""),
                wins = b.Wins, losses = b.Losses, draws = b.Draws,
            }).ToList(),
        });
    }
}
