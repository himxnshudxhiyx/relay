import Foundation

/// First-launch content: a small collection that shows how variables,
/// folders and inherited auth fit together, against public echo APIs.
enum Seed {
    static func make() -> (collections: [APICollection], environments: [APIEnvironment]) {
        func request(_ name: String, _ method: String, _ url: String, _ change: (inout APIRequest) -> Void = { _ in }) -> CollectionItem {
            var r = APIRequest()
            r.name = name
            r.method = method
            r.url = url
            r.params = URLQuery.params(fromURL: url, previous: [])
            change(&r)
            return .request(r)
        }

        let posts = Folder(name: "Posts", items: [
            request("List posts", "GET", "{{baseUrl}}/posts?_limit=5"),
            request("Get a post", "GET", "{{baseUrl}}/posts/1"),
            request("Create a post", "POST", "{{baseUrl}}/posts") { r in
                r.body.mode = .json
                r.body.raw = """
                {
                  "title": "Hello from Relay",
                  "body": "Sent with {{$timestamp}}",
                  "userId": 1
                }
                """
            },
            request("Delete a post", "DELETE", "{{baseUrl}}/posts/1"),
        ])

        var echo = Folder(name: "Echo", items: [
            request("Query string", "GET", "https://httpbin.org/get?hello=world&from=relay"),
            request("Form submit", "POST", "https://httpbin.org/post") { r in
                r.body.mode = .form
                r.body.form = [KeyValue("name", "Relay"), KeyValue("kind", "api client")]
            },
            request("Inherited bearer token", "GET", "https://httpbin.org/bearer"),
        ])
        echo.auth = Auth(type: .bearer, token: "{{token}}")

        var collection = APICollection(name: "Getting Started", items: [.folder(posts), .folder(echo)])
        collection.variables = [KeyValue("token", "relay-demo-token")]
        collection.docs = "Requests in Echo inherit the folder's bearer token, which comes from the collection variable {{token}}."

        let environment = APIEnvironment(name: "JSONPlaceholder", variables: [
            KeyValue("baseUrl", "https://jsonplaceholder.typicode.com")
        ])
        return ([collection], [environment])
    }
}
