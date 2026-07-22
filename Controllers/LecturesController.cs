using EduApi.Common;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/Lectures. Mirrors:
///  POST   Lectures                                (multipart/form-data if a video file is attached, else plain JSON — e.g. Center lectures)
///  PATCH  Lectures/{id}
///  POST   Lectures/delete?lectureId=..
///  GET    Lectures/by-group?groupId=..&attendanceMethod=..&unitId=..&lessonIndex=..&noUnitOnly=..&p=..
///  GET    Lectures/{id}/materials
///  POST   Lectures/{id}/materials/file       (multipart, field name "Files")
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class LecturesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _files;
    private readonly ILogger<LecturesController> _logger;

    private readonly Common.ITenantContext _tenant;

    public LecturesController(AppDbContext db, IFileStorageService files, Common.ITenantContext tenant, ILogger<LecturesController> logger)
    {
        _db = db;
        _files = files;
        _tenant = tenant;
        _logger = logger;
    }

    // GET Lectures/latest-center
    // Returns the most recently created Center (in-person) lecture for the
    // current teacher — {id, name, createdAt}, or 404 if none exist yet.
    // Used by the offline notebook-payment flow: since a NotebookPayment
    // should record which lecture it was made for (LectureId) but the
    // teacher is recording payments offline with no lecture picker
    // available, the app fetches this once while it HAS connectivity and
    // caches it locally, then attaches that cached lecture id to every
    // payment it queues until the next time it can refresh this value.
    [HttpGet("latest-center")]
    public async Task<IActionResult> GetLatestCenterLecture()
    {
        var lecture = await _db.Lectures.AsNoTracking()
            .Where(l => l.AttendanceMethod == AttendanceMethod.Center)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new { l.Id, l.Name, l.CreatedAt })
            .FirstOrDefaultAsync();

        if (lecture == null)
            return NotFound(new { message = "No center lecture has been created yet." });

        return Ok(lecture);
    }

    // GET Lectures/video-upload-url?extension=.mp4&contentType=video/mp4
    // Returns a short-lived presigned R2 PUT URL so the app can upload the
    // recorded video straight to Cloudflare R2 from the device, instead of
    // sending the whole file through this API. Flow: 1) call this to get
    // {uploadUrl, publicUrl}; 2) PUT the raw video bytes to uploadUrl with
    // the SAME Content-Type header sent here; 3) call POST Lectures with
    // YoutubeLink = publicUrl (Create() already treats any non-YouTube link
    // in that field as an already-hosted video — no VideoFile needed, and no
    // separate upload endpoint/field required for this case).
    [HttpGet("video-upload-url")]
    public IActionResult GetVideoUploadUrl([FromQuery] string extension = ".mp4", [FromQuery] string contentType = "video/mp4")
    {
        if (!string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Only .mp4 video files are supported." });

        try
        {
            var (uploadUrl, publicUrl) = _files.GetPresignedUploadUrl("lectures", extension, contentType);
            return Ok(new { uploadUrl, publicUrl });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return StatusCode(501, new { message = ex.Message });
        }
    }

    // Multipart create — supports an actual recorded-video upload (stored in
    // Cloudflare R2, same as quiz-question images and lecture materials) in
    // addition to a plain YouTube link.
    // fields: Name, AttendanceMethod, GroupIds[i], UnitId?, LessonIndex?,
    //         SchoolYear?, YoutubeLink? (text — see IsYoutubeUrl below: this
    //         field also accepts a direct video URL the teacher already
    //         uploaded to R2/elsewhere by hand, so the app doesn't need any
    //         separate field or UI for that case)
    // file:   VideoFile (optional, .mp4)
    [HttpPost]
    public async Task<IActionResult> Create()
    {
        // BUGFIX: this endpoint used to be hard-locked to
        // [Consumes("multipart/form-data")], which made ASP.NET Core reject
        // any request with a different content type — including plain JSON
        // — with a 415 before the action even ran. That's fine for Online
        // lectures (which may carry a video file and need multipart), but
        // broke Center lecture creation, whose client still POSTs a normal
        // JSON body (no video to attach). Now both are accepted: read the
        // multipart form when there's actually a form, otherwise fall back
        // to parsing a JSON body with the same field names.
        string name, attendanceMethodRaw;
        string? youtubeLinkRaw;
        IFormFile? videoFile;
        var groupIds = new List<int>();
        int? unitId, lessonIndex, requestSchoolYear;

        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync();

            name = form["Name"].ToString();
            attendanceMethodRaw = form["AttendanceMethod"].ToString();
            youtubeLinkRaw = form["YoutubeLink"].ToString();
            if (string.IsNullOrWhiteSpace(youtubeLinkRaw)) youtubeLinkRaw = null;
            videoFile = form.Files["VideoFile"];

            for (var i = 0; form.ContainsKey($"GroupIds[{i}]"); i++)
                groupIds.Add(int.Parse(form[$"GroupIds[{i}]"].ToString()));

            unitId = int.TryParse(form["UnitId"].ToString(), out var uid) ? uid : null;
            lessonIndex = int.TryParse(form["LessonIndex"].ToString(), out var li) ? li : null;
            requestSchoolYear = int.TryParse(form["SchoolYear"].ToString(), out var sy) ? sy : null;
        }
        else
        {
            using var reader = new StreamReader(Request.Body);
            var bodyText = await reader.ReadToEndAsync();
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(bodyText) ? "{}" : bodyText);
            var root = json.RootElement;

            // Accepts either camelCase (typical System.Text.Json client output)
            // or PascalCase property names, whichever the caller sends.
            string? GetString(string camel, string pascal) =>
                root.TryGetProperty(camel, out var v1) && v1.ValueKind == JsonValueKind.String ? v1.GetString() :
                root.TryGetProperty(pascal, out var v2) && v2.ValueKind == JsonValueKind.String ? v2.GetString() : null;

            int? GetInt(string camel, string pascal)
            {
                if (root.TryGetProperty(camel, out var v1) && v1.ValueKind == JsonValueKind.Number) return v1.GetInt32();
                if (root.TryGetProperty(pascal, out var v2) && v2.ValueKind == JsonValueKind.Number) return v2.GetInt32();
                return null;
            }

            name = GetString("name", "Name") ?? "";
            attendanceMethodRaw = GetString("attendanceMethod", "AttendanceMethod") ?? "";
            youtubeLinkRaw = GetString("youtubeLink", "YoutubeLink");
            videoFile = null; // JSON bodies never carry a file.

            if (root.TryGetProperty("groupIds", out var gArr) || root.TryGetProperty("GroupIds", out gArr))
            {
                foreach (var el in gArr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.Number) groupIds.Add(el.GetInt32());
            }

            unitId = GetInt("unitId", "UnitId");
            lessonIndex = GetInt("lessonIndex", "LessonIndex");
            requestSchoolYear = GetInt("schoolYear", "SchoolYear");
        }

        if (!Enum.TryParse<AttendanceMethod>(attendanceMethodRaw, true, out var method))
            return BadRequest(new { message = "attendanceMethod must be 'Center' or 'Online'." });

        // The client only ever sends one text field for "the video link"
        // (still called YoutubeLink for backward compatibility with the
        // existing app — no frontend change needed). It's flexible on
        // purpose: if what's in there is an actual YouTube URL, treat it as
        // one; otherwise assume it's a direct video URL the teacher already
        // uploaded to R2 (or anywhere else) by hand, and treat it exactly
        // like an uploaded video file — same as VideoFile, just skipping the
        // upload step since it's already hosted somewhere.
        string? youtubeLink = null;
        string? manualVideoLink = null;
        if (youtubeLinkRaw != null)
        {
            if (IsYoutubeUrl(youtubeLinkRaw)) youtubeLink = youtubeLinkRaw;
            else manualVideoLink = youtubeLinkRaw;
        }

        // A recorded upload and a YouTube/manual-link video both describing
        // "the video for this lecture" don't make sense together — pick
        // exactly one so the client never has to guess which one plays.
        if (videoFile != null && youtubeLinkRaw != null)
            return BadRequest(new { message = "Provide either a video file or a video/YouTube link, not both." });

        string? storageFileUrl = null;
        string? thumbnailUrl = null;
        if (videoFile != null)
        {
            var ext = Path.GetExtension(videoFile.FileName);
            if (!string.Equals(ext, ".mp4", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Only .mp4 video files are supported." });

            storageFileUrl = await _files.SaveAsync(videoFile, "lectures");

            // Best-effort: grab a real frame from the video (via ffmpeg) to use
            // as its thumbnail, since a recorded upload has no YouTube-style
            // thumbnail API to fall back on. Never fails the whole request —
            // a lecture with a working video but no thumbnail is fine; the
            // reverse would not be.
            thumbnailUrl = await TryGenerateThumbnailAsync(videoFile);
        }
        else if (manualVideoLink != null)
        {
            // Nothing to upload — the video is already hosted at this URL.
            // ffmpeg can read directly from an http(s) URL, so we point it
            // there instead of downloading the file ourselves first.
            storageFileUrl = manualVideoLink;
            thumbnailUrl = await TryGenerateThumbnailFromUrlAsync(manualVideoLink);
        }

        // Same SchoolYear-derivation rule as before: prefer the Unit's year,
        // fall back to an explicitly-sent SchoolYear, then to the first
        // group's year — a teacher JWT never carries a "schoolYear" claim.
        int? schoolYear;
        if (unitId.HasValue)
        {
            schoolYear = await _db.Units.Where(u => u.Id == unitId.Value)
                .Select(u => (int?)u.SchoolYear).FirstOrDefaultAsync();
        }
        else if (requestSchoolYear.HasValue)
        {
            schoolYear = requestSchoolYear;
        }
        else
        {
            var firstGroupId = groupIds.FirstOrDefault();
            schoolYear = firstGroupId != 0
                ? await _db.Groups.Where(g => g.Id == firstGroupId).Select(g => (int?)g.SchoolYear).FirstOrDefaultAsync()
                : null;
        }

        var lecture = new Lecture
        {
            Name = name,
            AttendanceMethod = method,
            YoutubeLink = youtubeLink,
            StorageFileKey = storageFileUrl,
            ThumbnailUrl = thumbnailUrl,
            GroupIds = groupIds,
            UnitId = unitId,
            LessonIndex = lessonIndex,
            SchoolYear = schoolYear,
            TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
        };

        _db.Lectures.Add(lecture);
        await _db.SaveChangesAsync();

        return StatusCode(201, ToDto(lecture));
    }

    [HttpPatch("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateLectureRequest request)
    {
        var lecture = await _db.Lectures.FirstOrDefaultAsync(e => e.Id == (id));
        if (lecture == null) return NotFound(new { message = "Lecture not found." });

        if (request.Name != null) lecture.Name = request.Name;
        if (request.AttendanceMethod != null &&
            Enum.TryParse<AttendanceMethod>(request.AttendanceMethod, true, out var method))
            lecture.AttendanceMethod = method;
        if (request.SchoolYear.HasValue) lecture.SchoolYear = request.SchoolYear.Value;

        await _db.SaveChangesAsync();
        return Ok(ToDto(lecture));
    }

    [HttpPost("delete")]
    public async Task<IActionResult> Delete([FromQuery] int lectureId)
    {
        var lecture = await _db.Lectures.FirstOrDefaultAsync(e => e.Id == (lectureId));
        if (lecture == null) return NotFound(new { message = "Lecture not found." });

        // Best-effort: remove the recorded video AND its generated thumbnail
        // from R2 too, so deleting a lecture doesn't leave orphaned files
        // sitting in the bucket forever. Does nothing for a YoutubeLink-only
        // lecture (both fields are null there).
        await _files.DeleteAsync(lecture.StorageFileKey);
        await _files.DeleteAsync(lecture.ThumbnailUrl);

        _db.Lectures.Remove(lecture);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Lecture deleted." });
    }

    // GET Lectures/by-year?schoolYear=..&attendanceMethod=Center[&unitId=..]
    // No pagination — used by the teacher's center-lecture screen for a whole year.
    [HttpGet("by-year")]
    public async Task<IActionResult> ByYear(
        [FromQuery] int schoolYear,
        [FromQuery] string attendanceMethod,
        [FromQuery] int? unitId = null)
    {
        if (!Enum.TryParse<AttendanceMethod>(attendanceMethod, true, out var method))
            return BadRequest(new { message = "attendanceMethod must be 'Center' or 'Online'." });

        var query = _db.Lectures.AsNoTracking().Where(l => l.SchoolYear == schoolYear && l.AttendanceMethod == method);
        if (unitId.HasValue) query = query.Where(l => l.UnitId == unitId.Value);

        // Defensive: this action has no [Authorize(Roles=...)] restricting it to
        // staff, so apply the same subscription/unlock gate as ByGroup in case
        // a student token calls it directly.
        if (User.IsInRole(Roles.Student))
        {
            var studentId = User.GetUserId();
            var subscribedIds = User.GetUnitIds();
            var unlockedLectureIds = await _db.StudentLectureUnlocks.AsNoTracking()
                .Where(u => u.StudentId == studentId)
                .Select(u => u.LectureId)
                .ToListAsync();

            // A lecture inside a Unit is visible either because the student is
            // subscribed to the whole Unit, OR because they redeemed a code
            // that unlocked that ONE lecture specifically (see RedeemCode) —
            // previously only a full-unit subscription counted here, so a
            // lecture-level code for an in-unit lecture was granted but never
            // actually visible to the student.
            query = query.Where(l =>
                (l.UnitId != null && (subscribedIds.Contains(l.UnitId.Value) || unlockedLectureIds.Contains(l.Id))) ||
                (l.UnitId == null && unlockedLectureIds.Contains(l.Id)));
        }

        // ToDto only reads Id/Name/AttendanceMethod/YoutubeLink/StorageFileKey/
        // UnitId/LessonIndex/GroupIdsCsv/CreatedAt -- project those columns at
        // the SQL level instead of materializing the full Lecture row
        // (TeacherId/Months/SchoolYear are never used here).
        var items = await query.OrderBy(l => l.Id)
            .Select(l => new { l.Id, l.Name, l.AttendanceMethod, l.YoutubeLink, l.StorageFileKey, l.ThumbnailUrl, l.UnitId, l.LessonIndex, l.GroupIdsCsv, l.CreatedAt })
            .ToListAsync();
        return Ok(items.Select(l =>
        {
            var (link, sourceType) = PlaybackInfo(l.StorageFileKey, l.YoutubeLink);
            return new LectureListItem(
                l.Id, l.Name, l.AttendanceMethod.ToString(), link, sourceType, l.ThumbnailUrl, l.UnitId, l.LessonIndex,
                l.GroupIdsCsv.Length == 0 ? new List<int>() : l.GroupIdsCsv.Split(',').Select(int.Parse).ToList(),
                l.CreatedAt);
        }));
    }

    [HttpGet("by-group")]
    public async Task<IActionResult> ByGroup(
        [FromQuery] int? groupId,
        [FromQuery] string? attendanceMethod,
        [FromQuery] int? unitId,
        [FromQuery] int? lessonIndex,
        [FromQuery] bool noUnitOnly = false,
        [FromQuery] int p = 1)
    {
        var gId = groupId ?? User.GetGroupId(_tenant.CurrentTenantId);
        var query = _db.Lectures.AsNoTracking().AsQueryable();

        if (gId.HasValue) query = query.Where(l => _db.LectureGroupLinks.Any(x => x.LectureId == l.Id && x.GroupId == gId.Value));
        if (!string.IsNullOrEmpty(attendanceMethod) &&
            Enum.TryParse<AttendanceMethod>(attendanceMethod, true, out var method))
            query = query.Where(l => l.AttendanceMethod == method);

        if (noUnitOnly) query = query.Where(l => l.UnitId == null);
        else if (unitId.HasValue) query = query.Where(l => l.UnitId == unitId.Value);

        // Same subscription gate as Units/Notifications: a lecture tied to a
        // specific unit is only visible to a student subscribed to that unit.
        //
        // BUGFIX: lectures with no UnitId (standalone/no-unit online lectures
        // meant to be unlocked by a plain code, e.g. via Codes/codeables ->
        // Codes POST with lectureIds) used to pass through completely
        // ungated — every student in the group could see them with no code
        // required at all. Now a standalone lecture only shows up once the
        // student has actually redeemed a code that unlocks it (see
        // StudentsController.RedeemCode, which now writes to
        // StudentLectureUnlocks). We query the DB directly (not a JWT claim)
        // so a lecture appears immediately after redeeming, without needing
        // to refresh/re-login first.
        if (User.IsInRole(Roles.Student))
        {
            var studentId = User.GetUserId();
            var subscribedIds = User.GetUnitIds();
            var unlockedLectureIds = await _db.StudentLectureUnlocks.AsNoTracking()
                .Where(u => u.StudentId == studentId)
                .Select(u => u.LectureId)
                .ToListAsync();

            // See identical note in ByYear: a lecture-level code unlock must
            // grant visibility to that lecture even when it belongs to a Unit
            // the student never subscribed to as a whole.
            query = query.Where(l =>
                (l.UnitId != null && (subscribedIds.Contains(l.UnitId.Value) || unlockedLectureIds.Contains(l.Id))) ||
                (l.UnitId == null && unlockedLectureIds.Contains(l.Id)));
        }

        if (lessonIndex.HasValue) query = query.Where(l => l.LessonIndex == lessonIndex.Value);

        var items = await query
            .OrderBy(l => l.Id)
            .Skip((p - 1) * PagingDefaults.PageSize)
            .Take(PagingDefaults.PageSize)
            .Select(l => new { l.Id, l.Name, l.AttendanceMethod, l.YoutubeLink, l.StorageFileKey, l.ThumbnailUrl, l.UnitId, l.LessonIndex, l.GroupIdsCsv, l.CreatedAt })
            .ToListAsync();

        return Ok(items.Select(l =>
        {
            var (link, sourceType) = PlaybackInfo(l.StorageFileKey, l.YoutubeLink);
            return new LectureListItem(
                l.Id, l.Name, l.AttendanceMethod.ToString(), link, sourceType, l.ThumbnailUrl, l.UnitId, l.LessonIndex,
                l.GroupIdsCsv.Length == 0 ? new List<int>() : l.GroupIdsCsv.Split(',').Select(int.Parse).ToList(),
                l.CreatedAt);
        }));
    }

    [HttpGet("{lectureId:int}/materials")]
    public async Task<IActionResult> GetMaterials(int lectureId)
    {
        // Same subscription gate: if this lecture belongs to a unit, a student
        // must be subscribed to that unit to see its materials, even though
        // they already knew the lectureId.
        if (User.IsInRole(Roles.Student))
        {
            var lecture = await _db.Lectures.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lectureId);
            if (lecture == null) return NotFound(new { message = "Lecture not found." });
            if (lecture.UnitId.HasValue && !User.GetUnitIds().Contains(lecture.UnitId.Value))
            {
                // Same lecture-level-unlock exception as ByGroup/ByYear: a
                // code redeemed for this one in-unit lecture should still let
                // the student open its materials without a full subscription.
                var studentId = User.GetUserId();
                var unlocked = await _db.StudentLectureUnlocks.AsNoTracking()
                    .AnyAsync(u => u.StudentId == studentId && u.LectureId == lectureId);
                if (!unlocked)
                    return StatusCode(403, new { message = "Not subscribed to this unit." });
            }
        }

        var materials = await _db.Materials.AsNoTracking().Where(m => m.LectureId == lectureId)
            .Select(m => new MaterialListItem(m.Id, m.Name, m.Type, m.Link))
            .ToListAsync();

        return Ok(materials);
    }

    [HttpPost("{lectureId:int}/materials/file")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadMaterials(
        int lectureId,
        List<IFormFile> files,
        [FromQuery] int? Months,
        [FromQuery] int? SchoolYear)
    {
        var lecture = await _db.Lectures.FirstOrDefaultAsync(e => e.Id == (lectureId));
        if (lecture == null) return NotFound(new { message = "Lecture not found." });

        var created = new List<MaterialListItem>();
        foreach (var file in files)
        {
            var url = await _files.SaveAsync(file, "materials");
            var material = new Material
            {
                Name = file.FileName,
                Type = "File",
                Link = url,
                LectureId = lectureId,
                UnitId = lecture.UnitId,
                Months = Months,
                SchoolYear = SchoolYear ?? lecture.SchoolYear,
                TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
            };
            _db.Materials.Add(material);
            created.Add(new MaterialListItem(material.Id, material.Name, material.Type, material.Link));
        }

        await _db.SaveChangesAsync();
        return Ok(created);
    }

    private static LectureListItem ToDto(Lecture l)
    {
        var (link, sourceType) = PlaybackInfo(l.StorageFileKey, l.YoutubeLink);
        return new(l.Id, l.Name, l.AttendanceMethod.ToString(), link, sourceType, l.ThumbnailUrl, l.UnitId, l.LessonIndex, l.GroupIds, l.CreatedAt);
    }

    /// <summary>
    /// A lecture has at most one actual video source: an uploaded file stored
    /// in R2 (StorageFileKey holds its public URL) or a YoutubeLink — never
    /// both (enforced in Create). This picks whichever is set and tells the
    /// client which kind it is, so it knows whether to open a YouTube player
    /// or its own video player pointed at the R2 URL.
    /// </summary>
    private static (string? Link, string? SourceType) PlaybackInfo(string? storageFileUrl, string? youtubeLink)
    {
        if (!string.IsNullOrWhiteSpace(storageFileUrl)) return (storageFileUrl, "File");
        if (!string.IsNullOrWhiteSpace(youtubeLink)) return (youtubeLink, "Youtube");
        return (null, null);
    }

    /// <summary>
    /// Extracts a single frame (1 second in, so it's rarely a black
    /// intro/fade frame) from an uploaded .mp4 via ffmpeg, uploads it to R2
    /// as a JPEG, and returns its public URL. Requires ffmpeg to be
    /// installed and on PATH on the server — if it's missing, the frame
    /// extraction fails, or anything else goes wrong, this returns null
    /// rather than throwing, since a lecture is still perfectly usable
    /// without a thumbnail.
    /// </summary>
    /// <summary>True for youtube.com/youtu.be links; false for anything else (including a manually-hosted R2/other video URL).</summary>
    private static bool IsYoutubeUrl(string url) =>
        url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Same as TryGenerateThumbnailAsync, but for a video that's already
    /// hosted somewhere (manually uploaded to R2 by the teacher) rather than
    /// one just received in this request. ffmpeg can read straight from an
    /// http(s) URL, so there's no local file to save/clean up here.
    /// </summary>
    private async Task<string?> TryGenerateThumbnailFromUrlAsync(string videoUrl)
    {
        var tempThumbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");
        try
        {
            var ok = await RunFfmpegAsync($"-y -ss 00:00:01 -i \"{videoUrl}\" -frames:v 1 -q:v 3 \"{tempThumbPath}\"");
            if (!ok || !System.IO.File.Exists(tempThumbPath))
                ok = await RunFfmpegAsync($"-y -i \"{videoUrl}\" -frames:v 1 -q:v 3 \"{tempThumbPath}\"");
            if (!ok || !System.IO.File.Exists(tempThumbPath)) return null;

            await using var thumbStream = System.IO.File.OpenRead(tempThumbPath);
            var thumbFormFile = new FormFile(thumbStream, 0, thumbStream.Length, "thumbnail", "thumbnail.jpg")
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/jpeg",
            };
            return await _files.SaveAsync(thumbFormFile, "lecture-thumbnails");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lecture thumbnail generation (from manual video URL) failed.");
            return null;
        }
        finally
        {
            TryDeleteTempFile(tempThumbPath);
        }
    }

    /// <summary>Runs ffmpeg with the given arguments; returns true on exit code 0, logging a warning (with stderr) otherwise.</summary>
    private async Task<bool> RunFfmpegAsync(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            _logger.LogWarning("Lecture thumbnail: ffmpeg Process.Start returned null (couldn't launch process — is ffmpeg installed and on PATH?).");
            return false;
        }

        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("Lecture thumbnail: ffmpeg exited with code {ExitCode}. Args: {Args}. stderr: {Stderr}",
                process.ExitCode, arguments, stderr);
            return false;
        }

        return true;
    }

    private async Task<string?> TryGenerateThumbnailAsync(IFormFile videoFile)
    {
        var tempVideoPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        var tempThumbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");

        try
        {
            await using (var fileStream = System.IO.File.Create(tempVideoPath))
            await using (var videoStream = videoFile.OpenReadStream())
            {
                await videoStream.CopyToAsync(fileStream);
            }

            var ok = await RunFfmpegAsync($"-y -ss 00:00:01 -i \"{tempVideoPath}\" -frames:v 1 -q:v 3 \"{tempThumbPath}\"");
            if (!ok || !System.IO.File.Exists(tempThumbPath))
            {
                // Falls back to the very first frame — the 1s seek above
                // fails on videos shorter than 1 second (common with short
                // test clips), since there's nothing at that timestamp.
                ok = await RunFfmpegAsync($"-y -i \"{tempVideoPath}\" -frames:v 1 -q:v 3 \"{tempThumbPath}\"");
            }
            if (!ok || !System.IO.File.Exists(tempThumbPath)) return null;

            await using var thumbStream = System.IO.File.OpenRead(tempThumbPath);
            var thumbFormFile = new FormFile(thumbStream, 0, thumbStream.Length, "thumbnail", "thumbnail.jpg")
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/jpeg",
            };

            return await _files.SaveAsync(thumbFormFile, "lecture-thumbnails");
        }
        catch (Exception ex)
        {
            // ffmpeg not installed, corrupt video, etc. — never let a
            // thumbnail failure block creating the lecture itself, but DO
            // log it so it's diagnosable instead of silently null forever.
            _logger.LogWarning(ex, "Lecture thumbnail generation failed.");
            return null;
        }
        finally
        {
            TryDeleteTempFile(tempVideoPath);
            TryDeleteTempFile(tempThumbPath);
        }
    }

    private static void TryDeleteTempFile(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch { /* best-effort cleanup only */ }
    }
}
