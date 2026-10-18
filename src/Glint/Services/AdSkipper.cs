using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Glint;

public enum AdSkipStatus { None, AlternateStream, Waiting }

// Only the playlist goes through localhost; mpv fetches video from Twitch's CDN.
public sealed class AdSkipper : IDisposable
{
    static readonly string[] BackupPlayerTypes = { "embed", "popout", "autoplay" };

    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();
    volatile Session? _session;

    public event Action? StatusChanged;
    public AdSkipStatus Status => _session?.Status ?? AdSkipStatus.None;

    public AdSkipper()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async Task<string?> StartAsync(string login, StreamVariant variant)
    {
        var s = new Session(this, login, variant);
        _session?.Cancel();
        _session = s;
        try
        {
            await s.WaitForContentAsync();
            return _session == s ? $"http://127.0.0.1:{Port}/{s.Key}.m3u8" : null;
        }
        catch (OperationCanceledException) { return null; }
    }

    public void Stop()
    {
        var s = _session;
        _session = null;
        s?.Cancel();
    }

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
            if (s != null && path == "/" + s.Key + ".m3u8")
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
        catch (OperationCanceledException) { }
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

    void RaiseStatus(Session session, string login)
    {
        if (_session != session) return;
        Log.Info($"Ad skipping ({login}): {session.Status}");
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        Stop();
        _listener.Stop();
    }

    internal record Segment(string Url, double Duration, DateTimeOffset? Time, bool IsAd, bool Discontinuity,
        string? Map = null, string? Key = null, string? ByteRange = null);

    internal record Playlist(List<Segment> Segments, int TargetDuration);

