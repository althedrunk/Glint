using Glint.Chat;
using Glint.Player;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Glint;

sealed class ChannelTab
{
    public ChannelTab(ChannelEntry entry, ChatChannel chat)
    {
        Entry = entry;
        Chat = chat;
    }

    public ChannelEntry Entry { get; }
    public ChatChannel Chat { get; }
    public string Login => Entry.Login;
    public TabViewItem Item { get; set; } = null!;
    public TabPlayback Playback { get; } = new();
    public TextBlock HeaderText { get; } = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    public Button AudioButton { get; } = new();
    public ImageBrush AvatarBrush { get; } = new() { Stretch = Stretch.UniformToFill };
    public Ellipse LiveDot { get; } = new()
    {
        Width = 8, Height = 8, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 79, 94)),
        Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 36)), StrokeThickness = 1.5,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -2),
    };
    public string Draft { get; set; } = "";
    public override string ToString() => Entry.DisplayName;

    // VOD state. Vod == null means we're watching live.
    public VideoInfo? Vod { get; set; }
    public double VodPosition { get; set; }
    public ChatChannel? Replay { get; set; }
    public VodReplay? ReplayEngine { get; set; }
    public List<VideoInfo>? Videos { get; set; }
    public DateTime VideosFetched { get; set; }
}

public sealed partial class MainWindow
{
    bool _changingTabs;

    ChannelEntry GetEntry(string login)
    {
        login = login.ToLowerInvariant();
        if (!_entries.TryGetValue(login, out var e)) _entries[login] = e = new ChannelEntry(login);
        return e;
    }

    bool _refreshingLive;

    async Task RefreshLiveAsync(bool openTabsOnly = false)
    {
        if (_closing || _refreshingLive) return;
        var logins = (openTabsOnly ? Enumerable.Empty<string>() : _entries.Values.Where(e => e.IsFavorite || e.IsFollowed).Select(e => e.Login))
            .Concat(_tabs.Select(t => t.Login)).Distinct().ToList();
        if (logins.Count == 0) return;
        _refreshingLive = true;
        try
        {
            var infos = await TwitchGql.GetUsersAsync(logins);
            if (_closing) return;
            foreach (var info in infos)
            {
                var entry = GetEntry(info.Login);
                bool wasLive = entry.IsLive;
                entry.Update(info);
                if (_tabs.FirstOrDefault(t => t.Entry == entry) is not { } tab) continue;
                UpdateTabHeader(tab);
                // Start an open offline channel when it goes live.
                if (!S.ChatOnly && (tab == _active || tab.Playback.Started) && tab.Vod == null && !wasLive && entry.IsLive &&
                    tab.Playback.State is PlayerState.Idle or PlayerState.Ended or PlayerState.Error)
                    _ = PlayAsync(tab);
            }
            RebuildSidebar();
            UpdateInfoBar();
        }
        finally { _refreshingLive = false; }
    }

    void RebuildSidebar()
    {
        var target = _entries.Values.Where(e => e.IsFavorite || e.IsFollowed)
            .OrderByDescending(e => e.IsLive)
            .ThenByDescending(e => e.Stream?.Viewers ?? 0)
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Move items rather than resetting the list, so scroll position and hover state survive a refresh.
        for (int i = 0; i < target.Count; i++)
        {
            if (i < _sidebarItems.Count && _sidebarItems[i] == target[i]) continue;
            int existing = _sidebarItems.IndexOf(target[i]);
            if (existing >= 0) _sidebarItems.Move(existing, i);
            else _sidebarItems.Insert(i, target[i]);
        }
        while (_sidebarItems.Count > target.Count) _sidebarItems.RemoveAt(_sidebarItems.Count - 1);
        SidebarHeader.Text = _entries.Values.Any(e => e.IsFollowed) ? "FOLLOWING" : "CHANNELS";
    }

    void AddBoxKey(TextBox box, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        var login = NormalizeLogin(box.Text);
        if (login == null) return;
        box.Text = "";
        var entry = GetEntry(login);
        if (!entry.IsFavorite)
        {
            entry.IsFavorite = true;
            S.Favorites.Add(login);
            Settings.Save();
            RebuildSidebar();
        }
        if (ReferenceEquals(box, MultiviewAddBox)) OpenChannel(login, true);
        else OpenChannelInCurrentTab(login);
        e.Handled = true;
    }

