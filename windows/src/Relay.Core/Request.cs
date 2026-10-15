using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Relay.Core;

// Ported from URLQuery.swift, Variables.swift and RequestPreparer.swift.

/// <summary>
/// Keeps the URL's query string and the Params table in step. Both work on the
/// text as typed (nothing is decoded), so {{vars}} and existing escapes
/// survive a round trip. Encoding happens once, at send time.
/// </summary>
public static class UrlQuery
{
    public static (string Base, string? Query, string? Fragment) Split(string url)
    {
        var rest = url;
        string? fragment = null;
        var hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            fragment = rest[(hash + 1)..];
            rest = rest[..hash];
        }
        var q = rest.IndexOf('?');
        return q < 0 ? (rest, null, fragment) : (rest[..q], rest[(q + 1)..], fragment);
    }

    public static List<(string Key, string Value)> Pairs(string query) =>
        query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var eq = part.IndexOf('=');
            return eq < 0 ? (part, "") : (part[..eq], part[(eq + 1)..]);
        }).ToList();

    /// <summary>
    /// The Params table after the URL was edited. Enabled rows take the query's
    /// pairs in order and keep their ids (so the table doesn't flicker); disabled
    /// rows aren't in the URL and stay where they were.
    /// </summary>
    public static List<KeyValue> ParamsFromUrl(string url, IEnumerable<KeyValue> previous)
    {
        var parsed = new Queue<(string Key, string Value)>(Pairs(Split(url).Query ?? ""));
        var rows = new List<KeyValue>();
        foreach (var old in previous)
        {
            if (!old.Enabled)
            {
                rows.Add(old);
                continue;
            }
            if (!parsed.TryDequeue(out var pair)) continue;
            old.Key = pair.Key;
            old.Value = pair.Value;
            rows.Add(old);
        }
        rows.AddRange(parsed.Select(p => new KeyValue(p.Key, p.Value)));
        return rows;
    }

    /// <summary>The URL after the Params table was edited.</summary>
    public static string UrlApplying(string url, IEnumerable<KeyValue> parameters)
    {
        var parts = Split(url);
        var query = string.Join("&", parameters
            .Where(p => p.Enabled && !p.IsBlank)
            .Select(p => p.Value.Length == 0 ? p.Key : $"{p.Key}={p.Value}"));
        var result = parts.Base;
        if (query.Length > 0) result += "?" + query;
        if (parts.Fragment != null) result += "#" + parts.Fragment;
        return result;
    }

    public static string Appending(IEnumerable<(string Key, string Value)> pairs, string url)
    {
        var list = pairs.ToList();
        if (list.Count == 0) return url;
        var parts = Split(url);
        var added = string.Join("&", list.Select(p => $"{FormEncode(p.Key)}={FormEncode(p.Value)}"));
        var result = parts.Base + "?" + string.Join("&", new[] { parts.Query, added }.Where(s => !string.IsNullOrEmpty(s)));
        if (parts.Fragment != null) result += "#" + parts.Fragment;
        return result;
    }

    const string QueryAllowed = "!$&'()*+,-./:;=?@_~%#[]";

    /// <summary>Percent-encodes what an HTTP client would reject (spaces, unicode, braces) and leaves existing %XX escapes alone.</summary>
    public static Uri? EncodedUrl(string s)
    {
        var encoded = PercentEncode(s, c => IsAsciiAlphanumeric(c) || QueryAllowed.Contains(c));
        return Uri.TryCreate(encoded, UriKind.Absolute, out var uri) && uri.Scheme.Length > 0 && !uri.IsFile ? uri : null;
    }

    /// <summary>application/x-www-form-urlencoded encoding.</summary>
    public static string FormEncode(string s) =>
        PercentEncode(s, c => IsAsciiAlphanumeric(c) || "-._* ".Contains(c)).Replace(' ', '+');

    static bool IsAsciiAlphanumeric(char c) => char.IsAsciiLetterOrDigit(c);

    static string PercentEncode(string s, Func<char, bool> allowed)
    {
        var sb = new StringBuilder(s.Length);
        Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in s.EnumerateRunes())
        {
            if (rune.IsAscii && allowed((char)rune.Value))
            {
                sb.Append((char)rune.Value);
                continue;
            }
            var n = rune.EncodeToUtf8(bytes);
            for (var i = 0; i < n; i++) sb.Append('%').Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }
}

