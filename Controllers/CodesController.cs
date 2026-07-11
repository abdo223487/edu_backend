using EduApi.Common;
using EduApi.Data;
using EduApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Controllers;

public record GenerateCodeRequest(int SchoolYear, List<int> UnitIds, List<int> LectureIds);

/// <summary>
/// Route: api/Codes
///  GET  Codes/codeables?schoolYear=..     -> units (with nested lessons/lectures) +
///                                             standalone lectures eligible for a code
///  GET  Codes?schoolYear=..
///  GET  Codes/{codeId}
///  POST Codes/{codeId}/delete              -- now blocked once a code has been used
///  POST Codes                              body: { schoolYear, unitIds, lectureIds }
///
/// Reconciled against the real client ("create Code .dart") — several things
/// were mismatched before this pass and are now fixed to match the client
/// exactly (see inline notes at each spot):
///   1) GetCodeables: client reads data['lectures'], not data['standaloneLectures'];
///      and expects unit['lessons'][]['lectures'][] nested trees, not flat {id,name}.
///   2) Generate: client only treats HTTP 200 as success (`res.statusCode == 200`),
///      not 201.
///   3) ToDto: client's list item reads `isRedeemed` (not `isUsed`) and `unlocks`
///      (a list, only its .length is used); the details sheet reads `redeemedBy`
///      (object), `units`/`lectures` (arrays of {name} objects, not raw id lists),
///      and `redeemedAt` (timestamp string) — none of which existed before.
///   4) Delete: now returns 409 once a code IsUsed, per your request — deleting a
///      redeemed code was allowed before with no restriction at all.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = $"{Roles.Teacher},{Roles.AssistantAdmin}")]
public class CodesController : ControllerBase
{
    private readonly AppDbContext _db;
    public CodesController(AppDbContext db) => _db = db;

    // GET Codes/codeables?schoolYear=..
    // Client shape (from "create Code .dart"): { units: [...], lectures: [...] }
    // where each unit is { id, name, lessons: [ { name, lectures: [{id, name}] } ] }
    // and each top-level "lectures" entry is a standalone lecture { id, name }.
    [HttpGet("codeables")]
    public async Task<IActionResult> GetCodeables([FromQuery] int schoolYear)
    {
        var units = await _db.Units.Where(u => u.SchoolYear == schoolYear)
            .Include(u => u.Lessons)
            .ToListAsync();

        var unitIds = units.Select(u => u.Id).ToList();

        // Some lectures were saved earlier with SchoolYear = null (created
        // without a Unit, before the Create endpoint derived it from the
        // Group). Rather than requiring a DB backfill, compute an "effective"
        // school year here: SchoolYear if set, otherwise the SchoolYear of
        // the first Group the lecture belongs to (every lecture's group
        // belongs to exactly one year, so this is always safe/correct).
        var allGroupYears = await _db.Groups.ToDictionaryAsync(g => g.Id, g => g.SchoolYear);
        int? EffectiveYear(Lecture l)
        {
            if (l.SchoolYear.HasValue) return l.SchoolYear;
            foreach (var gid in l.GroupIds)
                if (allGroupYears.TryGetValue(gid, out var y)) return y;
            return null;
        }

        // A lecture is only "codeable" (redeemable via a Code) when a student
        // has no other way to reach it: it must be an Online lecture with an
        // actual YouTube link (a Center-only lecture, or an Online lecture
        // still missing its link, isn't watchable yet), and it must belong to
        // this school year (directly or via its group) and carry at least
        // one Group. Without this filter, codes could be generated for
        // lectures that either aren't playable or aren't scoped to any
        // year/group at all.
        bool IsCodeable(Lecture l) =>
            l.AttendanceMethod == AttendanceMethod.Online &&
            !string.IsNullOrWhiteSpace(l.YoutubeLink) &&
            !string.IsNullOrEmpty(l.GroupIdsCsv) &&
            EffectiveYear(l) == schoolYear;

        var lecturesByUnit = await _db.Lectures
            .Where(l => l.UnitId != null && unitIds.Contains(l.UnitId.Value))
            .ToListAsync();
        lecturesByUnit = lecturesByUnit.Where(IsCodeable).ToList();

        var unitTree = units.Select(u => new
        {
            id = u.Id,
            name = u.Name,
            lessons = u.Lessons.OrderBy(l => l.LessonIndex).Select(lesson => new
            {
                name = lesson.Name,
                lectures = lecturesByUnit
                    .Where(l => l.UnitId == u.Id && l.LessonIndex == lesson.LessonIndex)
                    .Select(l => new { id = l.Id, name = l.Name })
                    .ToList()
            })
            // Drop lessons that end up with zero codeable lectures — nothing
            // in them could actually be granted by a code.
            .Where(lesson => lesson.lectures.Count > 0)
            .ToList()
        })
        // Likewise drop units left with no codeable lessons at all.
        .Where(u => u.lessons.Count > 0)
        .ToList();

        // Fetch all no-unit lectures (not just ones with SchoolYear already
        // set) so IsCodeable's group-fallback can still catch the
        // already-created null-SchoolYear rows described above.
        var standaloneLectures = await _db.Lectures
            .Where(l => l.UnitId == null)
            .ToListAsync();
        var standaloneDtos = standaloneLectures.Where(IsCodeable)
            .Select(l => new { id = l.Id, name = l.Name })
            .ToList();

        // NOTE: key is "lectures", NOT "standaloneLectures" — the client does
        // `standaloneLectures = data['lectures'] ?? []`.
        return Ok(new { units = unitTree, lectures = standaloneDtos });
    }

