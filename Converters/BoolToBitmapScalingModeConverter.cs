using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace InkCanvasForPDF.Converters;

/// <summary>缩放交互状态 → 位图缩放模式：缩放中 Linear（快），停止 HighQuality（清晰）。</summary>
public sealed class BoolToBitmapScalingModeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? BitmapScalingMode.Linear : BitmapScalingMode.HighQuality;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
