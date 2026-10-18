using System.Collections.Concurrent;
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
    public string? Author { get; init; }

    AnimatedImage? _image;
    public AnimatedImage Image => _image ??= ImageCache.Get(Url, this);

    public string ProviderLabel => Provider switch
    {
        EmoteProvider.SevenTV => IsGlobal ? "7TV Global" : "7TV Channel",
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

    public static Task<List<EmoteInfo>> SevenTvGlobal() => _seventvGlobal ??= Retry(async () =>
    {
        using var doc = await Net.GetJsonAsync("https://7tv.io/v3/emote-sets/global");
        return doc == null ? null : doc.RootElement.Arr("emotes").Select(e => FromSevenTv(e, true)).OfType<EmoteInfo>().ToList();
    }, () => _seventvGlobal = null);

    public static async Task<(string? SetId, string? UserId, List<EmoteInfo> Emotes)> SevenTvChannel(string twitchId)
    {
        using var doc = await Net.GetJsonAsync($"https://7tv.io/v3/users/twitch/{twitchId}");
        if (doc == null) return (null, null, new());
        var set = doc.RootElement.Obj("emote_set");
        var emotes = set.Arr("emotes").Select(e => FromSevenTv(e, false)).OfType<EmoteInfo>().ToList();
        return (set.Str("id"), doc.RootElement.Obj("user").Str("id"), emotes);
    }

    // Parses a 7TV "active emote" (from REST or EventAPI).
    public static EmoteInfo? FromSevenTv(JsonElement e, bool global)
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
            Author = data.Obj("owner").Str("display_name"),
        };
    }

    public static Task<List<EmoteInfo>> BttvGlobal() => _bttvGlobal ??= Retry(async () =>
    {
        using var doc = await Net.GetJsonAsync("https://api.betterttv.net/3/cached/emotes/global");
        return doc == null ? null : doc.RootElement.EnumerateArray().Select(e => FromBttv(e, true)).ToList();
    }, () => _bttvGlobal = null);

    public static async Task<List<EmoteInfo>> BttvChannel(string twitchId)
    {
        using var doc = await Net.GetJsonAsync($"https://api.betterttv.net/3/cached/users/twitch/{twitchId}");
        if (doc == null) return new();
        return doc.RootElement.Arr("channelEmotes").Concat(doc.RootElement.Arr("sharedEmotes"))
            .Select(e => FromBttv(e, false)).ToList();
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
        using var doc = await Net.GetJsonAsync("https://api.frankerfacez.com/v1/set/global");
        if (doc == null) return null;
        var defaults = doc.RootElement.Arr("default_sets").Select(s => s.ToString()).ToHashSet();
        return ParseFfzSets(doc.RootElement.Obj("sets"), true, defaults);
    }, () => _ffzGlobal = null);

    public static async Task<List<EmoteInfo>> FfzChannel(string twitchId)
    {
        using var doc = await Net.GetJsonAsync($"https://api.frankerfacez.com/v1/room/id/{twitchId}");
        return doc == null ? new() : ParseFfzSets(doc.RootElement.Obj("sets"), false, null);
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

    static Task<List<EmoteInfo>> Retry(Func<Task<List<EmoteInfo>?>> load, Action reset) => Task.Run(async () =>
    {
        var result = await load();
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

    public async Task LoadAsync(string twitchId)
    {
        var stv = EmoteSources.SevenTvChannel(twitchId);
        var bttv = EmoteSources.BttvChannel(twitchId);
        var ffz = EmoteSources.FfzChannel(twitchId);
        var g7 = EmoteSources.SevenTvGlobal();
        var gb = EmoteSources.BttvGlobal();
        var gf = EmoteSources.FfzGlobal();
        await Task.WhenAll(stv, bttv, ffz, g7, gb, gf);

        (SevenTvSetId, SevenTvUserId, _seventv) = stv.Result;
        _bttv = bttv.Result;
        _ffz = ffz.Result;
        _seventvGlobal = g7.Result;
        _bttvGlobal = gb.Result;
        _ffzGlobal = gf.Result;
        Rebuild();
    }

    public EmoteInfo? Find(string word) => _map.TryGetValue(word, out var e) ? e : null;

    public IEnumerable<EmoteInfo> All => _map.Values;

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
