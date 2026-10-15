using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Relay.Core.Tests;

public class FormatTests
{
    [Fact]
    public void CurlWithResponseText()
    {
        var result = new HttpResult
        {
            Status = 201, Headers = [("Content-Type", "application/json")], Body = Encoding.UTF8.GetBytes("""{"id":1}"""),
            Duration = 0.1234, Ttfb = null, Redirects = 0, FinalUrl = "https://x.test",
            SentMethod = "POST", SentUrl = "https://x.test", SentHeaders = [], SentBody = "",
            Kind = BodyKind.Json, Text = """{"id":1}""", Pretty = "{\n  \"id\": 1\n}",
        };
        var text = Format.CurlWithResponse("curl https://x.test", result, includeHeaders: true);
        Assert.Equal("curl https://x.test\n\nHTTP 201 Created  (123 ms, 8 B)\nContent-Type: application/json\n\n{\n  \"id\": 1\n}", text);
    }

    [Fact]
    public void ResponseFileNameFollowsTheRequest()
    {
        var url = "https://devgoapi.livekeeping.com/v1/gst/gstr1/getSection?page=1";
        // CurlParser.RequestName(url) is the URL without scheme and query.
        Assert.Equal("gst/gstr1/getSection", Format.ResponseFileName("devgoapi.livekeeping.com/v1/gst/gstr1/getSection", url));
        Assert.Equal("users", Format.ResponseFileName("Untitled Request", "localhost:8000/users"));
        Assert.Equal("GST Summary", Format.ResponseFileName("GST Summary", url));  // a chosen name wins
        Assert.Equal("response", Format.ResponseFileName("Untitled Request", "https://x.test/"));
    }

    [Fact]
    public void FindsBase64FilesInJson()
    {
        var pdf = Convert.ToBase64String([.. "%PDF-1.4\n"u8, .. Enumerable.Repeat((byte)0x20, 80)]);
        var png = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[80]]);
        var notAFile = string.Concat(Enumerable.Repeat("QUJD", 40));  // "ABC…": valid base64, no known header
        var json = $$"""{"data":{"fileName":"GSTR1-B2B","pdf":"{{pdf}}","note":"{{notAFile}}"},"images":["data:image/png;base64,{{png}}"]}""";
        var files = EmbeddedFile.Find(Encoding.UTF8.GetBytes(json));
        Assert.Equal(["data.pdf", "images[0]"], files.Select(f => f.Path));
        Assert.Equal([EmbeddedFileKind.Pdf, EmbeddedFileKind.Png], files.Select(f => f.Kind));
        Assert.Equal("GSTR1-B2B", files[0].SuggestedName);  // a sibling fileName names it
        Assert.True(files[0].Data.AsSpan().StartsWith("%PDF"u8));
        Assert.Empty(EmbeddedFile.Find("""{"pdf":"short"}"""u8.ToArray()));
    }

    [Fact]
    public async Task SendsPostAndFollowsARedirect()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        string? receivedBody = null, receivedType = null;
        var server = Task.Run(async () =>
        {
            for (var hop = 0; hop < 2; hop++)
            {
                var context = await listener.GetContextAsync();
                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                var response = context.Response;
                if (context.Request.Url!.AbsolutePath == "/start")
                {
                    // 307 keeps the method and body.
                    response.StatusCode = 307;
                    response.RedirectLocation = "/final";
                }
                else
                {
                    receivedBody = body;
                    receivedType = context.Request.ContentType;
                    response.ContentType = "application/json";
                    await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes($$"""{"got":{{body}},"n":1.0}"""));
                }
                response.Close();
            }
        }, TestContext.Current.CancellationToken);

        var prepared = new PreparedRequest
        {
            Method = "POST",
            Url = $"http://127.0.0.1:{port}/start",
            Headers = [("Content-Type", "application/json"), ("X-Test", "yes")],
            Body = new PreparedBody.Raw("""{"a":1}"""),
        };
        var result = await RelayHttp.SendAsync(prepared, new HttpSettings(), TestContext.Current.CancellationToken);
        await server;

        Assert.Equal(200, result.Status);
        Assert.Equal("OK", result.StatusText);
        Assert.Equal(1, result.Redirects);
        Assert.Equal($"http://127.0.0.1:{port}/final", result.FinalUrl);
        Assert.Equal("""{"a":1}""", receivedBody);
        Assert.Equal("application/json", receivedType);

        Assert.Equal(BodyKind.Json, result.Kind);
        Assert.Equal("""{"got":{"a":1},"n":1.0}""", result.Text);
        Assert.Equal("{\n  \"got\": {\n    \"a\": 1\n  },\n  \"n\": 1.0\n}", result.Pretty);
        Assert.Empty(result.Files);
        Assert.StartsWith("application/json", result.ContentType);
        Assert.True(result.Ttfb > 0 && result.Ttfb <= result.Duration);

        Assert.Equal("POST", result.SentMethod);
        Assert.Equal($"http://127.0.0.1:{port}/start", result.SentUrl);
        Assert.Equal([("Content-Type", "application/json"), ("X-Test", "yes")], result.SentHeaders);
        Assert.Equal("""{"a":1}""", result.SentBody);
    }
}
