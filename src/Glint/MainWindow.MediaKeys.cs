using Glint.Player;
using Windows.Media;
using Windows.Storage.Streams;

namespace Glint;

// Windows' own media controls: the keyboard's play/pause key works even when Glint isn't focused, and the
// stream shows up in the media panel by the volume flyout.
public sealed partial class MainWindow
{
    SystemMediaTransportControls? _media;
    string? _mediaKey;

    void WireMediaKeys()
    {
        try { _media = SystemMediaTransportControlsInterop.GetForWindow(_hwnd); }
        catch (Exception ex) { Log.Error("Media controls", ex); return; }
        _media.IsPlayEnabled = _media.IsPauseEnabled = true;
        _media.ButtonPressed += (_, e) => _dispatcher.TryEnqueue(() =>
        {
            bool paused = _player?.Paused == true;
            if (e.Button == SystemMediaTransportControlsButton.Play && paused || e.Button == SystemMediaTransportControlsButton.Pause && !paused)
                TogglePause();
        });
    }

    // Called with the overlay's state; only talks to Windows when something it shows has changed.
    void UpdateMediaInfo()
    {
        if (_media == null) return;
        var tab = _active;
        bool playing = tab != null && _playerState is PlayerState.Playing or PlayerState.Buffering;
        bool paused = _player?.Paused == true;
        var key = $"{tab?.Login}|{tab?.Vod?.Id}|{tab?.Entry.Stream?.Title}|{playing}|{paused}";
        if (key == _mediaKey) return;
        _mediaKey = key;
        try
        {
            _media.IsEnabled = playing;
            if (!playing) return;
            _media.PlaybackStatus = paused ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Playing;
            var display = _media.DisplayUpdater;
            display.ClearAll();
            display.Type = MediaPlaybackType.Video;
            display.VideoProperties.Title = tab!.Entry.DisplayName;
            display.VideoProperties.Subtitle = tab.Vod?.Title ?? tab.Entry.Stream?.Title ?? "";
            if (tab.Entry.AvatarUrl is { } avatar) display.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(avatar));
            display.Update();
        }
        catch (Exception ex) { Log.Error("Media controls", ex); }
    }
}
