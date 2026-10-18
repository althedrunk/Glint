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
    VideoHost? _host;
    MpvPlayer? _player;
    AdSkipper? _ads;
    PlayerState _playerState = PlayerState.Idle;
    int _playVersion;
    bool _adBreak, _overlayAvailable;
    readonly PlayerOverlay _overlay;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _overlayTimer;
    List<StreamVariant> _variants = new();
    StreamVariant? _selectedVariant;

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
            S.Volume = e.NewValue;
            if (S.Muted) { S.Muted = false; _player?.SetMuted(false); }
            UpdateMuteIcon();
            _player?.SetVolume(e.NewValue);
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
        _overlay.DoubleClicked += ToggleFullscreen;
        ChatOnlyButton.Click += (_, _) => SetChatOnly(true);
        ShowVideoButton.Click += (_, _) => SetChatOnly(false);
    }

    MpvPlayer? EnsurePlayer()
    {
        if (_player != null) return _player;
        try
        {
            _host = new VideoHost(_hwnd);
            _host.Input += OnVideoInput;
            _player = new MpvPlayer(_host.Hwnd, S.LowLatency);
            _player.SetVolume(S.Volume);
            _player.SetMuted(S.Muted);
            _player.StateChanged += s => _dispatcher.TryEnqueue(() => OnPlayerState(s));
            _player.Input += cmd => _dispatcher.TryEnqueue(() => OnVideoInput(cmd));
            return _player;
        }
        catch (Exception ex)
        {
            Log.Error("Player init", ex);
            ShowVideoMessage("The video player failed to start. Is libmpv-2.dll next to Glint.exe?", null, retry: false);
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
        _playVersion++;
        _vodTimer.Stop();
        _ads?.Stop();
        _player?.Stop();
        _playerState = PlayerState.Idle;
        UpdateVideoBounds();
    }

    async Task PlayAsync(ChannelTab tab)
    {
        int version = ++_playVersion;
        _vodTimer.Stop();
        if (S.ChatOnly) { _player?.Stop(); return; }

        ShowVideoMessage(null, null, retry: false, loading: true);
        List<StreamVariant> variants;
        try { variants = await TwitchGql.GetStreamVariantsAsync(tab.Login); }
        catch (Exception ex)
        {
            Log.Error("Stream variants", ex);
            variants = new();
        }
        if (version != _playVersion || tab != _active) return;

        SetQualityItems(variants);
        if (variants.Count == 0)
        {
            _ads?.Stop();
            _player?.Stop();
            _playerState = PlayerState.Idle;
            if (tab.Entry.Known) ShowVideoMessage($"{tab.Entry.DisplayName} is offline", tab.Entry.Avatar, retry: true, vods: true);
            else ShowVideoMessage("Couldn't load the stream", null, retry: true);
            UpdateVideoBounds();
            return;
        }
        LoadLive(tab, _selectedVariant!);
    }

    void LoadLive(ChannelTab tab, StreamVariant variant)
    {
        var player = EnsurePlayer();
        if (player == null) return;
        _playerState = PlayerState.Loading;
        _adBreak = false;
        if (S.BlockAds)
        {
            if (_ads == null)
            {
                _ads = new AdSkipper();
                _ads.AdBreakChanged += on => _dispatcher.TryEnqueue(() => { _adBreak = on; UpdateInfoMeta(); });
            }
            player.Load(_ads.Start(tab.Login, variant));
        }
        else
        {
            _ads?.Stop();
            player.Load(variant.Url);
        }
        UpdateVideoBounds();
    }

    void SetQualityItems(List<StreamVariant> variants)
    {
        _variants = variants;
        _selectedVariant = PickVariant(variants);
        var menu = new MenuFlyout();
        TrackFlyout(menu);
        foreach (var variant in _variants)
        {
            var item = new RadioMenuFlyoutItem { Text = variant.Name, GroupName = "Quality", IsChecked = variant == _selectedVariant };
            item.Click += (_, _) =>
            {
                if (_active == null) return;
                _selectedVariant = variant;
                _overlay.QualityText.Text = variant.Name;
                S.PreferredQuality = variant.IsSource ? "Source" : variant.Name;
                Settings.Save();
                if (_active.Vod != null) LoadVod(variant, _player?.TimePos);
                else LoadLive(_active, variant);
            };
            menu.Items.Add(item);
        }
        _overlay.QualityButton.Flyout = menu;
        _overlay.QualityButton.IsEnabled = variants.Count > 0;
        _overlay.QualityText.Text = _selectedVariant?.Name ?? "Quality";
    }

    static StreamVariant? PickVariant(List<StreamVariant> variants)
    {
        if (variants.Count == 0) return null;
        var pref = S.PreferredQuality;
        if (pref == "Source") return variants[0]; // list is sorted best first, so this also picks 1440p when there is one
        return variants.FirstOrDefault(v => v.Name.StartsWith(pref, StringComparison.OrdinalIgnoreCase))
               ?? variants.FirstOrDefault(v => v.IsSource) ?? variants[0];
    }

    void OnPlayerState(PlayerState s)
    {
        if (_closing) return;
        _playerState = s;
        switch (s)
        {
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
                ShowVideoMessage("Couldn't play this", _active?.Entry.Avatar, retry: true);
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
        bool visible = _host != null && !IsIconic(_hwnd) && !_dialogOpen && !_vodPanelOpen && _active != null && !S.ChatOnly
                       && MainArea.Visibility == Visibility.Visible
                       && _playerState is PlayerState.Playing or PlayerState.Buffering
                       && VideoArea.ActualWidth > 0 && Root.XamlRoot != null;
        if (!visible)
        {
            _host?.SetBounds(0, 0, 0, 0, false);
            _overlayAvailable = false;
            _overlayTimer.Stop();
            _overlay.QualityButton.Flyout?.Hide();
            _overlay.SpeedButton.Flyout?.Hide();
            _overlay.HideOverlay(immediately: true);
            return;
        }
        var scale = Root.XamlRoot!.RasterizationScale;
        var pt = VideoArea.TransformToVisual(Root).TransformPoint(new Point(0, 0));
        int x = (int)Math.Round(pt.X * scale), y = (int)Math.Round(pt.Y * scale);
        int width = (int)Math.Round(VideoArea.ActualWidth * scale), height = (int)Math.Round(VideoArea.ActualHeight * scale);
        _host!.SetBounds(x, y, width, height, true);
        int overlayHeight = Math.Min(height, (int)Math.Round((96 + (_active?.Vod != null ? 24 : 0) + (_fullscreen || S.Theatre ? 44 : 0)) * scale));
        var screen = new NativePoint { X = x, Y = y + height - overlayHeight };
        if (!ClientToScreen(_hwnd, ref screen)) return;
        _overlay.Place(screen.X, screen.Y, width, overlayHeight);
        bool wasAvailable = _overlayAvailable;
        _overlayAvailable = true;
        if (!wasAvailable) ShowPlayerOverlay();
    }

    void ToggleMute()
    {
        S.Muted = !S.Muted;
        _player?.SetMuted(S.Muted);
        UpdateMuteIcon();
        Settings.Save();
        ShowPlayerOverlay();
    }

    void TogglePause()
    {
        if (_player == null || _playerState is not (PlayerState.Playing or PlayerState.Buffering)) return;
        _player.SetPaused(!_player.Paused);
        UpdateOverlayState();
        ShowPlayerOverlay();
    }

    void UpdateMuteIcon() =>
        _overlay.MuteButton.Content = S.Muted || S.Volume == 0 ? "" : S.Volume < 40 ? "" : "";

    void ToggleFullscreen()
    {
        if (_active == null && !_fullscreen) return;
        _fullscreen = !_fullscreen;
        AppWindow.SetPresenter(_fullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        ApplyLayoutSettings();
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
        ShowPlayerOverlay();
    }

    void ToggleChat()
    {
        if (_fullscreen) S.FullscreenChat = !S.FullscreenChat;
        else if (S.ChatOnly)
        {
            S.ChatHidden = true;
            SetChatOnly(false);
        }
        else S.ChatHidden = !S.ChatHidden;
        Settings.Save();
        ApplyLayoutSettings();
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
        _overlay.ChatButton.Opacity = ChatShown ? 1 : 0.7;
        _overlay.LiveBadge.Visibility = !vod && tab?.Entry.IsLive == true ? Visibility.Visible : Visibility.Collapsed;
        _overlay.GoLiveButton.Visibility = vod && tab?.Entry.IsLive == true ? Visibility.Visible : Visibility.Collapsed;
        _overlay.Seek.Visibility = _overlay.SpeedButton.Visibility = vod ? Visibility.Visible : Visibility.Collapsed;
        _overlay.TitlePanel.Visibility = _fullscreen || S.Theatre ? Visibility.Visible : Visibility.Collapsed;
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
        S.ChatOnly = on;
        Settings.Save();
        ApplyLayoutSettings();
        if (on) StopPlayback();
        else Reload();
        UpdateVideoBounds();
    }
}
