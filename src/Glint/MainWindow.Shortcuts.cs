using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Glint;

public sealed partial class MainWindow
{
    internal void RebuildShortcuts()
    {
        Root.KeyboardAccelerators.Clear();
        foreach (var definition in ShortcutMap.Definitions)
            foreach (string binding in ShortcutMap.Bindings(definition, S))
            {
                if (!ShortcutMap.TryParse(binding, out var key, out var mods) || !UsesAccelerator(key, mods)) continue;
                var accelerator = new KeyboardAccelerator { Key = key, Modifiers = mods };
                accelerator.Invoked += (_, e) => e.Handled = RunShortcut(definition);
                Root.KeyboardAccelerators.Add(accelerator);
            }
        ToolTipService.SetToolTip(_overlay.FullscreenButton, ShortcutText("Fullscreen", "fullscreen"));
        ToolTipService.SetToolTip(_overlay.MuteButton, ShortcutText("Mute or unmute", "mute"));
        ToolTipService.SetToolTip(_overlay.ChatButton, ShortcutText("Show or hide chat", "chat"));
        ToolTipService.SetToolTip(_overlay.ReloadButton, ShortcutText("Reload", "reload"));
        ToolTipService.SetToolTip(_overlay.ClipButton, ShortcutText("Create clip", "clip"));
        ToolTipService.SetToolTip(_overlay.MultiviewButton, ShortcutText("Multiview", "multiview"));
        UpdateOverlayState();
    }

    static string ShortcutText(string text, string id)
    {
        string keys = string.Join(" / ", ShortcutMap.Bindings(ShortcutMap.Definitions.First(d => d.Id == id), S));
        return keys.Length == 0 ? text : $"{text} ({keys})";
    }

    static bool UsesAccelerator(VirtualKey key, VirtualKeyModifiers mods) =>
        (mods & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu)) != 0 || key >= VirtualKey.F1 && key <= VirtualKey.F24;

    internal static VirtualKeyModifiers CurrentModifiers()
    {
        bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
        return (Down(VirtualKey.Control) ? VirtualKeyModifiers.Control : 0) |
            (Down(VirtualKey.Menu) ? VirtualKeyModifiers.Menu : 0) | (Down(VirtualKey.Shift) ? VirtualKeyModifiers.Shift : 0);
    }

    void OnShortcutKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or RichEditBox or ButtonBase or Slider or ComboBox) return;
        var mods = CurrentModifiers();
        if (UsesAccelerator(e.Key, mods)) return;
        foreach (var definition in ShortcutMap.Definitions)
            foreach (string binding in ShortcutMap.Bindings(definition, S))
                if (ShortcutMap.TryParse(binding, out var key, out var expected) && key == e.Key && mods == expected)
                { e.Handled = RunShortcut(definition); return; }
        if (mods == VirtualKeyModifiers.None && _active?.Vod != null && e.Key >= VirtualKey.Number0 && e.Key <= VirtualKey.Number9)
        { SeekToFraction((e.Key - VirtualKey.Number0) / 10.0); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape)
        {
            if (_browseOpen) CloseBrowse();
            else if (_multiviewZoom != null) ZoomMultiview(null);
            else if (_fullscreen) ToggleFullscreen();
            else if (_vodPanelOpen) CloseVodPanel();
            else if (S.Theatre) ToggleTheatre();
            else return;
            e.Handled = true;
        }
    }

    bool RunShortcut(ShortcutDefinition definition)
    {
        if (definition.Vod && _active?.Vod == null) return false;
        switch (definition.Id)
        {
            case "fullscreen": ToggleFullscreen(); break;
            case "theatre": ToggleTheatre(); break;
            case "chat": ToggleChat(); break;
            case "mute": ToggleMute(); break;
            case "pause": TogglePause(); break;
            case "volumeUp": ChangeVolume(5); break;
            case "volumeDown": ChangeVolume(-5); break;
            case "back": SeekBy(-S.SeekSeconds); break;
            case "forward": SeekBy(S.SeekSeconds); break;
            case "longBack": SeekBy(-S.LongSeekSeconds); break;
            case "longForward": SeekBy(S.LongSeekSeconds); break;
            case "clip": _ = CreateClipAsync(); break;
            case "browse": if (_browseOpen) CloseBrowse(); else OpenBrowse(); break;
            case "multiview": SetMultiview(!_multiview); break;
            case "newTab": OpenNewTab(); break;
            case "closeTab": if (Tabs.SelectedItem is TabViewItem item) CloseTabItem(item); break;
            case "nextTab": CycleTab(1); break;
            case "previousTab": CycleTab(-1); break;
            case "channel": FocusAddBox(); break;
            case "reload": Reload(); break;
            case "settings": ShowSettings(); break;
            default: return false;
        }
        return true;
    }
}
