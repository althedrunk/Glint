using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Glint;

static class Appearance
{
    static string? _appliedAccent;
    public static readonly string[] Accents = ["Purple", "Blue", "Teal", "Green", "Yellow", "Orange", "Red", "Pink", "Custom"];
    public static Color Accent => Settings.Current.AccentColor switch
    {
        "Custom" => ParseHex(Settings.Current.CustomAccent) ?? Color.FromArgb(255, 169, 112, 255),
        "Green" => Color.FromArgb(255, 92, 214, 120),
        "Yellow" => Color.FromArgb(255, 245, 205, 80),
        "Red" => Color.FromArgb(255, 255, 99, 99),
        "Blue" => Color.FromArgb(255, 95, 169, 255),
        "Teal" => Color.FromArgb(255, 62, 210, 180),
        "Pink" => Color.FromArgb(255, 242, 133, 195),
        "Orange" => Color.FromArgb(255, 255, 178, 102),
        _ => Color.FromArgb(255, 169, 112, 255),
    };

    public static bool Apply()
    {
        var r = Application.Current.Resources;
        void Brush(string key, Color color)
        {
            if (r.TryGetValue(key, out var value) && value is SolidColorBrush brush) brush.Color = color;
            else r[key] = new SolidColorBrush(color);
        }
        var accent = Accent;
        var accentKey = Settings.Current.AccentColor == "Custom" ? "Custom " + Settings.Current.CustomAccent : Settings.Current.AccentColor;
        bool accentChanged = _appliedAccent != accentKey;
        _appliedAccent = accentKey;
        foreach (string key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" }) r[key] = accent;
        Brush("GlintAccentBrush", accent);
        if (r.ThemeDictionaries["Dark"] is ResourceDictionary dark && dark["GlintThemeAccent"] is SolidColorBrush themeAccent)
            themeAccent.Color = accent;
        byte shade = Settings.Current.BackgroundStyle switch { "Black" => 0, "Grey" => 36, _ => 24 };
        Brush("GlintBackgroundBrush", Color.FromArgb(255, shade, shade, (byte)(shade == 0 ? 0 : shade + 4)));
        Brush("GlintPanelBrush", Color.FromArgb(255, (byte)(shade + 4), (byte)(shade + 4), (byte)(shade + 8)));
        return accentChanged;
    }

    public static Color? ParseHex(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#' ||
            !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
        return Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // Smallest width a stream card may shrink to before the row drops a column.
    public static double CardMinWidth => Settings.Current.CardSize switch { "Small" => 210, "Large" => 360, _ => 270 };

    public static void RefreshTheme(FrameworkElement root)
    {
        // Refresh cached theme resources after changing the accent.
        root.RequestedTheme = ElementTheme.Light;
        root.RequestedTheme = ElementTheme.Dark;
    }
}
