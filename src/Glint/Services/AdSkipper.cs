using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Glint;

// Serves mpv a cleaned copy of a Twitch live playlist from 127.0.0.1.
//
// Twitch stitches ads straight into the HLS stream; ad segments are titled "Amazon|..." instead of "live".
// When we see them we fetch the same stream with a different player type (embed / popout / autoplay),
// which are often ad-free, and splice those segments in by timestamp. If every copy has ads we just drop
// the ad segments and the picture holds until the break is over (same idea as streamlink's --twitch-disable-ads).
// Only the playlist goes through here; mpv fetches the video segments from Twitch's CDN directly.
public sealed class AdSkipper : IDisposable
{
    static readonly string[] BackupPlayerTypes = { "embed", "popout", "autoplay" };

    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();
    volatile Session? _session;

    // True when an ad break starts, false when it ends. Raised on a worker thread.
    public event Action<bool>? AdBreakChanged;

    public AdSkipper()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Start(string login, StreamVariant variant)
    {
        var s = new Session(this, login, variant);
        _session = s;
        return $"http://127.0.0.1:{Port}/{s.Key}.m3u8";
    }

    public void Stop() => _session = null;

    async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = HandleAsync(client);
        }
    }

    async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var request = await ReadHeadAsync(stream);
            var path = request.Split(' ').ElementAtOrDefault(1) ?? "";
            var s = _session;

            string body = "";
            var status = "404 Not Found";
            if (s != null && path.StartsWith("/" + s.Key))
            {
                body = await s.RenderAsync();
                status = "200 OK";
            }
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status}\r\nContent-Type: application/vnd.apple.mpegurl\r\nContent-Length: {bytes.Length}\r\n" +
                       "Cache-Control: no-cache\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(bytes);
        }
        catch (IOException) { } // mpv hung up (switched stream or quality)
        catch (Exception ex) { Log.Error("Ad skipper request", ex); }
    }

    static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var buf = new byte[8192];
        int total = 0;
        while (total < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(total));
            if (n == 0) break;
            total += n;
            if (Encoding.ASCII.GetString(buf, 0, total).Contains("\r\n\r\n")) break;
        }
        return Encoding.ASCII.GetString(buf, 0, total);
    }

    void RaiseAdBreak(bool on)
    {
        Log.Info(on ? "Ad break started" : "Ad break over");
        AdBreakChanged?.Invoke(on);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }

    record Segment(string Url, double Duration, DateTimeOffset? Time, bool IsAd, bool Discontinuity);

    record Playlist(List<Segment> Segments, int TargetDuration);

    sealed class Session
    {
        readonly AdSkipper _owner;
        readonly string _login;
        readonly StreamVariant _variant;
        readonly SemaphoreSlim _gate = new(1, 1);
        readonly List<(long Seq, Segment Seg, bool Disc)> _window = new();
        readonly HashSet<string> _seenUrls = new();
        long _nextSeq;
        int _discontinuitySeq;
        DateTimeOffset _lastTime = DateTimeOffset.MinValue;
        string? _lastSource;
        bool _inAdBreak;
        string? _backupUrl;
        DateTime _backupFetched;

        public string Key { get; } = Guid.NewGuid().ToString("N");

        public Session(AdSkipper owner, string login, StreamVariant variant)
        {
            _owner = owner;
            _login = login;
            _variant = variant;
        }

        public async Task<string> RenderAsync()
        {
            await _gate.WaitAsync();
            try
            {
                var main = await FetchAsync(_variant.Url);
                if (main == null) return Render(6);

                bool ads = main.Segments.Any(s => s.IsAd);
                var pick = main.Segments.Where(s => !s.IsAd).ToList();
                var source = "main";

                if (ads && await GetBackupAsync() is { } backup)
                {
                    pick = backup.Segments;
                    source = "backup";
                }
                else if (!ads) _backupUrl = null;

                // Nothing to show yet (e.g. a pre-roll with no clean copy): better to play the ad than fail to start.
                if (_window.Count == 0 && pick.Count == 0) pick = main.Segments;

                if (ads != _inAdBreak)
                {
                    _inAdBreak = ads;
                    _owner.RaiseAdBreak(ads);
                }

                foreach (var seg in pick)
                {
                    if (seg.Time is { } t ? t <= _lastTime : !_seenUrls.Add(seg.Url)) continue;
                    bool disc = seg.Discontinuity || (_lastSource != null && _lastSource != source);
                    _window.Add((_nextSeq++, seg, disc));
                    if (seg.Time is { } time) _lastTime = time;
                    _lastSource = source;
                }
                while (_window.Count > 15)
                {
                    if (_window[0].Disc) _discontinuitySeq++;
                    _window.RemoveAt(0);
                }
                if (_seenUrls.Count > 500) _seenUrls.Clear();
                return Render(main.TargetDuration);
            }
            catch (Exception ex)
            {
                Log.Error("Ad skipper playlist", ex);
                return Render(6);
            }
            finally { _gate.Release(); }
        }

        async Task<Playlist?> GetBackupAsync()
        {
            if (_backupUrl != null && DateTime.UtcNow - _backupFetched < TimeSpan.FromMinutes(2))
            {
                var cached = await FetchAsync(_backupUrl);
                if (cached != null && cached.Segments.Count > 0 && !cached.Segments.Any(s => s.IsAd)) return cached;
            }
            _backupUrl = null;
            foreach (var playerType in BackupPlayerTypes)
            {
                var variants = await TwitchGql.GetStreamVariantsAsync(_login, playerType);
                var match = variants.FirstOrDefault(v => v.Name == _variant.Name)
                            ?? variants.FirstOrDefault(v => v.Name != "Audio only");
                if (match == null) continue;
                var pl = await FetchAsync(match.Url);
                if (pl == null || pl.Segments.Count == 0 || pl.Segments.Any(s => s.IsAd)) continue;
                Log.Info($"Ad break: using the {playerType} stream ({match.Name})");
                _backupUrl = match.Url;
                _backupFetched = DateTime.UtcNow;
                return pl;
            }
            return null;
        }

        string Render(int targetDuration)
        {
            var sb = new StringBuilder();
            sb.Append("#EXTM3U\n#EXT-X-VERSION:3\n");
            sb.Append("#EXT-X-TARGETDURATION:").Append(Math.Max(1, targetDuration)).Append('\n');
            sb.Append("#EXT-X-MEDIA-SEQUENCE:").Append(_window.Count > 0 ? _window[0].Seq : _nextSeq).Append('\n');
            sb.Append("#EXT-X-DISCONTINUITY-SEQUENCE:").Append(_discontinuitySeq).Append('\n');
            foreach (var (_, seg, disc) in _window)
            {
                if (disc) sb.Append("#EXT-X-DISCONTINUITY\n");
                if (seg.Time is { } t) sb.Append("#EXT-X-PROGRAM-DATE-TIME:").Append(t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("#EXTINF:").Append(seg.Duration.ToString("0.000", CultureInfo.InvariantCulture)).Append(",live\n");
                sb.Append(seg.Url).Append('\n');
            }
            return sb.ToString();
        }

        static async Task<Playlist?> FetchAsync(string url)
        {
            try
            {
                var text = await Net.Http.GetStringAsync(url);
                var segments = new List<Segment>();
                int target = 6;
                double duration = 0;
                string title = "";
                DateTimeOffset? time = null;
                bool disc = false;
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("#EXT-X-TARGETDURATION:")) int.TryParse(line[22..], out target);
                    else if (line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:"))
                        time = DateTimeOffset.TryParse(line[25..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
                    else if (line == "#EXT-X-DISCONTINUITY") disc = true;
                    else if (line.StartsWith("#EXTINF:"))
                    {
                        var body = line[8..];
                        var comma = body.IndexOf(',');
                        double.TryParse(comma < 0 ? body : body[..comma], NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
                        title = comma < 0 ? "" : body[(comma + 1)..];
                    }
                    else if (line.Length > 0 && !line.StartsWith('#'))
                    {
                        bool isAd = title.Length > 0 && !title.StartsWith("live", StringComparison.OrdinalIgnoreCase);
                        segments.Add(new Segment(line, duration, time, isAd, disc));
                        time = null;
                        disc = false;
                        title = "";
                    }
                }
                return new Playlist(segments, target);
            }
            catch (Exception ex)
            {
                Log.Error("Playlist fetch", ex);
                return null;
            }
        }
    }
}
