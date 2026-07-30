using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LibreGuard_VPN_Desktop.Converters;

/// <summary>
/// Converts ping ms value to a color: green (&lt;100), blue (&lt;250), yellow (250+).
/// </summary>
public sealed class PingToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush s_good = new(Color.FromRgb(0x10, 0xB9, 0x81)); // Green
    private static readonly SolidColorBrush s_medium = new(Color.FromRgb(0x3B, 0x82, 0xF6)); // Blue
    private static readonly SolidColorBrush s_poor = new(Color.FromRgb(0xF5, 0x9E, 0x0B)); // Yellow

    static PingToBrushConverter()
    {
        s_good.Freeze();
        s_medium.Freeze();
        s_poor.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int ping ? ping switch
        {
            < 100 => s_good,
            < 250 => s_medium,
            _ => s_poor,
        } : s_poor;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
