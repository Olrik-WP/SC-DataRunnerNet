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

/// <summary>
/// Maps an <see cref="ViewModels.InboxStatus"/> to a tinted background brush
/// for the inbox card. Tints are intentionally subtle (15-20% opacity) so the
/// status colour does NOT clash with the WPF-UI selection highlight — the
/// selection accent always stays clearly visible on top.
///
///   Pending    → muted gray
///   Processing → soft blue (work in progress)
///   Ready      → muted green (clean, ready to send)
///   Review     → muted amber (needs user attention)
///   Validated  → vivid green tint (user-confirmed, queued for batch)
///   Sending    → bright blue tint (POST in flight)
///   Sent       → solid green tint + left bar (terminal state)
///   Failed     → muted red
/// </summary>
public sealed class InboxStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value?.ToString() ?? "";
        return status switch
        {
            "Processing" => Brush("#222F8FCB"),  // soft blue
            "Ready"      => Brush("#2532A852"),  // muted green
            "Review"     => Brush("#28DAA520"),  // muted amber
            "Validated"  => Brush("#552ECC71"),  // vivid green (confirmed)
            "Sending"    => Brush("#552F8FCB"),  // vivid blue (in flight)
            "Sent"       => Brush("#3532A852"),  // stronger green
            "Failed"     => Brush("#33C8504F"),  // muted red
            _            => Brush("#1AFFFFFF"),  // gray fallback (Pending)
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static System.Windows.Media.SolidColorBrush Brush(string hex)
    {
        var b = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>
/// Status → solid colour for the small status dot on each inbox card.
/// Brighter than the background tint so the dot is unmistakable at a glance.
/// </summary>
public sealed class InboxStatusToDotBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value?.ToString() ?? "";
        return status switch
        {
            "Processing" => System.Windows.Media.Brushes.SteelBlue,
            "Ready"      => System.Windows.Media.Brushes.MediumSeaGreen,
            "Review"     => System.Windows.Media.Brushes.Goldenrod,
            "Validated"  => System.Windows.Media.Brushes.LimeGreen,
            "Sending"    => System.Windows.Media.Brushes.DodgerBlue,
            "Sent"       => System.Windows.Media.Brushes.MediumSeaGreen,
            "Failed"     => System.Windows.Media.Brushes.IndianRed,
            _            => System.Windows.Media.Brushes.Gray,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps any non-null value to <see cref="Visibility.Visible"/>, null to
/// <see cref="Visibility.Collapsed"/>. Useful for "show this card only when
/// the bound object is non-null" without writing a custom converter per type.
/// Honour `parameter="invert"` to flip the logic.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        var visible = value is not null;
        if (invert) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
