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
                var score = Fuzz.WeightedRatio(token, Normalize(value));
                if (best is null || score > best.Score)
                {
                    best = new CommodityMatch(c, score, field, raw);
                }
            }
        }

        return best is not null && best.Score >= effectiveMinScore ? best : null;
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
