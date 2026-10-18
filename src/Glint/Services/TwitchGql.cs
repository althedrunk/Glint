using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glint;

public sealed record StreamInfo(int Viewers, string Title, string? Game, DateTimeOffset StartedAt);

public sealed record UserInfo(string Id, string Login, string DisplayName, string? AvatarUrl, StreamInfo? Stream);

public sealed record VideoInfo(string Id, string Title, int LengthSeconds, DateTimeOffset CreatedAt, int Views, string? Thumbnail, string? Game);

public sealed record CommentFragment(string Text, string? EmoteId);

public sealed record VodComment(string Id, int Offset, string Login, string DisplayName, string? Color,
    List<CommentFragment> Fragments, List<string> Badges);

public sealed record StreamVariant(string Name, string Url, int Bandwidth, bool IsSource)
{
    public override string ToString() => Name;
}

// Twitch's public web GQL endpoint (same one the website and Streamlink use). No login needed for
// channel info, live status, badges and playback tokens.
public static class TwitchGql
{
    const string Endpoint = "https://gql.twitch.tv/gql";
    public const string WebClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    static async Task<JsonElement?> QueryAsync(string query, object? variables = null, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(new { query, variables }),
            };
            req.Headers.Add("Client-ID", WebClientId);
            using var resp = await Net.Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Error($"GQL HTTP {(int)resp.StatusCode}");
                return null;
            }
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.Obj("data").Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("GQL", ex);
            return null;
        }
    }

    public static async Task<List<UserInfo>> GetUsersAsync(IEnumerable<string> logins, CancellationToken ct = default)
    {
        var result = new List<UserInfo>();
        foreach (var chunk in logins.Select(l => l.ToLowerInvariant()).Distinct().Chunk(90))
        {
            const string q = """
                query($logins: [String!]) {
                  users(logins: $logins) {
                    id login displayName profileImageURL(width: 70)
                    stream { viewersCount title createdAt game { displayName } }
                  }
                }
                """;
            var data = await QueryAsync(q, new { logins = chunk }, ct);
            if (data is not { } d) continue;
            foreach (var u in d.Arr("users"))
            {
                if (u.ValueKind != JsonValueKind.Object) continue; // unknown logins come back as null
                result.Add(ParseUser(u));
            }
        }
        return result;
    }

    static UserInfo ParseUser(JsonElement u)
    {
        StreamInfo? stream = null;
        var s = u.Obj("stream");
        if (s.ValueKind == JsonValueKind.Object)
        {
            DateTimeOffset.TryParse(s.Str("createdAt"), out var started);
            stream = new StreamInfo(s.Int("viewersCount"), s.Str("title") ?? "", s.Obj("game").Str("displayName"), started);
        }
        var login = u.Str("login") ?? "";
        return new UserInfo(u.Str("id") ?? "", login, u.Str("displayName") ?? login, u.Str("profileImageURL"), stream);
    }

    // Badge image URLs keyed "setID/version". Pass null for global badges.
    public static async Task<Dictionary<string, (string Url, string Title)>> GetBadgesAsync(string? channelLogin, CancellationToken ct = default)
    {
        var map = new Dictionary<string, (string, string)>();
        JsonElement? data;
        IEnumerable<JsonElement> list;
        if (channelLogin == null)
        {
            data = await QueryAsync("{ badges { setID version title imageURL(size: QUADRUPLE) } }", null, ct);
            list = data?.Arr("badges") ?? Enumerable.Empty<JsonElement>();
        }
        else
        {
            data = await QueryAsync("query($l: String!) { user(login: $l) { broadcastBadges { setID version title imageURL(size: QUADRUPLE) } } }",
                new { l = channelLogin }, ct);
            list = data?.Obj("user").Arr("broadcastBadges") ?? Enumerable.Empty<JsonElement>();
        }
        foreach (var b in list)
        {
            if (b.ValueKind != JsonValueKind.Object) continue;
            var url = b.Str("imageURL");
            if (url != null) map[$"{b.Str("setID")}/{b.Str("version")}"] = (url, b.Str("title") ?? "");
        }
        return map;
    }

    public static async Task<List<VideoInfo>> GetVideosAsync(string login, CancellationToken ct = default)
    {
        const string q = """
            query($login: String!) {
              user(login: $login) {
                videos(first: 40, type: ARCHIVE, sort: TIME) {
                  edges { node { id title lengthSeconds createdAt viewCount previewThumbnailURL(width: 320, height: 180) game { displayName } } }
                }
              }
            }
            """;
        var data = await QueryAsync(q, new { login = login.ToLowerInvariant() }, ct);
        var list = new List<VideoInfo>();
        foreach (var edge in data?.Obj("user").Obj("videos").Arr("edges") ?? Enumerable.Empty<JsonElement>())
        {
            var n = edge.Obj("node");
            DateTimeOffset.TryParse(n.Str("createdAt"), out var created);
            var thumb = n.Str("previewThumbnailURL");
            if (thumb?.Contains("404_processing") == true) thumb = null; // still being processed (usually the current stream)
            list.Add(new VideoInfo(n.Str("id") ?? "", n.Str("title") ?? "", n.Int("lengthSeconds"), created, n.Int("viewCount"), thumb, n.Obj("game").Str("displayName")));
        }
        return list;
    }

    public static async Task<List<StreamVariant>> GetVodVariantsAsync(string videoId, CancellationToken ct = default)
    {
        const string q = """
            query($id: ID!) {
              videoPlaybackAccessToken(id: $id,
                params: { platform: "web", playerBackend: "mediaplayer", playerType: "site" }) { value signature }
            }
            """;
        var data = await QueryAsync(q, new { id = videoId }, ct);
        var tok = data?.Obj("videoPlaybackAccessToken") ?? default;
        if (tok.Str("value") is not { } value || tok.Str("signature") is not { } sig) return new();
        var url = $"https://usher.ttvnw.net/vod/{videoId}.m3u8?sig={sig}&token={Uri.EscapeDataString(value)}" +
                  "&allow_source=true&allow_audio_only=true&playlist_include_framerate=true&player_backend=mediaplayer";
        using var resp = await Net.Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return new();
        return ParseMasterPlaylist(await resp.Content.ReadAsStringAsync(ct));
    }

    // VOD chat from roughly offset seconds onwards (one page, usually a few seconds' worth).
    // Twitch rejects cursor paging from third-party clients, so callers page by offset instead.
    public static async Task<List<VodComment>> GetVodCommentsAsync(string videoId, int offset, CancellationToken ct = default)
    {
        const string q = """
            query($id: ID!, $offset: Int) {
              video(id: $id) {
                comments(contentOffsetSeconds: $offset) {
                  edges { node {
                    id contentOffsetSeconds
                    commenter { login displayName }
                    message { userColor fragments { text emote { emoteID } } userBadges { setID version } }
                  } }
                }
              }
            }
            """;
        var data = await QueryAsync(q, new { id = videoId, offset }, ct);
        var list = new List<VodComment>();
        foreach (var edge in data?.Obj("video").Obj("comments").Arr("edges") ?? Enumerable.Empty<JsonElement>())
        {
            var n = edge.Obj("node");
            var who = n.Obj("commenter");
            var msg = n.Obj("message");
            var frags = msg.Arr("fragments").Select(f => new CommentFragment(f.Str("text") ?? "", f.Obj("emote").Str("emoteID"))).ToList();
            var badges = msg.Arr("userBadges").Select(b => $"{b.Str("setID")}/{b.Str("version")}").ToList();
            var login = who.Str("login") ?? "";
            list.Add(new VodComment(n.Str("id") ?? "", n.Int("contentOffsetSeconds"), login, who.Str("displayName") ?? login, msg.Str("userColor"), frags, badges));
        }
        return list;
    }

    // Quality variants for a live channel, best first. Empty if offline.
    // playerType: "site" is the normal player. "embed"/"popout"/"autoplay" are what the ad skipper falls back to.
    public static async Task<List<StreamVariant>> GetStreamVariantsAsync(string login, string playerType = "site", CancellationToken ct = default)
    {
        const string q = """
            query($login: String!, $playerType: String!) {
              streamPlaybackAccessToken(channelName: $login,
                params: { platform: "web", playerBackend: "mediaplayer", playerType: $playerType }) { value signature }
            }
            """;
        var data = await QueryAsync(q, new { login = login.ToLowerInvariant(), playerType }, ct);
        var tok = data?.Obj("streamPlaybackAccessToken") ?? default;
        var value = tok.Str("value");
        var sig = tok.Str("signature");
        if (value == null || sig == null) return new();

        var url = $"https://usher.ttvnw.net/api/channel/hls/{Uri.EscapeDataString(login.ToLowerInvariant())}.m3u8" +
                  $"?sig={sig}&token={Uri.EscapeDataString(value)}&allow_source=true&allow_audio_only=true" +
                  $"&fast_bread=true&player_backend=mediaplayer&playlist_include_framerate=true" +
                  $"&p={Random.Shared.Next(100000, 999999)}";
        using var resp = await Net.Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return new(); // 404 = offline
        return ParseMasterPlaylist(await resp.Content.ReadAsStringAsync(ct));
    }

    static readonly Regex AttrRegex = new(@"([A-Z0-9\-]+)=(""[^""]*""|[^,]*)", RegexOptions.Compiled);

    static Dictionary<string, string> Attrs(string line)
    {
        var d = new Dictionary<string, string>();
        foreach (Match m in AttrRegex.Matches(line[(line.IndexOf(':') + 1)..]))
            d[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
        return d;
    }

    static List<StreamVariant> ParseMasterPlaylist(string text)
    {
        var names = new Dictionary<string, string>();
        var variants = new List<StreamVariant>();
        Dictionary<string, string>? pending = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-MEDIA:"))
            {
                var a = Attrs(line);
                if (a.TryGetValue("GROUP-ID", out var g) && a.TryGetValue("NAME", out var n)) names[g] = n;
            }
            else if (line.StartsWith("#EXT-X-STREAM-INF:"))
            {
                pending = Attrs(line);
            }
            else if (pending != null && line.Length > 0 && !line.StartsWith('#'))
            {
                var group = pending.GetValueOrDefault("VIDEO", "");
                var name = names.GetValueOrDefault(group, group);
                if (group == "audio_only") name = "Audio only";
                int.TryParse(pending.GetValueOrDefault("BANDWIDTH", "0"), out var bw);
                variants.Add(new StreamVariant(name, line, bw, group == "chunked"));
                pending = null;
            }
        }
        // Best first, audio-only last
        return variants
            .OrderBy(v => v.Name == "Audio only")
            .ThenByDescending(v => v.IsSource)
            .ThenByDescending(v => v.Bandwidth)
            .ToList();
    }
}