/// <summary>{{name}} substitution, Postman style.</summary>
public static partial class Variables
{
    [GeneratedRegex(@"\{\{\s*([^{}\s]+)\s*\}\}")]
    private static partial Regex Pattern();

    /// <summary>Names referenced in <paramref name="text"/>, in order of first appearance.</summary>
    public static List<string> Names(string text)
    {
        if (!text.Contains("{{")) return [];
        return Pattern().Matches(text).Select(m => m.Groups[1].Value).Distinct().ToList();
    }

    /// <summary>Every {{…}} in <paramref name="text"/> (UTF-16 offsets), for highlighting.</summary>
    public static List<(int Start, int Length, string Name)> Ranges(string text)
    {
        if (!text.Contains("{{")) return [];
        return Pattern().Matches(text).Select(m => (m.Index, m.Length, m.Groups[1].Value)).ToList();
    }

    /// <summary>
    /// Replaces known variables and leaves unknown ones as written, so a typo
    /// shows up in the request rather than silently becoming an empty string.
    /// </summary>
    public static string Resolve(string text, IReadOnlyDictionary<string, string> values)
    {
        if (!text.Contains("{{")) return text;
        var result = text;
        // A value may itself reference a variable ({{baseUrl}} = {{host}}/v1).
        // A few passes covers real use without looping on a self-reference.
        for (var i = 0; i < 4; i++)
        {
            var next = Pattern().Replace(result, m =>
                values.TryGetValue(m.Groups[1].Value, out var v) ? v : Dynamic(m.Groups[1].Value) ?? m.Value);
            if (next == result) break;
            result = next;
        }
        return result;
    }

    /// <summary>Postman's built-in dynamic variables that people actually use.</summary>
    public static string? Dynamic(string name) => name switch
    {
        "$guid" or "$randomUUID" => Guid.NewGuid().ToString(),
        "$timestamp" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        "$isoTimestamp" => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        "$randomInt" => Random.Shared.Next(0, 1001).ToString(CultureInfo.InvariantCulture),
        _ => null,
    };
}

/// <summary>The variables visible to a request: its collection's, overridden by the active environment's (Postman's precedence).</summary>
public sealed class VariableScope
{
    public IReadOnlyList<KeyValue> Collection { get; init; } = [];
    public IReadOnlyList<KeyValue> Environment { get; init; } = [];

    public Dictionary<string, string> Values
    {
        get
        {
            var result = new Dictionary<string, string>();
            foreach (var kv in Collection.Where(k => k.Enabled && k.Key.Length > 0)) result[kv.Key] = kv.Value;
            foreach (var kv in Environment.Where(k => k.Enabled && k.Key.Length > 0)) result[kv.Key] = kv.Value;
            return result;
        }
    }

    public enum Source { Environment, Collection, Dynamic, Missing }

    public Source SourceOf(string name)
    {
        if (Environment.Any(k => k.Enabled && k.Key == name)) return Source.Environment;
        if (Collection.Any(k => k.Enabled && k.Key == name)) return Source.Collection;
        if (name.StartsWith('$') && Variables.Dynamic(name) != null) return Source.Dynamic;
        return Source.Missing;
    }
}

public abstract record PreparedBody
{
    public sealed record None : PreparedBody;
    public sealed record Raw(string Text) : PreparedBody;
    public sealed record Form(List<(string Key, string Value)> Fields) : PreparedBody;
    /// <summary>Resolved rows; IsFile rows hold a file path in Value.</summary>
    public sealed record Multipart(List<KeyValue> Rows) : PreparedBody;
    public sealed record File(string Path) : PreparedBody;
}

