using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DataRunner.App.Converters;

/// <summary>
/// Returns true when <c>values[0]</c> and <c>values[1]</c> refer to the same instance.
/// Compares the inbox <c>SelectedItem</c> (from the view DataContext) with the current item.
/// </summary>
public sealed class ReferenceEqualsToBoolConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return false;
        if (values[0] == DependencyProperty.UnsetValue || values[1] == DependencyProperty.UnsetValue)
            return false;
        return ReferenceEquals(values[0], values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
