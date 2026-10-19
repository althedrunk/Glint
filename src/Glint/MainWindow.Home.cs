using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glint;

// The home page, shown in an empty tab: your live channels and the ones you watched recently.
public sealed partial class MainWindow
{
    const int HomeRecentCount = 12;
    string? _homeKey;

    void WireHome()
    {
        SidebarHomeButton.Click += (_, _) => ShowHome();
        HomeLiveGrid.ItemClick += (_, e) => { if (e.ClickedItem is StreamCard s) OpenChannelInCurrentTab(s.Stream.Login); };
        HomeRecentGrid.ItemClick += (_, e) => { if (e.ClickedItem is ChannelEntry c) OpenChannelInCurrentTab(c.Login); };
        HomeLiveGrid.SizeChanged += (_, _) => SizeStreamCards(HomeLiveGrid);
    }

    // Home lives in an empty tab, so reuse one if it's open.
    void ShowHome()
    {
        CloseBrowse();
        if (_active == null) { UpdateHome(); return; }
        if (Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => t.Tag is not ChannelTab) is { } empty) Tabs.SelectedItem = empty;
        else OpenNewTab();
    }

    void NoteRecent(string login)
    {
        S.RecentChannels.Remove(login);
        S.RecentChannels.Insert(0, login);
        if (S.RecentChannels.Count > 30) S.RecentChannels.RemoveRange(30, S.RecentChannels.Count - 30);
    }

    void UpdateHome()
    {
        if (EmptyState.Visibility != Visibility.Visible) return;
        var live = _entries.Values.Where(e => (e.IsFavorite || e.IsFollowed) && e.IsLive)
            .OrderByDescending(e => e.Stream!.Viewers).ToList();
        var shown = live.Select(e => e.Login).ToHashSet();
        var recent = S.RecentChannels.Take(HomeRecentCount).Select(GetEntry)
            .Where(e => e.Known && !shown.Contains(e.Login)).ToList();
        bool anyFollows = _entries.Values.Any(e => e.IsFavorite || e.IsFollowed);

        // Skip rebuilding (and reloading thumbnails) when nothing visible changed.
        var key = string.Join(",", live.Select(e => $"{e.Login}:{e.Stream!.Viewers}:{e.Stream.Title}")) + "|"
            + string.Join(",", recent.Select(e => $"{e.Login}:{e.IsLive}")) + "|" + anyFollows;
        if (key == _homeKey) return;
        _homeKey = key;

        HomeLiveGrid.ItemsSource = live.Select(e => new StreamCard(new BrowseStream(e.Login, e.DisplayName, e.AvatarUrl, e.Stream!.Title,
            e.Stream.Viewers, e.Stream.Game, $"https://static-cdn.jtvnw.net/previews-ttv/live_user_{e.Login}-440x248.jpg", true))).ToList();
        HomeRecentGrid.ItemsSource = recent;

        HomeLiveHeader.Visibility = anyFollows ? Visibility.Visible : Visibility.Collapsed;
        HomeLiveGrid.Visibility = live.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeLiveEmpty.Visibility = anyFollows && live.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeRecentHeader.Visibility = HomeRecentGrid.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // With nothing to list, keep the intro centred like before; otherwise it sits above the lists.
        bool content = anyFollows || recent.Count > 0;
        HomePanel.VerticalAlignment = content ? VerticalAlignment.Top : VerticalAlignment.Center;
        EmptyLogo.Width = EmptyLogo.Height = content ? 56 : 80;
        HomeSubtitle.Text = content ? "Pick a channel below, browse, or enter one." : "Choose a stream from the sidebar, browse, or enter a channel.";
    }

    // At launch, keep only the tabs whose channel is live. If Twitch is slow to answer, keep them all.
    async Task<List<string>> LiveOnlyAsync(List<string> logins)
    {
        var lookup = TwitchGql.GetUsersAsync(logins);
        if (await Task.WhenAny(lookup, Task.Delay(4000)) != lookup || !lookup.IsCompletedSuccessfully || lookup.Result.Count == 0)
            return logins;
        foreach (var info in lookup.Result) GetEntry(info.Login).Update(info);
        var live = lookup.Result.Where(i => i.Stream != null).Select(i => i.Login.ToLowerInvariant()).ToHashSet();
        return logins.Where(l => live.Contains(l.ToLowerInvariant())).ToList();
    }
}
