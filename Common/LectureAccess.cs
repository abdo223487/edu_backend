using System.Security.Claims;
using EduApi.Data;
using EduApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Common;

public record LectureInfo(
    int Id, string Name, int? UnitId, int? LessonIndex, int? ViewLimit,
    string? StorageFileKey, string? YoutubeLink, int TeacherId,
    int? OnlineLessonId, int? ExternalBookId);

/// <summary>
/// One reusable answer to "may THIS user open THIS lecture?" for the new
/// engagement endpoints (playback position, bookmarks, Q&amp;A, offline), mirroring
/// the visibility rules of LecturesController.ByGroup:
///   staff   -> any lecture of their own tenant (the global tenant filter already scopes it)
///   student -> lecture unlocked by a code, OR unlocked through its OnlineLesson,
///              OR (member of one of the lecture's groups AND subscribed to its unit /
///              able to reach its external book).
/// Returns null when the lecture doesn't exist in the current tenant OR the user may not open it.
/// </summary>
public static class LectureAccess
{
    public static async Task<LectureInfo?> GetAsync(
        AppDbContext db, ClaimsPrincipal user, ITenantContext tenant, int lectureId, CancellationToken ct = default)
    {
        var l = await db.Lectures.AsNoTracking()
            .Where(x => x.Id == lectureId)
            .Select(x => new LectureInfo(x.Id, x.Name, x.UnitId, x.LessonIndex, x.ViewLimit,
                x.StorageFileKey, x.YoutubeLink, x.TeacherId, x.OnlineLessonId, x.ExternalBookId))
            .FirstOrDefaultAsync(ct);
        if (l == null) return null;

        if (!user.IsInRole(Roles.Student)) return l;

        var studentId = user.GetUserId();

        if (await db.StudentLectureUnlocks.AsNoTracking()
                .AnyAsync(u => u.StudentId == studentId && u.LectureId == lectureId, ct))
            return l;

        if (l.OnlineLessonId.HasValue &&
            await db.StudentOnlineLessonUnlocks.AsNoTracking()
                .AnyAsync(u => u.StudentId == studentId && u.OnlineLessonId == l.OnlineLessonId.Value, ct))
            return l;

        var gId = user.GetGroupId(tenant.CurrentTenantId);
        if (gId.HasValue &&
            !await db.LectureGroupLinks.AsNoTracking()
                .AnyAsync(x => x.LectureId == lectureId && x.GroupId == gId.Value, ct))
            return null;

        if (l.UnitId.HasValue)
        {
            var units = await StudentAccessHelpers.GetEffectiveUnitIdsAsync(db, user, studentId);
            if (units.Contains(l.UnitId.Value)) return l;
        }

        if (l.ExternalBookId.HasValue)
        {
            var books = await StudentAccessHelpers.GetEffectiveExternalBookIdsAsync(db, user, studentId);
            if (books.Contains(l.ExternalBookId.Value)) return l;
        }

        return null;
    }

    /// <summary>"File" when the lecture is an uploaded video, "Youtube" for a YouTube link, else null.</summary>
    public static string? SourceType(LectureInfo l)
        => !string.IsNullOrWhiteSpace(l.StorageFileKey) ? "File"
         : !string.IsNullOrWhiteSpace(l.YoutubeLink) ? "Youtube" : null;
}
