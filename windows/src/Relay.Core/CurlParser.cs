using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Relay.Core;

// Ported from CurlParser.swift, plus URLBar.parseCurl from RequestView.swift.

public sealed class CurlParseException(string message) : Exception(message);

/// <summary>
/// Turns a pasted cURL command into a request.
///
/// Written against what people actually paste: Chrome/Firefox "Copy as cURL"
/// (<c>$'…'</c> quoting, <c>--data-raw</c>), Postman (<c>--location</c>), terminals (<c>\</c>
/// continuations, <c>-sSL</c>) and Windows cmd (<c>^</c> continuations, <c>curl.exe</c>).
/// Flags that don't change the request (<c>--compressed</c>, <c>-o</c>) are skipped,
/// with their values, so they can't be mistaken for the URL.
/// </summary>
public static partial class CurlParser
{
    static readonly HashSet<string> FlagsWithValue =
    [
        "-X", "--request", "-H", "--header", "-d", "--data", "--data-raw", "--data-binary",
        "--data-ascii", "--data-urlencode", "--json", "-u", "--user", "-F", "--form",
        "--form-string", "--url", "-A", "--user-agent", "-b", "--cookie", "-e", "--referer",
        "-o", "--output", "-m", "--max-time", "--connect-timeout", "-x", "--proxy", "-w",
        "--write-out", "--retry", "-c", "--cookie-jar", "-T", "--upload-file", "--cacert",
        "--cert", "--key", "-r", "--range", "-z", "--time-cond", "--resolve", "--interface",
    ];

    /// <exception cref="CurlParseException">Not a cURL command, or one missing its URL or a flag's value.</exception>
    public static ApiRequest Parse(string input)
    {
        // A command copied on Windows has CRLF line endings; read it as a Mac one.
        var source = input.ReplaceLineEndings("\n").Trim();
        if (source.StartsWith("$ ", StringComparison.Ordinal)) source = source[2..];
        var tokens = Tokenize(source);
        var first = tokens.Count > 0 ? tokens[0].ToLowerInvariant() : null;
        if (first is not ("curl" or "curl.exe")) throw new CurlParseException("Command must start with \"curl\"");

        string? method = null;
        string? url = null;
        var headers = new List<KeyValue>();
        var dataParts = new List<string>();
        var formFields = new List<KeyValue>();
        var forceGet = false;
        var head = false;
        var isJsonFlag = false;
        var auth = Auth.Inherit();

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            string? inlineValue = null;

            // --flag=value
            if (token.StartsWith("--", StringComparison.Ordinal) && token.IndexOf('=') is var eq and >= 0)
            {
                var name = token[..eq];
                if (FlagsWithValue.Contains(name))
                {
                    inlineValue = token[(eq + 1)..];
                    token = name;
                }
            }
            // -XPOST, -HAccept:x, or combined boolean flags like -sSLk.
            if (inlineValue == null && token.Length > 2 && token.StartsWith('-') && !token.StartsWith("--", StringComparison.Ordinal))
            {
                var shortFlag = token[..2];
                if (FlagsWithValue.Contains(shortFlag))
                {
                    inlineValue = token[2..];
                    token = shortFlag;
                }
                else
                {
                    var letters = token[1..];
                    if (letters.Contains('G')) forceGet = true;
                    if (letters.Contains('I')) head = true;
                    continue;
                }
            }

            string Next()
            {
                if (inlineValue != null) return inlineValue;
                if (i + 1 >= tokens.Count) throw new CurlParseException($"Missing value for {token}");
                i++;
                return tokens[i];
            }

            switch (token)
            {
                case "-X" or "--request":
                    method = Next().ToUpperInvariant();
                    break;
                case "-H" or "--header":
                    var header = Next();
                    var colon = header.IndexOf(':');
                    if (colon <= 0) break;
                    headers.Add(new KeyValue(TrimSpaces(header[..colon]), TrimSpaces(header[(colon + 1)..])));
                    break;
                case "-d" or "--data" or "--data-raw" or "--data-binary" or "--data-ascii":
                    dataParts.Add(Next());
                    break;
                case "--data-urlencode":
                    dataParts.Add(UrlencodeData(Next()));
                    break;
                case "--json":
                    dataParts.Add(Next());
                    isJsonFlag = true;
                    break;
                case "-F" or "--form" or "--form-string":
                    formFields.Add(FormField(Next(), allowFiles: token != "--form-string"));
                    break;
                case "-u" or "--user":
                    var parts = Next().Split(':', 2);
                    auth = new Auth { Type = AuthType.Basic, Username = parts[0], Password = parts.Length > 1 ? parts[1] : "" };
                    break;
                case "-A" or "--user-agent":
                    headers.Add(new KeyValue("User-Agent", Next()));
                    break;
                case "-b" or "--cookie":
                    headers.Add(new KeyValue("Cookie", Next()));
                    break;
                case "-e" or "--referer":
                    headers.Add(new KeyValue("Referer", Next()));
                    break;
                case "--url":
                    url = Next();
                    break;
                case "-G" or "--get":
                    forceGet = true;
                    break;
                case "-I" or "--head":
                    head = true;
                    break;
                default:
                    if (FlagsWithValue.Contains(token)) Next();
                    else if (!token.StartsWith('-') && url == null) url = token;
                    break;
            }
        }

