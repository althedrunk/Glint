using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glint;

// Reports "minute watched" for live streams you're actually watching, the same event twitch.tv sends once a
// minute. That's what Twitch counts for watch streaks and passive channel points. Only used with a
// connected twitch.tv session.
public static class WatchTime
{
    const string FallbackSpadeUrl = "https://spade.twitch.tv/track";
    const string BrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    static string? _spadeUrl;
    static DateTime _spadeFetched;
    static readonly Dictionary<string, (string BroadcastId, DateTime At)> Broadcasts = new();
    static readonly HashSet<string> Reported = new();

    public static async Task ReportAsync(string login, string channelId)
    {
        if (Settings.Current.WebSessionUserId is not { } userId) return;
        try
        {
            var broadcastId = await BroadcastIdAsync(login);
            if (broadcastId == null) return; // not live any more
            var payload = JsonSerializer.Serialize(new[]
            {
                new
                {
                    @event = "minute-watched",
                    properties = new { channel_id = channelId, broadcast_id = broadcastId, player = "site", user_id = userId, live = true, channel = login },
                },
            });
            var url = await SpadeUrlAsync();
            HttpResponseMessage resp;
            try { resp = await PostAsync(url, payload); }
            // Spade drops idle keep-alive connections at about the minute mark, so the reused one can die mid-request.
            catch (HttpRequestException) { resp = await PostAsync(url, payload); }
            using var _ = resp;
            if (!resp.IsSuccessStatusCode) Log.Error($"Watch time for {login}: HTTP {(int)resp.StatusCode}");
            else if (Reported.Add(login)) Log.Info($"Counting watch time for {login}");
        }
        catch (Exception ex) { Log.Error($"Watch time for {login}", ex); }
    }

    static Task<HttpResponseMessage> PostAsync(string url, string payload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) }),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
        return Net.Http.SendAsync(req);
    }

    static async Task<string?> BroadcastIdAsync(string login)
    {
        if (Broadcasts.TryGetValue(login, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(10)) return cached.BroadcastId;
        var data = await TwitchGql.RawQueryAsync("query($l: String!) { user(login: $l) { stream { id } } }", new { l = login });
        var id = data?.Obj("user").Obj("stream").Str("id");
        if (id != null) Broadcasts[login] = (id, DateTime.UtcNow);
        else Broadcasts.Remove(login);
        return id;
    }

    // twitch.tv reads this from its settings script; it has been the same address for years, hence the fallback.
    static async Task<string> SpadeUrlAsync()
    {
        if (_spadeUrl != null && DateTime.UtcNow - _spadeFetched < TimeSpan.FromHours(6)) return _spadeUrl;
        _spadeFetched = DateTime.UtcNow;
        try
        {
            using var page = new HttpRequestMessage(HttpMethod.Get, "https://www.twitch.tv/");
            page.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
            using var pageResp = await Net.Http.SendAsync(page);
            var html = await pageResp.Content.ReadAsStringAsync();
            var settings = Regex.Match(html, @"https://(?:static\.twitchcdn\.net|assets\.twitch\.tv)/config/settings[^""]*?\.js");
            if (settings.Success)
            {
                var js = await Net.Http.GetStringAsync(settings.Value);
                var spade = Regex.Match(js, @"""spade_url"":""([^""]+)""");
                if (spade.Success) return _spadeUrl = spade.Groups[1].Value;
            }
        }
        catch (Exception ex) { Log.Error("Spade URL", ex); }
        return _spadeUrl = FallbackSpadeUrl;
    }
}
