import XCTest
@testable import Relay

final class CurlTests: XCTestCase {
    private func header(_ request: APIRequest, _ name: String) -> String? {
        request.headers.first { $0.key.lowercased() == name.lowercased() }?.value
    }

    // MARK: - Parsing

    func testRequestNameIsHostAndPath() {
        XCTAssertEqual(CurlParser.requestName(for: "https://httpbin.org/get?hello=world#top"), "httpbin.org/get")
        XCTAssertEqual(CurlParser.requestName(for: "http://localhost:3000/api/items/"), "localhost:3000/api/items")
        XCTAssertEqual(CurlParser.requestName(for: "https://api.example.com"), "api.example.com")
        XCTAssertEqual(CurlParser.requestName(for: "https://user:secret@api.example.com/v1"), "api.example.com/v1")
        XCTAssertEqual(CurlParser.requestName(for: "{{baseUrl}}/posts/1"), "{{baseUrl}}/posts/1")
        XCTAssertEqual(CurlParser.requestName(for: "https://x.test/" + String(repeating: "a", count: 100)).count, 80)
    }

    func testChromeCopyAsCurl() throws {
        let command = """
        curl 'https://api.example.com/v1/users?page=2' \\
          -H 'accept: application/json' \\
          -H 'authorization: Bearer abc.def' \\
          -H $'x-note: it\\'s fine' \\
          --data-raw '{"name":"Ada","tags":["x","y"],"meta":{}}' \\
          --compressed
        """
        let r = try CurlParser.parse(command)
        XCTAssertEqual(r.method, "POST")
        XCTAssertEqual(r.url, "https://api.example.com/v1/users?page=2")
        XCTAssertEqual(r.name, "api.example.com/v1/users")
        XCTAssertEqual(r.params.map(\.key), ["page"])
        XCTAssertEqual(r.params.map(\.value), ["2"])
        XCTAssertEqual(header(r, "accept"), "application/json")
        XCTAssertEqual(header(r, "x-note"), "it's fine")
        XCTAssertNil(header(r, "authorization"))
        XCTAssertEqual(r.auth.type, .bearer)
        XCTAssertEqual(r.auth.token, "abc.def")
        XCTAssertEqual(r.body.mode, .json)
        XCTAssertEqual(r.body.raw, """
        {
          "name": "Ada",
          "tags": [
            "x",
            "y"
          ],
          "meta": {}
        }
        """)
    }

    func testPostmanStyle() throws {
        let r = try CurlParser.parse("""
        curl --location 'https://x.io/login' --header 'Content-Type: application/json' --data '{"a":1}'
        """)
        XCTAssertEqual(r.method, "POST")
        XCTAssertEqual(r.url, "https://x.io/login")
        XCTAssertTrue(r.headers.isEmpty, "implied Content-Type is dropped")
        XCTAssertEqual(r.body.mode, .json)
        XCTAssertEqual(r.auth.type, .inherit)
    }

    func testUserFlagIsBasicAuth() throws {
        let r = try CurlParser.parse("curl -u admin:s3:cret https://x.io/me")
        XCTAssertEqual(r.method, "GET")
        XCTAssertEqual(r.auth.type, .basic)
        XCTAssertEqual(r.auth.username, "admin")
        XCTAssertEqual(r.auth.password, "s3:cret")
    }

    func testBasicAuthorizationHeaderIsDecoded() throws {
        let r = try CurlParser.parse("curl https://x.io -H 'Authorization: Basic YWxhZGRpbjpvcGVuc2VzYW1l'")
        XCTAssertEqual(r.auth.type, .basic)
        XCTAssertEqual(r.auth.username, "aladdin")
        XCTAssertEqual(r.auth.password, "opensesame")
        XCTAssertTrue(r.headers.isEmpty)
    }

    func testFormURLEncoded() throws {
        let r = try CurlParser.parse("curl -X POST https://x.io/f -d 'name=John+Doe&city=New%20York'")
        XCTAssertEqual(r.method, "POST")
        XCTAssertEqual(r.body.mode, .form)
        XCTAssertEqual(r.body.form.map(\.key), ["name", "city"])
        XCTAssertEqual(r.body.form.map(\.value), ["John Doe", "New York"])
    }

    func testDataURLEncodeKeepsLiteralValue() throws {
        let r = try CurlParser.parse("curl https://x.io -H 'Content-Type: application/x-www-form-urlencoded' --data-urlencode 'q=50% & more'")
        XCTAssertEqual(r.body.mode, .form)
        XCTAssertEqual(r.body.form.first?.value, "50% & more")
        XCTAssertTrue(r.headers.isEmpty)
    }