        if (string.IsNullOrEmpty(url)) throw new CurlParseException("No URL found in cURL command");

        // Credentials belong in the Auth tab, where they can be inherited and edited.
        headers.RemoveAll(header =>
        {
            if (header.Key.ToLowerInvariant() != "authorization") return false;
            var value = header.Value;
            var lower = value.ToLowerInvariant();
            if (lower.StartsWith("bearer ", StringComparison.Ordinal))
            {
                auth = new Auth { Type = AuthType.Bearer, Token = TrimSpaces(value[7..]) };
                return true;
            }
            if (lower.StartsWith("basic ", StringComparison.Ordinal) && DecodeBase64Utf8(TrimSpaces(value[6..])) is { } decoded)
            {
                var parts = decoded.Split(':', 2);
                auth = new Auth { Type = AuthType.Basic, Username = parts[0], Password = parts.Length > 1 ? parts[1] : "" };
                return true;
            }
            return false;
        });

        if (isJsonFlag)
        {
            if (HeaderValue(headers, "content-type") == null) headers.Add(new KeyValue("Content-Type", "application/json"));
            if (HeaderValue(headers, "accept") == null) headers.Add(new KeyValue("Accept", "application/json"));
        }

        var body = new RequestBody();
        var data = string.Join("&", dataParts);

        if (forceGet && dataParts.Count > 0)
        {
            url += (url.Contains('?') ? "&" : "?") + data;
        }
        else if (formFields.Count > 0)
        {
            body.Mode = BodyMode.Multipart;
            body.Form = new(formFields);
            headers.RemoveAll(h => h.Key.ToLowerInvariant() == "content-type" && h.Value.ToLowerInvariant().Contains("multipart"));
        }
        else if (dataParts.Count > 0)
        {
            var contentType = HeaderValue(headers, "content-type")?.ToLowerInvariant() ?? "";
            if (contentType.Contains("json") || LooksLikeJson(data))
            {
                body.Mode = BodyMode.Json;
                body.Raw = PrettyJson(data);
            }
            else if (contentType.Contains("xml"))
            {
                body.Mode = BodyMode.Xml;
                body.Raw = data;
            }
            else if (contentType.Contains("x-www-form-urlencoded") || (contentType.Length == 0 && LooksLikeForm(data)))
            {
                body.Mode = BodyMode.Form;
                body.Form = new(ParseForm(data));
            }
            else
            {
                body.Mode = BodyMode.Text;
                body.Raw = data;
            }
        }

