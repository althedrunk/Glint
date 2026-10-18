namespace Glint.Chat;

public readonly record struct EmoteQuery(int Start, int End, string Prefix);

public static class EmoteCompletion
{
    public static bool TryGetQuery(string text, int caret, int selectionLength, out EmoteQuery query)
    {
        query = default;
        if (selectionLength != 0 || caret < 0 || caret > text.Length) return false;
        int start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        if (caret - start < 3 || text[start] != ':') return false;
        string prefix = text[(start + 1)..caret];
        if (prefix.EndsWith(':')) prefix = prefix[..^1];
        if (prefix.Length < 2 || prefix.Contains(':') || prefix.Contains('/')) return false;
        int end = caret;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
        if (end < text.Length && text[end] == ':') end++;
        query = new(start, end, prefix);
        return true;
    }

    public static List<EmoteInfo> Find(string prefix, IEnumerable<EmoteInfo> emotes) => emotes
        .Where(e => e.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase))
        .OrderBy(e => e.IsGlobal ? 2 : e.Provider == EmoteProvider.Twitch ? 1 : 0)
        .ThenByDescending(e => e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(e => e.Name.StartsWith(prefix, StringComparison.Ordinal))
        .ThenBy(e => e.Name.Length).ThenBy(e => e.Name, StringComparer.Ordinal)
        .DistinctBy(e => e.Name, StringComparer.Ordinal).Take(50).ToList();

    public static (string Text, int Caret) Insert(string text, EmoteQuery query, string name)
    {
        string suffix = text[query.End..];
        bool space = suffix.Length > 0 && char.IsWhiteSpace(suffix[0]);
        string separator = suffix.Length == 0 || !space && !char.IsPunctuation(suffix[0]) ? " " : "";
        string result = text[..query.Start] + name + separator + suffix;
        return (result, query.Start + name.Length + separator.Length + (space ? 1 : 0));
    }
}
