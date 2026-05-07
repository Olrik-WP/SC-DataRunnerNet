using System.Globalization;
using System.Windows.Data;

namespace DataRunner.App.Converters;

/// <summary>
/// Returns <c>true</c> when the bound string is <c>null</c>, empty, or
/// whitespace-only. Used by the Trade Routes view to flip the
/// Trader↔Datarunner slider's inertness hint between Visible and Collapsed
/// based on whether <c>DefaultSortMember</c> is set: empty = slider active,
/// non-empty = slider in-pause hint visible.
///
/// Pass <c>ConverterParameter=invert</c> to flip the result, mirroring the
/// pattern used by <see cref="BoolToVisibilityConverter"/> elsewhere in the app.
/// </summary>
public sealed class StringIsNullOrEmptyToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value is not string s || string.IsNullOrWhiteSpace(s);
        var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        return invert ? !isEmpty : isEmpty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
