using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LibreGuard_VPN_Desktop.Converters;

/// <summary>
/// Returns Visible when value equals the converter parameter, Collapsed otherwise.
/// Usage: Visibility="{Binding Status, Converter={StaticResource EqualToVis}, ConverterParameter=Connected}"
/// </summary>
public sealed class EqualityToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
