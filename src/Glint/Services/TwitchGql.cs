using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glint;

public sealed record StreamInfo(int Viewers, string Title, string? Game, DateTimeOffset StartedAt);

public sealed record UserInfo(string Id, string Login, string DisplayName, string? AvatarUrl, StreamInfo? Stream);

public sealed record VideoInfo(string Id, string Title, int LengthSeconds, DateTimeOffset CreatedAt, int Views, string? Thumbnail, string? Game,
    string? ClipSlug = null, string? ClipBy = null, bool Recording = false);

public sealed record VodChapter(double Start, double Length, string Name);

public sealed record ChannelAbout(string? Description, int Followers, bool Partner, string? Banner, DateTimeOffset? LastLive, string? LastGame);

public sealed record CommentFragment(string Text, string? EmoteId);

public sealed record VodComment(string Id, int Offset, string Login, string DisplayName, string? Color,
    List<CommentFragment> Fragments, List<string> Badges);

public sealed record StreamVariant(string Name, string Url, int Bandwidth, bool IsSource, int Height = 0, string Codec = "H.264")
{
    public override string ToString() => Name;
}

// Twitch's public web GQL endpoint (same one the website and Streamlink use). No login needed for
// channel info, live status, badges and playback tokens.
public static partial class TwitchGql
{
    const string Endpoint = "https://gql.twitch.tv/gql";
    public const string WebClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    internal static Task<JsonElement?> RawQueryAsync(string query, object? variables = null) => QueryAsync(query, variables);

