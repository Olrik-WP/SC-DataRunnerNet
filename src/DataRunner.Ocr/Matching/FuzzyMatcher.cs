using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using FuzzySharp;

namespace DataRunner.Ocr.Matching;

/// <summary>
/// Fuzzy resolution of OCR fragments to UEX commodity / terminal entries.
/// Uses the live <see cref="ICatalogProvider"/> snapshot so refreshes (settings or
/// auto-update) are picked up automatically without restarting the OCR pipeline.
/// </summary>
public sealed class FuzzyMatcher
{
    private readonly ICatalogProvider _catalog;

    public FuzzyMatcher(ICatalogProvider catalog) => _catalog = catalog;

    public TerminalMatch? MatchTerminal(string raw, int minScore = 75)
    {
        var token = Normalize(raw);
        if (token.Length < 3) return null;

        TerminalMatch? best = null;

        foreach (var t in _catalog.CommodityTerminals)
        {
            var candidates = new[]
            {
                ("displayname", t.DisplayName),
                ("name", t.Name),
                ("nickname", t.Nickname),
                ("space_station_name", t.SpaceStationName ?? ""),
                ("outpost_name", t.OutpostName ?? ""),
                ("city_name", t.CityName ?? ""),
            };

            foreach (var (field, value) in candidates)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                var norm = Normalize(value);

                var weighted = Fuzz.WeightedRatio(token, norm);
                var partial = Fuzz.PartialRatio(token, norm);
                var tokenSet = Fuzz.TokenSetRatio(token, norm);
                var score = Math.Max(weighted, Math.Max(partial, tokenSet));

                if (best is null || score > best.Score)
                {
                    best = new TerminalMatch(t, score, field, raw);
                }
            }
        }

        return best is not null && best.Score >= minScore ? best : null;
    }

    public CommodityMatch? MatchCommodity(string raw, int minScore = 70)
    {
        var token = Normalize(raw);
        if (token.Length < 3) return null;

        // Short tokens (<=4 chars) are very risky: a 3-letter OCR fragment can match
        // dozens of UEX codes at >=80. Force a much stricter threshold for them.
        var effectiveMinScore = token.Length <= 4
            ? Math.Max(minScore, 95)
            : minScore;

        CommodityMatch? best = null;

        foreach (var c in _catalog.Commodities)
        {
            var candidates = new[]
            {
                ("name", c.Name),
                ("code", c.Code),
            };

            foreach (var (field, value) in candidates)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                var normValue = Normalize(value);
                var score = ScoreCommodityCandidate(token, normValue);
                if (best is null || score > best.Score)
                {
                    best = new CommodityMatch(c, score, field, raw);
                }
            }
        }

        return best is not null && best.Score >= effectiveMinScore ? best : null;
    }

    /// <summary>
    /// Computes a length-aware score between an OCR token and a catalog candidate.
    ///
    /// The default <see cref="Fuzz.WeightedRatio"/> includes a partial-substring
    /// bonus, which causes long OCR tokens to spuriously match short catalog names
    /// (eg. OCR "STINS" -> catalog "TIN" scores 90% because "TIN" is a substring of
    /// "STIN(S)"). We compensate by penalizing matches where the candidate is much
    /// shorter or much longer than the OCR token.
    ///
    /// Penalty curve: lengthRatio = min(len) / max(len).
    ///   lengthRatio &gt;= 0.75  -> no penalty (score kept as-is).
    ///   lengthRatio &lt;  0.75  -> score multiplied by lengthRatio (linear decay).
    ///
    /// Examples:
    ///   "STINS" (5) vs "STIMS" (5): ratio=1.0 -> no penalty. Score ~85 stays 85.
    ///   "STINS" (5) vs "TIN"   (3): ratio=0.6 -> 90 * 0.6 = 54. STIMS wins.
    ///   "BIOPLASTIC" (10) vs "BIOPLASTIC" (10): ratio=1.0, score=100.
    ///   "BIOPLASTIC" (10) vs "BIO" (3): ratio=0.3 -> 100 * 0.3 = 30, rejected.
    /// </summary>
    private static int ScoreCommodityCandidate(string token, string candidate)
    {
        var raw = Fuzz.WeightedRatio(token, candidate);

        var minLen = Math.Min(token.Length, candidate.Length);
        var maxLen = Math.Max(token.Length, candidate.Length);
        if (maxLen == 0) return 0;

        var lengthRatio = (double)minLen / maxLen;
        if (lengthRatio >= 0.75) return raw;

        return (int)Math.Round(raw * lengthRatio);
    }

    /// <summary>
    /// Tries to detect a terminal across multiple OCR lines by combining adjacent lines
    /// into 1-, 2-, or 3-line windows and keeping the best match.
    /// </summary>
    public TerminalMatch? MatchTerminalAcrossLines(IReadOnlyList<string> lines, int minScore = 75)
    {
        TerminalMatch? best = null;

        for (var i = 0; i < lines.Count; i++)
        {
            for (var span = 1; span <= 3 && i + span <= lines.Count; span++)
            {
                var combined = string.Join(' ', lines.Skip(i).Take(span));
                if (combined.Length < 4) continue;
                var match = MatchTerminal(combined, minScore: 0);
                if (match is null) continue;
                if (best is null || match.Score > best.Score)
                {
                    best = match;
                }
            }
        }

        return best is not null && best.Score >= minScore ? best : null;
    }

    private static string Normalize(string s) => s.Trim().ToUpperInvariant();
}

public sealed record TerminalMatch(UexTerminal Terminal, int Score, string MatchedField, string FromOcr);
public sealed record CommodityMatch(UexCommodity Commodity, int Score, string MatchedField, string FromOcr);
