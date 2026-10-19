using System.Collections.Concurrent;
using System.Text.Json;
using Windows.UI;

namespace Glint.Chat;

// State for one open channel: emotes, badges, and its message buffer. IRC arrives on a worker thread and
// is queued; the UI thread drains the queue once per frame (see Drain), so a busy chat costs
// one UI update per frame rather than one per message.
public sealed class ChatChannel
{
    public const int MaxMessages = 1500;

    public string Login { get; }
    public string? RoomId { get; private set; }
    public ChannelEmotes Emotes { get; }
    public List<ChatMessage> Messages { get; } = new();
    public string? SelfLogin { get; set; }

    readonly ConcurrentQueue<object> _incoming = new();
    readonly object _historyLock = new();
    readonly List<UserHistory> _userHistories = new();
    readonly List<IrcMessage> _beforeReady = new();
    readonly HashSet<string> _chatters = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, (string Url, string Title)> _channelBadges = new();
    volatile bool _ready;

    // Our own USERSTATE, used to render locally-echoed messages.
    public Color SelfColor { get; private set; } = Color.FromArgb(255, 169, 112, 255);
    public string? SelfDisplayName { get; private set; }
    public bool SelfIsMod { get; private set; }

    // Room modes from ROOMSTATE. FollowersOnly is in minutes, -1 when off.
    public bool EmoteOnly { get; private set; }
    public bool SubsOnly { get; private set; }
    public int FollowersOnly { get; private set; } = -1;
    public int SlowSeconds { get; private set; }
    public bool UniqueChat { get; private set; }
    public event Action<ChatChannel>? ModesChanged;

    // Short summary for the chat box, e.g. "emote only, slow 30s".
    public string ModesText
    {
        get
        {
            var modes = new List<string>();
            if (EmoteOnly) modes.Add("emote only");
            if (SubsOnly) modes.Add("subscribers only");
            if (FollowersOnly == 0) modes.Add("followers only");
            else if (FollowersOnly > 0) modes.Add($"followers only ({FormatDuration(FollowersOnly * 60)})");
            if (SlowSeconds > 0) modes.Add($"slow {FormatDuration(SlowSeconds)}");
            if (UniqueChat) modes.Add("unique chat");
            return string.Join(", ", modes);
        }
    }

    // Your last sent message, until Twitch has had time to reject it.
    ChatMessage? _lastEcho;
    DateTime _lastEchoAt;
    List<BadgeRef> _selfBadges = new();

    static Task<Dictionary<string, (string Url, string Title)>>? _globalBadges;
    static Dictionary<string, (string Url, string Title)> GlobalBadges =>
        _globalBadges is { IsCompletedSuccessfully: true } t ? t.Result : new();

    // Raised on a worker thread whenever something is queued for the UI.
    public event Action<ChatChannel>? Updated;

    // A live (not backfilled) message that mentions you or matches a highlight word. Worker thread.
    public event Action<ChatChannel, ChatMessage>? Mentioned;

    public ChatChannel(string login)
    {
        Login = login.ToLowerInvariant();
        Emotes = new ChannelEmotes();
    }

    // A VOD chat replay that reuses a live channel's emotes and badges.
    public ChatChannel(ChatChannel live) : this(live, true) { }

    ChatChannel(ChatChannel live, bool isReplay)
    {
        Login = live.Login;
        RoomId = live.RoomId;
        Emotes = live.Emotes;
        _channelBadges = live._channelBadges;
        _ready = true;
        IsReplay = isReplay;
    }

    public bool IsReplay { get; }

    // UI thread: include the buffer and pending messages without draining the main view's queue.
    public UserHistory WatchUser(ChatMessage user)
    {
        lock (_historyLock)
        {
            var history = new UserHistory(this, user);
            foreach (var message in Messages) history.Forward(message);
            foreach (var item in _incoming) history.Forward(item);
            _userHistories.Add(history);
            return history;
        }
    }

    public sealed class UserHistory : IDisposable
    {
        readonly ChatChannel _source;
        readonly string? _userId;
        readonly string _login;
        public ChatChannel Channel { get; }

        internal UserHistory(ChatChannel source, ChatMessage user)
        {
            _source = source;
            _userId = user.UserId;
            _login = user.Login;
            Channel = new ChatChannel(source, source.IsReplay);
        }

        internal void Forward(object item)
        {
            if (item is ChatMessage message)
            {
                if (message.Kind == MessageKind.System) return;
                bool match = !string.IsNullOrEmpty(_userId) && !string.IsNullOrEmpty(message.UserId)
                    ? message.UserId == _userId
                    : _login.Length > 0 && message.Login.Equals(_login, StringComparison.OrdinalIgnoreCase);
                if (match) Channel.Enqueue(message.CopyForView());
            }
            else Channel.Enqueue(item);
        }

