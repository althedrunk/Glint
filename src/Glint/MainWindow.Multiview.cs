using Glint.Chat;
using Glint.Player;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Glint;

public sealed partial class MainWindow
{
    sealed class MultiviewSlot
    {
        public ChannelTab? Tab;
        public Grid Root = new(), Area = new();
        public ComboBox Channel = new();
        public Button Audio = new(), Zoom = new(), More = new(), Retry = new();
        public TextBlock Status = new();
    }

    sealed class MultiviewChatPane
    {
        public required ChannelTab Tab;
        public Grid Root = new();
        public ChatView View = new();
        public Button Recipient = new(), Pin = new();
    }

    readonly List<MultiviewSlot> _multiviewSlots = new();
    readonly Dictionary<ChannelTab, MultiviewChatPane> _multiviewChats = new();
    readonly HashSet<object> _videoPopups = new();
    bool _multiview, _multiviewUpdating, _multiviewLayingOut;
    int _multiviewFocus;
    ChannelTab? _multiviewZoom, _multiviewRecipient;
    string _multiviewResolvedDock = "Below";

    bool MultiviewActive => _multiview && !S.ChatOnly && _tabs.Count > 0;
    bool MultiviewChatShown => MultiviewActive && S.MultiviewChats != "Hidden" && ChatShown;

    void WireMultiview()
    {
        _multiviewUpdating = true;
        MultiviewCount.ItemsSource = new[] { "2 streams", "3 streams", "4 streams" };
        MultiviewLayout.ItemsSource = new[] { "Grid", "Main stream" };
        MultiviewChatMode.ItemsSource = new[] { "Each stream", "Following chat", "Pinned chats", "Hidden" };
        MultiviewDock.ItemsSource = new[] { "Below", "Right", "Auto" };
        S.MultiviewCount = Math.Clamp(S.MultiviewCount, 2, 4);
        MultiviewCount.SelectedIndex = S.MultiviewCount - 2;
        MultiviewLayout.SelectedItem = S.MultiviewLayout;
        if (MultiviewLayout.SelectedIndex < 0) MultiviewLayout.SelectedIndex = 0;
        MultiviewChatMode.SelectedItem = S.MultiviewChats;
        if (MultiviewChatMode.SelectedIndex < 0) MultiviewChatMode.SelectedIndex = 0;
        MultiviewDock.SelectedItem = S.MultiviewDock;
        if (MultiviewDock.SelectedIndex < 0) MultiviewDock.SelectedIndex = 0;
        S.MultiviewLayout = (string)MultiviewLayout.SelectedItem;
        S.MultiviewChats = (string)MultiviewChatMode.SelectedItem;
        S.MultiviewDock = (string)MultiviewDock.SelectedItem;
        MultiviewChatWidth.Value = Math.Clamp(S.MultiviewChatWidth, 260, 600);
        MultiviewChatHeight.Value = Math.Clamp(S.MultiviewChatHeight, 140, 700);
        _multiviewUpdating = false;

        TrackVideoPopup(MultiviewOptions.Flyout);
        TrackVideoPopup(MultiviewAddFlyout);
        foreach (var combo in new[] { MultiviewCount, MultiviewLayout, MultiviewChatMode, MultiviewDock, MultiviewRecipient })
            TrackVideoPopup(combo);

        MultiviewButton.Click += (_, _) => SetMultiview(true);
        _overlay.MultiviewButton.Click += (_, _) => SetMultiview(!_multiview);
        MultiviewExit.Click += (_, _) => SetMultiview(false);
        MultiviewRestoreGrid.Click += (_, _) => ZoomMultiview(null);
        MultiviewCount.SelectionChanged += (_, _) =>
        {
            if (_multiviewUpdating) return;
            S.MultiviewCount = MultiviewCount.SelectedIndex + 2;
            EnsureMultiviewSlots();
            SaveMultiviewSlots();
            ApplyMultiviewLayout();
        };
        MultiviewLayout.SelectionChanged += (_, _) =>
        {
            if (_multiviewUpdating) return;
            S.MultiviewLayout = (string)MultiviewLayout.SelectedItem;
            Settings.Save();
            ApplyMultiviewLayout();
        };
        MultiviewChatMode.SelectionChanged += (_, _) =>
        {
            if (_multiviewUpdating) return;
            S.MultiviewChats = (string)MultiviewChatMode.SelectedItem;
            Settings.Save();
            ApplyMultiviewLayout();
        };
        MultiviewDock.SelectionChanged += (_, _) =>
        {
            if (_multiviewUpdating) return;
            S.MultiviewDock = (string)MultiviewDock.SelectedItem;
            Settings.Save();
            ApplyMultiviewLayout();
        };
        MultiviewChatWidth.ValueChanged += (_, e) =>
        {
            if (_multiviewUpdating) return;
            S.MultiviewChatWidth = e.NewValue;
            ResizeMultiviewDock();
            Settings.Save();
        };
        MultiviewChatHeight.ValueChanged += (_, e) =>
        {
            if (_multiviewUpdating) return;
            S.MultiviewChatHeight = e.NewValue;
            ResizeMultiviewDock();
            Settings.Save();
        };
        MultiviewPins.Click += (_, _) => ShowMultiviewPins();
        MultiviewAdd.Click += (_, _) => PrepareMultiviewAdd();
        MultiviewRecipient.SelectionChanged += (_, _) =>
        {
            if (!_multiviewUpdating && MultiviewRecipient.SelectedItem is ChannelTab tab) SetChatRecipient(tab);
        };
        MultiviewAddBox.KeyDown += (_, e) =>
        {
            AddBoxKey(MultiviewAddBox, e);
            if (e.Handled) MultiviewAddFlyout.Hide();
        };
        MultiviewContent.SizeChanged += (_, _) => ApplyMultiviewLayout();
        WireMultiviewDivider();
    }

