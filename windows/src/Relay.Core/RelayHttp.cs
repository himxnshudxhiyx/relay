using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Relay.Core;

// Ported from HTTPClient.swift.

/// <summary>Swift's SendOptions, read from Settings (UserDefaults keys timeout, followRedirects, verifySSL).</summary>
public sealed record HttpSettings
{
    /// <summary>An idle limit, like URLRequest's timeoutInterval: waiting for headers, or between body chunks.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool FollowRedirects { get; init; } = true;
    public bool VerifySsl { get; init; } = true;
}

public enum BodyKind { Empty, Json, Xml, Html, Image, Text, Binary }

public sealed class HttpResult
{
    public required int Status { get; init; }
    public required List<(string Name, string Value)> Headers { get; init; }
    public required byte[] Body { get; init; }
    /// <summary>Seconds, including redirects and the body download.</summary>
    public required double Duration { get; init; }
    /// <summary>Seconds from sending the final request to its response headers.</summary>
    public double? Ttfb { get; init; }
    public int Redirects { get; init; }
    public required string FinalUrl { get; init; }
    public DateTime Date { get; init; } = DateTime.UtcNow;

    // What actually went out, for the Request tab.
    public required string SentMethod { get; init; }
    public required string SentUrl { get; init; }
    public required List<(string Name, string Value)> SentHeaders { get; init; }
    public required string SentBody { get; init; }

    // Worked out once, off the UI thread, so switching tabs never re-parses a large body.
    public required BodyKind Kind { get; init; }
    public required string Text { get; init; }
    public string? Pretty { get; init; }
    /// <summary>The cURL for exactly what was sent, even if the request is edited afterwards. Set by the caller.</summary>
    public string Curl { get; set; } = "";
    /// <summary>Base64 files inside a JSON body (a PDF, an image), for the Preview view.</summary>
    public List<EmbeddedFile> Files { get; init; } = [];

    public string StatusText => HttpStatus.Phrase(Status);
    public string ContentType => Header("Content-Type") ?? "";

    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>A failure worth showing as is, already worded for the user.</summary>
public sealed class HttpError(string message) : Exception(message);

public static class RelayHttp
{
    // One cookie jar for the app's lifetime: cookies set by a login request carry
    // to the next request, as in a browser, but nothing is written to disk.
    static readonly CookieContainer Cookies = new();
    // Two pools because certificate checking is per handler. Redirects are followed by hand to count them.
    static readonly HttpMessageInvoker Verified = Client(verify: true);
    static readonly HttpMessageInvoker Unverified = Client(verify: false);

