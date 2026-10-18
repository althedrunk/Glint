namespace Glint.Chat;

public static class ChatFilters
{
    public static bool Matches(string text, IEnumerable<string> words) =>
        words.Any(word => !string.IsNullOrWhiteSpace(word) && Highlights.ContainsWord(text, word));

    public static bool IsIgnored(ChatMessage message)
    {
        if (message.Kind == MessageKind.System) return false;
        var s = Settings.Current;
        if (s.IgnoredUsers.Any(user => message.Login.Equals(user.TrimStart('@'), StringComparison.OrdinalIgnoreCase))) return true;
        if (!s.ShowAnnouncements && message.NoticeId == "announcement") return true;
        if (!s.ShowSubscriptionNotices && message.Kind == MessageKind.UserNotice &&
            message.NoticeId is "sub" or "resub" or "subgift" or "anonsubgift" or "submysterygift" or "anonsubmysterygift"
                or "giftpaidupgrade" or "anongiftpaidupgrade" or "primepaidupgrade" or "standardpayforward" or "communitypayforward") return true;
        return s.IgnoredWords.Count > 0 && Matches(message.PlainText, s.IgnoredWords);
    }

    public static List<string> ParseWords(string text) => text.Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
