using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record ClusterItemRef(string Source, int Id);
public record ClusterReplyRequest(List<ClusterItemRef>? Items, string? Body);

/// <summary>
/// "تجميع أسئلة الطلبة المتشابهة": the teacher gets ONE card per question that many students asked
/// (in different words), and can answer them all with one reply.
///
/// Sources: lecture Q&amp;A questions ("qa") and "تواصل معنا" messages ("contact").
/// Grouping = plain text similarity (Arabic normalisation + TF-IDF cosine), no AI and no cost.
/// Two questions on the SAME lecture need less overlap to be grouped than questions on different ones.
///
/// Route: api/QuestionClusters  (teacher / assistants)
///  GET  QuestionClusters?source=all|qa|contact&amp;lectureId=&amp;days=30&amp;minSize=2&amp;onlyUnanswered=true
///  POST QuestionClusters/reply    body { items: [{ source, id }], body }   (max 50 items)
///       answers every item with the same text: a staff reply in the Q&amp;A thread, or a teacher
///       message in the contact thread, and pushes the students
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin},{Roles.Assistant}")]
public class QuestionClustersController : ControllerBase
{
    private const double SimilarityThreshold = 0.33;
    /// <summary>A match on a single shared word is not enough unless the similarity is already high.</summary>
    private const int MinSharedWords = 2;
    private const double StrongSimilarity = 0.60;
    private const double SameLectureBonus = 0.08;
    private const int MaxItems = 1500;
    private const int MaxReplyItems = 50;
    private const int MaxBodyLength = 2000;

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IPushService _push;

    public QuestionClustersController(AppDbContext db, ITenantContext tenant, IPushService push)
    {
        _db = db;
        _tenant = tenant;
        _push = push;
    }

    private sealed class Item
    {
        public string Source { get; init; } = "qa";
        public int Id { get; init; }
        public int StudentId { get; init; }
        public int? LectureId { get; init; }
        public int? VideoSeconds { get; init; }
        public string Text { get; init; } = "";
        public string? ImageUrl { get; init; }
        public DateTime CreatedAt { get; init; }
        public bool Answered { get; init; }
        public List<string> Tokens { get; set; } = new();
        public Dictionary<string, double> Vector { get; set; } = new();
    }

    private sealed class Cluster
    {
        public List<Item> Items { get; } = new();
        public Dictionary<string, double> Centroid { get; set; } = new();
    }

    private static string Short(string s, int max = 200) => s.Length <= max ? s : s[..max] + "…";

