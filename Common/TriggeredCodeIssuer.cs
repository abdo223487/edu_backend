using EduApi.Data;
using EduApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Common;

/// <summary>
/// Shared by AttendanceController (issues clones going forward, right when a
/// new attendance is recorded) and CodesController (backfills clones for
/// students who already attended TriggerLectureId BEFORE the template was
/// created) so both directions of "who gets the code" -- past attendees and
/// future ones -- use the exact same clone/unlock logic and can never drift
/// apart.
/// </summary>
public static class TriggeredCodeIssuer
{
    /// <summary>
    /// Clones <paramref name="template"/> into a fresh, already-redeemed Code
    /// for <paramref name="studentId"/>, and grants the same unit/lecture
    /// unlocks a real redeem would. No-ops (returns false) if this student
    /// already has a clone of this template. Caller still owns SaveChangesAsync.
    /// </summary>
    public static async Task<bool> IssueOneAsync(AppDbContext db, Code template, int studentId)
    {
        var alreadyIssued = await db.Codes.AnyAsync(c =>
            c.SourceCodeTemplateId == template.Id && c.UsedByStudentId == studentId);
        if (alreadyIssued) return false;

        var issued = new Code
        {
            Value = await CodeGenerator.GenerateUniqueAsync(db),
            SchoolYear = template.SchoolYear,
            UnitIds = template.UnitIds,
            LectureIds = template.LectureIds,
            OnlineLessonIds = template.OnlineLessonIds,
            ExternalBookIds = template.ExternalBookIds,
            TeacherId = template.TeacherId,
            SourceCodeTemplateId = template.Id,
            IsUsed = true,
            UsedByStudentId = studentId,
            UsedAt = DateTime.UtcNow
        };
        db.Codes.Add(issued);

        foreach (var unitId in issued.UnitIds)
        {
            if (!await db.StudentUnitSubscriptions.AnyAsync(s => s.StudentId == studentId && s.UnitId == unitId))
                db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = template.TeacherId, StudentId = studentId, UnitId = unitId });
        }
        foreach (var lecId in issued.LectureIds)
        {
            if (!await db.StudentLectureUnlocks.AnyAsync(u => u.StudentId == studentId && u.LectureId == lecId))
                db.StudentLectureUnlocks.Add(new StudentLectureUnlock { TeacherId = template.TeacherId, StudentId = studentId, LectureId = lecId });
        }
        foreach (var onlineLessonId in issued.OnlineLessonIds)
        {
            if (!await db.StudentOnlineLessonUnlocks.AnyAsync(u => u.StudentId == studentId && u.OnlineLessonId == onlineLessonId))
                db.StudentOnlineLessonUnlocks.Add(new StudentOnlineLessonUnlock { TeacherId = template.TeacherId, StudentId = studentId, OnlineLessonId = onlineLessonId });
        }
        foreach (var externalBookId in issued.ExternalBookIds)
        {
            if (!await db.StudentExternalBookSubscriptions.AnyAsync(s => s.StudentId == studentId && s.ExternalBookId == externalBookId))
                db.StudentExternalBookSubscriptions.Add(new StudentExternalBookSubscription { TeacherId = template.TeacherId, StudentId = studentId, ExternalBookId = externalBookId });
        }

        return true;
    }

    /// <summary>
    /// Backfills clones of <paramref name="template"/> for every student who
    /// already has an Attendance row for TriggerLectureId as of right now --
    /// i.e. everyone who attended the Center lecture BEFORE this template
    /// existed. Used once, right after a TriggerLectureId template is
    /// created (see CodesController.Generate); going-forward attendees are
    /// covered separately by AttendanceController.IssueTriggeredCodesAsync.
    /// </summary>
    public static async Task IssueForExistingAttendeesAsync(AppDbContext db, Code template)
    {
        if (!template.TriggerLectureId.HasValue) return;

        var studentIds = await db.Attendances
            .Where(a => a.LectureId == template.TriggerLectureId.Value)
            .Select(a => a.StudentId)
            .Distinct()
            .ToListAsync();

        foreach (var studentId in studentIds)
            await IssueOneAsync(db, template, studentId);
    }
}
