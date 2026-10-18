using System.Net;
using System.Net.Sockets;
using System.Text;
using Glint;

await Tests.RunAsync();

static class Tests
{
    const string Start = "2026-10-18T12:00:00Z";
    const string AdRange = "#EXT-X-DATERANGE:ID=\"stitched-ad-test\",CLASS=\"twitch-stitched-ad\",START-DATE=\"2026-10-18T12:00:02Z\",DURATION=2\n";

    public static async Task RunAsync()
    {
        ParseMarkers();
        ParseMediaMetadata();
        await StartupAndCancellation();
        await MidrollAndResume();
        await BackupAndQuality();
        await ReplacedSession();
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine($"PASS: {message}");
    }

    static string Playlist(int second, string name, string title = "live", double duration = 2) =>
        $"#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-PROGRAM-DATE-TIME:{DateTimeOffset.Parse(Start).AddSeconds(second):O}\n#EXTINF:{duration.ToString(System.Globalization.CultureInfo.InvariantCulture)},{title}\n{name}.ts\n";

    static void ParseMarkers()
    {
        var uri = new Uri("https://example.test/channel/index.m3u8");
        var text = Playlist(0, "before", "broadcast") + "#EXTINF:2,live\nad.ts\n#EXTINF:2,\nafter.ts\n" + AdRange;
        var playlist = AdSkipper.ParsePlaylist(text, uri);
        Check(playlist.Segments.Select(s => s.IsAd).SequenceEqual(new[] { false, true, false }), "Date ranges detect ads even with live or empty titles, including inferred segment times");
        Check(playlist.Segments[0].Url == "https://example.test/channel/before.ts", "Relative segment URLs resolve against the source playlist");
        Check(AdSkipper.ParsePlaylist(Playlist(0, "ad", "Amazon|123"), uri).Segments[0].IsAd, "Amazon segment titles are detected");
        Check(!AdSkipper.ParsePlaylist(Playlist(8, "clean") + AdRange, uri).Segments[0].IsAd, "Expired ad ranges do not mark later live segments");
        var endDate = AdRange.Replace("CLASS=\"twitch-stitched-ad\",", "").Replace("DURATION=2", "END-DATE=\"2026-10-18T12:00:04Z\"");
        Check(AdSkipper.ParsePlaylist(Playlist(2, "ad", "") + endDate, uri).Segments[0].IsAd, "Ad IDs and explicit end dates are recognised");
        var classOnly = AdRange.Replace("stitched-ad-test", "different-id").Replace("DURATION=2", "PLANNED-DURATION=2");
        Check(AdSkipper.ParsePlaylist(Playlist(1, "overlap", "") + classOnly, uri).Segments[0].IsAd, "Ad classes, planned duration and partially overlapping segments are recognised");
        var unrelated = AdRange.Replace("stitched-ad-test", "chapter").Replace("twitch-stitched-ad", "chapter");
        Check(!AdSkipper.ParsePlaylist(Playlist(2, "normal") + unrelated, uri).Segments[0].IsAd, "Unrelated date ranges are retained as normal content");
    }

    static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    static void ParseMediaMetadata()
    {
        var uri = new Uri("https://example.test/channel/index.m3u8");
        var text = "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:17\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n" +
            "#EXT-X-BYTERANGE:20@100\n#EXTINF:2,Amazon|123\nmedia.mp4\n#EXT-X-BYTERANGE:20\n#EXTINF:2,live\nmedia.mp4\n";
        var segments = AdSkipper.ParsePlaylist(text, uri).Segments;
        Check(segments[1].Map == "#EXT-X-MAP:URI=\"https://example.test/channel/init.mp4\"", "Fragmented MP4 initialization metadata survives filtering");
        Check(segments[1].Key!.Contains("https://example.test/channel/key.bin") && segments[1].Key!.EndsWith("IV=0x00000000000000000000000000000012"),
            "Encryption keys retain the original sequence IV when media is renumbered");
        Check(segments[1].ByteRange == "20@120", "Implicit byte-range offsets are made explicit before ads are removed");
    }

    static async Task StartupAndCancellation()
    {
        using var server = new PlaylistServer();
        server.Text = Playlist(0, "ad", "Amazon|123");
        TwitchGql.Variants = new();
        using var skipper = new AdSkipper();
        var start = skipper.StartAsync("test", server.Variant);
        await WaitUntil(() => skipper.Status == AdSkipStatus.Waiting);
        Check(!start.IsCompleted, "An all-ad preroll waits instead of loading ad segments into mpv");
        server.Text = Playlist(2, "clean");
        var url = await start.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await Net.Http.GetStringAsync(url);
        Check(result.Contains("clean.ts") && !result.Contains("/ad.ts"), "Playback starts with clean content after a preroll");
        Check(skipper.Status == AdSkipStatus.None, "Ad status clears when the broadcast returns");
        server.Text = Playlist(4, "ad", "Amazon|456");
        var cancelled = skipper.StartAsync("test", server.Variant);
        await WaitUntil(() => skipper.Status == AdSkipStatus.Waiting);
        skipper.Stop();
        Check(await cancelled.WaitAsync(TimeSpan.FromSeconds(1)) == null, "Stopping cancels a pending preroll promptly");
    }