        public void Dispose()
        {
            lock (_source._historyLock) _source._userHistories.Remove(this);
        }
    }

    public async Task InitAsync(string roomId)
    {
        RoomId = roomId;
        _globalBadges ??= TwitchGql.GetBadgesAsync(null);
        var badges = TwitchGql.GetBadgesAsync(Login);
        var emotes = Emotes.LoadAsync(roomId);
        var recent = LoadRecentAsync();
        try { await Task.WhenAll(_globalBadges, badges, emotes); }
        catch (Exception ex) { Log.Error($"Init {Login}", ex); }
        if (badges.IsCompletedSuccessfully) _channelBadges = badges.Result;

        var history = await recent;
        foreach (var line in history)
            if (IrcMessage.Parse(line) is { } m) Process(m, historical: true);

        lock (_beforeReady)
        {
            foreach (var m in _beforeReady) Process(m);
            _beforeReady.Clear();
            _ready = true;
        }
        Enqueue(ChatMessage.System(Emotes.Count > 0
            ? $"Joined #{Login} ({Emotes.Count} 7TV/BTTV/FFZ emotes)"
            : $"Joined #{Login}"));
    }

    async Task<List<string>> LoadRecentAsync()
    {
        try
        {
            using var doc = await Net.GetJsonAsync($"https://recent-messages.robotty.de/api/v2/recent-messages/{Login}?limit=150");
            return doc?.RootElement.Arr("messages").Select(e => e.GetString() ?? "").ToList() ?? new();
        }
        catch { return new(); }
    }

    public void OnIrc(IrcMessage m)
    {
        if (!_ready)
        {
            lock (_beforeReady)
            {
                if (!_ready) { _beforeReady.Add(m); return; }
            }
        }
        Process(m);
    }

    void Process(IrcMessage m, bool historical = false)
    {
        switch (m.Command)
        {
            case "PRIVMSG":
            case "USERNOTICE":
                if (MessageBuilder.FromIrc(m, this) is { } msg)
                {
                    Enqueue(msg);
                    if (!historical && !IsReplay && msg.Highlighted) Mentioned?.Invoke(this, msg);
                }
                break;
            case "CLEARMSG":
                Enqueue(new DeleteOp(m.Tag("target-msg-id"), null));
                break;
            case "CLEARCHAT":
                var target = m.Trailing;
                if (m.Params.Count < 2 || string.IsNullOrEmpty(target))
                {
                    if (!historical) Enqueue(ChatMessage.System("Chat was cleared by a moderator"));
                    break;
                }
                Enqueue(new DeleteOp(null, m.Tag("target-user-id")));
                var dur = m.Tag("ban-duration");
                Enqueue(ChatMessage.System(dur.Length > 0
                    ? $"{target} has been timed out for {FormatDuration(int.Parse(dur))}"
                    : $"{target} has been permanently banned"));
                break;
            case "USERSTATE":
                SelfColor = MessageBuilder.ParseColor(m.Tag("color"), SelfLogin ?? "");
                SelfDisplayName = m.Tag("display-name");
                SelfIsMod = m.Tag("badges").Split(',').Any(b => b.StartsWith("moderator/") || b.StartsWith("broadcaster/"));
                _selfBadges = m.Tag("badges").Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(FindBadge).OfType<BadgeRef>().ToList();
                break;
            case "NOTICE":
                if (historical) break;
                // msg_* notices are Twitch refusing a message you just sent (emote only, slow mode, ...).
                if (m.Tag("msg-id").StartsWith("msg_") && _lastEcho is { } echo && DateTime.UtcNow - _lastEchoAt < TimeSpan.FromSeconds(5))
                {
                    Enqueue(new RejectOp(echo));
                    _lastEcho = null;
                }
                if (m.Trailing is { Length: > 0 } notice) Enqueue(ChatMessage.System(notice));
                break;
            case "ROOMSTATE":
                if (RoomId == null && m.Tag("room-id") is { Length: > 0 } id) RoomId = id;
                // A full ROOMSTATE on join, then only the tags that changed.
                if (m.Tags.TryGetValue("emote-only", out var v)) EmoteOnly = v == "1";
                if (m.Tags.TryGetValue("subs-only", out v)) SubsOnly = v == "1";
                if (m.Tags.TryGetValue("followers-only", out v) && int.TryParse(v, out var mins)) FollowersOnly = mins;
                if (m.Tags.TryGetValue("slow", out v) && int.TryParse(v, out var secs)) SlowSeconds = secs;
                if (m.Tags.TryGetValue("r9k", out v)) UniqueChat = v == "1";
                ModesChanged?.Invoke(this);
                break;
        }
    }

