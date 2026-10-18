using System.Net.Http.Headers;
using System.Text.Json;

namespace Glint;

// The twitch.tv session supplies chat, follows, emotes and playback benefits.
// Clips still use the separate API login.
public static class TwitchSession
{
    static AppSettings S => Settings.Current;

    public static bool IsConnected => S.WebSessionToken != null;
    public static string? Login => S.WebSessionLogin;

    // Adds the session to a GQL request when one is connected.
    public static void Apply(HttpRequestMessage req)
    {
        if (S.WebSessionToken is not { } token) return;
        req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
        if (S.WebDeviceId is { Length: > 0 } device) req.Headers.TryAddWithoutValidation("Device-Id", device);
    }

    // Checks a token from the website's auth-token cookie and stores it. Returns the login, or null.
    public static async Task<string?> ConnectAsync(string token, string? deviceId)
    {
        var info = await ValidateAsync(token);
        if (info == null) return null;
        S.WebSessionToken = token;
        S.WebSessionLogin = info.Value.Login;
        S.WebSessionUserId = info.Value.UserId;
        S.WebDeviceId = deviceId;
        Settings.Save();
        return info.Value.Login;
    }

    public static void Disconnect()
    {
        S.WebSessionToken = null;
        S.WebSessionLogin = null;
        S.WebSessionUserId = null;
        S.WebDeviceId = null;
        Settings.Save();
    }

    // Run at startup: drop the session if Twitch no longer accepts it (signed out elsewhere, password changed).
    public static async Task CheckAsync()
    {
        if (S.WebSessionToken is not { } token) return;
        if (await ValidateAsync(token) == null)
        {
            Log.Info("twitch.tv session is no longer valid, disconnecting");
            Disconnect();
        }
    }

    // Who you follow, via the same query twitch.tv uses (Twitch's official API won't take a website session).
    // Twitch blocks paging this query from outside its own site, so we take the 100 oldest and 100 newest.
    public static async Task<List<string>?> GetFollowedLoginsAsync()
    {
        const string hash = "eecf815273d3d949e5cf0085cc5084cd8a1b5b7b6f7990cf43cb0beadf546907";
        var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool any = false;
        foreach (var order in new[] { "ASC", "DESC" })
        {
            if (await PersistedAsync("ChannelFollows", hash, new { limit = 100, order }) is not { } data) continue;
            any = true;
            var follows = data.Obj("user").Obj("follows");
            foreach (var e in follows.Arr("edges"))
                if (e.Obj("node").Str("login") is { } login) logins.Add(login);
            if (!follows.Obj("pageInfo").Bool("hasNextPage")) break; // everything fitted in one page
        }
        return any ? logins.ToList() : null;
    }

    // Twitch emotes you can use (subs, follower emotes, bits, globals).
    public static async Task<List<EmoteInfo>> GetUserEmotesAsync()
    {
        var data = await GqlAsync(new { query = "query { currentUser { emoteSets { emotes { id token } } } }" });
        var emotes = new List<EmoteInfo>();
        foreach (var set in data?.Obj("currentUser").Arr("emoteSets") ?? Enumerable.Empty<JsonElement>())
            foreach (var e in set.Arr("emotes"))
                if (e.Str("id") is { } id && e.Str("token") is { } name) emotes.Add(EmoteSources.Twitch(id, name));
        return emotes.GroupBy(e => e.Name).Select(g => g.First()).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static Task<JsonElement?> PersistedAsync(string operation, string hash, object variables) =>
        GqlAsync(new { operationName = operation, variables, extensions = new { persistedQuery = new { version = 1, sha256Hash = hash } } });

    static async Task<JsonElement?> GqlAsync(object body)
    {
        if (!IsConnected) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql") { Content = System.Net.Http.Json.JsonContent.Create(body) };
            req.Headers.Add("Client-ID", TwitchGql.WebClientId);
            Apply(req);
            using var resp = await Net.Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) { Log.Error($"Session GQL HTTP {(int)resp.StatusCode}"); return null; }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.Arr("errors").FirstOrDefault().Str("message") is { } error) { Log.Error($"Session GQL: {error}"); return null; }
            return doc.RootElement.Obj("data").Clone();
        }
        catch (Exception ex) { Log.Error("Session GQL", ex); return null; }
    }

    static async Task<(string Login, string UserId)?> ValidateAsync(string token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
            req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
            using var resp = await Net.Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            return r.Str("login") is { } login && r.Str("user_id") is { } id ? (login, id) : null;
        }
        catch (Exception ex)
        {
            Log.Error("Session validate", ex);
            return null;
        }
    }
}
