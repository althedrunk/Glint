using System.Globalization;
using Glint.Chat;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Glint;

public sealed class VodItem
{
    public VodItem(VideoInfo v)
    {
        Video = v;
        Title = v.Title;
        Length = FormatTime(v.LengthSeconds);
        Meta = string.Join("  ·  ", new[] { Ago(v.CreatedAt), $"{ChannelEntry.FormatCount(v.Views)} views",
            v.ClipBy != null ? $"Clipped by {v.ClipBy}" : v.Game }.Where(s => s != null));
        if (v.Thumbnail != null) Thumbnail = new BitmapImage(new Uri(v.Thumbnail)) { DecodePixelWidth = 256 };
    }

    public VideoInfo Video { get; }
    public string Title { get; }
    public string Length { get; }
    public string Meta { get; }
    public ImageSource? Thumbnail { get; }

    public override string ToString() => Title; // what screen readers announce

    internal static string Ago(DateTimeOffset when)
    {
        var days = (DateTime.Now.Date - when.LocalDateTime.Date).Days;
        return days switch
        {
            0 => "Today",
            1 => "Yesterday",
            < 7 => $"{days} days ago",
            _ => when.LocalDateTime.ToString(when.Year == DateTime.Now.Year ? "d MMM" : "d MMM yyyy"),
        };
    }

    public static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}

public sealed partial class MainWindow
{
    static readonly double[] Speeds = { 0.5, 0.75, 1, 1.25, 1.5, 2 };
    static readonly (string Label, string Value)[] ClipPeriods =
        { ("Last 24 hours", "LAST_DAY"), ("Last 7 days", "LAST_WEEK"), ("Last 30 days", "LAST_MONTH"), ("All time", "ALL_TIME") };
    bool _vodPanelOpen, _seekDragging, _updatingSeek;
    string _vodMode = "ARCHIVE";
    int _vodVersion;
    string? _bannerUrl;