    // ───────────────────────── list ─────────────────────────

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? source, [FromQuery] int? lectureId, [FromQuery] int days = 30,
        [FromQuery] int minSize = 2, [FromQuery] bool onlyUnanswered = true)
    {
        days = Math.Clamp(days, 1, 180);
        minSize = Math.Clamp(minSize, 2, 20);
        var since = DateTime.UtcNow.AddDays(-days);
        var src = (source ?? "all").Trim().ToLowerInvariant();

        var items = new List<Item>();

        if (src is "all" or "qa")
        {
            var qQuery = _db.LectureComments.AsNoTracking()
                .Where(c => c.ParentId == null && !c.IsDeleted && c.AuthorRole == "Student" && c.CreatedAt >= since);
            if (lectureId.HasValue) qQuery = qQuery.Where(c => c.LectureId == lectureId.Value);
            var questions = await qQuery.OrderByDescending(c => c.CreatedAt).Take(MaxItems).ToListAsync();

            var qIds = questions.Select(q => q.Id).ToList();
            var answered = qIds.Count == 0 ? new HashSet<int>()
                : (await _db.LectureComments.AsNoTracking()
                    .Where(r => r.ParentId != null && qIds.Contains(r.ParentId.Value) && !r.IsDeleted && r.AuthorRole == "Staff")
                    .Select(r => r.ParentId!.Value).Distinct().ToListAsync()).ToHashSet();

            items.AddRange(questions.Select(q => new Item
            {
                Source = "qa", Id = q.Id, StudentId = q.AuthorId, LectureId = q.LectureId,
                VideoSeconds = q.VideoSeconds, Text = q.Body ?? "", ImageUrl = q.ImageUrl,
                CreatedAt = q.CreatedAt, Answered = answered.Contains(q.Id),
            }));
        }

        if ((src is "all" or "contact") && !lectureId.HasValue)
        {
            var threads = await _db.ContactMessages.AsNoTracking().Include(m => m.Replies)
                .Where(m => m.CreatedAt >= since)
                .OrderByDescending(m => m.CreatedAt).Take(MaxItems).ToListAsync();
            items.AddRange(threads.Select(m => new Item
            {
                Source = "contact", Id = m.Id, StudentId = m.StudentId, LectureId = null,
                Text = m.Text ?? "", ImageUrl = m.ImageUrl, CreatedAt = m.CreatedAt,
                Answered = m.Replies.Any(r => r.IsFromTeacher),
            }));
        }

        if (items.Count == 0)
            return Ok(new { total = 0, clusters = Array.Empty<object>(), singles = 0 });

        // ───── vectors ─────
        foreach (var it in items) it.Tokens = ArabicText.Tokens(it.Text);
        var vectors = ArabicText.TfIdf(items.Select(i => i.Tokens).ToList());
        for (var i = 0; i < items.Count; i++) items[i].Vector = vectors[i];

        // ───── leader clustering (oldest first so the first asker defines the cluster) ─────
        var clusters = new List<Cluster>();
        var singles = 0;
        foreach (var it in items.OrderBy(i => i.CreatedAt))
        {
            if (it.Tokens.Count < 2) { singles++; continue; }

            Cluster? best = null; var bestScore = 0.0;
            foreach (var c in clusters)
            {
                var score = ArabicText.Cosine(it.Vector, c.Centroid);
                var shared = c.Items.Max(x => x.Tokens.Intersect(it.Tokens).Count());
                if (shared < MinSharedWords && score < StrongSimilarity) continue;
                if (it.LectureId.HasValue && c.Items.Any(x => x.LectureId == it.LectureId)) score += SameLectureBonus;
                if (score > bestScore) { bestScore = score; best = c; }
            }

            if (best != null && bestScore >= SimilarityThreshold)
            {
                best.Items.Add(it);
                var sum = new Dictionary<string, double>();
                foreach (var m in best.Items)
                    foreach (var kv in m.Vector) sum[kv.Key] = sum.GetValueOrDefault(kv.Key) + kv.Value;
                ArabicText.Normalise(sum);
                best.Centroid = sum;
            }
            else
            {
                var c = new Cluster { Centroid = new Dictionary<string, double>(it.Vector) };
                c.Items.Add(it);
                clusters.Add(c);
            }
        }

        var grouped = clusters.Where(c => c.Items.Count >= minSize).ToList();
        singles += clusters.Where(c => c.Items.Count < minSize).Sum(c => c.Items.Count);
        if (onlyUnanswered) grouped = grouped.Where(c => c.Items.Any(i => !i.Answered)).ToList();

        // ───── names ─────
        var studentIds = grouped.SelectMany(c => c.Items.Select(i => i.StudentId)).Distinct().ToList();
        var studentNames = await _db.Students.IgnoreQueryFilters().AsNoTracking()
            .Where(s => studentIds.Contains(s.Id)).Select(s => new { s.Id, s.Name })
            .ToDictionaryAsync(s => s.Id, s => s.Name);
        var lectureIds = grouped.SelectMany(c => c.Items.Where(i => i.LectureId.HasValue).Select(i => i.LectureId!.Value)).Distinct().ToList();
        var lectureNames = await _db.Lectures.AsNoTracking()
            .Where(l => lectureIds.Contains(l.Id)).Select(l => new { l.Id, l.Name })
            .ToDictionaryAsync(l => l.Id, l => l.Name);

        var result = grouped
            .OrderByDescending(c => c.Items.Count(i => !i.Answered))
            .ThenByDescending(c => c.Items.Count)
            .Take(50)
            .Select(c =>
            {
                var ordered = c.Items.OrderBy(i => i.CreatedAt).ToList();
                var first = ordered[0];
                var unanswered = ordered.Count(i => !i.Answered);
                var lecIds = ordered.Where(i => i.LectureId.HasValue).Select(i => i.LectureId!.Value).Distinct().ToList();
                var keywords = c.Centroid.OrderByDescending(kv => kv.Value).Take(4).Select(kv => kv.Key).ToList();

                return new
                {
                    id = $"{first.Source}:{first.Id}",
                    size = ordered.Count,
                    students = ordered.Select(i => i.StudentId).Distinct().Count(),
                    unanswered,
                    keywords,
                    sample = Short(first.Text),
                    firstAskedAt = first.CreatedAt,
                    lastAskedAt = ordered[^1].CreatedAt,
                    lectures = lecIds.Select(l => new { id = l, name = lectureNames.GetValueOrDefault(l, "") }).ToList(),
                    suggestion = ordered.Count >= 5
                        ? "أكتر من 5 طلبة سألوا نفس السؤال. يستاهل تشرحه في الحصة أو تنزّل فيديو قصير."
                        : "رد عليهم كلهم مرة واحدة من هنا.",
                    items = ordered.Select(i => new
                    {
                        source = i.Source,
                        id = i.Id,
                        studentId = i.StudentId,
                        studentName = studentNames.GetValueOrDefault(i.StudentId, ""),
                        lectureId = i.LectureId,
                        videoSeconds = i.VideoSeconds,
                        text = Short(i.Text, 300),
                        imageUrl = i.ImageUrl,
                        createdAt = i.CreatedAt,
                        answered = i.Answered,
                    }).ToList(),
                };
            })
            .ToList();

        return Ok(new { total = items.Count, clusters = result, singles });
    }

    // ───────────────────────── one reply to many ─────────────────────────

    [HttpPost("reply")]
    public async Task<IActionResult> Reply([FromBody] ClusterReplyRequest request)
    {
        var body = (request.Body ?? string.Empty).Trim();
        if (body.Length == 0) return BadRequest(new { message = "اكتب الرد الأول." });
        if (body.Length > MaxBodyLength) return BadRequest(new { message = $"الحد الأقصى {MaxBodyLength} حرف." });

        var refs = (request.Items ?? new()).Take(MaxReplyItems)
            .Where(i => i.Id > 0 && (i.Source == "qa" || i.Source == "contact"))
            .GroupBy(i => (i.Source, i.Id)).Select(g => g.First()).ToList();
        if (refs.Count == 0) return BadRequest(new { message = "No questions selected." });

        var tenantId = _tenant.CurrentTenantId;
        if (!tenantId.HasValue) return Forbid();
        var staffId = User.GetUserId();
        var staffName = await _db.Teachers.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Id == staffId).Select(t => t.Name).FirstOrDefaultAsync() ?? "المدرس";
        if (staffName.Length > 120) staffName = staffName[..120];

        var now = DateTime.UtcNow;
        var pushQa = new List<(int StudentId, int LectureId)>();
        var pushContact = new List<int>();
        var replied = 0;

        // ───── lecture Q&A ─────
        var qaIds = refs.Where(r => r.Source == "qa").Select(r => r.Id).ToList();
        if (qaIds.Count > 0)
        {
            var questions = await _db.LectureComments
                .Where(c => qaIds.Contains(c.Id) && c.ParentId == null && !c.IsDeleted && c.AuthorRole == "Student")
                .ToListAsync();
            foreach (var q in questions)
            {
                _db.LectureComments.Add(new LectureComment
                {
                    TeacherId = q.TeacherId, LectureId = q.LectureId, ParentId = q.Id,
                    AuthorRole = "Staff", AuthorId = staffId, AuthorName = staffName,
                    Body = body, CreatedAt = now,
                });
                pushQa.Add((q.AuthorId, q.LectureId));
                replied++;
            }
        }

        // ───── contact threads ─────
        var contactIds = refs.Where(r => r.Source == "contact").Select(r => r.Id).ToList();
        if (contactIds.Count > 0)
        {
            var threads = await _db.ContactMessages.Where(m => contactIds.Contains(m.Id)).ToListAsync();
            foreach (var m in threads)
            {
                _db.ContactReplies.Add(new ContactReply
                {
                    ContactMessageId = m.Id, TeacherId = m.TeacherId, IsFromTeacher = true, Text = body, CreatedAt = now,
                });
                m.LastActivityAt = now;
                m.IsReadByTeacher = true;
                pushContact.Add(m.StudentId);
                replied++;
            }
        }

        if (replied == 0) return NotFound(new { message = "Those questions were not found." });
        await _db.SaveChangesAsync();

        // PUSH (best effort; never fail the reply because of it)
        try
        {
            var preview = body.Length <= 90 ? body : body[..90] + "…";
            foreach (var g in pushQa.GroupBy(x => x.LectureId))
                await _push.SendToStudentsAsync(g.Select(x => x.StudentId).Distinct(),
                    "المدرس رد على سؤالك 💬", preview,
                    new Dictionary<string, string> { ["type"] = "qa", ["id"] = g.Key.ToString(), ["teacherId"] = tenantId.Value.ToString() });
            if (pushContact.Count > 0)
                await _push.SendToStudentsAsync(pushContact.Distinct(),
                    "رد جديد من المدرس 💬", preview,
                    new Dictionary<string, string> { ["type"] = "contact", ["id"] = "0", ["teacherId"] = tenantId.Value.ToString() });
        }
        catch { /* ignore */ }

        return Ok(new { replied });
    }
}
