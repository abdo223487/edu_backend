using EduApi.Data;
using EduApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Common;

/// <summary>
/// MULTI-TENANT: a Student can belong to a Group under several teachers at
/// once (StudentGroupMembership), so "this student's group name" is only
/// meaningful PER TENANT. These helpers resolve the group under the CURRENT
/// tenant (AppDbContext's own query filters already restrict
/// StudentGroupMemberships to the caller's tenant), for display purposes in
/// list/detail screens (attempts, takers, payment lists, codes redemption
/// list, etc.) that used to read the old single Student.GroupId/Group
/// directly and could show the wrong -- or a null -- group name for a student
/// who's also linked to a different teacher.
/// </summary>
public static class TenantDisplayHelpers
{
    /// <summary>Bulk lookup: studentId -> group name under the current tenant.</summary>
    public static async Task<Dictionary<int, string>> GetTenantGroupNamesAsync(
        this AppDbContext db, IEnumerable<int> studentIds)
    {
        var ids = studentIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<int, string>();

        return await db.StudentGroupMemberships
            .Where(m => ids.Contains(m.StudentId))
            .Select(m => new { m.StudentId, GroupName = m.Group!.Name })
            .ToDictionaryAsync(x => x.StudentId, x => x.GroupName);
    }

    /// <summary>Single lookup: group name for one student under the current tenant.</summary>
    public static async Task<string?> GetTenantGroupNameAsync(this AppDbContext db, int studentId)
    {
        return await db.StudentGroupMemberships
            .Where(m => m.StudentId == studentId)
            .Select(m => m.Group!.Name)
            .FirstOrDefaultAsync();
    }
}
