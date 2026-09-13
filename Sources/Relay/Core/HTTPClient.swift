import Foundation
import UniformTypeIdentifiers

struct SendOptions {
    var timeout: TimeInterval = 30
    var followRedirects = true
    var verifySSL = true

    /// From Settings. `object(forKey:)` rather than `bool(forKey:)` so an unset
    /// toggle means its default (on), not false.
    static var current: SendOptions {
        let d = UserDefaults.standard
        var options = SendOptions()
        if let t = d.object(forKey: "timeout") as? Double, t > 0 { options.timeout = t }
        options.followRedirects = d.object(forKey: "followRedirects") as? Bool ?? true
        options.verifySSL = d.object(forKey: "verifySSL") as? Bool ?? true
        return options
    }
}

enum BodyKind { case empty, json, xml, html, image, text, binary }

struct HTTPResult {
    var status: Int
    var headers: [(name: String, value: String)]
    var body: Data
    var duration: TimeInterval
    var ttfb: TimeInterval?
    var redirects: Int
    var finalURL: String
    var date = Date()

    /// What actually went out, for the Request tab.
    var sentMethod: String
    var sentURL: String
    var sentHeaders: [(name: String, value: String)]
    var sentBody: String

    /// Worked out once, off the main thread, so switching tabs never re-parses a large body.
    var kind: BodyKind
    var text: String
    var pretty: String?
    /// The cURL for exactly what was sent, even if the request is edited afterwards.
    var curl = ""

    var statusText: String { HTTPStatus.phrase(status) }
    var contentType: String { header("Content-Type") ?? "" }

    func header(_ name: String) -> String? {
        headers.first { $0.name.caseInsensitiveCompare(name) == .orderedSame }?.value
    }
}

struct HTTPError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

