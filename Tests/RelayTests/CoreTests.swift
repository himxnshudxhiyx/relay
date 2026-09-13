import XCTest
@testable import Relay

final class CoreTests: XCTestCase {
    // MARK: URL ⇄ params

    func testParamsFollowURLAndKeepDisabledRows() {
        let disabled = KeyValue("debug", "1", enabled: false)
        let first = KeyValue("page", "1")
        let rows = URLQuery.params(fromURL: "https://api.test/users?page=2&limit={{limit}}", previous: [first, disabled])

        XCTAssertEqual(rows.map(\.key), ["page", "debug", "limit"])
        XCTAssertEqual(rows[0].id, first.id, "the edited row keeps its identity")
        XCTAssertEqual(rows[0].value, "2")
        XCTAssertFalse(rows[1].enabled)
        XCTAssertEqual(rows[2].value, "{{limit}}")
    }

    func testURLFollowsParamsAndKeepsFragment() {
        let params = [KeyValue("q", "a b"), KeyValue("off", "x", enabled: false), KeyValue("flag", "")]
        XCTAssertEqual(URLQuery.url("https://x.test/search?old=1#top", applying: params), "https://x.test/search?q=a b&flag#top")
        XCTAssertEqual(URLQuery.url("https://x.test/search?old=1", applying: []), "https://x.test/search")
    }

    func testEncodedURLKeepsExistingEscapes() {
        let url = URLQuery.encodedURL("https://x.test/a b?q=caf%C3%A9&name=José")
        XCTAssertEqual(url?.absoluteString, "https://x.test/a%20b?q=caf%C3%A9&name=Jos%C3%A9")
        XCTAssertNil(URLQuery.encodedURL("not a url"))
    }

    // MARK: Variables

    func testVariablesResolveNestedAndLeaveUnknown() {
        let values = ["host": "api.test", "base": "https://{{host}}/v1"]
        XCTAssertEqual(Variables.resolve("{{base}}/users/{{ id }}", values), "https://api.test/v1/users/{{ id }}")
        XCTAssertEqual(Variables.names(in: "{{a}} {{b}} {{a}}"), ["a", "b"])
    }

    func testVariablesDoNotLoopOnSelfReference() {
        XCTAssertEqual(Variables.resolve("{{loop}}", ["loop": "{{loop}}"]), "{{loop}}")
    }

    func testEnvironmentOverridesCollection() {
        let scope = VariableScope(collection: [KeyValue("base", "collection"), KeyValue("only", "c")],
                                  environment: [KeyValue("base", "env"), KeyValue("off", "x", enabled: false)])
        XCTAssertEqual(scope.values["base"], "env")
        XCTAssertEqual(scope.values["only"], "c")
        XCTAssertNil(scope.values["off"])
        XCTAssertEqual(scope.source(of: "base"), .environment)
        XCTAssertEqual(scope.source(of: "only"), .collection)
        XCTAssertEqual(scope.source(of: "$guid"), .dynamic)
        XCTAssertEqual(scope.source(of: "nope"), .missing)
    }

    // MARK: Preparing requests

