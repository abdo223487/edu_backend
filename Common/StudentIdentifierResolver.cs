using EduApi.Data;
using EduApi.Models;
using Microsoft.EntityFrameworkCore;

namespace EduApi.Common;

/// <summary>
/// Resolves whatever a teacher manually typed for "which student" -- their
/// numeric Id, their phone number, or their (free-typed) name -- to a real
/// Student.Id. Shared by every endpoint that accepts a hand-typed student
/// identifier (grades, notebook/billing payments &amp; discounts, and
/// attendance manual entry / offline bulk sync) so the exact same matching
/// rules apply everywhere instead of drifting out of sync between controllers.
/// </summary>
public static class StudentIdentifierResolver
{
    // See NormalizeArabic below for what each mapping is for.
    private static readonly Dictionary<char, char> ArabicNormalizationMap = new()
    {
        ['أ'] = 'ا', ['إ'] = 'ا', ['آ'] = 'ا', ['ٱ'] = 'ا',
        ['ة'] = 'ه',
        ['ى'] = 'ي', ['ئ'] = 'ي',
        ['ؤ'] = 'و',
    };

    /// <summary>
    /// Collapses Arabic letter-shape variants that real users type
    /// interchangeably (أ/إ/آ/ٱ vs ا, ة vs ه, ى/ئ vs ي, ؤ vs و) down to one
    /// canonical form, and strips tashkeel/diacritics -- so e.g. "أحمد" and
    /// "احمد" become byte-identical for comparison purposes.
    /// </summary>
    public static string NormalizeArabic(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var sb = new System.Text.StringBuilder(input.Length);
        foreach (var ch in input)
        {
            // Arabic diacritics/tashkeel (fatha, damma, kasra, shadda, sukun,
            // tanween, superscript alef) -- never part of what someone types.
            if (ch is >= '\u064B' and <= '\u0652' or '\u0670')
                continue;

            sb.Append(ArabicNormalizationMap.TryGetValue(ch, out var mapped) ? mapped : ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Resolves a hand-typed identifier against <c>db.Students</c> in this
    /// order:
    ///   1) Numeric AND matches an existing Student.Id exactly.
    ///   2) Exact PhoneNumber match, compared as a STRING -- this is what
    ///      keeps a leading zero intact (e.g. "01012345678"). Parsing a
    ///      phone number to an int first (like the old id-or-phone fallback
    ///      used to) silently drops that leading zero, so the phone lookup
    ///      then never matches anything.
    ///   3) Name, Arabic-normalized (see NormalizeArabic). Only resolves
    ///      when exactly ONE student matches -- with zero or several
    ///      matches we can't safely guess which student was meant, so this
    ///      falls through to "not found" instead of silently picking one.
    /// Returns null if nothing (or more than one candidate by name) matched.
    ///
    /// <paramref name="ignoreTenantFilter"/>: pass true to match
    /// AttendanceController.ResolveManualStudentIdAsync's original
    /// cross-tenant behavior (a manually-entered identifier can resolve to
    /// a student under ANY tenant, not just the caller's). Pass false (the
    /// default) to respect whatever tenant query filter the caller's
    /// AppDbContext already has active -- e.g. StudentsController's grades
    /// and payment endpoints, which are meant to stay tenant-scoped.
    /// </summary>
    public static async Task<int?> ResolveAsync(AppDbContext db, string? identifierRaw, bool ignoreTenantFilter = false)
    {
        var identifier = identifierRaw?.Trim() ?? string.Empty;
        if (identifier.Length == 0) return null;

        IQueryable<Student> students = ignoreTenantFilter ? db.Students.IgnoreQueryFilters() : db.Students;

        if (int.TryParse(identifier, out var asId) &&
            await students.AnyAsync(s => s.Id == asId))
            return asId;

        var byPhone = await students
            .Where(s => s.PhoneNumber == identifier)
            .Select(s => (int?)s.Id)
            .FirstOrDefaultAsync();
        if (byPhone != null) return byPhone;

        var normalizedIdentifier = NormalizeArabic(identifier);
        if (normalizedIdentifier.Length == 0) return null;

        var nameCandidates = await students
            .Select(s => new { s.Id, s.Name })
            .ToListAsync();
        var nameMatches = nameCandidates
            .Where(s => NormalizeArabic(s.Name).Contains(normalizedIdentifier, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return nameMatches.Count == 1 ? nameMatches[0].Id : null;
    }
}
