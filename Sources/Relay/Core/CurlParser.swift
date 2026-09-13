import Foundation

struct CurlParseError: LocalizedError {
    let message: String

    var errorDescription: String? { message }
}

/// Turns a pasted cURL command into a request.
///
/// Written against what people actually paste: Chrome/Firefox "Copy as cURL"
/// (`$'…'` quoting, `--data-raw`), Postman (`--location`), terminals (`\`
/// continuations, `-sSL`) and Windows cmd (`^` continuations, `curl.exe`).
/// Flags that don't change the request (`--compressed`, `-o`) are skipped,
/// with their values, so they can't be mistaken for the URL.
enum CurlParser {
    private static let flagsWithValue: Set<String> = [
        "-X", "--request", "-H", "--header", "-d", "--data", "--data-raw", "--data-binary",
        "--data-ascii", "--data-urlencode", "--json", "-u", "--user", "-F", "--form",
        "--form-string", "--url", "-A", "--user-agent", "-b", "--cookie", "-e", "--referer",
        "-o", "--output", "-m", "--max-time", "--connect-timeout", "-x", "--proxy", "-w",
        "--write-out", "--retry", "-c", "--cookie-jar", "-T", "--upload-file", "--cacert",
        "--cert", "--key", "-r", "--range", "-z", "--time-cond", "--resolve", "--interface",
    ]

