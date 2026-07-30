using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LibreGuard_VPN_Desktop.Converters;

/// <summary>
/// Converts server load percentage to a color: green (&lt;40), amber (&lt;70), red (70+).
/// </summary>
public sealed class LoadToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush s_low = new(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly SolidColorBrush s_medium = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush s_high = new(Color.FromRgb(0xEF, 0x44, 0x44));

    static LoadToBrushConverter()
    {
        s_low.Freeze();
        s_medium.Freeze();
        s_high.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int load ? load switch
        {
            < 40 => s_low,
            < 70 => s_medium,
            _ => s_high,
        } : s_low;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
