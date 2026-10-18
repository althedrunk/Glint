using System.Globalization;
using Glint.Chat;
using Microsoft.UI.Xaml;
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
        Meta = string.Join("  ·  ", new[] { Ago(v.CreatedAt), $"{ChannelEntry.FormatCount(v.Views)} views", v.Game }.Where(s => s != null));
        if (v.Thumbnail != null) Thumbnail = new BitmapImage(new Uri(v.Thumbnail)) { DecodePixelWidth = 256 };
    }

    public VideoInfo Video { get; }
    public string Title { get; }
    public string Length { get; }
    public string Meta { get; }
    public ImageSource? Thumbnail { get; }

    public override string ToString() => Title; // what screen readers announce

    static string Ago(DateTimeOffset when)
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
    bool _vodPanelOpen, _seekDragging, _updatingSeek;

    void WireVodControls()
    {
        VideosButton.Click += async (_, _) => await OpenVodPanelAsync();
        OfflineVodsButton.Click += async (_, _) => await OpenVodPanelAsync();
        VodPanelClose.Click += (_, _) => CloseVodPanel();
        VodGrid.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is not VodItem item || _active == null) return;
            CloseVodPanel();
            _ = PlayVodAsync(_active, item.Video, 0);
        };
        GoLiveButton.Click += (_, _) => { if (_active != null) ExitVod(_active); };
        PlayPauseButton.Click += (_, _) => TogglePause();

        SpeedBox.ItemsSource = Speeds.Select(s => s.ToString("0.##", CultureInfo.InvariantCulture) + "x").ToList();
        SpeedBox.SelectedIndex = 2;
        SpeedBox.SelectionChanged += (_, _) =>
        {
            if (SpeedBox.SelectedIndex >= 0) _player?.SetSpeed(Speeds[SpeedBox.SelectedIndex]);
        };

        // The slider swallows pointer events, hence handledEventsToo. Seek on release, not on every pixel of a drag.
        VodSeek.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _seekDragging = true), true);
        VodSeek.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => EndSeekDrag()), true);
        VodSeek.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndSeekDrag()), true);
        VodSeek.ValueChanged += (_, e) =>
        {
            if (_updatingSeek) return;
            VodTime.Text = VodItem.FormatTime(e.NewValue);
            if (!_seekDragging) _player?.Seek(e.NewValue); // keyboard nudges on the slider
        };
    }

    void EndSeekDrag()
    {
        if (!_seekDragging) return;
        _seekDragging = false;
        _player?.Seek(VodSeek.Value);
    }

    void SeekBy(double seconds)
    {
        if (_player?.TimePos is { } pos) _player.Seek(pos + seconds);
    }

    async Task OpenVodPanelAsync()
    {
        if (_active is not { } tab) return;
        _vodPanelOpen = true;
        VodPanel.Visibility = Visibility.Visible;
        VodPanelTitle.Text = $"{tab.Entry.DisplayName}'s past broadcasts";
        UpdateVideoBounds();

        if (tab.Videos == null || DateTime.UtcNow - tab.VideosFetched > TimeSpan.FromMinutes(5))
        {
            VodGrid.ItemsSource = null;
            VodEmpty.Visibility = Visibility.Collapsed;
            VodSpinner.IsActive = true;
            try { tab.Videos = await TwitchGql.GetVideosAsync(tab.Login); }
            catch (Exception ex) { Log.Error("Videos", ex); tab.Videos = new(); }
            tab.VideosFetched = DateTime.UtcNow;
            VodSpinner.IsActive = false;
        }
        if (_active != tab || !_vodPanelOpen) return;
        VodGrid.ItemsSource = tab.Videos.Select(v => new VodItem(v)).ToList();
        VodEmpty.Visibility = tab.Videos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        int version = ++_playVersion;
        _ads?.Stop();
        _adBreak = false;

        if (tab.Vod?.Id != video.Id || tab.Replay == null)
        {
            tab.Replay = new ChatChannel(tab.Chat);
            tab.ReplayEngine = new VodReplay(video.Id, tab.Replay);
        }
        tab.Vod = video;
        tab.VodPosition = start;
        Chat.SetChannel(tab.Replay);
        UpdateInfoBar();
        UpdateModeUi();
        UpdateChatInput();
        ShowVideoMessage(null, null, retry: false, loading: true);

        List<StreamVariant> variants;
        try { variants = await TwitchGql.GetVodVariantsAsync(video.Id); }
        catch (Exception ex) { Log.Error("VOD variants", ex); variants = new(); }
        if (version != _playVersion || tab != _active) return;

        SetQualityItems(variants);
        if (variants.Count == 0)
        {
            _player?.Stop();
            _playerState = Player.PlayerState.Idle;
            ShowVideoMessage("Couldn't load this video. Subscriber-only VODs need you to be subbed.", null, retry: true);
            UpdateVideoBounds();
            return;
        }
        LoadVod((StreamVariant)QualityBox.SelectedItem!, start > 3 ? start : null);
        _vodTimer.Start();
    }

    void LoadVod(StreamVariant variant, double? start)
    {
        var player = EnsurePlayer();
        if (player == null) return;
        _playerState = Player.PlayerState.Loading;
        player.Load(variant.Url, start);
        if (SpeedBox.SelectedIndex >= 0) player.SetSpeed(Speeds[SpeedBox.SelectedIndex]);
        UpdateVideoBounds();
    }

    void ExitVod(ChannelTab tab)
    {
        tab.Vod = null;
        tab.Replay = null;
        tab.ReplayEngine = null;
        tab.VodPosition = 0;
        _vodTimer.Stop();
        Chat.SetChannel(tab.Chat);
        UpdateInfoBar();
        UpdateModeUi();
        UpdateChatInput();
        _ = PlayAsync(tab);
    }

    void VodTick()
    {
        if (_active?.Vod is not { } vod || _player == null) return;
        if (_player.TimePos is not { } pos) return;
        var duration = _player.Duration ?? vod.LengthSeconds;
        _active.VodPosition = pos;

        if (!_seekDragging)
        {
            _updatingSeek = true;
            VodSeek.Maximum = Math.Max(1, duration);
            VodSeek.Value = Math.Min(pos, VodSeek.Maximum);
            _updatingSeek = false;
            VodTime.Text = VodItem.FormatTime(pos);
        }
        VodDuration.Text = VodItem.FormatTime(duration);
        PlayPauseButton.Content = _player.Paused ? "" : "";
        _active.ReplayEngine?.Tick(pos);
    }

    // Shows/hides the bits that differ between live and VOD.
    void UpdateModeUi()
    {
        var tab = _active;
        bool vod = tab?.Vod != null;
        Transport.Visibility = vod && !_fullscreen ? Visibility.Visible : Visibility.Collapsed;
        GoLiveButton.Visibility = vod ? Visibility.Visible : Visibility.Collapsed;
        GoLiveButton.Content = tab?.Entry.IsLive == true ? "Back to live" : "Close video";
        VodBadge.Visibility = vod ? Visibility.Visible : Visibility.Collapsed;
    }
}
