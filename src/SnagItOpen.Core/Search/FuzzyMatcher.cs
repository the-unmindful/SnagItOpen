namespace SnagItOpen.Core.Search;

/// <summary>Case-insensitive command search; words and prefixes outrank scattered letters.</summary>
public static class FuzzyMatcher
{
    public static int Score(string query, string title, string keywords = "")
    {
        query = query.Trim();
        if (query.Length == 0) return 0;
        int keyword = Match(query, keywords);
        return Math.Max(Match(query, title), keyword < 0 ? -1 : Math.Max(0, keyword - 8));
    }

    private static int Match(string query, string text)
    {
        if (text.Length == 0) return -1;
        if (text.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 800 - Math.Min(200, text.Length);
        int substring = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (substring >= 0) return 600 - Math.Min(200, substring + text.Length);
        int cursor = 0, previous = -2, score = 100;
        foreach (char ch in query)
        {
            int at = text.IndexOf(ch.ToString(), cursor, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return -1;
            score += at == previous + 1 ? 15 : 2;
            if (at == 0 || char.IsWhiteSpace(text[at - 1])) score += 10;
            score -= at - cursor;
            previous = at; cursor = at + 1;
        }
        return Math.Max(0, score);
    }
}
