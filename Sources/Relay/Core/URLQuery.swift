import Foundation

/// Keeps the URL's query string and the Params table in step.
///
/// Both work on the text as typed — nothing is decoded — so `{{vars}}` and
/// existing escapes survive a round trip. Encoding happens once, at send time.
enum URLQuery {
    static func split(_ url: String) -> (base: String, query: String?, fragment: String?) {
        var rest = url
        var fragment: String?
        if let hash = rest.firstIndex(of: "#") {
            fragment = String(rest[rest.index(after: hash)...])
            rest = String(rest[..<hash])
        }
        guard let q = rest.firstIndex(of: "?") else { return (rest, nil, fragment) }
        return (String(rest[..<q]), String(rest[rest.index(after: q)...]), fragment)
    }

    static func pairs(_ query: String) -> [(key: String, value: String)] {
        query.split(separator: "&").map { part in
            guard let eq = part.firstIndex(of: "=") else { return (String(part), "") }
            return (String(part[..<eq]), String(part[part.index(after: eq)...]))
        }
    }

    /// The Params table after the URL was edited. Enabled rows take the
    /// query's pairs in order and keep their ids (so the table doesn't
    /// flicker); disabled rows aren't in the URL and stay where they were.
    static func params(fromURL url: String, previous: [KeyValue]) -> [KeyValue] {
        var parsed = pairs(split(url).query ?? "")[...]
        var rows: [KeyValue] = []
        for old in previous {
            if !old.enabled {
                rows.append(old)
                continue
            }
            guard let pair = parsed.popFirst() else { continue }
            var row = old
            row.key = pair.key
            row.value = pair.value
            rows.append(row)
        }
        rows += parsed.map { KeyValue($0.key, $0.value) }
        return rows
    }

    /// The URL after the Params table was edited.
    static func url(_ url: String, applying params: [KeyValue]) -> String {
        let parts = split(url)
        let query = params
            .filter { $0.enabled && !$0.isBlank }
            .map { $0.value.isEmpty ? $0.key : "\($0.key)=\($0.value)" }
            .joined(separator: "&")
        var result = parts.base
        if !query.isEmpty { result += "?" + query }
        if let fragment = parts.fragment { result += "#" + fragment }
        return result
    }

    static func appending(_ pairs: [(key: String, value: String)], to url: String) -> String {
        guard !pairs.isEmpty else { return url }
        let parts = split(url)
        let added = pairs.map { "\(formEncode($0.key))=\(formEncode($0.value))" }.joined(separator: "&")
        var result = parts.base + "?" + [parts.query, added].compactMap { $0?.isEmpty == false ? $0 : nil }.joined(separator: "&")
        if let fragment = parts.fragment { result += "#" + fragment }
        return result
    }

    /// Percent-encodes what URLSession would reject (spaces, unicode, braces)
    /// and leaves existing `%XX` escapes alone.
    static func encodedURL(_ string: String) -> URL? {
        var allowed = CharacterSet.urlQueryAllowed
        allowed.insert(charactersIn: "%#[]")
        let encoded = string.addingPercentEncoding(withAllowedCharacters: allowed) ?? string
        guard let url = URL(string: encoded), url.scheme != nil else { return nil }
        return url
    }

    /// `application/x-www-form-urlencoded` encoding.
    static func formEncode(_ s: String) -> String {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._* ")
        return (s.addingPercentEncoding(withAllowedCharacters: allowed) ?? s).replacingOccurrences(of: " ", with: "+")
    }
}
