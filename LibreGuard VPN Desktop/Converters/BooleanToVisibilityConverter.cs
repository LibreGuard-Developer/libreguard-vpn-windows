using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LibreGuard_VPN_Desktop.Converters;

/// <summary>
/// Converts a boolean to Visibility. True = Visible, False = Collapsed (or configurable).
/// </summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public Visibility FalseValue { get; set; } = Visibility.Collapsed;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : FalseValue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}
