using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace Glint;

// Clipping the live stream from the player.
public sealed partial class MainWindow
{
    DateTime _clipToastUntil;
    TwitchAuth.ClipResult? _lastClip;
    bool _clipping;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _clipToastTimer;

    void WireClips()
    {
        _overlay.ClipButton.Click += (_, _) => _ = CreateClipAsync();
        _overlay.ClipEditButton.Click += (_, _) => { if (_lastClip?.EditUrl is { } url) Launch(url); };
        _overlay.ClipCopyButton.Click += (_, _) =>
        {
            if (_lastClip?.Id == null) return;
            var data = new DataPackage();
            data.SetText(_lastClip.Url);
            Clipboard.SetContent(data);
            ShowClipToast("Link copied", busy: false, clip: _lastClip);
        };
        _clipToastTimer = _dispatcher.CreateTimer();
        _clipToastTimer.IsRepeating = false;
        _clipToastTimer.Tick += (_, _) =>
        {
            _overlay.ClipToast.Visibility = Visibility.Collapsed;
            UpdateVideoBounds();
        };
    }

    async Task CreateClipAsync()
    {
        if (_clipping || _active is not { } tab || tab.Vod != null || tab.Entry.Id is not { } broadcasterId) return;
        // Creating clips is only possible through Twitch's official API, so it needs the API login.
        if (S.AccessToken == null) { ShowClipToast("Turn on clips in Settings first", busy: false); return; }
        if (!TwitchAuth.CanClip) { ShowClipToast("Turn on clips again in Settings", busy: false); return; }

        _clipping = true;
        ShowClipToast("Clipping…", busy: true);
        try
        {
            var token = await TwitchAuth.EnsureTokenAsync();
            var result = token == null
                ? new TwitchAuth.ClipResult(null, null, "Log in to make clips")
                : await TwitchAuth.CreateClipAsync(broadcasterId, token);
            if (result.Error == null) _lastClip = result;
            ShowClipToast(result.Error ?? "Clip created", busy: false, clip: result.Error == null ? result : null);
        }
        finally { _clipping = false; }
    }

    void ShowClipToast(string text, bool busy, TwitchAuth.ClipResult? clip = null)
    {
        _overlay.ClipToastText.Text = text;
        _overlay.ClipSpinner.IsActive = busy;
        _overlay.ClipSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _overlay.ClipEditButton.Visibility = clip?.EditUrl != null ? Visibility.Visible : Visibility.Collapsed;
        _overlay.ClipCopyButton.Visibility = clip?.Id != null ? Visibility.Visible : Visibility.Collapsed;
        _overlay.ClipToast.Visibility = Visibility.Visible;

        var duration = TimeSpan.FromSeconds(busy ? 30 : clip != null ? 10 : 5);
        _clipToastUntil = DateTime.UtcNow + duration;
        _clipToastTimer!.Stop();
        _clipToastTimer.Interval = duration;
        _clipToastTimer.Start();
        UpdateVideoBounds(); // overlay grows to fit the notice
        ShowPlayerOverlay();
    }
}
