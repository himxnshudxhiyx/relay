import Foundation

enum PreparedBody {
    case none
    case raw(String)
    case form([(key: String, value: String)])
    /// Resolved rows; `isFile` rows hold a file path in `value`.
    case multipart([KeyValue])
    case file(path: String)
}

/// A request with variables substituted, auth applied and disabled rows
/// dropped — what the HTTP client sends and what "Copy as cURL" prints.
struct PreparedRequest {
    var method: String
    var url: String
    var headers: [(name: String, value: String)]
    var body: PreparedBody
    /// Kept apart from `headers` so cURL can print `--user` instead of an opaque base64 header.
    var basicAuth: (username: String, password: String)?

    func header(_ name: String) -> String? {
        headers.first { $0.name.caseInsensitiveCompare(name) == .orderedSame }?.value
    }

    /// Headers as sent on the wire, including basic auth.
    var wireHeaders: [(name: String, value: String)] {
        guard let basic = basicAuth, header("Authorization") == nil else { return headers }
        let token = Data("\(basic.username):\(basic.password)".utf8).base64EncodedString()
        return headers + [("Authorization", "Basic \(token)")]
    }
}

enum RequestPreparer {
    /// - Parameters:
    ///   - auth: the request's effective auth, with `.inherit` already resolved.
    ///   - variables: values to substitute. `nil` leaves `{{placeholders}}` as
    ///     written, for sharing a cURL that works in someone else's environment.
    static func prepare(_ request: APIRequest, auth: Auth, variables: [String: String]?) -> PreparedRequest {
        func r(_ s: String) -> String { variables.map { Variables.resolve(s, $0) } ?? s }

        let method = request.method.trimmingCharacters(in: .whitespaces).uppercased()
        var url = r(request.url.trimmingCharacters(in: .whitespacesAndNewlines))
        if variables != nil, !url.isEmpty, !url.contains("://") {
            url = "http://" + url
        }

        var headers: [(name: String, value: String)] = request.headers
            .filter { $0.enabled && !$0.key.trimmingCharacters(in: .whitespaces).isEmpty }
            .map { (r($0.key).trimmingCharacters(in: .whitespaces), r($0.value)) }

        func has(_ name: String) -> Bool {
            headers.contains { $0.name.caseInsensitiveCompare(name) == .orderedSame }
        }

        var basicAuth: (username: String, password: String)?
        switch auth.type {
        case .bearer where !auth.token.isEmpty && !has("Authorization"):
            headers.append(("Authorization", "Bearer \(r(auth.token))"))
        case .basic where !(auth.username.isEmpty && auth.password.isEmpty) && !has("Authorization"):
            basicAuth = (r(auth.username), r(auth.password))
        case .apiKey where !auth.key.isEmpty:
            if auth.location == .header {
                if !has(r(auth.key)) { headers.append((r(auth.key), r(auth.value))) }
            } else {
                url = URLQuery.appending([(r(auth.key), r(auth.value))], to: url)
            }
        default:
            break
        }

        let body: PreparedBody
        switch request.body.mode {
        case .none:
            body = .none
        case .json, .text, .xml:
            body = .raw(r(request.body.raw))
        case .form:
            body = .form(request.body.form
                .filter { $0.enabled && !$0.key.isEmpty }
                .map { (r($0.key), r($0.value)) })
        case .multipart:
            body = .multipart(request.body.form
                .filter { $0.enabled && !$0.key.isEmpty }
                .map { row in
                    var row = row
                    row.key = r(row.key)
                    row.value = r(row.value)
                    return row
                })
        case .file:
            body = .file(path: r(request.body.filePath))
        }

        if let type = request.body.mode.contentType, !has("Content-Type") {
            headers.append(("Content-Type", type))
        }

        return PreparedRequest(method: method.isEmpty ? "GET" : method, url: url,
                               headers: headers, body: body, basicAuth: basicAuth)
    }
}
