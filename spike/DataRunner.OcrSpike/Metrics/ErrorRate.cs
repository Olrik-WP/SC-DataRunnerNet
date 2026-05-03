namespace DataRunner.OcrSpike.Metrics;

public static class ErrorRate
{
    /// <summary>
    /// Character Error Rate. Lower is better. 0 = perfect, 1.0 = nothing matched.
    /// CER = Levenshtein(reference, hypothesis) / max(len(reference), 1)
    /// </summary>
    public static double Cer(string reference, string hypothesis)
    {
        var r = Normalize(reference);
        var h = Normalize(hypothesis);
        if (r.Length == 0)
        {
            return h.Length == 0 ? 0.0 : 1.0;
        }
        var distance = Levenshtein(r, h);
        return (double)distance / r.Length;
    }

    /// <summary>
    /// Word Error Rate (token-level). Lower is better.
    /// </summary>
    public static double Wer(string reference, string hypothesis)
    {
        var r = Tokenize(reference);
        var h = Tokenize(hypothesis);
        if (r.Length == 0)
        {
            return h.Length == 0 ? 0.0 : 1.0;
        }
        var distance = LevenshteinTokens(r, h);
        return (double)distance / r.Length;
    }

    private static string Normalize(string s)
    {
        return s.Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Trim();
    }

    private static string[] Tokenize(string s)
    {
        return Normalize(s)
            .Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static int Levenshtein(string a, string b)
    {
        var m = a.Length;
        var n = b.Length;
        if (m == 0) return n;
        if (n == 0) return m;

        var prev = new int[n + 1];
        var curr = new int[n + 1];

        for (var j = 0; j <= n; j++) prev[j] = j;

        for (var i = 1; i <= m; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= n; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[n];
    }

    private static int LevenshteinTokens(string[] a, string[] b)
    {
        var m = a.Length;
        var n = b.Length;
        if (m == 0) return n;
        if (n == 0) return m;

        var prev = new int[n + 1];
        var curr = new int[n + 1];

        for (var j = 0; j <= n; j++) prev[j] = j;

        for (var i = 1; i <= m; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= n; j++)
            {
                var cost = string.Equals(a[i - 1], b[j - 1], StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[n];
    }
}
