using Glint;
using Glint.Chat;

void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS: {name}");
}
ChatMessage Message(string id, string login = "alice", string? uid = "1") => new() { Id = id, Login = login, UserId = uid };
IrcMessage Delete(string id) => new() { Command = "CLEARMSG", Tags = new() { ["target-msg-id"] = id } };

var source = new ChatChannel("channel");
await source.InitAsync("room");
source.Add(Message("loaded"));
source.Drain();
source.Add(Message("pending"));
source.Add(Message("other", "bob", "2"));
using var history = source.WatchUser(Message("selected"));
history.Channel.Drain();
Check(history.Channel.Messages.Select(m => m.Id).SequenceEqual(new[] { "loaded", "pending" }), "History includes loaded and pending messages from the selected author only");
Check(source.HasPending && source.Drain().Count == 2, "Opening history does not consume the main chat queue");
Check(!ReferenceEquals(source.Messages.First(m => m.Id == "loaded"), history.Channel.Messages[0]), "History owns independent message copies");
Check(!history.Channel.IsReplay && ReferenceEquals(history.Channel.Emotes, source.Emotes), "Live history preserves live reply eligibility and emotes");
source.Add(Message("background"));
history.Channel.Drain();
Check(history.Channel.Messages.Last().Id == "background" && source.HasPending, "New messages update history while the channel is in the background");
source.Add(Message("renamed", "alice_new", "1"));
source.Add(Message("impostor", "alice", "9"));
source.Add(Message("no-id", "ALICE", null));
history.Channel.Drain();
Check(history.Channel.Messages.Any(m => m.Id == "renamed") && history.Channel.Messages.All(m => m.Id != "impostor"), "Stable user IDs distinguish renamed accounts and matching display names");
Check(history.Channel.Messages.Any(m => m.Id == "no-id"), "Login matching is case-insensitive when user IDs are unavailable");
source.OnIrc(Delete("pending"));
history.Channel.Drain();
Check(history.Channel.Messages.Single(m => m.Id == "pending").Deleted, "Message deletions propagate to open history");
source.OnIrc(new() { Command = "CLEARCHAT", Trailing = "alice", Params = new() { "#channel", "alice" }, Tags = new() { ["target-user-id"] = "1" } });
history.Channel.Drain();
Check(history.Channel.Messages.Where(m => m.UserId == "1").All(m => m.Deleted), "Timeouts mark the user's history messages as deleted");
using var second = source.WatchUser(Message("selected", "bob", "2"));
second.Channel.Drain();
Check(second.Channel.Messages.Count == 1 && second.Channel.Messages[0].Id == "other", "Multiple user histories remain isolated");
source.Clear();
history.Channel.Drain();
Check(history.Channel.Messages.Count == 0, "Clearing or seeking the source clears open user history");
source.Add(Message("after-clear"));
history.Channel.Drain();
Check(history.Channel.Messages.Count == 1 && history.Channel.Messages[0].Id == "after-clear", "History keeps updating after a source clear");
history.Dispose();
source.Add(Message("after-close"));
Check(!history.Channel.HasPending, "Closing history unsubscribes from future messages");

var replay = new ChatChannel(source);
using var replayHistory = replay.WatchUser(Message("selected"));
Check(replayHistory.Channel.IsReplay, "VOD history remains read-only replay chat");

var concurrent = new ChatChannel("busy");
var started = new ManualResetEventSlim();
var producer = Task.Run(() =>
{
    for (int i = 0; i < 1000; i++)
    {
        concurrent.Add(Message(i.ToString()));
        if (i == 10) started.Set();
    }
});
started.Wait();
using var concurrentHistory = concurrent.WatchUser(Message("selected"));
await producer;
concurrentHistory.Channel.Drain();
Check(concurrentHistory.Channel.Messages.Count == 1000 && concurrentHistory.Channel.Messages.Select(m => m.Id).Distinct().Count() == 1000,
    "Opening history during incoming messages loses or duplicates none");
for (int i = 1000; i < 2000; i++) concurrent.Add(Message(i.ToString()));
concurrentHistory.Channel.Drain();
Check(concurrentHistory.Channel.Messages.Count == ChatChannel.MaxMessages && concurrentHistory.Channel.Messages[0].Id == "500", "User history retains a bounded buffer of the latest messages");

var moderated = new ChatChannel("moderated");
await moderated.InitAsync("room");
for (int i = 0; i < 1000; i++) moderated.Add(Message(i.ToString()));
using var moderatedHistory = moderated.WatchUser(Message("selected"));
moderatedHistory.Channel.Drain();
moderated.OnIrc(Delete("0"));
moderatedHistory.Channel.Drain();
Check(moderatedHistory.Channel.Messages[0].Deleted, "Deletions cover older retained history, beyond the latest 500 messages");
moderated.Clear();
moderated.Add(Message("replacement"));
using var afterClear = moderated.WatchUser(Message("selected"));
afterClear.Channel.Drain();
Check(afterClear.Channel.Messages.Count == 1 && afterClear.Channel.Messages[0].Id == "replacement", "Opening history respects source clears that are still pending");
