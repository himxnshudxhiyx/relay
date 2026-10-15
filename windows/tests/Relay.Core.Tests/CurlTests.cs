namespace Relay.Core.Tests;

// Ported from Tests/RelayTests/CurlTests.swift.
public class CurlTests
{
    static string? Header(ApiRequest request, string name) =>
        request.Headers.FirstOrDefault(h => h.Key.ToLowerInvariant() == name.ToLowerInvariant())?.Value;

    static readonly Dictionary<string, string> NoVariables = [];

    // MARK: - Parsing

    [Fact]
    public void RequestNameIsHostAndPath()
    {
        Assert.Equal("httpbin.org/get", CurlParser.RequestName("https://httpbin.org/get?hello=world#top"));
        Assert.Equal("localhost:3000/api/items", CurlParser.RequestName("http://localhost:3000/api/items/"));
        Assert.Equal("api.example.com", CurlParser.RequestName("https://api.example.com"));
        Assert.Equal("api.example.com/v1", CurlParser.RequestName("https://user:secret@api.example.com/v1"));
        Assert.Equal("{{baseUrl}}/posts/1", CurlParser.RequestName("{{baseUrl}}/posts/1"));
        Assert.Equal(80, CurlParser.RequestName("https://x.test/" + new string('a', 100)).Length);
    }

    [Fact]
    public void ChromeCopyAsCurl()
    {
        var command = """
        curl 'https://api.example.com/v1/users?page=2' \
          -H 'accept: application/json' \
          -H 'authorization: Bearer abc.def' \
          -H $'x-note: it\'s fine' \
          --data-raw '{"name":"Ada","tags":["x","y"],"meta":{}}' \
          --compressed
        """;
        var r = CurlParser.Parse(command);
        Assert.Equal("POST", r.Method);
        Assert.Equal("https://api.example.com/v1/users?page=2", r.Url);
        Assert.Equal("api.example.com/v1/users", r.Name);
        Assert.Equal(["page"], r.Params.Select(p => p.Key));
        Assert.Equal(["2"], r.Params.Select(p => p.Value));
        Assert.Equal("application/json", Header(r, "accept"));
        Assert.Equal("it's fine", Header(r, "x-note"));
        Assert.Null(Header(r, "authorization"));
        Assert.Equal(AuthType.Bearer, r.Auth.Type);
        Assert.Equal("abc.def", r.Auth.Token);
        Assert.Equal(BodyMode.Json, r.Body.Mode);
        Assert.Equal("""
        {
          "name": "Ada",
          "tags": [
            "x",
            "y"
          ],
          "meta": {}
        }
        """, r.Body.Raw);
    }

    [Fact]
    public void PostmanStyle()
    {
        var r = CurlParser.Parse("""
        curl --location 'https://x.io/login' --header 'Content-Type: application/json' --data '{"a":1}'
        """);
        Assert.Equal("POST", r.Method);
        Assert.Equal("https://x.io/login", r.Url);
        Assert.Empty(r.Headers); // implied Content-Type is dropped
        Assert.Equal(BodyMode.Json, r.Body.Mode);
        Assert.Equal(AuthType.Inherit, r.Auth.Type);
    }

    [Fact]
    public void UserFlagIsBasicAuth()
    {
        var r = CurlParser.Parse("curl -u admin:s3:cret https://x.io/me");
        Assert.Equal("GET", r.Method);
        Assert.Equal(AuthType.Basic, r.Auth.Type);
        Assert.Equal("admin", r.Auth.Username);
        Assert.Equal("s3:cret", r.Auth.Password);
    }

    [Fact]
    public void BasicAuthorizationHeaderIsDecoded()
    {
        var r = CurlParser.Parse("curl https://x.io -H 'Authorization: Basic YWxhZGRpbjpvcGVuc2VzYW1l'");
        Assert.Equal(AuthType.Basic, r.Auth.Type);
        Assert.Equal("aladdin", r.Auth.Username);
        Assert.Equal("opensesame", r.Auth.Password);
        Assert.Empty(r.Headers);
    }

