import Foundation

/// What's saved in `workspace.json`: the things you'd be upset to lose.
struct WorkspaceFile: Codable {
    var collections: [APICollection]
    var environments: [APIEnvironment]
    var activeEnvironmentID: UUID?
    var history: [HistoryEntry]
}

/// What's saved in `session.json`: open tabs (unsaved edits included) and
/// sidebar state. Kept apart so a bad session file can't cost you collections.
struct SessionFile: Codable {
    var tabs: [RequestTab]
    var selectedTabID: UUID?
    var expanded: [UUID]
}

enum Storage {
    static let folder: URL = FileManager.default
        .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("Relay", isDirectory: true)

    static let workspaceURL = folder.appendingPathComponent("workspace.json")
    static let sessionURL = folder.appendingPathComponent("session.json")
    static let backupURL = folder.appendingPathComponent("workspace.backup.json")

    /// Serial, so an older snapshot can never land on disk after a newer one.
    private static let queue = DispatchQueue(label: "com.himanshu.relay.storage")

    enum Loaded<T> {
        case missing
        case value(T)
        case unreadable
    }

    static func load<T: Decodable>(_ type: T.Type, from url: URL) -> Loaded<T> {
        guard FileManager.default.fileExists(atPath: url.path) else { return .missing }
        guard let data = try? Data(contentsOf: url),
              let value = try? decoder.decode(T.self, from: data) else { return .unreadable }
        return .value(value)
    }

    static func write<T: Encodable>(_ value: T, to url: URL, synchronously: Bool = false) {
        guard let data = try? encoder.encode(value) else { return }
        let work = {
            try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            try? data.write(to: url, options: .atomic)
        }
        if synchronously { queue.sync(execute: work) } else { queue.async(execute: work) }
    }

    /// A copy of the workspace as it was at launch — one step of undo for
    /// anything that goes wrong during a session.
    static func backUpWorkspace() {
        queue.async {
            try? FileManager.default.removeItem(at: backupURL)
            try? FileManager.default.copyItem(at: workspaceURL, to: backupURL)
        }
    }

    /// Moves an unreadable file aside rather than letting the next save
    /// overwrite something that might still be recoverable by hand.
    static func setAside(_ url: URL) {
        let stamp = Int(Date().timeIntervalSince1970)
        let aside = url.deletingPathExtension().appendingPathExtension("unreadable-\(stamp).json")
        try? FileManager.default.moveItem(at: url, to: aside)
    }

    private static let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .withoutEscapingSlashes]
        e.dateEncodingStrategy = .iso8601
        return e
    }()

    private static let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()
}
