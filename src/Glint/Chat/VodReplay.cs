namespace Glint.Chat;

// Feeds VOD chat into a replay channel in step with playback. Call Tick with the current
// position a few times a second (UI thread). Comments are fetched ~15s ahead and released as playback
// reaches them; a seek clears chat and backfills the 30s before the new position.
public sealed class VodReplay
{
    readonly string _videoId;
    readonly ChatChannel _chat;
    readonly List<VodComment> _pending = new();
    readonly HashSet<string> _seen = new();
    int _fetchFrom;
    bool _fetching;
    double _lastPos = -1;
    int _generation;

    public VodReplay(string videoId, ChatChannel chat)
    {
        _videoId = videoId;
        _chat = chat;
    }

    public void Tick(double pos)
    {
        if (_lastPos < 0 || pos < _lastPos - 2 || pos > _lastPos + 20) Reset(pos);
        _lastPos = pos;

        int released = 0;
        while (_pending.Count > 0 && _pending[0].Offset <= pos && released++ < 200)
        {
            _chat.AddComment(_pending[0]);
            _pending.RemoveAt(0);
        }
        if (!_fetching && _fetchFrom < pos + 15) _ = FetchAsync(_generation);
    }

    void Reset(double pos)
    {
        _generation++;
        _pending.Clear();
        _seen.Clear();
        _chat.Clear();
        _fetching = false;
        _fetchFrom = Math.Max(0, (int)pos - 30);
    }

    async Task FetchAsync(int generation)
    {
        _fetching = true;
        try
        {
            var page = await TwitchGql.GetVodCommentsAsync(_videoId, _fetchFrom);
            if (generation != _generation) return;

            int newest = _fetchFrom;
            foreach (var c in page)
            {
                if (_seen.Add(c.Id)) _pending.Add(c);
                newest = Math.Max(newest, c.Offset);
            }
            _pending.Sort((a, b) => a.Offset.CompareTo(b.Offset));

            // Page by offset. If a whole page landed in one second, step past it or we'd loop forever.
            if (page.Count == 0) _fetchFrom += 30;
            else _fetchFrom = newest > _fetchFrom ? newest : _fetchFrom + 1;
        }
        catch (Exception ex)
        {
            Log.Error("VOD chat", ex);
            if (generation == _generation) _fetchFrom += 5;
        }
        finally
        {
            if (generation == _generation) _fetching = false;
        }
    }
}
