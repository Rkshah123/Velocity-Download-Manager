using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Velocity.App;

/// <summary>Bool to Visibility. Pass "Invert" to flip the result.</summary>
public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool boolean && boolean;
        if (parameter is string text && text.Equals("Invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Shows an element only when a count is greater than zero.</summary>
public sealed class CountVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Colors the file-type tile. Pass "Tint" for a translucent background.</summary>
public sealed class CategoryToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Color> Palette = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Documents"] = Color.FromRgb(0x5B, 0x8C, 0xFF),
        ["Videos"] = Color.FromRgb(0xA1, 0x6B, 0xFF),
        ["Music"] = Color.FromRgb(0x27, 0xC4, 0xA6),
        ["Archives"] = Color.FromRgb(0xF2, 0xA9, 0x3B),
        ["Images"] = Color.FromRgb(0xFF, 0x7B, 0xA9),
        ["Software"] = Color.FromRgb(0x45, 0xC4, 0xE8),
        ["Other"] = Color.FromRgb(0x8B, 0x97, 0xAA)
    };

    private static readonly Dictionary<Color, SolidColorBrush> Cache = [];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var category = value as string ?? "Other";
        var color = Palette.GetValueOrDefault(category, Palette["Other"]);
        var tint = parameter is string text && text.Equals("Tint", StringComparison.OrdinalIgnoreCase);
        if (tint) color = Color.FromArgb(0x2E, color.R, color.G, color.B);

        lock (Cache)
        {
            if (Cache.TryGetValue(color, out var cached)) return cached;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Cache[color] = brush;
            return brush;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
