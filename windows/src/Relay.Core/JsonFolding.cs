using System.Text;

namespace Relay.Core;

// Ported from JSONFolding.swift.

/// <summary>
/// Collapsible {…} / […] blocks in multi-line JSON, like Postman's response
/// view. Works on the text as the server formatted it, so folding never
/// reorders keys or reformats numbers.
/// </summary>
public sealed class JsonFolding
{
    /// <summary>An opener at the end of its line and its matching closer, as UTF-16 offsets into the source.</summary>
    public readonly record struct Region(int Open, int Close);

    public string Source { get; }
    /// <summary>Swift's <c>regions</c>; renamed because C# can't share the name with the static <see cref="Regions"/>.</summary>
    public IReadOnlyList<Region> AllRegions { get; }
    readonly HashSet<int> _folded = [];
    /// <summary>The source with folded regions shown as {…}.</summary>
    public string Display { get; private set; } = "";
    /// <summary>Display offset of a line start → index into <see cref="AllRegions"/> of the block it opens.</summary>
    public IReadOnlyDictionary<int, int> Markers { get; private set; } = new Dictionary<int, int>();
    /// <summary>Display offset of each fold's "…" → index into <see cref="AllRegions"/>.</summary>
    public IReadOnlyDictionary<int, int> Placeholders { get; private set; } = new Dictionary<int, int>();
    /// <summary>Each fold's display offset and the source lines hidden up to and including it.</summary>
    List<(int Offset, int Lines)> _hidden = [];

    public JsonFolding(string source)
    {
        Source = source;
        AllRegions = Regions(source);
        Render();
    }

    /// <summary>
    /// Source lines hidden by folds before display <paramref name="offset"/>, so line
    /// numbers can skip over a fold the way the original text is numbered.
    /// </summary>
    public int HiddenLines(int offset)
    {
        int low = 0, high = _hidden.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_hidden[mid].Offset <= offset) low = mid + 1; else high = mid;
        }
        return low == 0 ? 0 : _hidden[low - 1].Lines;
    }

    public bool IsFolded(int region) => _folded.Contains(AllRegions[region].Open);

    /// <summary>The fold whose {…} covers display <paramref name="offset"/>, if any.</summary>
    public int? Placeholder(int offset)
    {
        for (var o = offset - 1; o <= offset + 1; o++)
            if (Placeholders.TryGetValue(o, out var index)) return index;
        return null;
    }

    public void Toggle(int region)
    {
        var key = AllRegions[region].Open;
        if (!_folded.Remove(key)) _folded.Add(key);
        Render();
    }

    void Render()
    {
        var src = Source;
        var out_ = new StringBuilder(src.Length);
        var markers = new Dictionary<int, int>();
        var hidden = new List<(int Offset, int Lines)>();
        var placeholders = new Dictionary<int, int>();
        var cursor = 0;
        var lineStart = 0;

        void Copy(int end)
        {
            if (end <= cursor) return;
            var chunk = src.AsSpan(cursor, end - cursor);
            var newline = chunk.LastIndexOf('\n');
            if (newline >= 0) lineStart = out_.Length + newline + 1;
            out_.Append(chunk);
            cursor = end;
        }

        for (var index = 0; index < AllRegions.Count; index++)
        {
            var region = AllRegions[index];
            if (region.Open < cursor) continue;
            Copy(region.Open + 1);
            // Two blocks can open on one display line (`}, {` after a fold); the first keeps the marker.
            markers.TryAdd(lineStart, index);
            if (_folded.Contains(region.Open))
            {
                placeholders[out_.Length] = index;
                out_.Append('…');
                var lines = src.AsSpan(region.Open + 1, region.Close - region.Open - 1).Count('\n');
                hidden.Add((out_.Length, (hidden.Count > 0 ? hidden[^1].Lines : 0) + lines));
                cursor = region.Close;
            }
        }
        Copy(src.Length);
        Display = out_.ToString();
        Markers = markers;
        _hidden = hidden;
        Placeholders = placeholders;
    }

    /// <summary>Blocks whose opener ends its line, skipping brackets inside strings. Sorted by opener.</summary>
    public static List<Region> Regions(ReadOnlySpan<char> u)
    {
        var stack = new List<(int Offset, bool EndsLine)>();
        var result = new List<Region>();
        var inString = false;
        for (var i = 0; i < u.Length; i++)
        {
            var c = u[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{' or '[':
                    var j = i + 1;
                    while (j < u.Length && u[j] is ' ' or '\t') j++;
                    stack.Add((i, j < u.Length && u[j] is '\n' or '\r'));
                    break;
                case '}' or ']':
                    if (stack.Count == 0) break;
                    var open = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    if (open.EndsLine) result.Add(new Region(open.Offset, i));
                    break;
            }
        }
        result.Sort((a, b) => a.Open.CompareTo(b.Open));
        return result;
    }
}
