using System.Globalization;
using System.Windows.Data;

namespace LibreGuard_VPN_Desktop.Converters;

/// <summary>
/// Returns true when value equals the converter parameter.
/// Useful for RadioButton IsChecked bindings.
/// </summary>
public sealed class EqualityToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null)
            return Binding.DoNothing;

        if (targetType.IsEnum)
            return Enum.Parse(targetType, parameter.ToString()!);

        return parameter;
    }
}
