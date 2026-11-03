using System.Text.Json;

namespace Glint;

public sealed record PredictionOutcome(string Id, string Title, string Color, long Points, int Users);

public sealed record Prediction(string Id, string Title, string Status, DateTimeOffset ClosesAt, List<PredictionOutcome> Outcomes,
    string? MyOutcomeId, long MyPoints);

public sealed record PollChoice(string Id, string Title, int Votes);

public sealed record Poll(string Id, string Title, string Status, DateTimeOffset EndsAt, List<PollChoice> Choices);

public sealed record Engagement(Prediction? Prediction, Poll? Poll, long? Balance);

// Channel-point predictions and polls, as twitch.tv shows and places them. Needs the twitch.tv session.
public static partial class TwitchGql
{
    public static async Task<Engagement> GetEngagementAsync(string login, CancellationToken ct = default)
    {
        const string events = "id title status createdAt predictionWindowSeconds outcomes { id title color totalPoints totalUsers } self { prediction { points outcome { id } } }";
        string q = $$"""
            query($login: String!) {
              channel(name: $login) {
                self { communityPoints { balance } }
                activePredictionEvents { {{events}} }
                lockedPredictionEvents { {{events}} }
              }
              user(login: $login) { viewablePoll { id title status startedAt durationSeconds remainingDurationMilliseconds choices { id title votes { total } } } }
            }
            """;
        var data = await QueryAsync(q, new { login = login.ToLowerInvariant() }, ct, session: true);
        var channel = data?.Obj("channel") ?? default;
        long? balance = channel.Obj("self").Obj("communityPoints") is { ValueKind: JsonValueKind.Object } cp && cp.TryGetProperty("balance", out var b)
            && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : null;
        var ev = channel.Arr("activePredictionEvents").Concat(channel.Arr("lockedPredictionEvents")).FirstOrDefault();
        return new Engagement(ev.ValueKind == JsonValueKind.Object ? ReadPrediction(ev) : null, ReadPoll(data?.Obj("user").Obj("viewablePoll") ?? default), balance);
    }

    static Prediction ReadPrediction(JsonElement e)
    {
        DateTimeOffset.TryParse(e.Str("createdAt"), out var created);
        var mine = e.Obj("self").Obj("prediction");
        long Long(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        return new Prediction(e.Str("id") ?? "", e.Str("title") ?? "", e.Str("status") ?? "",
            created.AddSeconds(e.Int("predictionWindowSeconds")),
            e.Arr("outcomes").Select(o => new PredictionOutcome(o.Str("id") ?? "", o.Str("title") ?? "", o.Str("color") ?? "BLUE",
                Long(o, "totalPoints"), o.Int("totalUsers"))).ToList(),
            mine.Obj("outcome").Str("id"), Long(mine, "points"));
    }

    static Poll? ReadPoll(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || p.Str("id") is not { } id) return null;
        double left = p.TryGetProperty("remainingDurationMilliseconds", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : 0;
        var ends = DateTimeOffset.TryParse(p.Str("startedAt"), out var started) && p.Int("durationSeconds") > 0
            ? started.AddSeconds(p.Int("durationSeconds")) : DateTimeOffset.UtcNow.AddMilliseconds(left);
        return new Poll(id, p.Str("title") ?? "", p.Str("status") ?? "", ends,
            p.Arr("choices").Select(c => new PollChoice(c.Str("id") ?? "", c.Str("title") ?? "", c.Obj("votes").Int("total"))).ToList());
    }

    // Returns Twitch's error code, or null when the prediction went in.
    public static async Task<string?> MakePredictionAsync(string eventId, string outcomeId, long points)
    {
        const string q = "mutation($in: MakePredictionInput!) { makePrediction(input: $in) { error { code } } }";
        var data = await QueryAsync(q, new { @in = new { eventID = eventId, outcomeID = outcomeId, points, transactionID = Guid.NewGuid().ToString("N") } }, session: true);
        if (data == null) return "NETWORK";
        return data.Value.Obj("makePrediction").Obj("error").Str("code");
    }

    public static async Task<string?> VoteInPollAsync(string pollId, string choiceId, string userId)
    {
        const string q = "mutation($in: VoteInPollInput!) { voteInPoll(input: $in) { error { code } } }";
        var data = await QueryAsync(q, new { @in = new { pollID = pollId, choiceID = choiceId, userID = userId, voteID = Guid.NewGuid().ToString() } }, session: true);
        if (data == null) return "NETWORK";
        return data.Value.Obj("voteInPoll").Obj("error").Str("code");
    }
}
