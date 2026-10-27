using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record PostCommentRequest(string? Body, int? ParentId, int? VideoSeconds);
public record EditCommentRequest(string Body);

/// <summary>
/// Per-lecture questions &amp; answers (replaces sending lecture questions through "تواصل معنا").
///
/// Route: api/LectureQa  (students AND staff)
///  GET  LectureQa/{lectureId}?p=1               questions (20 per page) with their replies
///  POST LectureQa/{lectureId}                   ask / reply. multipart: Body?, ParentId?, VideoSeconds?, Image?
///                                               (text OR image is required). Old JSON body { body, parentId?, videoSeconds? } still works.
///  PATCH LectureQa/comment/{id}                 body { body }   edit my own
///  POST LectureQa/comment/{id}/delete           author, or staff
///  POST LectureQa/comment/{id}/accept           toggle "best answer" (question author or staff)
///  POST LectureQa/comment/{id}/pin              toggle pin on a question (staff)
///  GET  LectureQa/inbox                         staff: unanswered questions across all lectures
///
/// Access: a student can only read/post on lectures they can actually open
/// (LectureAccess). Anti-spam: a student can post at most 10 comments per hour.
/// Pushes: a new question notifies the teacher; a reply notifies the question's author.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class LectureQaController : ControllerBase
{
    private const int MaxBodyLength = 2000;
    private const int StudentPostsPerHour = 10;
    private const long MaxImageBytes = 8 * 1024 * 1024;
    private const string ImageSubFolder = "lectureqa";

    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp"
    };

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IPushService _push;
    private readonly IFileStorageService _files;

    public LectureQaController(AppDbContext db, ITenantContext tenant, IPushService push, IFileStorageService files)
    {
        _db = db;
        _tenant = tenant;
        _push = push;
        _files = files;
    }

    private static string? ValidateImage(IFormFile? image)
    {
        if (image == null || image.Length == 0) return null;
        if (image.Length > MaxImageBytes) return "Image is too large (max 8 MB).";
        if (!AllowedImageExtensions.Contains(Path.GetExtension(image.FileName) ?? string.Empty))
            return "Only image files are allowed (jpg, png, gif, webp).";
        if (!string.IsNullOrEmpty(image.ContentType) &&
            !image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return "Only image files are allowed (jpg, png, gif, webp).";
        return null;
    }

    private static bool HasImage(IFormFile? image) => image != null && image.Length > 0;

    private bool IsStudent => User.IsInRole(Roles.Student);
    private bool IsStaff => User.IsInRole(Roles.Teacher) || User.IsInRole(Roles.AssistantAdmin)
                            || User.IsInRole(Roles.Assistant) || User.IsInRole(Roles.SuperAdmin);
    private string MyRole => IsStudent ? "Student" : "Staff";
    private int MyId => User.GetUserId();

    // ───────────────────────── list ─────────────────────────

    [HttpGet("{lectureId:int}")]
    public async Task<IActionResult> List(int lectureId, [FromQuery] int p = 1)
    {
        if (!IsStudent && !IsStaff) return Forbid();
        if (await LectureAccess.GetAsync(_db, User, _tenant, lectureId) == null)
            return NotFound(new { message = "Lecture not found." });

        p = Math.Max(1, p);
        var questions = await _db.LectureComments.AsNoTracking()
            .Where(c => c.LectureId == lectureId && c.ParentId == null && !c.IsDeleted)
            .OrderByDescending(c => c.IsPinned).ThenByDescending(c => c.CreatedAt)
            .Skip((p - 1) * PagingDefaults.PageSize).Take(PagingDefaults.PageSize)
            .ToListAsync();

        var qIds = questions.Select(q => q.Id).ToList();
        var replies = await _db.LectureComments.AsNoTracking()
            .Where(c => c.ParentId != null && qIds.Contains(c.ParentId.Value) && !c.IsDeleted)
            .OrderByDescending(c => c.IsAccepted).ThenBy(c => c.CreatedAt)
            .ToListAsync();

        var myRole = MyRole; var myId = MyId;
        object Shape(LectureComment c) => new
        {
            id = c.Id,
            body = c.Body,
            imageUrl = c.ImageUrl,
            authorName = c.AuthorName,
            authorRole = c.AuthorRole,
            isMine = c.AuthorRole == myRole && c.AuthorId == myId,
            videoSeconds = c.VideoSeconds,
            isAccepted = c.IsAccepted,
            isPinned = c.IsPinned,
            createdAt = c.CreatedAt,
            editedAt = c.EditedAt,
        };

        return Ok(questions.Select(q =>
        {
            var rs = replies.Where(r => r.ParentId == q.Id).ToList();
            return new
            {
                id = q.Id,
                body = q.Body,
                imageUrl = q.ImageUrl,
                authorName = q.AuthorName,
                authorRole = q.AuthorRole,
                isMine = q.AuthorRole == myRole && q.AuthorId == myId,
                videoSeconds = q.VideoSeconds,
                isPinned = q.IsPinned,
                createdAt = q.CreatedAt,
                editedAt = q.EditedAt,
                answered = rs.Any(r => r.AuthorRole == "Staff" || r.IsAccepted),
                replies = rs.Select(Shape).ToList(),
            };
        }));
    }

    // ───────────────────────── post ─────────────────────────

    // One action for both body types (a second action with the same route would break Swagger):
    //  - multipart/form-data (new app builds): Body?, ParentId?, VideoSeconds?, Image?
    //  - application/json   (older installed builds): { body, parentId?, videoSeconds? }
    [HttpPost("{lectureId:int}")]
    [Consumes("multipart/form-data", "application/json")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Post(int lectureId)
    {
        string? body;
        int? parentId = null, videoSeconds = null;
        IFormFile? image = null;

        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync();
            body = form["Body"].ToString();
            if (int.TryParse(form["ParentId"].ToString(), out var pid)) parentId = pid;
            if (int.TryParse(form["VideoSeconds"].ToString(), out var vs)) videoSeconds = vs;
            image = form.Files.GetFile("Image");
        }
        else
        {
            PostCommentRequest? req;
            try { req = await Request.ReadFromJsonAsync<PostCommentRequest>(); }
            catch { return BadRequest(new { message = "Invalid request body." }); }
            if (req == null) return BadRequest(new { message = "Invalid request body." });
            body = req.Body;
            parentId = req.ParentId;
            videoSeconds = req.VideoSeconds;
        }

        return await CreateAsync(lectureId, body, parentId, videoSeconds, image);
    }

    private async Task<IActionResult> CreateAsync(int lectureId, string? rawBody, int? parentId, int? videoSeconds, IFormFile? image)
    {
        if (!IsStudent && !IsStaff) return Forbid();

        var lecture = await LectureAccess.GetAsync(_db, User, _tenant, lectureId);
        if (lecture == null) return NotFound(new { message = "Lecture not found." });

        var body = (rawBody ?? string.Empty).Trim();
        var hasImage = HasImage(image);
        if (body.Length == 0 && !hasImage) return BadRequest(new { message = "اكتب سؤالك أو ردك أو أرفق صورة الأول." });
        if (body.Length > MaxBodyLength) return BadRequest(new { message = $"الحد الأقصى {MaxBodyLength} حرف." });

        var imageError = ValidateImage(image);
        if (imageError != null) return BadRequest(new { message = imageError });

        if (IsStudent)
        {
            var since = DateTime.UtcNow.AddHours(-1);
            var recent = await _db.LectureComments.CountAsync(c =>
                c.AuthorRole == "Student" && c.AuthorId == MyId && c.CreatedAt >= since);
            if (recent >= StudentPostsPerHour)
                return StatusCode(429, new { message = "بعتّ رسائل كتير في وقت قصير، جرّب بعد شوية." });
        }

        LectureComment? parent = null;
        if (parentId.HasValue)
        {
            parent = await _db.LectureComments.FirstOrDefaultAsync(c =>
                c.Id == parentId.Value && c.LectureId == lectureId && !c.IsDeleted);
            if (parent == null) return NotFound(new { message = "السؤال غير موجود." });
            if (parent.ParentId != null) return BadRequest(new { message = "مينفعش ترد على رد." });
        }

        var authorName = await ResolveNameAsync();

        string? imageUrl = null;
        if (hasImage)
            imageUrl = await _files.SaveAsync(image!, ImageSubFolder);

        var comment = new LectureComment
        {
            TeacherId = lecture.TeacherId,
            LectureId = lectureId,
            ParentId = parent?.Id,
            AuthorRole = MyRole,
            AuthorId = MyId,
            AuthorName = authorName,
            Body = body,
            ImageUrl = imageUrl,
            VideoSeconds = parent == null ? videoSeconds : null,
        };

        try
        {
            _db.LectureComments.Add(comment);
            await _db.SaveChangesAsync();
        }
        catch
        {
            await _files.DeleteAsync(imageUrl); // don't leave an orphan upload behind
            throw;
        }

        // PUSH (best effort)
        try
        {
            var data = new Dictionary<string, string>
            {
                ["type"] = "qa", ["id"] = lectureId.ToString(), ["teacherId"] = lecture.TeacherId.ToString(),
            };
            var preview = body.Length == 0 ? "📷 صورة" : Preview(body);
            if (parent == null && IsStudent)
            {
                await _push.SendToStaffAsync(new[] { lecture.TeacherId },
                    $"سؤال جديد في: {lecture.Name}", preview, data);
            }
            else if (parent != null && !(parent.AuthorRole == MyRole && parent.AuthorId == MyId) && parent.AuthorRole == "Student")
            {
                await _push.SendToStudentsAsync(new[] { parent.AuthorId },
                    IsStaff ? "المدرس رد على سؤالك 💬" : $"{authorName} رد على سؤالك 💬",
                    preview, data);
            }
        }
        catch { /* never fail the post because of a push */ }

        return StatusCode(201, new { id = comment.Id, imageUrl = comment.ImageUrl, message = "تم النشر." });
    }

    private static string Preview(string s) => s.Length <= 90 ? s : s[..90] + "…";

    private async Task<string> ResolveNameAsync()
    {
        string? name;
        if (IsStudent)
            name = await _db.Students.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.Id == MyId).Select(s => s.Name).FirstOrDefaultAsync();
        else
            name = await _db.Teachers.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == MyId).Select(t => t.Name).FirstOrDefaultAsync();

        name ??= IsStudent ? "طالب" : "المدرس";
        return name.Length > 120 ? name[..120] : name;
    }

    // ───────────────────────── edit / delete ─────────────────────────

    [HttpPatch("comment/{id:int}")]
    public async Task<IActionResult> Edit(int id, [FromBody] EditCommentRequest request)
    {
        var c = await _db.LectureComments.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        if (c == null) return NotFound(new { message = "Not found." });
        if (c.AuthorRole != MyRole || c.AuthorId != MyId) return Forbid();

        var body = (request.Body ?? string.Empty).Trim();
        if (body.Length > MaxBodyLength || (body.Length == 0 && string.IsNullOrEmpty(c.ImageUrl)))
            return BadRequest(new { message = "نص غير صالح." });

        c.Body = body;
        c.EditedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { message = "تم التعديل." });
    }

    [HttpPost("comment/{id:int}/delete")]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await _db.LectureComments.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        if (c == null) return NotFound(new { message = "Not found." });
        var mine = c.AuthorRole == MyRole && c.AuthorId == MyId;
        if (!mine && !IsStaff) return Forbid();

        // Collect the uploaded images (the comment itself + its replies when it's a question) to clean up.
        var imageUrls = new List<string?> { c.ImageUrl };
        if (c.ParentId == null)
            imageUrls.AddRange(await _db.LectureComments.Where(x => x.ParentId == c.Id).Select(x => x.ImageUrl).ToListAsync());

        c.IsDeleted = true;
        c.IsPinned = false;
        c.IsAccepted = false;
        if (c.ParentId == null)
        {
            // Deleting a question hides its whole thread.
            await _db.LectureComments.Where(x => x.ParentId == c.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDeleted, true));
        }
        await _db.SaveChangesAsync();

        foreach (var url in imageUrls.Where(u => !string.IsNullOrEmpty(u)))
        {
            try { await _files.DeleteAsync(url); } catch { /* cleanup is best effort */ }
        }
        return Ok(new { message = "تم الحذف." });
    }

    // ───────────────────────── accept / pin ─────────────────────────

    [HttpPost("comment/{id:int}/accept")]
    public async Task<IActionResult> Accept(int id)
    {
        var reply = await _db.LectureComments.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted && x.ParentId != null);
        if (reply == null) return NotFound(new { message = "Not found." });

        var question = await _db.LectureComments.FirstOrDefaultAsync(x => x.Id == reply.ParentId && !x.IsDeleted);
        if (question == null) return NotFound(new { message = "Not found." });

        var isQuestionAuthor = question.AuthorRole == MyRole && question.AuthorId == MyId;
        if (!isQuestionAuthor && !IsStaff) return Forbid();

        // Students cannot accept their own reply to their own question (no self-farming points).
        if (reply.AuthorRole == "Student" && reply.AuthorId == question.AuthorId && question.AuthorRole == "Student")
            return BadRequest(new { message = "مينفعش تقبل ردك أنت." });

        var newValue = !reply.IsAccepted;
        if (newValue)
        {
            // one accepted answer per question
            await _db.LectureComments.Where(x => x.ParentId == question.Id && x.IsAccepted)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsAccepted, false));
        }
        reply.IsAccepted = newValue;
        await _db.SaveChangesAsync();
        return Ok(new { isAccepted = reply.IsAccepted });
    }

    [HttpPost("comment/{id:int}/pin")]
    public async Task<IActionResult> Pin(int id)
    {
        if (!IsStaff) return Forbid();
        var q = await _db.LectureComments.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted && x.ParentId == null);
        if (q == null) return NotFound(new { message = "Not found." });
        q.IsPinned = !q.IsPinned;
        await _db.SaveChangesAsync();
        return Ok(new { isPinned = q.IsPinned });
    }

    // ───────────────────────── staff inbox ─────────────────────────

    [HttpGet("inbox")]
    public async Task<IActionResult> Inbox([FromQuery] int p = 1)
    {
        if (!IsStaff) return Forbid();
        p = Math.Max(1, p);

        var questions = await _db.LectureComments.AsNoTracking()
            .Where(c => c.ParentId == null && !c.IsDeleted && c.AuthorRole == "Student"
                        && !_db.LectureComments.Any(r => r.ParentId == c.Id && !r.IsDeleted && r.AuthorRole == "Staff"))
            .OrderByDescending(c => c.CreatedAt)
            .Skip((p - 1) * PagingDefaults.PageSize).Take(PagingDefaults.PageSize)
            .ToListAsync();

        var lectureIds = questions.Select(q => q.LectureId).Distinct().ToList();
        var names = await _db.Lectures.AsNoTracking()
            .Where(l => lectureIds.Contains(l.Id)).Select(l => new { l.Id, l.Name })
            .ToDictionaryAsync(l => l.Id, l => l.Name);

        return Ok(questions.Select(q => new
        {
            id = q.Id,
            lectureId = q.LectureId,
            lectureName = names.GetValueOrDefault(q.LectureId, ""),
            body = q.Body,
            imageUrl = q.ImageUrl,
            authorName = q.AuthorName,
            videoSeconds = q.VideoSeconds,
            createdAt = q.CreatedAt,
        }));
    }
}