    static func parse(_ input: String) throws -> APIRequest {
        var source = input.trimmingCharacters(in: .whitespacesAndNewlines)
        if source.hasPrefix("$ ") { source.removeFirst(2) }
        let tokens = try tokenize(source)
        guard let first = tokens.first?.lowercased(), first == "curl" || first == "curl.exe" else {
            throw CurlParseError(message: "Command must start with \"curl\"")
        }

        var method: String?
        var url: String?
        var headers: [KeyValue] = []
        var dataParts: [String] = []
        var formFields: [KeyValue] = []
        var forceGet = false
        var head = false
        var isJsonFlag = false
        var auth = Auth.inherit

        var i = 1
        while i < tokens.count {
            defer { i += 1 }
            var token = tokens[i]
            var inlineValue: String?

            // --flag=value
            if token.hasPrefix("--"), let eq = token.firstIndex(of: "=") {
                let name = String(token[..<eq])
                if flagsWithValue.contains(name) {
                    inlineValue = String(token[token.index(after: eq)...])
                    token = name
                }
            }
            // -XPOST, -HAccept:x, or combined boolean flags like -sSLk.
            if inlineValue == nil, token.count > 2, token.hasPrefix("-"), !token.hasPrefix("--") {
                let short = String(token.prefix(2))
                if flagsWithValue.contains(short) {
                    inlineValue = String(token.dropFirst(2))
                    token = short
                } else {
                    let letters = token.dropFirst()
                    if letters.contains("G") { forceGet = true }
                    if letters.contains("I") { head = true }
                    continue
                }
            }

            func next() throws -> String {
                if let inlineValue { return inlineValue }
                guard i + 1 < tokens.count else { throw CurlParseError(message: "Missing value for \(token)") }
                i += 1
                return tokens[i]
            }

            switch token {
            case "-X", "--request":
                method = try next().uppercased()
            case "-H", "--header":
                let header = try next()
                guard let colon = header.firstIndex(of: ":"), colon != header.startIndex else { break }
                headers.append(KeyValue(
                    header[..<colon].trimmingCharacters(in: .whitespaces),
                    header[header.index(after: colon)...].trimmingCharacters(in: .whitespaces)))
            case "-d", "--data", "--data-raw", "--data-binary", "--data-ascii":
                dataParts.append(try next())
            case "--data-urlencode":
                dataParts.append(urlencodeData(try next()))
            case "--json":
                dataParts.append(try next())
                isJsonFlag = true
            case "-F", "--form", "--form-string":
                formFields.append(formField(try next(), allowFiles: token != "--form-string"))
            case "-u", "--user":
                let creds = try next()
                let parts = creds.split(separator: ":", maxSplits: 1, omittingEmptySubsequences: false)
                auth = Auth(type: .basic, username: String(parts[0]), password: parts.count > 1 ? String(parts[1]) : "")
            case "-A", "--user-agent":
                headers.append(KeyValue("User-Agent", try next()))
            case "-b", "--cookie":
                headers.append(KeyValue("Cookie", try next()))
            case "-e", "--referer":
                headers.append(KeyValue("Referer", try next()))
            case "--url":
                url = try next()
            case "-G", "--get":
                forceGet = true
            case "-I", "--head":
                head = true
            default:
                if flagsWithValue.contains(token) {
                    _ = try next()
                } else if !token.hasPrefix("-"), url == nil {
                    url = token
                }
            }
        }

        guard var url, !url.isEmpty else { throw CurlParseError(message: "No URL found in cURL command") }

        // Credentials belong in the Auth tab, where they can be inherited and edited.
        headers.removeAll { header in
            guard header.key.lowercased() == "authorization" else { return false }
            let value = header.value
            if value.lowercased().hasPrefix("bearer ") {
                auth = Auth(type: .bearer, token: value.dropFirst(7).trimmingCharacters(in: .whitespaces))
                return true
            }
            if value.lowercased().hasPrefix("basic "),
               let data = Data(base64Encoded: value.dropFirst(6).trimmingCharacters(in: .whitespaces)),
               let decoded = String(data: data, encoding: .utf8) {
                let parts = decoded.split(separator: ":", maxSplits: 1, omittingEmptySubsequences: false)
                auth = Auth(type: .basic, username: String(parts[0]), password: parts.count > 1 ? String(parts[1]) : "")
                return true
            }
            return false
        }

        if isJsonFlag {
            if headerValue(headers, "content-type") == nil { headers.append(KeyValue("Content-Type", "application/json")) }
            if headerValue(headers, "accept") == nil { headers.append(KeyValue("Accept", "application/json")) }
        }

        var body = RequestBody()
        let data = dataParts.joined(separator: "&")

        if forceGet && !dataParts.isEmpty {
            url += (url.contains("?") ? "&" : "?") + data
        } else if !formFields.isEmpty {
            body.mode = .multipart
            body.form = formFields
            headers.removeAll { $0.key.lowercased() == "content-type" && $0.value.lowercased().contains("multipart") }
        } else if !dataParts.isEmpty {
            let contentType = headerValue(headers, "content-type")?.lowercased() ?? ""
            if contentType.contains("json") || looksLikeJSON(data) {
                body.mode = .json
                body.raw = prettyJSON(data)
            } else if contentType.contains("xml") {
                body.mode = .xml
                body.raw = data
            } else if contentType.contains("x-www-form-urlencoded") || (contentType.isEmpty && looksLikeForm(data)) {
                body.mode = .form
                body.form = parseForm(data)
            } else {
                body.mode = .text
                body.raw = data
            }
        }

        // The body mode implies its Content-Type; keep the header only when it says something more.
        if let implied = body.mode.contentType {
            headers.removeAll {
                $0.key.lowercased() == "content-type"
                    && $0.value.lowercased().split(separator: ";").first?.trimmingCharacters(in: .whitespaces) == implied
            }
        }

        let resolvedMethod = method ?? (head ? "HEAD" : (body.mode != .none && !forceGet) ? "POST" : "GET")

        var request = APIRequest()
        request.name = requestName(for: url)
        request.method = resolvedMethod
        request.url = url
        request.params = URLQuery.params(fromURL: url, previous: [])
        request.headers = headers
        request.body = body
        request.auth = auth
        return request
    }

