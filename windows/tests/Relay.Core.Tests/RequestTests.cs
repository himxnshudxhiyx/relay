namespace Relay.Core.Tests;

// Ported from the URL, variables, preparing, completion and pasted-cURL cases in Tests/RelayTests/CoreTests.swift.
public class RequestTests
{
    // MARK: URL ⇄ params

    [Fact]
    public void ParamsFollowUrlAndKeepDisabledRows()
    {
        var disabled = new KeyValue("debug", "1", enabled: false);
        var first = new KeyValue("page", "1");
        var rows = UrlQuery.ParamsFromUrl("https://api.test/users?page=2&limit={{limit}}", [first, disabled]);

        Assert.Equal(["page", "debug", "limit"], rows.Select(r => r.Key));
        Assert.Equal(first.Id, rows[0].Id); // the edited row keeps its identity
        Assert.Equal("2", rows[0].Value);
        Assert.False(rows[1].Enabled);
        Assert.Equal("{{limit}}", rows[2].Value);
    }

    [Fact]
    public void UrlFollowsParamsAndKeepsFragment()
    {
        KeyValue[] parameters = [new("q", "a b"), new("off", "x", enabled: false), new("flag", "")];
        Assert.Equal("https://x.test/search?q=a b&flag#top", UrlQuery.UrlApplying("https://x.test/search?old=1#top", parameters));
        Assert.Equal("https://x.test/search", UrlQuery.UrlApplying("https://x.test/search?old=1", []));
    }

    [Fact]
    public void EncodedUrlKeepsExistingEscapes()
    {
        var url = UrlQuery.EncodedUrl("https://x.test/a b?q=caf%C3%A9&name=José");
        Assert.Equal("https://x.test/a%20b?q=caf%C3%A9&name=Jos%C3%A9", url?.AbsoluteUri);
        Assert.Null(UrlQuery.EncodedUrl("not a url"));
    }

    // MARK: Variables

    [Fact]
    public void VariablesResolveNestedAndLeaveUnknown()
    {
        var values = new Dictionary<string, string> { ["host"] = "api.test", ["base"] = "https://{{host}}/v1" };
        Assert.Equal("https://api.test/v1/users/{{ id }}", Variables.Resolve("{{base}}/users/{{ id }}", values));
        Assert.Equal(["a", "b"], Variables.Names("{{a}} {{b}} {{a}}"));
    }

    [Fact]
    public void VariablesDoNotLoopOnSelfReference()
    {
        Assert.Equal("{{loop}}", Variables.Resolve("{{loop}}", new Dictionary<string, string> { ["loop"] = "{{loop}}" }));
    }

    [Fact]
    public void EnvironmentOverridesCollection()
    {
        var scope = new VariableScope
        {
            Collection = [new KeyValue("base", "collection"), new KeyValue("only", "c")],
            Environment = [new KeyValue("base", "env"), new KeyValue("off", "x", enabled: false)],
        };
        Assert.Equal("env", scope.Values["base"]);
        Assert.Equal("c", scope.Values["only"]);
        Assert.False(scope.Values.ContainsKey("off"));
        Assert.Equal(VariableScope.Source.Environment, scope.SourceOf("base"));
        Assert.Equal(VariableScope.Source.Collection, scope.SourceOf("only"));
        Assert.Equal(VariableScope.Source.Dynamic, scope.SourceOf("$guid"));
        Assert.Equal(VariableScope.Source.Missing, scope.SourceOf("nope"));
    }

    // MARK: Preparing requests

    [Fact]
    public void PrepareAppliesAuthAndContentType()
    {
        var request = new ApiRequest
        {
            Method = "post",
            Url = "{{base}}/items",
            Headers = [new KeyValue("X-Trace", "{{trace}}"), new KeyValue("X-Off", "1", enabled: false)],
            Body = new RequestBody { Mode = BodyMode.Json, Raw = """{"a":1}""" },
        };

        var prepared = RequestPreparer.Prepare(request, new Auth { Type = AuthType.Bearer, Token = "{{token}}" },
            new Dictionary<string, string> { ["base"] = "api.test", ["trace"] = "t1", ["token"] = "secret" });
        Assert.Equal("POST", prepared.Method);
        Assert.Equal("http://api.test/items", prepared.Url); // a URL with no scheme gets http://
        Assert.Equal("t1", prepared.Header("X-Trace"));
        Assert.Null(prepared.Header("X-Off"));
        Assert.Equal("Bearer secret", prepared.Header("Authorization"));
        Assert.Equal("application/json", prepared.Header("Content-Type"));
    }

    [Fact]
    public void PrepareWithoutVariablesKeepsPlaceholders()
    {
        var request = new ApiRequest { Url = "{{base}}/items" };
        var prepared = RequestPreparer.Prepare(request, new Auth { Type = AuthType.Basic, Username = "{{user}}", Password = "p" }, null);
        Assert.Equal("{{base}}/items", prepared.Url);
        Assert.Equal("{{user}}", prepared.BasicAuth?.Username);
        Assert.Contains("--user '{{user}}:p'", CurlGenerator.Generate(prepared));
    }

