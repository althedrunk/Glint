using System.Text.Json;
using Windows.UI;

namespace Windows.UI
{
    public struct Color { public static Color FromArgb(byte a, byte r, byte g, byte b) => new(); }
}

namespace Glint
{
    public static class Settings { public static AppSettings Current { get; } = new(); }
    public sealed class AppSettings { public string? UserId { get; set; } public bool ShowBadges { get; set; } = true; }
    public static class Log { public static void Error(string text, Exception error) { } }
    public static class Net { public static Task<JsonDocument?> GetJsonAsync(string url) => Task.FromResult<JsonDocument?>(null); }
    public static class TwitchGql
    {
        public static Task<Dictionary<string, (string Url, string Title)>> GetBadgesAsync(string? login) => Task.FromResult(new Dictionary<string, (string, string)>());
    }
    public static class JsonExtensions
    {
        public static IEnumerable<JsonElement> Arr(this JsonElement element, string key) => Array.Empty<JsonElement>();
        public static string? Str(this JsonElement element, string key) => null;
    }
    public sealed class IrcMessage
    {
        public string Command { get; init; } = "";
        public string Nick { get; init; } = "";
        public string? Trailing { get; init; }
        public List<string> Params { get; init; } = new();
        public Dictionary<string, string> Tags { get; init; } = new();
        public string Tag(string key) => Tags.GetValueOrDefault(key, "");
        public static IrcMessage? Parse(string text) => null;
    }
    public sealed class VodComment { }
}

namespace Glint.Chat
{
    public enum MessageKind { Normal, Action, System, UserNotice }
    public sealed class ChatMessage
    {
        public string? Id { get; init; }
        public string? UserId { get; init; }
        public string Login { get; init; } = "";
        public MessageKind Kind { get; init; }
        public bool Deleted { get; set; }
        public bool Highlighted { get; init; }
        public string? SystemText { get; init; }
        public List<Fragment> Fragments { get; init; } = new();
        public ChatMessage CopyForView() => (ChatMessage)MemberwiseClone();
        public static ChatMessage System(string text) => new() { Kind = MessageKind.System, SystemText = text };
    }
    public abstract record Fragment;
    public sealed record TextFragment(string Text) : Fragment;
    public sealed record EmoteFragment(EmoteInfo Emote, List<EmoteInfo> Overlays) : Fragment;
    public sealed record BadgeRef(AnimatedImage Image, string Title);
    public sealed class AnimatedImage { }
    public sealed class EmoteInfo { public string Name { get; init; } = ""; }
    public static class ImageCache { public static AnimatedImage Get(string url) => new(); }
    public static class EmoteSources { public static EmoteInfo? FromSevenTv(JsonElement element, bool global) => null; }
    public sealed class ChannelEmotes
    {
        public int Count => 0;
        public Task LoadAsync(string roomId) => Task.CompletedTask;
        public EmoteInfo? SevenTvRemove(string id) => null;
        public void SevenTvAdd(EmoteInfo info) { }
    }
    public static class MessageBuilder
    {
        public static ChatMessage? FromIrc(IrcMessage message, ChatChannel channel) => null;
        public static Color ParseColor(string color, string login) => new();
        public static ChatMessage Local(string text, ChatChannel channel, string login, string name, Color color, List<BadgeRef> badges, string? uid, ChatMessage? reply) => new();
        public static ChatMessage FromComment(VodComment comment, ChatChannel channel) => new();
    }
}