enum HTTPClient {
    /// One session for the app's lifetime: cookies set by a login request carry
    /// to the next request, as in a browser, but nothing is written to disk.
    private static let session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.urlCache = nil
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        config.httpAdditionalHeaders = ["User-Agent": "Relay/1.0"]
        return URLSession(configuration: config)
    }()

    static func send(_ prepared: PreparedRequest, options: SendOptions) async throws -> HTTPResult {
        let request = try urlRequest(for: prepared, timeout: options.timeout)
        let delegate = TaskDelegate(options: options)
        let started = Date()

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request, delegate: delegate)
        } catch let error as URLError {
            if error.code == .cancelled { throw CancellationError() }
            throw HTTPError(message: describe(error, url: request.url, timeout: options.timeout))
        }

        guard let http = response as? HTTPURLResponse else {
            throw HTTPError(message: "The server didn't return an HTTP response.")
        }

        let elapsed = Date().timeIntervalSince(started)
        let metrics = delegate.metrics
        let transaction = metrics?.transactionMetrics.last
        var ttfb: TimeInterval?
        if let start = transaction?.fetchStartDate, let first = transaction?.responseStartDate {
            ttfb = first.timeIntervalSince(start)
        }

        let headers = http.allHeaderFields
            .map { (name: "\($0.key)", value: "\($0.value)") }
            .sorted { $0.name.lowercased() < $1.name.lowercased() }
        let sentHeaders = (request.allHTTPHeaderFields ?? [:])
            .map { (name: $0.key, value: $0.value) }
            .sorted { $0.name.lowercased() < $1.name.lowercased() }

        let status = http.statusCode
        let finalURL = http.url?.absoluteString ?? prepared.url
        let redirects = delegate.redirects
        let duration = metrics?.taskInterval.duration ?? elapsed
        let sentBody = describeBody(prepared.body, bytes: request.httpBody?.count ?? 0)
        let method = request.httpMethod ?? prepared.method
        let sentURL = request.url?.absoluteString ?? prepared.url

        return await Task.detached(priority: .userInitiated) {
            let contentType = headers.first { $0.name.caseInsensitiveCompare("Content-Type") == .orderedSame }?.value ?? ""
            let (kind, text, pretty) = interpret(data, contentType: contentType)
            return HTTPResult(status: status, headers: headers, body: data, duration: duration, ttfb: ttfb,
                              redirects: redirects, finalURL: finalURL,
                              sentMethod: method, sentURL: sentURL, sentHeaders: sentHeaders, sentBody: sentBody,
                              kind: kind, text: text, pretty: pretty)
        }.value
    }

    static func urlRequest(for prepared: PreparedRequest, timeout: TimeInterval) throws -> URLRequest {
        guard !prepared.url.isEmpty else {
            throw HTTPError(message: "Enter a URL to send the request.")
        }
        guard let url = URLQuery.encodedURL(prepared.url),
              let scheme = url.scheme?.lowercased(), scheme == "http" || scheme == "https",
              url.host != nil else {
            throw HTTPError(message: "“\(prepared.url)” isn't a valid http or https URL.")
        }

        var request = URLRequest(url: url, timeoutInterval: timeout)
        request.httpMethod = prepared.method
        for header in prepared.wireHeaders {
            request.addValue(header.value, forHTTPHeaderField: header.name)
        }

        switch prepared.body {
        case .none:
            break
        case .raw(let text):
            request.httpBody = Data(text.utf8)
        case .form(let pairs):
            let encoded = pairs.map { "\(URLQuery.formEncode($0.key))=\(URLQuery.formEncode($0.value))" }
            request.httpBody = Data(encoded.joined(separator: "&").utf8)
        case .multipart(let parts):
            let boundary = "RelayBoundary" + UUID().uuidString.replacingOccurrences(of: "-", with: "")
            request.httpBody = try multipartBody(parts, boundary: boundary)
            // Always ours: a hand-written multipart Content-Type has no boundary to match the body.
            request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        case .file(let path):
            request.httpBody = try readFile(path)
        }
        return request
    }

    private static func multipartBody(_ parts: [KeyValue], boundary: String) throws -> Data {
        func quoted(_ s: String) -> String { s.replacingOccurrences(of: "\"", with: "%22") }
        var data = Data()
        for part in parts {
            data.append(Data("--\(boundary)\r\n".utf8))
            if part.isFile {
                let path = (part.value as NSString).expandingTildeInPath
                let file = try readFile(path)
                let name = (path as NSString).lastPathComponent
                let mime = UTType(filenameExtension: (path as NSString).pathExtension)?.preferredMIMEType ?? "application/octet-stream"
                data.append(Data("Content-Disposition: form-data; name=\"\(quoted(part.key))\"; filename=\"\(quoted(name))\"\r\nContent-Type: \(mime)\r\n\r\n".utf8))
                data.append(file)
            } else {
                data.append(Data("Content-Disposition: form-data; name=\"\(quoted(part.key))\"\r\n\r\n\(part.value)".utf8))
            }
            data.append(Data("\r\n".utf8))
        }
        data.append(Data("--\(boundary)--\r\n".utf8))
        return data
    }

    private static func readFile(_ path: String) throws -> Data {
        let expanded = (path as NSString).expandingTildeInPath
        guard !expanded.isEmpty else { throw HTTPError(message: "Choose a file to send.") }
        guard let data = FileManager.default.contents(atPath: expanded) else {
            throw HTTPError(message: "Couldn't read the file at \(expanded).")
        }
        return data
    }

    private static func describeBody(_ body: PreparedBody, bytes: Int) -> String {
        switch body {
        case .none:             return ""
        case .raw(let text):    return text
        case .form(let pairs):  return pairs.map { "\($0.key)=\($0.value)" }.joined(separator: "&")
        case .multipart(let parts):
            return parts.map { "\($0.key)=\($0.isFile ? "@" + $0.value : $0.value)" }.joined(separator: "\n")
        case .file(let path):   return "@\(path) (\(Format.bytes(bytes)))"
        }
    }

    private static func interpret(_ data: Data, contentType: String) -> (BodyKind, String, String?) {
        let type = contentType.lowercased()
        if data.isEmpty { return (.empty, "", nil) }
        if type.hasPrefix("image/") { return (.image, "", nil) }

        guard let text = String(data: data, encoding: .utf8) ?? String(data: data, encoding: .isoLatin1),
              !looksBinary(data) else {
            return (.binary, "", nil)
        }
        if type.contains("json") || JSONFormat.isJSON(text) {
            return (.json, text, JSONFormat.pretty(text))
        }
        if type.contains("html") { return (.html, text, nil) }
        if type.contains("xml") || text.hasPrefix("<?xml") { return (.xml, text, XMLFormat.pretty(text)) }
        return (.text, text, nil)
    }

    /// NUL bytes in the first few KB mean it isn't text, whatever the header claims.
    private static func looksBinary(_ data: Data) -> Bool {
        data.prefix(4096).contains(0)
    }

    private static func describe(_ error: URLError, url: URL?, timeout: TimeInterval) -> String {
        let host = url?.host ?? "the server"
        switch error.code {
        case .timedOut:
            return "The request timed out after \(Int(timeout)) seconds. You can raise the limit in Settings."
        case .cannotFindHost, .dnsLookupFailed:
            return "Couldn't find \(host). Check the URL and your connection."
        case .cannotConnectToHost:
            return "Couldn't connect to \(host). Is the server running?"
        case .notConnectedToInternet:
            return "You appear to be offline."
        case .networkConnectionLost:
            return "The connection to \(host) was lost."
        case .serverCertificateUntrusted, .serverCertificateHasBadDate, .serverCertificateNotYetValid,
             .serverCertificateHasUnknownRoot, .secureConnectionFailed:
            return "\(host)'s SSL certificate isn't trusted. For local or self-signed servers, turn off certificate verification in Settings."
        case .appTransportSecurityRequiresSecureConnection:
            return "macOS blocked this insecure connection."
        default:
            return error.localizedDescription
        }
    }
}

