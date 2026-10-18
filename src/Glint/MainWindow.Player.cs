using Glint.Player;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Glint;

public sealed partial class MainWindow
{
    VideoHost? _host => _active?.Playback.Host;
    MpvPlayer? _player => _active?.Playback.Player;
    PlayerState _playerState => _active?.Playback.State ?? PlayerState.Idle;
    bool _overlayAvailable, _updatingVolume;
    readonly PlayerOverlay _overlay;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _overlayTimer;

    void ShowPlayerOverlay()
    {
        if (_closing || !_overlayAvailable) return;
        _overlay.ShowOverlay();
        _overlayTimer.Stop();
        _overlayTimer.Start();
    }

    void HideIdleOverlay()
    {
        if (_overlay.IsPointerInside || _player?.Paused == true || _seekDragging ||
            _overlay.QualityButton.Flyout?.IsOpen == true || _overlay.SpeedButton.Flyout?.IsOpen == true) return;
        _overlay.HideOverlay();
        _overlayTimer.Stop();
    }

    void TrackFlyout(MenuFlyout menu)
    {
        menu.ShowMode = FlyoutShowMode.Transient;
        menu.Opened += (_, _) => ShowPlayerOverlay();
        menu.Closed += (_, _) => ShowPlayerOverlay();
    }

    void WirePlayerControls()
    {
        _overlay.VolumeSlider.Value = S.Volume;
        UpdateMuteIcon();
        _overlay.VolumeSlider.ValueChanged += (_, e) =>
        {
            if (_updatingVolume || _active is not { } tab) return;
            S.Volume = e.NewValue;
            tab.Playback.Volume = e.NewValue;
            SetTabMuted(tab, false);
            UpdateMuteIcon();
            ApplyTabAudio(tab);
            Settings.Save();
            ShowPlayerOverlay();
        };
        _overlay.MuteButton.Click += (_, _) => ToggleMute();
        _overlay.ReloadButton.Click += (_, _) => Reload();
        RetryButton.Click += (_, _) => Reload();
        _overlay.FullscreenButton.Click += (_, _) => ToggleFullscreen();
        _overlay.TheatreButton.Click += (_, _) => ToggleTheatre();
        _overlay.ChatButton.Click += (_, _) => ToggleChat();
        _overlay.Activity += ShowPlayerOverlay;
        _overlay.Wheel += delta => ChangeVolume(delta > 0 ? 5 : -5);
        _overlay.DoubleClicked += () =>
        {
            if (MultiviewActive && _active != null) ZoomMultiview(_multiviewZoom == _active ? null : _active);
            else ToggleFullscreen();
        };
        ChatOnlyButton.Click += (_, _) => SetChatOnly(true);
        ShowVideoButton.Click += (_, _) => SetChatOnly(false);
    }

    void ApplyTabAudio(ChannelTab tab)
    {
        tab.Playback.Player?.SetVolume(tab.Playback.Volume);
        tab.Playback.Player?.SetMuted(tab.Playback.Muted || S.ChatOnly);
        UpdateTabAudioButton(tab);
    }

    void UpdateVolumeControl()
    {
        _updatingVolume = true;
        _overlay.VolumeSlider.Value = _active?.Playback.Volume ?? S.Volume;
        _updatingVolume = false;
        UpdateMuteIcon();
    }

    void SetTabMuted(ChannelTab tab, bool muted)
    {
        tab.Playback.Muted = muted;
        S.MutedTabs.RemoveAll(login => login.Equals(tab.Login, StringComparison.OrdinalIgnoreCase));
        if (muted) S.MutedTabs.Add(tab.Login);
        Settings.Save();
    }

    void ToggleTabMute(ChannelTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        SetTabMuted(tab, !tab.Playback.Muted);
        ApplyTabAudio(tab);
        if (tab == _active)
        {
            UpdateMuteIcon();
            ShowPlayerOverlay();
        }
    }

