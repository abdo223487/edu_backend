using System.Security.Cryptography;
using System.Text;
using EduApi.Common;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

/// <summary>
/// "مصادر الحل". Route: api/SolutionSources.
///
/// STAFF (Teacher / AssistantAdmin) register the sources (book, worksheet, ...) for
/// a school year; a student asking a question can pick one of the sources of HIS
/// school year (see ContactMessagesController).
///
/// STUDENT also gets the "أسئلة زمايلي" browsing endpoints: every source of his
/// year, then the ANONYMOUS classmates who asked about it, then everything one
/// such classmate asked about that source (with the teacher's replies and the
/// classmate's follow-ups). Nothing here ever returns a student id / name /
/// group -- classmates are only a number plus an opaque key (HMAC of
/// source+student, so the real id can't be derived or enumerated), and the
/// caller's own questions are excluded.
///
///  GET  SolutionSources?schoolYear=&p=   -- staff: all (optionally one year); student: own year (paged; all=true = no paging)
///  POST SolutionSources                  -- staff: { schoolYear, name }
///  POST SolutionSources/{id}/edit        -- staff: { name }
///  POST SolutionSources/{id}/delete      -- staff (questions stay, just lose the source)
///  GET  SolutionSources/{id}/peers?p=    -- student: anonymous classmates of that source
///  GET  SolutionSources/{id}/peers/{key}?p=  -- student: that classmate's questions on the source
///  GET  SolutionSources/none/peers[/{key}]   -- same, for questions asked WITHOUT a source
///
/// Everything is tenant-scoped through SolutionSource's / ContactMessage's global
/// query filters.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SolutionSourcesController : ControllerBase
{
    private const string StaffRoles = $"{Roles.Teacher},{Roles.AssistantAdmin}";
    private const string AllRoles = $"{Roles.Student},{Roles.Teacher},{Roles.AssistantAdmin}";
    private const int MaxNameLength = 120;

    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public SolutionSourcesController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    // ───────────────────────── helpers ─────────────────────────

    private static object ToItem(SolutionSource s, int questionsCount, int peersCount) => new
    {
        id = s.Id,
        name = s.Name,
        schoolYear = s.SchoolYear,
        createdAt = s.CreatedAt,
        questionsCount,
        peersCount
    };

    private static string? ValidateName(string? name, out string trimmed)
    {
        trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "Name is required.";
        if (trimmed.Length > MaxNameLength) return $"Name is too long (max {MaxNameLength} characters).";
        return null;
    }

    /// <summary>Opaque, stable, non-reversible handle of "this classmate inside this source".</summary>
    private string PeerKey(int sourceId, int studentId)
    {
        var secret = _config["Jwt:Key"] ?? string.Empty;
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = h.ComputeHash(Encoding.UTF8.GetBytes($"solution-peer:{sourceId}:{studentId}"));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>The source, only if it's in the caller's own school year for this teacher (students). Null = "not found".</summary>
    private async Task<SolutionSource?> FindStudentSourceAsync(int id)
    {
        var years = await _db.GetStudentSchoolYearsAsync(User);
        if (years.Count == 0) return null;
        return await _db.SolutionSources.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && years.Contains(s.SchoolYear));
    }

    // ───────────────────────── list (both roles) ─────────────────────────

    [HttpGet]
    [Authorize(Roles = AllRoles)]
    public async Task<IActionResult> List([FromQuery] int? schoolYear, [FromQuery] int p = 1, [FromQuery] bool all = false)
    {
        if (p < 1) p = 1;
        var isStudent = User.IsInRole(Roles.Student);
        var studentId = isStudent ? User.GetUserId() : 0;

        var query = _db.SolutionSources.AsNoTracking().AsQueryable();
        if (isStudent)
        {
            var years = await _db.GetStudentSchoolYearsAsync(User);
            if (years.Count == 0) return Ok(new List<object>());
            query = query.Where(s => years.Contains(s.SchoolYear));
        }
        else if (schoolYear.HasValue)
        {
            query = query.Where(s => s.SchoolYear == schoolYear.Value);
        }

        var ordered = query.OrderBy(s => s.SchoolYear).ThenBy(s => s.Name).ThenBy(s => s.Id);
        // Paged (PageSize per page) by default; all=true returns everything -- only for the
        // student's "pick a source" dropdown, which needs the complete list.
        var sources = all
            ? await ordered.ToListAsync()
            : await ordered.Skip((p - 1) * PagingDefaults.PageSize).Take(PagingDefaults.PageSize).ToListAsync();
        var ids = sources.Select(s => s.Id).ToList();

        // (source, student) -> number of questions. Students don't count their own.
        var rows = await _db.ContactMessages.AsNoTracking()
            .Where(m => m.SolutionSourceId != null && ids.Contains(m.SolutionSourceId.Value)
                        && (!isStudent || m.StudentId != studentId))
            .GroupBy(m => new { m.SolutionSourceId, m.StudentId })
            .Select(g => new { SourceId = g.Key.SolutionSourceId, g.Key.StudentId, Count = g.Count() })
            .ToListAsync();

        var perSource = rows
            .GroupBy(r => r.SourceId)
            .ToDictionary(g => g.Key!.Value, g => (Questions: g.Sum(x => x.Count), Peers: g.Count()));

        return Ok(sources.Select(s =>
        {
            perSource.TryGetValue(s.Id, out var c);
            return ToItem(s, c.Questions, c.Peers);
        }).ToList());
    }

    // ───────────────────────── staff: manage ─────────────────────────

    // GET SolutionSources/school-years  -> [3, 2, ...]  the years this teacher actually has
    // (groups / courses / existing sources), so the "new source" dropdown offers real values.
    [HttpGet("school-years")]
    [Authorize(Roles = StaffRoles)]
    public async Task<IActionResult> SchoolYears()
    {
        var fromGroups = await _db.Groups.AsNoTracking().Select(g => g.SchoolYear).Distinct().ToListAsync();
        var fromUnits = await _db.Units.AsNoTracking().Select(u => u.SchoolYear).Distinct().ToListAsync();
        var fromSources = await _db.SolutionSources.AsNoTracking().Select(x => x.SchoolYear).Distinct().ToListAsync();
        return Ok(fromGroups.Union(fromUnits).Union(fromSources).OrderBy(y => y).ToList());
    }

    // POST SolutionSources   { schoolYear, name }
    [HttpPost]
    [Authorize(Roles = StaffRoles)]
    public async Task<IActionResult> Create([FromBody] SolutionSourceRequest request)
    {
        var tenantId = User.GetStaffTenantId();
        if (tenantId == null) return BadRequest(new { message = "Teacher could not be resolved." });
        if (request.SchoolYear <= 0) return BadRequest(new { message = "School year is required." });

        var error = ValidateName(request.Name, out var name);
        if (error != null) return BadRequest(new { message = error });

        var lower = name.ToLower();
        if (await _db.SolutionSources.AnyAsync(s => s.SchoolYear == request.SchoolYear && s.Name.ToLower() == lower))
            return BadRequest(new { message = "A source with this name already exists for this school year." });

        var source = new SolutionSource
        {
            TeacherId = tenantId.Value, // TENANT LAYER
            SchoolYear = request.SchoolYear,
            Name = name,
            CreatedAt = DateTime.UtcNow
        };
        _db.SolutionSources.Add(source);
        await _db.SaveChangesAsync();

        return StatusCode(201, ToItem(source, 0, 0));
    }

    // POST SolutionSources/{id}/edit   { name }
    [HttpPost("{id:int}/edit")]
    [Authorize(Roles = StaffRoles)]
    public async Task<IActionResult> Rename(int id, [FromBody] SolutionSourceRenameRequest request)
    {
        var error = ValidateName(request.Name, out var name);
        if (error != null) return BadRequest(new { message = error });

        var source = await _db.SolutionSources.FirstOrDefaultAsync(s => s.Id == id);
        if (source == null) return NotFound(new { message = "Source not found." });

        var lower = name.ToLower();
        if (await _db.SolutionSources.AnyAsync(s => s.Id != id && s.SchoolYear == source.SchoolYear && s.Name.ToLower() == lower))
            return BadRequest(new { message = "A source with this name already exists for this school year." });

        source.Name = name;
        await _db.SaveChangesAsync();

        var questions = await _db.ContactMessages.AsNoTracking().CountAsync(m => m.SolutionSourceId == id);
        var peers = await _db.ContactMessages.AsNoTracking()
            .Where(m => m.SolutionSourceId == id).Select(m => m.StudentId).Distinct().CountAsync();
        return Ok(ToItem(source, questions, peers));
    }

    // POST SolutionSources/{id}/delete
    [HttpPost("{id:int}/delete")]
    [Authorize(Roles = StaffRoles)]
    public async Task<IActionResult> Delete(int id)
    {
        var source = await _db.SolutionSources.FirstOrDefaultAsync(s => s.Id == id);
        if (source == null) return NotFound(new { message = "Source not found." });

        // ContactMessages.SolutionSourceId is ON DELETE SET NULL: the questions stay.
        _db.SolutionSources.Remove(source);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Source deleted." });
    }

    // ───────────────────────── student: "أسئلة زمايلي" ─────────────────────────

    /// <summary>
    /// Messages of the "classmates" bucket: those of one source, or (sourceId == null)
    /// the ones with NO source from students of the caller's own school year.
    /// The caller's own questions are always excluded.
    /// </summary>
    private IQueryable<ContactMessage> PeerMessages(int? sourceId, int me, List<int> years)
        => sourceId == null
            // "same school year" = the asker has a group of that year under THIS teacher
            // (deliberately no m.Student navigation: it would INNER JOIN the filtered Students).
            ? _db.ContactMessages.AsNoTracking()
                .Where(m => m.SolutionSourceId == null && m.StudentId != me
                    && _db.StudentGroupMemberships.Any(gm => gm.StudentId == m.StudentId && years.Contains(gm.Group!.SchoolYear)))
            : _db.ContactMessages.AsNoTracking()
                .Where(m => m.SolutionSourceId == sourceId && m.StudentId != me);

    // GET SolutionSources/{id}/peers?p=1  -> [{ key, number, questionsCount, lastActivityAt }]
    [HttpGet("{id:int}/peers")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> Peers(int id, [FromQuery] int p = 1)
    {
        var source = await FindStudentSourceAsync(id);
        if (source == null) return NotFound(new { message = "Source not found." });
        return await PeersCore(id, p);
    }

    // GET SolutionSources/none/peers?p=1  -- classmates who asked WITHOUT choosing a source
    [HttpGet("none/peers")]
    [Authorize(Roles = Roles.Student)]
    public Task<IActionResult> PeersNoSource([FromQuery] int p = 1) => PeersCore(null, p);

    private async Task<IActionResult> PeersCore(int? sourceId, int p)
    {
        if (p < 1) p = 1;
        var years = await _db.GetStudentSchoolYearsAsync(User);
        if (years.Count == 0) return Ok(new List<object>());

        var me = User.GetUserId();
        var rows = await PeerMessages(sourceId, me, years)
            .GroupBy(m => m.StudentId)
            .Select(g => new
            {
                StudentId = g.Key,
                Count = g.Count(),
                FirstId = g.Min(x => x.Id),
                Last = g.Max(x => x.LastActivityAt)
            })
            .ToListAsync();

        // "طالب 1", "طالب 2" ... numbered by who asked first (stable while the list changes).
        var numbered = rows.OrderBy(r => r.FirstId)
            .Select((r, i) => new
            {
                key = PeerKey(sourceId ?? 0, r.StudentId),
                number = i + 1,
                questionsCount = r.Count,
                lastActivityAt = r.Last
            })
            .OrderByDescending(x => x.lastActivityAt)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .ToList();

        return Ok(numbered);
    }

    // GET SolutionSources/{id}/peers/{key}?p=1
    //  -> every question that anonymous classmate asked about this source, newest activity first
    [HttpGet("{id:int}/peers/{key}")]
    [Authorize(Roles = Roles.Student)]
    public async Task<IActionResult> PeerQuestions(int id, string key, [FromQuery] int p = 1)
    {
        var source = await FindStudentSourceAsync(id);
        if (source == null) return NotFound(new { message = "Source not found." });
        return await PeerQuestionsCore(id, key, p);
    }

    // GET SolutionSources/none/peers/{key}?p=1  -- that classmate's no-source questions
    [HttpGet("none/peers/{key}")]
    [Authorize(Roles = Roles.Student)]
    public Task<IActionResult> PeerQuestionsNoSource(string key, [FromQuery] int p = 1)
        => PeerQuestionsCore(null, key, p);

    private async Task<IActionResult> PeerQuestionsCore(int? sourceId, string key, int p)
    {
        if (p < 1) p = 1;
        var years = await _db.GetStudentSchoolYearsAsync(User);
        if (years.Count == 0) return NotFound(new { message = "Student not found." });

        var me = User.GetUserId();
        var candidateIds = await PeerMessages(sourceId, me, years)
            .Select(m => m.StudentId)
            .Distinct()
            .ToListAsync();

        var normalizedKey = (key ?? string.Empty).Trim().ToLowerInvariant();
        int? peerId = null;
        foreach (var sid in candidateIds)
        {
            if (string.Equals(PeerKey(sourceId ?? 0, sid), normalizedKey, StringComparison.Ordinal))
            {
                peerId = sid;
                break;
            }
        }
        if (peerId == null) return NotFound(new { message = "Student not found." });

        var page = await PeerMessages(sourceId, me, years)
            .Include(m => m.Replies)
            .Where(m => m.StudentId == peerId.Value)
            .OrderByDescending(m => m.LastActivityAt)
            .ThenByDescending(m => m.Id)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .ToListAsync();

        // Deliberately NO studentId / name / group / read flags in here.
        return Ok(page.Select(m => new
        {
            id = m.Id,
            text = m.Text,
            imageUrl = m.ImageUrl,
            createdAt = m.CreatedAt,
            editedAt = m.EditedAt,
            pageNumber = m.PageNumber,
            questionNumber = m.QuestionNumber,
            isAnswered = m.Replies.Any(r => r.IsFromTeacher),
            replies = m.Replies
                .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
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
        }).ToList());
    }
}
