using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace InkCanvasForPDF.Converters;

/// <summary>布尔值与可见性的反向转换：true -> Collapsed，false -> Visible。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
