using DataRunner.Core.Abstractions;
using DataRunner.Core.Models;

namespace DataRunner.UexClient;

/// <summary>
/// Static (offline) validator. Mirrors the documented UEX rules so we never knowingly
/// send something the server will reject. Performed BEFORE the duplicate check.
/// </summary>
public sealed class PayloadValidator : IPayloadValidator
{
    private readonly ICatalogProvider _catalog;
    private const double MaxPriceUaec = 50_000_000.0; // 50M aUEC/SCU sanity ceiling

    public PayloadValidator(ICatalogProvider catalog) => _catalog = catalog;

    public ValidationReport Validate(UexDataSubmitPayload payload)
    {
        var issues = new List<ValidationIssue>();

        // ---------- payload-level checks ----------
        if (payload.IdTerminal <= 0)
        {
            issues.Add(new(ValidationSeverity.Error, "terminal_missing",
                "id_terminal is required and must be a positive integer."));
        }
        else if (_catalog.GetTerminal(payload.IdTerminal) is null)
        {
            issues.Add(new(ValidationSeverity.Error, "terminal_unknown",
                $"id_terminal={payload.IdTerminal} is not present in the local UEX catalog."));
        }

        if (!string.Equals(payload.Type, "commodity", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new(ValidationSeverity.Error, "type_invalid",
                $"type='{payload.Type}' is not supported by this app (commodity only)."));
        }

        if (payload.IsProduction is not (0 or 1))
        {
            issues.Add(new(ValidationSeverity.Error, "is_production_invalid",
                $"is_production must be 0 or 1, got {payload.IsProduction}."));
        }

        if (payload.Prices.Count == 0)
        {
            issues.Add(new(ValidationSeverity.Error, "no_rows",
                "At least one price row is required."));
        }

        // ---------- per-row checks ----------
        var seen = new HashSet<int>();
        for (var i = 0; i < payload.Prices.Count; i++)
        {
            var p = payload.Prices[i];

            if (p.IdCommodity <= 0)
            {
                issues.Add(new(ValidationSeverity.Error, "commodity_missing",
                    "id_commodity is required.", i));
                continue;
            }
            if (_catalog.GetCommodity(p.IdCommodity) is null)
            {
                issues.Add(new(ValidationSeverity.Error, "commodity_unknown",
                    $"id_commodity={p.IdCommodity} is not present in the local UEX catalog.", i));
            }
            if (!seen.Add(p.IdCommodity))
            {
                issues.Add(new(ValidationSeverity.Error, "duplicate_row",
                    $"id_commodity={p.IdCommodity} appears more than once in the same payload.", i));
            }

            // Mutual exclusivity (UEX rule)
            var hasBuy = p.PriceBuy is not null || p.ScuBuy is not null || p.StatusBuy is not null;
            var hasSell = p.PriceSell is not null || p.ScuSell is not null || p.StatusSell is not null;
            if (hasBuy && hasSell)
            {
                issues.Add(new(ValidationSeverity.Error, "buy_sell_exclusive",
                    "Cannot mix buy_* and sell_* fields in the same row (UEX server will reject).", i));
            }
            if (!hasBuy && !hasSell && p.IsMissing != 1)
            {
                issues.Add(new(ValidationSeverity.Warning, "row_empty",
                    "Row has no price/scu/status and is not flagged is_missing=1.", i));
            }

            // Status range
            ValidateStatus(p.StatusBuy, "status_buy", i, issues);
            ValidateStatus(p.StatusSell, "status_sell", i, issues);

            // Price sanity
            ValidatePrice(p.PriceBuy, "price_buy", i, issues);
            ValidatePrice(p.PriceSell, "price_sell", i, issues);

            // SCU sanity
            ValidateScu(p.ScuBuy, "scu_buy", i, issues);
            ValidateScu(p.ScuSell, "scu_sell", i, issues);
        }

        var blocking = issues.Any(x => x.Severity == ValidationSeverity.Error);
        return new ValidationReport(blocking, issues);
    }

    private static void ValidateStatus(int? value, string field, int row, List<ValidationIssue> issues)
    {
        if (value is null) return;
        if (value < 1 || value > 7)
            issues.Add(new(ValidationSeverity.Error, "status_out_of_range",
                $"{field}={value} is outside the documented 1..7 range.", row));
    }

    private static void ValidatePrice(double? value, string field, int row, List<ValidationIssue> issues)
    {
        if (value is null) return;
        if (value < 0)
            issues.Add(new(ValidationSeverity.Error, "price_negative",
                $"{field}={value} cannot be negative.", row));
        else if (value == 0)
            issues.Add(new(ValidationSeverity.Warning, "price_zero",
                $"{field} is exactly 0; double-check before submitting.", row));
        else if (value > MaxPriceUaec)
            issues.Add(new(ValidationSeverity.Warning, "price_huge",
                $"{field}={value:F0} aUEC/SCU is unusually high; please double-check.", row));
    }

    private static void ValidateScu(int? value, string field, int row, List<ValidationIssue> issues)
    {
        if (value is null) return;
        if (value < 0)
            issues.Add(new(ValidationSeverity.Error, "scu_negative",
                $"{field}={value} cannot be negative.", row));
        else if (value > 100_000)
            issues.Add(new(ValidationSeverity.Warning, "scu_huge",
                $"{field}={value} SCU is unusually high; please double-check.", row));
    }
}
