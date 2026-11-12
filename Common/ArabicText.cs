using System.Text;

namespace EduApi.Common;

/// <summary>
/// Small, dependency-free text helpers for Arabic (+ English) student questions: normalisation,
/// tokenising and TF-IDF cosine similarity. Used to group questions that mean the same thing.
/// </summary>
public static class ArabicText
{
    private static readonly string[] RawStopWords =
    {
        // Arabic function words (already normalised: alef forms -> ا, ى -> ي, ة -> ه)
        "في", "من", "الي", "على", "عن", "مع", "هذا", "هذه", "ده", "دي", "دا", "ذلك", "تلك", "هو", "هي", "هم",
        "انا", "انت", "احنا", "كان", "كانت", "يكون", "ان", "انه", "انها", "لو", "اذا", "ايه", "ايش", "ازاي",
        "ليه", "لما", "لماذا", "كيف", "ما", "ماذا", "هل", "او", "ثم", "كل", "بس", "قد", "لا", "لم", "لن", "مش",
        "يا", "استاذ", "مستر", "ميستر", "دكتور", "مدرس", "سوال", "السوال", "ممكن", "لو", "سمحت", "من", "فضلك",
        "عايز", "عاوز", "اعرف", "افهم", "شرح", "تشرح", "اشرح", "ازاى", "حضرتك", "الله", "يخليك", "جزاك", "خير",
        "شكرا", "بعد", "قبل", "عند", "هنا", "هناك", "ايضا", "برضو", "كده", "كدا", "دول", "اللي", "الذي", "التي",
        // English
        "the", "a", "an", "is", "are", "how", "what", "why", "can", "you", "please", "to", "of", "in", "on", "for", "and", "or",
        // dialect filler
        "تاني", "يعني", "عشان", "علشان", "لان", "حاجه", "بتاع", "بتاعه", "دلوقتي", "كمان", "طيب", "اه", "ايوه",
    };

    private static readonly HashSet<string> StopWords = new(RawStopWords.Select(w => Normalize(w).Trim()), StringComparer.Ordinal);

    /// <summary>Removes tashkeel/tatweel, unifies letter forms, maps Arabic-Indic digits, lowercases.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            var ch = raw;
            // tashkeel + tatweel
            if (ch is >= '\u064B' and <= '\u065F' or '\u0670' or '\u0640') continue;
            // Arabic-Indic and Eastern Arabic-Indic digits -> ASCII
            if (ch is >= '\u0660' and <= '\u0669') ch = (char)('0' + (ch - '\u0660'));
            else if (ch is >= '\u06F0' and <= '\u06F9') ch = (char)('0' + (ch - '\u06F0'));
            else ch = ch switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                'ؤ' => 'و',
                'ئ' => 'ي',
                _ => char.ToLowerInvariant(ch),
            };
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        return sb.ToString();
    }

    /// <summary>Light stemming: strips a leading و/ف/ب/ل/ك + "ال", and a few plural/feminine endings.</summary>
    private static string Stem(string w)
    {
        if (w.Length > 4 && (w.StartsWith("وال", StringComparison.Ordinal) || w.StartsWith("بال", StringComparison.Ordinal) || w.StartsWith("فال", StringComparison.Ordinal) || w.StartsWith("كال", StringComparison.Ordinal) || w.StartsWith("لل", StringComparison.Ordinal)))
            w = w.StartsWith("لل", StringComparison.Ordinal) ? w[2..] : w[3..];
        else if (w.Length > 3 && w.StartsWith("ال", StringComparison.Ordinal)) w = w[2..];

        foreach (var suffix in new[] { "ات", "ون", "ين", "ان", "ها", "هم" })
            if (w.Length > suffix.Length + 3 && w.EndsWith(suffix, StringComparison.Ordinal)) { w = w[..^suffix.Length]; break; }
        if (w.Length > 3 && w.EndsWith("ه", StringComparison.Ordinal)) w = w[..^1];
        return w;
    }

    public static List<string> Tokens(string? text)
    {
        var result = new List<string>();
        foreach (var part in Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (StopWords.Contains(part)) continue;
            var stem = Stem(part);
            if (stem.Length < 2 || StopWords.Contains(stem)) continue;
            result.Add(stem);
        }
        return result;
    }

    /// <summary>TF-IDF vector (L2-normalised) for each document, given every document's tokens.</summary>
    public static List<Dictionary<string, double>> TfIdf(IReadOnlyList<List<string>> docs)
    {
        var n = docs.Count;
        var df = new Dictionary<string, int>();
        foreach (var d in docs)
            foreach (var t in d.Distinct()) df[t] = df.GetValueOrDefault(t) + 1;

        var vectors = new List<Dictionary<string, double>>(n);
        foreach (var d in docs)
        {
            var v = new Dictionary<string, double>();
            foreach (var g in d.GroupBy(t => t))
            {
                var idf = Math.Log((n + 1.0) / (df[g.Key] + 1.0)) + 1.0;
                v[g.Key] = (1 + Math.Log(g.Count())) * idf;
            }
            Normalise(v);
            vectors.Add(v);
        }
        return vectors;
    }

    public static void Normalise(Dictionary<string, double> v)
    {
        var norm = Math.Sqrt(v.Values.Sum(x => x * x));
        if (norm <= 0) return;
        foreach (var k in v.Keys.ToList()) v[k] /= norm;
    }

    public static double Cosine(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var (small, big) = a.Count <= b.Count ? (a, b) : (b, a);
        double dot = 0;
        foreach (var kv in small)
            if (big.TryGetValue(kv.Key, out var w)) dot += kv.Value * w;
        return dot;
    }
}
