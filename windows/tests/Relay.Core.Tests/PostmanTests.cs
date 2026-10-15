using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relay.Core.Tests;

// Ported from Tests/RelayTests/PostmanTests.swift.
public class PostmanTests
{
    // MARK: - Fixtures

    const string Shop = """
    {
      "info": {
        "_postman_id": "abc-123",
        "name": "Shop API",
        "description": { "content": "Shop docs", "type": "text/markdown" },
        "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
      },
      "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "{{token}}", "type": "string" } ] },
      "variable": [
        { "key": "baseUrl", "value": "https://api.shop.test" },
        { "key": "port", "value": 8080 },
        { "key": "old", "value": "x", "disabled": true }
      ],
      "event": [ { "listen": "prerequest", "script": { "type": "text/javascript", "exec": ["console.log('hi')"] } } ],
      "item": [
        {
          "name": "Users",
          "item": [
            {
              "name": "Admin",
              "auth": { "type": "apikey", "apikey": [
                { "key": "key", "value": "X-Admin" }, { "key": "value", "value": "secret" }, { "key": "in", "value": "header" }
              ] },
              "item": [
                {
                  "name": "List admins",
                  "request": {
                    "method": "GET",
                    "url": {
                      "raw": "{{baseUrl}}/admins?page=1",
                      "host": ["{{baseUrl}}"],
                      "path": ["admins"],
                      "query": [ { "key": "page", "value": "1" }, { "key": "debug", "value": "true", "disabled": true } ]
                    }
                  },
                  "response": []
                }
              ]
            },
            {
              "name": "Get user",
              "event": [ { "listen": "test", "script": { "exec": ["pm.test('ok', () => {})"], "type": "text/javascript" } } ],
              "request": {
                "method": "GET",
                "header": [
                  { "key": "Accept", "value": "application/json" },
                  { "key": "X-Trace", "value": "1", "disabled": true }
                ],
                "url": {
                  "raw": "{{baseUrl}}/users/:id",
                  "host": ["{{baseUrl}}"],
                  "path": ["users", ":id"],
                  "variable": [ { "key": "id", "value": "42" } ]
                },
                "description": "Fetch one"
              },
              "response": [ { "name": "200", "status": "OK", "code": 200, "body": "{}" } ]
            },
            {
              "name": "Login",
              "request": {
                "method": "POST",
                "auth": { "type": "noauth" },
                "url": "{{baseUrl}}/login",
                "body": { "mode": "raw", "raw": "{\"user\":\"a\"}", "options": { "raw": { "language": "json" } } }
              }
            }
          ]
        },
        {
          "name": "Upload avatar",
          "request": {
            "method": "POST",
            "url": "https://api.shop.test/avatar",
            "body": { "mode": "formdata", "formdata": [
              { "key": "file", "type": "file", "src": "/tmp/a.png" },
              { "key": "note", "value": "hi", "type": "text" },
              { "key": "multi", "type": "file", "src": ["/tmp/b.png", "/tmp/c.png"], "disabled": true }
            ] }
          }
        },
        {
          "name": "Token",
          "request": {
            "method": "POST",
            "url": "https://auth.test/token",
            "body": { "mode": "urlencoded", "urlencoded": [
              { "key": "grant_type", "value": "client_credentials" },
              { "key": "scope", "value": "read", "disabled": true }
            ] },
            "auth": { "type": "oauth2", "oauth2": [ { "key": "accessTokenUrl", "value": "https://auth.test", "type": "string" } ] }
          }
        },
        {
          "name": "Graph",
          "request": {
            "method": "POST",
            "url": "https://gql.test/graphql",
            "body": { "mode": "graphql", "graphql": { "query": "query { me { id } }", "variables": "{\"a\": 1}" } }
          }
        },
        { "name": "Plain", "request": "https://example.com/ping?x=1" }
      ]
    }
    """;

    const string Legacy = """
    {
      "info": { "name": "Old", "schema": "https://schema.getpostman.com/json/collection/v2.0.0/collection.json" },
      "item": [
        {
          "name": "Basic",
          "request": {
            "url": "http://localhost:3000/me",
            "method": "get",
            "header": "Accept: application/json\n// X-Off: 1\nX-Id: 7",
            "auth": { "type": "basic", "basic": { "username": "ann", "password": "pw" } }
          }
        }
      ]
    }
    """;

