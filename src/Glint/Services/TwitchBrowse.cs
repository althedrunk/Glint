using System.Text.Json;

namespace Glint;

public sealed record BrowseStream(string Login, string DisplayName, string? Avatar, string? Title, int Viewers, string? Game, string? Thumbnail, bool Live);

public sealed record BrowseCategory(string Name, string DisplayName, string? BoxArt, int Viewers);

// Directory data for the Browse page, from the same public GQL endpoint as everything else.
public static class TwitchBrowse
{
    const string StreamFields = """
        title viewersCount previewImageURL(width: 440, height: 248)
        broadcaster { login displayName profileImageURL(width: 70) }
        game { displayName }
        """;

    // Twitch caps the top-streams list at 30 for third-party clients.
    public static async Task<List<BrowseStream>> TopStreamsAsync()
    {
        var data = await TwitchGql.RawQueryAsync($"{{ streams(first: 30) {{ edges {{ node {{ {StreamFields} }} }} }} }}");
        return data?.Obj("streams").Arr("edges").Select(e => ParseStream(e.Obj("node"))).OfType<BrowseStream>().ToList() ?? new();
    }

    public static async Task<List<BrowseStream>> CategoryStreamsAsync(string category)
    {
        var data = await TwitchGql.RawQueryAsync(
            $"query($name: String!) {{ game(name: $name) {{ streams(first: 100) {{ edges {{ node {{ {StreamFields} }} }} }} }} }}", new { name = category });
        return data?.Obj("game").Obj("streams").Arr("edges").Select(e => ParseStream(e.Obj("node"))).OfType<BrowseStream>().ToList() ?? new();
    }

    public static async Task<List<BrowseCategory>> TopCategoriesAsync()
    {
        var data = await TwitchGql.RawQueryAsync("{ games(first: 100) { edges { node { name displayName viewersCount boxArtURL(width: 188, height: 250) } } } }");
        return data?.Obj("games").Arr("edges").Select(e => ParseCategory(e.Obj("node"))).OfType<BrowseCategory>().ToList() ?? new();
    }

    public static async Task<(List<BrowseStream> Channels, List<BrowseCategory> Categories)> SearchAsync(string query)
    {
        const string q = """
            query($q: String!) {
              searchFor(userQuery: $q, platform: "web") {
                channels { items { login displayName profileImageURL(width: 70)
                  stream { viewersCount title previewImageURL(width: 440, height: 248) game { displayName } } } }
                games { items { name displayName viewersCount boxArtURL(width: 188, height: 250) } }
              }
            }
            """;
        var data = await TwitchGql.RawQueryAsync(q, new { q = query });
        var root = data?.Obj("searchFor") ?? default;
        var channels = root.Obj("channels").Arr("items").Select(ParseSearchChannel).OfType<BrowseStream>()
            .OrderByDescending(c => c.Live).ThenByDescending(c => c.Viewers).ToList();
        var categories = root.Obj("games").Arr("items").Select(ParseCategory).OfType<BrowseCategory>().ToList();
        return (channels, categories);
    }

    static BrowseStream? ParseStream(JsonElement n)
    {
        var b = n.Obj("broadcaster");
        if (b.Str("login") is not { } login) return null;
        return new BrowseStream(login, b.Str("displayName") ?? login, b.Str("profileImageURL"), n.Str("title"), n.Int("viewersCount"),
            n.Obj("game").Str("displayName"), n.Str("previewImageURL"), true);
    }

    static BrowseStream? ParseSearchChannel(JsonElement c)
    {
        if (c.Str("login") is not { } login) return null;
        var s = c.Obj("stream");
        bool live = s.ValueKind == JsonValueKind.Object;
        return new BrowseStream(login, c.Str("displayName") ?? login, c.Str("profileImageURL"), live ? s.Str("title") : null,
            live ? s.Int("viewersCount") : 0, live ? s.Obj("game").Str("displayName") : null, live ? s.Str("previewImageURL") : null, live);
    }

    static BrowseCategory? ParseCategory(JsonElement g) =>
        g.Str("name") is { } name ? new BrowseCategory(name, g.Str("displayName") ?? name, g.Str("boxArtURL"), g.Int("viewersCount")) : null;
}
