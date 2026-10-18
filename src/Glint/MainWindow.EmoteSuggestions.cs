using Glint.Chat;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Glint;

public sealed partial class MainWindow
{
    Popup? _emoteSuggestionsPopup;
    EmoteSuggestions? _emoteSuggestions;
    EmoteQuery? _emoteQuery;
    (ChannelTab? Tab, string Text)? _dismissedEmoteQuery;
    bool _suggestionsQueued, _insertingSuggestion, _suggestionEnterPending;

    void WireEmoteSuggestions()
    {
        ChatInput.TextChanged += (_, _) => QueueEmoteSuggestions();
        ChatInput.SelectionChanged += (_, _) => QueueEmoteSuggestions();
        ChatInput.GotFocus += (_, _) => QueueEmoteSuggestions();
        ChatInput.LostFocus += (_, _) => { HideEmoteSuggestions(); _suggestionEnterPending = false; };
        ChatInput.KeyUp += (_, e) => { if (e.Key == VirtualKey.Enter) _suggestionEnterPending = false; };
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (_emoteSuggestionsPopup?.IsOpen != true) return;
            for (DependencyObject? node = e.OriginalSource as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
                if (node == ChatInput || node == _emoteSuggestions) return;
            HideEmoteSuggestions(dismiss: true);
        }), true);
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) HideEmoteSuggestions();
            else QueueEmoteSuggestions();
        };
        Root.LayoutUpdated += (_, _) =>
        {
            if (_emoteSuggestionsPopup?.IsOpen == true && _emoteSuggestions != null)
                _emoteSuggestions.Width = Math.Min(Math.Max(280, ChatInput.ActualWidth), Math.Max(100, Math.Min(380, Root.ActualWidth - 24)));
        };
    }

    void QueueEmoteSuggestions()
    {
        if (_closing || _updatingDraft || _insertingSuggestion || _suggestionsQueued) return;
        _suggestionsQueued = true;
        _dispatcher.TryEnqueue(() =>
        {
            _suggestionsQueued = false;
            UpdateEmoteSuggestions();
        });
    }

    void UpdateEmoteSuggestions()
    {
        if (_closing || !S.EmoteSuggestions || !_windowActive || !ChatInput.IsEnabled || ChatInput.FocusState == FocusState.Unfocused ||
            ChatTarget is not { } target || _dismissedEmoteQuery == (target, ChatInput.Text) ||
            !EmoteCompletion.TryGetQuery(ChatInput.Text, ChatInput.SelectionStart, ChatInput.SelectionLength, out var query))
        { HideEmoteSuggestions(); return; }
        var matches = EmoteCompletion.Find(query.Prefix, target.Chat.Emotes.All.Concat(_userEmotes ?? Enumerable.Empty<EmoteInfo>()));
        if (_userEmotes == null && _irc.IsAuthenticated) _ = LoadSuggestionEmotesAsync();
        if (matches.Count == 0) { HideEmoteSuggestions(); return; }
        if (_emoteSuggestions == null)
        {
            _emoteSuggestions = new EmoteSuggestions();
            _emoteSuggestions.Chosen += ChooseEmoteSuggestion;
            _emoteSuggestionsPopup = new Popup { Child = _emoteSuggestions, PlacementTarget = ChatInput,
                DesiredPlacement = PopupPlacementMode.TopEdgeAlignedLeft, IsLightDismissEnabled = false,
                ShouldConstrainToRootBounds = true, RequestedTheme = ElementTheme.Dark };
            _emoteSuggestionsPopup.Opened += (_, _) => RefreshCutoutsSoon();
            _emoteSuggestionsPopup.Closed += (_, _) => RefreshCutoutsSoon();
            Root.Children.Add(_emoteSuggestionsPopup);
        }
        _emoteQuery = query;
        _emoteSuggestions.Width = Math.Min(Math.Max(280, ChatInput.ActualWidth), Math.Max(100, Math.Min(380, Root.ActualWidth - 24)));
        int rows = Math.Clamp((int)((Root.ActualHeight - 120) / 44), 1, 6);
        _emoteSuggestions.SetItems(matches, rows);
        _emoteSuggestionsPopup!.IsOpen = true;
    }

    async Task LoadSuggestionEmotesAsync()
    {
        await EnsureUserEmotesAsync();
        QueueEmoteSuggestions();
    }

    void HideEmoteSuggestions(bool dismiss = false)
    {
        if (dismiss) _dismissedEmoteQuery = (ChatTarget, ChatInput.Text);
        if (_emoteSuggestionsPopup != null) _emoteSuggestionsPopup.IsOpen = false;
        _emoteQuery = null;
    }

    bool HandleEmoteSuggestionKey(KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && _suggestionEnterPending) { e.Handled = true; return true; }
        if (_emoteSuggestionsPopup?.IsOpen != true) return false;
        switch (e.Key)
        {
            case VirtualKey.Up: _emoteSuggestions!.Move(-1); break;
            case VirtualKey.Down: _emoteSuggestions!.Move(1); break;
            case VirtualKey.Enter:
            case VirtualKey.Tab:
                if ((CurrentModifiers() & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu)) != 0) return false;
                _suggestionEnterPending = e.Key == VirtualKey.Enter;
                if (_emoteSuggestions!.Selected is { } emote) ChooseEmoteSuggestion(emote);
                break;
            case VirtualKey.Escape: HideEmoteSuggestions(dismiss: true); break;
            default: return false;
        }
        e.Handled = true;
        return true;
    }

    void ChooseEmoteSuggestion(EmoteInfo emote)
    {
        if (_emoteQuery is not { } query || ChatTarget == null || !ChatInput.IsEnabled ||
            !EmoteCompletion.TryGetQuery(ChatInput.Text, ChatInput.SelectionStart, ChatInput.SelectionLength, out var current) || query != current) return;
        _insertingSuggestion = true;
        try
        {
            var result = EmoteCompletion.Insert(ChatInput.Text, query, emote.Name);
            HideEmoteSuggestions();
            ChatInput.Text = result.Text;
            ChatInput.SelectionStart = result.Caret;
            _completions = null;
            var recent = S.RecentEmotes;
            recent.Remove(emote.Name); recent.Insert(0, emote.Name);
            if (recent.Count > 27) recent.RemoveRange(27, recent.Count - 27);
            Settings.Save();
            ChatInput.Focus(FocusState.Programmatic);
        }
        finally { _insertingSuggestion = false; }
    }
}