    static async Task MidrollAndResume()
    {
        using var server = new PlaylistServer();
        TwitchGql.Variants = new();
        server.Text = Playlist(0, "before");
        using var skipper = new AdSkipper();
        var url = await skipper.StartAsync("test", server.Variant);
        server.Text = Playlist(2, "ad") + AdRange;
        var blocked = await Net.Http.GetStringAsync(url);
        Check(skipper.Status == AdSkipStatus.Waiting && !blocked.Contains("/ad.ts") && blocked.Contains("before.ts"), "A marked midroll is filtered while the previous clean playlist stays valid");
        server.Text = Playlist(4, "after");
        var resumed = await Net.Http.GetStringAsync(url);
        Check(resumed.Contains("after.ts") && resumed.Contains("#EXT-X-DISCONTINUITY\n"), "The broadcast resumes with a decoder discontinuity after filtered ads");
        var repeat = await Net.Http.GetStringAsync(url);
        Check(repeat == resumed, "Repeated playlist polls do not duplicate segments");
        Check(skipper.Status == AdSkipStatus.None, "Midroll status resets after recovery");
        server.Text = "#EXT-X-MAP:URI=\"init.mp4\"\n" + Playlist(6, "fragment");
        var fragmented = await Net.Http.GetStringAsync(url);
        Check(fragmented.Contains("#EXT-X-VERSION:6") && fragmented.Contains("/init.mp4\""), "Rendered HLS retains initialization metadata and the required playlist version");
    }

    static async Task BackupAndQuality()
    {
        using var main = new PlaylistServer();
        using var backup = new PlaylistServer();
        main.Text = Playlist(0, "ad", "Amazon|123");
        backup.Text = Playlist(0, "backup", duration: 6);
        TwitchGql.Variants = new()
        {
            new("Source", main.Variant.Url, 6000000, true, 1080),
            backup.Variant with { Name = "480p" },
        };
        using var skipper = new AdSkipper();
        var url = await skipper.StartAsync("test", main.Variant);
        var result = await Net.Http.GetStringAsync(url);
        Check(skipper.Status == AdSkipStatus.AlternateStream && result.Contains("backup.ts") && !result.Contains("/ad.ts"), "A clean backup at the requested resolution replaces the ad stream");
        Check(result.Contains("#EXT-X-TARGETDURATION:6"), "Target duration includes longer backup segments");
        backup.Text = Playlist(6, "backup-ad", "Amazon|456");
        var failedBackup = await Net.Http.GetStringAsync(url);
        Check(skipper.Status == AdSkipStatus.Waiting && !failedBackup.Contains("backup-ad.ts"), "A backup that starts serving ads is no longer used");
        main.Text = Playlist(8, "main-returned");
        var resumed = await Net.Http.GetStringAsync(url);
        Check(resumed.Contains("main-returned.ts") && resumed.Contains("#EXT-X-DISCONTINUITY\n"), "Switching from a backup to the main stream resets the decoder");
    }

    static async Task ReplacedSession()
    {
        using var ads = new PlaylistServer();
        using var clean = new PlaylistServer();
        TwitchGql.Variants = new();
        ads.Text = Playlist(0, "ad", "Amazon|123");
        clean.Text = Playlist(0, "live");
        using var skipper = new AdSkipper();
        var old = skipper.StartAsync("old", ads.Variant);
        await WaitUntil(() => skipper.Status == AdSkipStatus.Waiting);
        var current = await skipper.StartAsync("new", clean.Variant);
        Check(await old.WaitAsync(TimeSpan.FromSeconds(1)) == null && current != null && skipper.Status == AdSkipStatus.None,
            "Changing stream cancels the previous session without carrying its ad status over");
    }
}

sealed class PlaylistServer : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    public string Text = "";
    public StreamVariant Variant { get; }

    public PlaylistServer()
    {
        _listener.Start();
        Variant = new("480p", $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/index.m3u8", 1000000, false, 480);
        _ = Serve();
    }

    async Task Serve()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 }) { }
                var body = Encoding.UTF8.GetBytes(Text);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/vnd.apple.mpegurl\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                await stream.WriteAsync(body, _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose() { _stop.Cancel(); _listener.Stop(); }
}

namespace Glint
{
    public sealed record StreamVariant(string Name, string Url, int Bandwidth, bool IsSource, int Height = 0, string Codec = "H.264");
    static class TwitchGql
    {
        public static List<StreamVariant> Variants = new();
        public static Task<List<StreamVariant>> GetStreamVariantsAsync(string login, string playerType, CancellationToken ct) => Task.FromResult(Variants);
    }
    static class Log
    {
        public static void Info(string message) { }
        public static void Error(string message, Exception? ex = null) => Console.WriteLine($"{message}: {ex?.GetType().Name}");
    }
}