    MpvPlayer? EnsurePlayer(ChannelTab tab)
    {
        var playback = tab.Playback;
        if (playback.Player != null)
        {
            ApplyTabAudio(tab);
            return playback.Player;
        }
        try
        {
            playback.Host = new VideoHost(_hwnd);
            playback.Host.Input += cmd => OnTabVideoInput(tab, cmd);
            playback.Player = new MpvPlayer(playback.Host.Hwnd, S.LowLatency);
            playback.Player.SetMuted(true);
            playback.Player.SetVolume(playback.Volume);
            playback.Player.StateChanged += s => _dispatcher.TryEnqueue(() =>
            {
                if (s != PlayerState.Idle && !playback.Resolving && playback.Player?.State == s) OnPlayerState(tab, s);
            });
            playback.Player.Input += cmd => _dispatcher.TryEnqueue(() => OnTabVideoInput(tab, cmd));
            ApplyTabAudio(tab);
            return playback.Player;
        }
        catch (Exception ex)
        {
            Log.Error("Player init", ex);
            playback.Host?.Dispose();
            playback.Host = null;
            playback.ErrorMessage = "The video player failed to start. Is libmpv-2.dll next to Glint.exe?";
            OnPlayerState(tab, PlayerState.Error);
            return null;
        }
    }

    void Reload()
    {
        if (_active is not { } tab) return;
        if (tab.Vod != null) _ = PlayVodAsync(tab, tab.Vod, _player?.TimePos ?? tab.VodPosition);
        else _ = PlayAsync(tab);
    }

    void StopPlayback()
    {
        _vodTimer.Stop();
        foreach (var tab in _tabs)
        {
            if (tab.Vod != null && tab.Playback.Player?.TimePos is { } pos) tab.VodPosition = pos;
            tab.Playback.Stop();
        }
        UpdateVideoBounds();
    }

    async Task PlayAsync(ChannelTab tab)
    {
        if (_closing || !_tabs.Contains(tab) || S.ChatOnly) return;
        var playback = tab.Playback;
        int version = ++playback.Version;
        playback.Started = true;
        playback.Resolving = true;
        playback.ErrorMessage = null;
        if (tab == _active && !MultiviewActive) _vodTimer.Stop();
        OnPlayerState(tab, PlayerState.Loading);

        List<StreamVariant> variants;
        try { variants = await TwitchGql.GetStreamVariantsAsync(tab.Login); }
        catch (Exception ex)
        {
            Log.Error("Stream variants", ex);
            variants = new();
        }
        if (_closing || !_tabs.Contains(tab) || version != playback.Version) return;
        playback.Resolving = false;

        playback.Variants = variants;
        playback.SelectedVariant = PickVariant(variants);
        if (tab == _active) SetQualityItems();
        if (variants.Count == 0)
        {
            playback.Ads?.Stop();
            playback.Player?.Stop();
            OnPlayerState(tab, PlayerState.Idle);
            return;
        }
        LoadLive(tab, playback.SelectedVariant!);
    }

    void LoadLive(ChannelTab tab, StreamVariant variant) => _ = LoadLiveSafelyAsync(tab, variant);

    async Task LoadLiveSafelyAsync(ChannelTab tab, StreamVariant variant)
    {
        try { await LoadLiveAsync(tab, variant); }
        catch (Exception ex)
        {
            if (_closing || !_tabs.Contains(tab)) return;
            Log.Error("Live playback", ex);
            tab.Playback.ErrorMessage = "Couldn't load the stream";
            OnPlayerState(tab, PlayerState.Error);
        }
    }

    async Task LoadLiveAsync(ChannelTab tab, StreamVariant variant)
    {
        var player = EnsurePlayer(tab);
        if (player == null) return;
        var playback = tab.Playback;
        int version = ++playback.Version;
        playback.Ads?.Stop();
        player.Stop();
        playback.AdStatus = AdSkipStatus.None;
        OnPlayerState(tab, PlayerState.Loading);
        if (S.BlockAds)
        {
            if (playback.Ads == null)
            {
                playback.Ads = new AdSkipper();
                playback.Ads.StatusChanged += () => _dispatcher.TryEnqueue(() =>
                {
                    if (_closing || !_tabs.Contains(tab)) return;
                    playback.AdStatus = playback.Ads.Status;
                    if (tab == _active) { UpdateInfoMeta(); RenderPlayerState(); }
                    foreach (var slot in _multiviewSlots.Where(s => s.Tab == tab)) UpdateMultiviewSlotStatus(slot);
                    UpdateVideoBounds();
                });
            }
            var url = await playback.Ads.StartAsync(tab.Login, variant);
            if (url == null || _closing || !_tabs.Contains(tab) || version != playback.Version) return;
            player.Load(url);
        }
        else
        {
            playback.Ads?.Stop();
            player.Load(variant.Url);
        }
        if (tab == _active) UpdateVideoBounds();
    }

