using System.Net.Http.Headers;
using System.Text.Json;

namespace Glint;

public sealed record DeviceCode(string Code, string UserCode, Uri VerificationUri, int IntervalSeconds, DateTimeOffset ExpiresAt);

// Login uses Twitch's device-code flow: we show a code, the browser opens twitch.tv/activate and you
// click Authorize. No redirect URL or local web server needed.
public static class TwitchAuth
{
    // Glint's own app on dev.twitch.tv (client type "Public"). Client IDs aren't secret, so it ships in the app.
    public const string AppClientId = "2nzt9ygobu2qoihvawxodil7mqrn10";

    // The ID to log in with: a custom one from settings, else the built-in one.
    public static string? ClientId =>
        !string.IsNullOrWhiteSpace(Settings.Current.ClientId) ? Settings.Current.ClientId.Trim()
        : AppClientId.Length > 0 ? AppClientId : null;

    const string Scopes = "chat:read chat:edit user:read:follows user:read:emotes clips:edit";

    public static async Task<DeviceCode> StartDeviceFlowAsync(string clientId)
    {
        using var resp = await Net.Http.PostAsync("https://id.twitch.tv/oauth2/device", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scopes"] = Scopes,
        }));
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Twitch rejected the Client ID ({(int)resp.StatusCode}): {json}");
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new DeviceCode(r.Str("device_code")!, r.Str("user_code")!, new Uri(r.Str("verification_uri")!),
            Math.Max(1, r.Int("interval")), DateTimeOffset.UtcNow.AddSeconds(r.Int("expires_in")));
    }

    // Polls until the user approves; returns (access, refresh) or null on expiry/cancel.
    public static async Task<(string Access, string Refresh)?> PollDeviceFlowAsync(string clientId, DeviceCode code, CancellationToken ct)
    {
        while (DateTimeOffset.UtcNow < code.ExpiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(code.IntervalSeconds), ct);
            using var resp = await Net.Http.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scopes"] = Scopes,
                ["device_code"] = code.Code,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }), ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(json);
                return (doc.RootElement.Str("access_token")!, doc.RootElement.Str("refresh_token")!);
            }
            if (!json.Contains("authorization_pending")) throw new InvalidOperationException(json);
        }
        return null;
    }

    public static async Task<(string Access, string Refresh)?> RefreshAsync(string clientId, string refreshToken)
    {
        try
        {
            using var resp = await Net.Http.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
            }));
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return (doc.RootElement.Str("access_token")!, doc.RootElement.Str("refresh_token")!);
        }
        catch (Exception ex) { Log.Error("Token refresh", ex); return null; }
    }

    // Returns (login, userId) for a valid token, or null.
    public static async Task<(string Login, string UserId)?> ValidateAsync(string token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
            req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
            using var resp = await Net.Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Settings.Current.TokenScopes = doc.RootElement.Arr("scopes").Select(s => s.GetString() ?? "").ToList();
            return (doc.RootElement.Str("login")!, doc.RootElement.Str("user_id")!);
        }
        catch (Exception ex) { Log.Error("Token validate", ex); return null; }
    }

    // Logins from before clipping existed don't have the scope; logging in again adds it.
    public static bool CanClip => Settings.Current.TokenScopes.Contains("clips:edit");

    public sealed record ClipResult(string? Id, string? EditUrl, string? Error)
    {
        public string Url => $"https://clips.twitch.tv/{Id}";
    }

    // Clips the last ~30 seconds of a live broadcast. Twitch takes a few seconds to finish processing it.
    public static async Task<ClipResult> CreateClipAsync(string broadcasterId, string token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.twitch.tv/helix/clips?broadcaster_id={broadcasterId}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("Client-Id", Settings.Current.TokenClientId ?? ClientId);
            using var resp = await Net.Http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                Log.Error($"Create clip {(int)resp.StatusCode}: {body}");
                return new ClipResult(null, null, (int)resp.StatusCode switch
                {
                    401 => "Log in again to allow clipping",
                    403 => "This channel doesn't allow clips",
                    404 => "The channel needs to be live to clip",
                    _ => "Couldn't create the clip",
                });
            }
            using var doc = JsonDocument.Parse(body);
            var clip = doc.RootElement.Arr("data").FirstOrDefault();
            return new ClipResult(clip.Str("id"), clip.Str("edit_url"), clip.Str("id") == null ? "Couldn't create the clip" : null);
        }
        catch (Exception ex)
        {
            Log.Error("Create clip", ex);
            return new ClipResult(null, null, "Couldn't create the clip");
        }
    }

    // Ensures the stored token is valid (refreshing if needed). Returns the token or null if logged out.
    public static async Task<string?> EnsureTokenAsync()
    {
        var s = Settings.Current;
        // Tokens only work with the client ID that issued them.
        if (s.AccessToken is not { } token || (s.TokenClientId ?? ClientId) is not { } clientId) return null;
        var v = await ValidateAsync(token);
        if (v == null && s.RefreshToken is { } refresh)
        {
            var r = await RefreshAsync(clientId, refresh);
            if (r != null)
            {
                s.AccessToken = r.Value.Access;
                s.RefreshToken = r.Value.Refresh;
                token = r.Value.Access;
                v = await ValidateAsync(token);
            }
        }
        if (v == null) return null;
        s.UserLogin = v.Value.Login;
        s.UserId = v.Value.UserId;
        Settings.Save();
        return token;
    }

    // Every Twitch emote you can use (subs, follower emotes, bits, globals). Needs user:read:emotes.
    public static async Task<List<EmoteInfo>> GetUserEmotesAsync(string token)
    {
        var s = Settings.Current;
        var emotes = new List<EmoteInfo>();
        string? cursor = null;
        do
        {
            var url = $"https://api.twitch.tv/helix/chat/emotes/user?user_id={s.UserId}" + (cursor != null ? $"&after={cursor}" : "");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("Client-Id", s.TokenClientId ?? ClientId);
            using var resp = await Net.Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) { Log.Error($"Helix user emotes {(int)resp.StatusCode}"); break; }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var e in doc.RootElement.Arr("data"))
                if (e.Str("id") is { } id && e.Str("name") is { } name) emotes.Add(EmoteSources.Twitch(id, name));
            cursor = doc.RootElement.Obj("pagination").Str("cursor");
        } while (cursor != null && emotes.Count < 5000);
        return emotes.GroupBy(e => e.Name).Select(g => g.First()).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<List<string>> GetFollowedLoginsAsync(string token)
    {
        var s = Settings.Current;
        var logins = new List<string>();
        string? cursor = null;
        do
        {
            var url = $"https://api.twitch.tv/helix/channels/followed?user_id={s.UserId}&first=100" + (cursor != null ? $"&after={cursor}" : "");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("Client-Id", s.TokenClientId ?? ClientId);
            using var resp = await Net.Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) { Log.Error($"Helix followed {(int)resp.StatusCode}"); break; }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var f in doc.RootElement.Arr("data"))
                if (f.Str("broadcaster_login") is { } l) logins.Add(l);
            cursor = doc.RootElement.Obj("pagination").Str("cursor");
        } while (cursor != null && logins.Count < 2000);
        return logins;
    }

    public static void Logout()
    {
        var s = Settings.Current;
        s.AccessToken = null;
        s.RefreshToken = null;
        s.UserLogin = null;
        s.UserId = null;
        s.TokenClientId = null;
        Settings.Save();
    }
}
