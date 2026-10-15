using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Relay.Core;

// Ported from EmbeddedFiles.swift.

public enum EmbeddedFileKind { Pdf, Png, Jpeg, Gif, Webp }

/// <summary>
/// A file sent inside a JSON response as a base64 string, such as
/// {"fileName": "GSTR1", "pdf": "JVBERi0x…"}. Recognised by its first bytes,
/// so the key's name doesn't matter.
/// </summary>
/// <param name="Path">Where it sits, e.g. data.pdf or files[0].content.</param>
/// <param name="SuggestedName">A sibling fileName/filename/name, when the response gives one.</param>
public sealed record EmbeddedFile(string Path, EmbeddedFileKind Kind, byte[] Data, string? SuggestedName)
{
    public bool IsImage => Kind != EmbeddedFileKind.Pdf;
    public string FileExtension => Kind == EmbeddedFileKind.Jpeg ? "jpg" : Kind.ToString().ToLowerInvariant();

    /// <summary>Shorter strings are never files worth previewing.</summary>
    public const int MinimumLength = 64;

    static readonly SearchValues<char> Base64Chars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=");

    public static List<EmbeddedFile> Find(ReadOnlyMemory<byte> json)
    {
        var found = new List<EmbeddedFile>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = JsonFormat.MaxDepth });
            Walk(doc.RootElement, "", found);
        }
        catch (JsonException)
        {
            return [];
        }
        return found;
    }

    static void Walk(JsonElement value, string path, List<EmbeddedFile> found)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            // Last duplicate key wins, as in a decoded dictionary.
            var obj = new Dictionary<string, JsonElement>();
            foreach (var p in value.EnumerateObject()) obj[p.Name] = p.Value;
            string? name = null;
            foreach (var k in (string[])["fileName", "filename", "file_name", "name"])
                if (obj.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String) { name = v.GetString(); break; }
            foreach (var key in obj.Keys.Order(StringComparer.Ordinal))
            {
                var child = path.Length == 0 ? key : $"{path}.{key}";
                var element = obj[key];
                if (element.ValueKind == JsonValueKind.String && Decode(element.GetString()!, child, name) is { } file)
                    found.Add(file);
                else
                    Walk(element, child, found);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var element in value.EnumerateArray())
            {
                var child = $"{path}[{i++}]";
                if (element.ValueKind == JsonValueKind.String && Decode(element.GetString()!, child, null) is { } file)
                    found.Add(file);
                else
                    Walk(element, child, found);
            }
        }
    }

    public static EmbeddedFile? Decode(string s, string path, string? name)
    {
        if (Encoding.UTF8.GetByteCount(s) < MinimumLength) return null;
        // data:application/pdf;base64, prefixes are common; the bytes decide the type either way.
        var payload = s.AsSpan();
        if (payload.StartsWith("data:") && payload.IndexOf(',') is var comma and >= 0) payload = payload[(comma + 1)..];
        // Check the first bytes before decoding the whole string. Strict, like Data(base64Encoded:):
        // Convert would skip whitespace, so reject anything outside the alphabet first.
        var headChars = payload[..Math.Min(16, payload.Length)];
        Span<byte> head = stackalloc byte[12];
        if (headChars.ContainsAnyExcept(Base64Chars) || !Convert.TryFromBase64Chars(headChars, head, out var headLength)) return null;
        if (KindOf(head[..headLength]) is not { } kind) return null;
        // .ignoreUnknownCharacters: drop anything outside the alphabet (line breaks in MIME base64).
        var clean = payload.ContainsAnyExcept(Base64Chars) ? Filter(payload) : payload;
        var data = new byte[clean.Length / 4 * 3];
        if (!Convert.TryFromBase64Chars(clean, data, out var written)) return null;
        Array.Resize(ref data, written);
        return new EmbeddedFile(path, kind, data, name);
    }

    static ReadOnlySpan<char> Filter(ReadOnlySpan<char> s)
    {
        var buffer = new char[s.Length];
        var n = 0;
        foreach (var c in s) if (Base64Chars.Contains(c)) buffer[n++] = c;
        return buffer.AsSpan(0, n);
    }

    static EmbeddedFileKind? KindOf(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith((byte[])[0x25, 0x50, 0x44, 0x46])) return EmbeddedFileKind.Pdf;   // %PDF
        if (b.StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47])) return EmbeddedFileKind.Png;
        if (b.StartsWith((byte[])[0xFF, 0xD8, 0xFF])) return EmbeddedFileKind.Jpeg;
        if (b.StartsWith((byte[])[0x47, 0x49, 0x46, 0x38])) return EmbeddedFileKind.Gif;   // GIF8
        if (b.StartsWith((byte[])[0x52, 0x49, 0x46, 0x46]) && b.Length >= 12 && b[8..12].SequenceEqual((byte[])[0x57, 0x45, 0x42, 0x50]))
            return EmbeddedFileKind.Webp;
        return null;
    }
}