    // session: send the twitch.tv session if one is connected (playback tokens only).
    static async Task<JsonElement?> QueryAsync(string query, object? variables = null, CancellationToken ct = default, bool session = false)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(new { query, variables }),
            };
            req.Headers.Add("Client-ID", WebClientId);
            if (session) TwitchSession.Apply(req);
            using var resp = await Net.Http.SendAsync(req, ct);
            if (session && req.Headers.Authorization != null && resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Log.Error("twitch.tv session rejected for playback, using anonymous playback");
                return await QueryAsync(query, variables, ct, session: false);
            }
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

    public static async Task<ChannelAbout?> GetChannelAboutAsync(string login, CancellationToken ct = default)
    {
        const string q = """
            query($login: String!) {
              user(login: $login) {
                description followers { totalCount } roles { isPartner } bannerImageURL
                lastBroadcast { startedAt game { displayName } }
              }
            }
            """;
        var user = (await QueryAsync(q, new { login = login.ToLowerInvariant() }, ct))?.Obj("user") ?? default;
        if (user.ValueKind != JsonValueKind.Object) return null;
        var last = user.Obj("lastBroadcast");
        return new ChannelAbout(user.Str("description"), user.Obj("followers").Int("totalCount"), user.Obj("roles").Bool("isPartner"),
            user.Str("bannerImageURL"), DateTimeOffset.TryParse(last.Str("startedAt"), out var at) ? at : null, last.Obj("game").Str("displayName"));
    }

    // type is ARCHIVE (past broadcasts), HIGHLIGHT or UPLOAD.
    public static async Task<List<VideoInfo>> GetVideosAsync(string login, string type = "ARCHIVE", CancellationToken ct = default)
    {
        string q = """
            query($login: String!) {
              user(login: $login) {
                stream { previewImageURL(width: 320, height: 180) }
                videos(first: 40, type: TYPE, sort: TIME) {
                  edges { node { id title status lengthSeconds createdAt viewCount previewThumbnailURL(width: 320, height: 180) game { displayName } } }
                }
              }
            }
            """;
        var data = await QueryAsync(q.Replace("TYPE", type), new { login = login.ToLowerInvariant() }, ct);
        var user = data?.Obj("user") ?? default;
        var livePreview = user.Obj("stream").Str("previewImageURL");
        var list = new List<VideoInfo>();
        foreach (var edge in user.Obj("videos").Arr("edges"))
        {
            var n = edge.Obj("node");
            DateTimeOffset.TryParse(n.Str("createdAt"), out var created);
            bool recording = n.Str("status") == "RECORDING";
            var thumb = n.Str("previewThumbnailURL");
            // Twitch has no thumbnail until processing finishes; for the stream still going, show it live like the website does.
            if (thumb?.Contains("404_processing") == true) thumb = recording ? livePreview : null;
            list.Add(new VideoInfo(n.Str("id") ?? "", n.Str("title") ?? "", n.Int("lengthSeconds"), created, n.Int("viewCount"), thumb,
                n.Obj("game").Str("displayName"), Recording: recording));
        }
        return list;
    }

    // Current details for VODs you've part-watched, by id. A missing id means Twitch deleted that VOD.
    public static async Task<Dictionary<string, (VideoInfo Video, string Login, string Channel)>> GetVideosByIdAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var list = ids.Where(id => id.Length > 0 && id.All(char.IsAsciiDigit)).Distinct().Take(10).ToList();
        var result = new Dictionary<string, (VideoInfo, string, string)>();
        if (list.Count == 0) return result;
        const string fields = """
            id title status lengthSeconds createdAt viewCount previewThumbnailURL(width: 320, height: 180) game { displayName }
            owner { login displayName stream { previewImageURL(width: 320, height: 180) } }
            """;
        var q = "query { " + string.Join(" ", list.Select((id, i) => $"v{i}: video(id: \"{id}\") {{ {fields} }}")) + " }";
        var data = await QueryAsync(q, new { }, ct) ?? throw new HttpRequestException("No answer from Twitch");
        for (int i = 0; i < list.Count; i++)
        {
            var n = data.Obj($"v{i}");
            if (n.ValueKind != JsonValueKind.Object || n.Str("id") is not { } id) continue;
            DateTimeOffset.TryParse(n.Str("createdAt"), out var created);
            bool recording = n.Str("status") == "RECORDING";
            var owner = n.Obj("owner");
            var thumb = n.Str("previewThumbnailURL");
            if (thumb?.Contains("404_processing") == true) thumb = recording ? owner.Obj("stream").Str("previewImageURL") : null;
            var video = new VideoInfo(id, n.Str("title") ?? "", n.Int("lengthSeconds"), created, n.Int("viewCount"), thumb,
                n.Obj("game").Str("displayName"), Recording: recording);
            result[id] = (video, owner.Str("login") ?? "", owner.Str("displayName") ?? "");
        }
        return result;
    }

    // A raid the channel has started and not yet sent, or null.
    public static async Task<(string Id, string Target, string TargetName, int Viewers)?> GetRaidAsync(string login, CancellationToken ct = default)
    {
#if DEBUG
        // Raids are hard to catch live, so a debug build can pretend one is under way.
        if (Environment.GetEnvironmentVariable("GLINT_TEST_RAID") is { Length: > 0 } fake) return ("test-" + login, fake, fake, 1234);
#endif
        const string q = """
            query($login: String!) {
              user(login: $login) { raid { id viewerCount targetChannel { login displayName } } }
            }
            """;
        var data = await QueryAsync(q, new { login = login.ToLowerInvariant() }, ct);
        var raid = data?.Obj("user").Obj("raid") ?? default;
        if (raid.ValueKind != JsonValueKind.Object || raid.Str("id") is not { } id || raid.Obj("targetChannel").Str("login") is not { } target) return null;
        return (id, target, raid.Obj("targetChannel").Str("displayName") ?? target, raid.Int("viewerCount"));
    }

    // Where the game changes in a VOD, in order. A VOD that stays on one game has a single chapter.
    public static async Task<List<VodChapter>> GetChaptersAsync(string videoId, CancellationToken ct = default)
    {
        const string q = """
            query($id: ID!) {
              video(id: $id) {
                moments(first: 100, momentRequestType: VIDEO_CHAPTER_MARKERS) {
                  edges { node { positionMilliseconds durationMilliseconds description details { ... on GameChangeMomentDetails { game { displayName } } } } }
                }
              }
            }
            """;
        var data = await QueryAsync(q, new { id = videoId }, ct);
        return (data?.Obj("video").Obj("moments").Arr("edges") ?? Enumerable.Empty<JsonElement>())
            .Select(e => e.Obj("node"))
            .Select(n => new VodChapter(n.Int("positionMilliseconds") / 1000.0, n.Int("durationMilliseconds") / 1000.0,
                n.Obj("details").Obj("game").Str("displayName") ?? n.Str("description") ?? "Chapter"))
            .OrderBy(c => c.Start)
            .ToList();
    }

    // Stretches of a VOD whose audio Twitch muted for copyrighted music, joined where they run into each other.
    public static async Task<List<(double Start, double End)>> GetMutedRangesAsync(string videoId, CancellationToken ct = default)
    {
        const string q = """
            query($id: ID!) {
              video(id: $id) { muteInfo { mutedSegmentConnection { nodes { offset duration } } } }
            }
            """;
        var data = await QueryAsync(q, new { id = videoId }, ct);
        var segments = (data?.Obj("video").Obj("muteInfo").Obj("mutedSegmentConnection").Arr("nodes") ?? Enumerable.Empty<JsonElement>())
            .Select(n => (Start: (double)n.Int("offset"), End: (double)n.Int("offset") + n.Int("duration")))
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start);
        var ranges = new List<(double Start, double End)>();
        foreach (var s in segments)
        {
            if (ranges.Count > 0 && s.Start <= ranges[^1].End + 1) ranges[^1] = (ranges[^1].Start, Math.Max(ranges[^1].End, s.End));
            else ranges.Add(s);
        }
        return ranges;
    }

    // period is LAST_DAY, LAST_WEEK, LAST_MONTH or ALL_TIME; most viewed first.
    public static async Task<List<VideoInfo>> GetClipsAsync(string login, string period, CancellationToken ct = default)
    {
        string q = """
            query($login: String!) {
              user(login: $login) {
                clips(first: 40, criteria: { period: PERIOD, sort: VIEWS_DESC }) {
                  edges { node { slug title viewCount durationSeconds createdAt thumbnailURL(width: 320, height: 180) curator { displayName } game { displayName } } }
                }
              }
            }
            """;
        var data = await QueryAsync(q.Replace("PERIOD", period), new { login = login.ToLowerInvariant() }, ct);
        var list = new List<VideoInfo>();
        foreach (var edge in data?.Obj("user").Obj("clips").Arr("edges") ?? Enumerable.Empty<JsonElement>())
        {
            var n = edge.Obj("node");
            if (n.Str("slug") is not { } slug) continue;
            DateTimeOffset.TryParse(n.Str("createdAt"), out var created);
            list.Add(new VideoInfo("clip:" + slug, n.Str("title") ?? "", n.Int("durationSeconds"), created, n.Int("viewCount"),
                n.Str("thumbnailURL"), n.Obj("game").Str("displayName"), slug, n.Obj("curator").Str("displayName")));
        }
        return list;
    }

    // Clips are plain MP4 files; the access token just has to ride along on the URL.
    public static async Task<List<StreamVariant>> GetClipVariantsAsync(string slug, CancellationToken ct = default)
    {
        const string q = """
            query($slug: ID!) {
              clip(slug: $slug) {
                videoQualities { quality frameRate sourceURL }
                playbackAccessToken(params: { platform: "web", playerBackend: "mediaplayer", playerType: "site" }) { signature value }
              }
            }
            """;
        var clip = (await QueryAsync(q, new { slug }, ct))?.Obj("clip") ?? default;
        var token = clip.Obj("playbackAccessToken");
        if (token.Str("signature") is not { } sig || token.Str("value") is not { } value) return new();
        var variants = new List<StreamVariant>();
        foreach (var quality in clip.Arr("videoQualities"))
        {
            if (quality.Str("sourceURL") is not { } url || !int.TryParse(quality.Str("quality"), out var height)) continue;
            double fps = quality.TryGetProperty("frameRate", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 30;
            var name = fps >= 50 ? $"{height}p60" : $"{height}p";
            variants.Add(new StreamVariant(name, $"{url}?sig={sig}&token={Uri.EscapeDataString(value)}", 0, variants.Count == 0, height));
        }
        return variants.OrderByDescending(v => v.Height).ToList();
    }

    public static async Task<List<StreamVariant>> GetVodVariantsAsync(string videoId, CancellationToken ct = default)
    {
        const string q = """
            query($id: ID!) {
              videoPlaybackAccessToken(id: $id,
                params: { platform: "web", playerBackend: "mediaplayer", playerType: "site" }) { value signature }
            }
            """;
        var data = await QueryAsync(q, new { id = videoId }, ct, session: true);
        var tok = data?.Obj("videoPlaybackAccessToken") ?? default;
        if (tok.Str("value") is not { } value || tok.Str("signature") is not { } sig) return new();
        var url = $"https://usher.ttvnw.net/vod/{videoId}.m3u8?sig={sig}&token={Uri.EscapeDataString(value)}" +
                  "&allow_source=true&allow_audio_only=true&playlist_include_framerate=true&player_backend=mediaplayer" +
                  "&supported_codecs=" + SupportedCodecs;
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
        var data = await QueryAsync(q, new { login = login.ToLowerInvariant(), playerType }, ct, session: true);
        var tok = data?.Obj("streamPlaybackAccessToken") ?? default;
        var value = tok.Str("value");
        var sig = tok.Str("signature");
        if (value == null || sig == null) return new();

        var url = $"https://usher.ttvnw.net/api/channel/hls/{Uri.EscapeDataString(login.ToLowerInvariant())}.m3u8" +
                  $"?sig={sig}&token={Uri.EscapeDataString(value)}&allow_source=true&allow_audio_only=true" +
                  $"&fast_bread=true&player_backend=mediaplayer&playlist_include_framerate=true" +
                  $"&supported_codecs={SupportedCodecs}" +
                  $"&p={Random.Shared.Next(100000, 999999)}";
        using var resp = await Net.Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return new(); // 404 = offline
        return ParseMasterPlaylist(await resp.Content.ReadAsStringAsync(ct));
    }

    // Twitch only lists HEVC/AV1 renditions (which is where 1440p lives) for players that ask for them.
    // mpv decodes all three, on the GPU where the card supports it.
    const string SupportedCodecs = "av1,h265,h264";

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
                // Older playlists name renditions via EXT-X-MEDIA groups, newer ones inline (IVS-NAME).
                var group = pending.GetValueOrDefault("VIDEO", pending.GetValueOrDefault("STABLE-VARIANT-ID", ""));
                var name = names.GetValueOrDefault(group) ?? pending.GetValueOrDefault("IVS-NAME", group);
                bool isSource = group == "chunked" || pending.GetValueOrDefault("IVS-VARIANT-SOURCE") == "source";
                if (isSource && !name.Contains("source", StringComparison.OrdinalIgnoreCase)) name += " (source)";
                if (group == "audio_only") name = "Audio only";
                int.TryParse(pending.GetValueOrDefault("BANDWIDTH", "0"), out var bw);
                int width = 0, height = 0;
                var res = pending.GetValueOrDefault("RESOLUTION", "").Split('x');
                if (res.Length == 2) { int.TryParse(res[0], out width); int.TryParse(res[1], out height); }
                var codecs = pending.GetValueOrDefault("CODECS", "");
                var codec = codecs.StartsWith("hvc1") || codecs.StartsWith("hev1") ? "HEVC"
                          : codecs.StartsWith("av01") ? "AV1" : "H.264";
                // Dual-format streams also carry a vertical copy for phones; not useful here.
                if (height <= width || group == "audio_only")
                    variants.Add(new StreamVariant(name, line, bw, isSource, height, codec));
                pending = null;
            }
        }

        // Same name in two codecs (e.g. 1080p60 in H.264 and HEVC): say which is which.
        var clashing = variants.GroupBy(v => v.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        variants = variants.Select(v => clashing.Contains(v.Name) && v.Codec != "H.264" ? v with { Name = $"{v.Name} ({v.Codec})" } : v).ToList();

        // Highest resolution first, audio-only last
        return variants
            .OrderBy(v => v.Name == "Audio only")
            .ThenByDescending(v => v.Height)
            .ThenByDescending(v => v.IsSource)
            .ThenByDescending(v => v.Bandwidth)
            .ToList();
    }
}
