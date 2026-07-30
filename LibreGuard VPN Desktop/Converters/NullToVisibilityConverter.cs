using System.Windows;
using System.Windows.Data;

namespace LibreGuard_VPN_Desktop.Converters;

/// <summary>
/// Converts null values to Collapsed visibility and non-null values to Visible.
/// </summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        bool isNullOrEmpty = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        bool isInverted = parameter?.ToString() == "Inverted";

        if (isInverted)
            return isNullOrEmpty ? Visibility.Visible : Visibility.Collapsed;

        return isNullOrEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
