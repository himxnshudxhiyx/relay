namespace Relay.Core;

// Ported from BulkEdit in RequestEditor.swift.

/// <summary>"key: value" per line; a leading <c>//</c> marks a disabled row.</summary>
public static class BulkEdit
{
    static readonly char[] Newlines = ['\n', '\v', '\f', '\r', '\u0085', '\u2028', '\u2029'];

    public static string Text(IEnumerable<KeyValue> rows) =>
        string.Join("\n", rows.Where(r => !r.IsBlank).Select(r => (r.Enabled ? "" : "// ") + r.Key + ": " + r.Value));

    /// <summary>
    /// Rows for <paramref name="text"/>. The nth line reuses the nth previous row
    /// (keeping its id, description and flags), so the table doesn't flicker.
    /// </summary>
    public static List<KeyValue> Parse(string text, IReadOnlyList<KeyValue> previous)
    {
        var rows = new List<KeyValue>();
        foreach (var line in text.Split(Newlines))
        {
            var content = line.Trim();
            if (content.Length == 0) continue;
            var enabled = true;
            if (content.StartsWith("//", StringComparison.Ordinal))
            {
                enabled = false;
                content = content[2..].Trim();
            }
            var row = rows.Count < previous.Count ? previous[rows.Count] : new KeyValue();
            var colon = content.IndexOf(':');
            row.Key = colon < 0 ? content : content[..colon].Trim();
            row.Value = colon < 0 ? "" : content[(colon + 1)..].Trim();
            row.Enabled = enabled;
            rows.Add(row);
        }
        return rows;
    }
}
