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

        // UI noise filter: a single OCR line that essentially IS one of the
        // SC commodity-screen UI labels (COMMODITIES, YOUR INVENTORIES, ...)
        // must never resolve to a terminal — those labels are static UI
        // chrome, not terminal identifiers.
        if (IsUiNoiseLabel(token)) return null;

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
    /// Minimum alphabetic-character content a sliding OCR window must
    /// carry before we even try to match it against the terminal catalog.
    /// 6 corresponds to roughly one short real word (eg. "EVERUS",
    /// "HICKES", "DEAKIN") — enough information for fuzzy ratio to be
    /// meaningful.
    ///
    /// Below this floor the retry pass on screenshots that hide the
    /// terminal name (eg. when an in-game HUD overlay covers the top
    /// banner) produces fragments like "FPS.", "tE", "1 2 4 8" — pure
    /// noise. Combining 2-3 such fragments into a 5-letter token like
    /// "FPS. TE" then gets spuriously matched to a long terminal name
    /// (observed: "FPS. TE" → "People's Service Station Theta", score
    /// 71) via PartialRatio / TokenSetRatio's substring-bonus heuristics,
    /// which happily find some 5-char overlap with any sufficiently long
    /// catalog candidate.
    /// </summary>
    private const int MinTerminalLetters = 6;

    /// <summary>
    /// Static SC commodity-screen UI labels that the OCR consistently
    /// pulls out of the LEFT panel header / right panel — they are NEVER
    /// terminal names, so we drop any sliding window whose normalized
    /// content fuzzy-matches one of them above
    /// <see cref="UiNoiseFuzzyThreshold"/>.
    /// <para>
    /// Without this filter the deeper left-header crop introduced for
    /// letterboxed captures starts catching strings like
    /// <c>"YOUR INVENTORIES"</c> and <c>"SELECT SUB-CATEGORY"</c>, which
    /// FuzzySharp's WeightedRatio happily mangles into matches like
    /// <c>"YOUR INVENTORIES" → "Orison"</c> at score 67 (observed on
    /// terminal_screenshot-5.jpg). Suppressing them at the matcher
    /// level keeps the recovery threshold loose enough to catch real
    /// terminals while throwing away garbage.
    /// </para>
    /// </summary>
    private static readonly string[] UiNoiseLabels =
    {
        "COMMODITIES",
        "YOUR INVENTORIES",
        "SHOP INVENTORY",
        "SELECT SUB CATEGORY",
        "SELECT SUB-CATEGORY",
        "IN DEMAND",
        "NO DEMAND",
        "CANNOT SELL",
        "LOCAL MARKET VALUE",
        "CURRENT BALANCE",
        "AVAILABLE CARGO SIZE",
        "CARGO SIZE",
        "OUT OF STOCK",
        "MAX INVENTORY",
        "MAXIMUM INVENTORY",
    };

    /// <summary>
    /// Fuzzy-similarity threshold above which an OCR window is considered
    /// a "UI noise" label and skipped before terminal matching. Tuned just
    /// loose enough to catch garbled OCR variants like
    /// <c>"YOURSIOVEHTORTES"</c> (= "YOUR INVENTORIES") while letting
    /// real terminal names through.
    /// </summary>
    private const int UiNoiseFuzzyThreshold = 75;

    /// <summary>
    /// True when the OCR fragment is so similar to a known SC UI label
    /// that matching it against the terminal catalog would always be a
    /// false positive. Centralizes the noise check so both single-line
    /// and multi-line matching paths share the same gate.
    /// </summary>
    private static bool IsUiNoiseLabel(string normalized)
    {
        foreach (var label in UiNoiseLabels)
        {
            if (Fuzz.WeightedRatio(normalized, label) >= UiNoiseFuzzyThreshold)
                return true;
        }
        return false;
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

                var letterCount = 0;
                foreach (var ch in combined)
                {
                    if (char.IsLetter(ch)) letterCount++;
                }
                if (letterCount < MinTerminalLetters) continue;

                // UI noise filter: drop windows that are essentially a SC
                // commodity-screen label (COMMODITIES, YOUR INVENTORIES,
                // IN DEMAND, ...). These would otherwise spuriously match
                // unrelated terminals via the substring/token-set bonus
                // in FuzzySharp.WeightedRatio.
                if (IsUiNoiseLabel(Normalize(combined))) continue;

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
