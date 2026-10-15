using System.Text.Json.Nodes;

namespace Relay.Core.Tests;

public class ModelTests
{
    // Shaped exactly like the Mac app's workspace.json.
    const string MacWorkspace = """
    {
      "activeEnvironmentID" : "6F9619FF-8B86-D011-B42D-00C04FC964FF",
      "collections" : [
        {
          "auth" : { "key" : "", "location" : "header", "password" : "", "token" : "{{token}}", "type" : "bearer", "username" : "", "value" : "" },
          "docs" : "",
          "extras" : { "event" : [ { "listen" : "test", "script" : { "exec" : [ "pm.test()" ] } } ] },
          "id" : "11111111-2222-3333-4444-555555555555",
          "items" : [
            { "folder" : { "auth" : { "type" : "inherit" }, "docs" : "", "extras" : {}, "id" : "AAAAAAAA-0000-0000-0000-000000000001", "name" : "auth",
              "items" : [ { "request" : { "body" : { "filePath" : "", "form" : [], "mode" : "json", "raw" : "{\"a\": 1.50}" },
                "docs" : "", "extras" : {}, "headers" : [ { "description" : "", "enabled" : true, "id" : "BBBBBBBB-0000-0000-0000-000000000001", "isFile" : false, "isSecret" : false, "key" : "token", "value" : "x" } ],
                "id" : "CCCCCCCC-0000-0000-0000-000000000001", "method" : "POST", "name" : "auth/login", "params" : [],
                "url" : "https://api.test/v1/auth/login", "auth" : { "type" : "inherit" } } } ] } }
          ],
          "name" : "Livekeeping",
          "variables" : []
        }
      ],
      "environments" : [ { "id" : "6F9619FF-8B86-D011-B42D-00C04FC964FF", "name" : "Dev", "variables" : [ { "key" : "token", "value" : "abc", "isSecret" : true } ] } ],
      "history" : [ { "date" : "2026-10-01T12:13:14Z", "duration" : 0.123, "id" : "DDDDDDDD-0000-0000-0000-000000000001", "request" : { "name" : "x" }, "status" : 200 } ]
    }
    """;

    [Fact]
    public void ReadsTheMacWorkspaceFormat()
    {
        var file = Json.Deserialize<WorkspaceFile>(MacWorkspace)!;
        var collection = Assert.Single(file.Collections);
        Assert.Equal(AuthType.Bearer, collection.Auth.Type);
        var folder = Assert.IsType<Folder>(Assert.Single(collection.Items));
        var request = Assert.IsType<ApiRequest>(Assert.Single(folder.Items));
        Assert.Equal(BodyMode.Json, request.Body.Mode);
        Assert.Equal("{\"a\": 1.50}", request.Body.Raw);
        Assert.Equal(new DateTime(2026, 10, 1, 12, 13, 14, DateTimeKind.Utc), file.History[0].Date);
        Assert.True(file.Environments[0].Variables[0].IsSecret);
        Assert.True(file.Environments[0].Variables[0].Enabled, "a missing key takes its default");
        Assert.Equal("GET", file.History[0].Request.Method);
    }

    [Fact]
    public void WritesWhatTheMacReads()
    {
        var file = Json.Deserialize<WorkspaceFile>(MacWorkspace)!;
        var json = JsonNode.Parse(Json.Serialize(file))!;
        var collection = json["collections"]![0]!;
        Assert.Equal("11111111-2222-3333-4444-555555555555", (string?)collection["id"]);
        Assert.Equal("bearer", (string?)collection["auth"]!["type"]);
        Assert.Equal("header", (string?)collection["auth"]!["location"]);
        Assert.Equal("auth", (string?)collection["items"]![0]!["folder"]!["name"]);
        Assert.Equal("pm.test()", (string?)collection["extras"]!["event"]![0]!["script"]!["exec"]![0]);
        Assert.Equal("2026-10-01T12:13:14Z", (string?)json["history"]![0]!["date"]);
        Assert.Null(json["history"]![0]!["error"]);   // nil optionals are left out, as Swift does
        Assert.Contains("https://api.test/v1/auth/login", Json.Serialize(file));  // slashes unescaped
    }

    [Fact]
    public void UnknownEnumValuesFallBack()
    {
        var body = Json.Deserialize<RequestBody>("""{ "mode": "graphql" }""")!;
        Assert.Equal(BodyMode.None, body.Mode);
        Assert.Equal("apiKey", (string?)JsonNode.Parse(Json.Serialize(new Auth { Type = AuthType.ApiKey }))!["type"]);
    }

    [Fact]
    public void TreeEditsAndEffectiveAuth()
    {
        var collection = Json.Deserialize<WorkspaceFile>(MacWorkspace)!.Collections[0];
        var id = Guid.Parse("CCCCCCCC-0000-0000-0000-000000000001");
        Assert.Equal("auth", Assert.Single(collection.Items.FolderChain(id)!).Name);
        Assert.Equal(1, collection.Items.RequestCount());
        Assert.Equal("{{token}}", collection.EffectiveAuth(collection.Items.Request(id)!).Token);

        var copy = collection.Items.Request(id)!.WithNewIds();
        Assert.NotEqual(id, copy.Id);
        Assert.Equal("auth/login", copy.Name);
        Assert.True(collection.Items.InsertBeside(copy, id, after: true));
        Assert.Equal(2, collection.Items.RequestCount());
        Assert.NotNull(collection.Items.RemoveItem(copy.Id));
        Assert.Equal(1, collection.Items.RequestCount());
    }
}
