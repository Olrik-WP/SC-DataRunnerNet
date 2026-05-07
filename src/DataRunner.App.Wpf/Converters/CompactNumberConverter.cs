using System.Globalization;
using System.Windows.Data;

namespace DataRunner.App.Converters;

/// <summary>
/// Formats a numeric value as a compact, glance-friendly string for the
/// trade-routes grid:
///   • &lt; 1 000        → "957"
///   • 1 000 – 999 999  → "57.6K"  (one decimal kept when meaningful)
///   • 1 M – 999 M      → "1.52M"
///   • ≥ 1 G            → "1.2B"
///
/// Negatives keep their sign. Zero renders as "0". Non-numeric values fall
/// through to <c>value.ToString()</c>.
///
/// Used on the Profit / aUEC-min columns where the display benefits more
/// from a quick scan than from per-aUEC precision (we already drop a
/// "57,557.30025" full-precision number two lines above where SmartNumber
/// would have put it). The raw double stays bound for sorting purposes.
/// Always uses en-US culture for the decimal separator so screenshots
/// from any locale render identically (matches the Star Citizen UI).
/// </summary>
public sealed class CompactNumberConverter : IValueConverter
{
    private static readonly CultureInfo Display = new("en-US");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return string.Empty;

        double d = value switch
        {
            double dv => dv,
            float fv => fv,
            decimal mv => (double)mv,
            int iv => iv,
            long lv => lv,
            _ => double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p)
                ? p : double.NaN,
        };

        if (double.IsNaN(d) || double.IsInfinity(d)) return value.ToString() ?? string.Empty;

        var sign = d < 0 ? "-" : "";
        var abs = Math.Abs(d);

        if (abs < 1_000) return sign + abs.ToString("N0", Display);
        if (abs < 1_000_000) return sign + Trim((abs / 1_000).ToString("F1", Display)) + "K";
        if (abs < 1_000_000_000) return sign + Trim((abs / 1_000_000).ToString("F2", Display)) + "M";
        return sign + Trim((abs / 1_000_000_000).ToString("F2", Display)) + "B";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    /// <summary>Strip trailing zero(s) and a dangling decimal point so "57.0" → "57", "1.20" → "1.2".</summary>
    private static string Trim(string s)
    {
        if (!s.Contains('.')) return s;
        return s.TrimEnd('0').TrimEnd('.');
    }
}
