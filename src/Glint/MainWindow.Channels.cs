using Glint.Chat;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    ChannelEntry GetEntry(string login)
    {
        login = login.ToLowerInvariant();
        if (!_entries.TryGetValue(login, out var e)) _entries[login] = e = new ChannelEntry(login);
        return e;
    }

    async Task RefreshLiveAsync()
    {
        var logins = _entries.Values.Where(e => e.IsFavorite || e.IsFollowed).Select(e => e.Login)
            .Concat(_tabs.Select(t => t.Login)).Distinct().ToList();
        if (logins.Count == 0) return;
        var infos = await TwitchGql.GetUsersAsync(logins);
        foreach (var info in infos)
        {
            var entry = GetEntry(info.Login);
            bool wasLive = entry.IsLive;
            entry.Update(info);
            if (_tabs.FirstOrDefault(t => t.Entry == entry) is not { } tab) continue;
            UpdateTabHeader(tab);
            // if you're sat on an offline channel and it goes live, just start it
            if (tab == _active && tab.Vod == null && !wasLive && entry.IsLive && _playerState is Player.PlayerState.Idle or Player.PlayerState.Ended or Player.PlayerState.Error)
                _ = PlayAsync(tab);
        }
        RebuildSidebar();
        UpdateInfoBar();
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
        OpenChannel(login, true);
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
        Item("Open", "", () => OpenChannel(entry.Login, true));
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

    void OpenChannel(string login, bool activate)
    {
        login = login.ToLowerInvariant();
        if (_tabs.FirstOrDefault(t => t.Login == login) is { } existing)
        {
            if (activate) Tabs.SelectedItem = existing.Item;
            return;
        }

        var entry = GetEntry(login);
        var chat = new ChatChannel(login) { SelfLogin = _irc.IsAuthenticated ? S.UserLogin : null };
        _chats[login] = chat;
        var tab = new ChannelTab(entry, chat);
        tab.Item = new TabViewItem { Tag = tab };
        UpdateTabHeader(tab);
        _tabs.Add(tab);
        Tabs.TabItems.Add(tab.Item);
        _irc.Join(login);
        _ = InitChannelAsync(tab);
        if (activate || Tabs.SelectedItem == null) Tabs.SelectedItem = tab.Item;
        UpdateEmptyState();
        SaveTabs();
    }

    async Task InitChannelAsync(ChannelTab tab)
    {
        if (!tab.Entry.Known)
        {
            var infos = await TwitchGql.GetUsersAsync(new[] { tab.Login });
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
        if (tab.Chat.Emotes.SevenTvSetId is { } set && _tabs.Contains(tab)) _stv.Subscribe(set);
    }

    void UpdateTabHeader(ChannelTab tab)
    {
        tab.Item.Header = tab.Entry.DisplayName;
        if (tab.Entry.Avatar is { } av && tab.Item.IconSource is not ImageIconSource)
            tab.Item.IconSource = new ImageIconSource { ImageSource = av };
        ToolTipService.SetToolTip(tab.Item, tab.Entry.Tooltip);
    }

    void CloseTab(ChannelTab tab)
    {
        int index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        Tabs.TabItems.Remove(tab.Item);
        _irc.Part(tab.Login);
        _chats.TryRemove(tab.Login, out _);
        if (tab.Chat.Emotes.SevenTvSetId is { } set) _stv.Unsubscribe(set);
        if (tab == _active)
        {
            _active = null;
            if (_tabs.Count > 0) Tabs.SelectedItem = _tabs[Math.Min(index, _tabs.Count - 1)].Item;
            else
            {
                StopPlayback();
                Chat.SetChannel(null);
            }
        }
        UpdateEmptyState();
        SaveTabs();
    }

    void CycleTab(int dir)
    {
        if (_tabs.Count < 2) return;
        int i = Tabs.SelectedIndex;
        Tabs.SelectedIndex = ((i + dir) % _tabs.Count + _tabs.Count) % _tabs.Count;
    }

    void SaveTabs()
    {
        S.OpenTabs = Tabs.TabItems.OfType<TabViewItem>().Select(t => ((ChannelTab)t.Tag).Login).ToList();
        S.ActiveTab = _active?.Login;
        Settings.Save();
    }

    void ActivateTab(ChannelTab tab)
    {
        if (_active == tab) return;
        if (_active?.Vod != null && _player?.TimePos is { } pos) _active.VodPosition = pos;
        CloseVodPanel();
        _active = tab;
        SaveTabs();
        Chat.SetChannel(tab.Replay ?? tab.Chat);
        UpdateInfoBar();
        UpdateChatInput();
        UpdateModeUi();
        if (tab.Vod != null) _ = PlayVodAsync(tab, tab.Vod, tab.VodPosition);
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
        _overlay.StatusText.Text = Join($"{s.Viewers:N0} viewers",
            up > TimeSpan.Zero && up < TimeSpan.FromDays(7) ? $"{(int)up.TotalHours}:{up.Minutes:00}:{up.Seconds:00}" : null);
        InfoMeta.Text = Join(
            s.Game,
            $"{s.Viewers:N0} viewers",
            up > TimeSpan.Zero && up < TimeSpan.FromDays(7) ? $"{(int)up.TotalHours}:{up.Minutes:00}:{up.Seconds:00}" : null,
            _adBreak ? "skipping ads" : _playerState == Player.PlayerState.Buffering ? "buffering" : null);
    }

    static string Join(params string?[] parts) => string.Join("  ·  ", parts.Where(p => !string.IsNullOrEmpty(p)));
}