    func testMultipartWithFile() throws {
        let r = try CurlParser.parse(#"curl -F 'file=@/tmp/a.png;type=image/png' -F 'title="Hi"' https://x.io/up"#)
        XCTAssertEqual(r.method, "POST")
        XCTAssertEqual(r.body.mode, .multipart)
        XCTAssertEqual(r.body.form.count, 2)
        XCTAssertEqual(r.body.form[0].key, "file")
        XCTAssertEqual(r.body.form[0].value, "/tmp/a.png")
        XCTAssertTrue(r.body.form[0].isFile)
        XCTAssertEqual(r.body.form[1].value, "Hi")
        XCTAssertFalse(r.body.form[1].isFile)
    }

    func testGetFlagMovesDataToQuery() throws {
        let r = try CurlParser.parse("curl -G https://x.io/search -d q=swift -d lang=en")
        XCTAssertEqual(r.method, "GET")
        XCTAssertEqual(r.url, "https://x.io/search?q=swift&lang=en")
        XCTAssertEqual(r.params.count, 2)
        XCTAssertEqual(r.body.mode, .none)
    }

    func testCombinedAndInlineFlags() throws {
        let r = try CurlParser.parse("curl -sSLk -XPUT https://x.io/a --data-binary 'plain words' --max-time=5")
        XCTAssertEqual(r.method, "PUT")
        XCTAssertEqual(r.url, "https://x.io/a")
        XCTAssertEqual(r.body.mode, .text)
        XCTAssertEqual(r.body.raw, "plain words")
    }

    func testHeadFlag() throws {
        XCTAssertEqual(try CurlParser.parse("curl -I https://x.io").method, "HEAD")
        XCTAssertEqual(try CurlParser.parse("curl -sI https://x.io").method, "HEAD")
    }

    func testJSONFlag() throws {
        let r = try CurlParser.parse(#"curl --json '{"k":true}' https://x.io/j"#)
        XCTAssertEqual(r.method, "POST")
        XCTAssertEqual(r.body.mode, .json)
        XCTAssertNil(header(r, "content-type"))
        XCTAssertEqual(header(r, "accept"), "application/json")
    }

    func testWindowsContinuationsPromptAndExe() throws {
        let r = try CurlParser.parse("$ curl.exe ^\n \"https://x.io/v\" ^\r\n -H \"A: b\"")
        XCTAssertEqual(r.url, "https://x.io/v")
        XCTAssertEqual(header(r, "A"), "b")
    }

    func testErrors() {
        XCTAssertThrowsError(try CurlParser.parse("curl -H 'A: b'"))
        XCTAssertThrowsError(try CurlParser.parse("wget https://x.io"))
        XCTAssertThrowsError(try CurlParser.parse("curl 'https://x.io"))
        XCTAssertThrowsError(try CurlParser.parse("curl https://x.io -H"))
    }

    // MARK: - Generating

    func testSimpleGet() {
        let request = APIRequest(url: "https://x.io")
        let prepared = RequestPreparer.prepare(request, auth: .noAuth, variables: [:])
        XCTAssertEqual(CurlGenerator.generate(prepared), "curl \\\n  --url 'https://x.io'")
        XCTAssertEqual(CurlGenerator.generate(prepared, multiline: false), "curl --url 'https://x.io'")
    }

    func testQuotesAreEscaped() {
        var request = APIRequest(url: "https://x.io")
        request.headers = [KeyValue("X-Note", "it's")]
        let out = CurlGenerator.generate(RequestPreparer.prepare(request, auth: .noAuth, variables: [:]), multiline: false)
        XCTAssertEqual(out, #"curl --url 'https://x.io' --header 'X-Note: it'\''s'"#)
    }

    func testBodiesAndBasicAuth() {
        var request = APIRequest(method: "POST", url: "https://x.io/up")
        request.body.mode = .multipart
        request.body.form = [KeyValue("file", "/tmp/a b.png", isFile: true), KeyValue("title", "Hi")]
        let auth = Auth(type: .basic, username: "u", password: "p")
        let out = CurlGenerator.generate(RequestPreparer.prepare(request, auth: auth, variables: [:]), multiline: false)
        XCTAssertEqual(out, "curl --request POST --url 'https://x.io/up' --user 'u:p' --form 'file=@/tmp/a b.png' --form 'title=Hi'")

        request.body.mode = .form
        let form = CurlGenerator.generate(RequestPreparer.prepare(request, auth: .noAuth, variables: [:]), multiline: false)
        XCTAssertTrue(form.hasSuffix("--data 'file=%2Ftmp%2Fa+b.png&title=Hi'"), form)
    }

    func testRoundTrip() throws {
        var request = APIRequest(method: "PATCH", url: "https://x.io/items/1?x=1")
        request.headers = [KeyValue("X-Trace", "it's 1")]
        request.body.mode = .json
        request.body.raw = "{\n  \"a\": 1\n}"
        let auth = Auth(type: .bearer, token: "tok")

        for multiline in [true, false] {
            let command = CurlGenerator.generate(RequestPreparer.prepare(request, auth: auth, variables: [:]), multiline: multiline)
            let parsed = try CurlParser.parse(command)
            XCTAssertEqual(parsed.method, "PATCH")
            XCTAssertEqual(parsed.url, request.url)
            XCTAssertEqual(parsed.headers.map(\.key), ["X-Trace"])
            XCTAssertEqual(parsed.headers.map(\.value), ["it's 1"])
            XCTAssertEqual(parsed.auth.type, .bearer)
            XCTAssertEqual(parsed.auth.token, "tok")
            XCTAssertEqual(parsed.body.mode, .json)
            XCTAssertEqual(parsed.body.raw, request.body.raw)
        }

        var form = APIRequest(method: "POST", url: "https://x.io/f")
        form.body.mode = .form
        form.body.form = [KeyValue("q", "a&b=c d"), KeyValue("n", "1")]
        let parsed = try CurlParser.parse(CurlGenerator.generate(RequestPreparer.prepare(form, auth: .noAuth, variables: [:])))
        XCTAssertEqual(parsed.body.mode, .form)
        XCTAssertEqual(parsed.body.form.map(\.value), ["a&b=c d", "1"])
    }
}