    void WireVodControls()
    {
        foreach (var button in new ButtonBase[] { VideosButton, OfflineVodsButton, InfoNameButton, InfoAvatarButton })
            button.Click += (_, _) => _ = OpenVodPanelAsync();
        VodPanelClose.Click += (_, _) => CloseVodPanel();
        VodModeArchive.Click += (_, _) => _ = ShowVideosAsync("ARCHIVE");
        VodModeHighlights.Click += (_, _) => _ = ShowVideosAsync("HIGHLIGHT");
        VodModeClips.Click += (_, _) => _ = ShowVideosAsync("CLIPS");
        ClipPeriod.ItemsSource = ClipPeriods.Select(p => p.Label).ToList();
        ClipPeriod.SelectedIndex = 1;
        ClipPeriod.SelectionChanged += (_, _) => { if (_vodMode == "CLIPS") _ = ShowVideosAsync("CLIPS"); };
        ChannelAlertButton.Click += (_, _) => { if (_active != null) SetLiveAlert(_active.Login, ChannelAlertButton.IsChecked == true); };
        ChannelTwitchButton.Click += (_, _) => { if (_active != null) Launch($"https://www.twitch.tv/{_active.Login}"); };
        ChannelWatchButton.Click += (_, _) =>
        {
            if (_active is not { } tab) return;
            CloseVodPanel();
            if (tab.Vod != null) ExitVod(tab);
        };
        VodGrid.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is not VodItem item || _active == null) return;
            CloseVodPanel();
            _ = PlayVodAsync(_active, item.Video, 0);
        };
        GoLiveButton.Click += (_, _) => { if (_active != null) ExitVod(_active); };
        _overlay.GoLiveButton.Click += (_, _) => { if (_active != null) ExitVod(_active); };
        _overlay.PlayPauseButton.Click += (_, _) => TogglePause();
        _overlay.BackButton.Click += (_, _) => SeekBy(-S.SeekSeconds);
        _overlay.ForwardButton.Click += (_, _) => SeekBy(S.SeekSeconds);

        _overlay.Seek.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) => ShowSeekPreview(e.GetCurrentPoint(_overlay.Seek).Position.X)), true);
        _overlay.Seek.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, _) => _overlay.SeekPreview.Visibility = Visibility.Collapsed), true);

        var speeds = new MenuFlyout();
        TrackFlyout(speeds);
        foreach (var speed in Speeds)
        {
            string label = speed.ToString("0.##", CultureInfo.InvariantCulture) + "x";
            var item = new RadioMenuFlyoutItem { Text = label, Tag = speed, GroupName = "Speed", IsChecked = speed == 1 };
            item.Click += (_, _) =>
            {
                if (_active == null) return;
                _active.Playback.Speed = speed;
                _overlay.SpeedButton.Content = label;
                _player?.SetSpeed(speed);
            };
            speeds.Items.Add(item);
        }
        SetOverlayMenu(_overlay.SpeedButton, speeds);

        // The slider swallows pointer events, hence handledEventsToo. Seek on release, not on every pixel of a drag.
        _overlay.Seek.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _seekDragging = true), true);
        _overlay.Seek.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => EndSeekDrag()), true);
        _overlay.Seek.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndSeekDrag()), true);
        _overlay.Seek.ValueChanged += (_, e) =>
        {
            if (_updatingSeek) return;
            UpdateVodTime(e.NewValue);
            if (!_seekDragging) _player?.Seek(e.NewValue); // keyboard nudges on the slider
            ShowPlayerOverlay();
        };
    }

    void UpdateSpeedItems()
    {
        double speed = _active?.Playback.Speed ?? 1;
        _overlay.SpeedButton.Content = speed.ToString("0.##", CultureInfo.InvariantCulture) + "x";
        if (_overlayMenus.TryGetValue(_overlay.SpeedButton, out var menu))
            foreach (var item in menu.Items.OfType<RadioMenuFlyoutItem>()) item.IsChecked = (double)item.Tag == speed;
    }

    void EndSeekDrag()
    {
        if (!_seekDragging) return;
        _seekDragging = false;
        _player?.Seek(_overlay.Seek.Value);
        ShowPlayerOverlay();
    }

    void ShowSeekPreview(double x)
    {
        var seek = _overlay.Seek;
        // The track is inset from the slider's edges by about half the thumb.
        double track = Math.Max(1, seek.ActualWidth - 20);
        double fraction = Math.Clamp((x - 10) / track, 0, 1);
        _overlay.SeekPreviewText.Text = VodItem.FormatTime(fraction * seek.Maximum);
        _overlay.SeekPreview.Visibility = Visibility.Visible;
        _overlay.SeekPreview.Measure(new Windows.Foundation.Size(200, 40));
        double w = _overlay.SeekPreview.DesiredSize.Width;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_overlay.SeekPreview, Math.Clamp(x - w / 2, 0, Math.Max(0, seek.ActualWidth - w)));
    }

    // 0-9 jump to 0%-90% of the video, like most players.
    void SeekToFraction(double fraction)
    {
        if (_player?.Duration is { } d && d > 0) _player.Seek(d * fraction);
        ShowPlayerOverlay();
    }

    void SeekBy(double seconds)
    {
        if (_player?.TimePos is { } pos) _player.Seek(pos + seconds);
        ShowPlayerOverlay();
    }

    // The channel page: who they are, then their past broadcasts, highlights and clips.
    async Task OpenVodPanelAsync()
    {
        if (_active is not { } tab) return;
        CloseBrowse();
        _vodPanelOpen = true;
        VodPanel.Visibility = Visibility.Visible;
        FillChannelHeader(tab);
        UpdateVideoBounds();
        _ = LoadChannelAboutAsync(tab);
        await ShowVideosAsync(_vodMode);
    }

    void FillChannelHeader(ChannelTab tab)
    {
        var entry = tab.Entry;
        var about = tab.About?.About;
        VodPanelTitle.Text = entry.DisplayName;
        ChannelAlertButton.IsChecked = HasLiveAlert(entry.Login);
        ChannelAvatar.ImageSource = entry.Avatar;
        var live = entry.IsLive ? Visibility.Visible : Visibility.Collapsed;
        ChannelLiveRing.Visibility = ChannelLiveBadge.Visibility = ChannelWatchButton.Visibility = live;

        var parts = new List<string>();
        if (about is { Followers: > 0 }) parts.Add($"{ChannelEntry.FormatCount(about.Followers)} followers");
        if (about?.Partner == true) parts.Add("Partner");
        if (entry.Stream is { } stream)
            parts.Add($"Live now{(stream.Game != null ? $" with {stream.Game}" : "")}, {ChannelEntry.FormatCount(stream.Viewers)} watching");
        else if (about?.LastLive is { } last)
        {
            var ago = VodItem.Ago(last);
            parts.Add("Last live " + (ago is "Today" or "Yesterday" ? ago.ToLowerInvariant() : ago.EndsWith("ago") ? ago : "on " + ago)
                + (about.LastGame != null ? $" with {about.LastGame}" : ""));
        }
        ChannelMeta.Text = string.Join("  ·  ", parts);
        ChannelBio.Text = about?.Description?.Trim() ?? "";
        ChannelBio.Visibility = ChannelBio.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_bannerUrl != about?.Banner)
        {
            _bannerUrl = about?.Banner;
            ChannelBanner.Source = _bannerUrl != null ? new BitmapImage(new Uri(_bannerUrl)) : null;
        }
    }

    async Task LoadChannelAboutAsync(ChannelTab tab)
    {
        if (tab.About is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(10)) return;
        ChannelAbout? about = null;
        try { about = await TwitchGql.GetChannelAboutAsync(tab.Login); }
        catch (Exception ex) { Log.Error("Channel about", ex); }
        tab.About = (DateTime.UtcNow, about);
        if (_vodPanelOpen && _active == tab) FillChannelHeader(tab);
    }

    // mode: ARCHIVE, HIGHLIGHT or CLIPS (for the period picked beside it).
    async Task ShowVideosAsync(string mode)
    {
        if (_active is not { } tab) return;
        _vodMode = mode;
        VodModeArchive.IsChecked = mode == "ARCHIVE";
        VodModeHighlights.IsChecked = mode == "HIGHLIGHT";
        VodModeClips.IsChecked = mode == "CLIPS";
        ClipPeriod.Visibility = mode == "CLIPS" ? Visibility.Visible : Visibility.Collapsed;
        var period = ClipPeriods[Math.Max(0, ClipPeriod.SelectedIndex)].Value;
        var key = mode == "CLIPS" ? "CLIPS:" + period : mode;
        int version = ++_vodVersion;

        if (!tab.VideoCache.TryGetValue(key, out var cached) || DateTime.UtcNow - cached.At > TimeSpan.FromMinutes(5))
        {
            VodGrid.ItemsSource = null;
            VodEmpty.Visibility = Visibility.Collapsed;
            VodSpinner.IsActive = true;
            List<VideoInfo> items;
            try { items = mode == "CLIPS" ? await TwitchGql.GetClipsAsync(tab.Login, period) : await TwitchGql.GetVideosAsync(tab.Login, mode); }
            catch (Exception ex) { Log.Error("Videos", ex); items = new(); }
            cached = (DateTime.UtcNow, items);
            tab.VideoCache[key] = cached;
        }
        if (version != _vodVersion || _active != tab || !_vodPanelOpen) return;
        VodSpinner.IsActive = false;
        VodGrid.ItemsSource = cached.Items.Select(v => new VodItem(v)).ToList();
        VodEmpty.Text = mode switch
        {
            "HIGHLIGHT" => "No highlights yet.",
            "CLIPS" => "No clips from this period.",
            _ => "No past broadcasts. Some streamers keep them private.",
        };
        VodEmpty.Visibility = cached.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void CloseVodPanel()
    {
        if (!_vodPanelOpen) return;
        _vodPanelOpen = false;
        VodPanel.Visibility = Visibility.Collapsed;
        UpdateVideoBounds();
    }

    async Task PlayVodAsync(ChannelTab tab, VideoInfo video, double start)
    {
        if (_closing || !_tabs.Contains(tab) || S.ChatOnly) return;
        if (!await EnsureVideoRuntimeAsync(tab)) return;
        var playback = tab.Playback;
        int version = ++playback.Version;
        playback.Started = true;
        playback.Resolving = true;
        playback.ErrorMessage = null;
        playback.Ads?.Stop();
        playback.AdStatus = AdSkipStatus.None;

        if (tab.Vod?.Id != video.Id || tab.Replay == null)
        {
            tab.Replay = new ChatChannel(tab.Chat);
            // Clips have no chat replay of their own.
            tab.ReplayEngine = video.ClipSlug == null ? new VodReplay(video.Id, tab.Replay) : null;
            if (video.ClipSlug != null) tab.Replay.AddSystem("Chat replay isn't available for clips.");
        }
        tab.Vod = video;
        tab.VodPosition = start;
        if (tab == _active)
        {
            _vodTimer.Stop();
            RefreshChatViews();
            UpdateInfoBar();
            UpdateModeUi();
            UpdateChatInput();
        }
        OnPlayerState(tab, Player.PlayerState.Loading);

        List<StreamVariant> variants;
        try { variants = video.ClipSlug is { } slug ? await TwitchGql.GetClipVariantsAsync(slug) : await TwitchGql.GetVodVariantsAsync(video.Id); }
        catch (Exception ex) { Log.Error("VOD variants", ex); variants = new(); }
        if (_closing || !_tabs.Contains(tab) || version != playback.Version) return;
        playback.Resolving = false;

        playback.Variants = variants;
        playback.SelectedVariant = PickVariant(variants);
        if (tab == _active) SetQualityItems();
        if (variants.Count == 0)
        {
            playback.Player?.Stop();
            playback.ErrorMessage = video.ClipSlug != null ? "Couldn't load this clip."
                : TwitchSession.IsConnected
                ? "Couldn't load this video. It may be subscriber-only."
                : "Couldn't load this video. Subscriber-only VODs need your twitch.tv session (Settings).";
            OnPlayerState(tab, Player.PlayerState.Error);
            return;
        }
        LoadVod(tab, playback.SelectedVariant!, Math.Max(0, start));
        if (tab == _active) _vodTimer.Start();
    }

    void LoadVod(ChannelTab tab, StreamVariant variant, double? start)
    {
        var player = EnsurePlayer(tab);
        if (player == null) return;
        OnPlayerState(tab, Player.PlayerState.Loading);
        player.Load(variant.Url, start);
        player.SetSpeed(tab.Playback.Speed);
        if (tab == _active) UpdateVideoBounds();
    }

    void ExitVod(ChannelTab tab)
    {
        tab.Vod = null;
        tab.Replay = null;
        tab.ReplayEngine = null;
        tab.VodPosition = 0;
        _vodTimer.Stop();
        RefreshChatViews();
        UpdateInfoBar();
        UpdateModeUi();
        UpdateChatInput();
        _ = PlayAsync(tab);
    }

    void VodTick()
    {
        if (MultiviewActive)
        {
            foreach (var tab in _tabs.Where(t => t != _active && t.Vod != null &&
                (_multiviewSlots.Any(s => s.Tab == t) || _multiviewChats.TryGetValue(t, out var pane) && pane.View.Channel != null)))
            {
                if (tab.Playback.Player?.TimePos is not { } position) continue;
                tab.VodPosition = position;
                tab.ReplayEngine?.Tick(position);
            }
        }
        if (_playerState is not (Player.PlayerState.Playing or Player.PlayerState.Buffering)) return;
        if (_active?.Vod is not { } vod || _player == null) return;
        if (_player.TimePos is not { } pos) return;
        var duration = _player.Duration ?? vod.LengthSeconds;
        _active.VodPosition = pos;

        if (!_seekDragging)
        {
            _updatingSeek = true;
            _overlay.Seek.Maximum = Math.Max(1, duration);
            _overlay.Seek.Value = Math.Min(pos, _overlay.Seek.Maximum);
            _updatingSeek = false;
            UpdateVodTime(pos);
        }
        UpdateOverlayState();
        _active.ReplayEngine?.Tick(pos);
    }

    void UpdateVodTime(double position) =>
        _overlay.StatusText.Text = $"{VodItem.FormatTime(position)} / {VodItem.FormatTime(_player?.Duration ?? _active?.Vod?.LengthSeconds ?? 0)}";

    // Shows/hides the bits that differ between live and VOD.
    void UpdateModeUi()
    {
        var tab = _active;
        bool vod = tab?.Vod != null;
        GoLiveButton.Visibility = vod ? Visibility.Visible : Visibility.Collapsed;
        GoLiveButton.Content = tab?.Entry.IsLive == true ? "Back to live" : "Close video";
        VodBadge.Visibility = vod ? Visibility.Visible : Visibility.Collapsed;
        VodBadgeText.Text = tab?.Vod?.ClipSlug != null ? "CLIP" : "VOD";
        UpdateOverlayState();
        if (vod) UpdateVodTime(tab!.VodPosition);
        UpdateVideoBounds();
    }
}
