using System.Windows.Media;

namespace Velocity.App;

public static class ThemeManager
{
    public const string DefaultTheme = "Midnight";

    public static readonly string[] ThemeNames = ["Midnight", "Ember", "Light"];

    private sealed record ThemePalette(
        string WindowTop, string WindowBottom, string Sidebar, string Card, string CardAlt, string Elevated,
        string Border, string BorderStrong, string Text, string SecondaryText, string Muted,
        string Accent, string AccentHover, string AccentPressed, string Accent2, string AccentSoft,
        string Success, string SuccessSoft, string Warning, string WarningSoft,
        string Danger, string DangerSoft, string Track, string ScrollThumb, string ScrollThumbHover);

    private static readonly ThemePalette Midnight = new(
        WindowTop: "#0B0E13", WindowBottom: "#080A0F", Sidebar: "#0F1218", Card: "#141823", CardAlt: "#1A1F2B", Elevated: "#222836",
        Border: "#232A37", BorderStrong: "#2E3645",
        Text: "#F2F5FA", SecondaryText: "#AFBACB", Muted: "#7A8698",
        Accent: "#5B8CFF", AccentHover: "#7BA6FF", AccentPressed: "#4470DD", Accent2: "#7C6BFF", AccentSoft: "#16203A",
        Success: "#34D399", SuccessSoft: "#0F2A1E", Warning: "#F5B84B", WarningSoft: "#2A2113",
        Danger: "#F87171", DangerSoft: "#2A1618", Track: "#1E2633", ScrollThumb: "#323B4A", ScrollThumbHover: "#4B5669");

    private static readonly ThemePalette Ember = new(
        WindowTop: "#17110D", WindowBottom: "#120D0A", Sidebar: "#1D1713", Card: "#221A16", CardAlt: "#2A201A", Elevated: "#33261F",
        Border: "#3A2C24", BorderStrong: "#4A382D",
        Text: "#FFF4EA", SecondaryText: "#E3CBB6", Muted: "#B08F76",
        Accent: "#E8792F", AccentHover: "#F7924C", AccentPressed: "#C05E22", Accent2: "#F2A03D", AccentSoft: "#3A2314",
        Success: "#4CCB92", SuccessSoft: "#1B2C22", Warning: "#F3B24A", WarningSoft: "#332513",
        Danger: "#F0746F", DangerSoft: "#331A19", Track: "#3A2B22", ScrollThumb: "#4C3A2E", ScrollThumbHover: "#6A5040");

    private static readonly ThemePalette Light = new(
        WindowTop: "#F7F9FD", WindowBottom: "#EFF3FA", Sidebar: "#FFFFFF", Card: "#FFFFFF", CardAlt: "#F7FAFE", Elevated: "#EDF3FB",
        Border: "#E2E8F2", BorderStrong: "#CBD6E6",
        Text: "#0F172A", SecondaryText: "#44506A", Muted: "#6C7890",
        Accent: "#3B6FD4", AccentHover: "#4F82E6", AccentPressed: "#2C57AE", Accent2: "#6A7BF0", AccentSoft: "#E8EFFC",
        Success: "#0F9D63", SuccessSoft: "#E4F7EE", Warning: "#B47800", WarningSoft: "#FDF4E3",
        Danger: "#C93A50", DangerSoft: "#FCEAEE", Track: "#E6ECF6", ScrollThumb: "#C6D1E2", ScrollThumbHover: "#A3B2C9");

    private static string _current = DefaultTheme;

    public static string Current => _current;

    public static void Apply(string theme)
    {
        var palette = theme switch
        {
            "Ember" => Ember,
            "Light" => Light,
            _ => Midnight
        };

        _current = theme is "Ember" or "Light" ? theme : DefaultTheme;

        SetBrush("WindowBackgroundBrush", Vertical(palette.WindowTop, palette.WindowBottom));
        SetBrush("SidebarBrush", Solid(palette.Sidebar));
        SetBrush("CardBrush", Solid(palette.Card));
        SetBrush("CardAltBrush", Solid(palette.CardAlt));
        SetBrush("ElevatedBrush", Solid(palette.Elevated));
        SetBrush("BorderBrush", Solid(palette.Border));
        SetBrush("BorderStrongBrush", Solid(palette.BorderStrong));
        SetBrush("PrimaryTextBrush", Solid(palette.Text));
        SetBrush("SecondaryTextBrush", Solid(palette.SecondaryText));
        SetBrush("MutedTextBrush", Solid(palette.Muted));
        SetBrush("AccentBrush", Solid(palette.Accent));
        SetBrush("Accent2Brush", Solid(palette.Accent2));
        SetBrush("AccentHoverBrush", Solid(palette.AccentHover));
        SetBrush("AccentPressedBrush", Solid(palette.AccentPressed));
        SetBrush("AccentSoftBrush", Solid(palette.AccentSoft));
        SetBrush("AccentGradientBrush", Diagonal(palette.Accent, palette.Accent2));
        SetBrush("AccentGradientHoverBrush", Diagonal(palette.AccentHover, palette.Accent2));
        SetBrush("SuccessBrush", Solid(palette.Success));
        SetBrush("SuccessSoftBrush", Solid(palette.SuccessSoft));
        SetBrush("WarningBrush", Solid(palette.Warning));
        SetBrush("WarningSoftBrush", Solid(palette.WarningSoft));
        SetBrush("DangerBrush", Solid(palette.Danger));
        SetBrush("DangerSoftBrush", Solid(palette.DangerSoft));
        SetBrush("TrackBrush", Solid(palette.Track));
        SetBrush("ScrollThumbBrush", Solid(palette.ScrollThumb));
        SetBrush("ScrollThumbHoverBrush", Solid(palette.ScrollThumbHover));
    }

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);

    private static SolidColorBrush Solid(string value)
    {
        var brush = new SolidColorBrush(Parse(value));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Vertical(string top, string bottom) =>
        Build(Parse(top), Parse(bottom), 90);

    private static LinearGradientBrush Diagonal(string start, string end) =>
        Build(Parse(start), Parse(end), 45);

    private static LinearGradientBrush Build(Color start, Color end, double angle)
    {
        var brush = new LinearGradientBrush(start, end, angle);
        brush.Freeze();
        return brush;
    }

    private static void SetBrush(string key, Brush brush) => System.Windows.Application.Current.Resources[key] = brush;
}
