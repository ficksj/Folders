using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ShareManager.Models;

namespace ShareManager.Converters;

public class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ServerConnectionStatus status)
        {
            return status switch
            {
                ServerConnectionStatus.Online => new SolidColorBrush(Color.FromRgb(16, 185, 129)),    // Emerald green
                ServerConnectionStatus.Connecting => new SolidColorBrush(Color.FromRgb(245, 158, 11)), // Amber
                ServerConnectionStatus.Offline => new SolidColorBrush(Color.FromRgb(239, 68, 68)),    // Red
                _ => new SolidColorBrush(Color.FromRgb(100, 116, 139))                                // Slate gray
            };
        }
        return new SolidColorBrush(Color.FromRgb(100, 116, 139));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        bool flag = value is bool b && b;
        if (invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        bool hasValue = value != null && (value is not string s || !string.IsNullOrWhiteSpace(s));
        if (invert) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class AccessTypeToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string s = value as string ?? "";
        if (s.Equals("Deny", StringComparison.OrdinalIgnoreCase) || s.Equals("Запретить", StringComparison.OrdinalIgnoreCase))
            return new SolidColorBrush(Color.FromRgb(248, 113, 113));
        return new SolidColorBrush(Color.FromRgb(52, 211, 153));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