    // TEMPORARY DEBUG ENDPOINT — remove once codeables filtering is confirmed
    // working. Shows the RAW field values for every lecture in this school
    // year (both in-unit and standalone) so you can see exactly which of the
    // 4 conditions (Online / YoutubeLink / SchoolYear / GroupIds) is failing,
    // instead of just getting an empty result with no explanation.
    // GET Codes/codeables/debug?schoolYear=..
    [HttpGet("codeables/debug")]
    public async Task<IActionResult> GetCodeablesDebug([FromQuery] int schoolYear)
    {
        var unitIds = await _db.Units.Where(u => u.SchoolYear == schoolYear).Select(u => u.Id).ToListAsync();
        var allGroupYears = await _db.Groups.ToDictionaryAsync(g => g.Id, g => g.SchoolYear);

        int? EffectiveYear(Lecture l)
        {
            if (l.SchoolYear.HasValue) return l.SchoolYear;
            foreach (var gid in l.GroupIds)
                if (allGroupYears.TryGetValue(gid, out var y)) return y;
            return null;
        }

        var inUnitLectures = await _db.Lectures
            .Where(l => l.UnitId != null && unitIds.Contains(l.UnitId.Value))
            .ToListAsync();
        // No year filter here on purpose — show every no-unit lecture so you
        // can see its effectiveYear even if raw SchoolYear is null.
        var standalone = await _db.Lectures.Where(l => l.UnitId == null).ToListAsync();

        object Dump(Lecture l) => new
        {
            id = l.Id,
            name = l.Name,
            unitId = l.UnitId,
            lessonIndex = l.LessonIndex,
            attendanceMethod = l.AttendanceMethod.ToString(),
            youtubeLink = l.YoutubeLink,
            rawSchoolYear = l.SchoolYear,
            effectiveSchoolYear = EffectiveYear(l),
            groupIdsCsv = l.GroupIdsCsv,
            // which of the conditions this lecture fails, if any
            failsBecause = new
            {
                notOnline = l.AttendanceMethod != AttendanceMethod.Online,
                noYoutubeLink = string.IsNullOrWhiteSpace(l.YoutubeLink),
                noGroup = string.IsNullOrEmpty(l.GroupIdsCsv),
                effectiveYearDoesNotMatchRequested = EffectiveYear(l) != schoolYear
            }
        };

        return Ok(new
        {
            unitIdsFoundForYear = unitIds,
            inUnitLectures = inUnitLectures.Select(Dump),
            standaloneLectures = standalone.Select(Dump)
        });
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? schoolYear)
    {
        var query = _db.Codes.AsQueryable();
        if (schoolYear.HasValue) query = query.Where(c => c.SchoolYear == schoolYear.Value);

        var codes = await query.ToListAsync();
        var dtos = new List<object>();
        foreach (var c in codes) dtos.Add(await ToDto(c));
        return Ok(dtos);
    }

