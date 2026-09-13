import Foundation

/// `{{name}}` substitution, Postman style.
enum Variables {
    private static let pattern = try! NSRegularExpression(pattern: #"\{\{\s*([^{}\s]+)\s*\}\}"#)

    /// Names referenced in `text`, in order of first appearance.
    static func names(in text: String) -> [String] {
        guard text.contains("{{") else { return [] }
        let ns = text as NSString
        var seen = Set<String>()
        return pattern.matches(in: text, range: NSRange(location: 0, length: ns.length)).compactMap { match in
            let name = ns.substring(with: match.range(at: 1))
            return seen.insert(name).inserted ? name : nil
        }
    }

    /// Ranges of every `{{…}}` in `text`, for highlighting.
    static func ranges(in text: String) -> [(range: NSRange, name: String)] {
        guard text.contains("{{") else { return [] }
        let ns = text as NSString
        return pattern.matches(in: text, range: NSRange(location: 0, length: ns.length)).map {
            ($0.range, ns.substring(with: $0.range(at: 1)))
        }
    }

    /// Replaces known variables and leaves unknown ones as written, so a typo
    /// shows up in the request rather than silently becoming an empty string.
    static func resolve(_ text: String, _ values: [String: String]) -> String {
        guard text.contains("{{") else { return text }
        var result = text
        // A value may itself reference a variable ({{baseUrl}} = {{host}}/v1).
        // A few passes covers real use without looping on a self-reference.
        for _ in 0..<4 {
            let next = substitute(result, values)
            if next == result { break }
            result = next
        }
        return result
    }

    private static func substitute(_ text: String, _ values: [String: String]) -> String {
        let ns = text as NSString
        let matches = pattern.matches(in: text, range: NSRange(location: 0, length: ns.length))
        guard !matches.isEmpty else { return text }
        let out = NSMutableString(string: text)
        for match in matches.reversed() {
            let name = ns.substring(with: match.range(at: 1))
            if let value = values[name] ?? dynamic(name) {
                out.replaceCharacters(in: match.range, with: value)
            }
        }
        return out as String
    }

    /// Postman's built-in dynamic variables that people actually use.
    static func dynamic(_ name: String) -> String? {
        switch name {
        case "$guid", "$randomUUID": return UUID().uuidString.lowercased()
        case "$timestamp":           return String(Int(Date().timeIntervalSince1970))
        case "$isoTimestamp":        return ISO8601DateFormatter().string(from: Date())
        case "$randomInt":           return String(Int.random(in: 0...1000))
        default:                     return nil
        }
    }
}

/// The variables visible to a request: its collection's, overridden by the
/// active environment's (Postman's precedence).
struct VariableScope {
    var collection: [KeyValue] = []
    var environment: [KeyValue] = []

    var values: [String: String] {
        var result: [String: String] = [:]
        for kv in collection where kv.enabled && !kv.key.isEmpty { result[kv.key] = kv.value }
        for kv in environment where kv.enabled && !kv.key.isEmpty { result[kv.key] = kv.value }
        return result
    }

    enum Source { case environment, collection, dynamic, missing }

    func source(of name: String) -> Source {
        if environment.contains(where: { $0.enabled && $0.key == name }) { return .environment }
        if collection.contains(where: { $0.enabled && $0.key == name }) { return .collection }
        if name.hasPrefix("$"), Variables.dynamic(name) != nil { return .dynamic }
        return .missing
    }
}
