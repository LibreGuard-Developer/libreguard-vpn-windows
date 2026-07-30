using System.Globalization;
using System.Windows.Data;
using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Converters;

public sealed class PlanToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is UserPlan plan)
        {
            return plan == UserPlan.Pro ? "Pro Plan" : "Free Plan";
        }
        return "Unknown Plan";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
