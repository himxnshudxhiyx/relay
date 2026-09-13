import XCTest
@testable import Relay

final class PostmanTests: XCTestCase {

    // MARK: - Fixtures

    static let shop = #"""
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
    """#

    static let legacy = #"""
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
    """#

    static let staging = #"""
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
    """#

    private func load(_ json: String) throws -> ImportResult {
        try Postman.importData(Data(json.utf8))
    }

    private func shop() throws -> APICollection {
        let result = try load(Self.shop)
        XCTAssertEqual(result.collections.count, 1)
        return try XCTUnwrap(result.collections.first)
    }

    private func request(_ c: APICollection, _ name: String) throws -> APIRequest {
        func find(_ items: [CollectionItem]) -> APIRequest? {
            for item in items {
                switch item {
                case .request(let r) where r.name == name: return r
                case .folder(let f): if let r = find(f.items) { return r }
                default: break
                }
            }
            return nil
        }
        return try XCTUnwrap(find(c.items), "no request named \(name)")
    }

    // MARK: - Import

    func testCollectionMetadata() throws {
        let c = try shop()
        XCTAssertEqual(c.name, "Shop API")
        XCTAssertEqual(c.docs, "Shop docs")
        XCTAssertEqual(c.variables.map(\.key), ["baseUrl", "port", "old"])
        XCTAssertEqual(c.variables[1].value, "8080")
        XCTAssertFalse(c.variables[2].enabled)
        XCTAssertEqual(c.auth.type, .bearer)
        XCTAssertEqual(c.auth.token, "{{token}}")
        XCTAssertEqual(c.extras["_postman_id"], .string("abc-123"))
        XCTAssertNotNil(c.extras["event"])
        XCTAssertEqual(c.items.count, 5)
        XCTAssertEqual(c.items.requestCount, 7)
    }

    func testNestedFoldersAndInheritedAuth() throws {
        let c = try shop()
        guard case .folder(let users) = c.items[0], case .folder(let admin) = users.items[0] else {
            return XCTFail("expected Users / Admin folders")
        }
        XCTAssertEqual(users.name, "Users")
        XCTAssertEqual(users.auth.type, .inherit)
        XCTAssertEqual(admin.auth.type, .apiKey)

        let list = try request(c, "List admins")
        XCTAssertEqual(list.auth.type, .inherit)
        let listAuth = c.effectiveAuth(for: list)
        XCTAssertEqual(listAuth.type, .apiKey)
        XCTAssertEqual(listAuth.key, "X-Admin")
        XCTAssertEqual(listAuth.value, "secret")
        XCTAssertEqual(listAuth.location, .header)

        let get = try request(c, "Get user")
        XCTAssertEqual(c.effectiveAuth(for: get).type, .bearer)

        let login = try request(c, "Login")
        XCTAssertEqual(c.effectiveAuth(for: login).type, .none)
    }

    func testURLObjectKeepsDisabledQuery() throws {
        let list = try request(try shop(), "List admins")
        XCTAssertEqual(list.url, "{{baseUrl}}/admins?page=1")
        XCTAssertEqual(list.params.map(\.key), ["page", "debug"])
        XCTAssertEqual(list.params.map(\.enabled), [true, false])
        XCTAssertNil(list.extras["response"], "an empty response list isn't worth keeping")
    }

    func testHeadersDocsAndItemExtras() throws {
        let get = try request(try shop(), "Get user")
        XCTAssertEqual(get.headers.map(\.key), ["Accept", "X-Trace"])
        XCTAssertEqual(get.headers.map(\.enabled), [true, false])
        XCTAssertEqual(get.docs, "Fetch one")
        XCTAssertNotNil(get.extras["event"])
        XCTAssertNotNil(get.extras["response"])
        XCTAssertNotNil(get.extras["urlVariable"])
    }

    func testRawJSONBody() throws {
        let login = try request(try shop(), "Login")
        XCTAssertEqual(login.method, "POST")
        XCTAssertEqual(login.url, "{{baseUrl}}/login")
        XCTAssertEqual(login.body.mode, .json)
        XCTAssertEqual(login.body.raw, #"{"user":"a"}"#)
    }

    func testFormDataWithFiles() throws {
        let upload = try request(try shop(), "Upload avatar")
        XCTAssertEqual(upload.body.mode, .multipart)
        XCTAssertEqual(upload.body.form.map(\.key), ["file", "note", "multi"])
        XCTAssertEqual(upload.body.form.map(\.isFile), [true, false, true])
        XCTAssertEqual(upload.body.form[0].value, "/tmp/a.png")
        XCTAssertEqual(upload.body.form[1].value, "hi")
        XCTAssertEqual(upload.body.form[2].value, "/tmp/b.png")
        XCTAssertFalse(upload.body.form[2].enabled)
    }

    func testURLEncodedAndUnsupportedAuth() throws {
        let token = try request(try shop(), "Token")
        XCTAssertEqual(token.body.mode, .form)
        XCTAssertEqual(token.body.form.map(\.key), ["grant_type", "scope"])
        XCTAssertEqual(token.body.form.map(\.enabled), [true, false])
        XCTAssertEqual(token.auth.type, .none)
        XCTAssertNotNil(token.extras["auth"], "OAuth 2 settings are stashed for export")
    }

    func testGraphQLBecomesJSON() throws {
        let graph = try request(try shop(), "Graph")
        XCTAssertEqual(graph.body.mode, .json)
        let payload = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(graph.body.raw.utf8)) as? [String: Any])
        XCTAssertEqual(payload["query"] as? String, "query { me { id } }")
        XCTAssertEqual((payload["variables"] as? [String: Any])?["a"] as? Int, 1)
    }

    func testRequestAsPlainString() throws {
        let plain = try request(try shop(), "Plain")
        XCTAssertEqual(plain.method, "GET")
        XCTAssertEqual(plain.url, "https://example.com/ping?x=1")
        XCTAssertEqual(plain.params.map(\.key), ["x"])
        XCTAssertEqual(plain.params.map(\.value), ["1"])
    }

    func testLegacyDictAuthAndHeaderString() throws {
        let c = try XCTUnwrap(try load(Self.legacy).collections.first)
        XCTAssertEqual(c.auth.type, .none)
        let basic = try request(c, "Basic")
        XCTAssertEqual(basic.method, "GET")
        XCTAssertEqual(basic.auth.type, .basic)
        XCTAssertEqual(basic.auth.username, "ann")
        XCTAssertEqual(basic.auth.password, "pw")
        XCTAssertEqual(basic.headers.map(\.key), ["Accept", "X-Off", "X-Id"])
        XCTAssertEqual(basic.headers.map(\.enabled), [true, false, true])
        XCTAssertEqual(basic.headers[2].value, "7")
    }

    func testEnvironmentWithSecret() throws {
        let result = try load(Self.staging)
        XCTAssertTrue(result.collections.isEmpty)
        let env = try XCTUnwrap(result.environments.first)
        XCTAssertEqual(env.name, "Staging")
        XCTAssertEqual(env.variables.map(\.key), ["baseUrl", "token", "off"])
        XCTAssertEqual(env.variables.map(\.isSecret), [false, true, false])
        XCTAssertEqual(env.variables.map(\.enabled), [true, true, false])
    }

    func testDumpAndArray() throws {
        let dump = try load(#"{"version": 1, "collections": [\#(Self.shop)], "environments": [\#(Self.staging)]}"#)
        XCTAssertEqual(dump.collections.count, 1)
        XCTAssertEqual(dump.environments.count, 1)

        let array = try load("[\(Self.legacy), \(Self.staging)]")
        XCTAssertEqual(array.collections.map(\.name), ["Old"])
        XCTAssertEqual(array.environments.map(\.name), ["Staging"])

        let wrapped = try load(#"{"collection": \#(Self.legacy)}"#)
        XCTAssertEqual(wrapped.collections.count, 1)
    }

    func testGarbageThrows() {
        for input in ["nope", #"{"foo": 1}"#, "[]", "42", #"{"requests": [], "name": "v1"}"#] {
            XCTAssertThrowsError(try load(input), input) { error in
                XCTAssertTrue(error is PostmanError, "\(input): \(error)")
            }
        }
    }

    // MARK: - Export

    func testExportIsV21Shape() throws {
        let data = try Postman.exportCollection(try shop())
        let root = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let info = try XCTUnwrap(root["info"] as? [String: Any])
        XCTAssertEqual(info["schema"] as? String, Postman.schema)
        XCTAssertEqual(info["_postman_id"] as? String, "abc-123")
        XCTAssertEqual((root["auth"] as? [String: Any])?["type"] as? String, "bearer")
        XCTAssertNotNil(root["event"])

        let users = try XCTUnwrap((root["item"] as? [[String: Any]])?.first)
        XCTAssertNil(users["auth"], "inherit is written as no auth key")
        let admin = try XCTUnwrap((users["item"] as? [[String: Any]])?.first)
        let list = try XCTUnwrap((admin["item"] as? [[String: Any]])?.first)
        let url = try XCTUnwrap((list["request"] as? [String: Any])?["url"] as? [String: Any])
        XCTAssertEqual(url["raw"] as? String, "{{baseUrl}}/admins?page=1")
        XCTAssertEqual(url["host"] as? [String], ["{{baseUrl}}"])
        XCTAssertEqual(url["path"] as? [String], ["admins"])
        let query = try XCTUnwrap(url["query"] as? [[String: Any]])
        XCTAssertEqual(query.count, 2)
        XCTAssertEqual(query[1]["disabled"] as? Bool, true)
        XCTAssertNotNil(list["response"])
    }

    func testURLParts() {
        let full = Postman.urlObject("https://api.test:8443/v1/users?id=2", params: [])
        XCTAssertEqual(full["protocol"] as? String, "https")
        XCTAssertEqual(full["host"] as? [String], ["api", "test"])
        XCTAssertEqual(full["port"] as? String, "8443")
        XCTAssertEqual(full["path"] as? [String], ["v1", "users"])

        let templated = Postman.urlObject("{{base.url}}/a/b", params: [])
        XCTAssertNil(templated["protocol"])
        XCTAssertEqual(templated["host"] as? [String], ["{{base.url}}"])
        XCTAssertEqual(templated["path"] as? [String], ["a", "b"])
    }

    func testCollectionRoundTrip() throws {
        let original = try shop()
        let again = try XCTUnwrap(try Postman.importData(try Postman.exportCollection(original)).collections.first)
        XCTAssertEqual(Self.stripIDs(again), Self.stripIDs(original))

        let token = try request(again, "Token")
        XCTAssertEqual(token.extras["auth"], try request(original, "Token").extras["auth"])
        let get = try request(again, "Get user")
        XCTAssertEqual(get.extras["event"], try request(original, "Get user").extras["event"])
    }

    func testEnvironmentAndBackupRoundTrip() throws {
        let env = try XCTUnwrap(try load(Self.staging).environments.first)
        let envAgain = try XCTUnwrap(try Postman.importData(try Postman.exportEnvironment(env)).environments.first)
        XCTAssertEqual(Self.stripIDs(envAgain.variables), Self.stripIDs(env.variables))
        XCTAssertEqual(envAgain.name, env.name)

        let backup = try Postman.exportBackup(collections: [try shop(), try XCTUnwrap(try load(Self.legacy).collections.first)],
                                              environments: [env])
        let restored = try Postman.importData(backup)
        XCTAssertEqual(restored.collections.map(\.name), ["Shop API", "Old"])
        XCTAssertEqual(restored.environments.map(\.name), ["Staging"])
    }

    // MARK: - Helpers

    private static let zero = UUID(uuidString: "00000000-0000-0000-0000-000000000000")!

    static func stripIDs(_ rows: [KeyValue]) -> [KeyValue] {
        rows.map { var row = $0; row.id = zero; return row }
    }

    static func stripIDs(_ items: [CollectionItem]) -> [CollectionItem] {
        items.map { item in
            switch item {
            case .folder(var f):
                f.id = zero
                f.items = stripIDs(f.items)
                return .folder(f)
            case .request(var r):
                r.id = zero
                r.params = stripIDs(r.params)
                r.headers = stripIDs(r.headers)
                r.body.form = stripIDs(r.body.form)
                return .request(r)
            }
        }
    }

    static func stripIDs(_ c: APICollection) -> APICollection {
        var c = c
        c.id = zero
        c.variables = stripIDs(c.variables)
        c.items = stripIDs(c.items)
        return c
    }
}
