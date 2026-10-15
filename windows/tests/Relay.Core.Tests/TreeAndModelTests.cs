namespace Relay.Core.Tests;

// Ported from the tree and decoding tests in Tests/RelayTests/CoreTests.swift.
public class TreeAndModelTests
{
    [Fact]
    public void EffectiveAuthWalksUpFolders()
    {
        var request = new ApiRequest { Auth = Auth.Inherit() };
        var inner = new Folder { Name = "Inner", Items = [request] };
        var outer = new Folder { Name = "Outer", Items = [inner], Auth = new Auth { Type = AuthType.ApiKey, Key = "k", Value = "v" } };
        var collection = new ApiCollection { Name = "C", Items = [outer], Auth = new Auth { Type = AuthType.Bearer, Token = "t" } };

        Assert.Equal(AuthType.ApiKey, collection.EffectiveAuth(request).Type);

        // A copy with the same id, as Swift's `var own = request` makes.
        var own = Json.Clone(request);
        own.Auth = new Auth { Type = AuthType.Basic, Username = "u" };
        Assert.Equal(AuthType.Basic, collection.EffectiveAuth(own).Type);
    }

    [Fact]
    public void MoveHelpersAndDescendantCheck()
    {
        var a = new ApiRequest { Name = "A" };
        var b = new ApiRequest { Name = "B" };
        var folder = new Folder { Name = "F", Items = [a] };
        System.Collections.ObjectModel.ObservableCollection<CollectionItem> items = [folder, b];

        Assert.True(items.IsDescendant(a.Id, folder.Id));
        Assert.False(items.IsDescendant(b.Id, folder.Id));

        var removed = items.RemoveItem(b.Id);
        Assert.NotNull(removed);
        Assert.True(items.InsertBeside(removed, a.Id, after: false));
        Assert.Equal(["F"], items.FolderChain(b.Id)?.Select(f => f.Name));
        Assert.Equal(2, items.RequestCount());
    }

    [Fact]
    public void DecodingToleratesMissingKeys()
    {
        var collection = Json.Deserialize<ApiCollection>("""{"name":"Old","items":[{"request":{"url":"https://x.test"}}]}""")!;
        Assert.Equal("Old", collection.Name);
        Assert.Equal(AuthType.None, collection.Auth.Type);
        var r = Assert.IsType<ApiRequest>(Assert.Single(collection.Items));
        Assert.Equal("GET", r.Method);
        Assert.Equal(AuthType.Inherit, r.Auth.Type);
    }

    [Fact]
    public void UnknownEnumValuesFallBack()
    {
        var body = Json.Deserialize<RequestBody>("""{"mode":"graphql-v9"}""")!;
        Assert.Equal(BodyMode.None, body.Mode);
    }
}
