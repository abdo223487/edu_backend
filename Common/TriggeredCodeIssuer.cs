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
    /// covered separately by AttendanceController.IssueTriggeredCodesAsync
    /// (that one-at-a-time path is fine as-is: it only ever runs for ONE
    /// student per attendance event).
    ///
    /// PERF: this used to call IssueOneAsync in a loop, which is 4-6+
    /// sequential DB round-trips PER STUDENT (already-issued check, unique
    /// code generation, one existence check per unit/lecture/online-lesson/
    /// external-book id). With a lecture that already has many attendees,
    /// that turned a single "create code" request into hundreds of
    /// sequential round-trips and made the teacher's app hang on load for
    /// tens of seconds. This version does the same work with a small,
    /// FIXED number of queries no matter how many students are involved:
    /// one query per existing-row lookup (already-issued codes, existing
    /// unit/lecture/online-lesson/external-book unlocks) and one query to
    /// preload every currently-used Code.Value so new codes can be
    /// generated and de-duplicated in memory instead of round-tripping the
    /// DB for every single candidate.
    /// </summary>
    public static async Task IssueForExistingAttendeesAsync(AppDbContext db, Code template)
    {
        if (!template.TriggerLectureId.HasValue) return;

        var allStudentIds = await db.Attendances
            .Where(a => a.LectureId == template.TriggerLectureId.Value)
            .Select(a => a.StudentId)
            .Distinct()
            .ToListAsync();
        if (allStudentIds.Count == 0) return;

        // Skip students who already have a clone of this template (same
        // "one clone per student per template, ever" rule as IssueOneAsync).
        var alreadyIssuedStudentIds = await db.Codes
            .Where(c => c.SourceCodeTemplateId == template.Id && c.UsedByStudentId.HasValue)
            .Select(c => c.UsedByStudentId!.Value)
            .ToListAsync();
        var studentIds = allStudentIds.Except(alreadyIssuedStudentIds).ToList();
        if (studentIds.Count == 0) return;

        // Preload every Code.Value currently in use (across all tenants --
        // the Value column's unique index isn't tenant-scoped) so unique
        // codes can be generated locally, one DB hit total instead of one
        // per student.
        var usedValues = (await db.Codes.IgnoreQueryFilters().Select(c => c.Value).ToListAsync())
            .ToHashSet();

        var unitIds = template.UnitIds;
        var lectureIds = template.LectureIds;
        var onlineLessonIds = template.OnlineLessonIds;
        var externalBookIds = template.ExternalBookIds;

        // One existence-lookup query per unlock type, covering ALL affected
        // students at once, instead of one query per (student, id) pair.
        var existingUnitSubs = unitIds.Count == 0
            ? new HashSet<(int, int)>()
            : (await db.StudentUnitSubscriptions
                .Where(s => studentIds.Contains(s.StudentId) && unitIds.Contains(s.UnitId))
                .Select(s => new { s.StudentId, s.UnitId })
                .ToListAsync())
                .Select(x => (x.StudentId, x.UnitId)).ToHashSet();

        var existingLectureUnlocks = lectureIds.Count == 0
            ? new HashSet<(int, int)>()
            : (await db.StudentLectureUnlocks
                .Where(u => studentIds.Contains(u.StudentId) && lectureIds.Contains(u.LectureId))
                .Select(u => new { u.StudentId, u.LectureId })
                .ToListAsync())
                .Select(x => (x.StudentId, x.LectureId)).ToHashSet();

        var existingOnlineLessonUnlocks = onlineLessonIds.Count == 0
            ? new HashSet<(int, int)>()
            : (await db.StudentOnlineLessonUnlocks
                .Where(u => studentIds.Contains(u.StudentId) && onlineLessonIds.Contains(u.OnlineLessonId))
                .Select(u => new { u.StudentId, u.OnlineLessonId })
                .ToListAsync())
                .Select(x => (x.StudentId, x.OnlineLessonId)).ToHashSet();

        var existingExternalBookSubs = externalBookIds.Count == 0
            ? new HashSet<(int, int)>()
            : (await db.StudentExternalBookSubscriptions
                .Where(s => studentIds.Contains(s.StudentId) && externalBookIds.Contains(s.ExternalBookId))
                .Select(s => new { s.StudentId, s.ExternalBookId })
                .ToListAsync())
                .Select(x => (x.StudentId, x.ExternalBookId)).ToHashSet();

        foreach (var studentId in studentIds)
        {
            var value = GenerateUniqueLocally(usedValues);

            var issued = new Code
            {
                Value = value,
                SchoolYear = template.SchoolYear,
                UnitIds = unitIds,
                LectureIds = lectureIds,
                OnlineLessonIds = onlineLessonIds,
                ExternalBookIds = externalBookIds,
                TeacherId = template.TeacherId,
                SourceCodeTemplateId = template.Id,
                IsUsed = true,
                UsedByStudentId = studentId,
                UsedAt = DateTime.UtcNow
            };
            db.Codes.Add(issued);

            foreach (var unitId in unitIds)
                if (existingUnitSubs.Add((studentId, unitId)))
                    db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = template.TeacherId, StudentId = studentId, UnitId = unitId });

            foreach (var lecId in lectureIds)
                if (existingLectureUnlocks.Add((studentId, lecId)))
                    db.StudentLectureUnlocks.Add(new StudentLectureUnlock { TeacherId = template.TeacherId, StudentId = studentId, LectureId = lecId });

            foreach (var onlineLessonId in onlineLessonIds)
                if (existingOnlineLessonUnlocks.Add((studentId, onlineLessonId)))
                    db.StudentOnlineLessonUnlocks.Add(new StudentOnlineLessonUnlock { TeacherId = template.TeacherId, StudentId = studentId, OnlineLessonId = onlineLessonId });

            foreach (var externalBookId in externalBookIds)
                if (existingExternalBookSubs.Add((studentId, externalBookId)))
                    db.StudentExternalBookSubscriptions.Add(new StudentExternalBookSubscription { TeacherId = template.TeacherId, StudentId = studentId, ExternalBookId = externalBookId });
        }

        // NOTE: `existingUnitSubs.Add(...)` (and the other three sets) doing
        // double duty as both "have I seen this pair" AND "mark it seen now"
        // is intentional and safe here: each (studentId, unitId) pair is only
        // ever visited once across this whole loop (each student appears
        // once in studentIds, each id appears once in its list), so there's
        // no risk of a later iteration wrongly skipping a row because an
        // earlier iteration for a DIFFERENT pair already added it.
    }

    /// <summary>
    /// Same random-code generation as CodeGenerator.GenerateRandom, but
    /// checked for uniqueness against an in-memory set instead of a DB
    /// round-trip -- lets IssueForExistingAttendeesAsync mint many codes in
    /// one request without one query per candidate. The set is updated with
    /// the chosen value so the next call in the same batch can't collide
    /// with it either. The real guarantee is still the unique index on
    /// Code.Value in AppDbContext; this only makes collisions during a
    /// single SaveChangesAsync batch vanishingly unlikely, same spirit as
    /// CodeGenerator.GenerateUniqueAsync's DB-backed version.
    /// </summary>
    private static string GenerateUniqueLocally(HashSet<string> usedValues)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = CodeGenerator.GenerateRandom();
            if (usedValues.Add(candidate)) return candidate;
        }
        throw new InvalidOperationException("Could not generate a unique code after 20 attempts.");
    }
}
