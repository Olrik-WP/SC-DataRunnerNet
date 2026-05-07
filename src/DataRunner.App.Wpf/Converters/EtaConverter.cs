using System.Globalization;
using System.Windows.Data;

namespace DataRunner.App.Converters;

/// <summary>
/// Formats a <see cref="TimeSpan"/> as a compact human-readable ETA, the
/// way UEX shows it on its trade-routes page:
///   • &lt; 1 min    → "Ns"
///   • &lt; 1 hour   → "Mm Ss"
///   • ≥ 1 hour      → "Hh Mm"
///   • zero / null   → "—"
///
/// Used by the Trade Routes ETA column. Kept null-tolerant because the
/// EstimatedTravelTime property returns <see cref="TimeSpan.Zero"/> for
/// intra-station hops where the route distance is unknown — those should
/// render as a dash, not "0s".
/// </summary>
public sealed class EtaConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TimeSpan ts || ts <= TimeSpan.Zero) return "—";

        if (ts.TotalMinutes < 1) return $"{ts.Seconds}s";
        if (ts.TotalHours < 1) return $"{ts.Minutes}m {ts.Seconds:00}s";
        return $"{(int)ts.TotalHours}h {ts.Minutes:00}m";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
