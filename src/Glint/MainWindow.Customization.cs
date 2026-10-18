using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Glint;

public sealed partial class MainWindow
{
    internal void ApplyAppearance()
    {
        bool accentChanged = Appearance.Apply();
        Root.Background = (Brush)Application.Current.Resources["GlintBackgroundBrush"];
        if (accentChanged)
        {
            Appearance.RefreshTheme(Root);
            if (_settingsWindow?.Content is FrameworkElement settings) Appearance.RefreshTheme(settings);
            if (_overlay.Content is FrameworkElement overlay) Appearance.RefreshTheme(overlay);
            foreach (var window in _chatHistoryWindows)
                if (window.Content is FrameworkElement history) Appearance.RefreshTheme(history);
        }
        foreach (var entry in _entries.Values) entry.RefreshAppearance();
        ApplyLayoutSettings();
    }

    internal void ApplyPlayerPreferences()
    {
        _overlayTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(S.ControlsHideSeconds, 1, 10));
        _overlay.BackSeconds.Text = $"-{S.SeekSeconds:0}";
        _overlay.ForwardSeconds.Text = $"+{S.SeekSeconds:0}";
        string back = $"Back {S.SeekSeconds:0} seconds", forward = $"Forward {S.SeekSeconds:0} seconds";
        ToolTipService.SetToolTip(_overlay.BackButton, back);
        ToolTipService.SetToolTip(_overlay.ForwardButton, forward);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_overlay.BackButton, back);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_overlay.ForwardButton, forward);
    }

    internal void ApplyMultiviewPreferences()
    {
        _multiviewUpdating = true;
        MultiviewCount.SelectedIndex = Math.Clamp(S.MultiviewCount, 2, 4) - 2;
        MultiviewLayout.SelectedItem = S.MultiviewLayout;
        MultiviewChatMode.SelectedItem = S.MultiviewChats;
        MultiviewDock.SelectedItem = S.MultiviewDock;
        MultiviewChatWidth.Value = S.MultiviewChatWidth;
        MultiviewChatHeight.Value = S.MultiviewChatHeight;
        _multiviewUpdating = false;
        if (_multiview) EnsureMultiviewSlots();
        ApplyMultiviewLayout();
    }

    internal void ApplyPreferences(bool adSkippingChanged = false)
    {
        ApplyAppearance();
        OnChatStyleChanged();
        OnChatPreferencesChanged();
        ApplyPlayerPreferences();
        ApplyMultiviewPreferences();
        RebuildShortcuts();
        if (adSkippingChanged) OnAdSkippingChanged();
        Settings.Save();
    }
}
