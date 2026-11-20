using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace Glint;

public enum EmoteProvider { Twitch, SevenTV, BTTV, FFZ }

public sealed class EmoteInfo
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public required EmoteProvider Provider { get; init; }
    // Image URL at 2x density.
    public required string Url { get; init; }
    // Display size in DIPs; 0 until known (BTTV doesn't publish sizes).
    public float Width { get; set; }
    public float Height { get; set; }
    public bool ZeroWidth { get; init; }
    public bool IsGlobal { get; init; }
    public bool IsPersonal { get; init; }
    public string? Author { get; init; }

    AnimatedImage? _image;
    public AnimatedImage Image => _image ??= ImageCache.Get(Url, this);

    public string ProviderLabel => Provider switch
    {
        EmoteProvider.SevenTV => IsPersonal ? "7TV Personal" : IsGlobal ? "7TV Global" : "7TV Channel",
        EmoteProvider.BTTV => IsGlobal ? "BTTV Global" : "BTTV Channel",
        EmoteProvider.FFZ => IsGlobal ? "FFZ Global" : "FFZ Channel",
        _ => "Twitch",
    };
}

public static class EmoteSources
{
    static readonly ConcurrentDictionary<string, EmoteInfo> TwitchEmotes = new();
    static Task<List<EmoteInfo>>? _seventvGlobal, _bttvGlobal, _ffzGlobal;

    public static EmoteInfo Twitch(string id, string name) => TwitchEmotes.GetOrAdd(id, _ => new EmoteInfo
    {
        Name = name,
        Id = id,
        Provider = EmoteProvider.Twitch,
        Url = $"https://static-cdn.jtvnw.net/emoticons/v2/{id}/default/dark/2.0",
        Width = 28,
        Height = 28,
    });

    // Emote APIs sometimes stall on one request, so give up on it sooner and have a second go.
    // Chat waits for emotes before showing anything, so both tries together stay under the old 20s.
    static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(8);

    const string SevenTvGlobalUrl = "https://7tv.io/v3/emote-sets/global";
    const string BttvGlobalUrl = "https://api.betterttv.net/3/cached/emotes/global";
    const string FfzGlobalUrl = "https://api.frankerfacez.com/v1/set/global";
    static string SevenTvChannelUrl(string id) => $"https://7tv.io/v3/users/twitch/{id}";
    static string BttvChannelUrl(string id) => $"https://api.betterttv.net/3/cached/users/twitch/{id}";
    static string FfzChannelUrl(string id) => $"https://api.frankerfacez.com/v1/room/id/{id}";

