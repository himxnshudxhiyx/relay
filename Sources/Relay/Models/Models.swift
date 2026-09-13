import Foundation

// MARK: - Decoding helpers

/// Every stored type decodes a missing key to its default, so a workspace file
/// written by an older build still opens after a field is added.
extension KeyedDecodingContainer {
    func field<T: Decodable>(_ key: Key, _ fallback: @autoclosure () -> T) throws -> T {
        try decodeIfPresent(T.self, forKey: key) ?? fallback()
    }
}

/// A string enum that decodes an unknown raw value to `fallback` instead of failing.
protocol LenientEnum: RawRepresentable, Codable, CaseIterable, Identifiable, Hashable where RawValue == String {
    static var fallback: Self { get }
}

extension LenientEnum {
    init(from decoder: Decoder) throws {
        self = Self(rawValue: try String(from: decoder)) ?? .fallback
    }

    var id: String { rawValue }
}

// MARK: - Key/value rows

/// A row in a params, headers, form or variables table.
struct KeyValue: Hashable, Identifiable {
    var id = UUID()
    var key = ""
    var value = ""
    var enabled = true
    var description = ""
    /// Multipart only: `value` is a file path.
    var isFile = false
    /// Variables only: masked in the editor.
    var isSecret = false

    init(_ key: String = "", _ value: String = "", enabled: Bool = true,
         description: String = "", isFile: Bool = false, isSecret: Bool = false) {
        self.key = key
        self.value = value
        self.enabled = enabled
        self.description = description
        self.isFile = isFile
        self.isSecret = isSecret
    }

    var isBlank: Bool { key.isEmpty && value.isEmpty }
}

extension KeyValue: Codable {
    enum CodingKeys: String, CodingKey { case id, key, value, enabled, description, isFile, isSecret }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.field(.id, UUID())
        key = try c.field(.key, "")
        value = try c.field(.value, "")
        enabled = try c.field(.enabled, true)
        description = try c.field(.description, "")
        isFile = try c.field(.isFile, false)
        isSecret = try c.field(.isSecret, false)
    }
}

// MARK: - Body

enum BodyMode: String, LenientEnum {
    case none, json, text, xml, form, multipart, file

    static let fallback = BodyMode.none

    var title: String {
        switch self {
        case .none:      return "None"
        case .json:      return "JSON"
        case .text:      return "Text"
        case .xml:       return "XML"
        case .form:      return "Form URL-Encoded"
        case .multipart: return "Multipart Form"
        case .file:      return "Binary File"
        }
    }

    /// The Content-Type sent when the request doesn't set one. Multipart gets
    /// its boundary from the client, so it has none here.
    var contentType: String? {
        switch self {
        case .json: return "application/json"
        case .text: return "text/plain"
        case .xml:  return "application/xml"
        case .form: return "application/x-www-form-urlencoded"
        case .file: return "application/octet-stream"
        case .none, .multipart: return nil
        }
    }

    /// Edited as free text rather than a table.
    var isRaw: Bool { self == .json || self == .text || self == .xml }
}

struct RequestBody: Hashable {
    var mode = BodyMode.none
    var raw = ""
    var form: [KeyValue] = []
    var filePath = ""
}

extension RequestBody: Codable {
    enum CodingKeys: String, CodingKey { case mode, raw, form, filePath }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        mode = try c.field(.mode, .none)
        raw = try c.field(.raw, "")
        form = try c.field(.form, [])
        filePath = try c.field(.filePath, "")
    }
}

// MARK: - Auth

enum AuthType: String, LenientEnum {
    case inherit, none, bearer, basic, apiKey

    static let fallback = AuthType.none

    var title: String {
        switch self {
        case .inherit: return "Inherit from Parent"
        case .none:    return "No Auth"
        case .bearer:  return "Bearer Token"
        case .basic:   return "Basic Auth"
        case .apiKey:  return "API Key"
        }
    }
}

enum APIKeyLocation: String, LenientEnum {
    case header, query

    static let fallback = APIKeyLocation.header

    var title: String { self == .header ? "Header" : "Query Param" }
}

struct Auth: Hashable {
    var type = AuthType.inherit
    var token = ""
    var username = ""
    var password = ""
    var key = ""
    var value = ""
    var location = APIKeyLocation.header

    static let inherit = Auth()
    static let noAuth = Auth(type: .none)
}

