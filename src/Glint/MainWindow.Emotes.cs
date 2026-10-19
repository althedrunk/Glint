using Glint.Chat;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Glint;

// Emote picker next to the chat box.
public sealed partial class MainWindow
{
    List<EmoteInfo>? _userEmotes; // your own Twitch emotes (subs, follower, bits), once logged in
    Task<List<EmoteInfo>>? _userEmotesTask;

    void ResetUserEmotes()
    {
        _userEmotes = null;
        _userEmotesTask = null;
        HideEmoteSuggestions();
    }

    async Task<List<EmoteInfo>> EnsureUserEmotesAsync()
    {
        if (_userEmotes != null) return _userEmotes;
        if (!_irc.IsAuthenticated) return new();
        var task = _userEmotesTask ??= TwitchSession.IsConnected ? TwitchSession.GetUserEmotesAsync()
            : S.AccessToken is { } token ? TwitchAuth.GetUserEmotesAsync(token) : Task.FromResult(new List<EmoteInfo>());
        List<EmoteInfo> emotes;
        try { emotes = await task; }
        catch (Exception ex) { Log.Error("User emotes", ex); emotes = new(); }
        if (_userEmotesTask != task) return new();
        return _userEmotes = emotes;
    }

    Dictionary<string, EmoteInfo>? _ownEmotesByName;
    List<EmoteInfo>? _ownEmotesFrom;

    async Task<IReadOnlyDictionary<string, EmoteInfo>> OwnEmotesAsync()
    {
        var emotes = await EnsureUserEmotesAsync();
        if (_ownEmotesFrom != emotes)
        {
            _ownEmotesByName = new();
            foreach (var e in emotes) _ownEmotesByName.TryAdd(e.Name, e);
            _ownEmotesFrom = emotes;
        }
        return _ownEmotesByName!;
    }

    void WireEmotePicker() => EmoteButton.Click += (_, _) => ShowEmotePicker();

    async void ShowEmotePicker()
    {
        if (ChatTarget is not { } tab) return;
        var picker = new EmotePicker();
        var flyout = new Flyout { Content = picker, Placement = FlyoutPlacementMode.TopEdgeAlignedRight };
        bool closed = false;
        picker.EmoteChosen += e => InsertEmote(e.Name);
        flyout.Closed += (_, _) => { closed = true; picker.Dispose(); };
        TrackVideoPopup(flyout);
        picker.SetSections(EmoteSections(tab));
        flyout.ShowAt(EmoteButton);

        if (_userEmotes == null && _irc.IsAuthenticated)
        {
            await EnsureUserEmotesAsync();
            if (!closed && _tabs.Contains(tab)) picker.SetSections(EmoteSections(tab));
        }
    }

    List<(string Title, List<EmoteInfo> Emotes)> EmoteSections(ChannelTab tab)
    {
        var sources = tab.Chat.Emotes.Sections();
        var twitch = _userEmotes ?? new();
        var byName = new Dictionary<string, EmoteInfo>();
        foreach (var e in sources.SelectMany(s => s.Emotes).Concat(twitch)) byName.TryAdd(e.Name, e);

        var sections = new List<(string, List<EmoteInfo>)>
        {
            ("Recent", S.RecentEmotes.Select(n => byName.GetValueOrDefault(n)).OfType<EmoteInfo>().ToList()),
        };
        sections.AddRange(sources.Take(3)); // this channel's 7TV / BTTV / FFZ
        sections.Add(("Twitch", twitch));
        sections.AddRange(sources.Skip(3)); // globals
        return sections;
    }

    void InsertEmote(string name)
    {
        if (!ChatInput.IsEnabled) return;
        var text = ChatInput.Text;
        int caret = Math.Clamp(ChatInput.SelectionStart, 0, text.Length);
        var before = text[..caret];
        var insert = (before.Length > 0 && !before.EndsWith(' ') ? " " : "") + name + " ";
        ChatInput.Text = before + insert + text[caret..];
        ChatInput.SelectionStart = caret + insert.Length;
    }
}
