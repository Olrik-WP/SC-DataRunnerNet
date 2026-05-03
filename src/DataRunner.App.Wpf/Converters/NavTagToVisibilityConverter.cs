using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DataRunner.App.Converters;

public sealed class NavTagToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var current = value?.ToString();
        var expected = parameter?.ToString();
        return string.Equals(current, expected, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);

        bool visible = value switch
        {
            bool b => b,
            int i => i > 0,
            string s => !string.IsNullOrWhiteSpace(s),
            null => false,
            _ => true,
        };

        if (invert) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class IntEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return Visibility.Collapsed;
        if (!int.TryParse(value.ToString(), out var v)) return Visibility.Collapsed;
        if (!int.TryParse(parameter.ToString(), out var p)) return Visibility.Collapsed;
        return v == p ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Renders a UTC <see cref="DateTimeOffset"/> as a friendly relative string:
/// "just now", "5 min ago", "2 h ago", "3 d ago". Returns "never" for null.
/// </summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "never";
        if (value is not DateTimeOffset at) return "never";

        var delta = DateTimeOffset.UtcNow - at;
        if (delta.TotalSeconds < 30) return "just now";
        if (delta.TotalMinutes < 1) return $"{(int)delta.TotalSeconds} s ago";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} min ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} h ago";
        if (delta.TotalDays < 30) return $"{(int)delta.TotalDays} d ago";
        return at.ToLocalTime().ToString("yyyy-MM-dd");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps a "days stale" int to a brush:
///   < 7  → muted gray
///   7-30 → goldenrod (warning)
///   30+  → indianred (urgent)
/// </summary>
public sealed class DaysStaleToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (!int.TryParse(value?.ToString(), out var days))
            return System.Windows.Media.Brushes.Gray;
        if (days >= 30) return System.Windows.Media.Brushes.IndianRed;
        if (days >= 7) return System.Windows.Media.Brushes.Goldenrod;
        return System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value?.ToString() ?? "";
        return name switch
        {
            "Block" or "Error" => System.Windows.Media.Brushes.IndianRed,
            "Warning" => System.Windows.Media.Brushes.Goldenrod,
            "Info" => System.Windows.Media.Brushes.SteelBlue,
            "Ok" => System.Windows.Media.Brushes.MediumSeaGreen,
            _ => System.Windows.Media.Brushes.Gray,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
