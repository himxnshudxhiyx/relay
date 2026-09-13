import Foundation

/// Lookups and edits on a collection's item tree, addressed by id.
///
/// The tree is plain values, so every edit rebuilds the path from the root to
/// the changed node. Collections are small enough that this is cheaper than
/// the bookkeeping a reference graph would need.
extension Array where Element == CollectionItem {
    func item(_ id: UUID) -> CollectionItem? {
        for item in self {
            if item.id == id { return item }
            if case .folder(let f) = item, let found = f.items.item(id) { return found }
        }
        return nil
    }

    func request(_ id: UUID) -> APIRequest? {
        if case .request(let r)? = item(id) { return r }
        return nil
    }

    func contains(_ id: UUID) -> Bool { item(id) != nil }

    /// Folders enclosing `id`, outermost first. `nil` when `id` isn't in this tree.
    func folderChain(to id: UUID) -> [Folder]? {
        for item in self {
            if item.id == id { return [] }
            if case .folder(let f) = item, let chain = f.items.folderChain(to: id) {
                return [f] + chain
            }
        }
        return nil
    }

    @discardableResult
    mutating func modify(_ id: UUID, _ change: (inout CollectionItem) -> Void) -> Bool {
        for i in indices {
            if self[i].id == id {
                change(&self[i])
                return true
            }
            if case .folder(var f) = self[i] {
                if f.items.modify(id, change) {
                    self[i] = .folder(f)
                    return true
                }
            }
        }
        return false
    }

    @discardableResult
    mutating func remove(_ id: UUID) -> CollectionItem? {
        if let i = firstIndex(where: { $0.id == id }) { return remove(at: i) }
        for i in indices {
            if case .folder(var f) = self[i], let removed = f.items.remove(id) {
                self[i] = .folder(f)
                return removed
            }
        }
        return nil
    }

    /// Inserts beside `sibling`, wherever in the tree it is.
    @discardableResult
    mutating func insert(_ new: CollectionItem, beside sibling: UUID, after: Bool) -> Bool {
        if let i = firstIndex(where: { $0.id == sibling }) {
            insert(new, at: after ? i + 1 : i)
            return true
        }
        for i in indices {
            if case .folder(var f) = self[i], f.items.insert(new, beside: sibling, after: after) {
                self[i] = .folder(f)
                return true
            }
        }
        return false
    }

    /// Appends inside the folder `folderID`.
    @discardableResult
    mutating func append(_ new: CollectionItem, toFolder folderID: UUID) -> Bool {
        var appended = false
        modify(folderID) { item in
            guard case .folder(var f) = item else { return }
            f.items.append(new)
            item = .folder(f)
            appended = true
        }
        return appended
    }

    var requestCount: Int {
        reduce(0) { count, item in
            switch item {
            case .request:        return count + 1
            case .folder(let f):  return count + f.items.requestCount
            }
        }
    }

    /// Every folder, depth first, with its path ("Users / Admin").
    func folderPaths(prefix: String = "") -> [(folder: Folder, path: String)] {
        flatMap { item -> [(folder: Folder, path: String)] in
            guard case .folder(let f) = item else { return [] }
            let path = prefix.isEmpty ? f.name : "\(prefix) / \(f.name)"
            return [(f, path)] + f.items.folderPaths(prefix: path)
        }
    }

    /// Does `ancestor` contain `id` anywhere below it? Used to refuse dropping a folder into itself.
    func isDescendant(_ id: UUID, of ancestor: UUID) -> Bool {
        guard case .folder(let f)? = item(ancestor) else { return false }
        return f.items.contains(id)
    }
}

extension CollectionItem {
    /// A deep copy with fresh ids, for Duplicate.
    func withNewIDs() -> CollectionItem {
        switch self {
        case .request(var r):
            r.id = UUID()
            r.params = r.params.map { var kv = $0; kv.id = UUID(); return kv }
            r.headers = r.headers.map { var kv = $0; kv.id = UUID(); return kv }
            r.body.form = r.body.form.map { var kv = $0; kv.id = UUID(); return kv }
            return .request(r)
        case .folder(var f):
            f.id = UUID()
            f.items = f.items.map { $0.withNewIDs() }
            return .folder(f)
        }
    }
}

extension APICollection {
    /// The auth a request actually uses: its own, or the nearest parent's when set to inherit.
    func effectiveAuth(for request: APIRequest) -> Auth {
        if request.auth.type != .inherit { return request.auth }
        for folder in (items.folderChain(to: request.id) ?? []).reversed() where folder.auth.type != .inherit {
            return folder.auth
        }
        return auth.type == .inherit ? .noAuth : auth
    }
}
