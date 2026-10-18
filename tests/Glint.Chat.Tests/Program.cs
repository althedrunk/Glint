using Glint;
using Glint.Chat;

void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS: {name}");
}

Check(ChatFilters.Matches("A SPOILER!", new[] { "spoiler" }), "Ignore words match case-insensitively at punctuation");
Check(!ChatFilters.Matches("spoilers and spoiler_free", new[] { "spoiler" }), "Partial words and username fragments are not hidden");
Check(ChatFilters.Matches("A bad game, sadly", new[] { "bad game" }), "Multiword phrases match");
Check(!ChatFilters.Matches("bad games", new[] { "bad game" }), "Phrases respect word endings");
Check(ChatFilters.Matches("café!", new[] { "CAFÉ" }), "Unicode words match");
Check(!ChatFilters.Matches("caféine", new[] { "café" }), "Unicode word boundaries are respected");
Check(!ChatFilters.Matches("anything", new[] { "", " " }), "Blank ignore entries are harmless");
Check(ChatFilters.Matches("catapult then cat", new[] { "cat" }), "A partial occurrence does not hide a later full match");
Check(ChatFilters.ParseWords("one\r\ntwo, ONE\n bad game ").SequenceEqual(new[] { "one", "two", "bad game" }), "Lines and commas are parsed, trimmed and deduplicated");
Settings.Current.IgnoredWords = new() { "spoiler" };
Check(ChatFilters.IsIgnored(new() { PlainText = "a spoiler", Kind = MessageKind.Normal }), "Regular messages are filtered");
Check(ChatFilters.IsIgnored(new() { PlainText = "a spoiler", Kind = MessageKind.Action }), "Action messages are filtered");
Check(ChatFilters.IsIgnored(new() { PlainText = "a spoiler", Kind = MessageKind.UserNotice }), "Announcement message bodies are filtered");
Check(!ChatFilters.IsIgnored(new() { PlainText = "a spoiler", Kind = MessageKind.System }), "System notices remain visible");
Settings.Current.IgnoredWords = new();
Check(!ChatFilters.IsIgnored(new() { PlainText = "a spoiler" }), "Removing an ignore word restores matching messages");
Settings.Current.HighlightWords = new() { "spoiler" };
Check(Highlights.Check("viewer", "a spoiler", "self") == HighlightKind.Keyword, "Highlight matching still works independently");
Settings.Current.HighlightUsers = new() { "@Viewer" };
Check(Highlights.Check("viewer", "hello", "self") == HighlightKind.User, "Highlighted usernames support an optional at sign");
Settings.Current.IgnoredUsers = new() { "@Viewer" };
Check(ChatFilters.IsIgnored(new() { Login = "viewer", PlainText = "hello" }), "Ignored usernames match case-insensitively with an optional at sign");
Check(!ChatFilters.IsIgnored(new() { Login = "viewers", PlainText = "hello" }), "Ignored usernames match the whole login");
Settings.Current.IgnoredUsers.Clear();
Settings.Current.ShowAnnouncements = false;
Check(ChatFilters.IsIgnored(new() { Kind = MessageKind.UserNotice, NoticeId = "announcement" }), "Announcements can be hidden");
Settings.Current.ShowSubscriptionNotices = false;
Check(ChatFilters.IsIgnored(new() { Kind = MessageKind.UserNotice, NoticeId = "subgift" }), "Gift subscription notices can be hidden");
Check(!ChatFilters.IsIgnored(new() { Kind = MessageKind.UserNotice, NoticeId = "raid" }), "Hiding subscriptions keeps other notices visible");
Settings.Current.ShowSubscriptionNotices = true;
Check(!ChatFilters.IsIgnored(new() { Kind = MessageKind.UserNotice, NoticeId = "subgift" }), "Turning notices back on restores loaded messages");