    [HttpGet("{codeId:int}")]
    public async Task<IActionResult> GetById(int codeId)
    {
        var code = await _db.Codes.FirstOrDefaultAsync(e => e.Id == (codeId));
        if (code == null) return NotFound(new { message = "Code not found." });
        return Ok(await ToDto(code));
    }

    // Was previously unconditional — a used code could be deleted with no
    // warning, silently orphaning the student's already-granted subscriptions
    // from any future audit trail. Now blocked once IsUsed is true.
    [HttpPost("{codeId:int}/delete")]
    public async Task<IActionResult> Delete(int codeId)
    {
        var code = await _db.Codes.FirstOrDefaultAsync(e => e.Id == (codeId));
        if (code == null) return NotFound(new { message = "Code not found." });

        if (code.IsUsed)
            return StatusCode(409, new { message = "Cannot delete a code that has already been used." });

        _db.Codes.Remove(code);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Code deleted." });
    }

    // Client success check is `res.statusCode == 200` (see _GenerateSheet._generate),
    // so this MUST return 200, not 201, or the client silently does nothing on a
    // successful create (no navigator pop, no onGenerated callback).
    [HttpPost]
    public async Task<IActionResult> Generate([FromBody] GenerateCodeRequest request)
    {
        var code = new Code
        {
            Value = GenerateRandomCode(),
            SchoolYear = request.SchoolYear,
            UnitIds = request.UnitIds ?? new(),
            LectureIds = request.LectureIds ?? new(),
            TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
        };
        _db.Codes.Add(code);
        await _db.SaveChangesAsync();

        return Ok(await ToDto(code));
    }

    private static string GenerateRandomCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = Random.Shared;
        return new string(Enumerable.Range(0, 8).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }

    private async Task<object> ToDto(Code c)
    {
        var units = await _db.Units.Where(u => c.UnitIds.Contains(u.Id))
            .Select(u => new { id = u.Id, name = u.Name }).ToListAsync();
        var lectures = await _db.Lectures.Where(l => c.LectureIds.Contains(l.Id))
            .Select(l => new { id = l.Id, name = l.Name }).ToListAsync();

        object? redeemedBy = null;
        if (c.UsedByStudentId.HasValue)
        {
            // MULTI-TENANT: IgnoreQueryFilters() because a student's own row
            // might not be "visible" under this tenant if they somehow redeemed
            // a code without a matching membership; the group NAME below is
            // still resolved specifically for THIS code's tenant (c.TeacherId),
            // not the student's legacy/other-tenant group.
            var student = await _db.Students.IgnoreQueryFilters()
                .Where(s => s.Id == c.UsedByStudentId.Value)
                .Select(s => new { s.Name, s.PhoneNumber })
                .FirstOrDefaultAsync();
            var groupName = await _db.StudentGroupMemberships.IgnoreQueryFilters()
                .Where(m => m.StudentId == c.UsedByStudentId.Value && m.Group!.TeacherId == c.TeacherId)
                .Select(m => m.Group!.Name)
                .FirstOrDefaultAsync();

            if (student != null)
                redeemedBy = new { name = student.Name, groupName = groupName ?? "", phoneNumber = student.PhoneNumber };
        }

        return new
        {
            id = c.Id,
            code = c.Value,
            schoolYear = c.SchoolYear,
            units,
            lectures,
            // "how many items this code unlocks" — only .length is read client-side.
            unlocks = c.UnitIds.Concat(c.LectureIds).ToList(),
            isUsed = c.IsUsed,
            isRedeemed = c.IsUsed, // client's list item reads "isRedeemed" specifically
            redeemedBy,
            redeemedAt = c.UsedAt?.ToString("O")
        };
    }
}
