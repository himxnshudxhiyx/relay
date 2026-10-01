import Foundation

/// Collapsible `{…}` / `[…]` blocks in multi-line JSON, like Postman's
/// response view. Works on the text as the server formatted it, so folding
/// never reorders keys or reformats numbers.
struct JSONFolding {
    /// An opener at the end of its line and its matching closer, as UTF-16 offsets into `source`.
    struct Region { let open: Int; let close: Int }

    let source: String
    let regions: [Region]
    private(set) var folded: Set<Int> = []
    /// `source` with folded regions shown as `{…}`.
    private(set) var display = ""
    /// Display offset of a line start → index into `regions` of the block it opens.
    private(set) var markers: [Int: Int] = [:]
    /// Display offset of each fold's "…" → index into `regions`.
    private(set) var placeholders: [Int: Int] = [:]
    /// Each fold's display offset and the source lines hidden up to and including it.
    private var hidden: [(offset: Int, lines: Int)] = []
    private let units: [UInt16]

    init(_ source: String) {
        self.source = source
        units = Array(source.utf16)
        regions = Self.regions(in: units)
        render()
    }

    /// Source lines hidden by folds before display `offset`, so line numbers
    /// can skip over a fold the way the original text is numbered.
    func hiddenLines(before offset: Int) -> Int {
        var low = 0, high = hidden.count
        while low < high {
            let mid = (low + high) / 2
            if hidden[mid].offset <= offset { low = mid + 1 } else { high = mid }
        }
        return low == 0 ? 0 : hidden[low - 1].lines
    }

    func isFolded(_ region: Int) -> Bool { folded.contains(regions[region].open) }

    /// The fold whose `{…}` covers display `offset`, if any.
    func placeholder(at offset: Int) -> Int? {
        (offset - 1...offset + 1).lazy.compactMap { placeholders[$0] }.first
    }

    mutating func toggle(_ region: Int) {
        let key = regions[region].open
        if folded.remove(key) == nil { folded.insert(key) }
        render()
    }

    private mutating func render() {
        let ns = source as NSString
        let out = NSMutableString()
        var markers: [Int: Int] = [:]
        var hidden: [(offset: Int, lines: Int)] = []
        var placeholders: [Int: Int] = [:]
        var cursor = 0
        var lineStart = 0

        func copy(upTo end: Int) {
            guard end > cursor else { return }
            let chunk = ns.substring(with: NSRange(location: cursor, length: end - cursor)) as NSString
            let newline = chunk.range(of: "\n", options: .backwards)
            if newline.location != NSNotFound { lineStart = out.length + newline.location + 1 }
            out.append(chunk as String)
            cursor = end
        }

        for (index, region) in regions.enumerated() where region.open >= cursor {
            copy(upTo: region.open + 1)
            // Two blocks can open on one display line (`}, {` after a fold); the first keeps the marker.
            if markers[lineStart] == nil { markers[lineStart] = index }
            if folded.contains(region.open) {
                placeholders[out.length] = index
                out.append("…")
                let lines = units[(region.open + 1)..<region.close].reduce(0) { $1 == 0x0A ? $0 + 1 : $0 }
                hidden.append((out.length, (hidden.last?.lines ?? 0) + lines))
                cursor = region.close
            }
        }
        copy(upTo: ns.length)
        display = out as String
        self.markers = markers
        self.hidden = hidden
        self.placeholders = placeholders
    }

    /// Blocks whose opener ends its line, skipping brackets inside strings.
    static func regions(in u: [UInt16]) -> [Region] {
        var stack: [(offset: Int, endsLine: Bool)] = []
        var result: [Region] = []
        var inString = false
        var i = 0
        while i < u.count {
            let c = u[i]
            if inString {
                if c == 0x5C { i += 1 } else if c == 0x22 { inString = false }
            } else {
                switch c {
                case 0x22:
                    inString = true
                case 0x7B, 0x5B:
                    var j = i + 1
                    while j < u.count, u[j] == 0x20 || u[j] == 0x09 { j += 1 }
                    stack.append((i, j < u.count && (u[j] == 0x0A || u[j] == 0x0D)))
                case 0x7D, 0x5D:
                    if let open = stack.popLast(), open.endsLine { result.append(Region(open: open.offset, close: i)) }
                default:
                    break
                }
            }
            i += 1
        }
        return result.sorted { $0.open < $1.open }
    }
}
