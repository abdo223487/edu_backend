using System.Security.Claims;
using EduApi.Data;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Common;

public static class StudentSchoolYears
{
    /// <summary>
    /// The school year(s) the calling STUDENT belongs to FOR THE CURRENT TENANT (teacher).
    ///
    /// Why not just User.GetSchoolYear(): the "schoolYear" JWT claim is the legacy
    /// Student.SchoolYear, stamped once by whichever teacher created the student first
    /// and never updated when another teacher links the same student to their own group
    /// (see the PER-TENANT FIX notes in StudentsController). The year that matters for
    /// THIS teacher is the SchoolYear of the student's group(s) under this tenant:
    /// StudentGroupMemberships (+ the legacy Student.GroupId group). Both Group and
    /// StudentGroupMembership carry the tenant query filter, so only this teacher's
    /// groups come back. The claim is only a last-resort fallback (e.g. a student who
    /// got into this tenant purely through a code redemption and has no group here).
    /// </summary>
    public static async Task<List<int>> GetStudentSchoolYearsAsync(this AppDbContext db, ClaimsPrincipal user)
    {
        var me = user.GetUserId();

        var years = await db.StudentGroupMemberships.AsNoTracking()
            .Where(m => m.StudentId == me)
            .Select(m => m.Group!.SchoolYear)
            .ToListAsync();

        var legacyGroupId = await db.Students.AsNoTracking()
            .Where(s => s.Id == me)
            .Select(s => s.GroupId)
            .FirstOrDefaultAsync();
        if (legacyGroupId != 0)
        {
            years.AddRange(await db.Groups.AsNoTracking()
                .Where(g => g.Id == legacyGroupId)
                .Select(g => g.SchoolYear)
                .ToListAsync());
        }

        years = years.Distinct().ToList();
        if (years.Count == 0 && user.GetSchoolYear() is int claimYear)
            years.Add(claimYear);
        return years;
    }
}
