using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Converters;

/// <summary>
/// Converts ConnectionStatus enum to the matching status color brush.
/// </summary>
public sealed class ConnectionStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush s_connected = new(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly SolidColorBrush s_connecting = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush s_disconnected = new(Color.FromRgb(0x94, 0xA3, 0xB8));
    private static readonly SolidColorBrush s_disconnecting = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush s_reconnecting = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush s_error = new(Color.FromRgb(0xEF, 0x44, 0x44));

    static ConnectionStatusToBrushConverter()
    {
        s_connected.Freeze();
        s_connecting.Freeze();
        s_disconnected.Freeze();
        s_disconnecting.Freeze();
        s_reconnecting.Freeze();
        s_error.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ConnectionStatus status)
            return targetType == typeof(Color) ? s_disconnected.Color : s_disconnected;

        var brush = status switch
        {
            ConnectionStatus.Connected => s_connected,
            ConnectionStatus.Connecting => s_connecting,
            ConnectionStatus.Disconnecting => s_disconnecting,
            ConnectionStatus.Reconnecting => s_reconnecting,
            ConnectionStatus.Error => s_error,
            _ => s_disconnected,
        };

        return targetType == typeof(Color) ? brush.Color : brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
