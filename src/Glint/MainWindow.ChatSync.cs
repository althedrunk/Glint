using Glint.Player;

namespace Glint;

// Twitch video runs a few seconds behind real time but chat arrives straight away, so each live
// tab's chat is held back by its stream's delay to land with what's on screen. The same measurement
// lines multiview streams up with each other.
public sealed partial class MainWindow
{
    readonly Dictionary<ChannelTab, StreamLatency> _latency = new();
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _latencyTimer;

    // Multiview sync state: streams paused right now, when each may next be nudged, and how many
    // checks in a row each has been ahead (one odd reading shouldn't pause anything).
    readonly HashSet<ChannelTab> _holding = new();
    readonly Dictionary<ChannelTab, DateTime> _syncCooldown = new();
    readonly Dictionary<ChannelTab, int> _aheadCount = new();

    void WireChatSync()
    {
        _latencyTimer = Timer(TimeSpan.FromSeconds(2), () => { SampleLatency(); SyncMultiview(); });
        _latencyTimer.Start();
        MultiviewSync.IsChecked = S.MultiviewSync;
        MultiviewSync.Click += (_, _) =>
        {
            S.MultiviewSync = MultiviewSync.IsChecked == true;
            Settings.Save();
        };
    }

    bool MeasuringLatency => S.SyncChatToVideo || S.MultiviewSync && MultiviewActive;

    void SampleLatency()
    {
        foreach (var gone in _latency.Keys.Where(t => !_tabs.Contains(t)).ToList()) _latency.Remove(gone);
        foreach (var tab in _tabs)
        {
            bool live = MeasuringLatency && !S.ChatOnly && tab.Vod == null && tab.Playback.SelectedVariant != null
                        && tab.Playback is { Player: not null, State: PlayerState.Playing or PlayerState.Buffering };
            if (!live)
            {
                tab.Chat.Delay = TimeSpan.Zero;
                _latency.Remove(tab);
                continue;
            }
            if (!S.SyncChatToVideo) tab.Chat.Delay = TimeSpan.Zero;
            // Buffering, paused or held for multiview sync: keep the last delay rather than measure a stalled player.
            if (tab.Playback.State == PlayerState.Buffering || tab.Playback.Player!.Paused || _holding.Contains(tab)) continue;
            if (!_latency.TryGetValue(tab, out var latency)) _latency[tab] = latency = new StreamLatency();
            if (!latency.Busy) _ = SampleLatencyAsync(tab, latency);
        }
    }

    async Task SampleLatencyAsync(ChannelTab tab, StreamLatency latency)
    {
        var player = tab.Playback.Player;
        var url = tab.Playback.SelectedVariant?.Url;
        if (player == null || url == null) return;
        var value = await latency.SampleAsync(url, () =>
            double.TryParse(player.Get("demuxer-cache-duration"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null);
        if (value is not { } delay || !S.SyncChatToVideo || _closing || !_tabs.Contains(tab) || tab.Playback.Player != player) return;
        // Small wobbles would just shuffle chat around, so only follow real changes.
        if (Math.Abs((delay - tab.Chat.Delay).TotalSeconds) >= 0.4) tab.Chat.Delay = delay;
    }

    // Multiview sync: every live stream runs a different distance behind real time, so the ones ahead
    // are paused just long enough to match the one furthest behind. Streamers playing together then
    // show the same moment, and each chat follows its own video.
    void SyncMultiview()
    {
        if (!S.MultiviewSync || !MultiviewActive) { _aheadCount.Clear(); return; }
        var now = DateTime.UtcNow;
        var live = _multiviewSlots.Select(s => s.Tab).OfType<ChannelTab>().Distinct()
            .Where(t => t.Vod == null && !_holding.Contains(t) && t.Playback is { State: PlayerState.Playing, Player: { Paused: false } }
                        && _latency.TryGetValue(t, out var l) && l.Settled && l.Value != null)
            .ToList();
        if (live.Count < 2) return;
        double target = live.Max(t => _latency[t].Value!.Value.TotalSeconds);
        foreach (var tab in live)
        {
            double gap = target - _latency[tab].Value!.Value.TotalSeconds;
            _aheadCount[tab] = gap >= 0.5 ? _aheadCount.GetValueOrDefault(tab) + 1 : 0;
            if (_aheadCount[tab] >= 3 && (!_syncCooldown.TryGetValue(tab, out var until) || now >= until))
                HoldBack(tab, gap);
        }
    }

    void HoldBack(ChannelTab tab, double seconds)
    {
        if (tab.Playback.Player is not { } player) return;
        _holding.Add(tab);
        _aheadCount[tab] = 0;
        _syncCooldown[tab] = DateTime.UtcNow + TimeSpan.FromSeconds(seconds + 8);
        player.SetPaused(true);
        // Pausing adds exactly this much delay, so carry the readings forward instead of starting over.
        _latency[tab].Shift(seconds);
        if (S.SyncChatToVideo) tab.Chat.Delay += TimeSpan.FromSeconds(seconds);
        var resume = _dispatcher.CreateTimer();
        resume.Interval = TimeSpan.FromSeconds(seconds);
        resume.IsRepeating = false;
        resume.Tick += (_, _) =>
        {
            resume.Stop();
            _holding.Remove(tab);
            if (!_closing && tab.Playback.Player == player) player.SetPaused(false);
        };
        resume.Start();
    }
}
