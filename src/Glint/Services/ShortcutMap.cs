using Windows.System;

namespace Glint;

public sealed record ShortcutDefinition(string Id, string Label, string[] Defaults, bool Vod = false);

public static class ShortcutMap
{
    public static readonly ShortcutDefinition[] Definitions =
    [
        new("fullscreen", "Toggle fullscreen", ["F11", "F"]),
        new("theatre", "Toggle theatre", ["T"]),
        new("chat", "Show or hide chat", ["C"]),
        new("mute", "Mute selected tab", ["M"]),
        new("pause", "Pause or resume", ["Space"]),
        new("volumeUp", "Volume up", ["Up"]),
        new("volumeDown", "Volume down", ["Down"]),
        new("back", "Seek back", ["Left", "J"], true),
        new("forward", "Seek forward", ["Right", "L"], true),
        new("longBack", "Long seek back", ["Shift+Left"], true),
        new("longForward", "Long seek forward", ["Shift+Right"], true),
        new("clip", "Create clip", ["Alt+X"]),
        new("home", "Home", ["Ctrl+H"]),
        new("browse", "Open Browse", ["Ctrl+B"]),
        new("multiview", "Toggle multiview", ["Ctrl+Shift+M"]),
        new("newTab", "New tab", ["Ctrl+T"]),
        new("closeTab", "Close tab", ["Ctrl+W"]),
        new("nextTab", "Next tab", ["Ctrl+Tab"]),
        new("previousTab", "Previous tab", ["Ctrl+Shift+Tab"]),
        new("channel", "Choose channel", ["Ctrl+L"]),
        new("reload", "Reload stream", ["F5"]),
        new("settings", "Open Settings", ["Ctrl+F1"]),
    ];

    public static string[] Bindings(ShortcutDefinition definition, AppSettings settings)
    {
        if (!settings.ShortcutOverrides.TryGetValue(definition.Id, out var value)) return definition.Defaults;
        if (value.Length == 0) return [];
        return TryParse(value, out _, out _) ? [value] : definition.Defaults;
    }

    public static string Format(VirtualKey key, VirtualKeyModifiers mods) =>
        (mods.HasFlag(VirtualKeyModifiers.Control) ? "Ctrl+" : "") +
        (mods.HasFlag(VirtualKeyModifiers.Menu) ? "Alt+" : "") +
        (mods.HasFlag(VirtualKeyModifiers.Shift) ? "Shift+" : "") + key;

    public static bool TryParse(string value, out VirtualKey key, out VirtualKeyModifiers mods)
    {
        mods = VirtualKeyModifiers.None;
        key = VirtualKey.None;
        var parts = value.Split('+');
        foreach (var part in parts.SkipLast(1))
        {
            switch (part)
            {
                case "Ctrl": mods |= VirtualKeyModifiers.Control; break;
                case "Alt": mods |= VirtualKeyModifiers.Menu; break;
                case "Shift": mods |= VirtualKeyModifiers.Shift; break;
                default: return false;
            }
        }
        return Enum.TryParse(parts[^1], out key) && key != VirtualKey.None;
    }

    public static string? Validate(string id, string chord, AppSettings settings)
    {
        if (!TryParse(chord, out var key, out var mods)) return "Choose a key or key combination.";
        if (key is VirtualKey.Escape or VirtualKey.Enter || mods == VirtualKeyModifiers.None &&
            (key == VirtualKey.Tab || key >= VirtualKey.Number0 && key <= VirtualKey.Number9)) return "That key is reserved for navigation.";
        if (mods == VirtualKeyModifiers.Control && key is VirtualKey.A or VirtualKey.C or VirtualKey.V or VirtualKey.X or VirtualKey.Z or VirtualKey.Y)
            return "That shortcut is reserved for editing text.";
        foreach (var definition in Definitions.Where(d => d.Id != id))
            foreach (var binding in Bindings(definition, settings))
                if (TryParse(binding, out var otherKey, out var otherMods) && key == otherKey && mods == otherMods)
                    return $"Already used by {definition.Label.ToLowerInvariant()}.";
        return null;
    }
}