    const string Staging = """
    {
      "id": "e1",
      "name": "Staging",
      "values": [
        { "key": "baseUrl", "value": "https://staging.test", "enabled": true },
        { "key": "token", "value": "s3cr3t", "type": "secret", "enabled": true },
        { "key": "off", "value": "1", "enabled": false }
      ],
      "_postman_variable_scope": "environment"
    }
    """;

    static ImportResult Load(string json) => Postman.ImportData(Encoding.UTF8.GetBytes(json));

    static ApiCollection LoadShop() => Assert.Single(Load(Shop).Collections);

    static ApiRequest Find(ApiCollection c, string name) =>
        c.Items.AllRequests().FirstOrDefault(r => r.Name == name) ?? throw new Xunit.Sdk.XunitException($"no request named {name}");

    static JsonNode Parse(byte[] data) => JsonNode.Parse(data)!;

    // MARK: - Import

    [Fact]
    public void CollectionMetadata()
    {
        var c = LoadShop();
        Assert.Equal("Shop API", c.Name);
        Assert.Equal("Shop docs", c.Docs);
        Assert.Equal(["baseUrl", "port", "old"], c.Variables.Select(v => v.Key));
        Assert.Equal("8080", c.Variables[1].Value);
        Assert.False(c.Variables[2].Enabled);
        Assert.Equal(AuthType.Bearer, c.Auth.Type);
        Assert.Equal("{{token}}", c.Auth.Token);
        Assert.Equal("abc-123", c.Extras["_postman_id"].GetString());
        Assert.True(c.Extras.ContainsKey("event"));
        Assert.Equal(5, c.Items.Count);
        Assert.Equal(7, c.Items.RequestCount());
    }

    [Fact]
    public void NestedFoldersAndInheritedAuth()
    {
        var c = LoadShop();
        var users = Assert.IsType<Folder>(c.Items[0]);
        var admin = Assert.IsType<Folder>(users.Items[0]);
        Assert.Equal("Users", users.Name);
        Assert.Equal(AuthType.Inherit, users.Auth.Type);
        Assert.Equal(AuthType.ApiKey, admin.Auth.Type);

        var list = Find(c, "List admins");
        Assert.Equal(AuthType.Inherit, list.Auth.Type);
        var listAuth = c.EffectiveAuth(list);
        Assert.Equal(AuthType.ApiKey, listAuth.Type);
        Assert.Equal("X-Admin", listAuth.Key);
        Assert.Equal("secret", listAuth.Value);
        Assert.Equal(ApiKeyLocation.Header, listAuth.Location);

        Assert.Equal(AuthType.Bearer, c.EffectiveAuth(Find(c, "Get user")).Type);
        Assert.Equal(AuthType.None, c.EffectiveAuth(Find(c, "Login")).Type);
    }

    [Fact]
    public void UrlObjectKeepsDisabledQuery()
    {
        var list = Find(LoadShop(), "List admins");
        Assert.Equal("{{baseUrl}}/admins?page=1", list.Url);
        Assert.Equal(["page", "debug"], list.Params.Select(p => p.Key));
        Assert.Equal([true, false], list.Params.Select(p => p.Enabled));
        Assert.False(list.Extras.ContainsKey("response"), "an empty response list isn't worth keeping");
    }

    [Fact]
    public void HeadersDocsAndItemExtras()
    {
        var get = Find(LoadShop(), "Get user");
        Assert.Equal(["Accept", "X-Trace"], get.Headers.Select(h => h.Key));
        Assert.Equal([true, false], get.Headers.Select(h => h.Enabled));
        Assert.Equal("Fetch one", get.Docs);
        Assert.True(get.Extras.ContainsKey("event"));
        Assert.True(get.Extras.ContainsKey("response"));
        Assert.True(get.Extras.ContainsKey("urlVariable"));
    }

    [Fact]
    public void RawJsonBody()
    {
        var login = Find(LoadShop(), "Login");
        Assert.Equal("POST", login.Method);
        Assert.Equal("{{baseUrl}}/login", login.Url);
        Assert.Equal(BodyMode.Json, login.Body.Mode);
        Assert.Equal("""{"user":"a"}""", login.Body.Raw);
    }