Check(EmoteCompletion.TryGetQuery(":pe", 3, 0, out var query) && query == new EmoteQuery(0, 3, "pe"), "Colon completion starts after two characters");
Check(!EmoteCompletion.TryGetQuery(":p", 2, 0, out _) && !EmoteCompletion.TryGetQuery("", 0, 0, out _), "Short and empty input do not open suggestions");
Check(!EmoteCompletion.TryGetQuery("https://example", 10, 0, out _) && !EmoteCompletion.TryGetQuery("12:30", 5, 0, out _), "URLs and times are not treated as emote searches");
Check(!EmoteCompletion.TryGetQuery(":pe", 3, 2, out _) && !EmoteCompletion.TryGetQuery(":pe", 20, 0, out _), "Selected text and invalid caret positions are ignored");
Check(EmoteCompletion.TryGetQuery("hi\t:pe there", 6, 0, out query) && query.Start == 3, "Colon completion recognises whitespace boundaries");
Check(EmoteCompletion.Insert("hi\t:pe there", query, "PepeLaugh") == ("hi\tPepeLaugh there", 13), "Insertion preserves surrounding text and reuses an existing space");
Check(EmoteCompletion.TryGetQuery(":Kappa", 3, 0, out query) && query.End == 6 &&
    EmoteCompletion.Insert(":Kappa", query, "Kappa") == ("Kappa ", 6), "Completing inside a token replaces its suffix and adds one trailing space");
Check(EmoteCompletion.TryGetQuery(":Kappa:", 7, 0, out query) && EmoteCompletion.Insert(":Kappa:", query, "Kappa").Text == "Kappa ", "Optional closing colons are removed when inserting");
Check(EmoteCompletion.TryGetQuery(":Kappa, next", 3, 0, out query) && EmoteCompletion.Insert(":Kappa, next", query, "Kappa").Text == "Kappa, next", "Completing a token preserves following punctuation");
EmoteInfo Emote(string name, EmoteProvider provider = EmoteProvider.SevenTV, bool global = false) => new() { Name = name, Provider = provider, IsGlobal = global };
var channel = Emote("pepeChannel");
var own = Emote("pepeTwitch", EmoteProvider.Twitch);
var global = Emote("pepeGlobal", global: true);
var matches = EmoteCompletion.Find("pe", [global, own, channel, Emote("unrelated"), Emote("pepeChannel", global: true)]);
Check(matches.SequenceEqual([channel, own, global]), "Channel and user emotes precede globals and duplicate names keep the right source");
Check(EmoteCompletion.Find("PE", [Emote("Pepe"), Emote("pepe")]).Count == 2, "Search ignores case while preserving distinct case-sensitive emote names");
Check(EmoteCompletion.Find("pe", [Emote("xpe"), Emote("peLong")])[0].Name == "peLong", "Prefix matches rank above substring matches in the same source");
Check(EmoteCompletion.Find("pe", Enumerable.Range(0, 100).Select(i => Emote("pe" + i))).Count == 50, "Suggestion lists have a bounded result count");

namespace Glint
{
    public enum EmoteProvider { Twitch, SevenTV, BTTV, FFZ }
    public sealed class EmoteInfo
    {
        public string Name { get; init; } = "";
        public EmoteProvider Provider { get; init; }
        public bool IsGlobal { get; init; }
    }
    public sealed class AppSettings
    {
        public List<string> IgnoredWords { get; set; } = new();
        public List<string> HighlightWords { get; set; } = new();
        public List<string> HighlightUsers { get; set; } = new();
        public List<string> IgnoredUsers { get; set; } = new();
        public bool ShowAnnouncements { get; set; } = true;
        public bool ShowSubscriptionNotices { get; set; } = true;
    }
    public static class Settings { public static AppSettings Current { get; } = new(); }
}

namespace Glint.Chat
{
    public enum MessageKind { Normal, Action, System, UserNotice }
    public sealed class ChatMessage
    {
        public MessageKind Kind { get; init; }
        public string PlainText { get; init; } = "";
        public string Login { get; init; } = "";
        public string? NoticeId { get; init; }
    }
}
