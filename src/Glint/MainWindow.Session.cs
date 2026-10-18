using Glint.Player;

namespace Glint;

// The optional twitch.tv session: reloading streams when it changes, and watch-time reporting.
public sealed partial class MainWindow
{
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _watchTimer;

    void WireSession()
    {
        _watchTimer = Timer(TimeSpan.FromMinutes(1), ReportWatchTime);
        _watchTimer.Start();
    }

    // Live streams you can actually see playing right now.
    IEnumerable<ChannelTab> WatchedTabs()
    {
        if (S.ChatOnly) return Enumerable.Empty<ChannelTab>();
        var candidates = MultiviewActive
            ? _multiviewSlots.Where(s => s.Tab != null && s.Root.Parent == MultiviewVideos).Select(s => s.Tab!)
            : _active != null ? new[] { _active } : Enumerable.Empty<ChannelTab>();
        return candidates.Where(t => t.Vod == null && t.Entry.IsLive && t.Entry.Id != null
                                     && t.Playback.State is PlayerState.Playing or PlayerState.Buffering).Distinct();
    }

    void ReportWatchTime()
    {
        if (!TwitchSession.IsConnected || !S.CountWatchTime) return;
        foreach (var tab in WatchedTabs()) _ = WatchTime.ReportAsync(tab.Login, tab.Entry.Id!);
    }

    // Connecting or disconnecting changes what Twitch serves (quality, ads), so reload live streams.
    internal void OnSessionChanged()
    {
        _ = ApplyAccountAsync();
        foreach (var tab in _tabs.Where(t => t.Vod == null && t.Playback.Started).ToList())
            _ = PlayAsync(tab);
    }
}
