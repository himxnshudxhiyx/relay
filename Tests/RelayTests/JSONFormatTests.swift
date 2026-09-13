import XCTest
@testable import Relay

final class JSONFormatTests: XCTestCase {
    func testKeepsKeyOrder() {
        let pretty = JSONFormat.pretty(#"{"z":1,"a":2,"m":{"y":true,"b":null}}"#)
        XCTAssertEqual(pretty, """
        {
          "z": 1,
          "a": 2,
          "m": {
            "y": true,
            "b": null
          }
        }
        """)
    }

    func testLeavesNumbersAsWritten() {
        XCTAssertEqual(JSONFormat.pretty("[1.0,1e5,-0.5,2E-3]"), "[\n  1.0,\n  1e5,\n  -0.5,\n  2E-3\n]")
    }

    func testStringsWithEscapesAndStructuralCharacters() {
        let source = #"{"a":"say \"hi\", {ok} [x]: \\","b":"\u00e9"}"#
        XCTAssertEqual(JSONFormat.pretty(source), """
        {
          "a": "say \\"hi\\", {ok} [x]: \\\\",
          "b": "\\u00e9"
        }
        """)
    }

    func testEmptyContainersStayOnOneLine() {
        XCTAssertEqual(JSONFormat.pretty(#"{"a":{ },"b":[ ]}"#), "{\n  \"a\": {},\n  \"b\": []\n}")
        XCTAssertEqual(JSONFormat.pretty("[]"), "[]")
    }

    func testNestedArrays() {
        XCTAssertEqual(JSONFormat.pretty("[[1,2],[3]]"), "[\n  [\n    1,\n    2\n  ],\n  [\n    3\n  ]\n]")
    }

    func testUnicodePassesThrough() {
        XCTAssertEqual(JSONFormat.pretty(#"{"name":"Zoë 🚀 日本"}"#), "{\n  \"name\": \"Zoë 🚀 日本\"\n}")
    }

    func testCustomIndent() {
        XCTAssertEqual(JSONFormat.pretty(#"{"a":1}"#, indent: 4), "{\n    \"a\": 1\n}")
    }

    func testNotJSON() {
        XCTAssertNil(JSONFormat.pretty("<html></html>"))
        XCTAssertNil(JSONFormat.pretty(#"{"a":}"#))
        XCTAssertNil(JSONFormat.pretty(""))
        XCTAssertFalse(JSONFormat.isJSON("hello"))
        XCTAssertTrue(JSONFormat.isJSON("  42 "))
    }

    func testMinify() {
        XCTAssertEqual(JSONFormat.minify("{\n  \"a\" : [ 1, 2 ],\n  \"b\": \"x y\"\n}"), #"{"a":[1,2],"b":"x y"}"#)
    }

    func testHighlightTokens() {
        let text = #"{"id": 7, "ok": true, "name": "x"}"#
        let tokens = JSONFormat.highlightRanges(text).map { ((text as NSString).substring(with: $0.0), $0.1) }
        let nonPunctuation = tokens.filter { $0.1 != .punctuation }
        XCTAssertEqual(nonPunctuation.map(\.0), [#""id""#, "7", #""ok""#, "true", #""name""#, #""x""#])
        XCTAssertEqual(nonPunctuation.map(\.1), [.key, .number, .key, .literal, .key, .string])
        XCTAssertEqual(tokens.first?.1, .punctuation)
    }

    func testHighlightRangesAreUTF16() {
        let text = #"{"🚀": "é"}"#
        let key = JSONFormat.highlightRanges(text).first { $0.1 == .key }
        XCTAssertEqual(key.map { (text as NSString).substring(with: $0.0) }, #""🚀""#)
    }

    func testXMLPretty() {
        XCTAssertEqual(XMLFormat.pretty(#"<a x="1"><b>hi</b><c/></a>"#), "<a x=\"1\">\n  <b>hi</b>\n  <c/>\n</a>")
        XCTAssertNil(XMLFormat.pretty("{}"))
    }

    func testLargeDocumentIsFast() {
        let item = #"{"id":123456,"name":"Item name here","tags":["a","b","c"],"price":12.5,"active":true},"#
        let body = "[" + String(repeating: item, count: 5_000_000 / item.utf8.count) + #"{"end":null}]"#
        let start = Date()
        let pretty = JSONFormat.pretty(body)
        XCTAssertNotNil(pretty)
        // Generous: tests run unoptimised.
        XCTAssertLessThan(Date().timeIntervalSince(start), 10)
    }
}
