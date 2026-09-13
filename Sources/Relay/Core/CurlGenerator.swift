import Foundation

/// Prints a prepared request as a bash cURL command.
///
/// Long-form flags throughout (`--header`, not `-H`) so the command reads
/// clearly when pasted into a ticket, and single quotes so nothing in a value
/// is expanded by the shell.
enum CurlGenerator {
    static func generate(_ prepared: PreparedRequest, multiline: Bool = true) -> String {
        var parts = ["curl"]

        let hasBody: Bool
        switch prepared.body {
        case .none:                 hasBody = false
        case .raw(let text):        hasBody = !text.isEmpty
        case .form(let pairs):      hasBody = !pairs.isEmpty
        case .multipart(let rows):  hasBody = !rows.isEmpty
        case .file(let path):       hasBody = !path.isEmpty
        }
        let isMultipart: Bool = {
            if case .multipart = prepared.body { return hasBody }
            return false
        }()

        if prepared.method != "GET" || hasBody {
            parts.append("--request \(prepared.method)")
        }
        parts.append("--url \(quote(prepared.url))")

        for header in prepared.headers {
            // curl writes its own multipart Content-Type, boundary included.
            if isMultipart && header.name.caseInsensitiveCompare("Content-Type") == .orderedSame { continue }
            parts.append("--header \(quote("\(header.name): \(header.value)"))")
        }

        if let basic = prepared.basicAuth {
            parts.append("--user \(quote("\(basic.username):\(basic.password)"))")
        }

        if hasBody {
            switch prepared.body {
            case .none:
                break
            case .raw(let text):
                parts.append("--data \(quote(text))")
            case .form(let pairs):
                let encoded = pairs.map { "\(URLQuery.formEncode($0.key))=\(URLQuery.formEncode($0.value))" }
                parts.append("--data \(quote(encoded.joined(separator: "&")))")
            case .multipart(let rows):
                for row in rows {
                    parts.append("--form \(quote("\(row.key)=\(row.isFile ? "@" : "")\(row.value)"))")
                }
            case .file(let path):
                parts.append("--data-binary \(quote("@\(path)"))")
            }
        }

        return parts.joined(separator: multiline ? " \\\n  " : " ")
    }

    private static func quote(_ s: String) -> String {
        "'" + s.replacingOccurrences(of: "'", with: #"'\''"#) + "'"
    }
}