/// <summary>
/// A request with variables substituted, auth applied and disabled rows
/// dropped: what the HTTP client sends and what "Copy as cURL" prints.
/// </summary>
public sealed class PreparedRequest
{
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public List<(string Name, string Value)> Headers { get; set; } = [];
    public PreparedBody Body { get; set; } = new PreparedBody.None();
    /// <summary>Kept apart from Headers so cURL can print --user instead of an opaque base64 header.</summary>
    public (string Username, string Password)? BasicAuth { get; set; }

    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Headers as sent on the wire, including basic auth.</summary>
    public List<(string Name, string Value)> WireHeaders
    {
        get
        {
            if (BasicAuth is not { } basic || Header("Authorization") != null) return Headers;
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{basic.Username}:{basic.Password}"));
            return [.. Headers, ("Authorization", $"Basic {token}")];
        }
    }
}

public static class RequestPreparer
{
    /// <param name="auth">The request's effective auth, with Inherit already resolved.</param>
    /// <param name="variables">Values to substitute; null leaves {{placeholders}} as written, for sharing a cURL.</param>
    public static PreparedRequest Prepare(ApiRequest request, Auth auth, IReadOnlyDictionary<string, string>? variables)
    {
        string R(string s) => variables == null ? s : Variables.Resolve(s, variables);

        var method = request.Method.Trim().ToUpperInvariant();
        var url = R(request.Url.Trim());
        if (variables != null && url.Length > 0 && !url.Contains("://")) url = "http://" + url;

        var headers = request.Headers
            .Where(h => h.Enabled && h.Key.Trim().Length > 0)
            .Select(h => (R(h.Key).Trim(), R(h.Value)))
            .ToList();

        bool Has(string name) => headers.Any(h => string.Equals(h.Item1, name, StringComparison.OrdinalIgnoreCase));

        (string, string)? basicAuth = null;
        switch (auth.Type)
        {
            case AuthType.Bearer when auth.Token.Length > 0 && !Has("Authorization"):
                headers.Add(("Authorization", $"Bearer {R(auth.Token)}"));
                break;
            case AuthType.Basic when !(auth.Username.Length == 0 && auth.Password.Length == 0) && !Has("Authorization"):
                basicAuth = (R(auth.Username), R(auth.Password));
                break;
            case AuthType.ApiKey when auth.Key.Length > 0:
                if (auth.Location == ApiKeyLocation.Header)
                {
                    if (!Has(R(auth.Key))) headers.Add((R(auth.Key), R(auth.Value)));
                }
                else
                {
                    url = UrlQuery.Appending([(R(auth.Key), R(auth.Value))], url);
                }
                break;
        }

        PreparedBody body = request.Body.Mode switch
        {
            BodyMode.Json or BodyMode.Text or BodyMode.Xml => new PreparedBody.Raw(R(request.Body.Raw)),
            BodyMode.Form => new PreparedBody.Form(request.Body.Form
                .Where(f => f.Enabled && f.Key.Length > 0)
                .Select(f => (R(f.Key), R(f.Value))).ToList()),
            BodyMode.Multipart => new PreparedBody.Multipart(request.Body.Form
                .Where(f => f.Enabled && f.Key.Length > 0)
                .Select(f => new KeyValue(R(f.Key), R(f.Value), isFile: f.IsFile)).ToList()),
            BodyMode.File => new PreparedBody.File(R(request.Body.FilePath)),
            _ => new PreparedBody.None(),
        };

        // An empty JSON/Text/XML body sends nothing, so it implies no Content-Type.
        var empty = request.Body.Mode.IsRaw() && request.Body.Raw.Length == 0;
        if (request.Body.Mode.ContentType() is { } type && !empty && !Has("Content-Type"))
            headers.Add(("Content-Type", type));

        return new PreparedRequest
        {
            Method = method.Length == 0 ? "GET" : method,
            Url = url,
            Headers = headers,
            Body = body,
            BasicAuth = basicAuth,
        };
    }
}
