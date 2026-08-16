using EduApi.Data;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Common;

/// <summary>
/// Shared random-code generation, used by both a teacher's manual
/// Codes/Generate call and the automatic per-student clones issued by
/// AttendanceController when a student attends a code-triggering lecture.
/// </summary>
public static class CodeGenerator
{
    private const string Chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string GenerateRandom()
    {
        var random = Random.Shared;
        return new string(Enumerable.Range(0, 8).Select(_ => Chars[random.Next(Chars.Length)]).ToArray());
    }

    /// <summary>
    /// Same as GenerateRandom, but retries against the DB on the (very rare)
    /// chance of a collision. Worth the extra check here specifically
    /// because these codes are minted automatically, with no teacher review
    /// before a student receives one.
    ///
    /// NOTE: this pre-check is best-effort, not a hard guarantee -- the
    /// AnyAsync check and the caller's later INSERT are two separate steps,
    /// so two near-simultaneous calls could still both pass this check
    /// before either row commits. The real guarantee is the unique index on
    /// Code.Value in AppDbContext, which makes the database itself reject a
    /// duplicate INSERT outright. Callers that insert a Code built from this
    /// method's return value should catch DbUpdateException around their
    /// SaveChangesAsync and retry (call this method again for a fresh
    /// candidate) on that rare conflict -- this method only reduces how
    /// often that retry is needed, it doesn't eliminate the need for it.
    /// </summary>
    public static async Task<string> GenerateUniqueAsync(AppDbContext db)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = GenerateRandom();
            if (!await db.Codes.IgnoreQueryFilters().AnyAsync(c => c.Value == candidate))
                return candidate;
        }
        throw new InvalidOperationException("Could not generate a unique code after 10 attempts.");
    }
}
