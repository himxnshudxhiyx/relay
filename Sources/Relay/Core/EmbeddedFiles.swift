import Foundation

/// A file sent inside a JSON response as a base64 string, such as
/// `{"fileName": "GSTR1", "pdf": "JVBERi0x…"}`. Recognised by its first
/// bytes, so the key's name doesn't matter.
struct EmbeddedFile: Hashable {
    enum Kind: String { case pdf, png, jpeg, gif, webp }

    /// Where it sits, e.g. `data.pdf` or `files[0].content`.
    let path: String
    let kind: Kind
    let data: Data
    /// A sibling `fileName`/`filename`/`name`, when the response gives one.
    let suggestedName: String?

    var isImage: Bool { kind != .pdf }
    var fileExtension: String { kind == .jpeg ? "jpg" : kind.rawValue }

    /// Shorter strings are never files worth previewing.
    static let minimumLength = 64

    static func find(inJSON data: Data) -> [EmbeddedFile] {
        guard let root = try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed]) else { return [] }
        var found: [EmbeddedFile] = []
        walk(root, path: "", into: &found)
        return found
    }

    private static func walk(_ value: Any, path: String, into found: inout [EmbeddedFile]) {
        if let object = value as? [String: Any] {
            let name = ["fileName", "filename", "file_name", "name"].lazy.compactMap { object[$0] as? String }.first
            for key in object.keys.sorted() {
                let child = path.isEmpty ? key : "\(path).\(key)"
                if let string = object[key] as? String, let file = decode(string, path: child, name: name) {
                    found.append(file)
                } else {
                    walk(object[key]!, path: child, into: &found)
                }
            }
        } else if let array = value as? [Any] {
            for (i, element) in array.enumerated() {
                let child = "\(path)[\(i)]"
                if let string = element as? String, let file = decode(string, path: child, name: nil) {
                    found.append(file)
                } else {
                    walk(element, path: child, into: &found)
                }
            }
        }
    }

    static func decode(_ string: String, path: String, name: String?) -> EmbeddedFile? {
        guard string.utf8.count >= minimumLength else { return nil }
        // `data:application/pdf;base64,` prefixes are common; the bytes decide the type either way.
        var payload = Substring(string)
        if payload.hasPrefix("data:"), let comma = payload.firstIndex(of: ",") { payload = payload[payload.index(after: comma)...] }
        // Check the first bytes before decoding the whole string.
        guard let head = Data(base64Encoded: String(payload.prefix(16))), let kind = kind(of: head),
              let data = Data(base64Encoded: String(payload), options: .ignoreUnknownCharacters) else { return nil }
        return EmbeddedFile(path: path, kind: kind, data: data, suggestedName: name)
    }

    private static func kind(of head: Data) -> Kind? {
        let b = [UInt8](head)
        func starts(_ magic: [UInt8]) -> Bool { b.count >= magic.count && Array(b.prefix(magic.count)) == magic }
        if starts([0x25, 0x50, 0x44, 0x46]) { return .pdf }                       // %PDF
        if starts([0x89, 0x50, 0x4E, 0x47]) { return .png }
        if starts([0xFF, 0xD8, 0xFF]) { return .jpeg }
        if starts([0x47, 0x49, 0x46, 0x38]) { return .gif }                       // GIF8
        if starts([0x52, 0x49, 0x46, 0x46]), b.count >= 12, Array(b[8..<12]) == [0x57, 0x45, 0x42, 0x50] { return .webp }
        return nil
    }
}
