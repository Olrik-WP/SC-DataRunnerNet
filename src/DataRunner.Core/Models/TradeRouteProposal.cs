namespace DataRunner.Core.Models;

/// <summary>
/// A <see cref="UexCommodityRoute"/> enriched with our local datarunner overlay:
///  - <see cref="StaleAtOrigin"/> / <see cref="StaleAtDestination"/>: how many rows
///    in the local <c>IStaleTargetProvider</c> would be refreshed by visiting each
///    end of the route, AFTER subtracting recent local submissions
///    (<see cref="StaleExcludedRecentlySubmitted"/>).
///  - <see cref="DatarunnerScore"/>: a normalised score in [0..1] combining
///    UEX profit and stale-refresh count, weighted by the user's "Trader ↔ Datarunner"
///    slider. Mutable so the slider can recompute without rebuilding the whole list.
///
/// The UEX-trade-relevant fields (profit, ROI, score, volatility, etc.) live on
/// <see cref="Route"/> and are NEVER recomputed on our side — single source of truth.
/// </summary>
public sealed class TradeRouteProposal
{
    public required UexCommodityRoute Route { get; init; }

    /// <summary>Stale rows currently tracked at the route's origin terminal (excluding local recent submissions).</summary>
    public int StaleAtOrigin { get; set; }

    /// <summary>Stale rows currently tracked at the route's destination terminal (excluding local recent submissions).</summary>
    public int StaleAtDestination { get; set; }

    /// <summary>
    /// Number of (terminal, commodity) pairs that WOULD have counted as stale but
    /// were excluded because the user successfully submitted them within the last
    /// 6 hours. Surfaced in the column tooltip so the user understands why the
    /// raw count differs from "all stale rows at this terminal".
    /// </summary>
    public int StaleExcludedRecentlySubmitted { get; set; }

    /// <summary>Total stale refresh potential = origin + destination (excludes already-submitted).</summary>
    public int TotalStale => StaleAtOrigin + StaleAtDestination;

    // ---- Age, route-specific ---------------------------------------------
    // These match what the UEX website displays in the trade-routes grid for
    // this exact (commodity × origin × destination) row: how old is the
    // commodity's BUY price at origin, and how old is its SELL price at
    // destination. Used by the headline badge so the number aligns with what
    // the user can verify on UEX.

    /// <summary>Age (days) of the route's commodity BUY price at the origin terminal.</summary>
    public int DaysStaleAtOriginThisCommodity { get; set; }

    /// <summary>Age (days) of the route's commodity SELL price at the destination terminal.</summary>
    public int DaysStaleAtDestinationThisCommodity { get; set; }

    /// <summary>Worst-case freshness for THIS specific trade — the headline number in
    /// the UI badge. Maps 1:1 to the UEX route grid so the user sees the same age
    /// in both places.</summary>
    public int MaxDaysStaleThisRoute
        => Math.Max(DaysStaleAtOriginThisCommodity, DaysStaleAtDestinationThisCommodity);

    // ---- Age, terminal-wide (any commodity) ------------------------------
    // These describe the "datarunner refresh potential" of the route's
    // endpoints, regardless of the traded commodity: by physically visiting
    // these two terminals, what's the OLDEST piece of data anywhere there
    // that you'd refresh? Surfaced in the tooltip, not the headline.

    /// <summary>Oldest <c>DaysStale</c> across ALL reachable commodities tracked at
    /// the origin terminal (any direction, after submission-history exclusion).</summary>
    public int MaxDaysStaleAtOriginAnyCommodity { get; set; }

    /// <summary>Oldest <c>DaysStale</c> across ALL reachable commodities tracked at
    /// the destination terminal (any direction, after submission-history exclusion).</summary>
    public int MaxDaysStaleAtDestinationAnyCommodity { get; set; }

    /// <summary>Worst-case staleness considering ALL commodities at both endpoints.</summary>
    public int MaxDaysStaleAnyCommodity
        => Math.Max(MaxDaysStaleAtOriginAnyCommodity, MaxDaysStaleAtDestinationAnyCommodity);

