using EduApi.Common;
using EduApi.Data;
using EduApi.DTOs;
using EduApi.Models;
using EduApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace EduApi.Controllers;

/// <summary>
/// Route: api/OnlineLessons
///
/// A "OnlineLesson" is a standalone container — NOT a Unit, no Month, no
/// nested Lessons/lesson-index tree — created with just a Name, SchoolYear,
/// and a cover Image. A teacher uploads online lectures INTO it (this
/// controller's own /lectures endpoints, separate from LecturesController
/// since these lectures never have a UnitId or GroupIds). A student can
/// never browse into a container or see its lectures on their own; the only
/// way in is redeeming a Code that carries this container's Id (see
/// CodesController + StudentsController.RedeemCode), which grants a
/// StudentOnlineLessonUnlock covering every lecture inside it.
///
///  POST   OnlineLessons                              (multipart: Name, SchoolYear, Image REQUIRED)
///  POST   OnlineLessons/edit                          (multipart, partial: Id, Name?, Image?)
///  POST   OnlineLessons/delete?onlineLessonId=..
///  GET    OnlineLessons?schoolYear=..                 teacher: all; student: only unlocked ones
///  GET    OnlineLessons/{id}                          teacher: always; student: 403 unless unlocked
///  POST   OnlineLessons/{id}/lectures                  (multipart: Name, YoutubeLink? / VideoFile?)
///  POST   OnlineLessons/{id}/lectures/delete?lectureId=..
///  GET    OnlineLessons/{id}/lectures/{lectureId}/materials
///  POST   OnlineLessons/{id}/lectures/{lectureId}/materials/file    (multipart, field "Files", supports multiple)
///  POST   OnlineLessons/{id}/lectures/{lectureId}/materials/delete?materialId=..
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class OnlineLessonsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _files;
    private readonly ILogger<OnlineLessonsController> _logger;

    public OnlineLessonsController(AppDbContext db, IFileStorageService files, ILogger<OnlineLessonsController> logger)
    {
        _db = db;
        _files = files;
        _logger = logger;
    }

    // ─────────────────────────── Containers ───────────────────────────

    [HttpGet]
    public async Task<IActionResult> GetOnlineLessons([FromQuery] int? schoolYear)
    {
        var year = schoolYear ?? User.GetSchoolYear();
        var query = _db.OnlineLessons.AsNoTracking().AsQueryable();
        if (year.HasValue) query = query.Where(o => o.SchoolYear == year.Value);

        // Students never get the full catalog here (unlike Units, which now
        // shows locked ones too) — a container is code-gated on purpose, so
        // there's nothing to tease/preview. Only the ones they've already
        // redeemed a code for show up at all.
        if (User.IsInRole(Roles.Student))
        {
            var studentId = User.GetUserId();
            var unlockedIds = await _db.StudentOnlineLessonUnlocks.AsNoTracking()
                .Where(u => u.StudentId == studentId)
                .Select(u => u.OnlineLessonId)
                .ToListAsync();
            query = query.Where(o => unlockedIds.Contains(o.Id));
        }

        var items = await query
            .Select(o => new OnlineLessonListItem(o.Id, o.Name, o.SchoolYear, o.ImageUrl))
            .ToListAsync();

        return Ok(items);
    }

    [HttpGet("{onlineLessonId:int}")]
    public async Task<IActionResult> GetOnlineLesson(int onlineLessonId)
    {
        var lesson = await _db.OnlineLessons.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == onlineLessonId);
        if (lesson == null) return NotFound(new { message = "Online lesson not found." });

        if (User.IsInRole(Roles.Student))
        {
            var studentId = User.GetUserId();
            var unlocked = await _db.StudentOnlineLessonUnlocks.AsNoTracking()
                .AnyAsync(u => u.StudentId == studentId && u.OnlineLessonId == onlineLessonId);
            if (!unlocked)
                return StatusCode(403, new { message = "Redeem a code for this online lesson first." });
        }

        var lectureEntities = await _db.Lectures.AsNoTracking()
            .Where(l => l.OnlineLessonId == onlineLessonId)
            .OrderBy(l => l.CreatedAt)
            .ToListAsync();

        var lectureIds = lectureEntities.Select(l => l.Id).ToList();
        var materialsByLecture = await _db.Materials.AsNoTracking()
            .Where(m => m.LectureId != null && lectureIds.Contains(m.LectureId.Value))
            .ToListAsync();
        var materialsLookup = materialsByLecture
            .GroupBy(m => m.LectureId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(m => new MaterialListItem(m.Id, m.Name, m.Type, m.Link)).ToList());

        var lectures = lectureEntities
            .Select(l => ToDto(l, materialsLookup.TryGetValue(l.Id, out var mats) ? mats : new List<MaterialListItem>()))
            .ToList();

        return Ok(new OnlineLessonDetailDto(lesson.Id, lesson.Name, lesson.SchoolYear, lesson.ImageUrl, lectures));
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> CreateOnlineLesson(
        [FromForm] string name,
        [FromForm] int schoolYear,
        IFormFile image)
    {
        if (image == null || image.Length == 0)
            return BadRequest(new { message = "Image is required to create an Online Lesson." });

        var lesson = new OnlineLesson
        {
            Name = name,
            SchoolYear = schoolYear,
            TeacherId = User.GetStaffTenantId()!.Value, // TENANT LAYER
            ImageUrl = await _files.SaveAsync(image, "online-lessons")
        };

        _db.OnlineLessons.Add(lesson);
        await _db.SaveChangesAsync();

        return StatusCode(201, new OnlineLessonListItem(lesson.Id, lesson.Name, lesson.SchoolYear, lesson.ImageUrl));
    }

    [HttpPost("edit")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> EditOnlineLesson(
        [FromForm(Name = "Id")] int id,
        [FromForm(Name = "Name")] string? name,
        IFormFile? image)
    {
        var lesson = await _db.OnlineLessons.FirstOrDefaultAsync(e => e.Id == id);
        if (lesson == null) return NotFound(new { message = "Online lesson not found." });

        if (name != null) lesson.Name = name;

        if (image != null && image.Length > 0)
        {
            var oldImageUrl = lesson.ImageUrl;
            lesson.ImageUrl = await _files.SaveAsync(image, "online-lessons");
            await _db.SaveChangesAsync();
            await _files.DeleteAsync(oldImageUrl);
            return Ok(new OnlineLessonListItem(lesson.Id, lesson.Name, lesson.SchoolYear, lesson.ImageUrl));
        }

        await _db.SaveChangesAsync();
        return Ok(new OnlineLessonListItem(lesson.Id, lesson.Name, lesson.SchoolYear, lesson.ImageUrl));
    }

    [HttpPost("delete")]
    public async Task<IActionResult> DeleteOnlineLesson([FromQuery] int onlineLessonId)
    {
        var lesson = await _db.OnlineLessons.FirstOrDefaultAsync(e => e.Id == onlineLessonId);
        if (lesson == null) return NotFound(new { message = "Online lesson not found." });

        // Clean up the lectures inside it too, instead of leaving them
        // dangling with an OnlineLessonId that no longer resolves to
        // anything (same defensive cleanup StudentLectureUnlock rows get
        // below — an orphaned lecture would be permanently unreachable
        // junk, never shown anywhere again anyway).
        var childLectures = await _db.Lectures.Where(l => l.OnlineLessonId == onlineLessonId).ToListAsync();
        _db.Lectures.RemoveRange(childLectures);

        var unlocks = await _db.StudentOnlineLessonUnlocks.Where(u => u.OnlineLessonId == onlineLessonId).ToListAsync();
        _db.StudentOnlineLessonUnlocks.RemoveRange(unlocks);

        var oldContainerImageUrl = lesson.ImageUrl;
        _db.OnlineLessons.Remove(lesson);
        await _db.SaveChangesAsync();

        await _files.DeleteAsync(oldContainerImageUrl);
        foreach (var l in childLectures)
        {
            await _files.DeleteAsync(l.StorageFileKey);
            await _files.DeleteAsync(l.ThumbnailUrl);
        }

        return Ok(new { message = "Online lesson deleted." });
    }

    // ─────────────────────────── Lectures inside it ───────────────────────────

    // fields: Name, YoutubeLink? (text — also accepts a direct/manually-hosted
    // video URL, same flexible rule as LecturesController.Create)
    // file:   VideoFile (optional, .mp4) — exactly one of YoutubeLink/VideoFile required.
    // Always created as AttendanceMethod = Online, OnlineLessonId = {id}, no
    // UnitId, no GroupIds (unreachable through Lectures/by-group on purpose —
    // the only door in is the code-gated GET OnlineLessons/{id} above).
    [HttpPost("{onlineLessonId:int}/lectures")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> AddLecture(
        int onlineLessonId,
        [FromForm(Name = "Name")] string name,
        [FromForm(Name = "YoutubeLink")] string? youtubeLink,
        IFormFile? videoFile)
    {
        var lesson = await _db.OnlineLessons.AsNoTracking().FirstOrDefaultAsync(o => o.Id == onlineLessonId);
        if (lesson == null) return NotFound(new { message = "Online lesson not found." });

        var hasFile = videoFile != null && videoFile.Length > 0;
        var hasLink = !string.IsNullOrWhiteSpace(youtubeLink);
        if (hasFile == hasLink)
            return BadRequest(new { message = "Provide exactly one of VideoFile or YoutubeLink." });

        string? storageFileKey = null;
        string? finalYoutubeLink = null;
        string? thumbnailUrl = null;
        if (hasFile)
        {
            storageFileKey = await _files.SaveAsync(videoFile!, "online-lesson-videos");
            // Best-effort — never blocks lecture creation if it fails (see
            // TryGenerateThumbnailAsync below, same behavior as LecturesController).
            thumbnailUrl = await TryGenerateThumbnailAsync(videoFile!);
        }
        else
        {
            finalYoutubeLink = youtubeLink;
            // Only for a manually-hosted video URL, not an actual YouTube
            // link — YouTube already has its own thumbnail (the client uses
            // img.youtube.com/vi/{id}/0.jpg directly), so generating one
            // here would just be a wasted ffmpeg run.
            if (!IsYoutubeUrl(youtubeLink!))
                thumbnailUrl = await TryGenerateThumbnailFromUrlAsync(youtubeLink!);
        }

        var lecture = new Lecture
        {
            Name = name,
            AttendanceMethod = AttendanceMethod.Online,
            YoutubeLink = finalYoutubeLink,
            StorageFileKey = storageFileKey,
            ThumbnailUrl = thumbnailUrl,
            OnlineLessonId = onlineLessonId,
            SchoolYear = lesson.SchoolYear,
            TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
        };

        _db.Lectures.Add(lecture);
        await _db.SaveChangesAsync();

        return StatusCode(201, ToDto(lecture, new List<MaterialListItem>()));
    }

    // ─────────────────────────── Materials on a lecture inside it ───────────────────────────

    // Same idea as LecturesController's Materials endpoints, but scoped under
    // OnlineLessons so the access check is the container's code-gated
    // StudentOnlineLessonUnlock instead of a Unit subscription (these
    // lectures never have a UnitId).
    //  GET  OnlineLessons/{onlineLessonId}/lectures/{lectureId}/materials
    //  POST OnlineLessons/{onlineLessonId}/lectures/{lectureId}/materials/file       (multipart, field "Files", supports multiple)
    //  POST OnlineLessons/{onlineLessonId}/lectures/{lectureId}/materials/delete?materialId=..

    [HttpGet("{onlineLessonId:int}/lectures/{lectureId:int}/materials")]
    public async Task<IActionResult> GetLectureMaterials(int onlineLessonId, int lectureId)
    {
        var lecture = await _db.Lectures.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == lectureId && l.OnlineLessonId == onlineLessonId);
        if (lecture == null) return NotFound(new { message = "Lecture not found in this online lesson." });

        if (User.IsInRole(Roles.Student))
        {
            var studentId = User.GetUserId();
            var unlocked = await _db.StudentOnlineLessonUnlocks.AsNoTracking()
                .AnyAsync(u => u.StudentId == studentId && u.OnlineLessonId == onlineLessonId);
            if (!unlocked)
                return StatusCode(403, new { message = "Redeem a code for this online lesson first." });
        }

        var materials = await _db.Materials.AsNoTracking().Where(m => m.LectureId == lectureId)
            .Select(m => new MaterialListItem(m.Id, m.Name, m.Type, m.Link))
            .ToListAsync();

        return Ok(materials);
    }

    [HttpPost("{onlineLessonId:int}/lectures/{lectureId:int}/materials/file")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadLectureMaterials(
        int onlineLessonId,
        int lectureId,
        [FromForm(Name = "Files")] List<IFormFile> files)
    {
        var lecture = await _db.Lectures.FirstOrDefaultAsync(l => l.Id == lectureId && l.OnlineLessonId == onlineLessonId);
        if (lecture == null) return NotFound(new { message = "Lecture not found in this online lesson." });

        if (files == null || files.Count == 0)
            return BadRequest(new { message = "At least one file is required." });

        var created = new List<MaterialListItem>();
        foreach (var file in files)
        {
            if (file.Length == 0) continue;

            var url = await _files.SaveAsync(file, "materials");
            var material = new Material
            {
                Name = file.FileName,
                Type = "File",
                Link = url,
                LectureId = lectureId,
                SchoolYear = lecture.SchoolYear,
                TeacherId = User.GetStaffTenantId()!.Value // TENANT LAYER
            };
            _db.Materials.Add(material);
            created.Add(new MaterialListItem(material.Id, material.Name, material.Type, material.Link));
        }

        await _db.SaveChangesAsync();

        // Single-file uploads keep returning one object (same back-compat
        // rule as MaterialController.UploadFile); multi-file returns an array.
        return StatusCode(201, created.Count == 1 ? created[0] : created);
    }

    [HttpPost("{onlineLessonId:int}/lectures/{lectureId:int}/materials/delete")]
    public async Task<IActionResult> DeleteLectureMaterial(int onlineLessonId, int lectureId, [FromQuery] int materialId)
    {
        var lecture = await _db.Lectures.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == lectureId && l.OnlineLessonId == onlineLessonId);
        if (lecture == null) return NotFound(new { message = "Lecture not found in this online lesson." });

        var material = await _db.Materials.FirstOrDefaultAsync(m => m.Id == materialId && m.LectureId == lectureId);
        if (material == null) return NotFound(new { message = "Material not found." });

        _db.Materials.Remove(material);
        await _db.SaveChangesAsync();

        if (material.Type == "File")
            await _files.DeleteAsync(material.Link);

        return Ok(new { message = "Material deleted." });
    }

    [HttpPost("{onlineLessonId:int}/lectures/delete")]
    public async Task<IActionResult> DeleteLecture(int onlineLessonId, [FromQuery] int lectureId)
    {
        var lecture = await _db.Lectures
            .FirstOrDefaultAsync(l => l.Id == lectureId && l.OnlineLessonId == onlineLessonId);
        if (lecture == null) return NotFound(new { message = "Lecture not found in this online lesson." });

        var oldFileUrl = lecture.StorageFileKey;
        var oldThumbnailUrl = lecture.ThumbnailUrl;
        _db.Lectures.Remove(lecture);
        await _db.SaveChangesAsync();
        await _files.DeleteAsync(oldFileUrl);
        await _files.DeleteAsync(oldThumbnailUrl);

        return Ok(new { message = "Lecture deleted." });
    }

    private static OnlineLectureDto ToDto(Lecture l, List<MaterialListItem> materials)
    {
        var (link, sourceType) = PlaybackInfo(l.StorageFileKey, l.YoutubeLink);
        return new(l.Id, l.Name, link, sourceType, l.ThumbnailUrl, l.CreatedAt, materials);
    }

    /// <summary>Same rule as LecturesController.PlaybackInfo: at most one of StorageFileKey/YoutubeLink is ever set.</summary>
    private static (string? Link, string? SourceType) PlaybackInfo(string? storageFileUrl, string? youtubeLink)
    {
        if (!string.IsNullOrWhiteSpace(storageFileUrl)) return (storageFileUrl, "File");
        if (!string.IsNullOrWhiteSpace(youtubeLink)) return (youtubeLink, "Youtube");
        return (null, null);
    }

    /// <summary>True for youtube.com/youtu.be links; false for anything else (including a manually-hosted R2/other video URL).</summary>
    private static bool IsYoutubeUrl(string url) =>
        url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts a single frame (1 second in) from an uploaded .mp4 via
    /// ffmpeg, uploads it to R2 as a JPEG, and returns its public URL.
    /// Requires ffmpeg installed and on PATH — if it's missing, the frame
    /// extraction fails, or anything else goes wrong, this returns null
    /// rather than throwing, since a lecture is still perfectly usable
    /// without a thumbnail. Mirrors LecturesController.TryGenerateThumbnailAsync.
    /// </summary>
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
                // fails on videos shorter than 1 second.
                ok = await RunFfmpegAsync($"-y -i \"{tempVideoPath}\" -frames:v 1 -q:v 3 \"{tempThumbPath}\"");
            }
            if (!ok || !System.IO.File.Exists(tempThumbPath)) return null;

            await using var thumbStream = System.IO.File.OpenRead(tempThumbPath);
            var thumbFormFile = new FormFile(thumbStream, 0, thumbStream.Length, "thumbnail", "thumbnail.jpg")
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/jpeg",
            };

            return await _files.SaveAsync(thumbFormFile, "online-lesson-thumbnails");
        }
        catch (Exception ex)
        {
            // ffmpeg not installed, corrupt video, etc. — never let a
            // thumbnail failure block creating the lecture itself, but DO
            // log it so it's diagnosable instead of silently null forever.
            _logger.LogWarning(ex, "Online lesson lecture thumbnail generation failed.");
            return null;
        }
        finally
        {
            TryDeleteTempFile(tempVideoPath);
            TryDeleteTempFile(tempThumbPath);
        }
    }

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
            return await _files.SaveAsync(thumbFormFile, "online-lesson-thumbnails");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Online lesson lecture thumbnail generation (from manual video URL) failed.");
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
            _logger.LogWarning("Online lesson thumbnail: ffmpeg Process.Start returned null (couldn't launch process — is ffmpeg installed and on PATH?).");
            return false;
        }

        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("Online lesson thumbnail: ffmpeg exited with code {ExitCode}. Args: {Args}. stderr: {Stderr}",
                process.ExitCode, arguments, stderr);
            return false;
        }

        return true;
    }

    private static void TryDeleteTempFile(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch { /* best-effort cleanup only */ }
    }
}
