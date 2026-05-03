using System.Globalization;
using System.Windows.Data;

namespace DataRunner.App.Converters;

/// <summary>
/// Formats a numeric value (double, int, decimal, nullable variants) for the
/// commodity grid:
///   - Round values     -> "395"    "21,600"   (no trailing decimals)
///   - Fractional values -> "1,425.99999"      (keep up to 5 decimals, strip trailing zeros)
///
/// Always uses en-US culture for the thousand separator, so screenshots from any
/// locale render identically (matches Star Citizen UI: comma = thousands, dot = decimal).
///
/// ConvertBack accepts user input with or without separators.
/// </summary>
public sealed class SmartNumberConverter : IValueConverter
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

        // Round-trip threshold: 1e-6 keeps "1425.99999" untouched but treats
        // "323.0000000000001" as 323.
        if (Math.Abs(d - Math.Round(d)) < 1e-6)
        {
            return d.ToString("N0", Display);
        }

        // Up to 5 decimals, strip trailing zeros (and the dot if all zeros got removed).
        var s = d.ToString("N5", Display);
        if (s.Contains('.'))
        {
            s = s.TrimEnd('0').TrimEnd('.');
        }
        return s;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return null;

        var cleaned = s.Replace(",", "").Replace(" ", "").Trim();

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying == typeof(int))
        {
            return int.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var i)
                ? (object)i
                : null;
        }
        if (underlying == typeof(long))
        {
            return long.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var l)
                ? (object)l
                : null;
        }
        if (underlying == typeof(decimal))
        {
            return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var m)
                ? (object)m
                : null;
        }
        // Default: double
        return double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? (object)d
            : null;
    }
}
