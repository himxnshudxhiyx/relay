import Foundation

/// JSON reformatting that treats the document as text, not as a value.
///
/// Round-tripping through `JSONSerialization` would sort keys, turn `1.0`
/// into `1` and rewrite `é` as `é` — so a response would no longer show
/// what the server actually sent. The reindenter only moves whitespace.
enum JSONFormat {
    enum Token { case key, string, number, literal, punctuation }

    static func isJSON(_ text: String) -> Bool {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let first = trimmed.utf8.first else { return false }
        // Cheap reject before handing megabytes of HTML to the parser.
        switch first {
        case UInt8(ascii: "{"), UInt8(ascii: "["), UInt8(ascii: "\""), UInt8(ascii: "-"),
             UInt8(ascii: "0")...UInt8(ascii: "9"), UInt8(ascii: "t"), UInt8(ascii: "f"), UInt8(ascii: "n"):
            break
        default:
            return false
        }
        return (try? JSONSerialization.jsonObject(with: Data(trimmed.utf8), options: .fragmentsAllowed)) != nil
    }

    static func pretty(_ text: String, indent: Int = 2) -> String? {
        guard isJSON(text) else { return nil }
        return reformat(text, indent: max(0, indent))
    }

    static func minify(_ text: String) -> String? {
        guard isJSON(text) else { return nil }
        return reformat(text, indent: nil)
    }

    /// `indent == nil` minifies. Input must already be valid JSON.
    private static func reformat(_ text: String, indent: Int?) -> String {
        var source = text
        var out: [UInt8] = []
        source.withUTF8 { src in
            let n = src.count
            out.reserveCapacity(indent == nil ? n : n + n / 2)
            var depth = 0
            var i = 0
            while i < n {
                let c = src[i]
                switch c {
                case 0x22: // "
                    var j = i + 1
                    while j < n {
                        let d = src[j]
                        if d == 0x5C { j += 2; continue }
                        if d == 0x22 { break }
                        j += 1
                    }
                    let end = min(j + 1, n)
                    out.append(contentsOf: UnsafeBufferPointer(rebasing: src[i..<end]))
                    i = end
                    continue
                case 0x7B, 0x5B: // { [
                    let close: UInt8 = c == 0x7B ? 0x7D : 0x5D
                    var j = i + 1
                    while j < n, isWhitespace(src[j]) { j += 1 }
                    if j < n, src[j] == close {
                        out.append(c)
                        out.append(close)
                        i = j + 1
                        continue
                    }
                    out.append(c)
                    depth += 1
                    if let indent { newline(&out, depth * indent) }
                case 0x7D, 0x5D: // } ]
                    depth = max(0, depth - 1)
                    if let indent { newline(&out, depth * indent) }
                    out.append(c)
                case 0x2C: // ,
                    out.append(c)
                    if let indent { newline(&out, depth * indent) }
                case 0x3A: // :
                    out.append(c)
                    if indent != nil { out.append(0x20) }
                case 0x20, 0x09, 0x0A, 0x0D:
                    break
                default:
                    out.append(c)
                }
                i += 1
            }
        }
        return String(decoding: out, as: UTF8.self)
    }

    @inline(__always)
    private static func isWhitespace(_ c: UInt8) -> Bool {
        c == 0x20 || c == 0x0A || c == 0x0D || c == 0x09
    }

    @inline(__always)
    private static func newline(_ out: inout [UInt8], _ spaces: Int) {
        out.append(0x0A)
        if spaces > 0 { out.append(contentsOf: repeatElement(0x20, count: spaces)) }
    }

    private static let trueWord = Array("true".utf16)
    private static let falseWord = Array("false".utf16)
    private static let nullWord = Array("null".utf16)

    /// Token spans for colouring. Lenient: request bodies are often
    /// half-typed or hold unquoted `{{vars}}`, and should still colour.
    static func highlightRanges(_ text: String) -> [(NSRange, Token)] {
        let u = Array(text.utf16)
        let n = u.count
        var result: [(NSRange, Token)] = []
        var i = 0
        while i < n {
            let c = u[i]
            switch c {
            case 0x22:
                let start = i
                i += 1
                while i < n {
                    if u[i] == 0x5C { i += 2 } else if u[i] == 0x22 { i += 1; break } else { i += 1 }
                }
                i = min(i, n)
                var j = i
                while j < n, u[j] == 0x20 || u[j] == 0x09 || u[j] == 0x0A || u[j] == 0x0D { j += 1 }
                result.append((NSRange(location: start, length: i - start), j < n && u[j] == 0x3A ? .key : .string))
            case 0x2D, 0x30...0x39:
                let start = i
                while i < n, isNumberChar(u[i]) { i += 1 }
                result.append((NSRange(location: start, length: i - start), .number))
            case 0x74, 0x66, 0x6E: // t f n
                let start = i
                while i < n, u[i] >= 0x61, u[i] <= 0x7A { i += 1 }
                let word = u[start..<i]
                if word.elementsEqual(trueWord) || word.elementsEqual(falseWord) || word.elementsEqual(nullWord) {
                    result.append((NSRange(location: start, length: i - start), .literal))
                }
            case 0x7B, 0x7D, 0x5B, 0x5D, 0x2C, 0x3A:
                result.append((NSRange(location: i, length: 1), .punctuation))
                i += 1
            default:
                i += 1
            }
        }
        return result
    }

    @inline(__always)
    private static func isNumberChar(_ c: UInt16) -> Bool {
        (c >= 0x30 && c <= 0x39) || c == 0x2E || c == 0x65 || c == 0x45 || c == 0x2B || c == 0x2D
    }
}