    /// Shell-like tokenizer supporting '…', "…", $'…', backslash escapes and
    /// line continuations (`\` in bash, `^` in Windows cmd).
    static func tokenize(_ input: String) throws -> [String] {
        let joined = input
            .replacingOccurrences(of: #"\\\r?\n"#, with: " ", options: .regularExpression)
            .replacingOccurrences(of: #"\^\r?\n"#, with: " ", options: .regularExpression)
        let src = Array(joined)
        var tokens: [String] = []
        var buf = ""
        var inToken = false
        var i = 0

        while i < src.count {
            let c = src[i]
            if c.isWhitespace {
                if inToken {
                    tokens.append(buf)
                    buf = ""
                    inToken = false
                }
                i += 1
            } else if c == "'" {
                inToken = true
                guard let end = src[(i + 1)...].firstIndex(of: "'") else {
                    throw CurlParseError(message: "Unterminated single quote")
                }
                buf += String(src[(i + 1)..<end])
                i = end + 1
            } else if c == "$", i + 1 < src.count, src[i + 1] == "'" {
                inToken = true
                i += 2
                while i < src.count, src[i] != "'" {
                    if src[i] == "\\", i + 1 < src.count {
                        switch src[i + 1] {
                        case "n": buf += "\n"
                        case "t": buf += "\t"
                        case "r": buf += "\r"
                        case "u" where i + 5 < src.count:
                            buf += scalar(String(src[(i + 2)..<(i + 6)]))
                            i += 4
                        case "x" where i + 3 < src.count:
                            buf += scalar(String(src[(i + 2)..<(i + 4)]))
                            i += 2
                        default:
                            buf.append(src[i + 1])
                        }
                        i += 2
                    } else {
                        buf.append(src[i])
                        i += 1
                    }
                }
                guard i < src.count else { throw CurlParseError(message: "Unterminated quote") }
                i += 1
            } else if c == "\"" {
                inToken = true
                i += 1
                while i < src.count, src[i] != "\"" {
                    if src[i] == "\\", i + 1 < src.count, "\"\\$`".contains(src[i + 1]) {
                        buf.append(src[i + 1])
                        i += 2
                    } else {
                        buf.append(src[i])
                        i += 1
                    }
                }
                guard i < src.count else { throw CurlParseError(message: "Unterminated double quote") }
                i += 1
            } else if c == "\\", i + 1 < src.count {
                inToken = true
                buf.append(src[i + 1])
                i += 2
            } else {
                inToken = true
                buf.append(c)
                i += 1
            }
        }
        if inToken { tokens.append(buf) }
        return tokens
    }

    // MARK: - Helpers

    private static func scalar(_ hex: String) -> String {
        UInt32(hex, radix: 16).flatMap(Unicode.Scalar.init).map { String(Character($0)) } ?? "?"
    }

    private static func headerValue(_ headers: [KeyValue], _ name: String) -> String? {
        headers.first { $0.key.lowercased() == name }?.value
    }

    /// `--data-urlencode` sends its content encoded; encoding here means the
    /// form parser's decode gives back exactly what was typed.
    private static func urlencodeData(_ value: String) -> String {
        guard let eq = value.firstIndex(of: "=") else { return URLQuery.formEncode(value) }
        let name = value[..<eq]
        let content = URLQuery.formEncode(String(value[value.index(after: eq)...]))
        return name.isEmpty ? content : "\(name)=\(content)"
    }

    private static func formField(_ field: String, allowFiles: Bool) -> KeyValue {
        guard let eq = field.firstIndex(of: "=") else { return KeyValue(field) }
        let key = String(field[..<eq])
        var value = String(field[field.index(after: eq)...])
        if allowFiles, value.hasPrefix("@") {
            value.removeFirst()
            for marker in [";type=", ";filename="] {
                if let range = value.range(of: marker) { value = String(value[..<range.lowerBound]) }
            }
            return KeyValue(key, stripQuotes(value), isFile: true)
        }
        return KeyValue(key, stripQuotes(value))
    }

    private static func stripQuotes(_ v: String) -> String {
        v.count >= 2 && v.hasPrefix("\"") && v.hasSuffix("\"") ? String(v.dropFirst().dropLast()) : v
    }

    private static func looksLikeJSON(_ s: String) -> Bool {
        let t = s.trimmingCharacters(in: .whitespacesAndNewlines)
        guard t.hasPrefix("{") || t.hasPrefix("[") else { return false }
        return (try? JSONSerialization.jsonObject(with: Data(t.utf8))) != nil
    }

    private static func looksLikeForm(_ s: String) -> Bool {
        s.trimmingCharacters(in: .whitespacesAndNewlines)
            .range(of: #"^[^=&\s]+=[^&\s]*(&[^=&\s]+=[^&\s]*)*$"#, options: .regularExpression) != nil
    }

    private static func parseForm(_ s: String) -> [KeyValue] {
        func decode(_ v: Substring) -> String {
            let spaced = v.replacingOccurrences(of: "+", with: " ")
            return spaced.removingPercentEncoding ?? spaced
        }
        return s.split(separator: "&").map { part in
            guard let eq = part.firstIndex(of: "=") else { return KeyValue(decode(part)) }
            return KeyValue(decode(part[..<eq]), decode(part[part.index(after: eq)...]))
        }
    }

    /// Already-formatted bodies are left alone; one-liners are reindented.
    /// Not `JSONSerialization` — it reorders keys, and key order is part of
    /// how people read an API.
    private static func prettyJSON(_ s: String) -> String {
        let t = s.trimmingCharacters(in: .whitespacesAndNewlines)
        guard looksLikeJSON(t), !t.contains("\n") else { return s }
        return reindent(t)
    }

    private static func reindent(_ json: String) -> String {
        let chars = Array(json)
        var out = ""
        var depth = 0
        var inString = false
        var escaped = false
        func newline() { out += "\n" + String(repeating: "  ", count: max(depth, 0)) }

        var i = 0
        while i < chars.count {
            let c = chars[i]
            defer { i += 1 }
            if inString {
                out.append(c)
                if escaped { escaped = false }
                else if c == "\\" { escaped = true }
                else if c == "\"" { inString = false }
                continue
            }
            switch c {
            case "\"":
                inString = true
                out.append(c)
            case "{", "[":
                // Keep empty containers on one line.
                var j = i + 1
                while j < chars.count, chars[j].isWhitespace { j += 1 }
                if j < chars.count, chars[j] == (c == "{" ? "}" : "]") {
                    out += c == "{" ? "{}" : "[]"
                    i = j
                } else {
                    out.append(c)
                    depth += 1
                    newline()
                }
            case "}", "]":
                depth -= 1
                newline()
                out.append(c)
            case ",":
                out.append(c)
                newline()
            case ":":
                out += ": "
            default:
                if !c.isWhitespace { out.append(c) }
            }
        }
        return out
    }

    /// "api.example.com/v1/users" — host and path, for a request that has no
    /// name of its own. The method is left out because a badge beside the
    /// name already shows it; the scheme, query and fragment are noise.
    static func requestName(for url: String) -> String {
        var rest = URLQuery.split(url.trimmingCharacters(in: .whitespaces)).base
        if let scheme = rest.range(of: "://") { rest = String(rest[scheme.upperBound...]) }
        // Never put credentials from user:pass@host into a name shown on screen.
        let authorityEnd = rest.firstIndex(of: "/") ?? rest.endIndex
        if let at = rest[..<authorityEnd].lastIndex(of: "@") {
            rest = String(rest[rest.index(after: at)...])
        }
        while rest.count > 1, rest.hasSuffix("/") { rest.removeLast() }
        guard !rest.isEmpty else { return APIRequest().name }
        return rest.count > 80 ? String(rest.prefix(79)) + "…" : rest
    }
}