    [Fact]
    public void ApiKeyInQuery()
    {
        var request = new ApiRequest { Url = "https://x.test/a?b=1#frag" };
        var auth = new Auth { Type = AuthType.ApiKey, Key = "api key", Value = "v&1", Location = ApiKeyLocation.Query };
        var prepared = RequestPreparer.Prepare(request, auth, new Dictionary<string, string>());
        Assert.Equal("https://x.test/a?b=1&api+key=v%261#frag", prepared.Url);
    }

    // MARK: Variable suggestions

    [Fact]
    public void CompletionContextFindsOpenBraces()
    {
        Assert.Equal("", CompletionContext.Find("{{", 2)?.Prefix);
        Assert.Equal(new CompletionContext(10, 3, "bas"), CompletionContext.Find("https://{{bas", 13));
        Assert.Equal("tok", CompletionContext.Find("""{"a": "{{tok"}""", 12)?.Prefix);
        // Editing the name inside a closed variable still completes.
        Assert.Equal("ba", CompletionContext.Find("{{bas}}/x", 4)?.Prefix);
    }

    [Fact]
    public void CompletionReplacesWholeName()
    {
        var inside = CompletionContext.Find("{{bas}}/x", 4)!.Value;
        Assert.Equal((2, 3, true), inside.Replacement("{{bas}}/x"));

        var open = CompletionContext.Find("{{ba", 4)!.Value;
        Assert.Equal((2, 2, false), open.Replacement("{{ba"));
    }

    [Fact]
    public void CompletionContextIgnoresEverythingElse()
    {
        Assert.Null(CompletionContext.Find("{", 1));
        Assert.Null(CompletionContext.Find("{{base}}", 8)); // after the closing braces
        Assert.Null(CompletionContext.Find("{{base url", 10)); // a space ends the name
        Assert.Null(CompletionContext.Find("""{"a": 1}""", 5));
        Assert.Null(CompletionContext.Find("abc", 99));
    }

    [Fact]
    public void SuggestionsOrderAndFiltering()
    {
        var scope = new VariableScope
        {
            Collection = [new KeyValue("baseUrl", "collection"), new KeyValue("apiVersion", "v2")],
            Environment = [new KeyValue("baseUrl", "https://env"), new KeyValue("token", "t", isSecret: true),
                           new KeyValue("off", "x", enabled: false)],
        };

        var all = scope.Suggestions("");
        Assert.Equal(["baseUrl", "token", "apiVersion"], all.Select(s => s.Name));
        Assert.Equal(VariableScope.Source.Environment, all[0].Source); // the environment wins a name clash
        Assert.Equal("https://env", all[0].Value);
        Assert.True(all[1].IsSecret);

        Assert.Equal(["apiVersion"], scope.Suggestions("ver").Select(s => s.Name));
        Assert.Equal(["baseUrl"], scope.Suggestions("B").Select(s => s.Name)); // prefix match, case-insensitive
        Assert.Equal(["apiVersion", "baseUrl"], scope.Suggestions("a").Select(s => s.Name)); // prefix matches first
        Assert.DoesNotContain(all, s => s.Source == VariableScope.Source.Dynamic); // built-ins wait for a $
        Assert.Equal(["$timestamp"], scope.Suggestions("$ti").Select(s => s.Name));
    }

    // MARK: Pasting cURL into the URL box

    [Fact]
    public void PastedMultilineCurlParses()
    {
        // Exactly what Relay's own "Copy as cURL" produces.
        var pasted = """
        curl \
          --url 'https://httpbin.org/get?hello=world&from=relay' \
          --header 'Authorization: Bearer relay-demo-token'
        """;
        var request = CurlParser.ParsePasted(pasted);
        Assert.NotNull(request);
        Assert.Equal("https://httpbin.org/get?hello=world&from=relay", request.Url);
        Assert.Equal(["hello", "from"], request.Params.Select(p => p.Key));
        Assert.Equal(AuthType.Bearer, request.Auth.Type);
        Assert.Equal("relay-demo-token", request.Auth.Token);
        Assert.Empty(request.Headers);
        Assert.Null(CurlParser.ParsePasted("https://example.com/curl something"));
    }

    [Fact]
    public void PastedCurlFlattenedToOneLineParses()
    {
        // A single-line field turns each "\⏎" into "\ ".
        var request = CurlParser.ParsePasted("curl \\ --url 'https://x.io/a' \\   --header 'A: b'");
        Assert.NotNull(request);
        Assert.Equal("https://x.io/a", request.Url);
        Assert.Equal("b", request.Headers.Single().Value);
    }

    // MARK: Bulk edit

    [Fact]
    public void BulkEditRoundTripsAndKeepsRowIdentity()
    {
        var first = new KeyValue("Accept", "application/json");
        KeyValue[] rows = [first, new("X-Off", "1", enabled: false), new("", "")];
        Assert.Equal("Accept: application/json\n// X-Off: 1", BulkEdit.Text(rows));

        var parsed = BulkEdit.Parse("Accept: text/html\r\n\n  //  X-Off : a:b  \nflag", rows);
        Assert.Equal(["Accept", "X-Off", "flag"], parsed.Select(r => r.Key));
        Assert.Equal(["text/html", "a:b", ""], parsed.Select(r => r.Value));
        Assert.Equal([true, false, true], parsed.Select(r => r.Enabled));
        Assert.Equal(first.Id, parsed[0].Id); // the nth line reuses the nth row
    }
}