    void SetQualityItems()
    {
        var tab = _active;
        var playback = tab?.Playback;
        var menu = new MenuFlyout();
        TrackFlyout(menu);
        foreach (var variant in playback?.Variants ?? new())
        {
            var item = new RadioMenuFlyoutItem { Text = variant.Name, GroupName = "Quality", IsChecked = variant == playback!.SelectedVariant };
            item.Click += (_, _) =>
            {
                if (tab == null || tab != _active) return;
                playback!.SelectedVariant = variant;
                _overlay.QualityText.Text = variant.Name;
                S.PreferredQuality = variant.IsSource ? "Source" : variant.Name;
                Settings.Save();
                if (tab.Vod != null) LoadVod(tab, variant, _player?.TimePos);
                else LoadLive(tab, variant);
            };
            menu.Items.Add(item);
        }
        _overlay.QualityButton.Flyout = menu;
        _overlay.QualityButton.IsEnabled = playback?.Variants.Count > 0;
        _overlay.QualityText.Text = playback?.SelectedVariant?.Name ?? "Quality";
    }

    static StreamVariant? PickVariant(List<StreamVariant> variants)
    {
        if (variants.Count == 0) return null;
        var pref = S.PreferredQuality;
        if (pref == "Source") return variants[0]; // list is sorted best first, so this also picks 1440p when there is one
        return variants.FirstOrDefault(v => v.Name.StartsWith(pref, StringComparison.OrdinalIgnoreCase))
               ?? variants.FirstOrDefault(v => v.IsSource) ?? variants[0];
    }

    void OnPlayerState(ChannelTab tab, PlayerState s)
    {
        if (_closing || !_tabs.Contains(tab)) return;
        tab.Playback.State = s;
        foreach (var slot in _multiviewSlots.Where(slot => slot.Tab == tab)) UpdateMultiviewSlotStatus(slot);
        if (MultiviewActive) UpdateVideoBounds();
        if (tab != _active) return;
        RenderPlayerState();
    }

    void RenderPlayerState()
    {
        if (_closing || _active is not { } tab) return;
        if (tab.Playback.AdStatus == AdSkipStatus.Waiting)
        {
            ShowVideoMessage("Waiting for stream after ads", null, retry: false, loading: true);
            UpdateInfoMeta();
            UpdateVideoBounds();
            return;
        }
        switch (_playerState)
        {
            case PlayerState.Idle:
                if (tab.Entry.Known) ShowVideoMessage($"{tab.Entry.DisplayName} is offline", tab.Entry.Avatar, retry: true, vods: true);
                else ShowVideoMessage("Couldn't load the stream", null, retry: true);
                break;
            case PlayerState.Loading:
                ShowVideoMessage(null, null, retry: false, loading: true);
                break;
            case PlayerState.Playing:
            case PlayerState.Buffering:
                VideoStatus.Visibility = Visibility.Collapsed;
                VideoSpinner.IsActive = false;
                break;
            case PlayerState.Ended when _active?.Vod != null:
                ShowVideoMessage("That's the end of the video", null, retry: false, vods: true);
                break;
            case PlayerState.Ended:
                if (_active != null) ShowVideoMessage($"{_active.Entry.DisplayName}'s stream ended", _active.Entry.Avatar, retry: true, vods: true);
                break;
            case PlayerState.Error:
                ShowVideoMessage(tab.Playback.ErrorMessage ?? "Couldn't play this", tab.Entry.Avatar, retry: true);
                break;
        }
        UpdateInfoMeta();
        UpdateVideoBounds();
        UpdateOverlayState();
    }