    /// <summary>
    /// Normalised composite score in [0..1] used by the "Trader ↔ Datarunner"
    /// slider for sorting. Recomputed in place by the view-model when the slider
    /// moves; do NOT persist this value to disk (it is purely UI-derived).
    /// </summary>
    public double DatarunnerScore { get; set; }

    // ---------------------------------------------------------------------
    // BUDGET-CAPPED PROJECTION
    //
    // UEX's <see cref="Route"/> reports profit/investment for the whole stock
    // at origin (<c>scu_origin × price_origin</c>). When the user has a budget
    // cap, that's almost always more than they can actually buy — e.g. Scrap
    // at CRU-L1 has 2100 SCU × 2990 = 6.3M of stock, but a 300K budget only
    // buys 100 SCU. The UEX website caps SCU client-side and shows the
    // partial profit (≈ 161K instead of 1.49M); we mirror that behaviour.
    //
    // These fields are populated by the provider after fetching, so the rest
    // of the app (DataGrid columns, scoring) can read them directly.
    // ---------------------------------------------------------------------

    /// <summary>SCU actually buyable within the user's budget cap (≤ <c>scu_origin</c>).</summary>
    public int EffectiveScu { get; set; }

    /// <summary>UEC actually invested within the budget cap (= <see cref="EffectiveScu"/> × <c>price_origin</c>).</summary>
    public double EffectiveInvestment { get; set; }

    /// <summary>UEC profit at the budget-capped quantity (= <see cref="EffectiveScu"/> × <c>price_margin</c>).</summary>
    public double EffectiveProfit { get; set; }

    // ---------------------------------------------------------------------
    // ETA — derived, not from UEX.
    //
    // UEX shows an ETA column on its trade-routes page; we mirror it here by
    // computing travel time from <see cref="UexCommodityRoute.Distance"/>
    // (Gm) using a fixed civilian quantum-drive baseline (175 Mm/s ≈ the
    // average of the meta haulers like C2 / Caterpillar / Hull C). We keep
    // a small spool/calibration overhead so very-short routes don't display
    // a meaninglessly tiny "1s" — the cargo-loading delay alone dwarfs that.
    //
    // Per-vehicle quantum speeds aren't exposed by /vehicles (the field is
    // present in the schema but always 0 in the live data), so a constant
    // is the most honest approximation we can make. Trade-route comparisons
    // are still meaningful because the constant cancels out — every route
    // is rated against the same ship.
    // ---------------------------------------------------------------------

    /// <summary>Quantum-drive baseline used by <see cref="EstimatedTravelTime"/>. Mégamètres per second.</summary>
    public const double QuantumSpeedMmPerSec = 175.0;

    /// <summary>Spool + calibration overhead added on top of pure travel time.</summary>
    public static readonly TimeSpan QuantumOverhead = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Estimated quantum travel time between origin and destination. Derived
    /// from <see cref="UexCommodityRoute.Distance"/> using <see cref="QuantumSpeedMmPerSec"/>.
    /// Returns <see cref="TimeSpan.Zero"/> when the route distance is unknown
    /// or non-positive (intra-station hops with distance=0).
    /// </summary>
    public TimeSpan EstimatedTravelTime
    {
        get
        {
            if (Route.Distance <= 0) return TimeSpan.Zero;
            // 1 Gm == 1000 Mm, so seconds = (Gm * 1000) / (Mm/s).
            var seconds = (Route.Distance * 1000.0) / QuantumSpeedMmPerSec;
            return TimeSpan.FromSeconds(seconds) + QuantumOverhead;
        }
    }

    /// <summary>
    /// Effective profit per minute of travel (aUEC/min). Falls back to
    /// <see cref="EffectiveProfit"/> when the route has no measurable
    /// distance — those routes happen instantly so per-minute math is
    /// undefined, and treating them as "infinite efficiency" would distort
    /// the filter / sort. Returning the raw profit is a sane proxy: an
    /// instant trade still surfaces near the top of any "min profit/min"
    /// filter the user sets to a reasonable threshold.
    /// </summary>
    public double ProfitPerMinute
    {
        get
        {
            var minutes = EstimatedTravelTime.TotalMinutes;
            return minutes > 0 ? EffectiveProfit / minutes : EffectiveProfit;
        }
    }
}