private final class TaskDelegate: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    let options: SendOptions
    var metrics: URLSessionTaskMetrics?
    var redirects = 0

    init(options: SendOptions) {
        self.options = options
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        if options.followRedirects {
            redirects += 1
            completionHandler(request)
        } else {
            // nil hands back the 3xx itself as the response.
            completionHandler(nil)
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        if !options.verifySSL,
           challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
           let trust = challenge.protectionSpace.serverTrust {
            completionHandler(.useCredential, URLCredential(trust: trust))
        } else {
            completionHandler(.performDefaultHandling, nil)
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didFinishCollecting metrics: URLSessionTaskMetrics) {
        self.metrics = metrics
    }
}

enum HTTPStatus {
    /// `HTTPURLResponse.localizedString(forStatusCode:)` says "no error" for 200, so spell them out.
    static func phrase(_ code: Int) -> String {
        switch code {
        case 100: return "Continue"
        case 101: return "Switching Protocols"
        case 200: return "OK"
        case 201: return "Created"
        case 202: return "Accepted"
        case 203: return "Non-Authoritative Information"
        case 204: return "No Content"
        case 206: return "Partial Content"
        case 301: return "Moved Permanently"
        case 302: return "Found"
        case 303: return "See Other"
        case 304: return "Not Modified"
        case 307: return "Temporary Redirect"
        case 308: return "Permanent Redirect"
        case 400: return "Bad Request"
        case 401: return "Unauthorized"
        case 402: return "Payment Required"
        case 403: return "Forbidden"
        case 404: return "Not Found"
        case 405: return "Method Not Allowed"
        case 406: return "Not Acceptable"
        case 408: return "Request Timeout"
        case 409: return "Conflict"
        case 410: return "Gone"
        case 411: return "Length Required"
        case 412: return "Precondition Failed"
        case 413: return "Payload Too Large"
        case 415: return "Unsupported Media Type"
        case 418: return "I'm a Teapot"
        case 422: return "Unprocessable Entity"
        case 423: return "Locked"
        case 425: return "Too Early"
        case 428: return "Precondition Required"
        case 429: return "Too Many Requests"
        case 431: return "Request Header Fields Too Large"
        case 451: return "Unavailable For Legal Reasons"
        case 500: return "Internal Server Error"
        case 501: return "Not Implemented"
        case 502: return "Bad Gateway"
        case 503: return "Service Unavailable"
        case 504: return "Gateway Timeout"
        case 505: return "HTTP Version Not Supported"
        default:  return ""
        }
    }
}

enum Format {
    static func duration(_ t: TimeInterval) -> String {
        t < 1 ? "\(Int((t * 1000).rounded())) ms" : String(format: "%.2f s", t)
    }

    static func bytes(_ count: Int) -> String {
        if count < 1024 { return "\(count) B" }
        return ByteCountFormatter.string(fromByteCount: Int64(count), countStyle: .file)
    }

    /// The "cURL + response" clipboard text: everything someone needs to
    /// reproduce and read a call, pasted into a ticket or chat.
    static func curlWithResponse(curl: String, result: HTTPResult, includeHeaders: Bool) -> String {
        var out = curl + "\n\n"
        out += "HTTP \(result.status) \(result.statusText)".trimmingCharacters(in: .whitespaces)
        out += "  (\(duration(result.duration)), \(bytes(result.body.count)))\n"
        if includeHeaders {
            out += result.headers.map { "\($0.name): \($0.value)" }.joined(separator: "\n") + "\n"
        }
        let body = result.pretty ?? result.text
        if !body.isEmpty { out += "\n" + body }
        return out
    }
}