    void OnVideoInput(string cmd)
    {
        ShowPlayerOverlay();
        switch (cmd)
        {
            case "dblclick": ToggleFullscreen(); break;
            case "wheel 1": ChangeVolume(5); break;
            case "wheel -1": ChangeVolume(-5); break;
        }
    }

    void ChangeVolume(double delta) => _overlay.VolumeSlider.Value = Math.Clamp(_overlay.VolumeSlider.Value + delta, 0, 100);

    void ShowVideoMessage(string? text, ImageSource? avatar, bool retry, bool loading = false, bool vods = false)
    {
        VideoStatus.Visibility = Visibility.Visible;
        VideoSpinner.IsActive = loading;
        VideoSpinner.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        VideoStatusText.Text = text ?? "";
        VideoStatusText.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
        OfflineAvatarBrush.ImageSource = avatar;
        OfflineAvatar.Visibility = avatar != null ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Content = vods ? "Check again" : "Try again";
        OfflineVodsButton.Visibility = vods ? Visibility.Visible : Visibility.Collapsed;
    }

    // Keeps mpv's native window glued over VideoArea. Called on every layout pass; SetBounds no-ops if nothing moved.
    void UpdateVideoBounds()
    {
        if (_closing) return;
        if (MultiviewActive) { UpdateMultiviewVideoBounds(); return; }
        foreach (var tab in _tabs) if (tab != _active) tab.Playback.Host?.Hide();
        bool visible = _host != null && !IsIconic(_hwnd) && !_dialogOpen && !_vodPanelOpen && _videoPopups.Count == 0 && _active != null && _active.Playback.AdStatus != AdSkipStatus.Waiting && !S.ChatOnly
                       && MainArea.Visibility == Visibility.Visible
                       && _playerState is PlayerState.Playing or PlayerState.Buffering
                       && VideoArea.ActualWidth > 0 && Root.XamlRoot != null;
        if (!visible)
        {
            _host?.Hide();
            HidePlayerOverlay();
            return;
        }
        var scale = Root.XamlRoot!.RasterizationScale;
        var pt = VideoArea.TransformToVisual(Root).TransformPoint(new Point(0, 0));
        int x = (int)Math.Round(pt.X * scale), y = (int)Math.Round(pt.Y * scale);
        int width = (int)Math.Round(VideoArea.ActualWidth * scale), height = (int)Math.Round(VideoArea.ActualHeight * scale);
        _host!.SetBounds(x, y, width, height, true);
        PlacePlayerOverlay(VideoArea);
    }

    void HidePlayerOverlay()
    {
        _overlayAvailable = false;
        _overlayTimer.Stop();
        _overlay.QualityButton.Flyout?.Hide();
        _overlay.SpeedButton.Flyout?.Hide();
        _overlay.HideOverlay(immediately: true);
    }

    void PlacePlayerOverlay(FrameworkElement area)
    {
        var scale = Root.XamlRoot!.RasterizationScale;
        _overlay.ConfigureForWidth(area.ActualWidth);
        var point = area.TransformToVisual(Root).TransformPoint(new Point());
        int width = (int)Math.Round(area.ActualWidth * scale), height = (int)Math.Round(area.ActualHeight * scale);
        int overlayHeight = Math.Min(height, (int)Math.Round((96 + (_overlay.IsCompact ? 38 : 0) + (_active?.Vod != null ? 24 : 0) + (!MultiviewActive && (_fullscreen || S.Theatre) ? 44 : 0)) * scale));
        var screen = new NativePoint { X = (int)Math.Round(point.X * scale), Y = (int)Math.Round(point.Y * scale) + height - overlayHeight };
        if (!ClientToScreen(_hwnd, ref screen)) return;
        _overlay.Place(screen.X, screen.Y, width, overlayHeight);
        bool wasAvailable = _overlayAvailable;
        _overlayAvailable = true;
        if (!wasAvailable) ShowPlayerOverlay();
    }

    void ToggleMute()
    {
        if (_active is { } tab) ToggleTabMute(tab);
    }

    void TogglePause()
    {
        if (_player == null || _playerState is not (PlayerState.Playing or PlayerState.Buffering)) return;
        _player.SetPaused(!_player.Paused);
        UpdateOverlayState();
        ShowPlayerOverlay();
    }

