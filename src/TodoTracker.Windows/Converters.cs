using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TodoTracker.Windows;

public sealed class HexBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = [];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hex = value as string ?? "#3b82f6";
        if (!Cache.TryGetValue(hex, out var brush))
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Cache[hex] = brush;
        }

        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is true / non-null / non-empty; pass "invert" to flip.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var truthy = value switch
        {
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            null => false,
            _ => true,
        };
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            truthy = !truthy;
        }

        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
