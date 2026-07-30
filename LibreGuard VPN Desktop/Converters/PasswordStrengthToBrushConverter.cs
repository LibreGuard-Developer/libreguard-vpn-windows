using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LibreGuard_VPN_Desktop.Converters;

public class PasswordStrengthToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int score)
        {
            return score switch
            {
                < 25 => new SolidColorBrush(Color.FromRgb(239, 68, 68)),   // Red
                < 50 => new SolidColorBrush(Color.FromRgb(245, 158, 11)),  // Orange
                < 75 => new SolidColorBrush(Color.FromRgb(234, 179, 8)),   // Yellow
                < 100 => new SolidColorBrush(Color.FromRgb(34, 197, 94)), // Light Green
                >= 100 => new SolidColorBrush(Color.FromRgb(21, 128, 61)), // Green
            };
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