    [Fact]
    public void FormUrlEncoded()
    {
        var r = CurlParser.Parse("curl -X POST https://x.io/f -d 'name=John+Doe&city=New%20York'");
        Assert.Equal("POST", r.Method);
        Assert.Equal(BodyMode.Form, r.Body.Mode);
        Assert.Equal(["name", "city"], r.Body.Form.Select(f => f.Key));
        Assert.Equal(["John Doe", "New York"], r.Body.Form.Select(f => f.Value));
    }

    [Fact]
    public void DataUrlEncodeKeepsLiteralValue()
    {
        var r = CurlParser.Parse("curl https://x.io -H 'Content-Type: application/x-www-form-urlencoded' --data-urlencode 'q=50% & more'");
        Assert.Equal(BodyMode.Form, r.Body.Mode);
        Assert.Equal("50% & more", r.Body.Form.FirstOrDefault()?.Value);
        Assert.Empty(r.Headers);
    }

    [Fact]
    public void MultipartWithFile()
    {
        var r = CurlParser.Parse("""curl -F 'file=@/tmp/a.png;type=image/png' -F 'title="Hi"' https://x.io/up""");
        Assert.Equal("POST", r.Method);
        Assert.Equal(BodyMode.Multipart, r.Body.Mode);
        Assert.Equal(2, r.Body.Form.Count);
        Assert.Equal("file", r.Body.Form[0].Key);
        Assert.Equal("/tmp/a.png", r.Body.Form[0].Value);
        Assert.True(r.Body.Form[0].IsFile);
        Assert.Equal("Hi", r.Body.Form[1].Value);
        Assert.False(r.Body.Form[1].IsFile);
    }

    [Fact]
    public void GetFlagMovesDataToQuery()
    {
        var r = CurlParser.Parse("curl -G https://x.io/search -d q=swift -d lang=en");
        Assert.Equal("GET", r.Method);
        Assert.Equal("https://x.io/search?q=swift&lang=en", r.Url);
        Assert.Equal(2, r.Params.Count);
        Assert.Equal(BodyMode.None, r.Body.Mode);
    }

    [Fact]
    public void CombinedAndInlineFlags()
    {
        var r = CurlParser.Parse("curl -sSLk -XPUT https://x.io/a --data-binary 'plain words' --max-time=5");
        Assert.Equal("PUT", r.Method);
        Assert.Equal("https://x.io/a", r.Url);
        Assert.Equal(BodyMode.Text, r.Body.Mode);
        Assert.Equal("plain words", r.Body.Raw);
    }

    [Fact]
    public void HeadFlag()
    {
        Assert.Equal("HEAD", CurlParser.Parse("curl -I https://x.io").Method);
        Assert.Equal("HEAD", CurlParser.Parse("curl -sI https://x.io").Method);
    }

    [Fact]
    public void JsonFlag()
    {
        var r = CurlParser.Parse("""curl --json '{"k":true}' https://x.io/j""");
        Assert.Equal("POST", r.Method);
        Assert.Equal(BodyMode.Json, r.Body.Mode);
        Assert.Null(Header(r, "content-type"));
        Assert.Equal("application/json", Header(r, "accept"));
    }

    [Fact]
    public void WindowsContinuationsPromptAndExe()
    {
        var r = CurlParser.Parse("$ curl.exe ^\n \"https://x.io/v\" ^\r\n -H \"A: b\"");
        Assert.Equal("https://x.io/v", r.Url);
        Assert.Equal("b", Header(r, "A"));
    }

    [Fact]
    public void Errors()
    {
        Assert.Throws<CurlParseException>(() => CurlParser.Parse("curl -H 'A: b'"));
        Assert.Throws<CurlParseException>(() => CurlParser.Parse("wget https://x.io"));
        Assert.Throws<CurlParseException>(() => CurlParser.Parse("curl 'https://x.io"));
        Assert.Throws<CurlParseException>(() => CurlParser.Parse("curl https://x.io -H"));
    }

