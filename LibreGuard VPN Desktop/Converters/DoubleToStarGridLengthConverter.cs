using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LibreGuard_VPN_Desktop.Converters;

/// <summary>
/// Converts a numeric value to a star GridLength (e.g., 0.5 -> 0.5*).
/// </summary>
public sealed class DoubleToStarGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double d)
        {
            // GridLength constructor treats very small values or 0 correctly
            return new GridLength(Math.Max(0, d), GridUnitType.Star);
        }
        
        return new GridLength(0, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