    // Null when the channel has no account there; throws when the service itself is failing.
    // Every answer is also kept on disk, so a channel's emotes are there straight away next time.
    static async Task<JsonDocument?> Fetch(string url)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var limit = new CancellationTokenSource(FetchTimeout);
            try
            {
                using var resp = await Net.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, limit.Token);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    try { File.Delete(CachePath(url)); } catch { }
                    return null;
                }
                resp.EnsureSuccessStatusCode();
                var bytes = await resp.Content.ReadAsByteArrayAsync(limit.Token);
                var doc = JsonDocument.Parse(bytes);
                try { await File.WriteAllBytesAsync(CachePath(url), bytes); } catch { }
                return doc;
            }
            catch (Exception ex) when (attempt == 1)
            {
                Log.Info($"Retrying {url} ({ex.GetType().Name})");
            }
        }
    }

    static string CachePath(string url)
    {
        var dir = Path.Combine(Paths.LocalData, "cache", "emotes");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ".json");
    }

    // What was saved last time, or null.
    static async Task<JsonDocument?> FromDisk(string url)
    {
        try
        {
            var path = CachePath(url);
            return File.Exists(path) ? JsonDocument.Parse(await File.ReadAllBytesAsync(path)) : null;
        }
        catch { return null; }
    }

    static bool CachedRecently(string url, TimeSpan age)
    {
        try { return File.GetLastWriteTimeUtc(CachePath(url)) > DateTime.UtcNow - age; }
        catch { return false; }
    }

    static List<EmoteInfo>? ParseSevenTvGlobal(JsonDocument? doc) =>
        doc?.RootElement.Arr("emotes").Select(e => FromSevenTv(e, true)).OfType<EmoteInfo>().ToList();

    static (string? SetId, string? UserId, List<EmoteInfo> Emotes) ParseSevenTvChannel(JsonDocument? doc)
    {
        if (doc == null) return (null, null, new());
        var set = doc.RootElement.Obj("emote_set");
        var emotes = set.Arr("emotes").Select(e => FromSevenTv(e, false)).OfType<EmoteInfo>().ToList();
        return (set.Str("id"), doc.RootElement.Obj("user").Str("id"), emotes);
    }

    static List<EmoteInfo>? ParseBttvGlobal(JsonDocument? doc) =>
        doc?.RootElement.EnumerateArray().Select(e => FromBttv(e, true)).ToList();

    static List<EmoteInfo> ParseBttvChannel(JsonDocument? doc) => doc == null ? new() :
        doc.RootElement.Arr("channelEmotes").Concat(doc.RootElement.Arr("sharedEmotes")).Select(e => FromBttv(e, false)).ToList();

    static List<EmoteInfo>? ParseFfzGlobal(JsonDocument? doc)
    {
        if (doc == null) return null;
        var defaults = doc.RootElement.Arr("default_sets").Select(s => s.ToString()).ToHashSet();
        return ParseFfzSets(doc.RootElement.Obj("sets"), true, defaults);
    }

    static List<EmoteInfo> ParseFfzChannel(JsonDocument? doc) =>
        doc == null ? new() : ParseFfzSets(doc.RootElement.Obj("sets"), false, null);

    // Everything saved for this channel last time, read from disk without touching the network.
    internal static async Task<(List<EmoteInfo>? SevenTvGlobal, List<EmoteInfo>? BttvGlobal, List<EmoteInfo>? FfzGlobal,
        (string? SetId, string? UserId, List<EmoteInfo> Emotes) SevenTv, List<EmoteInfo> Bttv, List<EmoteInfo> Ffz)> Cached(string twitchId)
    {
        using var g7 = await FromDisk(SevenTvGlobalUrl);
        using var gb = await FromDisk(BttvGlobalUrl);
        using var gf = await FromDisk(FfzGlobalUrl);
        using var c7 = await FromDisk(SevenTvChannelUrl(twitchId));
        using var cb = await FromDisk(BttvChannelUrl(twitchId));
        using var cf = await FromDisk(FfzChannelUrl(twitchId));
        return (ParseSevenTvGlobal(g7), ParseBttvGlobal(gb), ParseFfzGlobal(gf), ParseSevenTvChannel(c7), ParseBttvChannel(cb), ParseFfzChannel(cf));
    }

    // At launch: fetch the global lists and the given channels' sets in the background so they're on disk
    // before you open them. Channels fetched in the last few hours are left alone.
    public static async Task PreloadAsync(IEnumerable<string> twitchIds)
    {
        await Task.WhenAll(SevenTvGlobal(), BttvGlobal(), FfzGlobal());
        using var gate = new SemaphoreSlim(3);
        await Task.WhenAll(twitchIds.Distinct().Where(id => !CachedRecently(SevenTvChannelUrl(id), TimeSpan.FromHours(6))).Select(async id =>
        {
            await gate.WaitAsync();
            try
            {
                foreach (var url in new[] { SevenTvChannelUrl(id), BttvChannelUrl(id), FfzChannelUrl(id) })
                {
                    try { (await Fetch(url))?.Dispose(); }
                    catch { } // a service being down just means it isn't cached yet
                }
            }
            finally { gate.Release(); }
        }));
    }

    public static Task<List<EmoteInfo>> SevenTvGlobal() => _seventvGlobal ??= Retry(async () =>
    {
        using var doc = await Fetch(SevenTvGlobalUrl);
        return ParseSevenTvGlobal(doc);
    }, () => _seventvGlobal = null);

    public static async Task<(string? SetId, string? UserId, List<EmoteInfo> Emotes)> SevenTvChannel(string twitchId)
    {
        using var doc = await Fetch(SevenTvChannelUrl(twitchId));
        return ParseSevenTvChannel(doc);
    }

    // Parses a 7TV "active emote" (from REST or EventAPI).
    public static EmoteInfo? FromSevenTv(JsonElement e, bool global, bool personal = false)
    {
        var data = e.Obj("data");
        var host = data.Obj("host");
        var hostUrl = host.Str("url");
        var id = e.Str("id") ?? data.Str("id");
        var name = e.Str("name") ?? data.Str("name");
        if (hostUrl == null || id == null || name == null) return null;

        float w = 0, h = 0;
        foreach (var f in host.Arr("files"))
        {
            if (f.Str("name") == "1x.webp") { w = f.Int("width"); h = f.Int("height"); break; }
        }
        // Normalise 7TV's 32px base to Twitch's 28px line height so mixed emotes sit evenly.
        if (h > 0) { var scale = 28f / 32f; w *= scale; h *= scale; }

        var zeroWidth = (e.Int("flags") & 1) != 0 || (data.Int("flags") & 256) != 0;
        return new EmoteInfo
        {
            Name = name,
            Id = id,
            Provider = EmoteProvider.SevenTV,
            Url = "https:" + hostUrl + "/2x.webp",
            Width = w,
            Height = h,
            ZeroWidth = zeroWidth,
            IsGlobal = global,
            IsPersonal = personal,
            Author = data.Obj("owner").Str("display_name"),
        };
    }

    public static Task<List<EmoteInfo>> BttvGlobal() => _bttvGlobal ??= Retry(async () =>
    {
        using var doc = await Fetch(BttvGlobalUrl);
        return ParseBttvGlobal(doc);
    }, () => _bttvGlobal = null);

    public static async Task<List<EmoteInfo>> BttvChannel(string twitchId)
    {
        using var doc = await Fetch(BttvChannelUrl(twitchId));
        return ParseBttvChannel(doc);
    }

    static readonly HashSet<string> BttvZeroWidth = new() { "SoSnowy", "IceCold", "SantaHat", "TopHat", "ReinDeer", "CandyCane", "cvMask", "cvHazmat" };

    static EmoteInfo FromBttv(JsonElement e, bool global)
    {
        var id = e.Str("id")!;
        var code = e.Str("code")!;
        return new EmoteInfo
        {
            Name = code,
            Id = id,
            Provider = EmoteProvider.BTTV,
            Url = $"https://cdn.betterttv.net/emote/{id}/2x.webp",
            IsGlobal = global,
            ZeroWidth = global && BttvZeroWidth.Contains(code),
            Author = e.Obj("user").Str("displayName"),
        };
    }

    public static Task<List<EmoteInfo>> FfzGlobal() => _ffzGlobal ??= Retry(async () =>
    {
        using var doc = await Fetch(FfzGlobalUrl);
        return ParseFfzGlobal(doc);
    }, () => _ffzGlobal = null);

    public static async Task<List<EmoteInfo>> FfzChannel(string twitchId)
    {
        using var doc = await Fetch(FfzChannelUrl(twitchId));
        return ParseFfzChannel(doc);
    }

    static List<EmoteInfo> ParseFfzSets(JsonElement sets, bool global, HashSet<string>? only)
    {
        var list = new List<EmoteInfo>();
        if (sets.ValueKind != JsonValueKind.Object) return list;
        foreach (var set in sets.EnumerateObject())
        {
            if (only != null && !only.Contains(set.Name)) continue;
            foreach (var e in set.Value.Arr("emoticons"))
            {
                var anim = e.Obj("animated");
                var urls = anim.ValueKind == JsonValueKind.Object ? anim : e.Obj("urls");
                var url = urls.Str("2") ?? urls.Str("1");
                if (url == null) continue;
                if (url.StartsWith("//")) url = "https:" + url;
                list.Add(new EmoteInfo
                {
                    Name = e.Str("name")!,
                    Id = e.Int("id").ToString(),
                    Provider = EmoteProvider.FFZ,
                    Url = url,
                    Width = e.Int("width"),
                    Height = e.Int("height"),
                    IsGlobal = global,
                    Author = e.Obj("owner").Str("display_name"),
                });
            }
        }
        return list;
    }

    // Shared global lists: a failed load isn't kept, so the next channel tries again.
    static Task<List<EmoteInfo>> Retry(Func<Task<List<EmoteInfo>?>> load, Action reset) => Task.Run(async () =>
    {
        List<EmoteInfo>? result = null;
        try { result = await load(); }
        catch (Exception ex) { Log.Error("Global emotes", ex); }
        if (result == null) { reset(); return new List<EmoteInfo>(); }
        return result;
    });
}