    void UpdateMuteIcon()
    {
        var playback = _active?.Playback;
        _overlay.MuteButton.Content = playback == null || playback.Muted || playback.Volume == 0
            ? "\uE74F" : playback.Volume < 40 ? "\uE993" : "\uE767";
    }

    void ToggleFullscreen()
    {
        if (_active == null && !_fullscreen) return;
        _fullscreen = !_fullscreen;
        AppWindow.SetPresenter(_fullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        ApplyLayoutSettings();
        FocusMultiviewPlayer();
        ShowPlayerOverlay();
    }

    // T: from fullscreen it drops back into theatre rather than all the way out.
    void ToggleTheatre()
    {
        if (_fullscreen)
        {
            S.Theatre = true;
            ToggleFullscreen();
        }
        else
        {
            S.Theatre = !S.Theatre;
            ApplyLayoutSettings();
        }
        Settings.Save();
        FocusMultiviewPlayer();
        ShowPlayerOverlay();
    }

    void ToggleChat()
    {
        if (MultiviewActive && S.MultiviewChats == "Hidden")
        {
            S.MultiviewChats = "Each stream";
            _multiviewUpdating = true;
            MultiviewChatMode.SelectedItem = S.MultiviewChats;
            _multiviewUpdating = false;
            if (_fullscreen) S.FullscreenChat = true;
            else S.ChatHidden = false;
        }
        else if (_fullscreen) S.FullscreenChat = !S.FullscreenChat;
        else if (S.ChatOnly)
        {
            S.ChatHidden = true;
            SetChatOnly(false);
        }
        else S.ChatHidden = !S.ChatHidden;
        Settings.Save();
        ApplyLayoutSettings();
        FocusMultiviewPlayer();
        ShowPlayerOverlay();
    }

    void UpdateOverlayState()
    {
        if (_closing) return;
        var tab = _active;
        bool vod = tab?.Vod != null;
        bool paused = _player?.Paused == true;
        _overlay.PlayPauseButton.Content = paused ? "\uE768" : "\uE769";
        AutomationProperties.SetItemStatus(_overlay.PlayPauseButton, paused ? "Paused" : "Playing");
        ToolTipService.SetToolTip(_overlay.PlayPauseButton, paused ? "Play (Space)" : "Pause (Space)");
        _overlay.FullscreenButton.Content = _fullscreen ? "\uE73F" : "\uE740";
        _overlay.TheatreButton.Background = S.Theatre && !_fullscreen
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(64, 255, 255, 255)) : null;
        ToolTipService.SetToolTip(_overlay.TheatreButton, S.Theatre && !_fullscreen ? "Exit theatre (T)" : "Theatre mode (T)");
        AutomationProperties.SetName(_overlay.TheatreButton, S.Theatre && !_fullscreen ? "Exit theatre mode" : "Theatre mode");
        _overlay.ChatButton.Opacity = (MultiviewActive ? MultiviewChatShown : ChatShown) ? 1 : 0.7;
        _overlay.LiveBadge.Visibility = !vod && tab?.Entry.IsLive == true ? Visibility.Visible : Visibility.Collapsed;
        _overlay.GoLiveButton.Visibility = vod && tab?.Entry.IsLive == true ? Visibility.Visible : Visibility.Collapsed;
        _overlay.Seek.Visibility = _overlay.SpeedButton.Visibility = vod ? Visibility.Visible : Visibility.Collapsed;
        _overlay.TitlePanel.Visibility = !MultiviewActive && (_fullscreen || S.Theatre) ? Visibility.Visible : Visibility.Collapsed;
        _overlay.TitleText.Text = tab?.Entry.DisplayName ?? "";
        _overlay.SubtitleText.Text = tab?.Vod?.Title ?? tab?.Entry.Stream?.Title ?? "";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsIconic(IntPtr hwnd);

    void SetChatOnly(bool on)
    {
        SaveChatDraft();
        S.ChatOnly = on;
        Settings.Save();
        ApplyLayoutSettings();
        RestoreChatDraft();
        UpdateChatInput();
        if (on) StopPlayback();
        else Reload();
        UpdateVideoBounds();
    }
}