        // The body mode implies its Content-Type; keep the header only when it says something more.
        if (body.Mode.ContentType() is { } implied)
        {
            headers.RemoveAll(h =>
                h.Key.ToLowerInvariant() == "content-type"
                && h.Value.ToLowerInvariant().Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } type
                && TrimSpaces(type) == implied);
        }

        var resolvedMethod = method ?? (head ? "HEAD" : body.Mode != BodyMode.None && !forceGet ? "POST" : "GET");

        return new ApiRequest
        {
            Name = RequestName(url),
            Method = resolvedMethod,
            Url = url,
            Params = new(UrlQuery.ParamsFromUrl(url, [])),
            Headers = new(headers),
            Body = body,
            Auth = auth,
        };
    }

    /// <summary>
    /// A cURL command pasted into the URL field, or null when the text isn't one
    /// (or doesn't parse), so the paste goes in as plain text.
    /// </summary>
    public static ApiRequest? ParsePasted(string text)
    {
        var trimmed = text.Trim();
        var lower = trimmed.ToLowerInvariant();
        if (!(lower.StartsWith("curl ", StringComparison.Ordinal) || lower.StartsWith("curl.exe ", StringComparison.Ordinal)
              || lower.StartsWith("$ curl ", StringComparison.Ordinal))) return null;
        // A single-line field can flatten the command's "\⏎" continuations into "\ ".
        var flattened = SpacedContinuation().Replace(PastedContinuation().Replace(trimmed, " "), " ");
        try { return Parse(flattened); }
        catch (CurlParseException) { return null; }
    }

    [GeneratedRegex(@"\\[ \t]*\r?\n")] private static partial Regex PastedContinuation();
    [GeneratedRegex(@"\s\\\s+(?=-)")] private static partial Regex SpacedContinuation();
    [GeneratedRegex(@"\\\r?\n")] private static partial Regex BashContinuation();
    [GeneratedRegex(@"\^\r?\n")] private static partial Regex CmdContinuation();
    [GeneratedRegex(@"^[^=&\s]+=[^&\s]*(&[^=&\s]+=[^&\s]*)*$")] private static partial Regex FormShape();

    /// <summary>
    /// Shell-like tokenizer supporting '…', "…", $'…', backslash escapes and
    /// line continuations (<c>\</c> in bash, <c>^</c> in Windows cmd).
    /// </summary>
    public static List<string> Tokenize(string input)
    {
        var joined = CmdContinuation().Replace(BashContinuation().Replace(input, " "), " ");
        // Walk user-perceived characters, as Swift does, so a quote or backslash
        // carrying a combining mark isn't taken for syntax.
        var src = Characters(joined);
        var tokens = new List<string>();
        var buf = new StringBuilder();
        var inToken = false;
        var i = 0;

        while (i < src.Count)
        {
            var c = src[i];
            if (IsWhitespace(c))
            {
                if (inToken)
                {
                    tokens.Add(buf.ToString());
                    buf.Clear();
                    inToken = false;
                }
                i++;
            }
            else if (c == "'")
            {
                inToken = true;
                var end = src.IndexOf("'", i + 1);
                if (end < 0) throw new CurlParseException("Unterminated single quote");
                for (var k = i + 1; k < end; k++) buf.Append(src[k]);
                i = end + 1;
            }
            else if (c == "$" && i + 1 < src.Count && src[i + 1] == "'")
            {
                inToken = true;
                i += 2;
                while (i < src.Count && src[i] != "'")
                {
                    if (src[i] == "\\" && i + 1 < src.Count)
                    {
                        switch (src[i + 1])
                        {
                            case "n": buf.Append('\n'); break;
                            case "t": buf.Append('\t'); break;
                            case "r": buf.Append('\r'); break;
                            case "u" when i + 5 < src.Count:
                                buf.Append(Scalar(string.Concat(src.GetRange(i + 2, 4))));
                                i += 4;
                                break;
                            case "x" when i + 3 < src.Count:
                                buf.Append(Scalar(string.Concat(src.GetRange(i + 2, 2))));
                                i += 2;
                                break;
                            default: buf.Append(src[i + 1]); break;
                        }
                        i += 2;
                    }
                    else
                    {
                        buf.Append(src[i]);
                        i++;
                    }
                }
                if (i >= src.Count) throw new CurlParseException("Unterminated quote");
                i++;
            }
            else if (c == "\"")
            {
                inToken = true;
                i++;
                while (i < src.Count && src[i] != "\"")
                {
                    if (src[i] == "\\" && i + 1 < src.Count && src[i + 1] is "\"" or "\\" or "$" or "`")
                    {
                        buf.Append(src[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        buf.Append(src[i]);
                        i++;
                    }
                }
                if (i >= src.Count) throw new CurlParseException("Unterminated double quote");
                i++;
            }
            else if (c == "\\" && i + 1 < src.Count)
            {
                inToken = true;
                buf.Append(src[i + 1]);
                i += 2;
            }
            else
            {
                inToken = true;
                buf.Append(c);
                i++;
            }
        }
        if (inToken) tokens.Add(buf.ToString());
        return tokens;
    }

    // MARK: - Helpers

    /// <summary>Grapheme clusters: what Swift calls Characters ("\r\n" is one).</summary>
    static List<string> Characters(string s)
    {
        var list = new List<string>(s.Length);
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add(e.GetTextElement());
        return list;
    }

    /// <summary>Swift's Character.isWhitespace looks at the first scalar.</summary>
    static bool IsWhitespace(string character) => character.Length > 0 && char.IsWhiteSpace(character[0]);

    /// <summary>Swift's <c>.whitespaces</c>: spaces and tabs, not newlines.</summary>
    static string TrimSpaces(string s)
    {
        static bool Space(char c) => c == '\t' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
        int start = 0, end = s.Length;
        while (start < end && Space(s[start])) start++;
        while (end > start && Space(s[end - 1])) end--;
        return s[start..end];
    }

    /// <summary>Like Swift's <c>UInt32(hex, radix: 16)</c> then <c>Unicode.Scalar</c>: "?" for anything that isn't a scalar.</summary>
    static string Scalar(string hex)
    {
        var negative = hex.StartsWith('-');
        var digits = hex.StartsWith('+') || negative ? hex[1..] : hex;
        if (!uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) return "?";
        if (negative && value != 0) return "?";
        return Rune.TryCreate(value, out var rune) ? rune.ToString() : "?";
    }

    static string? HeaderValue(List<KeyValue> headers, string name) =>
        headers.FirstOrDefault(h => h.Key.ToLowerInvariant() == name)?.Value;

    static string? DecodeBase64Utf8(string s)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(s)); }
        catch (Exception e) when (e is FormatException or ArgumentException) { return null; }
    }

    /// <summary>
    /// <c>--data-urlencode</c> sends its content encoded; encoding here means the
    /// form parser's decode gives back exactly what was typed.
    /// </summary>
    static string UrlencodeData(string value)
    {
        var eq = value.IndexOf('=');
        if (eq < 0) return UrlQuery.FormEncode(value);
        var name = value[..eq];
        var content = UrlQuery.FormEncode(value[(eq + 1)..]);
        return name.Length == 0 ? content : $"{name}={content}";
    }

    static KeyValue FormField(string field, bool allowFiles)
    {
        var eq = field.IndexOf('=');
        if (eq < 0) return new KeyValue(field, "");
        var key = field[..eq];
        var value = field[(eq + 1)..];
        if (allowFiles && value.StartsWith('@'))
        {
            value = value[1..];
            foreach (var marker in new[] { ";type=", ";filename=" })
            {
                var at = value.IndexOf(marker, StringComparison.Ordinal);
                if (at >= 0) value = value[..at];
            }
            return new KeyValue(key, StripQuotes(value), isFile: true);
        }
        return new KeyValue(key, StripQuotes(value));
    }

    static string StripQuotes(string v) =>
        v.Length >= 2 && v.StartsWith('"') && v.EndsWith('"') ? v[1..^1] : v;

    static bool LooksLikeJson(string s)
    {
        var t = s.Trim();
        if (!(t.StartsWith('{') || t.StartsWith('['))) return false;
        try
        {
            using var _ = JsonDocument.Parse(t);
            return true;
        }
        catch (JsonException) { return false; }
    }

    static bool LooksLikeForm(string s) => FormShape().IsMatch(s.Trim());

    static List<KeyValue> ParseForm(string s)
    {
        static string Decode(string v)
        {
            var spaced = v.Replace('+', ' ');
            return RemovingPercentEncoding(spaced) ?? spaced;
        }
        return s.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var eq = part.IndexOf('=');
            return eq < 0 ? new KeyValue(Decode(part), "") : new KeyValue(Decode(part[..eq]), Decode(part[(eq + 1)..]));
        }).ToList();
    }

    /// <summary>Foundation's <c>removingPercentEncoding</c>: null on a malformed escape or invalid UTF-8, rather than a partial decode.</summary>
    static string? RemovingPercentEncoding(string s)
    {
        if (!s.Contains('%')) return s;
        var input = Encoding.UTF8.GetBytes(s);
        var bytes = new List<byte>(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] != '%')
            {
                bytes.Add(input[i]);
                continue;
            }
            if (i + 2 >= input.Length || !byte.TryParse(Encoding.ASCII.GetString(input, i + 1, 2), NumberStyles.AllowHexSpecifier, null, out var b))
                return null;
            bytes.Add(b);
            i += 2;
        }
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.ToArray()); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// Already-formatted bodies are left alone; one-liners are reindented.
    /// Not a JSON round trip — it can reorder keys, and key order is part of
    /// how people read an API.
    /// </summary>
    static string PrettyJson(string s)
    {
        var t = s.Trim();
        if (!LooksLikeJson(t) || t.Contains('\n')) return s;
        return Reindent(t);
    }

    static string Reindent(string json)
    {
        var chars = Characters(json);
        var output = new StringBuilder();
        var depth = 0;
        var inString = false;
        var escaped = false;
        void Newline() => output.Append('\n').Append(' ', 2 * Math.Max(depth, 0));

        for (var i = 0; i < chars.Count; i++)
        {
            var c = chars[i];
            if (inString)
            {
                output.Append(c);
                if (escaped) escaped = false;
                else if (c == "\\") escaped = true;
                else if (c == "\"") inString = false;
                continue;
            }
            switch (c)
            {
                case "\"":
                    inString = true;
                    output.Append(c);
                    break;
                case "{" or "[":
                    // Keep empty containers on one line.
                    var j = i + 1;
                    while (j < chars.Count && IsWhitespace(chars[j])) j++;
                    if (j < chars.Count && chars[j] == (c == "{" ? "}" : "]"))
                    {
                        output.Append(c == "{" ? "{}" : "[]");
                        i = j;
                    }
                    else
                    {
                        output.Append(c);
                        depth++;
                        Newline();
                    }
                    break;
                case "}" or "]":
                    depth--;
                    Newline();
                    output.Append(c);
                    break;
                case ",":
                    output.Append(c);
                    Newline();
                    break;
                case ":":
                    output.Append(": ");
                    break;
                default:
                    if (!IsWhitespace(c)) output.Append(c);
                    break;
            }
        }
        return output.ToString();
    }

    /// <summary>
    /// "api.example.com/v1/users" — host and path, for a request that has no
    /// name of its own. The method is left out because a badge beside the
    /// name already shows it; the scheme, query and fragment are noise.
    /// </summary>
    public static string RequestName(string url)
    {
        var rest = UrlQuery.Split(TrimSpaces(url)).Base;
        var scheme = rest.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) rest = rest[(scheme + 3)..];
        // Never put credentials from user:pass@host into a name shown on screen.
        var authorityEnd = rest.IndexOf('/');
        var at = (authorityEnd < 0 ? rest : rest[..authorityEnd]).LastIndexOf('@');
        if (at >= 0) rest = rest[(at + 1)..];
        while (rest.Length > 1 && rest.EndsWith('/')) rest = rest[..^1];
        if (rest.Length == 0) return ApiRequest.DefaultName;
        var info = new StringInfo(rest);
        return info.LengthInTextElements > 80 ? info.SubstringByTextElements(0, 79) + "…" : rest;
    }
}
