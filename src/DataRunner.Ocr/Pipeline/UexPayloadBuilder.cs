using DataRunner.Core.Models;

namespace DataRunner.Ocr.Pipeline;

/// <summary>
/// Converts our internal ParsedSubmission into the exact UEX POST /data_submit payload shape.
/// Always sets is_production=0 in pipeline mode (the UI flips it to 1 only after explicit confirmation).
/// The _meta block carries draft / review info for the UI; UexApiClient strips it before sending.
/// </summary>
public static class UexPayloadBuilder
{
    public static UexDataSubmitPayload Build(ParsedSubmission s)
    {
        var payload = new UexDataSubmitPayload
        {
            IdTerminal = s.IdTerminal ?? 0,
            Type = s.Type,
            IsProduction = 0,
            ContainerSizes = s.ContainerSizes,
            Meta = new PayloadMeta
            {
                Draft = true,
                SourceImage = s.SourceImage,
                TerminalDisplayName = s.TerminalDisplayName,
                TerminalMatchScore = s.TerminalMatchScore,
                TerminalMatchedFromOcr = s.TerminalMatchedFromOcr,
                TerminalMatchedField = s.TerminalMatchedField,
                TabDetected = s.Tab.ToString().ToLowerInvariant(),
                NeedsReview = new List<string>(s.NeedsReview),
            },
        };

        if (s.IdTerminal is null)
        {
            payload.Meta.Warnings.Add("id_terminal=0: NEVER submit until terminal is reviewed by a human.");
        }

        var isBuyTab = s.Tab is TerminalTab.Buy or TerminalTab.Unknown;

        foreach (var row in s.Prices)
        {
            if (row.IdCommodity is null) continue;

            var price = new UexPriceRow
            {
                IdCommodity = row.IdCommodity.Value,
            };

            if (isBuyTab)
            {
                price.PriceBuy = row.PriceBuy;
                price.ScuBuy = row.ScuBuy;
                price.StatusBuy = row.StatusBuy == InventoryStatus.Unknown ? null : (int)row.StatusBuy;
            }
            else
            {
                price.PriceSell = row.PriceBuy;
                price.ScuSell = row.ScuBuy;
                price.StatusSell = row.StatusBuy == InventoryStatus.Unknown ? null : (int)row.StatusBuy;
            }

            payload.Prices.Add(price);
            payload.Meta.CommodityMatchScores.Add((int)Math.Round(row.CommodityMatchScore));
        }

        if (payload.Prices.Count == 0)
        {
            payload.Meta.Warnings.Add("No price rows extracted; nothing to submit.");
        }

        return payload;
    }
}
