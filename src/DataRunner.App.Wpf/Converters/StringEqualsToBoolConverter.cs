using System.Globalization;
using System.Windows.Data;

namespace DataRunner.App.Converters;

/// <summary>
/// Returns <c>true</c> when the bound string equals the converter
/// <c>parameter</c> (ordinal compare, null-tolerant). Used by the
/// Trade Routes view to light up the ★ favourite-sort star on the
/// column whose <c>SortMemberPath</c> matches the persisted
/// <c>DefaultSortMember</c>: a single converter instance feeds every
/// column's ToggleButton, the parameter discriminates which one.
/// </summary>
public sealed class StringEqualsToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var left = value as string;
        var right = parameter as string;
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
