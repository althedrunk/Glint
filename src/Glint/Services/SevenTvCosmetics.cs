using System.Text.Json;
using Windows.UI;

namespace Glint;

public enum PaintKind { Linear, Radial, Image }

public sealed record PaintStop(float At, Color Color);
public sealed record PaintShadow(float X, float Y, float Radius, Color Color);

public sealed class SevenTvPaint
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public PaintKind Kind { get; init; }
    public float Angle { get; init; }
    public bool Repeat { get; init; }
    public bool Circle { get; init; } = true;
    public string? ImageUrl { get; init; }
    public List<PaintStop> Stops { get; init; } = new();
    public List<PaintShadow> Shadows { get; init; } = new();
}

public sealed record SevenTvBadge(string Id, string Tooltip, string Url);

// Who has which 7TV paint, badge and personal emotes. 7TV pushes this over the EventAPI for people
// chatting in channels we're subscribed to (see SevenTvEvents.SubscribeChannel). Everything is keyed by
// Twitch user ID. Thread-safe; written from the socket thread, read while building/drawing messages.
public static class SevenTvCosmetics
{
    sealed class UserState
    {
        public string? PaintId, BadgeId;
        public HashSet<string> Sets = new();
    }

    static readonly object Gate = new();
    static readonly Dictionary<string, SevenTvPaint> Paints = new();
    static readonly Dictionary<string, SevenTvBadge> Badges = new();
    static readonly Dictionary<string, UserState> Users = new();
    static readonly Dictionary<string, Dictionary<string, EmoteInfo>> PersonalSets = new();

    // Raised (on a worker thread) when something that affects how messages look has changed.
    public static event Action? Changed;

    public static SevenTvPaint? PaintFor(string? userId)
    {
        if (string.IsNullOrEmpty(userId) || !Settings.Current.ShowSevenTvPaints) return null;
        lock (Gate)
            return Users.TryGetValue(userId, out var u) && u.PaintId != null && Paints.TryGetValue(u.PaintId, out var p) ? p : null;
    }

    public static SevenTvBadge? BadgeFor(string? userId)
    {
        if (string.IsNullOrEmpty(userId) || !Settings.Current.ShowSevenTvBadges) return null;
        lock (Gate)
            return Users.TryGetValue(userId, out var u) && u.BadgeId != null && Badges.TryGetValue(u.BadgeId, out var b) ? b : null;
    }

    public static EmoteInfo? PersonalEmote(string? userId, string word)
    {
        if (string.IsNullOrEmpty(userId)) return null;
        lock (Gate)
        {
            if (!Users.TryGetValue(userId, out var u)) return null;
            foreach (var set in u.Sets)
                if (PersonalSets.TryGetValue(set, out var emotes) && emotes.TryGetValue(word, out var e)) return e;
        }
        return null;
    }

    public static bool IsPersonalSet(string setId)
    {
        lock (Gate) return PersonalSets.ContainsKey(setId);
    }

    // cosmetic.create
    public static void OnCosmetic(JsonElement obj)
    {
        var kind = obj.Str("kind");
        var data = obj.Obj("data");
        var id = data.Str("id");
        if (id == null) return;
        lock (Gate)
        {
            if (kind == "PAINT" && ParsePaint(data) is { } paint) Paints[id] = paint;
            else if (kind == "BADGE" && data.Obj("host").Str("url") is { } host)
                Badges[id] = new SevenTvBadge(id, data.Str("tooltip") ?? data.Str("name") ?? "7TV badge", "https:" + host + "/2x.webp");
            else return;
        }
        Changed?.Invoke();
    }