    // MARK: - Generating

    [Fact]
    public void SimpleGet()
    {
        var request = new ApiRequest { Url = "https://x.io" };
        var prepared = RequestPreparer.Prepare(request, Auth.NoAuth(), NoVariables);
        Assert.Equal("curl \\\n  --url 'https://x.io'", CurlGenerator.Generate(prepared));
        Assert.Equal("curl --url 'https://x.io'", CurlGenerator.Generate(prepared, multiline: false));
    }

    [Fact]
    public void QuotesAreEscaped()
    {
        var request = new ApiRequest { Url = "https://x.io", Headers = [new KeyValue("X-Note", "it's")] };
        var output = CurlGenerator.Generate(RequestPreparer.Prepare(request, Auth.NoAuth(), NoVariables), multiline: false);
        Assert.Equal("""curl --url 'https://x.io' --header 'X-Note: it'\''s'""", output);
    }

    [Fact]
    public void BodiesAndBasicAuth()
    {
        var request = new ApiRequest { Method = "POST", Url = "https://x.io/up" };
        request.Body.Mode = BodyMode.Multipart;
        request.Body.Form = [new KeyValue("file", "/tmp/a b.png", isFile: true), new KeyValue("title", "Hi")];
        var auth = new Auth { Type = AuthType.Basic, Username = "u", Password = "p" };
        var output = CurlGenerator.Generate(RequestPreparer.Prepare(request, auth, NoVariables), multiline: false);
        Assert.Equal("curl --request POST --url 'https://x.io/up' --user 'u:p' --form 'file=@/tmp/a b.png' --form 'title=Hi'", output);

        request.Body.Mode = BodyMode.Form;
        var form = CurlGenerator.Generate(RequestPreparer.Prepare(request, Auth.NoAuth(), NoVariables), multiline: false);
        Assert.EndsWith("--data 'file=%2Ftmp%2Fa+b.png&title=Hi'", form);
    }

    [Fact]
    public void RoundTrip()
    {
        var request = new ApiRequest { Method = "PATCH", Url = "https://x.io/items/1?x=1", Headers = [new KeyValue("X-Trace", "it's 1")] };
        request.Body.Mode = BodyMode.Json;
        request.Body.Raw = "{\n  \"a\": 1\n}";
        var auth = new Auth { Type = AuthType.Bearer, Token = "tok" };

        foreach (var multiline in new[] { true, false })
        {
            var command = CurlGenerator.Generate(RequestPreparer.Prepare(request, auth, NoVariables), multiline);
            var parsed = CurlParser.Parse(command);
            Assert.Equal("PATCH", parsed.Method);
            Assert.Equal(request.Url, parsed.Url);
            Assert.Equal(["X-Trace"], parsed.Headers.Select(h => h.Key));
            Assert.Equal(["it's 1"], parsed.Headers.Select(h => h.Value));
            Assert.Equal(AuthType.Bearer, parsed.Auth.Type);
            Assert.Equal("tok", parsed.Auth.Token);
            Assert.Equal(BodyMode.Json, parsed.Body.Mode);
            Assert.Equal(request.Body.Raw, parsed.Body.Raw);
        }

        var formRequest = new ApiRequest { Method = "POST", Url = "https://x.io/f" };
        formRequest.Body.Mode = BodyMode.Form;
        formRequest.Body.Form = [new KeyValue("q", "a&b=c d"), new KeyValue("n", "1")];
        var formParsed = CurlParser.Parse(CurlGenerator.Generate(RequestPreparer.Prepare(formRequest, Auth.NoAuth(), NoVariables)));
        Assert.Equal(BodyMode.Form, formParsed.Body.Mode);
        Assert.Equal(["a&b=c d", "1"], formParsed.Body.Form.Select(f => f.Value));
    }
}