    // Accepts "xqc", "@xqc", "twitch.tv/xqc", "https://www.twitch.tv/xqc/videos" etc.
    static string? NormalizeLogin(string input)
    {
        var s = input.Trim().TrimEnd('/');
        var i = s.LastIndexOf("twitch.tv/", StringComparison.OrdinalIgnoreCase);
        if (i >= 0) s = s[(i + 10)..];
        s = s.Split('/', '?', '#')[0].TrimStart('@').ToLowerInvariant();
        return s.Length is >= 2 and <= 25 && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? s : null;
    }

    void ChannelList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: ChannelEntry entry } source) return;
        var menu = new MenuFlyout();
        void Item(string text, string glyph, Action click)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }
        Item("Open in current tab", "", () => OpenChannelInCurrentTab(entry.Login));
        Item("Open in new tab", "\uE710", () => OpenChannel(entry.Login, true));
        Item("Open on twitch.tv", "", () => Launch($"https://www.twitch.tv/{entry.Login}"));
        menu.Items.Add(new MenuFlyoutSeparator());
        Item(entry.IsFavorite ? "Remove" : "Pin to sidebar", entry.IsFavorite ? "" : "", () =>
        {
            entry.IsFavorite = !entry.IsFavorite;
            S.Favorites.RemoveAll(f => f.Equals(entry.Login, StringComparison.OrdinalIgnoreCase));
            if (entry.IsFavorite) S.Favorites.Add(entry.Login);
            Settings.Save();
            RebuildSidebar();
        });
        menu.ShowAt(source, e.GetPosition(source));
        e.Handled = true;
    }

    void OpenChannelInCurrentTab(string login) => OpenChannel(login, true, Tabs.SelectedItem as TabViewItem);

    void OpenChannel(string login, bool activate) => OpenChannel(login, activate, null);

    void OpenChannel(string login, bool activate, TabViewItem? replace)
    {
        CloseBrowse();
        login = login.ToLowerInvariant();
        if (_tabs.FirstOrDefault(t => t.Login == login) is { } existing)
        {
            if (activate) Tabs.SelectedItem = existing.Item;
            if (replace is { Tag: null }) Tabs.TabItems.Remove(replace);
            return;
        }

        var entry = GetEntry(login);
        var chat = new ChatChannel(login) { SelfLogin = _chatLogin };
        chat.Mentioned += OnMentioned;
        _chats[login] = chat;
        var tab = new ChannelTab(entry, chat);
        tab.Playback.Volume = replace?.Tag is ChannelTab previous ? previous.Playback.Volume : S.UseLastVolumeForNewTabs ? S.Volume : S.NewTabVolume;
        SetTabMuted(tab, replace?.Tag is ChannelTab old ? old.Playback.Muted : S.MutedTabs.Contains(login, StringComparer.OrdinalIgnoreCase));
        tab.Item = new TabViewItem { Tag = tab };
        // Round avatar with a live dot, name, then the mute button on the right next to close.
        tab.Item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var avatar = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        avatar.Children.Add(new Border { CornerRadius = new CornerRadius(9), Background = tab.AvatarBrush });
        avatar.Children.Add(tab.LiveDot);
        tab.AudioButton.Style = (Style)Root.Resources["TabAudioButton"];
        tab.AudioButton.Click += (_, _) => ToggleTabMute(tab);
        Grid.SetColumn(tab.HeaderText, 1);
        Grid.SetColumn(tab.AudioButton, 2);
        header.Children.Add(avatar);
        header.Children.Add(tab.HeaderText);
        header.Children.Add(tab.AudioButton);
        tab.Item.Header = header;
        UpdateTabHeader(tab);
        _tabs.Add(tab);
        int index = replace == null ? -1 : Tabs.TabItems.IndexOf(replace);
        if (index >= 0)
        {
            _changingTabs = true;
            try
            {
                Tabs.TabItems[index] = tab.Item;
                if (replace!.Tag is ChannelTab replaced) CloseTab(replaced);
            }
            finally { _changingTabs = false; }
        }
        else Tabs.TabItems.Add(tab.Item);
        _irc.Join(login);
        _ = InitChannelAsync(tab);
        if (activate || Tabs.SelectedItem == null)
        {
            Tabs.SelectedItem = tab.Item;
            ActivateTab(tab);
        }
        UpdateEmptyState();
        RefreshMultiviewChoices();
        SaveTabs();
    }

    async Task InitChannelAsync(ChannelTab tab)
    {
        if (!tab.Entry.Known)
        {
            var infos = await TwitchGql.GetUsersAsync(new[] { tab.Login });
            if (_closing || !_tabs.Contains(tab)) return;
            if (infos.Count == 0)
            {
                tab.Chat.AddSystem($"There's no channel called \"{tab.Login}\"");
                if (tab == _active) ShowVideoMessage("Channel not found", null, retry: false);
                return;
            }
            tab.Entry.Update(infos[0]);
            UpdateTabHeader(tab);
            if (tab == _active) UpdateInfoBar();
        }
        await tab.Chat.InitAsync(tab.Entry.Id!);
        QueueEmoteSuggestions();
        if (!_tabs.Contains(tab)) return;
        if (tab.Chat.Emotes.SevenTvSetId is { } set) _stv.Subscribe(set);
        if (tab.Entry.Id is { } roomId) _stv.SubscribeChannel(roomId);
    }

    void UpdateTabHeader(ChannelTab tab)
    {
        tab.HeaderText.Text = tab.Entry.DisplayName;
        AutomationProperties.SetName(tab.Item, tab.Entry.DisplayName);
        UpdateTabAudioButton(tab);
        if (tab.AvatarBrush.ImageSource != tab.Entry.Avatar) tab.AvatarBrush.ImageSource = tab.Entry.Avatar;
        tab.LiveDot.Visibility = tab.Entry.IsLive ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(tab.Item, tab.Entry.Tooltip);
        RefreshMultiviewChoices();
    }

    void UpdateTabAudioButton(ChannelTab tab)
    {
        tab.AudioButton.Content = tab.Playback.Muted || tab.Playback.Volume == 0 ? "\uE74F" : "\uE767";
        string action = tab.Playback.Muted ? "Unmute" : "Mute";
        AutomationProperties.SetName(tab.AudioButton, $"{action} {tab.Entry.DisplayName}");
        ToolTipService.SetToolTip(tab.AudioButton, $"{action} {tab.Entry.DisplayName}");
        UpdateMultiviewAudio(tab);
    }

    void CloseTab(ChannelTab tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0) return;
        bool active = tab == _active;
        bool recipient = tab == ChatTarget;
        SaveChatDraft();
        if (active)
        {
            _active = null;
            _vodTimer.Stop();
        }
        _tabs.Remove(tab);
        CloseMultiviewTab(tab);
        tab.Playback.Dispose();
        Tabs.TabItems.Remove(tab.Item);
        _irc.Part(tab.Login);
        _chats.TryRemove(tab.Login, out _);
        if (tab.Chat.Emotes.SevenTvSetId is { } set) _stv.Unsubscribe(set);
        if (tab.Entry.Id is { } roomId && !_tabs.Any(t => t.Entry.Id == roomId)) _stv.UnsubscribeChannel(roomId);
        if (active && _active == null)
        {
            if (Tabs.SelectedItem is TabViewItem { Tag: null } empty && Tabs.TabItems.Contains(empty)) ActivateEmptyTab();
            else if (_tabs.Count > 0) Tabs.SelectedItem = _tabs[Math.Min(index, _tabs.Count - 1)].Item;
            else
            {
                Chat.SetChannel(null);
            }
        }
        UpdateEmptyState();
        RefreshMultiviewChoices();
        if (MultiviewActive) ApplyMultiviewLayout();
        if (recipient) { RestoreChatDraft(); UpdateChatInput(); }
        SaveTabs();
    }

    void CycleTab(int dir)
    {
        int count = Tabs.TabItems.Count;
        if (count < 2) return;
        int i = Tabs.SelectedIndex;
        Tabs.SelectedIndex = ((i + dir) % count + count) % count;
    }

    void SaveTabs()
    {
        if (_changingTabs) return;
        S.OpenTabs = Tabs.TabItems.OfType<TabViewItem>().Select(t => t.Tag).OfType<ChannelTab>().Select(t => t.Login).ToList();
        S.ActiveTab = _active?.Login;
        Settings.Save();
    }

    void OpenNewTab()
    {
        CloseBrowse();
        var item = new TabViewItem { Header = "New tab", IconSource = new SymbolIconSource { Symbol = Symbol.Add } };
        AutomationProperties.SetName(item, "New tab");
        Tabs.TabItems.Add(item);
        Tabs.SelectedItem = item;
        ActivateEmptyTab();
        EmptyAddBox.Focus(FocusState.Keyboard);
    }

    void CloseTabItem(TabViewItem item)
    {
        if (item.Tag is ChannelTab tab) CloseTab(tab);
        else
        {
            Tabs.TabItems.Remove(item);
            if (Tabs.SelectedItem is TabViewItem { Tag: ChannelTab selected }) ActivateTab(selected);
            else ActivateEmptyTab();
        }
    }

    void ActivateEmptyTab()
    {
        if (_changingTabs || _closing) return;
        CloseBrowse();
        SaveChatDraft();
        if (_active?.Vod != null && _player?.TimePos is { } pos) _active.VodPosition = pos;
        CloseOverlayMenus();
        _seekDragging = false;
        _vodTimer.Stop();
        CloseVodPanel();
        foreach (var tab in _tabs) tab.Playback.Host?.Hide();
        _active = null;
        Chat.SetChannel(null);
        CancelReply();
        RestoreChatDraft();
        UpdateChatInput();
        ApplyLayoutSettings();
        SaveTabs();
    }

    void ActivateTab(ChannelTab tab)
    {
        CloseBrowse();
        if (_closing || _active == tab || !_tabs.Contains(tab)) return;
        bool wasEmpty = _active == null;
        SaveChatDraft();
        if (_active?.Vod != null && _player?.TimePos is { } pos) _active.VodPosition = pos;
        CloseOverlayMenus();
        _seekDragging = false;
        _vodTimer.Stop();
        CloseVodPanel();
        _host?.Hide();
        _active = tab;
        FocusMultiviewTab(tab);
        UpdateVolumeControl();
        SaveTabs();
        RefreshChatViews();
        SetQualityItems();
        UpdateSpeedItems();
        UpdateInfoBar();
        UpdateChatInput();
        RestoreChatDraft();
        if (wasEmpty) ApplyLayoutSettings();
        else UpdateModeUi();
        if (S.ChatOnly) return;
        if (tab.Playback.Started)
        {
            RenderPlayerState();
            if (tab.Vod != null) { VodTick(); _vodTimer.Start(); }
        }
        else if (tab.Vod != null) _ = PlayVodAsync(tab, tab.Vod, tab.VodPosition);
        else _ = PlayAsync(tab);
    }

    void UpdateInfoBar()
    {
        if (_active is not { } tab) return;
        var e = tab.Entry;
        InfoName.Text = e.DisplayName;
        InfoAvatar.ImageSource = e.Avatar;
        bool live = e.IsLive && tab.Vod == null;
        LiveBadge.Visibility = InfoLiveRing.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        InfoTitle.Text = tab.Vod?.Title ?? e.Stream?.Title ?? "Offline";
        ChatHeader.Text = tab.Vod != null ? "Chat replay" : "Chat";
        UpdateInfoMeta();
        UpdateOverlayState();
    }

    void UpdateInfoMeta()
    {
        if (_closing) return;
        if (_active is not { } tab) return;
        if (tab.Vod is { } v)
        {
            InfoMeta.Text = Join(v.CreatedAt.LocalDateTime.ToString("d MMM yyyy"), $"{v.Views:N0} views", v.Game);
            return;
        }
        if (tab.Entry.Stream is not { } s)
        {
            InfoMeta.Text = "";
            _overlay.StatusText.Text = "";
            return;
        }
        var up = DateTimeOffset.UtcNow - s.StartedAt;
        string? adStatus = tab.Playback.AdStatus == AdSkipStatus.Waiting ? "Waiting for stream after ads" :
            tab.Playback.AdStatus == AdSkipStatus.AlternateStream ? "Alternate stream during ads" : null;
        _overlay.StatusText.Text = Join(adStatus, $"{s.Viewers:N0} viewers",
            up > TimeSpan.Zero && up < TimeSpan.FromDays(7) ? $"{(int)up.TotalHours}:{up.Minutes:00}:{up.Seconds:00}" : null);
        InfoMeta.Text = Join(
            s.Game,
            $"{s.Viewers:N0} viewers",
            up > TimeSpan.Zero && up < TimeSpan.FromDays(7) ? $"{(int)up.TotalHours}:{up.Minutes:00}:{up.Seconds:00}" : null,
            adStatus ?? (_playerState == PlayerState.Buffering ? "buffering" : null));
    }

    static string Join(params string?[] parts) => string.Join("  ·  ", parts.Where(p => !string.IsNullOrEmpty(p)));
}