    // entitlement.create / entitlement.delete
    public static void OnEntitlement(JsonElement obj, bool created)
    {
        var twitchId = obj.Obj("user").Arr("connections")
            .FirstOrDefault(c => c.Str("platform") == "TWITCH").Str("id");
        var refId = obj.Str("ref_id");
        if (twitchId == null || refId == null) return;
        lock (Gate)
        {
            if (!Users.TryGetValue(twitchId, out var u)) Users[twitchId] = u = new UserState();
            switch (obj.Str("kind"))
            {
                case "PAINT":
                    if (created) u.PaintId = refId;
                    else if (u.PaintId == refId) u.PaintId = null;
                    break;
                case "BADGE":
                    if (created) u.BadgeId = refId;
                    else if (u.BadgeId == refId) u.BadgeId = null;
                    break;
                case "EMOTE_SET":
                    if (created) { u.Sets.Add(refId); if (!PersonalSets.ContainsKey(refId)) PersonalSets[refId] = new(); }
                    else u.Sets.Remove(refId);
                    break;
                default: return;
            }
        }
        Changed?.Invoke();
    }

    // emote_set.create for a personal set can already contain emotes.
    public static void OnEmoteSetCreated(JsonElement obj)
    {
        var id = obj.Str("id");
        if (id == null) return;
        lock (Gate)
        {
            if (!PersonalSets.TryGetValue(id, out var set)) PersonalSets[id] = set = new();
            foreach (var e in obj.Arr("emotes"))
                if (EmoteSources.FromSevenTv(e, false, personal: true) is { } emote) set[emote.Name] = emote;
        }
    }

    // emote_set.update for a personal set
    public static void OnPersonalSetChanged(string setId, JsonElement? added, JsonElement? removed)
    {
        lock (Gate)
        {
            if (!PersonalSets.TryGetValue(setId, out var set)) return;
            if (removed is { } r)
                foreach (var kv in set.Where(kv => kv.Value.Id == r.Str("id")).ToList()) set.Remove(kv.Key);
            if (added is { } a && EmoteSources.FromSevenTv(a, false, personal: true) is { } emote)
            {
                foreach (var kv in set.Where(kv => kv.Value.Id == emote.Id).ToList()) set.Remove(kv.Key); // renames
                set[emote.Name] = emote;
            }
        }
    }

    static SevenTvPaint? ParsePaint(JsonElement d)
    {
        // Newer paints can be layered ("gradients"); we draw the first layer.
        var layer = d.Arr("gradients").FirstOrDefault();
        var src = layer.ValueKind == JsonValueKind.Object && d.Arr("stops").Count() == 0 && string.IsNullOrEmpty(d.Str("image_url")) ? layer : d;

        var kind = src.Str("function") switch
        {
            "RADIAL_GRADIENT" => PaintKind.Radial,
            "URL" => PaintKind.Image,
            _ => PaintKind.Linear,
        };
        var stops = src.Arr("stops").Select(s => new PaintStop(Float(s, "at"), Rgba(s, "color"))).ToList();
        var image = src.Str("image_url");
        if (kind == PaintKind.Image && string.IsNullOrEmpty(image)) return null;
        if (kind != PaintKind.Image && stops.Count == 0)
        {
            // Solid colour paint
            if (d.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.Number)
                stops = new() { new PaintStop(0, Rgba(d, "color")), new PaintStop(1, Rgba(d, "color")) };
            else return null;
        }
        return new SevenTvPaint
        {
            Id = d.Str("id")!,
            Name = d.Str("name") ?? "",
            Kind = kind,
            Angle = Float(src, "angle"),
            Repeat = src.Bool("repeat"),
            Circle = src.Str("shape") != "ellipse",
            ImageUrl = image,
            Stops = stops,
            Shadows = d.Arr("shadows").Select(s => new PaintShadow(Float(s, "x_offset"), Float(s, "y_offset"), Float(s, "radius"), Rgba(s, "color"))).ToList(),
        };
    }

    static float Float(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : 0;

    // 7TV packs colours as signed 32-bit RGBA.
    static Color Rgba(JsonElement e, string name)
    {
        uint v = e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? unchecked((uint)p.GetInt64()) : 0xFFFFFFFF;
        return Color.FromArgb((byte)v, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8));
    }
}
