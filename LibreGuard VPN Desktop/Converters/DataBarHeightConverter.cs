using System.Globalization;
using System.Windows.Data;

namespace LibreGuard_VPN_Desktop.Converters;

/// <summary>
/// Converts a numeric value to a proportional bar height (pixels) for chart display.
/// ConverterParameter is the max data value; output is scaled to 160px max height.
/// </summary>
public sealed class DataBarHeightConverter : IValueConverter
{
    private const double MaxBarHeight = 140;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double dataValue)
            return 4.0;

        double maxValue = 700;
        if (parameter is string s && double.TryParse(s, CultureInfo.InvariantCulture, out double parsed))
            maxValue = parsed;

        double ratio = Math.Clamp(dataValue / maxValue, 0, 1);
        return Math.Max(4, ratio * MaxBarHeight);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
