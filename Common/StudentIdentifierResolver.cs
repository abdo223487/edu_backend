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
    /// canonical form, strips tashkeel/diacritics, and collapses any run of
    /// whitespace (extra spaces from mobile-keyboard autocorrect, tabs, etc.)
    /// down to a single space -- so e.g. "أحمد  محمد" (double space) and
    /// "احمد محمد" become byte-identical for comparison purposes.
    /// </summary>
    public static string NormalizeArabic(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var sb = new System.Text.StringBuilder(input.Length);
        var lastWasSpace = false;
        foreach (var ch in input)
        {
            // Arabic diacritics/tashkeel (fatha, damma, kasra, shadda, sukun,
            // tanween, superscript alef) -- never part of what someone types.
            if (ch is >= '\u064B' and <= '\u0652' or '\u0670')
                continue;

            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            lastWasSpace = false;

            sb.Append(ArabicNormalizationMap.TryGetValue(ch, out var mapped) ? mapped : ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Reduces a phone number down to just its digits, then strips a leading
    /// Egypt country-code/trunk-prefix variant (+20, 0020, or 20) and a
    /// leading 0, leaving just the bare subscriber number -- so
    /// "01012345678", "+201012345678", "0020 101 234 5678", and "1012345678"
    /// (same number typed 4 different ways, all real things people type) all
    /// normalize to the same core digits and compare equal. Previously phone
    /// matching was a byte-for-byte string comparison with none of this, so
    /// any of those harmless variations silently failed to match a student
    /// who was actually in the database under a differently-formatted number.
    /// </summary>
    private static string NormalizePhone(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var digits = new string(input.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return string.Empty;

        if (digits.StartsWith("0020")) digits = digits[4..];
        else if (digits.StartsWith("20") && digits.Length > 10) digits = digits[2..];

        if (digits.StartsWith('0')) digits = digits[1..];

        return digits;
    }

    /// <summary>
    /// Resolves a hand-typed identifier against <c>db.Students</c> in this
    /// order:
    ///   1) Numeric AND matches an existing Student.Id exactly.
    ///   2) Phone match, compared via NormalizePhone (digits-only, leading
    ///      0 / country-code stripped) -- see that method's doc comment for
    ///      why a raw string comparison was too strict.
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

        // FIX: was `s.PhoneNumber == identifier` -- an exact byte-for-byte
        // string comparison, so e.g. a stored "01012345678" would NOT match
        // a typed "1012345678" or "+201012345678", even though they're
        // obviously the same number. Now compares on NormalizePhone's
        // digits-only, prefix-stripped form on BOTH sides so all the common
        // ways people actually type a phone number match the same student.
        // Pulled client-side (not translated to SQL) since it's only ever a
        // handful of students per tenant -- cheap enough, and the normalize
        // logic isn't SQL-translatable anyway.
        var normalizedPhoneIdentifier = NormalizePhone(identifier);
        if (normalizedPhoneIdentifier.Length > 0)
        {
            var phoneCandidates = await students
                .Select(s => new { s.Id, s.PhoneNumber })
                .ToListAsync();
            var byPhone = phoneCandidates
                .FirstOrDefault(s => NormalizePhone(s.PhoneNumber) == normalizedPhoneIdentifier);
            if (byPhone != null) return byPhone.Id;
        }

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
