using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glint;

// The home page, shown in an empty tab: your live channels, VODs to pick back up, and channels you watched recently.
public sealed partial class MainWindow
{
    const int HomeRecentCount = 12;
    string? _homeKey;

    void WireHome()
    {
        SidebarHomeButton.Click += (_, _) => ShowHome();
        HomeLiveGrid.ItemClick += (_, e) => { if (e.ClickedItem is StreamCard s) OpenChannelInCurrentTab(s.Stream.Login); };
        HomeRecentGrid.ItemClick += (_, e) => { if (e.ClickedItem is ChannelEntry c) OpenChannelInCurrentTab(c.Login); };
        HomeLiveGrid.SizeChanged += (_, e) =>
        {
            SizeStreamCards(HomeLiveGrid);
            if (e.PreviousSize.Width != e.NewSize.Width && HomeLiveColumns() != _homeLiveColumns) { _homeKey = null; UpdateHome(); }
        };
        HomeLiveMore.Click += (_, _) => { _homeLiveExpanded = !_homeLiveExpanded; _homeKey = null; UpdateHome(); };
        HomeCustomise.Click += (_, _) => ShowSettings("Home");
        WireContinueWatching();
    }

    bool _homeLiveExpanded;
    int _homeLiveColumns;

    // How many live cards fit across, matching SizeStreamCards.
    int HomeLiveColumns()
    {
        const double gap = 12;
        double width = (HomeLiveGrid.ActualWidth > 0 ? HomeLiveGrid.ActualWidth : HomePanel.ActualWidth - 32) - 16;
        return Math.Max(1, (int)((width + gap) / (Appearance.CardMinWidth + gap)));
    }

    internal void ApplyHomePreferences()
    {
        _homeKey = null;
        UpdateHome();
    }

    // Sections in the chosen order, any switched off left out.
    void ArrangeHomeSections()
    {
        var sections = new Dictionary<string, FrameworkElement> { ["Continue"] = HomeResumeSection, ["Live"] = HomeLiveSection, ["Recent"] = HomeRecentSection };
        var order = S.HomeSections.Where(sections.ContainsKey).Concat(sections.Keys).Distinct().ToList();
        for (int i = 0; i < order.Count; i++)
        {
            var section = sections[order[i]];
            int at = HomePanel.Children.IndexOf(section);
            if (at != i + 1) // after the intro
            {
                HomePanel.Children.RemoveAt(at);
                HomePanel.Children.Insert(i + 1, section);
            }
        }
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
        var resume = ResumeItems();
        _ = RefreshResumeAsync();
        // Live now stops after the chosen number of rows unless you asked to see them all.
        int columns = _homeLiveColumns = HomeLiveColumns();
        int limit = S.HomeLiveRows > 0 ? S.HomeLiveRows * columns : int.MaxValue;
        bool more = live.Count > limit;

        // Skip rebuilding (and reloading thumbnails) when nothing visible changed.
        var key = string.Join(",", live.Select(e => $"{e.Login}:{e.Stream!.Viewers}:{e.Stream.Title}")) + "|"
            + string.Join(",", recent.Select(e => $"{e.Login}:{e.IsLive}")) + "|" + anyFollows + "|"
            + string.Join(",", resume.Select(r => $"{r.Video.Id}:{r.Length}")) + "|"
            + $"{limit}:{_homeLiveExpanded}:{string.Join(",", S.HomeSections)}:{string.Join(",", S.HomeHiddenSections)}";
        if (key == _homeKey) return;
        _homeKey = key;

        ArrangeHomeSections();
        HomeLiveGrid.ItemsSource = live.Take(more && !_homeLiveExpanded ? limit : live.Count).Select(e => new StreamCard(new BrowseStream(e.Login, e.DisplayName, e.AvatarUrl, e.Stream!.Title,
            e.Stream.Viewers, e.Stream.Game, $"https://static-cdn.jtvnw.net/previews-ttv/live_user_{e.Login}-440x248.jpg", true), e.Avatar)).ToList();
        HomeLiveMore.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
        HomeLiveMore.Content = _homeLiveExpanded ? "Show less" : $"Show all ({live.Count})";
        HomeRecentGrid.ItemsSource = recent;

        bool Shown(string section) => !S.HomeHiddenSections.Contains(section);
        HomeLiveSection.Visibility = anyFollows && Shown("Live") ? Visibility.Visible : Visibility.Collapsed;
        HomeResumeSection.Visibility = resume.Count > 0 && Shown("Continue") ? Visibility.Visible : Visibility.Collapsed;
        HomeRecentSection.Visibility = recent.Count > 0 && Shown("Recent") ? Visibility.Visible : Visibility.Collapsed;
        HomeLiveGrid.Visibility = live.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeLiveEmpty.Visibility = anyFollows && live.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeResumeGrid.ItemsSource = resume;

        bool content = HomeLiveSection.Visibility == Visibility.Visible || HomeResumeSection.Visibility == Visibility.Visible ||
            HomeRecentSection.Visibility == Visibility.Visible;
        HomePanel.VerticalAlignment = content ? VerticalAlignment.Top : VerticalAlignment.Center;
        var alignment = content ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        HomeIntro.HorizontalAlignment = HomeTitle.HorizontalAlignment = EmptyBrowseButton.HorizontalAlignment = alignment;
        HomeTitle.Text = content ? "Home" : "What are we watching?";
        EmptyLogo.Visibility = HomeSubtitle.Visibility = HomeShortcuts.Visibility = content ? Visibility.Collapsed : Visibility.Visible;
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
