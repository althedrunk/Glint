using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Glint;

// "Continue watching" on the home page: VODs you stopped part way through, newest first.
public sealed partial class MainWindow
{
    const int HomeResumeCount = 6;
    readonly Dictionary<string, VideoInfo> _resumeVideos = new();
    DateTime _resumeChecked;
    bool _resumeChecking;

    void WireContinueWatching()
    {
        HomeResumeGrid.ItemClick += (_, e) => { if (e.ClickedItem is VodItem item) _ = ResumeVodAsync(item); };
        // Right-click, the Menu key or Shift+F10 all open the menu; Delete removes the card that has focus.
        HomeResumeGrid.ContextRequested += OnResumeContextRequested;
        HomeResumeGrid.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Delete && (e.OriginalSource as FrameworkElement)?.DataContext is VodItem item)
            {
                e.Handled = true;
                HideFromContinueWatching(item);
            }
        };
    }

    List<VodItem> ResumeItems()
    {
        List<VodProgress> saved;
        lock (S) saved = S.VodProgress.Where(p => p.Login.Length > 0 && !p.Hidden).Take(HomeResumeCount).ToList();
        return saved.Select(p =>
        {
            var video = _resumeVideos.GetValueOrDefault(p.Id)
                ?? new VideoInfo(p.Id, p.Title, (int)p.Length, default, 0, p.Thumbnail, null);
            return new VodItem(video, p.Login, p.Channel.Length > 0 ? p.Channel : p.Login) { Removed = HideFromContinueWatching };
        }).ToList();
    }

    // Only takes it off the home page; where you got to is kept for the next time you open it.
    void HideFromContinueWatching(VodItem item)
    {
        lock (S)
            foreach (var p in S.VodProgress.Where(p => p.Id == item.Video.Id)) p.Hidden = true;
        Settings.Save();
        _homeKey = null;
        UpdateHome();
    }

    // Brings titles and thumbnails up to date and drops VODs Twitch has since deleted. At most every few minutes.
    async Task RefreshResumeAsync()
    {
        if (_resumeChecking || DateTime.UtcNow - _resumeChecked < TimeSpan.FromMinutes(5)) return;
        List<string> ids;
        lock (S) ids = S.VodProgress.Where(p => p.Login.Length > 0 && !p.Hidden).Take(HomeResumeCount).Select(p => p.Id).ToList();
        if (ids.Count == 0) return;
        _resumeChecking = true;
        try
        {
            var found = await TwitchGql.GetVideosByIdAsync(ids);
            lock (S)
            {
                foreach (var id in ids)
                {
                    var saved = S.VodProgress.FirstOrDefault(p => p.Id == id);
                    if (saved == null) continue;
                    if (!found.TryGetValue(id, out var fresh)) { S.VodProgress.Remove(saved); continue; }
                    saved.Title = fresh.Video.Title;
                    saved.Thumbnail = fresh.Video.Thumbnail ?? saved.Thumbnail;
                    if (fresh.Channel.Length > 0) saved.Channel = fresh.Channel;
                    _resumeVideos[id] = fresh.Video;
                }
            }
            Settings.Save();
        }
        catch (Exception ex) { Log.Error("Continue watching", ex); }
        finally
        {
            _resumeChecking = false;
            _resumeChecked = DateTime.UtcNow; // a failed check waits its turn too, rather than retrying on every refresh
        }
        _homeKey = null;
        UpdateHome();
    }

    async Task ResumeVodAsync(VodItem item)
    {
        if (item.Login is not { } login) return;
        var video = item.Video;
        if (!_resumeVideos.ContainsKey(video.Id))
        {
            // Not refreshed yet: get its date, views and game for the info bar.
            try
            {
                if ((await TwitchGql.GetVideosByIdAsync(new[] { video.Id })).TryGetValue(video.Id, out var fresh))
                    video = _resumeVideos[video.Id] = fresh.Video;
            }
            catch (Exception ex) { Log.Error("Continue watching", ex); }
        }
        OpenChannelInCurrentTab(login);
        if (_tabs.FirstOrDefault(t => t.Login == login) is not { } tab) return;
        Tabs.SelectedItem = tab.Item;
        _ = PlayVodAsync(tab, video, SavedVodPosition(video));
    }

    void OnResumeContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement source || source.DataContext is not VodItem item) return;
        e.Handled = true;
        var menu = new MenuFlyout();
        var remove = new MenuFlyoutItem { Text = "Remove from Continue watching", Icon = new FontIcon { Glyph = "" },
            KeyboardAcceleratorTextOverride = "Delete" };
        remove.Click += (_, _) => HideFromContinueWatching(item);
        menu.Items.Add(remove);
        if (e.TryGetPosition(source, out var point)) menu.ShowAt(source, point);
        else menu.ShowAt(source);
    }
}
