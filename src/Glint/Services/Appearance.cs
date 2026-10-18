using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Glint;

static class Appearance
{
    static string? _appliedAccent;
    public static readonly string[] Accents = ["Purple", "Blue", "Teal", "Pink", "Orange"];
    public static Color Accent => Settings.Current.AccentColor switch
    {
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
        bool accentChanged = _appliedAccent != Settings.Current.AccentColor;
        _appliedAccent = Settings.Current.AccentColor;
        foreach (string key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" }) r[key] = accent;
        Brush("GlintAccentBrush", accent);
        if (r.ThemeDictionaries["Dark"] is ResourceDictionary dark && dark["GlintThemeAccent"] is SolidColorBrush themeAccent)
            themeAccent.Color = accent;
        byte shade = Settings.Current.BackgroundStyle switch { "Black" => 0, "Grey" => 36, _ => 24 };
        Brush("GlintBackgroundBrush", Color.FromArgb(255, shade, shade, (byte)(shade == 0 ? 0 : shade + 4)));
        Brush("GlintPanelBrush", Color.FromArgb(255, (byte)(shade + 4), (byte)(shade + 4), (byte)(shade + 8)));
        return accentChanged;
    }

    public static void RefreshTheme(FrameworkElement root)
    {
        // Refresh cached theme resources after changing the accent.
        root.RequestedTheme = ElementTheme.Light;
        root.RequestedTheme = ElementTheme.Dark;
    }
}
