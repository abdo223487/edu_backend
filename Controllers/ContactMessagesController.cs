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
/// STUDENT &lt;-&gt; TEACHER CONTACT. Route: api/ContactMessages.
///
/// A student opens a conversation (text + optional image) with the teacher
/// they're currently under (X-TenantId). Then BOTH sides can post any number of
/// follow-up messages (ContactReply, text + optional image). Each side can edit
/// or delete ONLY what they wrote themselves; the student can also delete the
/// whole thread (the opening question).
/// Everything is tenant-scoped through ContactMessage's global query filter,
/// so a student can never touch another teacher's inbox even with a forged
/// X-TenantId header (plus every student endpoint also filters by the caller's
/// own StudentId).
///
/// The opening question can optionally point at a SolutionSource ("مصدر حل" the
/// teacher registered for the student's school year) + a page + a question number.
///
/// All mutating endpoints are POST (same convention as Students/delete etc.)
/// and multipart/form-data where an image may be attached, because the
/// Flutter client's multipart helper only supports POST.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ContactMessagesController : ControllerBase
{
    private const string StaffRoles = $"{Roles.Teacher},{Roles.AssistantAdmin}";
    private const string AllRoles = $"{Roles.Student},{Roles.Teacher},{Roles.AssistantAdmin}";
    private const int MaxTextLength = 5000;
    private const int MaxRefLength = 30; // page / question number
    private const long MaxImageBytes = 8 * 1024 * 1024;
    private const string ImageSubFolder = "contact";

    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp"
    };

    private readonly AppDbContext _db;
    private readonly IFileStorageService _files;
    private readonly ITenantContext _tenant;

    public ContactMessagesController(AppDbContext db, IFileStorageService files, ITenantContext tenant)
    {
        _db = db;
        _files = files;
        _tenant = tenant;
    }

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>Returns an error message, or null when the text is valid.</summary>
    private static string? ValidateText(string? text, out string trimmed)
    {
        trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "Text is required.";
        if (trimmed.Length > MaxTextLength) return $"Text is too long (max {MaxTextLength} characters).";
        return null;
    }

    /// <summary>Returns an error message, or null when there's no image or it's a valid one.</summary>
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

    /// <summary>
    /// Validates the optional source/page/question of an opening question (student
    /// only). The source must exist for THIS teacher (tenant filter) and be for the
    /// caller's own school year. Page/question are only kept when a source is chosen.
    /// </summary>
    private async Task<(string? Error, int? SourceId, string? Page, string? Question)> ResolveSourceAsync(ContactMessageForm form)
    {
        if (form.SolutionSourceId == null || form.SolutionSourceId <= 0)
            return (null, null, null, null);

        var source = await _db.SolutionSources.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == form.SolutionSourceId.Value);
        if (source == null) return ("Solution source not found.", null, null, null);
        var years = await _db.GetStudentSchoolYearsAsync(User);
        if (!years.Contains(source.SchoolYear))
            return ("This solution source is not for your school year.", null, null, null);

        var page = (form.PageNumber ?? string.Empty).Trim();
        var question = (form.QuestionNumber ?? string.Empty).Trim();
        if (page.Length > MaxRefLength || question.Length > MaxRefLength)
            return ($"Page / question number is too long (max {MaxRefLength} characters).", null, null, null);

        return (null, source.Id,
            page.Length == 0 ? null : page,
            question.Length == 0 ? null : question);
    }

    private static object ToDto(ContactMessage m, string? studentName, string? groupName)
    {
        var replies = m.Replies.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToList();
        var lastReply = replies.LastOrDefault();

        return new
        {
            id = m.Id,
            studentId = m.StudentId,
            studentName,
            groupName,
            text = m.Text,
            imageUrl = m.ImageUrl,
            createdAt = m.CreatedAt,
            editedAt = m.EditedAt,
            lastActivityAt = m.LastActivityAt,
            isRead = m.IsReadByTeacher,
            isAnswered = replies.Any(r => r.IsFromTeacher),

            // optional solution source + page + question number
            solutionSourceId = m.SolutionSourceId,
            solutionSourceName = m.SolutionSource?.Name,
            pageNumber = m.PageNumber,
            questionNumber = m.QuestionNumber,

            // The NEWEST message of the thread (last reply, or the opening question
            // when nobody replied yet) -- list cards show this instead of always
            // showing the very first question.
            lastText = lastReply?.Text ?? m.Text,
            lastImageUrl = lastReply != null ? lastReply.ImageUrl : m.ImageUrl,
            lastFromTeacher = lastReply?.IsFromTeacher ?? false,
            lastIsReply = lastReply != null,
            lastMessageAt = lastReply?.CreatedAt ?? m.CreatedAt,

            replies = replies
                .Select(r => new
                {
                    id = r.Id,
                    fromTeacher = r.IsFromTeacher,
                    text = r.Text,
                    imageUrl = r.ImageUrl,
                    createdAt = r.CreatedAt,
                    editedAt = r.EditedAt
                })
                .ToList()
        };
    }

    /// <summary>Fresh, untracked copy of a thread (with replies) shaped for the caller's role.</summary>
    private async Task<object?> BuildDtoAsync(int messageId, bool forStudent)
    {
        var m = await _db.ContactMessages.AsNoTracking()
            .Include(x => x.Replies)
            .Include(x => x.SolutionSource)
            .FirstOrDefaultAsync(x => x.Id == messageId);
        if (m == null) return null;
        if (forStudent) return ToDto(m, null, null);

        var name = await _db.Students.AsNoTracking()
            .Where(s => s.Id == m.StudentId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync();
        var group = await _db.GetTenantGroupNameAsync(m.StudentId);
        return ToDto(m, name, group);
    }

    // ───────────────────────── student side ─────────────────────────

    // GET ContactMessages/mine?status=all|pending|answered&p=1
    [HttpGet("mine")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Mine([FromQuery] string? status, [FromQuery] int p = 1)
    {
        if (_tenant.CurrentTenantId == null) return BadRequest(new { message = "Teacher could not be resolved." });
        if (p < 1) p = 1;

        var studentId = User.GetUserId();
        var query = _db.ContactMessages.AsNoTracking()
            .Include(m => m.Replies)
            .Include(m => m.SolutionSource)
            .Where(m => m.StudentId == studentId);

        query = (status ?? "all").Trim().ToLowerInvariant() switch
        {
            "pending" => query.Where(m => !m.Replies.Any(r => r.IsFromTeacher)),
            "answered" => query.Where(m => m.Replies.Any(r => r.IsFromTeacher)),
            _ => query
        };

        var page = await query
            .OrderByDescending(m => m.LastActivityAt)
            .ThenByDescending(m => m.Id)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .ToListAsync();

        return Ok(page.Select(m => ToDto(m, null, null)).ToList());
    }

    // POST ContactMessages   (multipart: Text, Image?, SolutionSourceId?, PageNumber?, QuestionNumber?)
    [HttpPost]
    [Authorize(Roles = Roles.Student)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Create([FromForm] ContactMessageForm form)
    {
        var tenantId = _tenant.CurrentTenantId;
        if (tenantId == null) return BadRequest(new { message = "Teacher could not be resolved." });

        var textError = ValidateText(form.Text, out var text);
        if (textError != null) return BadRequest(new { message = textError });
        var imageError = ValidateImage(form.Image);
        if (imageError != null) return BadRequest(new { message = imageError });

        var src = await ResolveSourceAsync(form);
        if (src.Error != null) return BadRequest(new { message = src.Error });

        var studentId = User.GetUserId();

        // StudentGroupMemberships is tenant-filtered, so this only ever sees the
        // caller's relationship with THIS teacher.
        var membership = await _db.StudentGroupMemberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.StudentId == studentId);
        if (membership == null) return NotFound(new { message = "You are not linked to this teacher." });
        if (membership.IsCancelled) return StatusCode(403, new { message = "Your account with this teacher is cancelled." });

        string? imageUrl = null;
        if (HasImage(form.Image))
            imageUrl = await _files.SaveAsync(form.Image!, ImageSubFolder);

        var msg = new ContactMessage
        {
            TeacherId = tenantId.Value,
            StudentId = studentId,
            Text = text,
            ImageUrl = imageUrl,
            SolutionSourceId = src.SourceId,
            PageNumber = src.Page,
            QuestionNumber = src.Question,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
            IsReadByTeacher = false
        };

        try
        {
            _db.ContactMessages.Add(msg);
            await _db.SaveChangesAsync();
        }
        catch
        {
            await _files.DeleteAsync(imageUrl); // don't leave an orphan upload behind
            throw;
        }

        return Ok(await BuildDtoAsync(msg.Id, forStudent: true));
    }

    // POST ContactMessages/{id}/edit   (multipart: Text, Image?, RemoveImage?, SolutionSourceId?, PageNumber?, QuestionNumber?)
    //   -- student edits own question. Source/page/question REPLACE the stored values (omitted = cleared).
    [HttpPost("{id:int}/edit")]
    [Authorize(Roles = Roles.Student)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> EditQuestion(int id, [FromForm] ContactMessageForm form)
    {
        var textError = ValidateText(form.Text, out var text);
        if (textError != null) return BadRequest(new { message = textError });
        var imageError = ValidateImage(form.Image);
        if (imageError != null) return BadRequest(new { message = imageError });

        var src = await ResolveSourceAsync(form);
        if (src.Error != null) return BadRequest(new { message = src.Error });

        var studentId = User.GetUserId();
        var msg = await _db.ContactMessages.FirstOrDefaultAsync(m => m.Id == id && m.StudentId == studentId);
        if (msg == null) return NotFound(new { message = "Message not found." });

        var oldImageUrl = msg.ImageUrl;
        string? newImageUrl = null;

        if (HasImage(form.Image))
        {
            newImageUrl = await _files.SaveAsync(form.Image!, ImageSubFolder);
            msg.ImageUrl = newImageUrl;
        }
        else if (form.RemoveImage)
        {
            msg.ImageUrl = null;
        }

        msg.Text = text;
        msg.SolutionSourceId = src.SourceId;
        msg.PageNumber = src.Page;
        msg.QuestionNumber = src.Question;
        msg.EditedAt = DateTime.UtcNow;
        msg.LastActivityAt = DateTime.UtcNow;
        // The teacher should notice that the question changed after they read it.
        msg.IsReadByTeacher = false;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch
        {
            await _files.DeleteAsync(newImageUrl);
            throw;
        }

        if (!string.Equals(oldImageUrl, msg.ImageUrl, StringComparison.Ordinal))
            await _files.DeleteAsync(oldImageUrl);

        return Ok(await BuildDtoAsync(msg.Id, forStudent: true));
    }

    // POST ContactMessages/{id}/delete  -- student deletes the whole thread (question + every reply)
    [HttpPost("{id:int}/delete")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> DeleteQuestion(int id)
    {
        var studentId = User.GetUserId();
        var msg = await _db.ContactMessages.Include(m => m.Replies)
            .FirstOrDefaultAsync(m => m.Id == id && m.StudentId == studentId);
        if (msg == null) return NotFound(new { message = "Message not found." });

        var imageUrls = msg.Replies.Select(r => r.ImageUrl).Append(msg.ImageUrl).ToList();

        _db.ContactMessages.Remove(msg); // replies go with it (cascade)
        await _db.SaveChangesAsync();

        foreach (var url in imageUrls) await _files.DeleteAsync(url);

        return Ok(new { message = "Message deleted." });
    }

    // ───────────────────────── teacher side ─────────────────────────

    // GET ContactMessages/inbox?filter=all|unread|read&p=1
    [HttpGet("inbox")]
    [Authorize(Roles = StaffRoles)]
    public async Task<IActionResult> Inbox([FromQuery] string? filter, [FromQuery] int p = 1)
    {
        if (p < 1) p = 1;

        var query = _db.ContactMessages.AsNoTracking()
            .Include(m => m.Replies)
            .Include(m => m.SolutionSource)
            .AsQueryable();
        query = (filter ?? "all").Trim().ToLowerInvariant() switch
        {
            "unread" => query.Where(m => !m.IsReadByTeacher),
            "read" => query.Where(m => m.IsReadByTeacher),
            _ => query
        };

        var page = await query
            .OrderByDescending(m => m.LastActivityAt)
            .ThenByDescending(m => m.Id)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .ToListAsync();

        var studentIds = page.Select(m => m.StudentId).Distinct().ToList();
        var names = await _db.Students.AsNoTracking()
            .Where(s => studentIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name })
            .ToDictionaryAsync(s => s.Id, s => s.Name);
        var groups = await _db.GetTenantGroupNamesAsync(studentIds);

        return Ok(page.Select(m => ToDto(m, names.GetValueOrDefault(m.StudentId), groups.GetValueOrDefault(m.StudentId))).ToList());
    }

    // GET ContactMessages/unread-count  -> plain int (same shape as Students/count)
    [HttpGet("unread-count")]
    [Authorize(Roles = StaffRoles)]
    public async Task<IActionResult> UnreadCount()
        => Ok(await _db.ContactMessages.AsNoTracking().CountAsync(m => !m.IsReadByTeacher));

    // POST ContactMessages/{id}/read  -- the teacher opened the message
    [HttpPost("{id:int}/read")]
    [Authorize(Roles = StaffRoles)]
    public async Task<IActionResult> MarkRead(int id)
    {
        var msg = await _db.ContactMessages.FirstOrDefaultAsync(m => m.Id == id);
        if (msg == null) return NotFound(new { message = "Message not found." });

        if (!msg.IsReadByTeacher)
        {
            msg.IsReadByTeacher = true;
            await _db.SaveChangesAsync();
        }
        return Ok(new { message = "Marked as read." });
    }

    // ───────────────────────── conversation (both sides) ─────────────────────────

    // POST ContactMessages/{id}/replies   (multipart: Text, Image?)
    // A follow-up message in the thread: from the teacher when called by staff,
    // from the student when called by the thread's own student.
    [HttpPost("{id:int}/replies")]
    [Authorize(Roles = AllRoles)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> AddReply(int id, [FromForm] ContactMessageForm form)
    {
        var textError = ValidateText(form.Text, out var text);
        if (textError != null) return BadRequest(new { message = textError });
        var imageError = ValidateImage(form.Image);
        if (imageError != null) return BadRequest(new { message = imageError });

        var isStudent = User.IsInRole(Roles.Student);
        var studentId = isStudent ? User.GetUserId() : 0;

        var msg = await _db.ContactMessages.FirstOrDefaultAsync(m => m.Id == id && (!isStudent || m.StudentId == studentId));
        if (msg == null) return NotFound(new { message = "Message not found." });

        if (isStudent)
        {
            var membership = await _db.StudentGroupMemberships.AsNoTracking()
                .FirstOrDefaultAsync(m => m.StudentId == studentId);
            if (membership == null) return NotFound(new { message = "You are not linked to this teacher." });
            if (membership.IsCancelled) return StatusCode(403, new { message = "Your account with this teacher is cancelled." });
        }

        string? imageUrl = null;
        if (HasImage(form.Image))
            imageUrl = await _files.SaveAsync(form.Image!, ImageSubFolder);

        var now = DateTime.UtcNow;
        _db.ContactReplies.Add(new ContactReply
        {
            ContactMessageId = msg.Id,
            TeacherId = msg.TeacherId,
            IsFromTeacher = !isStudent,
            Text = text,
            ImageUrl = imageUrl,
            CreatedAt = now
        });
        msg.LastActivityAt = now;
        // Teacher posting => they've obviously read the thread; student posting => new for the teacher.
        msg.IsReadByTeacher = !isStudent;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch
        {
            await _files.DeleteAsync(imageUrl);
            throw;
        }

        return Ok(await BuildDtoAsync(msg.Id, isStudent));
    }

    // POST ContactMessages/replies/{replyId}/edit   (multipart: Text, Image?, RemoveImage?)
    // Each side can edit only its own messages.
    [HttpPost("replies/{replyId:int}/edit")]
    [Authorize(Roles = AllRoles)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> EditReply(int replyId, [FromForm] ContactMessageForm form)
    {
        var textError = ValidateText(form.Text, out var text);
        if (textError != null) return BadRequest(new { message = textError });
        var imageError = ValidateImage(form.Image);
        if (imageError != null) return BadRequest(new { message = imageError });

        var isStudent = User.IsInRole(Roles.Student);
        var reply = await FindOwnReplyAsync(replyId, isStudent);
        if (reply == null) return NotFound(new { message = "Message not found." });

        var oldImageUrl = reply.ImageUrl;
        string? newImageUrl = null;

        if (HasImage(form.Image))
        {
            newImageUrl = await _files.SaveAsync(form.Image!, ImageSubFolder);
            reply.ImageUrl = newImageUrl;
        }
        else if (form.RemoveImage)
        {
            reply.ImageUrl = null;
        }

        reply.Text = text;
        reply.EditedAt = DateTime.UtcNow;
        if (isStudent && reply.ContactMessage != null)
        {
            // The teacher should notice that the student changed something.
            reply.ContactMessage.IsReadByTeacher = false;
            reply.ContactMessage.LastActivityAt = DateTime.UtcNow;
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch
        {
            await _files.DeleteAsync(newImageUrl);
            throw;
        }

        if (!string.Equals(oldImageUrl, reply.ImageUrl, StringComparison.Ordinal))
            await _files.DeleteAsync(oldImageUrl);

        return Ok(await BuildDtoAsync(reply.ContactMessageId, isStudent));
    }

    // POST ContactMessages/replies/{replyId}/delete  -- each side deletes only its own messages
    [HttpPost("replies/{replyId:int}/delete")]
    [Authorize(Roles = AllRoles)]
    public async Task<IActionResult> DeleteReply(int replyId)
    {
        var isStudent = User.IsInRole(Roles.Student);
        var reply = await FindOwnReplyAsync(replyId, isStudent);
        if (reply == null) return NotFound(new { message = "Message not found." });

        var messageId = reply.ContactMessageId;
        var imageUrl = reply.ImageUrl;

        _db.ContactReplies.Remove(reply);
        await _db.SaveChangesAsync();

        await _files.DeleteAsync(imageUrl);

        return Ok(await BuildDtoAsync(messageId, isStudent));
    }

    /// <summary>
    /// The reply, only if the CALLER wrote it: a student gets their own student
    /// messages of their own threads; staff get teacher messages. Anything else is
    /// "not found" (no hint that it exists). Tenant isolation comes from the filters.
    /// </summary>
    private async Task<ContactReply?> FindOwnReplyAsync(int replyId, bool isStudent)
    {
        var reply = await _db.ContactReplies
            .Include(r => r.ContactMessage)
            .FirstOrDefaultAsync(r => r.Id == replyId);
        if (reply == null) return null;

        if (isStudent)
        {
            var studentId = User.GetUserId();
            if (reply.IsFromTeacher || reply.ContactMessage == null || reply.ContactMessage.StudentId != studentId)
                return null;
        }
        else if (!reply.IsFromTeacher)
        {
            return null;
        }

        return reply;
    }
}
