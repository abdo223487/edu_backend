using System.Text.Json;

namespace EduApi.Common;

/// <summary>
/// One switchable dashboard feature. A feature hides EVERY button that leads to
/// it on the teacher dashboard (quick actions, drawer, grid, top bar, ...), so
/// the SuperAdmin flips one switch per feature, not one per button.
/// The Flutter app uses the same <see cref="Key"/> strings
/// (lib/services/teacher_features.dart -> class TF).
/// </summary>
public record FeatureDef(string Key, string Label, string Group);

public static class TeacherFeatures
{
    public static readonly IReadOnlyList<FeatureDef> Catalog = new List<FeatureDef>
    {
        // الطلاب والحضور
        new("manage_students",  "إدارة وإضافة الطلاب",        "الطلاب والحضور"),
        new("my_students",      "طلابي",                      "الطلاب والحضور"),
        new("attendance",       "تسجيل الحضور وماسح QR",      "الطلاب والحضور"),
        new("dismissals",       "فصل الطلاب",                 "الطلاب والحضور"),

        // المحتوى
        new("add_video",        "رفع فيديو",                  "المحتوى"),
        new("courses",          "كورساتي",                    "المحتوى"),
        new("online_lessons",   "الحصص الأونلاين",            "المحتوى"),
        new("materials",        "ملفات PDF والماتريال",       "المحتوى"),
        new("external_books",   "كتب خارجية",                 "المحتوى"),
        new("drive",            "الدرايف",                    "المحتوى"),
        new("codes",            "أكواد الحصص",                "المحتوى"),

        // التقييم
        new("exams",            "الامتحانات",                 "التقييم"),
        new("assignments",      "الواجبات",                   "التقييم"),
        new("bank_questions",   "بنك الأسئلة",                "التقييم"),
        new("offline_grades",   "تسجيل الدرجات أوفلاين",      "التقييم"),

        // المالية والتواصل
        new("notebooks",        "المذكرات والمستحقات المالية", "المالية والتواصل"),
        new("notebook_payments","تسديد المستحقات",            "المالية والتواصل"),
        new("notifications",    "التنبيهات",                  "المالية والتواصل"),
        new("contact",          "التواصل",                    "المالية والتواصل"),

        // أخرى
        new("schedule",         "مواعيد الحصص",               "أخرى"),
        new("analytics",        "الإحصائيات",                 "أخرى"),
    };

    private static readonly HashSet<string> Keys = Catalog.Select(f => f.Key).ToHashSet();

    public static bool IsValid(string key) => Keys.Contains(key);

    /// <summary>"exams,drive" -> ["exams","drive"] (unknown/blank entries dropped).</summary>
    public static string[] Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Where(Keys.Contains)
                  .Distinct()
                  .ToArray();
    }

    /// <summary>
    /// The keys that must be HIDDEN for a tenant: if the tenant is on a plan,
    /// everything the plan does NOT include; otherwise the manual list.
    /// </summary>
    public static string[] EffectiveDisabled(string? manualRaw, Models.FeaturePlan? plan,
        string? swapInRaw = null, string? swapOutRaw = null)
    {
        if (plan == null) return Parse(manualRaw);

        // effective = (plan - swapped out) + swapped in
        var included = Parse(plan.FeatureKeys).ToHashSet();
        var (ins, outs) = SanitizeSwaps(plan, swapInRaw, swapOutRaw);
        foreach (var o in outs) included.Remove(o);
        foreach (var i in ins) included.Add(i);

        return Catalog.Where(f => !included.Contains(f.Key)).Select(f => f.Key).ToArray();
    }

    /// <summary>
    /// A teacher's stored swaps can go stale (SuperAdmin edits the plan or lowers
    /// its SwapLimit). Keep only valid pairs: swapIn must be OUTSIDE the plan,
    /// swapOut must be INSIDE it, counts match, and never more than SwapLimit.
    /// </summary>
    public static (string[] SwapIn, string[] SwapOut) SanitizeSwaps(
        Models.FeaturePlan plan, string? swapInRaw, string? swapOutRaw)
    {
        var planKeys = Parse(plan.FeatureKeys).ToHashSet();
        var ins = Parse(swapInRaw).Where(k => !planKeys.Contains(k)).ToList();
        var outs = Parse(swapOutRaw).Where(k => planKeys.Contains(k)).ToList();
        var n = Math.Min(Math.Min(ins.Count, outs.Count), Math.Max(0, plan.SwapLimit));
        return (ins.Take(n).ToArray(), outs.Take(n).ToArray());
    }

    /// <summary>{"students":"up to 500"} JSON -> dictionary (bad JSON / unknown keys / blanks dropped).</summary>
    public static Dictionary<string, string> ParseNotes(string? json)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (raw == null) return result;
            foreach (var (k, v) in raw)
                if (Keys.Contains(k) && !string.IsNullOrWhiteSpace(v)) result[k] = v.Trim();
        }
        catch (JsonException) { }
        return result;
    }

    /// <summary>dictionary -> JSON; empty -> null.</summary>
    public static string? SerializeNotes(IDictionary<string, string> notes)
    {
        var clean = notes
            .Where(kv => Keys.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
        return clean.Count == 0 ? null : JsonSerializer.Serialize(clean);
    }

    /// <summary>"a\nb\n" -> ["a","b"] (blank lines dropped).</summary>
    public static List<string> SplitLines(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? new List<string>()
            : raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>["a","b"] -> "a\nb"; empty -> null.</summary>
    public static string? JoinLines(IEnumerable<string> lines)
    {
        var list = lines.Select(l => (l ?? "").Trim()).Where(l => l.Length > 0).ToList();
        return list.Count == 0 ? null : string.Join("\n", list);
    }

    /// <summary>["exams","drive"] -> "drive,exams"; empty -> null (column stays NULL).</summary>
    public static string? Serialize(IEnumerable<string> keys)
    {
        var list = keys.Where(Keys.Contains).Distinct().OrderBy(k => k).ToList();
        return list.Count == 0 ? null : string.Join(",", list);
    }
}