    [Fact]
    public void FormDataWithFiles()
    {
        var upload = Find(LoadShop(), "Upload avatar");
        Assert.Equal(BodyMode.Multipart, upload.Body.Mode);
        Assert.Equal(["file", "note", "multi"], upload.Body.Form.Select(f => f.Key));
        Assert.Equal([true, false, true], upload.Body.Form.Select(f => f.IsFile));
        Assert.Equal("/tmp/a.png", upload.Body.Form[0].Value);
        Assert.Equal("hi", upload.Body.Form[1].Value);
        Assert.Equal("/tmp/b.png", upload.Body.Form[2].Value);
        Assert.False(upload.Body.Form[2].Enabled);
    }

    [Fact]
    public void UrlEncodedAndUnsupportedAuth()
    {
        var token = Find(LoadShop(), "Token");
        Assert.Equal(BodyMode.Form, token.Body.Mode);
        Assert.Equal(["grant_type", "scope"], token.Body.Form.Select(f => f.Key));
        Assert.Equal([true, false], token.Body.Form.Select(f => f.Enabled));
        Assert.Equal(AuthType.None, token.Auth.Type);
        Assert.True(token.Extras.ContainsKey("auth"), "OAuth 2 settings are stashed for export");
    }

    [Fact]
    public void GraphQLBecomesJson()
    {
        var graph = Find(LoadShop(), "Graph");
        Assert.Equal(BodyMode.Json, graph.Body.Mode);
        var payload = Assert.IsType<JsonObject>(JsonNode.Parse(graph.Body.Raw));
        Assert.Equal("query { me { id } }", (string?)payload["query"]);
        Assert.Equal(1, (int?)payload["variables"]?["a"]);
    }

    [Fact]
    public void RequestAsPlainString()
    {
        var plain = Find(LoadShop(), "Plain");
        Assert.Equal("GET", plain.Method);
        Assert.Equal("https://example.com/ping?x=1", plain.Url);
        Assert.Equal(["x"], plain.Params.Select(p => p.Key));
        Assert.Equal(["1"], plain.Params.Select(p => p.Value));
    }

    [Fact]
    public void LegacyDictAuthAndHeaderString()
    {
        var c = Assert.Single(Load(Legacy).Collections);
        Assert.Equal(AuthType.None, c.Auth.Type);
        var basic = Find(c, "Basic");
        Assert.Equal("GET", basic.Method);
        Assert.Equal(AuthType.Basic, basic.Auth.Type);
        Assert.Equal("ann", basic.Auth.Username);
        Assert.Equal("pw", basic.Auth.Password);
        Assert.Equal(["Accept", "X-Off", "X-Id"], basic.Headers.Select(h => h.Key));
        Assert.Equal([true, false, true], basic.Headers.Select(h => h.Enabled));
        Assert.Equal("7", basic.Headers[2].Value);
    }

    [Fact]
    public void EnvironmentWithSecret()
    {
        var result = Load(Staging);
        Assert.Empty(result.Collections);
        var env = Assert.Single(result.Environments);
        Assert.Equal("Staging", env.Name);
        Assert.Equal(["baseUrl", "token", "off"], env.Variables.Select(v => v.Key));
        Assert.Equal([false, true, false], env.Variables.Select(v => v.IsSecret));
        Assert.Equal([true, true, false], env.Variables.Select(v => v.Enabled));
    }

    [Fact]
    public void DumpAndArray()
    {
        var dump = Load($$"""{"version": 1, "collections": [{{Shop}}], "environments": [{{Staging}}]}""");
        Assert.Single(dump.Collections);
        Assert.Single(dump.Environments);

        var array = Load($"[{Legacy}, {Staging}]");
        Assert.Equal(["Old"], array.Collections.Select(c => c.Name));
        Assert.Equal(["Staging"], array.Environments.Select(e => e.Name));

        var wrapped = Load($$"""{"collection": {{Legacy}}}""");
        Assert.Single(wrapped.Collections);
    }

    [Fact]
    public void GarbageThrows()
    {
        foreach (var input in new[] { "nope", """{"foo": 1}""", "[]", "42", """{"requests": [], "name": "v1"}""" })
            Assert.Throws<PostmanException>(() => Load(input));
    }

