using System.Globalization;

namespace Glint;

// How far a live stream's picture runs behind real time: the age of the newest segment in the HLS
// playlist (Twitch stamps each one with EXT-X-PROGRAM-DATE-TIME) plus what the player has buffered
// ahead of the frame it's showing. It's the same figure as Twitch's "latency to broadcaster".
public sealed class StreamLatency
{
    const int Window = 15; // about 30 seconds of samples at one every 2 s

    readonly List<double> _samples = new();
    readonly List<double> _offsets = new();

    public bool Busy { get; private set; }
    public TimeSpan? Value { get; private set; }

    // Enough samples for multiview sync to act on.
    public bool Settled => _samples.Count >= 6;

    // The player was paused for this long, which adds exactly that much delay to everything measured so far.
    public void Shift(double seconds)
    {
        for (int i = 0; i < _samples.Count; i++) _samples[i] += seconds;
        if (Value is { } v) Value = v + TimeSpan.FromSeconds(seconds);
    }

    // buffered is read right after the playlist arrives, so both halves describe the same moment.
    public async Task<TimeSpan?> SampleAsync(string playlistUrl, Func<double?> buffered)
    {
        if (Busy) return Value;
        Busy = true;
        try
        {
            var sent = DateTime.UtcNow;
            using var resp = await Net.Http.GetAsync(playlistUrl);
            if (!resp.IsSuccessStatusCode) return Value;
            var text = await resp.Content.ReadAsStringAsync();
            var received = DateTime.UtcNow;
            if (NewestSegmentEnd(text) is not { } edge || buffered() is not { } ahead) return Value;

            // Twitch's clock, not ours: the Date header is whole seconds, so add half a second on average.
            if (resp.Headers.Date is { } date)
                Add(_offsets, (date.UtcDateTime.AddSeconds(0.5) - (sent + (received - sent) / 2)).TotalSeconds);
            var now = received.AddSeconds(Median(_offsets) ?? 0);

            double latency = (now - edge).TotalSeconds + ahead;
            if (latency is < 0 or > 120) return Value;
            Add(_samples, latency);
            // A sample can only come out low: when the player hasn't fetched the newest segment yet it is up
            // to a segment short. So the higher samples are the true delay; take a high percentile, not the middle.
            Value = TimeSpan.FromSeconds(Percentile(_samples, 0.8)!.Value);
            return Value;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return Value; }
        finally { Busy = false; }
    }

    // End time of the last whole segment. Twitch's prefetch hints carry no timestamp and players skip them.
    static DateTime? NewestSegmentEnd(string playlist)
    {
        DateTime? start = null, end = null;
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:"))
                start = DateTime.TryParse(line[25..], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
            else if (line.StartsWith("#EXTINF:") && start is { } s &&
                     double.TryParse(line[8..].Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                end = s.AddSeconds(seconds);
                start = null;
            }
        }
        return end;
    }

    static void Add(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > Window) list.RemoveAt(0);
    }

    static double? Median(List<double> list) => Percentile(list, 0.5);

    static double? Percentile(List<double> list, double p)
    {
        if (list.Count == 0) return null;
        var sorted = list.OrderBy(x => x).ToList();
        return sorted[(int)Math.Round((sorted.Count - 1) * p)];
    }
}
