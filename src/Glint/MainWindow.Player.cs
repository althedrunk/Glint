using Glint.Player;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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
    bool _suppressQuality, _adBreak;

    void WirePlayerControls()
    {
        VolumeSlider.Value = S.Volume;
        UpdateMuteIcon();
        VolumeSlider.ValueChanged += (_, e) =>
        {
            S.Volume = e.NewValue;
            if (S.Muted) { S.Muted = false; _player?.SetMuted(false); }
            UpdateMuteIcon();
            _player?.SetVolume(e.NewValue);
            Settings.Save();
        };
        MuteButton.Click += (_, _) => ToggleMute();
        ReloadButton.Click += (_, _) => Reload();
        RetryButton.Click += (_, _) => Reload();
        FullscreenButton.Click += (_, _) => ToggleFullscreen();
        TheatreButton.Click += (_, _) => ToggleTheatre();
        ExitTheatreButton.Click += (_, _) => ToggleTheatre();
        HideChatButton.Click += (_, _) => ToggleFullscreenChat();
        ChatOnlyButton.Click += (_, _) => SetChatOnly(true);
        ShowVideoButton.Click += (_, _) => SetChatOnly(false);
        QualityBox.SelectionChanged += (_, _) =>
        {
            if (_suppressQuality || QualityBox.SelectedItem is not StreamVariant v || _active == null) return;
            S.PreferredQuality = v.IsSource ? "Source" : v.Name;
            Settings.Save();
            if (_active.Vod != null) LoadVod(v, _player?.TimePos);
            else LoadLive(_active, v);
        };
    }

    MpvPlayer? EnsurePlayer()
    {
        if (_player != null) return _player;
        try
        {
            _host = new VideoHost(_hwnd);
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
        LoadLive(tab, (StreamVariant)QualityBox.SelectedItem!);
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
        _suppressQuality = true;
        QualityBox.ItemsSource = variants;
        QualityBox.SelectedItem = PickVariant(variants);
        _suppressQuality = false;
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
    }

    void OnVideoInput(string cmd)
    {
        switch (cmd)
        {
            case "dblclick": ToggleFullscreen(); break;
            case "wheel 1": VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5); break;
            case "wheel -1": VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5); break;
        }
    }

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
        if (_host == null) return;
        bool visible = !_dialogOpen && !_vodPanelOpen && _active != null && !S.ChatOnly
                       && MainArea.Visibility == Visibility.Visible
                       && _playerState is PlayerState.Playing or PlayerState.Buffering
                       && VideoArea.ActualWidth > 0 && Root.XamlRoot != null;
        if (!visible)
        {
            _host.SetBounds(0, 0, 0, 0, false);
            return;
        }
        var scale = Root.XamlRoot!.RasterizationScale;
        var pt = VideoArea.TransformToVisual(Root).TransformPoint(new Point(0, 0));
        _host.SetBounds((int)Math.Round(pt.X * scale), (int)Math.Round(pt.Y * scale),
            (int)Math.Round(VideoArea.ActualWidth * scale), (int)Math.Round(VideoArea.ActualHeight * scale), true);
    }

    void ToggleMute()
    {
        S.Muted = !S.Muted;
        _player?.SetMuted(S.Muted);
        UpdateMuteIcon();
        Settings.Save();
    }

    void TogglePause()
    {
        if (_player == null || _playerState is not (PlayerState.Playing or PlayerState.Buffering)) return;
        _player.SetPaused(!_player.Paused);
    }

    void UpdateMuteIcon() =>
        MuteButton.Content = S.Muted || S.Volume == 0 ? "" : S.Volume < 40 ? "" : "";

    void ToggleFullscreen()
    {
        if (_active == null && !_fullscreen) return;
        _fullscreen = !_fullscreen;
        AppWindow.SetPresenter(_fullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        ApplyLayoutSettings();
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
    }

    void ToggleFullscreenChat()
    {
        if (!_fullscreen) return;
        S.FullscreenChat = !S.FullscreenChat;
        Settings.Save();
        ApplyLayoutSettings();
    }

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