    [Fact]
    public void SkipsByteOrderMark()
    {
        // Not in the Swift tests: Windows editors add a BOM, which JSONSerialization skips on the Mac.
        var data = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Staging)).ToArray();
        Assert.Single(Postman.ImportData(data).Environments);
    }

    // MARK: - Export

    [Fact]
    public void ExportIsV21Shape()
    {
        var root = Parse(Postman.ExportCollection(LoadShop()));
        var info = root["info"]!;
        Assert.Equal(Postman.Schema, (string?)info["schema"]);
        Assert.Equal("abc-123", (string?)info["_postman_id"]);
        Assert.Equal("bearer", (string?)root["auth"]?["type"]);
        Assert.NotNull(root["event"]);

        var users = root["item"]![0]!.AsObject();
        Assert.False(users.ContainsKey("auth"), "inherit is written as no auth key");
        var admin = users["item"]![0]!;
        var list = admin["item"]![0]!.AsObject();
        var url = list["request"]!["url"]!;
        Assert.Equal("{{baseUrl}}/admins?page=1", (string?)url["raw"]);
        Assert.Equal(["{{baseUrl}}"], url["host"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(["admins"], url["path"]!.AsArray().Select(n => (string?)n));
        var query = url["query"]!.AsArray();
        Assert.Equal(2, query.Count);
        Assert.True((bool?)query[1]!["disabled"]);
        Assert.True(list.ContainsKey("response"));
    }

    [Fact]
    public void UrlParts()
    {
        var full = Postman.UrlObject("https://api.test:8443/v1/users?id=2", []);
        Assert.Equal("https", (string?)full["protocol"]);
        Assert.Equal(["api", "test"], full["host"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("8443", (string?)full["port"]);
        Assert.Equal(["v1", "users"], full["path"]!.AsArray().Select(n => (string?)n));

        var templated = Postman.UrlObject("{{base.url}}/a/b", []);
        Assert.False(templated.ContainsKey("protocol"));
        Assert.Equal(["{{base.url}}"], templated["host"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(["a", "b"], templated["path"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public void CollectionRoundTrip()
    {
        var original = LoadShop();
        var again = Assert.Single(Postman.ImportData(Postman.ExportCollection(original)).Collections);
        AssertSame(StripIds(original), StripIds(again));

        AssertSame(Find(original, "Token").Extras["auth"], Find(again, "Token").Extras["auth"]);
        AssertSame(Find(original, "Get user").Extras["event"], Find(again, "Get user").Extras["event"]);
    }

    [Fact]
    public void EnvironmentAndBackupRoundTrip()
    {
        var env = Assert.Single(Load(Staging).Environments);
        var envAgain = Assert.Single(Postman.ImportData(Postman.ExportEnvironment(env)).Environments);
        AssertSame(StripIds(env.Variables), StripIds(envAgain.Variables));
        Assert.Equal(env.Name, envAgain.Name);

        var backup = Postman.ExportBackup([LoadShop(), Assert.Single(Load(Legacy).Collections)], [env]);
        var restored = Postman.ImportData(backup);
        Assert.Equal(["Shop API", "Old"], restored.Collections.Select(c => c.Name));
        Assert.Equal(["Staging"], restored.Environments.Select(e => e.Name));
    }

    // MARK: - Helpers

    /// <summary>Swift's == on the value types: same content, key order aside.</summary>
    static void AssertSame<T>(T expected, T actual)
    {
        var a = JsonNode.Parse(Json.Serialize(expected));
        var b = JsonNode.Parse(Json.Serialize(actual));
        Assert.True(JsonNode.DeepEquals(a, b), $"expected\n{a}\nactual\n{b}");
    }

    static List<KeyValue> StripIds(IEnumerable<KeyValue> rows) =>
        rows.Select(row => { var copy = Json.Clone(row); copy.Id = Guid.Empty; return copy; }).ToList();

    static void StripIds(IEnumerable<CollectionItem> items)
    {
        foreach (var item in items)
        {
            item.Id = Guid.Empty;
            if (item is Folder f)
            {
                StripIds(f.Items);
            }
            else if (item is ApiRequest r)
            {
                foreach (var kv in r.Params.Concat(r.Headers).Concat(r.Body.Form)) kv.Id = Guid.Empty;
            }
        }
    }

    static ApiCollection StripIds(ApiCollection c)
    {
        var copy = Json.Clone(c);
        copy.Id = Guid.Empty;
        foreach (var kv in copy.Variables) kv.Id = Guid.Empty;
        StripIds(copy.Items);
        return copy;
    }
}
