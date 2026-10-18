using System.Runtime.InteropServices;
using Glint.Chat;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;

namespace Glint;

// Replying to messages, thread history, and the mentions inbox.
public sealed partial class MainWindow
{
    ChatMessage? _replyTo;
    ChannelTab? _replyTab;
    readonly ChatChannel _mentions = new("mentions");
    int _unreadMentions;
    bool _windowActive = true, _mentionsOpen;
    readonly List<ChatHistoryWindow> _chatHistoryWindows = new();
    ChatHistoryWindow? _mentionsWindow;
    readonly Dictionary<(ChatChannel Source, string User), ChatHistoryWindow> _userHistoryWindows = new();

    void WireReplies()
    {
        ReplyBarClose.Click += (_, _) => CancelReply();
        MentionsButton.Click += (_, _) => ShowMentions();
        IgnoreWordsButton.Click += (_, _) =>
        {
            var flyout = new Flyout { Content = new StackPanel { Width = 340, Children = { SettingsWindow.IgnoreWordsRow(this) } } };
            TrackVideoPopup(flyout);
            flyout.ShowAt(IgnoreWordsButton);
        };
        Activated += (_, e) => _windowActive = e.WindowActivationState != WindowActivationState.Deactivated;
        WireChatView(Chat, () => _active);
    }

    // Every chat view (main, multiview panes) reports reply/thread clicks here.
    void WireChatView(ChatView view, Func<ChannelTab?> tab)
    {
        view.ReplyRequested += m => { if (tab() is { } t) StartReply(t, m); };
        view.ThreadRequested += m => { if (tab() is { } t) ShowThread(t, m); };
        view.UserHistoryRequested += m => { if (view.Channel is { } source) ShowUserHistory(source, m, tab()); };
    }

    void StartReply(ChannelTab tab, ChatMessage m)
    {
        if (MultiviewActive) SetChatRecipient(tab);
        else if (tab != _active) return;
        _replyTo = m;
        _replyTab = tab;
        ReplyBarText.Text = $"Replying to @{m.DisplayName}: {m.PlainText}";
        ReplyBar.Visibility = Visibility.Visible;
        ChatInput.Focus(FocusState.Programmatic);
    }

    void CancelReply()
    {
        _replyTo = null;
        _replyTab = null;
        ReplyBar.Visibility = Visibility.Collapsed;
    }

    // The reply only makes sense for the channel it was started in.
    void DropStaleReply()
    {
        if (_replyTab != null && (_replyTab != ChatTarget || !_tabs.Contains(_replyTab))) CancelReply();
    }

    void ShowThread(ChannelTab tab, ChatMessage clicked)
    {
        var source = tab.Replay ?? tab.Chat;
        var rootId = clicked.ThreadId ?? clicked.ReplyParentId ?? clicked.Id;
        if (rootId == null) return;

        var thread = new ChatChannel(source);
        var root = source.FindMessage(rootId);
        if (root != null) thread.Add(root.CopyForView());
        else if (clicked.ReplyName != null)
            thread.Add(new ChatMessage { Kind = MessageKind.System, SystemText = $"{clicked.ReplyName}: {clicked.ReplyBody}" });
        foreach (var m in source.Messages)
            if (m != root && (m.ThreadId == rootId || m.ReplyParentId == rootId)) thread.Add(m.CopyForView());

        var view = new ChatView { AllowReplies = false, ShowReplyContext = false };
        view.UserHistoryRequested += m => ShowUserHistory(source, m, tab);
        var reply = new Button { Content = "Reply in thread", HorizontalAlignment = HorizontalAlignment.Right, Visibility = CanChatIn(tab) ? Visibility.Visible : Visibility.Collapsed };
        ChatHistoryWindow? window = null;
        reply.Click += (_, _) =>
        {
            window?.Close();
            StartReply(tab, root ?? clicked);
        };
        window = new ChatHistoryWindow(_hwnd, "Reply thread", $"Replies loaded from #{tab.Login}", thread, view, reply);
        OpenHistoryWindow(window);
    }

