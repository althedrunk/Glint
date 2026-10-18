using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Glint;

public sealed partial class MainWindow
{
    List<string>? _completions;
    int _completionIndex, _completionStart, _completionEnd;
    bool _updatingDraft;
    ChannelTab? ChatTarget => MultiviewActive ? _multiviewRecipient : _active;

    void SaveChatDraft()
    {
        if (!_updatingDraft && ChatTarget is { } tab) tab.Draft = ChatInput.Text;
    }

    void RestoreChatDraft()
    {
        _updatingDraft = true;
        ChatInput.Text = ChatTarget?.Draft ?? "";
        ChatInput.SelectionStart = ChatInput.Text.Length;
        _updatingDraft = false;
        _completions = null;
    }

    void SetChatRecipient(ChannelTab tab)
    {
        if (!_tabs.Contains(tab) || _multiviewRecipient == tab) return;
        SaveChatDraft();
        _multiviewRecipient = tab;
        MultiviewRecipient.SelectedItem = tab;
        RestoreChatDraft();
        UpdateChatInput();
    }

    void UpdateChatInput()
    {
        DropStaleReply();
        Chat.AllowReplies = _irc.IsAuthenticated && _active?.Vod == null;
        foreach (var pane in _multiviewChats.Values) pane.View.AllowReplies = _irc.IsAuthenticated && pane.Tab.Vod == null;
        if (ChatTarget?.Vod != null)
        {
            ChatInput.IsEnabled = false;
            EmoteButton.IsEnabled = false;
            ChatInput.PlaceholderText = "Chat replay";
            return;
        }
        bool canChat = _irc.IsAuthenticated && ChatTarget != null;
        ChatInput.IsEnabled = canChat;
        EmoteButton.IsEnabled = canChat;
        ChatInput.PlaceholderText = canChat ? $"Message #{ChatTarget!.Login}" : "Log in to chat";
    }

    void ChatInput_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Tab)
        {
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            Complete(shift);
            e.Handled = true;
        }
        else if (e.Key is not (VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift))
        {
            _completions = null;
        }
    }

    // Tab cycles through emotes (or @names) starting with the word under the caret, like Chatterino.
    void Complete(bool backwards)
    {
        if (ChatTarget is not { } target) return;
        var text = ChatInput.Text;
        if (_completions == null)
        {
            int caret = ChatInput.SelectionStart;
            int start = caret == 0 ? 0 : text.LastIndexOf(' ', caret - 1) + 1;
            var prefix = text[start..caret];
            if (prefix.Length == 0) return;

            IEnumerable<string> candidates;
            if (prefix.StartsWith('@'))
            {
                var p = prefix[1..];
                candidates = target.Chat.Chatters().Where(c => c.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).Select(c => "@" + c);
            }
            else
            {
                candidates = target.Chat.Emotes.All.Select(e => e.Name)
                    .Concat(_userEmotes?.Select(e => e.Name) ?? Enumerable.Empty<string>())
                    .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Concat(target.Chat.Chatters().Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    .Distinct()
                    .OrderByDescending(n => n.StartsWith(prefix, StringComparison.Ordinal))
                    .ThenBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal);
            }
            _completions = candidates.Take(200).ToList();
            if (_completions.Count == 0) { _completions = null; return; }
            _completionIndex = backwards ? 0 : -1;
            _completionStart = start;
            _completionEnd = caret;
        }

        int n = _completions.Count;
        _completionIndex = ((_completionIndex + (backwards ? -1 : 1)) % n + n) % n;
        var word = _completions[_completionIndex] + " ";
        var end = Math.Min(_completionEnd, text.Length);
        if (end < text.Length && text[end] == ' ') end++; // eat the space we added last time
        ChatInput.Text = text[.._completionStart] + word + text[end..];
        _completionEnd = _completionStart + word.Length - 1;
        ChatInput.SelectionStart = _completionStart + word.Length;
    }

    async void ChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _replyTo != null) { CancelReply(); e.Handled = true; return; }
        if (e.Key != VirtualKey.Enter || ChatTarget is not { } target || target.Vod != null) return;
        e.Handled = true;
        var text = ChatInput.Text.Trim();
        if (text.Length == 0 || !_irc.IsAuthenticated) return;
        ChatInput.Text = "";
        var chat = target.Chat;

        var replyTo = _replyTab == target && !text.StartsWith('/') ? _replyTo : null;
        if (replyTo != null && !text.StartsWith("@" + replyTo.Login + " ", StringComparison.OrdinalIgnoreCase))
            text = $"@{replyTo.Login} {text}"; // same as twitch.tv, so other clients show who it's for
        CancelReply();
        await _irc.SendMessageAsync(target.Login, text, replyTo?.Id);
        // Twitch doesn't echo your own messages back, so add it locally (commands other than /me aren't messages).
        if (!text.StartsWith('/') || text.StartsWith("/me ")) chat.AddLocalEcho(text, replyTo);
        if (MultiviewActive && _multiviewChats.TryGetValue(target, out var pane)) pane.View.ScrollToBottom();
        else if (target == _active) Chat.ScrollToBottom();
    }
}