    void TrackVideoPopup(FlyoutBase flyout)
    {
        flyout.Opening += (_, _) => SetVideoPopup(flyout, true);
        flyout.Closed += (_, _) => SetVideoPopup(flyout, false);
    }

    void TrackVideoPopup(ComboBox combo)
    {
        combo.DropDownOpened += (_, _) => SetVideoPopup(combo, true);
        combo.DropDownClosed += (_, _) => SetVideoPopup(combo, false);
    }

    void SetVideoPopup(object popup, bool open)
    {
        if (open) _videoPopups.Add(popup);
        else _videoPopups.Remove(popup);
        if (!_closing) UpdateVideoBounds();
    }

    Button MultiviewIcon(string glyph, string name)
    {
        var button = new Button { Style = (Style)Root.Resources["IconButton"], Content = glyph, Width = 30, Height = 30, AllowFocusOnInteraction = false };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    MultiviewSlot CreateMultiviewSlot(int index)
    {
        var slot = new MultiviewSlot();
        slot.Root.BorderThickness = new Thickness(2);
        slot.Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        slot.Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 28, 28, 33)), ColumnSpacing = 2, Padding = new Thickness(4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        slot.Channel.PlaceholderText = "Choose a channel";
        slot.Channel.HorizontalAlignment = HorizontalAlignment.Stretch;
        slot.Channel.MinWidth = 0;
        AutomationProperties.SetName(slot.Channel, $"Channel for view {index + 1}");
        TrackVideoPopup(slot.Channel);
        slot.Channel.SelectionChanged += (_, _) =>
        {
            if (_multiviewUpdating || slot.Channel.SelectedItem is not ChannelTab tab || slot.Tab == tab) return;
            var other = _multiviewSlots.FirstOrDefault(s => s != slot && s.Tab == tab);
            if (other != null) other.Tab = slot.Tab;
            slot.Tab = tab;
            _multiviewFocus = _multiviewSlots.IndexOf(slot);
            SaveMultiviewSlots();
            Tabs.SelectedItem = tab.Item;
            RefreshMultiviewChoices();
            ApplyMultiviewLayout();
            StartMultiviewPlayers();
        };
        slot.Audio = MultiviewIcon("\uE767", "Mute view");
        slot.Audio.Click += (_, _) => { if (slot.Tab is { } tab) ToggleTabMute(tab); };
        slot.Zoom = MultiviewIcon("\uE740", "Enlarge view");
        slot.Zoom.Click += (_, _) => { if (slot.Tab is { } tab) ZoomMultiview(_multiviewZoom == tab ? null : tab); };
        slot.More = MultiviewIcon("\uE712", "View options");
        slot.More.Click += (_, _) => ShowMultiviewSlotMenu(slot);
        header.Children.Add(slot.Channel);
        foreach (var (button, column) in new[] { (slot.Audio, 1), (slot.Zoom, 2), (slot.More, 3) })
        {
            Grid.SetColumn(button, column);
            header.Children.Add(button);
        }
        slot.Area.Background = new SolidColorBrush(Colors.Black);
        slot.Status.Foreground = new SolidColorBrush(Colors.White);
        slot.Status.TextWrapping = TextWrapping.Wrap;
        slot.Status.TextAlignment = TextAlignment.Center;
        slot.Status.Margin = new Thickness(12);
        slot.Retry.Content = "Add a channel";
        slot.Retry.HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetName(slot.Retry, $"Reload view {index + 1}");
        slot.Retry.Click += (_, _) =>
        {
            if (slot.Tab is { } tab) ReloadTab(tab);
            else ShowMultiviewAdd(_multiviewSlots.IndexOf(slot));
        };
        var status = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 8 };
        status.Children.Add(slot.Status);
        status.Children.Add(slot.Retry);
        slot.Area.Children.Add(status);
        Grid.SetRow(slot.Area, 1);
        slot.Root.Children.Add(header);
        slot.Root.Children.Add(slot.Area);
        return slot;
    }

    void SetMultiview(bool enabled)
    {
        SaveChatDraft();
        if (enabled && S.ChatOnly) SetChatOnly(false);
        _multiview = S.Multiview = enabled;
        _multiviewZoom = null;
        CloseVodPanel();
        foreach (var tab in _tabs) tab.Playback.Host?.Hide();
        if (enabled)
        {
            S.MultiviewPinnedChats = S.MultiviewPinnedChats.Where(login => _tabs.Any(t => t.Login == login)).Distinct().Take(4).ToList();
            EnsureMultiviewSlots();
            FocusMultiviewTab(_active);
            _multiviewRecipient ??= _active;
            RefreshMultiviewChoices();
            StartMultiviewPlayers();
        }
        Settings.Save();
        ApplyLayoutSettings();
        RefreshChatViews();
        RestoreChatDraft();
        UpdateChatInput();
        FocusMultiviewPlayer();
    }

    void EnsureMultiviewSlots()
    {
        int count = Math.Clamp(S.MultiviewCount, 2, 4);
        while (_multiviewSlots.Count < count)
        {
            int index = _multiviewSlots.Count;
            var slot = CreateMultiviewSlot(index);
            string? login = S.MultiviewChannels.ElementAtOrDefault(index);
            slot.Tab = _tabs.FirstOrDefault(t => t.Login == login && !_multiviewSlots.Any(s => s.Tab == t));
            if (slot.Tab == null) slot.Tab = index == 0 && _active != null ? _active
                : _tabs.FirstOrDefault(t => !_multiviewSlots.Any(s => s.Tab == t));
            _multiviewSlots.Add(slot);
        }
        if (_multiviewSlots.Count > count) _multiviewSlots.RemoveRange(count, _multiviewSlots.Count - count);
        _multiviewFocus = Math.Clamp(_multiviewFocus, 0, count - 1);
        if (_active != null && !_multiviewSlots.Any(s => s.Tab == _active)) _multiviewSlots[_multiviewFocus].Tab = _active;
        RefreshMultiviewChoices();
        StartMultiviewPlayers();
    }

    void SaveMultiviewSlots()
    {
        S.MultiviewChannels = _multiviewSlots.Select(s => s.Tab?.Login ?? "").ToList();
        Settings.Save();
    }

    void StartMultiviewPlayers()
    {
        if (!MultiviewActive || _closing) return;
        foreach (var tab in _multiviewSlots.Select(s => s.Tab).OfType<ChannelTab>().Distinct())
        {
            if (tab.Playback.Started) continue;
            if (tab.Vod is { } vod) _ = PlayVodAsync(tab, vod, tab.VodPosition);
            else _ = PlayAsync(tab);
        }
    }

    void FocusMultiviewTab(ChannelTab? tab)
    {
        if (!MultiviewActive || tab == null || _multiviewSlots.Count == 0) return;
        int index = _multiviewSlots.FindIndex(s => s.Tab == tab);
        if (index < 0) _multiviewSlots[_multiviewFocus].Tab = tab;
        else _multiviewFocus = index;
        if (_multiviewZoom != null) _multiviewZoom = tab;
        SaveMultiviewSlots();
        RefreshMultiviewChoices();
        ApplyMultiviewLayout();
    }

    void RefreshMultiviewChoices()
    {
        if (_multiviewUpdating || _closing || _multiviewSlots.Count == 0) return;
        _multiviewUpdating = true;
        foreach (var slot in _multiviewSlots)
        {
            slot.Channel.ItemsSource = _tabs.ToList();
            slot.Channel.SelectedItem = slot.Tab;
            UpdateMultiviewSlotStatus(slot);
        }
        if (_multiviewRecipient == null || !_tabs.Contains(_multiviewRecipient)) _multiviewRecipient = _active ?? _tabs.FirstOrDefault();
        MultiviewRecipient.ItemsSource = _tabs.ToList();
        MultiviewRecipient.SelectedItem = _multiviewRecipient;
        foreach (var pane in _multiviewChats.Values) UpdateMultiviewChatHeader(pane);
        _multiviewUpdating = false;
    }

    void UpdateMultiviewAudio(ChannelTab tab)
    {
        foreach (var slot in _multiviewSlots.Where(s => s.Tab == tab))
        {
            slot.Audio.Content = tab.Playback.Muted || tab.Playback.Volume == 0 ? "\uE74F" : "\uE767";
            string action = tab.Playback.Muted ? "Unmute" : "Mute";
            AutomationProperties.SetName(slot.Audio, $"{action} view {tab.Entry.DisplayName}");
            ToolTipService.SetToolTip(slot.Audio, $"{action} {tab.Entry.DisplayName}");
        }
    }

    void UpdateMultiviewSlotStatus(MultiviewSlot slot)
    {
        var tab = slot.Tab;
        slot.Audio.IsEnabled = slot.Zoom.IsEnabled = slot.More.IsEnabled = tab != null;
        slot.Status.Text = tab == null ? "Choose an open channel or add another one" : tab.Playback.State switch
        {
            PlayerState.Loading => $"Loading {tab.Entry.DisplayName}…",
            PlayerState.Error => tab.Playback.ErrorMessage ?? "Couldn't play this stream",
            PlayerState.Ended => "The stream or video has ended",
            PlayerState.Playing or PlayerState.Buffering => "",
            _ => tab.Entry.Known ? $"{tab.Entry.DisplayName} is offline" : "Ready to play",
        };
        if (tab?.Playback.AdStatus == AdSkipStatus.Waiting) slot.Status.Text = "Waiting for stream after ads";
        slot.Retry.Content = tab == null ? "Add a channel" : "Reload";
        slot.Retry.Visibility = tab == null || tab.Playback.State is PlayerState.Idle or PlayerState.Error or PlayerState.Ended ? Visibility.Visible : Visibility.Collapsed;
        slot.Root.BorderBrush = new SolidColorBrush(tab != null && tab == _active ? Windows.UI.Color.FromArgb(255, 145, 71, 255) : Windows.UI.Color.FromArgb(255, 50, 50, 60));
        if (tab != null)
        {
            UpdateMultiviewAudio(tab);
            AutomationProperties.SetName(slot.Zoom, $"{(_multiviewZoom == tab ? "Restore" : "Enlarge")} view {tab.Entry.DisplayName}");
            AutomationProperties.SetName(slot.More, $"Options for view {tab.Entry.DisplayName}");
        }
    }

    List<ChannelTab> MultiviewChatTabs()
    {
        if (!MultiviewChatShown) return new();
        return S.MultiviewChats switch
        {
            "Following chat" => _active == null ? new() : new() { _active },
            "Pinned chats" => S.MultiviewPinnedChats.Select(login => _tabs.FirstOrDefault(t => t.Login == login)).OfType<ChannelTab>().Distinct().Take(4).ToList(),
            _ => _multiviewSlots.Select(s => s.Tab).OfType<ChannelTab>().Distinct().ToList(),
        };
    }

    void ApplyMultiviewLayout()
    {
        if (_closing || _multiviewLayingOut) return;
        _multiviewLayingOut = true;
        try
        {
            bool active = MultiviewActive;
            MultiviewRoot.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            var destination = active ? MultiviewComposerInput : ChatInputHost;
            if (!destination.Children.Contains(ChatInput))
            {
                ChatInputHost.Children.Remove(ChatInput);
                MultiviewComposerInput.Children.Remove(ChatInput);
                destination.Children.Add(ChatInput);
            }
            if (!active)
            {
                foreach (var pane in _multiviewChats.Values) SetViewChannel(pane.View, null);
                if (_active != null) SetViewChannel(Chat, _active.Replay ?? _active.Chat);
                return;
            }
            if (_multiviewSlots.Count == 0) EnsureMultiviewSlots();
            SetViewChannel(Chat, null);
            MultiviewVideos.Children.Clear();
            MultiviewVideos.RowDefinitions.Clear();
            MultiviewVideos.ColumnDefinitions.Clear();
            var slots = _multiviewZoom == null ? _multiviewSlots.ToList() : _multiviewSlots.Where(s => s.Tab == _multiviewZoom).ToList();
            _multiviewResolvedDock = S.MultiviewDock == "Auto"
                ? MultiviewContent.ActualWidth >= 1200 && (slots.Count > 2 || MultiviewContent.ActualWidth > MultiviewContent.ActualHeight * 2) ? "Right" : "Below"
                : S.MultiviewDock;
            var chats = MultiviewChatTabs();
            if (S.MultiviewLayout == "Main stream" && _multiviewZoom == null && slots.Count > 1)
            {
                slots = slots.OrderByDescending(s => s.Tab == _active).ToList();
                MultiviewVideos.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.4, GridUnitType.Star) });
                MultiviewVideos.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int i = 1; i < slots.Count; i++) MultiviewVideos.RowDefinitions.Add(new RowDefinition());
                for (int i = 0; i < slots.Count; i++)
                {
                    Grid.SetColumn(slots[i].Root, i == 0 ? 0 : 1);
                    Grid.SetRow(slots[i].Root, i == 0 ? 0 : i - 1);
                    Grid.SetRowSpan(slots[i].Root, i == 0 ? slots.Count - 1 : 1);
                    MultiviewVideos.Children.Add(slots[i].Root);
                }
            }
            else
            {
                double videoWidth = MultiviewContent.ActualWidth - (_multiviewResolvedDock == "Right" && chats.Count > 0 ? S.MultiviewChatWidth + 6 : 0);
                int columns = slots.Count <= 1 || videoWidth is > 0 and < 660 ? 1 : 2;
                for (int i = 0; i < columns; i++) MultiviewVideos.ColumnDefinitions.Add(new ColumnDefinition());
                for (int i = 0; i < (slots.Count + columns - 1) / columns; i++) MultiviewVideos.RowDefinitions.Add(new RowDefinition());
                for (int i = 0; i < slots.Count; i++)
                {
                    Grid.SetColumn(slots[i].Root, i % columns);
                    Grid.SetRow(slots[i].Root, i / columns);
                    Grid.SetRowSpan(slots[i].Root, 1);
                    MultiviewVideos.Children.Add(slots[i].Root);
                }
            }
            foreach (var slot in _multiviewSlots) UpdateMultiviewSlotStatus(slot);
            MultiviewRestoreGrid.Visibility = _multiviewZoom != null ? Visibility.Visible : Visibility.Collapsed;
            MultiviewContent.ColumnDefinitions.Clear();
            MultiviewContent.RowDefinitions.Clear();
            bool right = _multiviewResolvedDock == "Right";
            if (right)
            {
                MultiviewContent.ColumnDefinitions.Add(new ColumnDefinition());
                MultiviewContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(chats.Count > 0 ? 6 : 0) });
                MultiviewContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(chats.Count > 0 ? S.MultiviewChatWidth : 0) });
            }
            else
            {
                MultiviewContent.RowDefinitions.Add(new RowDefinition());
                MultiviewContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(chats.Count > 0 ? 6 : 0) });
                MultiviewContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(chats.Count > 0 ? S.MultiviewChatHeight : 0) });
            }
            Grid.SetColumn(MultiviewVideos, 0); Grid.SetRow(MultiviewVideos, 0);
            Grid.SetColumn(MultiviewDivider, right ? 1 : 0); Grid.SetRow(MultiviewDivider, right ? 0 : 1);
            Grid.SetColumn(MultiviewChatDock, right ? 2 : 0); Grid.SetRow(MultiviewChatDock, right ? 0 : 2);
            MultiviewDivider.SetVerticalResize(!right);
            MultiviewDivider.Visibility = MultiviewChatDock.Visibility = chats.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var panes = chats.Select(GetMultiviewChat).ToList();
            foreach (var child in MultiviewChatDock.Children.ToList())
                if (!panes.Any(p => p.Root == child)) MultiviewChatDock.Children.Remove(child);
            MultiviewChatDock.RowDefinitions.Clear();
            MultiviewChatDock.ColumnDefinitions.Clear();
            int chatColumns = right ? 1 : Math.Min(2, Math.Max(1, chats.Count));
            for (int i = 0; i < chatColumns; i++) MultiviewChatDock.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < (chats.Count + chatColumns - 1) / chatColumns; i++) MultiviewChatDock.RowDefinitions.Add(new RowDefinition());
            for (int i = 0; i < chats.Count; i++)
            {
                var pane = panes[i];
                UpdateMultiviewChatHeader(pane);
                Grid.SetColumn(pane.Root, i % chatColumns); Grid.SetRow(pane.Root, i / chatColumns);
                if (!MultiviewChatDock.Children.Contains(pane.Root)) MultiviewChatDock.Children.Add(pane.Root);
            }
            foreach (var pane in _multiviewChats.Values) SetViewChannel(pane.View, chats.Contains(pane.Tab) ? pane.Tab.Replay ?? pane.Tab.Chat : null);
            ResizeMultiviewDock();
            _vodTimer?.Start();
        }
        finally { _multiviewLayingOut = false; }
        UpdateVideoBounds();
    }

    void ResizeMultiviewDock()
    {
        if (!MultiviewActive || !MultiviewChatShown || MultiviewChatDock.Children.Count == 0) return;
        if (_multiviewResolvedDock == "Right" && MultiviewContent.ColumnDefinitions.Count == 3)
            MultiviewContent.ColumnDefinitions[2].Width = new GridLength(Math.Clamp(S.MultiviewChatWidth, 220, Math.Max(220, MultiviewContent.ActualWidth * 0.55)));
        else if (MultiviewContent.RowDefinitions.Count == 3)
            MultiviewContent.RowDefinitions[2].Height = new GridLength(Math.Clamp(S.MultiviewChatHeight, 120, Math.Max(120, MultiviewContent.ActualHeight * 0.65)));
    }

    MultiviewChatPane GetMultiviewChat(ChannelTab tab)
    {
        if (_multiviewChats.TryGetValue(tab, out var existing)) return existing;
        var pane = new MultiviewChatPane { Tab = tab };
        pane.Root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 28, 28, 33));
        pane.Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.Root.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { Padding = new Thickness(6, 2, 6, 2) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pane.Recipient.Content = $"Chat: {tab.Entry.DisplayName}";
        pane.Recipient.Background = new SolidColorBrush(Colors.Transparent);
        pane.Recipient.BorderThickness = new Thickness(0);
        pane.Recipient.HorizontalAlignment = HorizontalAlignment.Stretch;
        pane.Recipient.HorizontalContentAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(pane.Recipient, $"Write to {tab.Entry.DisplayName}");
        pane.Recipient.Click += (_, _) => { SetChatRecipient(tab); ChatInput.Focus(FocusState.Programmatic); };
        pane.Pin = MultiviewIcon("\uE718", $"Pin chat {tab.Entry.DisplayName}");
        pane.Pin.Click += (_, _) => ToggleMultiviewPin(tab);
        Grid.SetColumn(pane.Pin, 1);
        header.Children.Add(pane.Recipient); header.Children.Add(pane.Pin);
        pane.View.MentionRequested += name =>
        {
            SetChatRecipient(tab);
            ChatInput.Text = $"@{name} " + ChatInput.Text;
            ChatInput.SelectionStart = ChatInput.Text.Length;
            ChatInput.Focus(FocusState.Programmatic);
        };
        Grid.SetRow(pane.View, 1);
        pane.Root.Children.Add(header); pane.Root.Children.Add(pane.View);
        _multiviewChats.Add(tab, pane);
        return pane;
    }

    static void SetViewChannel(ChatView view, ChatChannel? channel)
    {
        if (view.Channel != channel) view.SetChannel(channel);
    }

    void UpdateMultiviewChatHeader(MultiviewChatPane pane)
    {
        var tab = pane.Tab;
        pane.Recipient.Content = $"{(tab.Vod != null ? "Replay" : "Chat")}: {tab.Entry.DisplayName}";
        AutomationProperties.SetName(pane.Recipient, $"Write to {tab.Entry.DisplayName}");
        bool pinned = S.MultiviewPinnedChats.Contains(tab.Login);
        pane.Pin.Content = pinned ? "\uE77A" : "\uE718";
        AutomationProperties.SetName(pane.Pin, $"{(pinned ? "Unpin" : "Pin")} chat {tab.Entry.DisplayName}");
        ToolTipService.SetToolTip(pane.Pin, $"{(pinned ? "Unpin" : "Pin")} chat {tab.Entry.DisplayName}");
    }

    void RefreshChatViews()
    {
        if (MultiviewActive) ApplyMultiviewLayout();
        else SetViewChannel(Chat, _active?.Replay ?? _active?.Chat);
    }

    void ToggleMultiviewPin(ChannelTab tab)
    {
        if (S.MultiviewPinnedChats.Contains(tab.Login)) S.MultiviewPinnedChats.Remove(tab.Login);
        else if (S.MultiviewPinnedChats.Count < 4) S.MultiviewPinnedChats.Add(tab.Login);
        S.MultiviewChats = S.MultiviewPinnedChats.Count > 0 ? "Pinned chats" : "Following chat";
        _multiviewUpdating = true;
        MultiviewChatMode.SelectedItem = S.MultiviewChats;
        _multiviewUpdating = false;
        Settings.Save();
        ApplyMultiviewLayout();
    }

    void ShowMultiviewPins()
    {
        var menu = new MenuFlyout();
        TrackVideoPopup(menu);
        foreach (var tab in _tabs)
        {
            var item = new ToggleMenuFlyoutItem { Text = tab.Entry.DisplayName, IsChecked = S.MultiviewPinnedChats.Contains(tab.Login) };
            item.IsEnabled = item.IsChecked || S.MultiviewPinnedChats.Count < 4;
            item.Click += (_, _) => ToggleMultiviewPin(tab);
            menu.Items.Add(item);
        }
        menu.ShowAt(MultiviewPins);
    }

    void ShowMultiviewSlotMenu(MultiviewSlot slot)
    {
        if (slot.Tab is not { } tab) return;
        var menu = new MenuFlyout();
        TrackVideoPopup(menu);
        void Item(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Item("Reload", () => ReloadTab(tab));
        Item("Past broadcasts", () => { Tabs.SelectedItem = tab.Item; _ = OpenVodPanelAsync(); });
        Item("Remove from multiview", () =>
        {
            slot.Tab = null;
            if (_active == tab && _multiviewSlots.FirstOrDefault(s => s.Tab != null)?.Tab is { } next) Tabs.SelectedItem = next.Item;
            if (_multiviewZoom == tab) _multiviewZoom = null;
            SaveMultiviewSlots();
            RefreshMultiviewChoices();
            ApplyMultiviewLayout();
        });
        menu.ShowAt(slot.More);
    }

    void ReloadTab(ChannelTab tab)
    {
        if (tab.Vod is { } vod) _ = PlayVodAsync(tab, vod, tab.Playback.Player?.TimePos ?? tab.VodPosition);
        else _ = PlayAsync(tab);
    }

    void ZoomMultiview(ChannelTab? tab)
    {
        if (tab != null) Tabs.SelectedItem = tab.Item;
        _multiviewZoom = tab;
        ApplyMultiviewLayout();
        FocusMultiviewPlayer();
    }

    void FocusMultiviewPlayer()
    {
        if (!MultiviewActive) return;
        MultiviewRoot.UpdateLayout();
        MultiviewRoot.Focus(FocusState.Programmatic);
    }

    void PrepareMultiviewAdd()
    {
        int empty = _multiviewSlots.FindIndex(s => s.Tab == null);
        if (empty >= 0) _multiviewFocus = empty;
        else if (_multiviewSlots.Count < 4)
        {
            MultiviewCount.SelectedIndex = _multiviewSlots.Count - 1;
            _multiviewFocus = _multiviewSlots.Count - 1;
        }
    }

    void ShowMultiviewAdd(int? index = null)
    {
        if (index is { } slot) _multiviewFocus = slot;
        else PrepareMultiviewAdd();
        MultiviewAddFlyout.ShowAt(MultiviewAdd);
        MultiviewAddBox.Focus(FocusState.Programmatic);
    }

    void CloseMultiviewTab(ChannelTab tab)
    {
        S.MultiviewPinnedChats.Remove(tab.Login);
        foreach (var slot in _multiviewSlots.Where(s => s.Tab == tab))
            slot.Tab = _tabs.FirstOrDefault(t => !_multiviewSlots.Any(s => s.Tab == t));
        if (_multiviewZoom == tab) _multiviewZoom = null;
        if (_multiviewRecipient == tab) _multiviewRecipient = _tabs.FirstOrDefault();
        if (_multiviewChats.Remove(tab, out var pane))
        {
            MultiviewChatDock.Children.Remove(pane.Root);
            pane.View.Dispose();
        }
        SaveMultiviewSlots();
    }

    void DisposeMultiviewChats()
    {
        foreach (var pane in _multiviewChats.Values) pane.View.Dispose();
        _multiviewChats.Clear();
    }

    void WireMultiviewDivider()
    {
        bool dragging = false;
        double start = 0, size = 0;
        MultiviewDivider.PointerPressed += (_, e) =>
        {
            dragging = true;
            bool right = _multiviewResolvedDock == "Right";
            var p = e.GetCurrentPoint(MultiviewContent).Position;
            start = right ? p.X : p.Y;
            size = right ? MultiviewChatDock.ActualWidth : MultiviewChatDock.ActualHeight;
            MultiviewDivider.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        MultiviewDivider.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var p = e.GetCurrentPoint(MultiviewContent).Position;
            if (_multiviewResolvedDock == "Right") S.MultiviewChatWidth = Math.Clamp(size - (p.X - start), 260, 600);
            else S.MultiviewChatHeight = Math.Clamp(size - (p.Y - start), 140, 700);
            ResizeMultiviewDock();
        };
        MultiviewDivider.PointerReleased += (_, e) =>
        {
            dragging = false;
            MultiviewDivider.ReleasePointerCapture(e.Pointer);
            _multiviewUpdating = true;
            MultiviewChatWidth.Value = S.MultiviewChatWidth;
            MultiviewChatHeight.Value = S.MultiviewChatHeight;
            _multiviewUpdating = false;
            Settings.Save();
        };
        MultiviewDivider.PointerCaptureLost += (_, _) => dragging = false;
    }

    void OnTabVideoInput(ChannelTab tab, string command)
    {
        if (_closing) return;
        if (MultiviewActive && _multiviewSlots.Any(s => s.Tab == tab))
        {
            if (command is "click" or "dblclick" or "wheel 1" or "wheel -1")
            {
                Tabs.SelectedItem = tab.Item;
                MultiviewRoot.Focus(FocusState.Programmatic);
            }
            if (command == "dblclick") { ZoomMultiview(_multiviewZoom == tab ? null : tab); return; }
        }
        if (tab == _active) OnVideoInput(command);
    }

    void UpdateMultiviewVideoBounds()
    {
        bool show = !IsIconic(_hwnd) && !_dialogOpen && !_vodPanelOpen && _videoPopups.Count == 0 && Root.XamlRoot != null && MultiviewRoot.Visibility == Visibility.Visible;
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        MultiviewSlot? focused = null;
        foreach (var tab in _tabs)
        {
            var slot = _multiviewSlots.FirstOrDefault(s => s.Tab == tab && s.Root.Parent == MultiviewVideos);
            bool visible = show && slot != null && slot.Area.ActualWidth > 0 && slot.Area.ActualHeight > 0 &&
                tab.Playback.AdStatus != AdSkipStatus.Waiting && tab.Playback.State is PlayerState.Playing or PlayerState.Buffering;
            if (!visible) { tab.Playback.Host?.Hide(); continue; }
            var area = slot!.Area;
            var point = area.TransformToVisual(Root).TransformPoint(new Point());
            tab.Playback.Host?.SetBounds((int)Math.Round(point.X * scale), (int)Math.Round(point.Y * scale),
                (int)Math.Round(area.ActualWidth * scale), (int)Math.Round(area.ActualHeight * scale), true);
            if (tab == _active && tab.Playback.Host != null) focused = slot;
        }
        if (focused == null) { HidePlayerOverlay(); return; }
        PlacePlayerOverlay(focused.Area);
    }
}
