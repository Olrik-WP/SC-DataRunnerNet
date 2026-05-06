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

        // Tab=Unknown means the colour-based detector couldn't decide BUY vs
        // SELL (eg. low saturation gap on amber-themed Pyro stations). The
        // editor view raises a BLOCKING validation error for this case so
        // the payload never reaches UEX — but headless / diagnostic callers
        // of this builder must also see the issue clearly. Tag the meta so
        // they can fail fast instead of silently mirroring SELL prices into
        // the BUY column.
        if (s.Tab == TerminalTab.Unknown)
        {
            payload.Meta.Warnings.Add("tab=unknown: side could not be auto-detected from screenshot. The user MUST pick Buy or Sell in the editor before submission.");
            payload.Meta.NeedsReview.Add("tab_unknown");
        }

        // The Buy column is the safe default ONLY for headless drafts that
        // the user will review in the editor. The ViewModel.BuildPayload
        // path has its own hard guard that throws if Tab is still Unknown
        // at submit time, so a malformed draft can't reach UEX undetected.
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
