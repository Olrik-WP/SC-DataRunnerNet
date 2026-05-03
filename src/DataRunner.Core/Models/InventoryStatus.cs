namespace DataRunner.Core.Models;

/// <summary>
/// UEX inventory status mapping for `status_buy` / `status_sell`.
/// Reference: https://uexcorp.space/api/documentation/id/post_data_submit/
/// </summary>
public enum InventoryStatus
{
    Unknown = 0,
    OutOfStock = 1,
    VeryLow = 2,
    Low = 3,
    Medium = 4,
    High = 5,
    VeryHigh = 6,
    Maximum = 7,
}

public enum TerminalTab
{
    Unknown,
    Buy,
    Sell,
}