    static string FormatDuration(int s) => s switch
    {
        < 60 => $"{s}s",
        < 3600 => $"{s / 60}m",
        < 86400 => $"{s / 3600}h",
        _ => $"{s / 86400}d",
    };

    public void AddLocalEcho(string text, ChatMessage? replyTo = null, IReadOnlyDictionary<string, EmoteInfo>? ownEmotes = null)
    {
        var login = SelfLogin ?? "";
        var echo = MessageBuilder.Local(text, this, login, SelfDisplayName ?? login, SelfColor, _selfBadges, Settings.Current.UserId, replyTo, ownEmotes);
        _lastEcho = echo;
        _lastEchoAt = DateTime.UtcNow;
        Enqueue(echo);
    }

    // Adds a ready-made message (used by the thread and mentions views).
    public void Add(ChatMessage message) => Enqueue(message);

    public ChatMessage? FindMessage(string id)
    {
        for (int i = Messages.Count - 1; i >= 0; i--)
            if (Messages[i].Id == id) return Messages[i];
        return null;
    }

    public void AddSystem(string text) => Enqueue(ChatMessage.System(text));

    public void AddComment(VodComment c) => Enqueue(MessageBuilder.FromComment(c, this));

    public void Clear() => Enqueue(ClearOp.Instance);

    public BadgeRef? FindBadge(string key)
    {
        if (_channelBadges.TryGetValue(key, out var b) || GlobalBadges.TryGetValue(key, out b))
            return new BadgeRef(ImageCache.Get(b.Url), b.Title);
        return null;
    }

    public void NoteChatter(string name)
    {
        lock (_chatters)
        {
            if (_chatters.Count > 5000) _chatters.Clear();
            _chatters.Add(name);
        }
    }

    public List<string> Chatters()
    {
        lock (_chatters) return _chatters.ToList();
    }

    void Enqueue(object item)
    {
        lock (_historyLock)
        {
            _incoming.Enqueue(item);
            foreach (var history in _userHistories) history.Forward(item);
        }
        Updated?.Invoke(this);
    }

    public bool HasPending => !_incoming.IsEmpty;

    // UI thread: applies queued messages / deletions. Returns newly added messages.
    public List<ChatMessage> Drain()
    {
        var added = new List<ChatMessage>();
        while (_incoming.TryDequeue(out var item))
        {
            switch (item)
            {
                case ChatMessage msg:
                    Messages.Add(msg);
                    added.Add(msg);
                    break;
                case DeleteOp { MessageId: { } id }:
                    if (FindMessage(id) is { } deleted) deleted.Deleted = true;
                    break;
                case RejectOp { Message: var rejected }:
                    rejected.Deleted = true;
                    break;
                case ClearOp:
                    Messages.Clear();
                    added.Clear();
                    break;
                case DeleteOp { UserId: { } uid }:
                    foreach (var msg in Messages)
                        if (msg.UserId == uid) msg.Deleted = true;
                    break;
            }
        }
        if (Messages.Count > MaxMessages) Messages.RemoveRange(0, Messages.Count - MaxMessages);
        return added;
    }

    // 7TV EventAPI update for this channel's emote set.
    public void OnSevenTvChange(string? actor, JsonElement? added, JsonElement? removed, JsonElement? oldValue)
    {
        string who = actor ?? "Someone";
        if (oldValue is { } old && added is { } renamed)
        {
            Emotes.SevenTvRemove(old.Str("id") ?? "");
            if (EmoteSources.FromSevenTv(renamed, false) is { } e)
            {
                Emotes.SevenTvAdd(e);
                Enqueue(ChatMessage.System($"{who} renamed 7TV emote {old.Str("name")} to {e.Name}"));
            }
        }
        else if (added is { } a && EmoteSources.FromSevenTv(a, false) is { } e)
        {
            Emotes.SevenTvAdd(e);
            Enqueue(new ChatMessage
            {
                Kind = MessageKind.System,
                SystemText = $"{who} added 7TV emote",
                Fragments = new() { new EmoteFragment(e, new()), new TextFragment(e.Name) },
            });
        }
        else if (removed is { } r && Emotes.SevenTvRemove(r.Str("id") ?? "") is { } gone)
        {
            Enqueue(ChatMessage.System($"{who} removed 7TV emote {gone.Name}"));
        }
    }

    sealed record DeleteOp(string? MessageId, string? UserId);

    sealed record RejectOp(ChatMessage Message);

    sealed class ClearOp
    {
        public static readonly ClearOp Instance = new();
    }
}
