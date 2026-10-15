namespace Relay.Core;

/// <summary>
/// First-launch content: a small collection that shows how variables,
/// folders and inherited auth fit together, against public echo APIs.
/// Ported from Sources/Relay/Store/Seed.swift.
/// </summary>
public static class Seed
{
    public static (List<ApiCollection> Collections, List<ApiEnvironment> Environments) Make()
    {
        static ApiRequest Request(string name, string method, string url, Action<ApiRequest>? change = null)
        {
            var r = new ApiRequest
            {
                Name = name,
                Method = method,
                Url = url,
                Params = [.. UrlQuery.ParamsFromUrl(url, [])],
            };
            change?.Invoke(r);
            return r;
        }

        var posts = new Folder
        {
            Name = "Posts",
            Items =
            [
                Request("List posts", "GET", "{{baseUrl}}/posts?_limit=5"),
                Request("Get a post", "GET", "{{baseUrl}}/posts/1"),
                Request("Create a post", "POST", "{{baseUrl}}/posts", r =>
                {
                    r.Body.Mode = BodyMode.Json;
                    r.Body.Raw = """
                    {
                      "title": "Hello from Relay",
                      "body": "Sent with {{$timestamp}}",
                      "userId": 1
                    }
                    """;
                }),
                Request("Delete a post", "DELETE", "{{baseUrl}}/posts/1"),
            ],
        };

        var echo = new Folder
        {
            Name = "Echo",
            Items =
            [
                Request("Query string", "GET", "https://httpbin.org/get?hello=world&from=relay"),
                Request("Form submit", "POST", "https://httpbin.org/post", r =>
                {
                    r.Body.Mode = BodyMode.Form;
                    r.Body.Form = [new KeyValue("name", "Relay"), new KeyValue("kind", "api client")];
                }),
                Request("Inherited bearer token", "GET", "https://httpbin.org/bearer"),
            ],
            Auth = new Auth { Type = AuthType.Bearer, Token = "{{token}}" },
        };

        var collection = new ApiCollection
        {
            Name = "Getting Started",
            Items = [posts, echo],
            Variables = [new KeyValue("token", "relay-demo-token")],
            Docs = "Requests in Echo inherit the folder's bearer token, which comes from the collection variable {{token}}.",
        };

        var environment = new ApiEnvironment
        {
            Name = "JSONPlaceholder",
            Variables = [new KeyValue("baseUrl", "https://jsonplaceholder.typicode.com")],
        };
        return ([collection], [environment]);
    }
}