/// Best-effort XML/HTML reindenting. Not a parser: good enough to make a
/// minified SOAP or RSS response readable, and it never drops characters.
enum XMLFormat {
    enum Token { case tag, attribute, value, comment }

    static func pretty(_ text: String, indent: Int = 2) -> String? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.hasPrefix("<") else { return nil }
        let chars = Array(trimmed.unicodeScalars)
        let n = chars.count
        var out = String.UnicodeScalarView()
        var depth = 0
        var i = 0
        // Text right after an opening tag stays on its line: <name>Ada</name>.
        var inlineText = false
        var lastWasOpen = false

        func newline() {
            if !out.isEmpty { out.append("\n") }
            out.append(contentsOf: String(repeating: " ", count: depth * indent).unicodeScalars)
        }

        func hasPrefix(_ s: String, at index: Int) -> Bool {
            let p = Array(s.unicodeScalars)
            guard index + p.count <= n else { return false }
            for k in 0..<p.count where chars[index + k] != p[k] { return false }
            return true
        }

        while i < n {
            if chars[i] == "<" {
                var end: Int
                if hasPrefix("<!--", at: i) {
                    end = i + 4
                    while end < n, !hasPrefix("-->", at: end) { end += 1 }
                    end = min(end + 3, n)
                } else if hasPrefix("<![CDATA[", at: i) {
                    end = i + 9
                    while end < n, !hasPrefix("]]>", at: end) { end += 1 }
                    end = min(end + 3, n)
                } else {
                    end = i + 1
                    var quote: Unicode.Scalar?
                    while end < n {
                        let c = chars[end]
                        if let q = quote {
                            if c == q { quote = nil }
                        } else if c == "\"" || c == "'" {
                            quote = c
                        } else if c == ">" {
                            break
                        }
                        end += 1
                    }
                    end = min(end + 1, n)
                }
                let tag = chars[i..<end]
                let isClose = tag.count > 1 && tag[tag.startIndex + 1] == "/"
                let isSpecial = tag.count > 1 && (tag[tag.startIndex + 1] == "?" || tag[tag.startIndex + 1] == "!")
                let isSelfClosing = tag.count > 1 && tag[tag.endIndex - 2] == "/"

                if isClose {
                    depth = max(0, depth - 1)
                    if !(inlineText || lastWasOpen) { newline() }
                    lastWasOpen = false
                } else {
                    newline()
                    lastWasOpen = !(isSpecial || isSelfClosing)
                    if lastWasOpen { depth += 1 }
                }
                out.append(contentsOf: tag)
                inlineText = false
                i = end
            } else {
                var end = i
                while end < n, chars[end] != "<" { end += 1 }
                let content = String(String.UnicodeScalarView(chars[i..<end])).trimmingCharacters(in: .whitespacesAndNewlines)
                if !content.isEmpty {
                    if !lastWasOpen { newline() }
                    out.append(contentsOf: content.unicodeScalars)
                    inlineText = lastWasOpen
                }
                lastWasOpen = false
                i = end
            }
        }
        return String(out)
    }

    static func highlightRanges(_ text: String) -> [(NSRange, Token)] {
        let u = Array(text.utf16)
        let n = u.count
        var result: [(NSRange, Token)] = []

        func isNameChar(_ c: UInt16) -> Bool {
            (c >= 0x61 && c <= 0x7A) || (c >= 0x41 && c <= 0x5A) || (c >= 0x30 && c <= 0x39)
                || c == 0x2D || c == 0x5F || c == 0x3A || c == 0x2E || c > 0x7F
        }

        func starts(_ s: [UInt16], at index: Int) -> Bool {
            guard index + s.count <= n else { return false }
            for k in 0..<s.count where u[index + k] != s[k] { return false }
            return true
        }

        let commentOpen = Array("<!--".utf16)
        let commentClose = Array("-->".utf16)
        var i = 0
        while i < n {
            guard u[i] == 0x3C else { i += 1; continue }
            if starts(commentOpen, at: i) {
                var end = i + 4
                while end < n, !starts(commentClose, at: end) { end += 1 }
                end = min(end + 3, n)
                result.append((NSRange(location: i, length: end - i), .comment))
                i = end
                continue
            }
            let start = i
            var j = i + 1
            if j < n, u[j] == 0x2F || u[j] == 0x3F || u[j] == 0x21 { j += 1 }
            while j < n, isNameChar(u[j]) { j += 1 }
            result.append((NSRange(location: start, length: j - start), .tag))
            while j < n {
                let c = u[j]
                if c == 0x3E {
                    result.append((NSRange(location: j, length: 1), .tag))
                    j += 1
                    break
                } else if (c == 0x2F || c == 0x3F), j + 1 < n, u[j + 1] == 0x3E {
                    result.append((NSRange(location: j, length: 2), .tag))
                    j += 2
                    break
                } else if c == 0x22 || c == 0x27 {
                    let valueStart = j
                    j += 1
                    while j < n, u[j] != c { j += 1 }
                    j = min(j + 1, n)
                    result.append((NSRange(location: valueStart, length: j - valueStart), .value))
                } else if isNameChar(c) {
                    let nameStart = j
                    while j < n, isNameChar(u[j]) { j += 1 }
                    result.append((NSRange(location: nameStart, length: j - nameStart), .attribute))
                } else if c == 0x3C {
                    break // unterminated tag; let the outer loop take over
                } else {
                    j += 1
                }
            }
            i = j
        }
        return result
    }
}
