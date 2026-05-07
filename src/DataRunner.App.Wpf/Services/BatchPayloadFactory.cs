using System.IO;
using DataRunner.App.ViewModels;
using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataRunner.App.Services;

/// <summary>
/// Builds the wire <see cref="UexDataSubmitPayload"/> for a single
/// <see cref="PlannedSubmission"/>. Extracted from <see cref="BatchSubmitter"/>
/// so the same logic can be reused by:
///   - the actual sender (one POST per submission inside a batch),
///   - the preview dialog (so the user sees the EXACT JSON that will hit
///     UEX, screenshot bytes included, before they confirm Send all).
///
/// Keeping the build path in a single shared service guarantees the preview
/// can never drift from the real wire body — a class of bug that's a pain to
/// notice (UI says one thing, network sends another).
/// </summary>
public interface IBatchPayloadFactory
{
    UexDataSubmitPayload BuildPayload(PlannedSubmission planned, BatchOptions options);
}

public sealed class BatchPayloadFactory : IBatchPayloadFactory
{
    private readonly IAppPreferences _prefs;
    private readonly ILogger<BatchPayloadFactory> _logger;

    /// <summary>UEX hard limit for the `screenshot` field (10 MB raw → ~13.4 MB base64).</summary>
    private const long MaxScreenshotBytes = 10L * 1024 * 1024;

    public BatchPayloadFactory(IAppPreferences prefs, ILogger<BatchPayloadFactory> logger)
    {
        _prefs = prefs;
        _logger = logger;
    }

    public UexDataSubmitPayload BuildPayload(PlannedSubmission planned, BatchOptions options)
    {
        var item = planned.SourceItem;
        var submission = item.Submission ?? throw new InvalidOperationException(
            "PlannedSubmission.SourceItem.Submission is null — should have been gated upstream.");

        if (submission.Tab == TerminalTab.Unknown)
        {
            throw new InvalidOperationException(
                $"Cannot build /data_submit payload for screenshot #{planned.QueuePosition}: " +
                "Tab is Unknown — the user must pick BUY or SELL explicitly.");
        }

        var isBuyTab = submission.Tab == TerminalTab.Buy;
        var isProduction = item.DraftIsProduction ?? options.DefaultIsProduction;

        var payload = new UexDataSubmitPayload
        {
            IdTerminal = submission.IdTerminal ?? 0,
            Type = "commodity",
            IsProduction = isProduction ? 1 : 0,
            ContainerSizes = string.IsNullOrWhiteSpace(submission.ContainerSizes) ? null : submission.ContainerSizes.Trim(),
            GameVersion = ResolveGameVersion(item, options),
            Details = string.IsNullOrWhiteSpace(item.DraftDetails) ? null : item.DraftDetails!.Trim(),
            Meta = new PayloadMeta
            {
                Draft = false,
                SourceImage = Path.GetFileName(item.ImagePath),
                TerminalDisplayName = submission.TerminalDisplayName,
                TerminalMatchScore = submission.TerminalMatchScore,
                TerminalMatchedField = submission.TerminalMatchedField ?? "",
                TerminalMatchedFromOcr = submission.TerminalMatchedFromOcr ?? "",
                TabDetected = submission.Tab.ToString().ToLowerInvariant(),
            },
        };

        foreach (var r in planned.Rows)
        {
            if (r.IdCommodity is not int cid) continue;
            var row = new UexPriceRow { IdCommodity = cid };
            if (isBuyTab)
            {
                row.PriceBuy = r.PriceBuy;
                row.ScuBuy = r.ScuBuy;
                row.StatusBuy = r.StatusBuy == InventoryStatus.Unknown ? null : (int)r.StatusBuy;
            }
            else
            {
                row.PriceSell = r.PriceBuy;
                row.ScuSell = r.ScuBuy;
                row.StatusSell = r.StatusBuy == InventoryStatus.Unknown ? null : (int)r.StatusBuy;
            }
            payload.Prices.Add(row);
            payload.Meta.CommodityMatchScores.Add((int)Math.Round(r.CommodityMatchScore));
        }

        if (_prefs.AttachScreenshotOnSubmit)
        {
            payload.Screenshot = TryEncodeScreenshot(item.ImagePath);
        }

        return payload;
    }

    /// <summary>
    /// Resolves the <c>game_version</c> wire field for an item. User overrides
    /// (typed in the editor's "Optional metadata" panel) win; otherwise we
    /// fall back to the branch-resolved value provided by <see cref="BatchOptions"/>
    /// (cached at batch-start time so a single batch is internally consistent
    /// even if /game_versions refreshes mid-run).
    /// </summary>
    private static string? ResolveGameVersion(InboxItem item, BatchOptions options)
    {
        if (!string.IsNullOrWhiteSpace(item.DraftGameVersion))
            return item.DraftGameVersion!.Trim();

        return item.Branch == GameBranch.Ptu
            ? (string.IsNullOrWhiteSpace(options.PtuGameVersion) ? null : options.PtuGameVersion)
            : (string.IsNullOrWhiteSpace(options.LiveGameVersion) ? null : options.LiveGameVersion);
    }

    private string? TryEncodeScreenshot(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length == 0 || info.Length > MaxScreenshotBytes)
            {
                _logger.LogWarning(
                    "Screenshot {Path} is {Size} bytes — outside the UEX 10 MB limit, skipping attach.",
                    path, info.Length);
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            return Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to base64-encode screenshot {Path}", path);
            return null;
        }
    }
}
