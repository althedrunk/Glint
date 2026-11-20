using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Glint;

// Parts of a VOD Twitch muted for copyrighted audio: marked on the seek bar, counted down while you're in one,
// and skippable (or skipped automatically if you'd rather).
public sealed partial class MainWindow
{
    (double Start, double End)? _mutedRange;
    DateTime _mutedNoticeUntil;
    static readonly SolidColorBrush MutedBrush = new(Windows.UI.Color.FromArgb(255, 235, 64, 64));

    void WireMutedVod()
    {
        _overlay.MutedSkipButton.Click += (_, _) =>
        {
            if (_mutedRange is { } range) _player?.Seek(range.End + 0.5);
            ShowPlayerOverlay();
        };
        _overlay.Seek.SizeChanged += (_, _) => DrawSeekMarks();
        _overlay.Seek.RegisterPropertyChangedCallback(RangeBase.MaximumProperty, (_, _) => DrawSeekMarks());
    }

    async Task LoadMutedRangesAsync(ChannelTab tab, VideoInfo video)
    {
        tab.MutedRanges = new();
        if (video.ClipSlug != null) return;
        List<(double Start, double End)> ranges;
        try { ranges = await TwitchGql.GetMutedRangesAsync(video.Id); }
        catch (Exception ex) { Log.Error("Muted parts", ex); return; }
        if (tab.Vod != video) return;
        tab.MutedRanges = ranges;
        if (tab == _active) DrawSeekMarks();
    }

    static (double Start, double End)? MutedRangeAt(ChannelTab tab, double position)
    {
        foreach (var r in tab.MutedRanges)
            if (position >= r.Start && position < r.End) return r;
        return null;
    }

    void UpdateMutedNotice(ChannelTab tab, double position)
    {
        var range = MutedRangeAt(tab, position);
        bool entered = range != null && range != _mutedRange;
        _mutedRange = range;
        if (range is not { } r)
        {
            HideMutedNotice();
            return;
        }
        if (entered && S.SkipMutedVod)
        {
            _player?.Seek(r.End + 0.5);
            ShowMutedNotice($"Skipped {VodItem.FormatTime(r.End - position)} of muted audio", skippable: false);
            _mutedNoticeUntil = DateTime.UtcNow.AddSeconds(4);
            return;
        }
        if (S.SkipMutedVod) return; // the jump is still on its way
        // Kept small and out of the way: the controls don't pop up for it, since some people watch through these.
        var left = VodItem.FormatTime(r.End - position);
        ShowMutedNotice($"Muted  ·  back in {left}", skippable: true);
        MutedBadgeText.Text = $"MUTED {left}";
        MutedBadge.Visibility = Visibility.Visible;
    }

    void ShowMutedNotice(string text, bool skippable)
    {
        _overlay.MutedText.Text = text;
        _overlay.MutedSkipButton.Visibility = skippable ? Visibility.Visible : Visibility.Collapsed;
        _overlay.MutedNotice.Visibility = Visibility.Visible;
    }

    void HideMutedNotice()
    {
        MutedBadge.Visibility = Visibility.Collapsed;
        // An auto-skip message stays in the controls for a few seconds.
        if (_overlay.MutedSkipButton.Visibility != Visibility.Visible && DateTime.UtcNow < _mutedNoticeUntil) return;
        _overlay.MutedNotice.Visibility = Visibility.Collapsed;
    }

    static readonly SolidColorBrush ChapterBrush = new(Windows.UI.Color.FromArgb(220, 255, 255, 255));

    // On the seek bar: red where the sound is muted, a small white tick where each new chapter starts.
    void DrawSeekMarks()
    {
        var marks = _overlay.MutedMarks;
        marks.Children.Clear();
        if (_active is not { Vod: not null } tab || tab.MutedRanges.Count == 0 && tab.Chapters.Count < 2) return;
        double length = _overlay.Seek.Maximum > 1 ? _overlay.Seek.Maximum : tab.Vod.LengthSeconds;
        // The thumb travels the track minus its own width, so line the marks up with its centre.
        const double thumb = 18;
        double track = _overlay.Seek.ActualWidth - thumb;
        if (length <= 0 || track <= 0) return;
        double X(double seconds) => thumb / 2 + Math.Min(seconds, length) / length * track;
        foreach (var (start, end) in tab.MutedRanges)
        {
            var mark = new Rectangle { Width = Math.Max(2, X(end) - X(start)), Height = 4, Fill = MutedBrush, RadiusX = 2, RadiusY = 2 };
            Canvas.SetLeft(mark, X(start));
            marks.Children.Add(mark);
        }
        foreach (var chapter in tab.Chapters.Skip(1))
        {
            var tick = new Rectangle { Width = 2, Height = 10, Fill = ChapterBrush, RadiusX = 1, RadiusY = 1 };
            Canvas.SetLeft(tick, X(chapter.Start) - 1);
            Canvas.SetTop(tick, -3);
            marks.Children.Add(tick);
        }
    }
}