    static HttpMessageInvoker Client(bool verify) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        CookieContainer = Cookies,
        SslOptions = verify ? new() : new() { RemoteCertificateValidationCallback = (_, _, _, _) => true },
    });

    /// <summary>URLSession gives up after 16 hops.</summary>
    const int MaxRedirects = 16;

    sealed record Wire(string Method, Uri Url, List<(string Name, string Value)> Headers, byte[]? Body);

    /// <exception cref="HttpError">Bad URL, unreadable file, or a network failure.</exception>
    /// <exception cref="OperationCanceledException">When <paramref name="cancellationToken"/> fires.</exception>
    public static async Task<HttpResult> SendAsync(PreparedRequest prepared, HttpSettings settings, CancellationToken cancellationToken = default)
    {
        var request = BuildWire(prepared);
        var client = settings.VerifySsl ? Verified : Unverified;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var started = Stopwatch.StartNew();

        var current = request;
        var redirects = 0;
        var finalUrl = request.Url.OriginalString;
        HttpResponseMessage response;
        double ttfb;
        byte[] data;
        try
        {
            while (true)
            {
                timeout.CancelAfter(settings.Timeout);
                var hop = Stopwatch.StartNew();
                response = await client.SendAsync(Message(current), timeout.Token);
                ttfb = hop.Elapsed.TotalSeconds;
                if (!settings.FollowRedirects || Redirect(current, response) is not { } next) break;
                // A 3xx handed back as the response when not following; otherwise go round again.
                if (++redirects > MaxRedirects) { response.Dispose(); throw new HttpError("too many HTTP redirects"); }
                response.Dispose();
                current = next;
                finalUrl = next.Url.AbsoluteUri;
            }
            using (response)
            {
                // Reset the idle timer on each chunk, so a slow but steady download isn't cut off.
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    timeout.CancelAfter(settings.Timeout);
                }
                data = buffer.ToArray();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpError($"The request timed out after {(int)settings.Timeout.TotalSeconds} seconds. You can raise the limit in Settings.");
        }
        catch (HttpRequestException e)
        {
            throw new HttpError(Describe(e, request.Url));
        }
        catch (IOException)
        {
            throw new HttpError($"The connection to {request.Url.Host} was lost.");
        }
        var duration = started.Elapsed.TotalSeconds;

        var headers = response.Headers.NonValidated.Concat(response.Content.Headers.NonValidated)
            .Select(h => (h.Key, h.Value.ToString()))
            .OrderBy(h => h.Key.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();
        var sentHeaders = request.Headers.OrderBy(h => h.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();
        var status = (int)response.StatusCode;
        var sentBody = DescribeBody(prepared.Body, request.Body?.Length ?? 0);

        return await Task.Run(() =>
        {
            var contentType = headers.FirstOrDefault(h => string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)).Item2 ?? "";
            var (kind, text, pretty) = Interpret(data, contentType);
            return new HttpResult
            {
                Status = status, Headers = headers, Body = data, Duration = duration, Ttfb = ttfb,
                Redirects = redirects, FinalUrl = finalUrl,
                SentMethod = request.Method, SentUrl = request.Url.OriginalString, SentHeaders = sentHeaders, SentBody = sentBody,
                Kind = kind, Text = text, Pretty = pretty,
                Files = kind == BodyKind.Json ? EmbeddedFile.Find(data) : [],
            };
        }, CancellationToken.None);
    }

    /// <summary>Swift's urlRequest(for:): validates the URL and builds the body and headers to send.</summary>
    static Wire BuildWire(PreparedRequest prepared)
    {
        if (prepared.Url.Length == 0) throw new HttpError("Enter a URL to send the request.");
        if (UrlQuery.EncodedUrl(prepared.Url) is not { } url
            || url.Scheme.ToLowerInvariant() is not ("http" or "https")
            || url.Host.Length == 0)
            throw new HttpError($"“{prepared.Url}” isn't a valid http or https URL.");
        // URLSession sends any method; HttpMethod rejects non-token characters, so say so instead of throwing FormatException.
        try { _ = new HttpMethod(prepared.Method); }
        catch (FormatException) { throw new HttpError($"“{prepared.Method}” isn't a valid HTTP method."); }

        // URLRequest.addValue joins a repeated name into one field with ",".
        var headers = new List<(string Name, string Value)>();
        foreach (var (name, value) in prepared.WireHeaders)
        {
            var i = headers.FindIndex(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) headers.Add((name, value));
            else headers[i] = (headers[i].Name, headers[i].Value + "," + value);
        }

        byte[]? body = null;
        switch (prepared.Body)
        {
            case PreparedBody.Raw raw:
                body = Encoding.UTF8.GetBytes(raw.Text);
                break;
            case PreparedBody.Form form:
                body = Encoding.UTF8.GetBytes(string.Join("&", form.Fields.Select(f => $"{UrlQuery.FormEncode(f.Key)}={UrlQuery.FormEncode(f.Value)}")));
                break;
            case PreparedBody.Multipart multipart:
                var boundary = "RelayBoundary" + Guid.NewGuid().ToString("N").ToUpperInvariant();
                body = MultipartBody(multipart.Rows, boundary);
                // Always ours: a hand-written multipart Content-Type has no boundary to match the body.
                headers.RemoveAll(h => string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase));
                headers.Add(("Content-Type", $"multipart/form-data; boundary={boundary}"));
                break;
            case PreparedBody.File file:
                body = ReadFile(file.Path);
                break;
        }
        return new Wire(prepared.Method, url, headers, body);
    }

    static HttpRequestMessage Message(Wire wire)
    {
        var message = new HttpRequestMessage(new HttpMethod(wire.Method), wire.Url)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        if (wire.Body != null) message.Content = new ByteArrayContent(wire.Body);
        foreach (var (name, value) in wire.Headers)
        {
            // The body decides its length, as URLSession does.
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (message.Headers.TryAddWithoutValidation(name, value)) continue;
            // Content-Type and friends live on the content, even with no body.
            message.Content ??= new ByteArrayContent([]);
            message.Content.Headers.TryAddWithoutValidation(name, value);
        }
        // URLSession's defaults, when the request doesn't set its own.
        if (!message.Headers.Contains("User-Agent")) message.Headers.TryAddWithoutValidation("User-Agent", "Relay/1.0");
        if (!message.Headers.Contains("Accept")) message.Headers.TryAddWithoutValidation("Accept", "*/*");
        return message;
    }

    /// <summary>The next request for a 3xx with a Location, following URLSession's (and browsers') method rules.</summary>
    static Wire? Redirect(Wire current, HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        if (code is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location) return null;
        var url = location.IsAbsoluteUri ? location : new Uri(current.Url, location);
        if (url.Scheme is not ("http" or "https")) return null;
        var headers = current.Headers;
        // Credentials stay with the host they were meant for.
        if (!string.Equals(url.Host, current.Url.Host, StringComparison.OrdinalIgnoreCase))
            headers = headers.Where(h => !string.Equals(h.Name, "Authorization", StringComparison.OrdinalIgnoreCase)).ToList();
        var toGet = code == 303 ? current.Method != "HEAD" : code is 301 or 302 && current.Method == "POST";
        if (!toGet) return current with { Url = url, Headers = headers };
        headers = headers.Where(h => !h.Name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)).ToList();
        return new Wire("GET", url, headers, null);
    }

    static byte[] MultipartBody(List<KeyValue> parts, string boundary)
    {
        static string Quoted(string s) => s.Replace("\"", "%22");
        var data = new MemoryStream();
        void Write(string s) => data.Write(Encoding.UTF8.GetBytes(s));
        foreach (var part in parts)
        {
            Write($"--{boundary}\r\n");
            if (part.IsFile)
            {
                var path = ExpandTilde(part.Value);
                var file = ReadFile(path);
                var name = Path.GetFileName(path);
                var mime = MimeType(Path.GetExtension(path));
                Write($"Content-Disposition: form-data; name=\"{Quoted(part.Key)}\"; filename=\"{Quoted(name)}\"\r\nContent-Type: {mime}\r\n\r\n");
                data.Write(file);
            }
            else
            {
                Write($"Content-Disposition: form-data; name=\"{Quoted(part.Key)}\"\r\n\r\n{part.Value}");
            }
            Write("\r\n");
        }
        Write($"--{boundary}--\r\n");
        return data.ToArray();
    }

    // ponytail: common types only (Swift asks UTType); add entries when an upload needs one.
    static string MimeType(string extension) => extension.TrimStart('.').ToLowerInvariant() switch
    {
        "json" => "application/json",
        "xml" => "application/xml",
        "pdf" => "application/pdf",
        "zip" => "application/zip",
        "txt" => "text/plain",
        "csv" => "text/csv",
        "html" or "htm" => "text/html",
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "svg" => "image/svg+xml",
        "mp3" => "audio/mpeg",
        "mp4" => "video/mp4",
        "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "application/octet-stream",
    };

    static string ExpandTilde(string path) =>
        path == "~" || path.StartsWith("~/") || path.StartsWith("~\\")
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..]
            : path;

    static byte[] ReadFile(string path)
    {
        var expanded = ExpandTilde(path);
        if (expanded.Length == 0) throw new HttpError("Choose a file to send.");
        try
        {
            return File.ReadAllBytes(expanded);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new HttpError($"Couldn't read the file at {expanded}.");
        }
    }

    static string DescribeBody(PreparedBody body, int bytes) => body switch
    {
        PreparedBody.Raw raw => raw.Text,
        PreparedBody.Form form => string.Join("&", form.Fields.Select(f => $"{f.Key}={f.Value}")),
        PreparedBody.Multipart m => string.Join("\n", m.Rows.Select(r => $"{r.Key}={(r.IsFile ? "@" + r.Value : r.Value)}")),
        PreparedBody.File file => $"@{file.Path} ({Format.Bytes(bytes)})",
        _ => "",
    };

    internal static (BodyKind Kind, string Text, string? Pretty) Interpret(byte[] data, string contentType)
    {
        var type = contentType.ToLowerInvariant();
        if (data.Length == 0) return (BodyKind.Empty, "", null);
        if (type.StartsWith("image/")) return (BodyKind.Image, "", null);
        if (LooksBinary(data)) return (BodyKind.Binary, "", null);

        var text = System.Text.Unicode.Utf8.IsValid(data) ? Encoding.UTF8.GetString(data) : Encoding.Latin1.GetString(data);
        // Validate once; Pretty would parse again.
        var valid = JsonFormat.IsJson(text);
        if (type.Contains("json") || valid) return (BodyKind.Json, text, valid ? JsonFormat.Reformat(text, 2) : null);
        if (type.Contains("html")) return (BodyKind.Html, text, null);
        if (type.Contains("xml") || text.StartsWith("<?xml", StringComparison.Ordinal)) return (BodyKind.Xml, text, XmlFormat.Pretty(text));
        return (BodyKind.Text, text, null);
    }

    /// <summary>NUL bytes in the first few KB mean it isn't text, whatever the header claims.</summary>
    static bool LooksBinary(byte[] data) => data.AsSpan(0, Math.Min(4096, data.Length)).Contains((byte)0);

    static string Describe(HttpRequestException error, Uri url)
    {
        var host = url.Host.Length > 0 ? url.Host : "the server";
        return error.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => $"Couldn't find {host}. Check the URL and your connection.",
            HttpRequestError.ConnectionError => $"Couldn't connect to {host}. Is the server running?",
            HttpRequestError.ResponseEnded => $"The connection to {host} was lost.",
            HttpRequestError.SecureConnectionError =>
                $"{host}'s SSL certificate isn't trusted. For local or self-signed servers, turn off certificate verification in Settings.",
            _ => error.Message,
        };
    }
}