// The emote lookup table for one channel, merged with the right precedence.
public sealed class ChannelEmotes
{
    List<EmoteInfo> _seventv = new(), _bttv = new(), _ffz = new();
    List<EmoteInfo> _seventvGlobal = new(), _bttvGlobal = new(), _ffzGlobal = new();
    volatile Dictionary<string, EmoteInfo> _map = new();

    public string? SevenTvSetId { get; private set; }
    public string? SevenTvUserId { get; private set; }

    // What was saved on disk last time, so chat has its emotes before the services have even answered.
    public async Task LoadCachedAsync(string twitchId)
    {
        var cached = await Task.Run(() => EmoteSources.Cached(twitchId));
        lock (this)
        {
            if (cached.SevenTvGlobal is { Count: > 0 } g7) _seventvGlobal = g7;
            if (cached.BttvGlobal is { Count: > 0 } gb) _bttvGlobal = gb;
            if (cached.FfzGlobal is { Count: > 0 } gf) _ffzGlobal = gf;
            (SevenTvSetId, SevenTvUserId, _seventv) = cached.SevenTv;
            (_bttv, _ffz) = (cached.Bttv, cached.Ffz);
            Rebuild();
            Version++;
        }
    }

    public async Task LoadAsync(string twitchId)
    {
        var stv = EmoteSources.SevenTvChannel(twitchId);
        var bttv = EmoteSources.BttvChannel(twitchId);
        var ffz = EmoteSources.FfzChannel(twitchId);
        var g7 = EmoteSources.SevenTvGlobal();
        var gb = EmoteSources.BttvGlobal();
        var gf = EmoteSources.FfzGlobal();
        // Each source stands alone: one that fails mustn't take the others with it.
        bool failed = false;
        async Task<T> OrEmpty<T>(Task<T> load, T empty, string source)
        {
            try { return await load; }
            catch (Exception ex)
            {
                Log.Error($"Emotes ({source})", ex);
                failed = true;
                return empty;
            }
        }
        // A source that fails keeps whatever it had (from the disk cache, say) rather than going empty.
        var seventv = await OrEmpty(stv, (SevenTvSetId, SevenTvUserId, _seventv), "7TV channel");
        var bttvList = await OrEmpty(bttv, _bttv, "BTTV channel");
        var ffzList = await OrEmpty(ffz, _ffz, "FFZ channel");
        // The global lists are never really empty, so empty means that load failed.
        var g7List = await g7;
        var gbList = await gb;
        var gfList = await gf;
        lock (this)
        {
            (SevenTvSetId, SevenTvUserId, _seventv) = seventv;
            (_bttv, _ffz) = (bttvList, ffzList);
            if (g7List.Count > 0) _seventvGlobal = g7List;
            if (gbList.Count > 0) _bttvGlobal = gbList;
            if (gfList.Count > 0) _ffzGlobal = gfList;
            Incomplete = failed || g7List.Count == 0 || gbList.Count == 0 || gfList.Count == 0;
            Rebuild();
            Version++;
        }
    }

