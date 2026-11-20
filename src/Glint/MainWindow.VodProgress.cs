namespace Glint;

// Remembers how far into each VOD you got, so closing Glint (or the video) doesn't lose your place.
public sealed partial class MainWindow
{
    const double ResumeFrom = 30;   // nearer the start than this isn't worth resuming
    const double EndMargin = 60;    // the last minute counts as finished (outros, raids)
    const int MaxVodProgress = 300;

    internal static double SavedVodPosition(VideoInfo video)
    {
        if (video.ClipSlug != null) return 0;
        lock (S) return S.VodProgress.FirstOrDefault(p => p.Id == video.Id)?.Seconds ?? 0;
    }

    // Called every tick while a VOD plays, so it only writes when the position has moved on a bit.
    void RememberVodPosition(ChannelTab tab, bool now = false)
    {
        if (tab.Vod is not { ClipSlug: null } vod) return;
        double position = tab.VodPosition;
        double length = tab.Playback.Player?.Duration is > 0 and var d ? d : vod.LengthSeconds;
        if (position < ResumeFrom) return;
        lock (S)
        {
            var saved = S.VodProgress.FirstOrDefault(p => p.Id == vod.Id);
            if (length > 0 && position >= length - EndMargin)
            {
                if (saved == null) return;
                S.VodProgress.Remove(saved); // watched to the end, so next time starts from the top
            }
            else
            {
                if (!now && saved != null && Math.Abs(saved.Seconds - position) < 10) return;
                if (saved != null) S.VodProgress.Remove(saved);
                S.VodProgress.Insert(0, new VodProgress
                {
                    Id = vod.Id, Seconds = Math.Round(position), Length = Math.Round(length),
                    Login = tab.Login, Channel = tab.Entry.DisplayName, Title = vod.Title, Thumbnail = vod.Thumbnail ?? saved?.Thumbnail,
                    Hidden = saved?.Hidden == true && Math.Abs(saved.Seconds - position) < 10, // stays hidden unless watched on
                });
                if (S.VodProgress.Count > MaxVodProgress) S.VodProgress.RemoveRange(MaxVodProgress, S.VodProgress.Count - MaxVodProgress);
            }
        }
        Settings.Save();
    }
}
