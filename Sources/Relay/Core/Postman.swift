import Foundation

struct ImportResult {
    var collections: [APICollection] = []
    var environments: [APIEnvironment] = []

    var isEmpty: Bool { collections.isEmpty && environments.isEmpty }
}

struct PostmanError: LocalizedError {
    let message: String

    init(_ message: String) { self.message = message }

    var errorDescription: String? { message }
}

/// Reads Postman collections, environments and data dumps; writes Collection v2.1.
///
/// Parsing goes through `JSONSerialization` rather than `Codable`: real
/// exports are loose (a URL is a string or an object, auth params an array or
/// a dict, a description a string or `{content}`), and a single strict type
/// mismatch would otherwise reject a whole collection.
enum Postman {
    static let schema = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"

    // MARK: - Import

    static func importData(_ data: Data) throws -> ImportResult {
        let root: Any
        do {
            root = try JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed])
        } catch {
            throw PostmanError("The file isn't valid JSON.")
        }
        var result = ImportResult()
        try collect(root, into: &result)
        if result.isEmpty {
            throw PostmanError("No Postman collections or environments were found in the file.")
        }
        return result
    }

    private static func collect(_ value: Any, into result: inout ImportResult) throws {
        if let array = value as? [Any] {
            for element in array { try collect(element, into: &result) }
            return
        }
        guard let obj = value as? [String: Any] else {
            throw PostmanError("The file isn't a Postman collection or environment.")
        }

        // Postman data dump, or a Relay workspace backup (same shape).
        if obj["collections"] is [Any] || obj["environments"] is [Any] {
            for c in obj["collections"] as? [Any] ?? [] { try collect(c, into: &result) }
            for e in obj["environments"] as? [Any] ?? [] { try collect(e, into: &result) }
            return
        }
        // Postman API responses wrap the object in a single key.
        if let wrapped = obj["collection"] as? [String: Any] {
            try collect(wrapped, into: &result)
            return
        }
        if let wrapped = obj["environment"] as? [String: Any] {
            try collect(wrapped, into: &result)
            return
        }

        if obj["info"] is [String: Any] || obj["item"] is [Any] {
            result.collections.append(collection(obj))
        } else if obj["values"] is [Any] {
            result.environments.append(environment(obj))
        } else if obj["requests"] is [Any] {
            throw PostmanError("Postman v1 collections aren't supported. Export it again from Postman as Collection v2.1.")
        } else {
            throw PostmanError("The file isn't a Postman collection or environment.")
        }
    }

    static func collection(_ obj: [String: Any]) -> APICollection {
        let info = obj["info"] as? [String: Any] ?? [:]
        var c = APICollection()
        c.name = string(info["name"]) ?? string(obj["name"]) ?? "Imported Collection"
        c.docs = description(info["description"] ?? obj["description"])
        c.variables = variables(obj["variable"])
        c.items = items(obj["item"])

        for (key, value) in obj where !["info", "item", "variable", "auth", "name", "description"].contains(key) {
            c.extras[key] = JSONValue(any: value)
        }
        if let id = info["_postman_id"] { c.extras["_postman_id"] = JSONValue(any: id) }
        c.auth = auth(obj["auth"], missing: .noAuth, extras: &c.extras)
        if c.auth.type == .inherit { c.auth = .noAuth }
        return c
    }

    private static func items(_ value: Any?) -> [CollectionItem] {
        (value as? [Any] ?? []).compactMap { $0 as? [String: Any] }.map(item)
    }

    private static func item(_ obj: [String: Any]) -> CollectionItem {
        if obj["item"] is [Any] {
            var f = Folder()
            f.name = string(obj["name"]) ?? "Folder"
            f.items = items(obj["item"])
            f.docs = description(obj["description"])
            for (key, value) in obj where !["name", "item", "auth", "description"].contains(key) {
                f.extras[key] = JSONValue(any: value)
            }
            f.auth = auth(obj["auth"], missing: .inherit, extras: &f.extras)
            return .folder(f)
        }

        var r = APIRequest()
        r.name = string(obj["name"]) ?? "Request"
        // Item-level keys Relay doesn't model: id, response (saved examples), event (scripts), protocolProfileBehavior.
        for (key, value) in obj where !["name", "request"].contains(key) {
            // Export writes `"response": []` when there are none; skipping it here keeps a round trip stable.
            if key == "response", (value as? [Any])?.isEmpty == true { continue }
            r.extras[key] = JSONValue(any: value)
        }

        let request: [String: Any]
        if let urlString = obj["request"] as? String {
            request = ["url": urlString]
        } else {
            request = obj["request"] as? [String: Any] ?? [:]
        }

        r.method = (string(request["method"]) ?? "GET").uppercased()
        let url = self.url(request["url"])
        r.url = url.url
        r.params = url.params
        if let variable = url.variable { r.extras["urlVariable"] = variable }
        r.headers = headers(request["header"])
        r.body = body(request["body"])
        r.docs = description(request["description"] ?? obj["description"])

        let known: Set<String> = ["method", "url", "header", "body", "auth", "description"]
        let unknown = request.filter { !known.contains($0.key) }
        if !unknown.isEmpty { r.extras["request"] = JSONValue(any: unknown) }
        r.auth = auth(request["auth"], missing: .inherit, extras: &r.extras)
        return .request(r)
    }

    private static func url(_ value: Any?) -> (url: String, params: [KeyValue], variable: JSONValue?) {
        if let s = value as? String {
            return (s, URLQuery.params(fromURL: s, previous: []), nil)
        }
        guard let o = value as? [String: Any] else { return ("", [], nil) }

        var raw = string(o["raw"]) ?? rebuildURL(o)
        let variable = (o["variable"] as? [Any]).flatMap { $0.isEmpty ? nil : JSONValue(any: $0) }

        guard let query = o["query"] as? [Any] else {
            return (raw, URLQuery.params(fromURL: raw, previous: []), variable)
        }
        // The query array is authoritative: it carries disabled params, which `raw` omits.
        let params = query.compactMap { $0 as? [String: Any] }.map { q in
            KeyValue(string(q["key"]) ?? "", string(q["value"]) ?? "",
                     enabled: !bool(q["disabled"]), description: description(q["description"]))
        }
        let rawPairs = URLQuery.pairs(URLQuery.split(raw).query ?? "").map { [$0.key, $0.value] }
        let enabledPairs = params.filter { $0.enabled && !$0.isBlank }.map { [$0.key, $0.value] }
        // Only rewrite when they disagree, so `?a=` isn't needlessly normalised to `?a`.
        if rawPairs != enabledPairs {
            raw = URLQuery.url(raw, applying: params)
        }
        return (raw, params, variable)
    }

    private static func rebuildURL(_ o: [String: Any]) -> String {
        var result = ""
        if let proto = string(o["protocol"]), !proto.isEmpty { result += proto + "://" }
        if let host = o["host"] as? [Any] {
            result += host.compactMap(string).joined(separator: ".")
        } else if let host = string(o["host"]) {
            result += host
        }
        if let port = string(o["port"]), !port.isEmpty { result += ":" + port }
        if let path = o["path"] as? [Any] {
            let segments = path.compactMap { string($0) ?? string(($0 as? [String: Any])?["value"]) }
            if !segments.isEmpty { result += "/" + segments.joined(separator: "/") }
        } else if let path = string(o["path"]), !path.isEmpty {
            result += path.hasPrefix("/") ? path : "/" + path
        }
        return result
    }

    private static func headers(_ value: Any?) -> [KeyValue] {
        if let text = value as? String {
            // v2.0 allowed a raw header block. `//` marks a disabled line in Postman's bulk editor.
            return text.split(whereSeparator: \.isNewline).compactMap { line in
                var line = line.trimmingCharacters(in: .whitespaces)
                let disabled = line.hasPrefix("//")
                if disabled { line = String(line.dropFirst(2)).trimmingCharacters(in: .whitespaces) }
                guard let colon = line.firstIndex(of: ":") else { return nil }
                return KeyValue(line[..<colon].trimmingCharacters(in: .whitespaces),
                                line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces),
                                enabled: !disabled)
            }
        }
        return rows(value)
    }

    private static func rows(_ value: Any?) -> [KeyValue] {
        (value as? [Any] ?? []).compactMap { $0 as? [String: Any] }.map { row in
            KeyValue(string(row["key"]) ?? "", string(row["value"]) ?? "",
                     enabled: !bool(row["disabled"]), description: description(row["description"]))
        }
    }

    private static func body(_ value: Any?) -> RequestBody {
        guard let o = value as? [String: Any], let mode = string(o["mode"]) else { return RequestBody() }
        var b = RequestBody()
        switch mode {
        case "raw":
            b.raw = string(o["raw"]) ?? ""
            let language = ((o["options"] as? [String: Any])?["raw"] as? [String: Any])?["language"] as? String
            switch language?.lowercased() {
            case "json":                      b.mode = .json
            case "xml":                       b.mode = .xml
            case "text", "html", "javascript": b.mode = .text
            default:
                let first = b.raw.trimmingCharacters(in: .whitespacesAndNewlines).first
                b.mode = first == "{" || first == "[" ? .json : first == "<" ? .xml : .text
            }
        case "urlencoded":
            b.mode = .form
            b.form = rows(o["urlencoded"])
        case "formdata":
            b.mode = .multipart
            b.form = (o["formdata"] as? [Any] ?? []).compactMap { $0 as? [String: Any] }.map { row in
                let isFile = string(row["type"]) == "file"
                let src = string(row["src"]) ?? (row["src"] as? [Any])?.compactMap(string).first
                return KeyValue(string(row["key"]) ?? "",
                                isFile ? (src ?? "") : (string(row["value"]) ?? ""),
                                enabled: !bool(row["disabled"]),
                                description: description(row["description"]),
                                isFile: isFile)
            }
        case "file":
            b.mode = .file
            b.filePath = string((o["file"] as? [String: Any])?["src"]) ?? ""
        case "graphql":
            // Relay has no GraphQL editor; the JSON body a GraphQL server receives is the faithful equivalent.
            let graphql = o["graphql"] as? [String: Any] ?? [:]
            var payload: [String: Any] = ["query": string(graphql["query"]) ?? ""]
            if let vars = graphql["variables"] as? String {
                let trimmed = vars.trimmingCharacters(in: .whitespacesAndNewlines)
                if !trimmed.isEmpty {
                    payload["variables"] = (try? JSONSerialization.jsonObject(with: Data(trimmed.utf8), options: [.fragmentsAllowed])) ?? vars
                }
            } else if let vars = graphql["variables"], !(vars is NSNull) {
                payload["variables"] = vars
            }
            b.mode = .json
            if let data = try? JSONSerialization.data(withJSONObject: payload, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) {
                b.raw = String(decoding: data, as: UTF8.self)
            }
        default:
            break
        }
        return b
    }

    /// Auth types Relay can't edit (OAuth 2, Digest, AWS…) import as No Auth
    /// with the original stashed in `extras["auth"]`, so exporting gives it back untouched.
    private static func auth(_ value: Any?, missing: Auth, extras: inout [String: JSONValue]) -> Auth {
        guard let o = value as? [String: Any], let type = string(o["type"]) else { return missing }

        func param(_ name: String) -> String? {
            if let list = o[type] as? [Any] {
                for case let entry as [String: Any] in list where string(entry["key"]) == name {
                    return string(entry["value"])
                }
                return nil
            }
            return string((o[type] as? [String: Any])?[name])
        }

        switch type {
        case "noauth":
            return .noAuth
        case "inherit":
            return .inherit
        case "bearer":
            return Auth(type: .bearer, token: param("token") ?? "")
        case "basic":
            return Auth(type: .basic, username: param("username") ?? "", password: param("password") ?? "")
        case "apikey":
            return Auth(type: .apiKey, key: param("key") ?? "", value: param("value") ?? "",
                        location: param("in") == "query" ? .query : .header)
        default:
            extras["auth"] = JSONValue(any: o)
            return .noAuth
        }
    }

    private static func variables(_ value: Any?) -> [KeyValue] {
        (value as? [Any] ?? []).compactMap { $0 as? [String: Any] }.map { row in
            KeyValue(string(row["key"]) ?? "", string(row["value"]) ?? "",
                     enabled: !bool(row["disabled"]), description: description(row["description"]),
                     isSecret: string(row["type"]) == "secret")
        }
    }

    static func environment(_ obj: [String: Any]) -> APIEnvironment {
        let values = (obj["values"] as? [Any] ?? []).compactMap { $0 as? [String: Any] }.map { row in
            KeyValue(string(row["key"]) ?? "", string(row["value"]) ?? "",
                     enabled: row["enabled"].map { bool($0) } ?? true,
                     isSecret: string(row["type"]) == "secret")
        }
        return APIEnvironment(name: string(obj["name"]) ?? "Imported Environment", variables: values)
    }

    // MARK: - Export

    static func exportCollection(_ c: APICollection) throws -> Data {
        try serialize(collectionObject(c))
    }

    static func exportEnvironment(_ e: APIEnvironment) throws -> Data {
        try serialize(environmentObject(e))
    }

    static func exportBackup(collections: [APICollection], environments: [APIEnvironment]) throws -> Data {
        try serialize([
            "version": 1,
            "collections": collections.map(collectionObject),
            "environments": environments.map(environmentObject),
        ])
    }

    static func collectionObject(_ c: APICollection) -> [String: Any] {
        var o = merged(c.extras, excluding: ["_postman_id", "auth"])
        var info: [String: Any] = [
            "name": c.name,
            "schema": schema,
            "_postman_id": c.extras["_postman_id"]?.anyValue ?? c.id.uuidString.lowercased(),
        ]
        if !c.docs.isEmpty { info["description"] = c.docs }
        o["info"] = info
        o["item"] = c.items.map(itemObject)
        if !c.variables.isEmpty { o["variable"] = c.variables.map(variableObject) }
        o["auth"] = authObject(c.auth, extras: c.extras) ?? ["type": "noauth"]
        return o
    }

    private static func itemObject(_ item: CollectionItem) -> [String: Any] {
        switch item {
        case .folder(let f):
            var o = merged(f.extras, excluding: ["auth"])
            o["name"] = f.name
            o["item"] = f.items.map(itemObject)
            if !f.docs.isEmpty { o["description"] = f.docs }
            if let auth = authObject(f.auth, extras: f.extras) { o["auth"] = auth }
            return o

        case .request(let r):
            var o = merged(r.extras, excluding: ["auth", "request", "urlVariable"])
            o["name"] = r.name
            var request = r.extras["request"]?.anyValue as? [String: Any] ?? [:]
            request["method"] = r.method
            request["header"] = r.headers.map { rowObject($0) }
            var url = urlObject(r.url, params: r.params)
            if let variable = r.extras["urlVariable"] { url["variable"] = variable.anyValue }
            request["url"] = url
            if let body = bodyObject(r.body) { request["body"] = body }
            if let auth = authObject(r.auth, extras: r.extras) { request["auth"] = auth }
            if !r.docs.isEmpty { request["description"] = r.docs }
            o["request"] = request
            if o["response"] == nil { o["response"] = [Any]() }
            return o
        }
    }

    /// Postman's structured URL. `raw` is what Postman actually shows and
    /// sends; the parts exist for its own tooling, so they're best-effort.
    static func urlObject(_ raw: String, params: [KeyValue]) -> [String: Any] {
        var o: [String: Any] = ["raw": raw]
        var rest = URLQuery.split(raw).base

        if let scheme = rest.range(of: "://") {
            o["protocol"] = String(rest[..<scheme.lowerBound])
            rest = String(rest[scheme.upperBound...])
        }

        let hostAndPath = splitOutsideBraces(rest, on: "/", maxSplits: 1)
        var hostPort = hostAndPath[0]
        let path = hostAndPath.count > 1 ? hostAndPath[1] : nil
        if let colon = hostPort.lastIndex(of: ":") {
            let port = hostPort[hostPort.index(after: colon)...]
            if !port.isEmpty, port.allSatisfy(\.isNumber) {
                o["port"] = String(port)
                hostPort = String(hostPort[..<colon])
            }
        }
        if !hostPort.isEmpty { o["host"] = splitOutsideBraces(hostPort, on: ".") }
        if let path { o["path"] = splitOutsideBraces(path, on: "/") }
        if !params.isEmpty { o["query"] = params.map { rowObject($0) } }
        return o
    }

    private static func bodyObject(_ b: RequestBody) -> [String: Any]? {
        switch b.mode {
        case .none:
            return nil
        case .json, .text, .xml:
            let language = b.mode == .json ? "json" : b.mode == .xml ? "xml" : "text"
            return ["mode": "raw", "raw": b.raw, "options": ["raw": ["language": language]]]
        case .form:
            return ["mode": "urlencoded", "urlencoded": b.form.map { rowObject($0, type: "text") }]
        case .multipart:
            return ["mode": "formdata", "formdata": b.form.map { row -> [String: Any] in
                guard row.isFile else { return rowObject(row, type: "text") }
                var o = rowObject(row, type: "file")
                o["value"] = nil
                o["src"] = row.value
                return o
            }]
        case .file:
            return ["mode": "file", "file": ["src": b.filePath]]
        }
    }

    /// `nil` means "leave the key out", which Postman reads as inherit.
    private static func authObject(_ auth: Auth, extras: [String: JSONValue]) -> [String: Any]? {
        func params(_ pairs: [(String, String)]) -> [[String: Any]] {
            pairs.map { ["key": $0.0, "value": $0.1, "type": "string"] }
        }
        switch auth.type {
        case .inherit:
            return nil
        case .none:
            return extras["auth"]?.anyValue as? [String: Any] ?? ["type": "noauth"]
        case .bearer:
            return ["type": "bearer", "bearer": params([("token", auth.token)])]
        case .basic:
            return ["type": "basic", "basic": params([("username", auth.username), ("password", auth.password)])]
        case .apiKey:
            return ["type": "apikey", "apikey": params([("key", auth.key), ("value", auth.value),
                                                         ("in", auth.location.rawValue)])]
        }
    }

    private static func rowObject(_ row: KeyValue, type: String = "text") -> [String: Any] {
        var o: [String: Any] = ["key": row.key, "value": row.value, "type": type]
        if !row.enabled { o["disabled"] = true }
        if !row.description.isEmpty { o["description"] = row.description }
        return o
    }

    private static func variableObject(_ row: KeyValue) -> [String: Any] {
        rowObject(row, type: row.isSecret ? "secret" : "string")
    }

    static func environmentObject(_ e: APIEnvironment) -> [String: Any] {
        [
            "id": e.id.uuidString.lowercased(),
            "name": e.name,
            "values": e.variables.map { row -> [String: Any] in
                ["key": row.key, "value": row.value, "type": row.isSecret ? "secret" : "default", "enabled": row.enabled]
            },
            "_postman_variable_scope": "environment",
        ]
    }

    private static func serialize(_ object: Any) throws -> Data {
        guard JSONSerialization.isValidJSONObject(object) else {
            throw PostmanError("The collection contains data that can't be written as JSON.")
        }
        return try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .withoutEscapingSlashes])
    }

    // MARK: - Helpers

    private static func merged(_ extras: [String: JSONValue], excluding: Set<String>) -> [String: Any] {
        var o: [String: Any] = [:]
        for (key, value) in extras where !excluding.contains(key) { o[key] = value.anyValue }
        return o
    }

    /// Strings as-is; numbers and booleans as they'd be typed. Postman exports
    /// `"value": 3000` as readily as `"value": "3000"`.
    private static func string(_ value: Any?) -> String? {
        switch value {
        case let s as String:
            return s
        case let n as NSNumber:
            if CFGetTypeID(n) == CFBooleanGetTypeID() { return n.boolValue ? "true" : "false" }
            let d = n.doubleValue
            return d.rounded() == d && abs(d) < 1e15 ? String(Int64(d)) : String(d)
        default:
            return nil
        }
    }

    private static func bool(_ value: Any?) -> Bool {
        switch value {
        case let b as Bool:   return b
        case let s as String: return s.lowercased() == "true"
        default:              return false
        }
    }

    private static func description(_ value: Any?) -> String {
        string(value) ?? string((value as? [String: Any])?["content"]) ?? ""
    }

    /// Splits on `separator`, but never inside `{{…}}`: `{{api.host}}` is one host part.
    private static func splitOutsideBraces(_ s: String, on separator: Character, maxSplits: Int = .max) -> [String] {
        let chars = Array(s)
        var parts: [String] = []
        var current = ""
        var depth = 0
        var i = 0
        while i < chars.count {
            if i + 1 < chars.count, chars[i] == "{", chars[i + 1] == "{" {
                depth += 1
                current += "{{"
                i += 2
            } else if i + 1 < chars.count, chars[i] == "}", chars[i + 1] == "}", depth > 0 {
                depth -= 1
                current += "}}"
                i += 2
            } else if depth == 0, chars[i] == separator, parts.count < maxSplits {
                parts.append(current)
                current = ""
                i += 1
            } else {
                current.append(chars[i])
                i += 1
            }
        }
        parts.append(current)
        return parts
    }
}
