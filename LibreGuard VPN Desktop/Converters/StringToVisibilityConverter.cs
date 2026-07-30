using System.Windows;
using System.Windows.Data;

namespace LibreGuard_VPN_Desktop.Converters;

/// <summary>
/// Converts null or empty strings to Collapsed visibility and non-empty strings to Visible.
/// </summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        bool isEmpty = string.IsNullOrWhiteSpace(value as string);
        bool isInverted = parameter?.ToString() == "Inverted";

        if (isInverted)
            return isEmpty ? Visibility.Visible : Visibility.Collapsed;

        return isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