    void ShowUserHistory(ChatChannel source, ChatMessage user, ChannelTab? tab)
    {
        var key = (source, string.IsNullOrEmpty(user.UserId) ? user.Login.ToLowerInvariant() : user.UserId);
        if (_userHistoryWindows.TryGetValue(key, out var existing)) { existing.Activate(); return; }
        var history = source.WatchUser(user);
        var view = new ChatView { AllowReplies = tab != null && CanChatIn(tab) && !source.IsReplay };
        view.UserHistoryRequested += m => ShowUserHistory(source, m, tab);
        view.ThreadRequested += m => { if (tab != null) ShowThread(tab, m); };
        view.ReplyRequested += m => { if (FocusHistoryChat(tab, source)) StartReply(tab!, m); };
        view.MentionRequested += name => { if (FocusHistoryChat(tab, source)) MentionHistoryUser(name); };
        var mention = new Button { Content = "Mention", Visibility = view.AllowReplies ? Visibility.Visible : Visibility.Collapsed };
        mention.Click += (_, _) => { if (FocusHistoryChat(tab, source)) MentionHistoryUser(user.Login); };
        var window = new ChatHistoryWindow(_hwnd, user.DisplayName,
            $"Messages loaded in #{source.Login}. New messages appear while this window is open.", history.Channel, view, mention)
        {
            Title = $"{user.DisplayName} in #{source.Login} - Glint",
        };
        _userHistoryWindows.Add(key, window);
        window.Closed += (_, _) => { history.Dispose(); _userHistoryWindows.Remove(key); };
        OpenHistoryWindow(window);
    }

    bool FocusHistoryChat(ChannelTab? tab, ChatChannel source)
    {
        if (_closing || tab == null || !_tabs.Contains(tab) || source.IsReplay || !CanChatIn(tab)) return false;
        if (MultiviewActive) SetChatRecipient(tab);
        else Tabs.SelectedItem = tab.Item;
        Activate();
        return true;
    }

    void MentionHistoryUser(string name)
    {
        ChatInput.Text = $"@{name} " + ChatInput.Text;
        ChatInput.SelectionStart = ChatInput.Text.Length;
        ChatInput.Focus(FocusState.Programmatic);
    }

    bool CanChatIn(ChannelTab tab) => _irc.IsAuthenticated && tab.Vod == null;

    void OpenHistoryWindow(ChatHistoryWindow window)
    {
        _chatHistoryWindows.Add(window);
        window.Closed += (_, _) => _chatHistoryWindows.Remove(window);
        window.Activate();
    }

    void OnMentioned(ChatChannel channel, ChatMessage message)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_closing || ChatFilters.IsIgnored(message)) return;
            var copy = message.CopyForView();
            copy.ChannelLabel = "#" + channel.Login;
            _mentions.Add(copy);
            if (!_mentionsOpen) _unreadMentions++;
            UpdateMentionsBadge();

            // Don't nag about the chat you're already looking at.
            bool watching = _windowActive && (MultiviewActive ? _multiviewChats.Keys.Any(t => t.Chat == channel) : _active?.Chat == channel);
            if (watching && !S.NotifyWhenFocused) return;
            bool mention = message.Highlight == HighlightKind.Mention;
            if (mention && S.FlashOnMention && !_windowActive) Flash();
            if (mention ? S.SoundOnMention : S.SoundOnHighlight)
                NotificationSound.Play(mention ? S.MentionTone : S.HighlightTone);
        });
    }

    void UpdateMentionsBadge()
    {
        MentionsBadge.Visibility = _unreadMentions > 0 ? Visibility.Visible : Visibility.Collapsed;
        MentionsCount.Text = _unreadMentions > 99 ? "99+" : _unreadMentions.ToString();
    }

    void ShowMentions()
    {
        _unreadMentions = 0;
        UpdateMentionsBadge();
        if (_mentionsWindow is { IsClosed: false }) { _mentionsWindow.Activate(); return; }
        var view = new ChatView { AllowReplies = false };
        view.UserHistoryRequested += m =>
        {
            if (m.Channel is { } login && _chats.TryGetValue(login, out var source))
                ShowUserHistory(source, m, _tabs.FirstOrDefault(t => t.Login == login));
        };
        var clear = new Button { Content = "Clear", HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(clear, "Clear mentions");
        clear.Click += (_, _) => _mentions.Clear();
        _mentionsWindow = new ChatHistoryWindow(_hwnd, "Mentions", "Your mentions and highlighted messages from all open channels", _mentions, view, clear);
        _mentionsOpen = true;
        _mentionsWindow.Closed += (_, _) => { _mentionsOpen = false; _mentionsWindow = null; };
        OpenHistoryWindow(_mentionsWindow);
    }

    void Flash()
    {
        var info = new FlashInfo { cbSize = (uint)Marshal.SizeOf<FlashInfo>(), hwnd = _hwnd, dwFlags = 2 | 12 }; // TRAY | TIMERNOFG
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FlashInfo { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FlashInfo info);
}
