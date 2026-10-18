using System.Globalization;
using System.Text.RegularExpressions;
using Windows.UI;

namespace Glint.Chat;

public enum MessageKind { Normal, Action, System, UserNotice }

public abstract record Fragment;
public sealed record TextFragment(string Text, string? Link = null, bool IsMention = false) : Fragment;
public sealed record EmoteFragment(EmoteInfo Emote, List<EmoteInfo> Overlays) : Fragment;

public sealed record BadgeRef(AnimatedImage Image, string Title);

public sealed class ChatMessage
{
    public string? Id { get; init; }
    public string? UserId { get; init; }
    public string Login { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public Color NameColor { get; init; }
    public List<BadgeRef> Badges { get; init; } = new();
    public List<Fragment> Fragments { get; init; } = new();
    public MessageKind Kind { get; init; }
    public string? SystemText { get; init; }
    public string? ReplyName { get; init; }
    public string? ReplyBody { get; init; }
    public bool Highlighted { get; init; }
    public bool FirstMessage { get; init; }
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
    // Set for VOD replay messages: where in the video it was sent.
    public TimeSpan? ReplayOffset { get; init; }

    public bool Deleted { get; set; }

    // Render cache, owned by the UI thread.
    internal object? Layout;

    public string PlainText => string.Join(" ", Fragments.Select(f => f switch
    {
        TextFragment t => t.Text,
        EmoteFragment e => e.Emote.Name,
        _ => "",
    }));

    public static ChatMessage System(string text) => new()
    {
        Kind = MessageKind.System,
        SystemText = text,
    };
}

// Turns parsed IRC into render-ready messages. Thread-safe; runs on the socket thread.
public static class MessageBuilder
{
    static readonly Regex LinkRegex = new(
        @"^(?:(?:https?://)|(?:www\.))?[a-z0-9\-]+(?:\.[a-z0-9\-]+)*\.(?:[a-z]{2,24})(?::\d+)?(?:[/?#]\S*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Color[] DefaultColors =
    {
        Color.FromArgb(255, 255, 99, 99), Color.FromArgb(255, 99, 153, 255), Color.FromArgb(255, 72, 207, 106),
        Color.FromArgb(255, 216, 135, 255), Color.FromArgb(255, 255, 127, 80), Color.FromArgb(255, 154, 205, 50),
        Color.FromArgb(255, 255, 105, 180), Color.FromArgb(255, 30, 200, 200), Color.FromArgb(255, 218, 165, 32),
        Color.FromArgb(255, 95, 158, 160), Color.FromArgb(255, 180, 130, 255), Color.FromArgb(255, 0, 230, 160),
    };

    public static ChatMessage? FromIrc(IrcMessage m, ChatChannel ch)
    {
        string? text = m.Trailing;
        var kind = MessageKind.Normal;
        string? systemText = null;

        if (m.Command == "USERNOTICE")
        {
            kind = MessageKind.UserNotice;
            systemText = m.Tag("system-msg");
            if (m.Params.Count < 2) text = null;
        }
        else if (m.Command != "PRIVMSG") return null;

        if (text != null && text.StartsWith("\u0001ACTION ") && text.EndsWith('\u0001'))
        {
            text = text[8..^1];
            kind = MessageKind.Action;
        }

        var login = m.Tag("login") is { Length: > 0 } l ? l : m.Nick;
        var display = m.Tag("display-name") is { Length: > 0 } d ? d : login;
        var color = ParseColor(m.Tag("color"), login);

        var badges = new List<BadgeRef>();
        if (m.Tag("badges") is { Length: > 0 } badgeTag)
        {
            foreach (var b in badgeTag.Split(','))
                if (ch.FindBadge(b) is { } br) badges.Add(br);
        }

        var fragments = text != null ? Tokenize(text, m.Tag("emotes"), ch) : new List<Fragment>();
        var mention = text != null && IsMention(text, ch.SelfLogin);
        ch.NoteChatter(display);

        var replyName = m.Tag("reply-parent-display-name");
        var replyBody = m.Tag("reply-parent-msg-body");
        if (replyName.Length > 0 && text != null && text.StartsWith('@'))
        {
            // Twitch prefixes replies with "@name ", drop it since we render a reply line.
            if (fragments.Count > 0 && fragments[0] is TextFragment { IsMention: true }) fragments.RemoveAt(0);
        }

        DateTimeOffset time = DateTimeOffset.Now;
        if (long.TryParse(m.Tag("tmi-sent-ts"), out var ts))
            time = DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime();

        return new ChatMessage
        {
            Id = m.Tag("id"),
            UserId = m.Tag("user-id"),
            Login = login,
            DisplayName = display,
            NameColor = color,
            Badges = badges,
            Fragments = fragments,
            Kind = kind,
            SystemText = systemText,
            ReplyName = replyName.Length > 0 ? replyName : null,
            ReplyBody = replyBody.Length > 0 ? replyBody : null,
            Highlighted = mention,
            FirstMessage = m.Tag("first-msg") == "1",
            Time = time,
        };
    }

    public static ChatMessage Local(string text, ChatChannel ch, string login, string display, Color color, List<BadgeRef> badges) => new()
    {
        Login = login,
        DisplayName = display,
        NameColor = color,
        Badges = badges,
        Fragments = Tokenize(text.StartsWith("/me ") ? text[4..] : text, "", ch),
        Kind = text.StartsWith("/me ") ? MessageKind.Action : MessageKind.Normal,
    };

    public static ChatMessage FromComment(VodComment c, ChatChannel ch)
    {
        var fragments = new List<Fragment>();
        foreach (var f in c.Fragments)
        {
            if (f.EmoteId != null) fragments.Add(new EmoteFragment(EmoteSources.Twitch(f.EmoteId, f.Text.Trim()), new List<EmoteInfo>()));
            else AddWords(f.Text, ch, fragments);
        }
        var badges = c.Badges.Select(ch.FindBadge).OfType<BadgeRef>().ToList();
        return new ChatMessage
        {
            Id = c.Id,
            Login = c.Login,
            DisplayName = c.DisplayName,
            NameColor = ParseColor(c.Color ?? "", c.Login),
            Badges = badges,
            Fragments = fragments,
            ReplayOffset = TimeSpan.FromSeconds(c.Offset),
        };
    }

    static bool IsMention(string text, string? self)
    {
        if (string.IsNullOrEmpty(self)) return false;
        int i = 0;
        while ((i = text.IndexOf(self, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool startOk = i == 0 || !char.IsLetterOrDigit(text[i - 1]) && text[i - 1] != '_';
            int end = i + self.Length;
            bool endOk = end >= text.Length || !char.IsLetterOrDigit(text[end]) && text[end] != '_';
            if (startOk && endOk) return true;
            i = end;
        }
        return false;
    }

    static List<Fragment> Tokenize(string text, string emotesTag, ChatChannel ch)
    {
        // Twitch emote ranges are in Unicode code points; map them to UTF-16 indices.
        var twitch = new List<(int Start, int End, EmoteInfo Emote)>();
        if (!string.IsNullOrEmpty(emotesTag))
        {
            var cpToUtf16 = new List<int>(text.Length + 1);
            for (int i = 0; i < text.Length; i++)
            {
                cpToUtf16.Add(i);
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            }
            cpToUtf16.Add(text.Length);

            foreach (var group in emotesTag.Split('/'))
            {
                int colon = group.IndexOf(':');
                if (colon < 0) continue;
                var id = group[..colon];
                foreach (var range in group[(colon + 1)..].Split(','))
                {
                    var parts = range.Split('-');
                    if (parts.Length != 2 || !int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b)) continue;
                    if (a < 0 || b + 1 >= cpToUtf16.Count || a > b) continue;
                    int s = cpToUtf16[a], e = cpToUtf16[b + 1];
                    twitch.Add((s, e, EmoteSources.Twitch(id, text[s..e])));
                }
            }
            twitch.Sort((x, y) => x.Start.CompareTo(y.Start));
        }

        var result = new List<Fragment>();
        int pos = 0;
        foreach (var (start, end, emote) in twitch)
        {
            if (start < pos) continue;
            AddWords(text[pos..start], ch, result);
            result.Add(new EmoteFragment(emote, new List<EmoteInfo>()));
            pos = end;
        }
        AddWords(text[pos..], ch, result);
        return result;
    }

    static void AddWords(string segment, ChatChannel ch, List<Fragment> into)
    {
        foreach (var word in segment.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ch.Emotes.Find(word) is { } emote)
            {
                if (emote.ZeroWidth && into.Count > 0 && into[^1] is EmoteFragment prev)
                    prev.Overlays.Add(emote);
                else
                    into.Add(new EmoteFragment(emote, new List<EmoteInfo>()));
                continue;
            }
            if (word.Length > 4 && word.Contains('.') && LinkRegex.IsMatch(word))
            {
                var url = word.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? word : "https://" + word;
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                {
                    into.Add(new TextFragment(word, uri.AbsoluteUri));
                    continue;
                }
            }
            if (word.Length > 1 && word[0] == '@')
            {
                into.Add(new TextFragment(word, null, true));
                continue;
            }
            // Merge consecutive plain words into one fragment (fewer runs to lay out).
            if (into.Count > 0 && into[^1] is TextFragment { Link: null, IsMention: false } t)
                into[^1] = t with { Text = t.Text + " " + word };
            else
                into.Add(new TextFragment(word));
        }
    }

    public static Color ParseColor(string hex, string login)
    {
        Color c;
        if (hex.Length == 7 && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, null, out var v))
            c = Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        else
            c = DefaultColors[(int)((uint)StableHash(login) % DefaultColors.Length)];
        return Readable(c);
    }

    static int StableHash(string s)
    {
        unchecked
        {
            int h = 23;
            foreach (var ch in s) h = h * 31 + ch;
            return h;
        }
    }

    // Lightens colours that would be hard to read on the dark chat background.
    static Color Readable(Color c)
    {
        static double Lum(Color x) => (0.2126 * x.R + 0.7152 * x.G + 0.0722 * x.B) / 255.0;
        int guard = 0;
        while (Lum(c) < 0.42 && guard++ < 10)
            c = Color.FromArgb(255, (byte)(c.R + (255 - c.R) * 0.2), (byte)(c.G + (255 - c.G) * 0.2), (byte)(c.B + (255 - c.B) * 0.2));
        return c;
    }
}
