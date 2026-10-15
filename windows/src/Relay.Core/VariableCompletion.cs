namespace Relay.Core;

// Ported from VariableCompletion.swift (the logic; the popup is UI).

/// <summary>
/// A <c>{{name</c> being typed: the partial name between <c>{{</c> and the cursor.
/// <see cref="Start"/> and <see cref="Length"/> cover the typed part of the name, in UTF-16 units (as a TextBox counts).
/// </summary>
public readonly record struct CompletionContext(int Start, int Length, string Prefix)
{
    /// <summary>
    /// Names are short, so the scan back from the cursor stops well before
    /// it would crawl through a large body looking for a brace.
    /// </summary>
    const int MaxNameLength = 120;

    public static CompletionContext? Find(string text, int cursor)
    {
        if (cursor < 2 || cursor > text.Length) return null;
        var start = cursor;
        var limit = Math.Max(0, cursor - MaxNameLength);
        while (start > limit && !BreaksName(text[start - 1])) start--;
        if (start < 2 || text[start - 1] != '{' || text[start - 2] != '{') return null;
        return new CompletionContext(start, cursor - start, text[start..cursor]);
    }

    /// <summary>
    /// The range a picked name replaces: the typed part plus the rest of the
    /// name after the cursor, so picking inside <c>{{bas|eUrl}}</c> swaps the whole
    /// name. <c>Closed</c> says whether <c>}}</c> already follows.
    /// </summary>
    public (int Start, int Length, bool Closed) Replacement(string text)
    {
        var end = Math.Min(Start + Length, text.Length);
        while (end < text.Length && !BreaksName(text[end])) end++;
        var closed = end + 2 <= text.Length && string.CompareOrdinal(text, end, "}}", 0, 2) == 0;
        return (Start, end - Start, closed);
    }

    /// <summary>Braces, whitespace and quotes can't be part of a name, so they end the scan.</summary>
    static bool BreaksName(char c) => c is '{' or '}' or ' ' or '\t' or '\n' or '\r' or '"' or '\'';
}

public sealed record VariableSuggestion(string Name, string Value, VariableScope.Source Source, bool IsSecret = false);

public static class VariableCompletion
{
    public static readonly string[] DynamicNames = ["$guid", "$randomUUID", "$timestamp", "$isoTimestamp", "$randomInt"];

    /// <summary>
    /// Variables to offer for <paramref name="prefix"/>: environment first (it wins a name
    /// clash, so its row is the one shown), then the collection's. Built-in
    /// <c>$</c> variables only once a <c>$</c> is typed, so they don't bury real ones.
    /// </summary>
    public static List<VariableSuggestion> Suggestions(this VariableScope scope, string prefix)
    {
        var seen = new HashSet<string>();
        var all = new List<VariableSuggestion>();
        foreach (var row in scope.Environment.Where(r => r.Enabled && r.Key.Length > 0 && seen.Add(r.Key)))
            all.Add(new(row.Key, row.Value, VariableScope.Source.Environment, row.IsSecret));
        foreach (var row in scope.Collection.Where(r => r.Enabled && r.Key.Length > 0 && seen.Add(r.Key)))
            all.Add(new(row.Key, row.Value, VariableScope.Source.Collection, row.IsSecret));
        if (prefix.StartsWith('$'))
            all.AddRange(DynamicNames.Select(n => new VariableSuggestion(n, "", VariableScope.Source.Dynamic)));
        if (prefix.Length == 0) return all;

        var lower = prefix.ToLowerInvariant();
        var starting = all.Where(s => s.Name.ToLowerInvariant().StartsWith(lower, StringComparison.Ordinal));
        var containing = all.Where(s => !s.Name.ToLowerInvariant().StartsWith(lower, StringComparison.Ordinal)
                                        && s.Name.ToLowerInvariant().Contains(lower));
        return [.. starting, .. containing];
    }
}