    func testPrepareAppliesAuthAndContentType() {
        var request = APIRequest()
        request.method = "post"
        request.url = "{{base}}/items"
        request.headers = [KeyValue("X-Trace", "{{trace}}"), KeyValue("X-Off", "1", enabled: false)]
        request.body = RequestBody(mode: .json, raw: #"{"a":1}"#)

        let prepared = RequestPreparer.prepare(request, auth: Auth(type: .bearer, token: "{{token}}"),
                                               variables: ["base": "api.test", "trace": "t1", "token": "secret"])
        XCTAssertEqual(prepared.method, "POST")
        XCTAssertEqual(prepared.url, "http://api.test/items", "a URL with no scheme gets http://")
        XCTAssertEqual(prepared.header("X-Trace"), "t1")
        XCTAssertNil(prepared.header("X-Off"))
        XCTAssertEqual(prepared.header("Authorization"), "Bearer secret")
        XCTAssertEqual(prepared.header("Content-Type"), "application/json")
    }

    func testPrepareWithoutVariablesKeepsPlaceholders() {
        var request = APIRequest()
        request.url = "{{base}}/items"
        let prepared = RequestPreparer.prepare(request, auth: Auth(type: .basic, username: "{{user}}", password: "p"), variables: nil)
        XCTAssertEqual(prepared.url, "{{base}}/items")
        XCTAssertEqual(prepared.basicAuth?.username, "{{user}}")
        XCTAssertTrue(CurlGenerator.generate(prepared).contains("--user '{{user}}:p'"))
    }

    func testAPIKeyInQuery() {
        var request = APIRequest()
        request.url = "https://x.test/a?b=1#frag"
        let prepared = RequestPreparer.prepare(request, auth: Auth(type: .apiKey, key: "api key", value: "v&1", location: .query), variables: [:])
        XCTAssertEqual(prepared.url, "https://x.test/a?b=1&api+key=v%261#frag")
    }

    func testEffectiveAuthWalksUpFolders() {
        var request = APIRequest()
        request.auth = .inherit
        let inner = Folder(name: "Inner", items: [.request(request)])
        var outer = Folder(name: "Outer", items: [.folder(inner)])
        outer.auth = Auth(type: .apiKey, key: "k", value: "v")
        let collection = APICollection(name: "C", items: [.folder(outer)], auth: Auth(type: .bearer, token: "t"))

        XCTAssertEqual(collection.effectiveAuth(for: request).type, .apiKey)

        var own = request
        own.auth = Auth(type: .basic, username: "u")
        XCTAssertEqual(collection.effectiveAuth(for: own).type, .basic)
    }

    // MARK: Variable suggestions

    func testCompletionContextFindsOpenBraces() {
        XCTAssertEqual(CompletionContext.find(in: "{{", cursor: 2)?.prefix, "")
        XCTAssertEqual(CompletionContext.find(in: "https://{{bas", cursor: 13),
                       CompletionContext(range: NSRange(location: 10, length: 3), prefix: "bas"))
        XCTAssertEqual(CompletionContext.find(in: #"{"a": "{{tok"}"#, cursor: 12)?.prefix, "tok")
        // Editing the name inside a closed variable still completes.
        XCTAssertEqual(CompletionContext.find(in: "{{bas}}/x", cursor: 4)?.prefix, "ba")
    }

    func testCompletionReplacesWholeName() {
        let inside = CompletionContext.find(in: "{{bas}}/x", cursor: 4)!
        let r1 = inside.replacement(in: "{{bas}}/x")
        XCTAssertEqual(r1.range, NSRange(location: 2, length: 3))
        XCTAssertTrue(r1.closed)

        let open = CompletionContext.find(in: "{{ba", cursor: 4)!
        let r2 = open.replacement(in: "{{ba")
        XCTAssertEqual(r2.range, NSRange(location: 2, length: 2))
        XCTAssertFalse(r2.closed)
    }

    func testCompletionContextIgnoresEverythingElse() {
        XCTAssertNil(CompletionContext.find(in: "{", cursor: 1))
        XCTAssertNil(CompletionContext.find(in: "{{base}}", cursor: 8), "after the closing braces")
        XCTAssertNil(CompletionContext.find(in: "{{base url", cursor: 10), "a space ends the name")
        XCTAssertNil(CompletionContext.find(in: #"{"a": 1}"#, cursor: 5))
        XCTAssertNil(CompletionContext.find(in: "abc", cursor: 99))
    }

    func testSuggestionsOrderAndFiltering() {
        let scope = VariableScope(
            collection: [KeyValue("baseUrl", "collection"), KeyValue("apiVersion", "v2")],
            environment: [KeyValue("baseUrl", "https://env"), KeyValue("token", "t", isSecret: true),
                          KeyValue("off", "x", enabled: false)]
        )

        let all = scope.suggestions(matching: "")
        XCTAssertEqual(all.map(\.name), ["baseUrl", "token", "apiVersion"])
        XCTAssertEqual(all[0].source, .environment, "the environment wins a name clash")
        XCTAssertEqual(all[0].value, "https://env")
        XCTAssertTrue(all[1].isSecret)

        XCTAssertEqual(scope.suggestions(matching: "ver").map(\.name), ["apiVersion"])
        XCTAssertEqual(scope.suggestions(matching: "B").map(\.name), ["baseUrl"], "prefix match, case-insensitive")
        XCTAssertEqual(scope.suggestions(matching: "a").map(\.name), ["apiVersion", "baseUrl"], "prefix matches first")
        XCTAssertFalse(all.contains { $0.source == .dynamic }, "built-ins wait for a $")
        XCTAssertEqual(scope.suggestions(matching: "$ti").map(\.name), ["$timestamp"])
    }

    // MARK: Pasting cURL into the URL box

    func testPastedMultilineCurlParses() throws {
        // Exactly what Relay's own "Copy as cURL" produces.
        let pasted = """
        curl \\
          --url 'https://httpbin.org/get?hello=world&from=relay' \\
          --header 'Authorization: Bearer relay-demo-token'
        """
        let request = try XCTUnwrap(URLBar.parseCurl(pasted))
        XCTAssertEqual(request.url, "https://httpbin.org/get?hello=world&from=relay")
        XCTAssertEqual(request.params.map(\.key), ["hello", "from"])
        XCTAssertEqual(request.auth.type, .bearer)
        XCTAssertEqual(request.auth.token, "relay-demo-token")
        XCTAssertTrue(request.headers.isEmpty)
        XCTAssertNil(URLBar.parseCurl("https://example.com/curl something"))
    }

    // MARK: Tree edits

    func testMoveHelpersAndDescendantCheck() {
        let a = APIRequest(name: "A")
        let b = APIRequest(name: "B")
        let folder = Folder(name: "F", items: [.request(a)])
        var items: [CollectionItem] = [.folder(folder), .request(b)]

        XCTAssertTrue(items.isDescendant(a.id, of: folder.id))
        XCTAssertFalse(items.isDescendant(b.id, of: folder.id))

        let removed = items.remove(b.id)
        XCTAssertNotNil(removed)
        XCTAssertTrue(items.insert(removed!, beside: a.id, after: false))
        XCTAssertEqual(items.folderChain(to: b.id)?.map(\.name), ["F"])
        XCTAssertEqual(items.requestCount, 2)
    }

    // MARK: Stored data stays readable

    func testDecodingToleratesMissingKeys() throws {
        let json = #"{"name":"Old","items":[{"request":{"url":"https://x.test"}}]}"#
        let collection = try JSONDecoder().decode(APICollection.self, from: Data(json.utf8))
        XCTAssertEqual(collection.name, "Old")
        XCTAssertEqual(collection.auth.type, AuthType.none)
        guard case .request(let r) = collection.items.first else { return XCTFail("expected a request") }
        XCTAssertEqual(r.method, "GET")
        XCTAssertEqual(r.auth.type, .inherit)
    }

    func testUnknownEnumValuesFallBack() throws {
        let body = try JSONDecoder().decode(RequestBody.self, from: Data(#"{"mode":"graphql-v9"}"#.utf8))
        XCTAssertEqual(body.mode, BodyMode.none)
    }

    func testCurlWithResponseText() {
        let result = HTTPResult(status: 201, headers: [("Content-Type", "application/json")], body: Data(#"{"id":1}"#.utf8),
                                duration: 0.1234, ttfb: nil, redirects: 0, finalURL: "https://x.test",
                                sentMethod: "POST", sentURL: "https://x.test", sentHeaders: [], sentBody: "",
                                kind: .json, text: #"{"id":1}"#, pretty: "{\n  \"id\": 1\n}")
        let text = Format.curlWithResponse(curl: "curl https://x.test", result: result, includeHeaders: true)
        XCTAssertEqual(text, "curl https://x.test\n\nHTTP 201 Created  (123 ms, 8 B)\nContent-Type: application/json\n\n{\n  \"id\": 1\n}")
    }
}
