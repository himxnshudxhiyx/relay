using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relay.Core;

// Ported from Sources/Relay/Core/Postman.swift.

public sealed class ImportResult
{
    public List<ApiCollection> Collections { get; } = [];
    public List<ApiEnvironment> Environments { get; } = [];

    public bool IsEmpty => Collections.Count == 0 && Environments.Count == 0;
}

public sealed class PostmanException(string message) : Exception(message);

/// <summary>
/// Reads Postman collections, environments and data dumps; writes Collection v2.1.
///
/// Parsing walks a <see cref="JsonNode"/> tree rather than deserializing into
/// types: real exports are loose (a URL is a string or an object, auth params
/// an array or a dict, a description a string or <c>{content}</c>), and a
/// single strict type mismatch would otherwise reject a whole collection.
/// </summary>
public static class Postman
{
    public const string Schema = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json";

    static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // MARK: - Import

    public static ImportResult ImportData(byte[] data)
    {
        JsonNode? root;
        try
        {
            // Windows editors like to add a BOM; JSONSerialization skips it, System.Text.Json doesn't.
            var span = data.AsSpan();
            if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) span = span[3..];
            root = Parse(span);
        }
        catch (JsonException)
        {
            throw new PostmanException("The file isn't valid JSON.");
        }
        var result = new ImportResult();
        Collect(root, result);
        if (result.IsEmpty)
            throw new PostmanException("No Postman collections or environments were found in the file.");
        return result;
    }

    static void Collect(JsonNode? value, ImportResult result)
    {
        if (value is JsonArray array)
        {
            foreach (var element in array) Collect(element, result);
            return;
        }
        if (value is not JsonObject obj)
            throw new PostmanException("The file isn't a Postman collection or environment.");

        // Postman data dump, or a Relay workspace backup (same shape).
        if (obj["collections"] is JsonArray || obj["environments"] is JsonArray)
        {
            foreach (var c in obj["collections"] as JsonArray ?? []) Collect(c, result);
            foreach (var e in obj["environments"] as JsonArray ?? []) Collect(e, result);
            return;
        }
        // Postman API responses wrap the object in a single key.
        if (obj["collection"] is JsonObject wrappedCollection)
        {
            Collect(wrappedCollection, result);
            return;
        }
        if (obj["environment"] is JsonObject wrappedEnvironment)
        {
            Collect(wrappedEnvironment, result);
            return;
        }

        if (obj["info"] is JsonObject || obj["item"] is JsonArray)
            result.Collections.Add(Collection(obj));
        else if (obj["values"] is JsonArray)
            result.Environments.Add(Environment(obj));
        else if (obj["requests"] is JsonArray)
            throw new PostmanException("Postman v1 collections aren't supported. Export it again from Postman as Collection v2.1.");
        else
            throw new PostmanException("The file isn't a Postman collection or environment.");
    }

    public static ApiCollection Collection(JsonObject obj)
    {
        var info = obj["info"] as JsonObject ?? [];
        var c = new ApiCollection
        {
            Name = Str(info["name"]) ?? Str(obj["name"]) ?? "Imported Collection",
            Docs = Description(Either(info, obj, "description")),
            Variables = VariableRows(obj["variable"]),
            Items = Items(obj["item"]),
        };

        foreach (var (key, value) in obj)
            if (key is not ("info" or "item" or "variable" or "auth" or "name" or "description"))
                c.Extras[key] = Element(value);
        if (info.TryGetPropertyValue("_postman_id", out var id)) c.Extras["_postman_id"] = Element(id);
        c.Auth = AuthFrom(obj["auth"], Auth.NoAuth(), c.Extras);
        if (c.Auth.Type == AuthType.Inherit) c.Auth = Auth.NoAuth();
        return c;
    }

    static ObservableCollection<CollectionItem> Items(JsonNode? value) => [.. Objects(value).Select(Item)];

    static CollectionItem Item(JsonObject obj)
    {
        if (obj["item"] is JsonArray)
        {
            var f = new Folder
            {
                Name = Str(obj["name"]) ?? "Folder",
                Items = Items(obj["item"]),
                Docs = Description(obj["description"]),
            };
            foreach (var (key, value) in obj)
                if (key is not ("name" or "item" or "auth" or "description"))
                    f.Extras[key] = Element(value);
            f.Auth = AuthFrom(obj["auth"], Auth.Inherit(), f.Extras);
            return f;
        }

        var r = new ApiRequest { Name = Str(obj["name"]) ?? "Request" };
        // Item-level keys Relay doesn't model: id, response (saved examples), event (scripts), protocolProfileBehavior.
        foreach (var (key, value) in obj)
        {
            if (key is "name" or "request") continue;
            // Export writes `"response": []` when there are none; skipping it here keeps a round trip stable.
            if (key == "response" && value is JsonArray { Count: 0 }) continue;
            r.Extras[key] = Element(value);
        }

        var request = Text(obj["request"]) is { } urlString
            ? new JsonObject { ["url"] = urlString }
            : obj["request"] as JsonObject ?? [];

        r.Method = (Str(request["method"]) ?? "GET").ToUpperInvariant();
        var url = Url(request["url"]);
        r.Url = url.Url;
        r.Params = url.Params;
        if (url.Variable is { } variable) r.Extras["urlVariable"] = variable;
        r.Headers = Headers(request["header"]);
        r.Body = Body(request["body"]);
        r.Docs = Description(Either(request, obj, "description"));

        string[] known = ["method", "url", "header", "body", "auth", "description"];
        var unknown = new JsonObject(request.Where(p => !known.Contains(p.Key))
            .Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        if (unknown.Count > 0) r.Extras["request"] = Element(unknown);
        r.Auth = AuthFrom(request["auth"], Auth.Inherit(), r.Extras);
        return r;
    }

    static (string Url, ObservableCollection<KeyValue> Params, JsonElement? Variable) Url(JsonNode? value)
    {
        if (Text(value) is { } s) return (s, [.. UrlQuery.ParamsFromUrl(s, [])], null);
        if (value is not JsonObject o) return ("", [], null);

        var raw = Str(o["raw"]) ?? RebuildUrl(o);
        JsonElement? variable = o["variable"] is JsonArray { Count: > 0 } v ? Element(v) : null;

        if (o["query"] is not JsonArray query) return (raw, [.. UrlQuery.ParamsFromUrl(raw, [])], variable);
        // The query array is authoritative: it carries disabled params, which `raw` omits.
        var parameters = Objects(query).Select(q =>
            new KeyValue(Str(q["key"]) ?? "", Str(q["value"]) ?? "",
                         enabled: !Bool(q["disabled"]), description: Description(q["description"]))).ToList();
        var rawPairs = UrlQuery.Pairs(UrlQuery.Split(raw).Query ?? "");
        var enabledPairs = parameters.Where(p => p.Enabled && !p.IsBlank).Select(p => (p.Key, p.Value));
        // Only rewrite when they disagree, so `?a=` isn't needlessly normalised to `?a`.
        if (!rawPairs.SequenceEqual(enabledPairs)) raw = UrlQuery.UrlApplying(raw, parameters);
        return (raw, [.. parameters], variable);
    }

    static string RebuildUrl(JsonObject o)
    {
        var result = "";
        if (Str(o["protocol"]) is { Length: > 0 } proto) result += proto + "://";
        if (o["host"] is JsonArray host)
            result += string.Join(".", host.Select(Str).OfType<string>());
        else if (Str(o["host"]) is { } hostString)
            result += hostString;
        if (Str(o["port"]) is { Length: > 0 } port) result += ":" + port;
        if (o["path"] is JsonArray path)
        {
            var segments = path.Select(p => Str(p) ?? Str((p as JsonObject)?["value"])).OfType<string>().ToList();
            if (segments.Count > 0) result += "/" + string.Join("/", segments);
        }
        else if (Str(o["path"]) is { Length: > 0 } pathString)
        {
            result += pathString.StartsWith('/') ? pathString : "/" + pathString;
        }
        return result;
    }

    // Swift's Character.isNewline.
    static readonly char[] Newlines = ['\n', '\r', '\v', '\f', '\u0085', '\u2028', '\u2029'];

    static ObservableCollection<KeyValue> Headers(JsonNode? value)
    {
        if (Text(value) is not { } text) return Rows(value);
        // v2.0 allowed a raw header block. `//` marks a disabled line in Postman's bulk editor.
        var rows = new ObservableCollection<KeyValue>();
        foreach (var part in text.Split(Newlines, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = part.Trim();
            var disabled = line.StartsWith("//", StringComparison.Ordinal);
            if (disabled) line = line[2..].Trim();
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            rows.Add(new KeyValue(line[..colon].Trim(), line[(colon + 1)..].Trim(), enabled: !disabled));
        }
        return rows;
    }

    static ObservableCollection<KeyValue> Rows(JsonNode? value) =>
    [
        .. Objects(value).Select(row =>
            new KeyValue(Str(row["key"]) ?? "", Str(row["value"]) ?? "",
                         enabled: !Bool(row["disabled"]), description: Description(row["description"]))),
    ];

    static RequestBody Body(JsonNode? value)
    {
        if (value is not JsonObject o || Str(o["mode"]) is not { } mode) return new RequestBody();
        var b = new RequestBody();
        switch (mode)
        {
            case "raw":
                b.Raw = Str(o["raw"]) ?? "";
                var language = Text(o["options"]?["raw"] is JsonObject raw ? raw["language"] : null);
                switch (language?.ToLowerInvariant())
                {
                    case "json": b.Mode = BodyMode.Json; break;
                    case "xml": b.Mode = BodyMode.Xml; break;
                    case "text" or "html" or "javascript": b.Mode = BodyMode.Text; break;
                    default:
                        var trimmed = b.Raw.Trim();
                        var first = trimmed.Length > 0 ? trimmed[0] : '\0';
                        b.Mode = first is '{' or '[' ? BodyMode.Json : first == '<' ? BodyMode.Xml : BodyMode.Text;
                        break;
                }
                break;
            case "urlencoded":
                b.Mode = BodyMode.Form;
                b.Form = Rows(o["urlencoded"]);
                break;
            case "formdata":
                b.Mode = BodyMode.Multipart;
                b.Form = [.. Objects(o["formdata"]).Select(row =>
                {
                    var isFile = Str(row["type"]) == "file";
                    var src = Str(row["src"]) ?? (row["src"] as JsonArray)?.Select(Str).OfType<string>().FirstOrDefault();
                    return new KeyValue(Str(row["key"]) ?? "",
                                        isFile ? src ?? "" : Str(row["value"]) ?? "",
                                        enabled: !Bool(row["disabled"]),
                                        description: Description(row["description"]),
                                        isFile: isFile);
                })];
                break;
            case "file":
                b.Mode = BodyMode.File;
                b.FilePath = Str((o["file"] as JsonObject)?["src"]) ?? "";
                break;
            case "graphql":
                // Relay has no GraphQL editor; the JSON body a GraphQL server receives is the faithful equivalent.
                var graphql = o["graphql"] as JsonObject ?? [];
                var payload = new JsonObject { ["query"] = Str(graphql["query"]) ?? "" };
                if (Text(graphql["variables"]) is { } vars)
                {
                    var trimmedVars = vars.Trim();
                    if (trimmedVars.Length > 0)
                    {
                        JsonNode? parsed;
                        try { parsed = Parse(System.Text.Encoding.UTF8.GetBytes(trimmedVars)); }
                        catch (JsonException) { parsed = vars; }
                        payload["variables"] = Normalize(parsed, sortKeys: true);
                    }
                }
                else if (graphql["variables"] is { } other)
                {
                    payload["variables"] = Normalize(other, sortKeys: true);
                }
                b.Mode = BodyMode.Json;
                b.Raw = payload.ToJsonString(Pretty);
                break;
        }
        return b;
    }

    /// <summary>
    /// Auth types Relay can't edit (OAuth 2, Digest, AWS…) import as No Auth
    /// with the original stashed in <c>extras["auth"]</c>, so exporting gives it back untouched.
    /// </summary>
    static Auth AuthFrom(JsonNode? value, Auth missing, Dictionary<string, JsonElement> extras)
    {
        if (value is not JsonObject o || Str(o["type"]) is not { } type) return missing;

        string? Param(string name)
        {
            if (o[type] is JsonArray list)
            {
                foreach (var entry in list.OfType<JsonObject>())
                    if (Str(entry["key"]) == name) return Str(entry["value"]);
                return null;
            }
            return Str((o[type] as JsonObject)?[name]);
        }

        switch (type)
        {
            case "noauth": return Auth.NoAuth();
            case "inherit": return Auth.Inherit();
            case "bearer": return new Auth { Type = AuthType.Bearer, Token = Param("token") ?? "" };
            case "basic": return new Auth { Type = AuthType.Basic, Username = Param("username") ?? "", Password = Param("password") ?? "" };
            case "apikey":
                return new Auth
                {
                    Type = AuthType.ApiKey, Key = Param("key") ?? "", Value = Param("value") ?? "",
                    Location = Param("in") == "query" ? ApiKeyLocation.Query : ApiKeyLocation.Header,
                };
            default:
                extras["auth"] = Element(o);
                return Auth.NoAuth();
        }
    }

    static ObservableCollection<KeyValue> VariableRows(JsonNode? value) =>
    [
        .. Objects(value).Select(row =>
            new KeyValue(Str(row["key"]) ?? "", Str(row["value"]) ?? "",
                         enabled: !Bool(row["disabled"]), description: Description(row["description"]),
                         isSecret: Str(row["type"]) == "secret")),
    ];

    public static ApiEnvironment Environment(JsonObject obj) => new()
    {
        Name = Str(obj["name"]) ?? "Imported Environment",
        Variables =
        [
            .. Objects(obj["values"]).Select(row =>
                new KeyValue(Str(row["key"]) ?? "", Str(row["value"]) ?? "",
                             enabled: !row.TryGetPropertyValue("enabled", out var enabled) || Bool(enabled),
                             isSecret: Str(row["type"]) == "secret")),
        ],
    };

    // MARK: - Export

    public static byte[] ExportCollection(ApiCollection c) => Serialize(CollectionObject(c));

    public static byte[] ExportEnvironment(ApiEnvironment e) => Serialize(EnvironmentObject(e));

    public static byte[] ExportBackup(IEnumerable<ApiCollection> collections, IEnumerable<ApiEnvironment> environments) =>
        Serialize(new JsonObject
        {
            ["version"] = 1,
            ["collections"] = new JsonArray([.. collections.Select(CollectionObject)]),
            ["environments"] = new JsonArray([.. environments.Select(EnvironmentObject)]),
        });

    public static JsonObject CollectionObject(ApiCollection c)
    {
        var o = Merged(c.Extras, "_postman_id", "auth");
        var info = new JsonObject
        {
            ["name"] = c.Name,
            ["schema"] = Schema,
            ["_postman_id"] = c.Extras.TryGetValue("_postman_id", out var id) ? AnyValue(id) : c.Id.ToString(),
        };
        if (c.Docs.Length > 0) info["description"] = c.Docs;
        o["info"] = info;
        o["item"] = new JsonArray([.. c.Items.Select(ItemObject)]);
        if (c.Variables.Count > 0) o["variable"] = new JsonArray([.. c.Variables.Select(VariableObject)]);
        o["auth"] = AuthObject(c.Auth, c.Extras) ?? new JsonObject { ["type"] = "noauth" };
        return o;
    }

    static JsonObject ItemObject(CollectionItem item)
    {
        if (item is Folder f)
        {
            var fo = Merged(f.Extras, "auth");
            fo["name"] = f.Name;
            fo["item"] = new JsonArray([.. f.Items.Select(ItemObject)]);
            if (f.Docs.Length > 0) fo["description"] = f.Docs;
            if (AuthObject(f.Auth, f.Extras) is { } folderAuth) fo["auth"] = folderAuth;
            return fo;
        }

        var r = (ApiRequest)item;
        var o = Merged(r.Extras, "auth", "request", "urlVariable");
        o["name"] = r.Name;
        var request = r.Extras.TryGetValue("request", out var extra) && AnyValue(extra) is JsonObject stashed ? stashed : [];
        request["method"] = r.Method;
        request["header"] = new JsonArray([.. r.Headers.Select(h => RowObject(h))]);
        var url = UrlObject(r.Url, r.Params);
        if (r.Extras.TryGetValue("urlVariable", out var variable)) url["variable"] = AnyValue(variable);
        request["url"] = url;
        if (BodyObject(r.Body) is { } body) request["body"] = body;
        if (AuthObject(r.Auth, r.Extras) is { } auth) request["auth"] = auth;
        if (r.Docs.Length > 0) request["description"] = r.Docs;
        o["request"] = request;
        if (!o.ContainsKey("response")) o["response"] = new JsonArray();
        return o;
    }

    /// <summary>
    /// Postman's structured URL. <c>raw</c> is what Postman actually shows and
    /// sends; the parts exist for its own tooling, so they're best-effort.
    /// </summary>
    public static JsonObject UrlObject(string raw, IEnumerable<KeyValue> parameters)
    {
        var o = new JsonObject { ["raw"] = raw };
        var rest = UrlQuery.Split(raw).Base;

        var scheme = rest.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            o["protocol"] = rest[..scheme];
            rest = rest[(scheme + 3)..];
        }

        var hostAndPath = SplitOutsideBraces(rest, '/', maxSplits: 1);
        var hostPort = hostAndPath[0];
        var path = hostAndPath.Count > 1 ? hostAndPath[1] : null;
        var colon = hostPort.LastIndexOf(':');
        if (colon >= 0)
        {
            var port = hostPort[(colon + 1)..];
            if (port.Length > 0 && port.All(char.IsNumber))
            {
                o["port"] = port;
                hostPort = hostPort[..colon];
            }
        }
        if (hostPort.Length > 0) o["host"] = Strings(SplitOutsideBraces(hostPort, '.'));
        if (path != null) o["path"] = Strings(SplitOutsideBraces(path, '/'));
        var rows = parameters.ToList();
        if (rows.Count > 0) o["query"] = new JsonArray([.. rows.Select(p => RowObject(p))]);
        return o;
    }

    static JsonObject? BodyObject(RequestBody b)
    {
        switch (b.Mode)
        {
            case BodyMode.Json or BodyMode.Text or BodyMode.Xml:
                var language = b.Mode == BodyMode.Json ? "json" : b.Mode == BodyMode.Xml ? "xml" : "text";
                return new JsonObject
                {
                    ["mode"] = "raw",
                    ["raw"] = b.Raw,
                    ["options"] = new JsonObject { ["raw"] = new JsonObject { ["language"] = language } },
                };
            case BodyMode.Form:
                return new JsonObject { ["mode"] = "urlencoded", ["urlencoded"] = new JsonArray([.. b.Form.Select(r => RowObject(r))]) };
            case BodyMode.Multipart:
                return new JsonObject
                {
                    ["mode"] = "formdata",
                    ["formdata"] = new JsonArray([.. b.Form.Select(row =>
                    {
                        if (!row.IsFile) return RowObject(row);
                        var o = RowObject(row, "file");
                        o.Remove("value");
                        o["src"] = row.Value;
                        return o;
                    })]),
                };
            case BodyMode.File:
                return new JsonObject { ["mode"] = "file", ["file"] = new JsonObject { ["src"] = b.FilePath } };
            default:
                return null;
        }
    }

    /// <summary>null means "leave the key out", which Postman reads as inherit.</summary>
    static JsonObject? AuthObject(Auth auth, Dictionary<string, JsonElement> extras)
    {
        static JsonObject Typed(string type, params (string Key, string Value)[] pairs) => new()
        {
            ["type"] = type,
            [type] = new JsonArray([.. pairs.Select(p => new JsonObject { ["key"] = p.Key, ["value"] = p.Value, ["type"] = "string" })]),
        };

        return auth.Type switch
        {
            AuthType.Inherit => null,
            AuthType.None => extras.TryGetValue("auth", out var stashed) && AnyValue(stashed) is JsonObject o
                ? o : new JsonObject { ["type"] = "noauth" },
            AuthType.Bearer => Typed("bearer", ("token", auth.Token)),
            AuthType.Basic => Typed("basic", ("username", auth.Username), ("password", auth.Password)),
            _ => Typed("apikey", ("key", auth.Key), ("value", auth.Value),
                       ("in", auth.Location == ApiKeyLocation.Query ? "query" : "header")),
        };
    }

    static JsonObject RowObject(KeyValue row, string type = "text")
    {
        var o = new JsonObject { ["key"] = row.Key, ["value"] = row.Value, ["type"] = type };
        if (!row.Enabled) o["disabled"] = true;
        if (row.Description.Length > 0) o["description"] = row.Description;
        return o;
    }

    static JsonObject VariableObject(KeyValue row) => RowObject(row, row.IsSecret ? "secret" : "string");

    public static JsonObject EnvironmentObject(ApiEnvironment e) => new()
    {
        ["id"] = e.Id.ToString(),
        ["name"] = e.Name,
        ["values"] = new JsonArray([.. e.Variables.Select(row => new JsonObject
        {
            ["key"] = row.Key,
            ["value"] = row.Value,
            ["type"] = row.IsSecret ? "secret" : "default",
            ["enabled"] = row.Enabled,
        })]),
        ["_postman_variable_scope"] = "environment",
    };

    static byte[] Serialize(JsonNode node) => JsonSerializer.SerializeToUtf8Bytes(node, Pretty);

    // MARK: - Helpers

    static JsonObject Merged(Dictionary<string, JsonElement> extras, params string[] excluding)
    {
        var o = new JsonObject();
        foreach (var (key, value) in extras)
            if (!excluding.Contains(key)) o[key] = AnyValue(value);
        return o;
    }

    static IEnumerable<JsonObject> Objects(JsonNode? value) => (value as JsonArray)?.OfType<JsonObject>() ?? [];

    static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)v)]);

    /// <summary><c>a[key] ?? b[key]</c> as Swift reads it: a key present with null still wins.</summary>
    static JsonNode? Either(JsonObject a, JsonObject b, string key) => a.TryGetPropertyValue(key, out var v) ? v : b[key];

    /// <summary>Only an actual JSON string (Swift's <c>as? String</c>).</summary>
    static string? Text(JsonNode? value) => value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>
    /// Strings as-is; numbers and booleans as they'd be typed. Postman exports
    /// <c>"value": 3000</c> as readily as <c>"value": "3000"</c>.
    /// </summary>
    static string? Str(JsonNode? value) => value?.GetValueKind() switch
    {
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        // Swift prints doubles as 1e-05, .NET as 1E-05.
        JsonValueKind.Number => value.GetValue<double>() is var d && IsWhole(d)
            ? ((long)d).ToString(CultureInfo.InvariantCulture)
            : d.ToString("R", CultureInfo.InvariantCulture).Replace('E', 'e'),
        _ => null,
    };

    static bool Bool(JsonNode? value) => value?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => value.GetValue<string>().ToLowerInvariant() == "true",
        // A JSON number reaches Swift as an NSNumber, which casts to Bool when it's exactly 0 or 1.
        JsonValueKind.Number => value.GetValue<double>() == 1,
        _ => false,
    };

    static string Description(JsonNode? value) => Str(value) ?? Str((value as JsonObject)?["content"]) ?? "";

    static bool IsWhole(double d) => Math.Truncate(d) == d && Math.Abs(d) < 1e15;

    /// <summary>
    /// A detached copy with numbers as the Mac's JSONValue keeps them: a double,
    /// written back as an integer when it's whole (so 1.50 comes back as 1.5).
    /// </summary>
    static JsonNode? Normalize(JsonNode? node, bool sortKeys = false) => node switch
    {
        JsonObject o => new JsonObject((sortKeys ? o.OrderBy(p => p.Key, StringComparer.Ordinal) : o.AsEnumerable())
            .Select(p => KeyValuePair.Create(p.Key, Normalize(p.Value, sortKeys)))),
        JsonArray a => new JsonArray([.. a.Select(x => Normalize(x, sortKeys))]),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.GetValue<double>() is var d && IsWhole(d)
            ? JsonValue.Create((long)d) : JsonValue.Create(d),
        _ => node?.DeepClone(),
    };

    static JsonNode? Parse(ReadOnlySpan<byte> utf8) => Node(JsonSerializer.Deserialize<JsonElement>(utf8));

    /// <summary>
    /// Like JSONSerialization: a repeated key keeps the last value instead of
    /// throwing (JsonNode.Parse would), and an out-of-range number is invalid JSON.
    /// </summary>
    static JsonNode? Node(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var o = new JsonObject();
                foreach (var p in e.EnumerateObject()) o[p.Name] = Node(p.Value);
                return o;
            case JsonValueKind.Array:
                return new JsonArray([.. e.EnumerateArray().Select(Node)]);
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Number when !e.TryGetDouble(out _):
                throw new JsonException("Number out of range.");
            default:
                return JsonValue.Create(e);
        }
    }

    /// <summary>For extras, like Swift's <c>JSONValue(any:)</c>.</summary>
    static JsonElement Element(JsonNode? node) => JsonSerializer.SerializeToElement(Normalize(node));

    /// <summary>Back out of extras, like Swift's <c>anyValue</c>.</summary>
    static JsonNode? AnyValue(JsonElement e) => Normalize(Node(e));

    /// <summary>Splits on <paramref name="separator"/>, but never inside <c>{{…}}</c>: <c>{{api.host}}</c> is one host part.</summary>
    static List<string> SplitOutsideBraces(string s, char separator, int maxSplits = int.MaxValue)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;
        var i = 0;
        while (i < s.Length)
        {
            if (i + 1 < s.Length && s[i] == '{' && s[i + 1] == '{')
            {
                depth++;
                current.Append("{{");
                i += 2;
            }
            else if (i + 1 < s.Length && s[i] == '}' && s[i + 1] == '}' && depth > 0)
            {
                depth--;
                current.Append("}}");
                i += 2;
            }
            else if (depth == 0 && s[i] == separator && parts.Count < maxSplits)
            {
                parts.Add(current.ToString());
                current.Clear();
                i++;
            }
            else
            {
                current.Append(s[i]);
                i++;
            }
        }
        parts.Add(current.ToString());
        return parts;
    }
}
