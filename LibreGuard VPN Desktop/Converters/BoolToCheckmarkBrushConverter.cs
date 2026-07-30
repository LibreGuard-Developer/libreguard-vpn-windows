using System.Windows.Data;
using System.Windows.Media;
using System.Globalization;

namespace LibreGuard_VPN_Desktop.Converters;

/// <summary>
/// Converts boolean password strength values to brush colors for requirement checkmarks.
/// </summary>
public sealed class BoolToCheckmarkBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isValid && isValid)
            return new SolidColorBrush(Color.FromRgb(34, 197, 94)); // Green: #22C55E
        
        return new SolidColorBrush(Color.FromRgb(107, 114, 128)); // Gray: #6B7280
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
