using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Glint;

// When the channel you're watching raids someone: a notice in the controls, a line in chat, and (unless
// switched off) a short countdown before the tab moves to the raided channel, like twitch.tv.
public sealed partial class MainWindow
{
    sealed record RaidState(ChannelTab Tab, string Target, string TargetName, int Viewers, DateTime FollowAt);

    RaidState? _raid;
    readonly HashSet<string> _raidsSeen = new();
    DispatcherQueueTimer? _raidPoll, _raidClock;
    bool _raidChecking;

    void WireRaids()
    {
        // Twitch doesn't announce a raid in chat, so ask every so often while a live stream is on.
        _raidPoll = Timer(TimeSpan.FromSeconds(20), () => _ = CheckRaidAsync());
        _raidClock = Timer(TimeSpan.FromSeconds(1), UpdateRaidToast);
        _overlay.RaidFollowButton.Click += (_, _) => FollowRaid();
        _overlay.RaidStayButton.Click += (_, _) => DismissRaid();
        _raidPoll.Start();
    }

    async Task CheckRaidAsync()
    {
        if (_closing || _raidChecking || _raid != null || MultiviewActive) return;
        if (_active is not { Vod: null, Entry.IsLive: true } tab) return;
        _raidChecking = true;
        try
        {
            if (await TwitchGql.GetRaidAsync(tab.Login) is not { } raid || !_raidsSeen.Add(raid.Id) || _active != tab || _closing) return;
            // Without auto-follow the offer just stays up for a while.
            _raid = new(tab, raid.Target, raid.TargetName, raid.Viewers, DateTime.UtcNow.AddSeconds(S.FollowRaids ? 15 : 60));
            tab.Chat.AddSystem($"{tab.Entry.DisplayName} is raiding {raid.TargetName} with {raid.Viewers:N0} viewers");
            _overlay.RaidToast.Visibility = Visibility.Visible;
            UpdateRaidToast();
            UpdateVideoBounds(); // the controls grow to fit it
            _raidClock!.Start();
        }
        catch (Exception ex) { Log.Error("Raid check", ex); }
        finally { _raidChecking = false; }
    }

    void UpdateRaidToast()
    {
        if (_raid is not { } raid) { _raidClock?.Stop(); return; }
        if (_active != raid.Tab || !_tabs.Contains(raid.Tab)) { DismissRaid(); return; }
        int left = (int)Math.Ceiling((raid.FollowAt - DateTime.UtcNow).TotalSeconds);
        if (left <= 0)
        {
            if (S.FollowRaids) FollowRaid();
            else DismissRaid();
            return;
        }
        var text = $"{raid.Tab.Entry.DisplayName} is raiding {raid.TargetName} ({ChannelEntry.FormatCount(raid.Viewers)} viewers)";
        _overlay.RaidText.Text = S.FollowRaids ? $"{text}  ·  following in {left}s" : text;
        _overlay.RaidFollowButton.Content = S.FollowRaids ? "Follow now" : "Follow raid";
        ShowPlayerOverlay(); // keep the controls up while it's showing
    }

    void FollowRaid()
    {
        if (_raid is not { } raid) return;
        DismissRaid();
        if (_closing || !_tabs.Contains(raid.Tab)) return;
        Tabs.SelectedItem = raid.Tab.Item;
        OpenChannelInCurrentTab(raid.Target); // the raided channel takes this tab's place, as on twitch.tv
    }

    void DismissRaid()
    {
        _raid = null;
        _raidClock?.Stop();
        if (_overlay.RaidToast.Visibility != Visibility.Visible) return;
        _overlay.RaidToast.Visibility = Visibility.Collapsed;
        UpdateVideoBounds();
    }
}
