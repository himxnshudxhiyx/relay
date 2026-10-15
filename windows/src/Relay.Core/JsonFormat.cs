using System.Text;
using System.Text.Json;

namespace Relay.Core;

// Ported from JSONFormat.swift.

/// <summary>
/// JSON reformatting that treats the document as text, not as a value.
/// Round-tripping through a serializer would sort keys, turn <c>1.0</c> into
/// <c>1</c> and rewrite <c>é</c> as <c>é</c> — so a response would no longer
/// show what the server actually sent. The reindenter only moves whitespace.
/// </summary>
public static class JsonFormat
{
    public enum Token { Key, String, Number, Literal, Punctuation }

    /// <summary>Nesting limit for validity checks; the reader's default of 64 rejects real responses.</summary>
    internal const int MaxDepth = 512;

    public static bool IsJson(string text)
    {
        var trimmed = text.AsSpan().Trim();
        if (trimmed.IsEmpty) return false;
        // Cheap reject before handing megabytes of HTML to the parser.
        switch (trimmed[0])
        {
            case '{' or '[' or '"' or '-' or (>= '0' and <= '9') or 't' or 'f' or 'n':
                break;
            default:
                return false;
        }
        var bytes = new byte[Encoding.UTF8.GetByteCount(trimmed)];
        Encoding.UTF8.GetBytes(trimmed, bytes);
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaxDepth });
            while (reader.Read()) { }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string? Pretty(string text, int indent = 2) =>
        IsJson(text) ? Reformat(text, Math.Max(0, indent)) : null;

    public static string? Minify(string text) => IsJson(text) ? Reformat(text, null) : null;

    /// <summary><paramref name="indent"/> null minifies. Input must already be valid JSON.</summary>
    /// <remarks>Works on UTF-16 units: every structural character is ASCII, so this matches Swift's UTF-8 loop.</remarks>
    internal static string Reformat(string text, int? indent)
    {
        var src = text.AsSpan();
        var n = src.Length;
        var out_ = new StringBuilder(indent == null ? n : n + n / 2);
        var depth = 0;
        var i = 0;
        while (i < n)
        {
            var c = src[i];
            switch (c)
            {
                case '"':
                {
                    var j = i + 1;
                    while (j < n)
                    {
                        var d = src[j];
                        if (d == '\\') { j += 2; continue; }
                        if (d == '"') break;
                        j++;
                    }
                    var end = Math.Min(j + 1, n);
                    out_.Append(src[i..end]);
                    i = end;
                    continue;
                }
                case '{' or '[':
                {
                    var close = c == '{' ? '}' : ']';
                    var j = i + 1;
                    while (j < n && IsWhitespace(src[j])) j++;
                    if (j < n && src[j] == close)
                    {
                        out_.Append(c).Append(close);
                        i = j + 1;
                        continue;
                    }
                    out_.Append(c);
                    depth++;
                    if (indent is { } ind) Newline(out_, depth * ind);
                    break;
                }
                case '}' or ']':
                    depth = Math.Max(0, depth - 1);
                    if (indent is { } ind2) Newline(out_, depth * ind2);
                    out_.Append(c);
                    break;
                case ',':
                    out_.Append(c);
                    if (indent is { } ind3) Newline(out_, depth * ind3);
                    break;
                case ':':
                    out_.Append(c);
                    if (indent != null) out_.Append(' ');
                    break;
                case ' ' or '\t' or '\n' or '\r':
                    break;
                default:
                    out_.Append(c);
                    break;
            }
            i++;
        }
        return out_.ToString();
    }

    static bool IsWhitespace(char c) => c is ' ' or '\n' or '\r' or '\t';

    static void Newline(StringBuilder out_, int spaces)
    {
        out_.Append('\n');
        if (spaces > 0) out_.Append(' ', spaces);
    }

    /// <summary>
    /// Token spans for colouring, as UTF-16 offsets. Lenient: request bodies are
    /// often half-typed or hold unquoted {{vars}}, and should still colour.
    /// </summary>
    public static List<(int Start, int Length, Token Token)> HighlightRanges(string text)
    {
        var u = text.AsSpan();
        var n = u.Length;
        var result = new List<(int, int, Token)>();
        var i = 0;
        while (i < n)
        {
            var c = u[i];
            switch (c)
            {
                case '"':
                {
                    var start = i;
                    i++;
                    while (i < n)
                    {
                        if (u[i] == '\\') i += 2;
                        else if (u[i] == '"') { i++; break; }
                        else i++;
                    }
                    i = Math.Min(i, n);
                    var j = i;
                    while (j < n && IsWhitespace(u[j])) j++;
                    result.Add((start, i - start, j < n && u[j] == ':' ? Token.Key : Token.String));
                    break;
                }
                case '-' or (>= '0' and <= '9'):
                {
                    var start = i;
                    while (i < n && IsNumberChar(u[i])) i++;
                    result.Add((start, i - start, Token.Number));
                    break;
                }
                case 't' or 'f' or 'n':
                {
                    var start = i;
                    while (i < n && u[i] >= 'a' && u[i] <= 'z') i++;
                    var word = u[start..i];
                    if (word.SequenceEqual("true") || word.SequenceEqual("false") || word.SequenceEqual("null"))
                        result.Add((start, i - start, Token.Literal));
                    break;
                }
                case '{' or '}' or '[' or ']' or ',' or ':':
                    result.Add((i, 1, Token.Punctuation));
                    i++;
                    break;
                default:
                    i++;
                    break;
            }
        }
        return result;
    }

    static bool IsNumberChar(char c) => c is (>= '0' and <= '9') or '.' or 'e' or 'E' or '+' or '-';
}
