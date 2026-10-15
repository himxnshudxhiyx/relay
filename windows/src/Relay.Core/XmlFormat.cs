using System.Text;

namespace Relay.Core;

// Ported from JSONFormat.swift (XMLFormat).

/// <summary>
/// Best-effort XML/HTML reindenting. Not a parser: good enough to make a
/// minified SOAP or RSS response readable, and it never drops characters.
/// </summary>
public static class XmlFormat
{
    public enum Token { Tag, Attribute, Value, Comment }

    // Swift walks unicode scalars; every character it tests is ASCII, so UTF-16 units behave the same.
    public static string? Pretty(string text, int indent = 2)
    {
        var chars = text.AsSpan().Trim();
        if (!chars.StartsWith("<")) return null;
        var n = chars.Length;
        var out_ = new StringBuilder(n + n / 2);
        var depth = 0;
        var i = 0;
        // Text right after an opening tag stays on its line: <name>Ada</name>.
        var inlineText = false;
        var lastWasOpen = false;

        void Newline()
        {
            if (out_.Length > 0) out_.Append('\n');
            out_.Append(' ', depth * indent);
        }

        while (i < n)
        {
            if (chars[i] == '<')
            {
                int end;
                if (chars[i..].StartsWith("<!--"))
                {
                    end = i + 4;
                    while (end < n && !chars[end..].StartsWith("-->")) end++;
                    end = Math.Min(end + 3, n);
                }
                else if (chars[i..].StartsWith("<![CDATA["))
                {
                    end = i + 9;
                    while (end < n && !chars[end..].StartsWith("]]>")) end++;
                    end = Math.Min(end + 3, n);
                }
                else
                {
                    end = i + 1;
                    char? quote = null;
                    while (end < n)
                    {
                        var c = chars[end];
                        if (quote is { } q)
                        {
                            if (c == q) quote = null;
                        }
                        else if (c is '"' or '\'')
                        {
                            quote = c;
                        }
                        else if (c == '>')
                        {
                            break;
                        }
                        end++;
                    }
                    end = Math.Min(end + 1, n);
                }
                var tag = chars[i..end];
                var isClose = tag.Length > 1 && tag[1] == '/';
                var isSpecial = tag.Length > 1 && tag[1] is '?' or '!';
                var isSelfClosing = tag.Length > 1 && tag[^2] == '/';

                if (isClose)
                {
                    depth = Math.Max(0, depth - 1);
                    if (!(inlineText || lastWasOpen)) Newline();
                    lastWasOpen = false;
                }
                else
                {
                    Newline();
                    lastWasOpen = !(isSpecial || isSelfClosing);
                    if (lastWasOpen) depth++;
                }
                out_.Append(tag);
                inlineText = false;
                i = end;
            }
            else
            {
                var end = i;
                while (end < n && chars[end] != '<') end++;
                var content = chars[i..end].Trim();
                if (!content.IsEmpty)
                {
                    if (!lastWasOpen) Newline();
                    out_.Append(content);
                    inlineText = lastWasOpen;
                }
                lastWasOpen = false;
                i = end;
            }
        }
        return out_.ToString();
    }

    /// <summary>Token spans for colouring, as UTF-16 offsets.</summary>
    public static List<(int Start, int Length, Token Token)> HighlightRanges(string text)
    {
        var u = text.AsSpan();
        var n = u.Length;
        var result = new List<(int, int, Token)>();

        static bool IsNameChar(char c) =>
            c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or ':' or '.' or > (char)0x7F;

        var i = 0;
        while (i < n)
        {
            if (u[i] != '<') { i++; continue; }
            if (u[i..].StartsWith("<!--"))
            {
                var end = i + 4;
                while (end < n && !u[end..].StartsWith("-->")) end++;
                end = Math.Min(end + 3, n);
                result.Add((i, end - i, Token.Comment));
                i = end;
                continue;
            }
            var start = i;
            var j = i + 1;
            if (j < n && u[j] is '/' or '?' or '!') j++;
            while (j < n && IsNameChar(u[j])) j++;
            result.Add((start, j - start, Token.Tag));
            while (j < n)
            {
                var c = u[j];
                if (c == '>')
                {
                    result.Add((j, 1, Token.Tag));
                    j++;
                    break;
                }
                else if (c is '/' or '?' && j + 1 < n && u[j + 1] == '>')
                {
                    result.Add((j, 2, Token.Tag));
                    j += 2;
                    break;
                }
                else if (c is '"' or '\'')
                {
                    var valueStart = j;
                    j++;
                    while (j < n && u[j] != c) j++;
                    j = Math.Min(j + 1, n);
                    result.Add((valueStart, j - valueStart, Token.Value));
                }
                else if (IsNameChar(c))
                {
                    var nameStart = j;
                    while (j < n && IsNameChar(u[j])) j++;
                    result.Add((nameStart, j - nameStart, Token.Attribute));
                }
                else if (c == '<')
                {
                    break; // unterminated tag; let the outer loop take over
                }
                else
                {
                    j++;
                }
            }
            i = j;
        }
        return result;
    }
}
