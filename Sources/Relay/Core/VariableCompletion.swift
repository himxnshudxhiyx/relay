import Foundation

/// A `{{name` being typed: the partial name between `{{` and the cursor.
struct CompletionContext: Equatable {
    /// The typed part of the name, after `{{` (UTF-16, for NSTextView).
    var range: NSRange
    var prefix: String

    /// Names are short, so the scan back from the cursor stops well before
    /// it would crawl through a large body looking for a brace.
    private static let maxNameLength = 120

    static func find(in text: String, cursor: Int) -> CompletionContext? {
        let ns = text as NSString
        guard cursor >= 2, cursor <= ns.length else { return nil }
        var start = cursor
        let limit = max(0, cursor - maxNameLength)
        while start > limit, !breaksName(ns.character(at: start - 1)) {
            start -= 1
        }
        let brace: unichar = 0x7B
        guard start >= 2, ns.character(at: start - 1) == brace, ns.character(at: start - 2) == brace else { return nil }
        let range = NSRange(location: start, length: cursor - start)
        return CompletionContext(range: range, prefix: ns.substring(with: range))
    }

    /// The range a picked name replaces: the typed part plus the rest of the
    /// name after the cursor, so picking inside `{{bas|eUrl}}` swaps the whole
    /// name. `closed` says whether `}}` already follows.
    func replacement(in text: String) -> (range: NSRange, closed: Bool) {
        let ns = text as NSString
        var end = min(NSMaxRange(range), ns.length)
        while end < ns.length, !Self.breaksName(ns.character(at: end)) { end += 1 }
        let closed = end + 2 <= ns.length && ns.substring(with: NSRange(location: end, length: 2)) == "}}"
        return (NSRange(location: range.location, length: end - range.location), closed)
    }

    /// Braces, whitespace and quotes can't be part of a name, so they end the scan.
    private static func breaksName(_ c: unichar) -> Bool {
        switch c {
        case 0x7B, 0x7D, 0x20, 0x09, 0x0A, 0x0D, 0x22, 0x27: return true
        default: return false
        }
    }
}

struct VariableSuggestion: Identifiable, Hashable {
    var name: String
    var value: String
    var source: VariableScope.Source
    var isSecret = false

    var id: String { name }
}

extension VariableScope {
    static let dynamicNames = ["$guid", "$randomUUID", "$timestamp", "$isoTimestamp", "$randomInt"]

    /// Variables to offer for `prefix`: environment first (it wins a name
    /// clash, so its row is the one shown), then the collection's. Built-in
    /// `$` variables only once a `$` is typed, so they don't bury real ones.
    func suggestions(matching prefix: String) -> [VariableSuggestion] {
        var seen = Set<String>()
        var all: [VariableSuggestion] = []
        for row in environment where row.enabled && !row.key.isEmpty && seen.insert(row.key).inserted {
            all.append(VariableSuggestion(name: row.key, value: row.value, source: .environment, isSecret: row.isSecret))
        }
        for row in collection where row.enabled && !row.key.isEmpty && seen.insert(row.key).inserted {
            all.append(VariableSuggestion(name: row.key, value: row.value, source: .collection, isSecret: row.isSecret))
        }
        if prefix.hasPrefix("$") {
            all += Self.dynamicNames.map { VariableSuggestion(name: $0, value: "", source: .dynamic) }
        }
        guard !prefix.isEmpty else { return all }

        let lower = prefix.lowercased()
        let starting = all.filter { $0.name.lowercased().hasPrefix(lower) }
        let containing = all.filter { !$0.name.lowercased().hasPrefix(lower) && $0.name.lowercased().contains(lower) }
        return starting + containing
    }
}
