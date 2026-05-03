using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DataRunner.App.Services;

namespace DataRunner.App.Converters;

/// <summary>
/// Maps an <see cref="OcrPipelineStatus"/> to the dot-color shown in the status bar.
/// </summary>
public sealed class OcrStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            OcrPipelineStatus.Idle => Brushes.Gray,
            OcrPipelineStatus.Initializing => Brushes.Goldenrod,
            OcrPipelineStatus.Ready => Brushes.LimeGreen,
            OcrPipelineStatus.Processing => Brushes.DodgerBlue,
            OcrPipelineStatus.Failed => Brushes.IndianRed,
            _ => Brushes.Gray,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
