using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace EverLogger.App.Helpers;

public class BoolToStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool bVal && bVal;
        string context = parameter as string ?? "";
        
        if (context.Equals("logging", StringComparison.OrdinalIgnoreCase))
        {
            return b ? "Active" : "Inactive";
        }
        return b ? "Connected" : "Disconnected";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class BoolToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4EC9B0")); // SuccessBrush
        }
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F44747")); // ErrorBrush
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