    static readonly Regex Attribute = new("([A-Z0-9-]+)=(\"[^\"]*\"|[^,]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string ResolveTagUri(string line, Uri source) => Attribute.Replace(line, m =>
        m.Groups[1].Value.Equals("URI", StringComparison.OrdinalIgnoreCase)
            ? $"URI=\"{new Uri(source, m.Groups[2].Value.Trim('"')).AbsoluteUri}\"" : m.Value);

    internal static Playlist ParsePlaylist(string text, Uri source)
    {
        var segments = new List<Segment>();
        var ranges = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        int target = 6;
        double duration = 0;
        string title = "";
        DateTimeOffset? time = null;
        bool disc = false;
        string? map = null, key = null, byteRange = null;
        long sequence = 0;
        var rangeEnds = new Dictionary<string, long>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-TARGETDURATION:")) int.TryParse(line[22..], out target);
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:")) long.TryParse(line[22..], out sequence);
            else if (line.StartsWith("#EXT-X-MAP:")) map = ResolveTagUri(line, source);
            else if (line.StartsWith("#EXT-X-KEY:")) key = ResolveTagUri(line, source);
            else if (line.StartsWith("#EXT-X-BYTERANGE:")) byteRange = line[17..];
            else if (line.StartsWith("#EXT-X-DATERANGE:"))
            {
                var attributes = Attribute.Matches(line).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim('"'), StringComparer.OrdinalIgnoreCase);
                if (attributes.GetValueOrDefault("CLASS") != "twitch-stitched-ad" &&
                    attributes.GetValueOrDefault("ID")?.StartsWith("stitched-ad-", StringComparison.OrdinalIgnoreCase) != true) continue;
                if (!DateTimeOffset.TryParse(attributes.GetValueOrDefault("START-DATE"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start)) continue;
                DateTimeOffset end = DateTimeOffset.MaxValue;
                if (DateTimeOffset.TryParse(attributes.GetValueOrDefault("END-DATE"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ended)) end = ended;
                else if (double.TryParse(attributes.GetValueOrDefault("DURATION") ?? attributes.GetValueOrDefault("PLANNED-DURATION"),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0) end = start.AddSeconds(seconds);
                ranges.Add((start, end));
            }
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
            else if (line.Length > 0 && !line.StartsWith('#') && duration > 0)
            {
                bool ad = title.Contains("Amazon", StringComparison.OrdinalIgnoreCase);
                var url = new Uri(source, line).AbsoluteUri;
                var segmentKey = key;
                if (key?.Contains("METHOD=AES-128", StringComparison.OrdinalIgnoreCase) == true &&
                    !Attribute.Matches(key).Any(m => m.Groups[1].Value.Equals("IV", StringComparison.OrdinalIgnoreCase)))
                    segmentKey += ",IV=0x" + sequence.ToString("x32", CultureInfo.InvariantCulture);
                if (byteRange != null)
                {
                    var parts = byteRange.Split('@');
                    if (long.TryParse(parts[0], out var length))
                    {
                        long offset = parts.Length > 1 && long.TryParse(parts[1], out var explicitOffset) ? explicitOffset : rangeEnds.GetValueOrDefault(url);
                        byteRange = $"{length}@{offset}";
                        rangeEnds[url] = offset + length;
                    }
                }
                segments.Add(new Segment(url, duration, time, ad, disc, map, segmentKey, byteRange));
                sequence++;
                byteRange = null;
                if (time != null) time = time.Value.AddSeconds(duration);
                duration = 0;
                disc = false;
                title = "";
            }
        }
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (!segment.IsAd && segment.Time is { } start && ranges.Any(r => start < r.End && start.AddSeconds(segment.Duration) > r.Start))
                segments[i] = segment with { IsAd = true };
        }
        return new Playlist(segments, Math.Max(1, target));
    }

    sealed class Session
    {
        readonly AdSkipper _owner;
        readonly string _login;
        readonly StreamVariant _variant;
        readonly SemaphoreSlim _gate = new(1, 1);
        readonly CancellationTokenSource _stop;
        readonly List<(long Seq, Segment Seg, bool Disc)> _window = new();
        readonly HashSet<string> _seenUrls = new();
        long _nextSeq;
        int _discontinuitySeq;
        DateTimeOffset _lastTime = DateTimeOffset.MinValue;
        string? _lastSource;
        bool _pendingDiscontinuity;
        string? _backupUrl;
        DateTime _backupFetched;
        DateTime _nextBackupAttempt;
        public AdSkipStatus Status { get; private set; }

        public string Key { get; } = Guid.NewGuid().ToString("N");

        public Session(AdSkipper owner, string login, StreamVariant variant)
        {
            _owner = owner;
            _login = login;
            _variant = variant;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(owner._cts.Token);
        }

        public void Cancel() => _stop.Cancel();

        public async Task WaitForContentAsync()
        {
            int failures = 0;
            while (true)
            {
                _stop.Token.ThrowIfCancellationRequested();
                await RenderAsync();
                if (_window.Count > 0) return;
                if (Status == AdSkipStatus.None && ++failures >= 3) throw new InvalidOperationException("Couldn't load the stream playlist");
                await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token);
            }
        }

        public async Task<string> RenderAsync()
        {
            await _gate.WaitAsync(_stop.Token);
            try
            {
                var main = await FetchAsync(_variant.Url, _stop.Token);
                if (main == null) return Render(6);

                bool ads = main.Segments.Any(s => s.IsAd);
                var pick = main.Segments.Where(s => !s.IsAd).ToList();
                var source = _variant.Url;
                bool alternate = false;

                if (ads && await GetBackupAsync(main.Segments.Any(s => s.Map != null)) is { } backup)
                {
                    pick = backup.Segments.Where(s => !s.IsAd).ToList();
                    source = _backupUrl!;
                    alternate = true;
                }
                else if (!ads) _backupUrl = null;

                var status = !ads ? AdSkipStatus.None : alternate ? AdSkipStatus.AlternateStream : AdSkipStatus.Waiting;
                if (status != Status)
                {
                    Status = status;
                    _owner.RaiseStatus(this, _login);
                }
                if (ads && !alternate) _pendingDiscontinuity = true;

                foreach (var seg in pick)
                {
                    if (seg.Time is { } t ? t <= _lastTime : !_seenUrls.Add(seg.Url)) continue;
                    bool disc = seg.Discontinuity || _pendingDiscontinuity || (_lastSource != null && _lastSource != source);
                    _window.Add((_nextSeq++, seg, disc));
                    _pendingDiscontinuity = false;
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
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Error("Ad skipper playlist", ex);
                return Render(6);
            }
            finally { _gate.Release(); }
        }

        async Task<Playlist?> GetBackupAsync(bool fragmented)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            budget.CancelAfter(TimeSpan.FromSeconds(8));
            try { return await FindBackupAsync(fragmented, budget.Token); }
            catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
            {
                _nextBackupAttempt = DateTime.UtcNow.AddSeconds(8);
                return null;
            }
        }

        async Task<Playlist?> FindBackupAsync(bool fragmented, CancellationToken ct)
        {
            bool Usable(Playlist? playlist) => playlist != null && playlist.Segments.Count > 0 &&
                playlist.Segments.All(s => !s.IsAd && (s.Map != null) == fragmented);
            if (_backupUrl != null && DateTime.UtcNow - _backupFetched < TimeSpan.FromMinutes(2))
            {
                var cached = await FetchAsync(_backupUrl, ct);
                if (Usable(cached)) return cached;
            }
            _backupUrl = null;
            if (DateTime.UtcNow < _nextBackupAttempt) return null;
            foreach (var playerType in BackupPlayerTypes)
            {
                var variants = await TwitchGql.GetStreamVariantsAsync(_login, playerType, ct);
                var match = variants.Where(v => (v.Name == "Audio only") == (_variant.Name == "Audio only"))
                    .OrderBy(v => v.Codec != _variant.Codec)
                    .ThenBy(v => Math.Abs(v.Height - _variant.Height))
                    .ThenBy(v => v.Name != _variant.Name)
                    .ThenByDescending(v => v.Bandwidth).FirstOrDefault();
                if (match == null) continue;
                var pl = await FetchAsync(match.Url, ct);
                if (!Usable(pl)) continue;
                Log.Info($"Ad skipping ({_login}): using {playerType} ({match.Name})");
                _backupUrl = match.Url;
                _backupFetched = DateTime.UtcNow;
                _nextBackupAttempt = DateTime.MinValue;
                return pl;
            }
            _nextBackupAttempt = DateTime.UtcNow.AddSeconds(8);
            return null;
        }

        string Render(int targetDuration)
        {
            var sb = new StringBuilder();
            sb.Append("#EXTM3U\n#EXT-X-VERSION:").Append(_window.Any(s => s.Seg.Map != null || s.Seg.ByteRange != null) ? 6 : 3).Append('\n');
            int longest = _window.Count > 0 ? (int)Math.Ceiling(_window.Max(s => s.Seg.Duration)) : 1;
            sb.Append("#EXT-X-TARGETDURATION:").Append(Math.Max(longest, targetDuration)).Append('\n');
            sb.Append("#EXT-X-MEDIA-SEQUENCE:").Append(_window.Count > 0 ? _window[0].Seq : _nextSeq).Append('\n');
            sb.Append("#EXT-X-DISCONTINUITY-SEQUENCE:").Append(_discontinuitySeq).Append('\n');
            string? lastMap = null, lastKey = "";
            foreach (var (_, seg, disc) in _window)
            {
                if (disc) sb.Append("#EXT-X-DISCONTINUITY\n");
                if (seg.Key != lastKey) sb.Append(seg.Key ?? "#EXT-X-KEY:METHOD=NONE").Append('\n');
                if (seg.Map != null && seg.Map != lastMap) sb.Append(seg.Map).Append('\n');
                lastMap = seg.Map;
                lastKey = seg.Key;
                if (seg.Time is { } t) sb.Append("#EXT-X-PROGRAM-DATE-TIME:").Append(t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("#EXTINF:").Append(seg.Duration.ToString("0.000", CultureInfo.InvariantCulture)).Append(",live\n");
                if (seg.ByteRange != null) sb.Append("#EXT-X-BYTERANGE:").Append(seg.ByteRange).Append('\n');
                sb.Append(seg.Url).Append('\n');
            }
            return sb.ToString();
        }

        static async Task<Playlist?> FetchAsync(string url, CancellationToken ct)
        {
            try
            {
                return ParsePlaylist(await Net.Http.GetStringAsync(url, ct), new Uri(url));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error("Playlist fetch", ex);
                return null;
            }
        }
    }
}
