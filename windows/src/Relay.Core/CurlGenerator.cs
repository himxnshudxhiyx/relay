namespace Relay.Core;

// Ported from CurlGenerator.swift.

/// <summary>
/// Prints a prepared request as a bash cURL command.
///
/// Long-form flags throughout (<c>--header</c>, not <c>-H</c>) so the command reads
/// clearly when pasted into a ticket, and single quotes so nothing in a value
/// is expanded by the shell.
/// </summary>
public static class CurlGenerator
{
    public static string Generate(PreparedRequest prepared, bool multiline = true)
    {
        var parts = new List<string> { "curl" };

        var hasBody = prepared.Body switch
        {
            PreparedBody.Raw raw => raw.Text.Length > 0,
            PreparedBody.Form form => form.Fields.Count > 0,
            PreparedBody.Multipart multipart => multipart.Rows.Count > 0,
            PreparedBody.File file => file.Path.Length > 0,
            _ => false,
        };
        var isMultipart = prepared.Body is PreparedBody.Multipart && hasBody;

        if (prepared.Method != "GET" || hasBody) parts.Add($"--request {prepared.Method}");
        parts.Add($"--url {Quote(prepared.Url)}");

        foreach (var (name, value) in prepared.Headers)
        {
            // curl writes its own multipart Content-Type, boundary included.
            if (isMultipart && string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            parts.Add($"--header {Quote($"{name}: {value}")}");
        }

        if (prepared.BasicAuth is { } basic) parts.Add($"--user {Quote($"{basic.Username}:{basic.Password}")}");

        if (hasBody)
        {
            switch (prepared.Body)
            {
                case PreparedBody.Raw raw:
                    parts.Add($"--data {Quote(raw.Text)}");
                    break;
                case PreparedBody.Form form:
                    var encoded = form.Fields.Select(p => $"{UrlQuery.FormEncode(p.Key)}={UrlQuery.FormEncode(p.Value)}");
                    parts.Add($"--data {Quote(string.Join("&", encoded))}");
                    break;
                case PreparedBody.Multipart multipart:
                    foreach (var row in multipart.Rows)
                        parts.Add($"--form {Quote($"{row.Key}={(row.IsFile ? "@" : "")}{row.Value}")}");
                    break;
                case PreparedBody.File file:
                    parts.Add($"--data-binary {Quote($"@{file.Path}")}");
                    break;
            }
        }

        return string.Join(multiline ? " \\\n  " : " ", parts);
    }

    static string Quote(string s) => "'" + s.Replace("'", @"'\''") + "'";
}