public static class HttpStatus
{
    /// <summary>Spelled out: the platform's own phrases differ by OS (macOS says "no error" for 200).</summary>
    public static string Phrase(int code) => code switch
    {
        100 => "Continue",
        101 => "Switching Protocols",
        200 => "OK",
        201 => "Created",
        202 => "Accepted",
        203 => "Non-Authoritative Information",
        204 => "No Content",
        206 => "Partial Content",
        301 => "Moved Permanently",
        302 => "Found",
        303 => "See Other",
        304 => "Not Modified",
        307 => "Temporary Redirect",
        308 => "Permanent Redirect",
        400 => "Bad Request",
        401 => "Unauthorized",
        402 => "Payment Required",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        406 => "Not Acceptable",
        408 => "Request Timeout",
        409 => "Conflict",
        410 => "Gone",
        411 => "Length Required",
        412 => "Precondition Failed",
        413 => "Payload Too Large",
        415 => "Unsupported Media Type",
        418 => "I'm a Teapot",
        422 => "Unprocessable Entity",
        423 => "Locked",
        425 => "Too Early",
        428 => "Precondition Required",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        451 => "Unavailable For Legal Reasons",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        505 => "HTTP Version Not Supported",
        _ => "",
    };
}

public static partial class Format
{
    /// <summary>
    /// A file name for a saved response: the name you gave the request, or for
    /// an unnamed one its URL path without the host and a leading version
    /// (https://x.com/v1/gst/gstr1/getSection → gst/gstr1/getSection).
    /// </summary>
    public static string ResponseFileName(string name, string url)
    {
        var generated = name == ApiRequest.DefaultName || name == RequestName(url);
        if (!generated) return name;
        var path = UrlQuery.Split(url.Trim(' ', '\t')).Base;
        var scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) path = path[(scheme + 3)..];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();  // the host
        if (segments.Count > 0 && VersionSegment().IsMatch(segments[0])) segments.RemoveAt(0);
        return segments.Count == 0 ? "response" : string.Join("/", segments);
    }

    [GeneratedRegex(@"^v\d+$")]
    private static partial Regex VersionSegment();

    /// <summary>Private copy of CurlParser.RequestName, so this file doesn't depend on the cURL port.</summary>
    static string RequestName(string url)
    {
        var rest = UrlQuery.Split(url.Trim(' ', '\t')).Base;
        var scheme = rest.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) rest = rest[(scheme + 3)..];
        // Never put credentials from user:pass@host into a name shown on screen.
        var authorityEnd = rest.IndexOf('/') is var slash and >= 0 ? slash : rest.Length;
        var at = rest.AsSpan(0, authorityEnd).LastIndexOf('@');
        if (at >= 0) rest = rest[(at + 1)..];
        while (rest.Length > 1 && rest.EndsWith('/')) rest = rest[..^1];
        if (rest.Length == 0) return ApiRequest.DefaultName;
        var info = new StringInfo(rest);
        return info.LengthInTextElements > 80 ? info.SubstringByTextElements(0, 79) + "…" : rest;
    }

    public static string Duration(double seconds) =>
        seconds < 1
            ? $"{(long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero)} ms"
            : seconds.ToString("F2", CultureInfo.InvariantCulture) + " s";

    /// <summary>Like ByteCountFormatter's .file style: decimal units, KB whole, MB one decimal, larger two.</summary>
    public static string Bytes(long count)
    {
        if (count < 1024) return $"{count} B";
        string[] units = ["KB", "MB", "GB", "TB", "PB"];
        string[] formats = ["0", "0.#", "0.##", "0.##", "0.##"];
        var value = count / 1000.0;
        var u = 0;
        while (u < units.Length - 1 && Math.Round(value, u == 0 ? 0 : Math.Min(u, 2), MidpointRounding.AwayFromZero) >= 1000)
        {
            value /= 1000;
            u++;
        }
        return value.ToString(formats[u], CultureInfo.CurrentCulture) + " " + units[u];
    }

    /// <summary>
    /// The "cURL + response" clipboard text: everything someone needs to
    /// reproduce and read a call, pasted into a ticket or chat.
    /// </summary>
    public static string CurlWithResponse(string curl, HttpResult result, bool includeHeaders)
    {
        var out_ = new StringBuilder(curl).Append("\n\n");
        out_.Append($"HTTP {result.Status} {result.StatusText}".Trim(' ', '\t'));
        out_.Append($"  ({Duration(result.Duration)}, {Bytes(result.Body.Length)})\n");
        if (includeHeaders) out_.Append(string.Join("\n", result.Headers.Select(h => $"{h.Name}: {h.Value}"))).Append('\n');
        var body = result.Pretty ?? result.Text;
        if (body.Length > 0) out_.Append('\n').Append(body);
        return out_.ToString();
    }
}
