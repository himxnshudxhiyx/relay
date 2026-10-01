import XCTest
@testable import Relay

final class JSONFoldingTests: XCTestCase {
    let json = """
    {
      "note": "not a block {",
      "items": [
        {
          "id": 1
        },
        {
          "id": 2
        }
      ],
      "inline": [1, 2]
    }
    """

    func testFoldsAndUnfoldsBlocks() {
        var f = JSONFolding(json)
        // Root, items, and the two elements; the string's "{" and the one-line array aren't blocks.
        XCTAssertEqual(f.regions.count, 4)
        XCTAssertEqual(f.display, json)

        let items = f.markers[(json as NSString).range(of: "  \"items\"").location]!
        f.toggle(items + 1)  // first element
        XCTAssertTrue(f.display.contains("    {…},\n    {\n"))
        let after = (f.display as NSString).range(of: "{…},\n").location + 5
        XCTAssertEqual(f.hiddenLines(before: after), 2, "line numbers skip the folded element's lines")
        XCTAssertEqual(f.hiddenLines(before: 0), 0)
        let dots = (f.display as NSString).range(of: "…").location
        XCTAssertEqual(f.placeholder(at: dots - 1), items + 1, "clicking { … or } finds the fold")
        XCTAssertEqual(f.placeholder(at: dots + 1), items + 1)
        XCTAssertNil(f.placeholder(at: dots + 2), "the comma after it doesn't")

        f.toggle(items)      // the whole list, with an element folded inside
        XCTAssertTrue(f.display.contains("\"items\": […],\n  \"inline\""))
        XCTAssertEqual(f.markers.count, 2, "hidden elements lose their markers")

        f.toggle(items)
        f.toggle(items + 1)
        XCTAssertEqual(f.display, json, "unfolding everything restores the original text")
    }
}
