using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using NetBinder.Shared.Models;

namespace NetBinder.UI.Converters;

/// <summary>
/// Converts a boolean to Visibility (true = Visible, false = Collapsed).
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility.Visible;
    }
}

/// <summary>
/// Converts a boolean connection state to a color brush (Green = connected, Red = disconnected).
/// </summary>
public class BoolToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush ConnectedBrush = new(Color.FromRgb(0x10, 0x7C, 0x41)); // Windows Green
    private static readonly SolidColorBrush DisconnectedBrush = new(Color.FromRgb(0xE8, 0x11, 0x23)); // Windows Red

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? ConnectedBrush : DisconnectedBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts an interface index and bindings list to a badge string (e.g. "2 bound").
/// </summary>
public class BindingCountConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is int interfaceIndex && values[1] is IEnumerable bindings)
        {
            int count = bindings.Cast<BindingMapping>().Count(b => b.InterfaceIndex == interfaceIndex && b.IsActive);
            return count > 0 ? $"{count} bound" : string.Empty;
        }
        return string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Returns Visible if there are bindings on the interface, Collapsed otherwise.
/// </summary>
public class BindingCountToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is int interfaceIndex && values[1] is IEnumerable bindings)
        {
            int count = bindings.Cast<BindingMapping>().Count(b => b.InterfaceIndex == interfaceIndex && b.IsActive);
            return count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