extension Auth: Codable {
    enum CodingKeys: String, CodingKey { case type, token, username, password, key, value, location }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        type = try c.field(.type, .inherit)
        token = try c.field(.token, "")
        username = try c.field(.username, "")
        password = try c.field(.password, "")
        key = try c.field(.key, "")
        value = try c.field(.value, "")
        location = try c.field(.location, .header)
    }
}

// MARK: - Request

struct APIRequest: Hashable, Identifiable {
    static let methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"]

    var id = UUID()
    var name = "Untitled Request"
    var method = "GET"
    var url = ""
    var params: [KeyValue] = []
    var headers: [KeyValue] = []
    var body = RequestBody()
    var auth = Auth.inherit
    var docs = ""
    /// Postman fields Relay doesn't edit (scripts, saved examples), kept so an
    /// imported collection exports without losing them.
    var extras: [String: JSONValue] = [:]
}

extension APIRequest: Codable {
    enum CodingKeys: String, CodingKey { case id, name, method, url, params, headers, body, auth, docs, extras }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.field(.id, UUID())
        name = try c.field(.name, "Untitled Request")
        method = try c.field(.method, "GET")
        url = try c.field(.url, "")
        params = try c.field(.params, [])
        headers = try c.field(.headers, [])
        body = try c.field(.body, RequestBody())
        auth = try c.field(.auth, .inherit)
        docs = try c.field(.docs, "")
        extras = try c.field(.extras, [:])
    }
}

// MARK: - Collections

struct Folder: Hashable, Identifiable {
    var id = UUID()
    var name = "New Folder"
    var items: [CollectionItem] = []
    var auth = Auth.inherit
    var docs = ""
    var extras: [String: JSONValue] = [:]
}

extension Folder: Codable {
    enum CodingKeys: String, CodingKey { case id, name, items, auth, docs, extras }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.field(.id, UUID())
        name = try c.field(.name, "New Folder")
        items = try c.field(.items, [])
        auth = try c.field(.auth, .inherit)
        docs = try c.field(.docs, "")
        extras = try c.field(.extras, [:])
    }
}

enum CollectionItem: Hashable, Identifiable {
    case folder(Folder)
    case request(APIRequest)

    var id: UUID {
        switch self {
        case .folder(let f):  return f.id
        case .request(let r): return r.id
        }
    }

    var name: String {
        switch self {
        case .folder(let f):  return f.name
        case .request(let r): return r.name
        }
    }

    var isFolder: Bool {
        if case .folder = self { return true }
        return false
    }
}

extension CollectionItem: Codable {
    enum CodingKeys: String, CodingKey { case folder, request }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        if let folder = try c.decodeIfPresent(Folder.self, forKey: .folder) {
            self = .folder(folder)
        } else {
            self = .request(try c.decode(APIRequest.self, forKey: .request))
        }
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        switch self {
        case .folder(let f):  try c.encode(f, forKey: .folder)
        case .request(let r): try c.encode(r, forKey: .request)
        }
    }
}

struct APICollection: Hashable, Identifiable {
    var id = UUID()
    var name = "New Collection"
    var items: [CollectionItem] = []
    var auth = Auth.noAuth
    var variables: [KeyValue] = []
    var docs = ""
    var extras: [String: JSONValue] = [:]
}

extension APICollection: Codable {
    enum CodingKeys: String, CodingKey { case id, name, items, auth, variables, docs, extras }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.field(.id, UUID())
        name = try c.field(.name, "New Collection")
        items = try c.field(.items, [])
        auth = try c.field(.auth, .noAuth)
        variables = try c.field(.variables, [])
        docs = try c.field(.docs, "")
        extras = try c.field(.extras, [:])
    }
}

// MARK: - Environments

struct APIEnvironment: Hashable, Identifiable {
    var id = UUID()
    var name = "New Environment"
    var variables: [KeyValue] = []
}

extension APIEnvironment: Codable {
    enum CodingKeys: String, CodingKey { case id, name, variables }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.field(.id, UUID())
        name = try c.field(.name, "New Environment")
        variables = try c.field(.variables, [])
    }
}

// MARK: - History

struct HistoryEntry: Hashable, Identifiable, Codable {
    var id = UUID()
    var date = Date()
    var request: APIRequest
    var status: Int?
    var duration: TimeInterval = 0
    var error: String?
}
