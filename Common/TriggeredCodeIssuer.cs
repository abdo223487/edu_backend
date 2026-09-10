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
    /// Single-student, multi-template version of the same batching idea as
    /// IssueForExistingAttendeesAsync below. Called from
    /// AttendanceController.IssueTriggeredCodesAsync every time an
    /// attendance is recorded for a Center lecture that has one or more
    /// TriggerLectureId templates attached.
    ///
    /// PERF: calling IssueOneAsync in a loop here is N templates x (1
    /// already-issued check + ~1 GenerateUniqueAsync round-trip + up to 4
    /// per-unlock-type existence checks) sequential DB round-trips for a
    /// single attendance event. That's fine for ONE template, but a Center
    /// lecture can have several templates attached (e.g. one per unit
    /// unlocked by attending it), and this cost was scaling with that count
    /// on every single scan/manual-entry -- not just on the one-time
    /// backfill path. This version brings it back down to a small, fixed
    /// number of round-trips (one templates lookup, one already-issued
    /// lookup, one used-Values preload, one existence lookup per unlock
    /// type) no matter how many templates are attached to the lecture.
    /// </summary>
    public static async Task IssueForAttendeeAsync(AppDbContext db, List<Code> templates, int studentId)
    {
        if (templates.Count == 0) return;

        var templateIds = templates.Select(t => t.Id).ToList();

        var alreadyIssuedTemplateIds = (await db.Codes
            .Where(c => c.SourceCodeTemplateId.HasValue && templateIds.Contains(c.SourceCodeTemplateId.Value) && c.UsedByStudentId == studentId)
            .Select(c => c.SourceCodeTemplateId!.Value)
            .ToListAsync())
            .ToHashSet();

        var pending = templates.Where(t => !alreadyIssuedTemplateIds.Contains(t.Id)).ToList();
        if (pending.Count == 0) return;

        var usedValues = (await db.Codes.IgnoreQueryFilters().Select(c => c.Value).ToListAsync()).ToHashSet();

        // One existence-lookup query per unlock type, covering every
        // pending template's ids at once, instead of one query per
        // (template, id) pair.
        var allUnitIds = pending.SelectMany(t => t.UnitIds).Distinct().ToList();
        var existingUnitSubs = allUnitIds.Count == 0
            ? new HashSet<int>()
            : (await db.StudentUnitSubscriptions
                .Where(s => s.StudentId == studentId && allUnitIds.Contains(s.UnitId))
                .Select(s => s.UnitId).ToListAsync()).ToHashSet();

        var allLectureIds = pending.SelectMany(t => t.LectureIds).Distinct().ToList();
        var existingLectureUnlocks = allLectureIds.Count == 0
            ? new HashSet<int>()
            : (await db.StudentLectureUnlocks
                .Where(u => u.StudentId == studentId && allLectureIds.Contains(u.LectureId))
                .Select(u => u.LectureId).ToListAsync()).ToHashSet();

        var allOnlineLessonIds = pending.SelectMany(t => t.OnlineLessonIds).Distinct().ToList();
        var existingOnlineLessonUnlocks = allOnlineLessonIds.Count == 0
            ? new HashSet<int>()
            : (await db.StudentOnlineLessonUnlocks
                .Where(u => u.StudentId == studentId && allOnlineLessonIds.Contains(u.OnlineLessonId))
                .Select(u => u.OnlineLessonId).ToListAsync()).ToHashSet();

        var allExternalBookIds = pending.SelectMany(t => t.ExternalBookIds).Distinct().ToList();
        var existingExternalBookSubs = allExternalBookIds.Count == 0
            ? new HashSet<int>()
            : (await db.StudentExternalBookSubscriptions
                .Where(s => s.StudentId == studentId && allExternalBookIds.Contains(s.ExternalBookId))
                .Select(s => s.ExternalBookId).ToListAsync()).ToHashSet();

        foreach (var template in pending)
        {
            var issued = new Code
            {
                Value = GenerateUniqueLocally(usedValues),
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

            foreach (var unitId in template.UnitIds)
                if (existingUnitSubs.Add(unitId))
                    db.StudentUnitSubscriptions.Add(new StudentUnitSubscription { TeacherId = template.TeacherId, StudentId = studentId, UnitId = unitId });

            foreach (var lecId in template.LectureIds)
                if (existingLectureUnlocks.Add(lecId))
                    db.StudentLectureUnlocks.Add(new StudentLectureUnlock { TeacherId = template.TeacherId, StudentId = studentId, LectureId = lecId });

            foreach (var onlineLessonId in template.OnlineLessonIds)
                if (existingOnlineLessonUnlocks.Add(onlineLessonId))
                    db.StudentOnlineLessonUnlocks.Add(new StudentOnlineLessonUnlock { TeacherId = template.TeacherId, StudentId = studentId, OnlineLessonId = onlineLessonId });

            foreach (var externalBookId in template.ExternalBookIds)
                if (existingExternalBookSubs.Add(externalBookId))
                    db.StudentExternalBookSubscriptions.Add(new StudentExternalBookSubscription { TeacherId = template.TeacherId, StudentId = studentId, ExternalBookId = externalBookId });
        }

        // NOTE: same "Add() as both membership-test and mark-seen" reasoning
        // as IssueForExistingAttendeesAsync below -- each template is only
        // ever visited once in `pending`, so a given (unitId/lecId/etc.) can
        // only legitimately need to be added once across this whole loop
        // even when it's shared by more than one template.
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
