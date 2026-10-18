using Glint.Chat;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Glint;

// Emote picker next to the chat box.
public sealed partial class MainWindow
{
    List<EmoteInfo>? _userEmotes; // your own Twitch emotes (subs, follower, bits), once logged in

    void WireEmotePicker() => EmoteButton.Click += (_, _) => ShowEmotePicker();

    async void ShowEmotePicker()
    {
        if (ChatTarget is not { } tab) return;
        var picker = new EmotePicker();
        var flyout = new Flyout { Content = picker, Placement = FlyoutPlacementMode.TopEdgeAlignedRight };
        picker.EmoteChosen += e => InsertEmote(e.Name);
        flyout.Closed += (_, _) => picker.Dispose();
        TrackVideoPopup(flyout);
        picker.SetSections(EmoteSections(tab));
        flyout.ShowAt(EmoteButton);

        if (_userEmotes == null && _irc.IsAuthenticated)
        {
            try
            {
                _userEmotes = TwitchSession.IsConnected ? await TwitchSession.GetUserEmotesAsync()
                            : S.AccessToken is { } token ? await TwitchAuth.GetUserEmotesAsync(token)
                            : new();
            }
            catch (Exception ex) { Log.Error("User emotes", ex); _userEmotes = new(); }
            picker.SetSections(EmoteSections(tab));
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