    // Some source couldn't be reached, so a later LoadAsync may find more.
    public bool Incomplete { get; private set; }
    // Goes up with each full load, so messages matched against an older set can be checked again.
    public int Version { get; private set; }

    public EmoteInfo? Find(string word) => _map.TryGetValue(word, out var e) ? e : null;

    public IEnumerable<EmoteInfo> All => _map.Values;

    // For the emote picker: each source on its own, channel sets first.
    public List<(string Title, List<EmoteInfo> Emotes)> Sections() => new()
    {
        ("7TV channel", Sorted(_seventv)),
        ("BTTV channel", Sorted(_bttv)),
        ("FFZ channel", Sorted(_ffz)),
        ("7TV global", Sorted(_seventvGlobal)),
        ("BTTV global", Sorted(_bttvGlobal)),
        ("FFZ global", Sorted(_ffzGlobal)),
    };

    static List<EmoteInfo> Sorted(List<EmoteInfo> list) => list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public int Count => _map.Count;

    void Rebuild()
    {
        var map = new Dictionary<string, EmoteInfo>(StringComparer.Ordinal);
        // Lowest precedence first; later sources overwrite.
        foreach (var list in new[] { _ffzGlobal, _bttvGlobal, _seventvGlobal, _ffz, _bttv, _seventv })
            foreach (var e in list) map[e.Name] = e;
        _map = map;
    }

    public void SevenTvAdd(EmoteInfo e)
    {
        lock (this) { _seventv = _seventv.Where(x => x.Name != e.Name).Append(e).ToList(); Rebuild(); }
    }

    public EmoteInfo? SevenTvRemove(string id)
    {
        lock (this)
        {
            var old = _seventv.FirstOrDefault(x => x.Id == id);
            if (old == null) return null;
            _seventv = _seventv.Where(x => x != old).ToList();
            Rebuild();
            return old;
        }
    }
}
