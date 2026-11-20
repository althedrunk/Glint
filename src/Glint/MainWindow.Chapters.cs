using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glint;

// VOD chapters (where the game changes): ticks on the seek bar, the chapter under the pointer while
// hovering it, and a menu in the controls to jump straight to one.
public sealed partial class MainWindow
{
    VodChapter? _currentChapter;

    async Task LoadChaptersAsync(ChannelTab tab, VideoInfo video)
    {
        tab.Chapters = new();
        if (video.ClipSlug != null) return;
        List<VodChapter> chapters;
        try { chapters = await TwitchGql.GetChaptersAsync(video.Id); }
        catch (Exception ex) { Log.Error("Chapters", ex); return; }
        if (tab.Vod != video) return;
        tab.Chapters = chapters;
        if (tab != _active) return;
        DrawSeekMarks();
        SetChapterItems();
    }

    static VodChapter? ChapterAt(ChannelTab tab, double position) =>
        tab.Chapters.LastOrDefault(c => c.Start <= position) ?? tab.Chapters.FirstOrDefault();

    // Only worth a button when the VOD has more than one game in it.
    void SetChapterItems()
    {
        var tab = _active;
        var chapters = tab?.Vod != null ? tab.Chapters : new();
        _overlay.ChaptersButton.Visibility = chapters.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _currentChapter = null;
        if (chapters.Count < 2) return;
        var menu = new MenuFlyout();
        TrackFlyout(menu);
        foreach (var chapter in chapters)
        {
            var item = new RadioMenuFlyoutItem { Text = $"{chapter.Name}  ·  {VodItem.FormatTime(chapter.Start)}", GroupName = "Chapter", Tag = chapter };
            item.Click += (_, _) =>
            {
                if (_active != tab) return;
                _player?.Seek(chapter.Start);
                ShowPlayerOverlay();
            };
            menu.Items.Add(item);
        }
        SetOverlayMenu(_overlay.ChaptersButton, menu);
        UpdateCurrentChapter(tab!, tab!.VodPosition);
    }

    // Keeps the tick in the chapter menu on the one you're watching.
    void UpdateCurrentChapter(ChannelTab tab, double position)
    {
        if (tab.Chapters.Count < 2 || ChapterAt(tab, position) is not { } chapter || chapter == _currentChapter) return;
        _currentChapter = chapter;
        if (_overlayMenus.TryGetValue(_overlay.ChaptersButton, out var menu))
            foreach (var item in menu.Items.OfType<RadioMenuFlyoutItem>()) item.IsChecked = Equals(item.Tag, chapter);
        ToolTipService.SetToolTip(_overlay.ChaptersButton, $"Chapters: {chapter.Name}");
    }
}
