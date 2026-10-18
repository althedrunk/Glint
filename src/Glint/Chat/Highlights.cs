namespace Glint.Chat;

public enum HighlightKind { None, Mention, Keyword, User }

// Decides whether a message should stand out: your own name, words you've chosen, or people you've chosen.
public static class Highlights
{
    public static HighlightKind Check(string login, string? text, string? self)
    {
        var s = Settings.Current;
        bool fromSelf = self != null && login.Equals(self, StringComparison.OrdinalIgnoreCase);
        if (!fromSelf && text != null && self != null && ContainsWord(text, self)) return HighlightKind.Mention;
        if (!fromSelf && text != null)
            foreach (var word in s.HighlightWords)
                if (word.Length > 0 && ContainsWord(text, word)) return HighlightKind.Keyword;
        if (s.HighlightUsers.Any(u => u.TrimStart('@').Equals(login, StringComparison.OrdinalIgnoreCase))) return HighlightKind.User;
        return HighlightKind.None;
    }

    // Whole-word, case-insensitive. "@name" and "name," both count; "names" doesn't.
    public static bool ContainsWord(string text, string word)
    {
        int i = 0;
        while ((i = text.IndexOf(word, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool startOk = i == 0 || !IsWordChar(text[i - 1]);
            int end = i + word.Length;
            bool endOk = end >= text.Length || !IsWordChar(text[end]);
            if (startOk && endOk) return true;
            i = end;
        }
        return false;
    }

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // Settings store the lists as comma-separated text in the UI.
    public static List<string> ParseList(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.TrimStart('@')).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
