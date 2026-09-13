import AppKit
import Observation
import SwiftUI

struct RequestTab: Identifiable, Hashable, Codable {
    var id = UUID()
    /// The collection the request is saved in; `nil` for a scratch request.
    var collectionID: UUID?
    var draft: APIRequest
    /// The last saved state, so the tab can tell it has unsaved edits.
    var original: APIRequest
    /// Opened with a single click; the next single click reuses the tab until it's edited.
    var isPreview = false

    var isDirty: Bool { draft != original }
}

enum ResponseState {
    case idle
    case loading(started: Date, task: Task<Void, Never>)
    case done(HTTPResult)
    case failed(String)

    var isLoading: Bool {
        if case .loading = self { return true }
        return false
    }
}

enum ActiveSheet: Identifiable {
    case importer(paste: Bool)
    case environments(selected: UUID?)
    case collectionSettings(UUID)
    case folderSettings(collection: UUID, folder: UUID)
    case saveRequest(tab: UUID)

    var id: String {
        switch self {
        case .importer(let paste):             return "importer-\(paste)"
        case .environments:                    return "environments"
        case .collectionSettings(let id):      return "collection-\(id)"
        case .folderSettings(_, let folder):   return "folder-\(folder)"
        case .saveRequest(let tab):            return "save-\(tab)"
        }
    }
}

enum SidebarMode: String, CaseIterable {
    case collections, environments, history
}

enum EditorSection: String { case params, headers, body, auth }
enum ResponseSection: String { case body, headers, request }

enum PaneLayout: String {
    case stacked, sideBySide
}

/// Everything the window shows, and every way to change it.
///
/// One instance for the app (there's one window), so menu commands and views
/// reach the same state without threading it through the environment.
@Observable
@MainActor
final class Workspace {
    static let shared = Workspace()

    // MARK: Saved state

    var collections: [APICollection] = [] { didSet { scheduleWorkspaceSave() } }
    var environments: [APIEnvironment] = [] { didSet { scheduleWorkspaceSave() } }
    var activeEnvironmentID: UUID? { didSet { scheduleWorkspaceSave() } }
    var history: [HistoryEntry] = [] { didSet { scheduleWorkspaceSave() } }

    var tabs: [RequestTab] = [] { didSet { scheduleSessionSave() } }
    var selectedTabID: UUID? {
        didSet {
            scheduleSessionSave()
            // Mirror the tab in the sidebar; clear it for scratch tabs so clicking
            // the previously selected row still registers as a change.
            let tab = selectedTab
            sidebarSelection = tab?.collectionID != nil ? tab?.draft.id : nil
        }
    }
    var expanded: Set<UUID> = [] { didSet { scheduleSessionSave() } }

    // MARK: Transient state

    var responses: [UUID: ResponseState] = [:]
    var editorSections: [UUID: EditorSection] = [:]
    var responseSections: [UUID: ResponseSection] = [:]
    var sheet: ActiveSheet?
    var sidebarMode = SidebarMode.collections
    var sidebarSelection: UUID?
    var renamingID: UUID?
    var search = ""
    var toast: String?
    /// Bumped to move focus to the URL field.
    var urlFocusRequest = 0

    @ObservationIgnored private var isLoading = true
    @ObservationIgnored private var workspaceSaveTask: Task<Void, Never>?
    @ObservationIgnored private var sessionSaveTask: Task<Void, Never>?
    @ObservationIgnored private var toastTask: Task<Void, Never>?

    private init() {
        var seeded = false
        switch Storage.load(WorkspaceFile.self, from: Storage.workspaceURL) {
        case .value(let file):
            collections = file.collections
            environments = file.environments
            activeEnvironmentID = file.activeEnvironmentID
            history = file.history
            Storage.backUpWorkspace()
        case .missing:
            let seed = Seed.make()
            collections = seed.collections
            environments = seed.environments
            activeEnvironmentID = seed.environments.first?.id
            expanded = Set(seed.collections.map(\.id))
            seeded = true
        case .unreadable:
            Storage.setAside(Storage.workspaceURL)
        }

        if case .value(let session) = Storage.load(SessionFile.self, from: Storage.sessionURL) {
            let known = Set(collections.map(\.id))
            tabs = session.tabs.map { tab in
                var tab = tab
                if let id = tab.collectionID, !known.contains(id) { tab.collectionID = nil }
                return tab
            }
            selectedTabID = tabs.contains { $0.id == session.selectedTabID } ? session.selectedTabID : tabs.first?.id
            expanded = Set(session.expanded)
        }

        isLoading = false
        if seeded { saveWorkspace() }
    }

    // MARK: - Persistence

    private func scheduleWorkspaceSave() {
        guard !isLoading else { return }
        workspaceSaveTask?.cancel()
        workspaceSaveTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(500))
            guard !Task.isCancelled else { return }
            self?.saveWorkspace()
        }
    }

    private func scheduleSessionSave() {
        guard !isLoading else { return }
        sessionSaveTask?.cancel()
        sessionSaveTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(800))
            guard !Task.isCancelled else { return }
            self?.saveSession()
        }
    }

    private func saveWorkspace(synchronously: Bool = false) {
        let file = WorkspaceFile(collections: collections, environments: environments,
                                 activeEnvironmentID: activeEnvironmentID, history: history)
        Storage.write(file, to: Storage.workspaceURL, synchronously: synchronously)
    }

    private func saveSession(synchronously: Bool = false) {
        let file = SessionFile(tabs: tabs, selectedTabID: selectedTabID, expanded: Array(expanded))
        Storage.write(file, to: Storage.sessionURL, synchronously: synchronously)
    }

    /// Writes pending changes now; called on quit.
    func flush() {
        workspaceSaveTask?.cancel()
        sessionSaveTask?.cancel()
        saveWorkspace(synchronously: true)
        saveSession(synchronously: true)
    }

    // MARK: - Lookup

    var selectedTab: RequestTab? { tabs.first { $0.id == selectedTabID } }
    var activeEnvironment: APIEnvironment? { environments.first { $0.id == activeEnvironmentID } }

    func tab(_ id: UUID) -> RequestTab? { tabs.first { $0.id == id } }
    func collection(_ id: UUID?) -> APICollection? { collections.first { $0.id == id } }
    private func tabIndex(_ id: UUID) -> Int? { tabs.firstIndex { $0.id == id } }
    private func collectionIndex(_ id: UUID?) -> Int? { collections.firstIndex { $0.id == id } }

    /// Variables offered while typing `{{`: the selected request's, or just the
    /// active environment's when no request is open (e.g. editing variables).
    var completionScope: VariableScope {
        selectedTab.map { scope(for: $0) } ?? VariableScope(environment: activeEnvironment?.variables ?? [])
    }

    func scope(for tab: RequestTab) -> VariableScope {
        VariableScope(collection: collection(tab.collectionID)?.variables ?? [],
                      environment: activeEnvironment?.variables ?? [])
    }

    /// The auth a tab's request really uses, and where it came from when inherited.
    func effectiveAuth(for tab: RequestTab) -> (auth: Auth, source: String?) {
        if tab.draft.auth.type != .inherit { return (tab.draft.auth, nil) }
        guard let collection = collection(tab.collectionID) else { return (.noAuth, nil) }
        for folder in (collection.items.folderChain(to: tab.draft.id) ?? []).reversed() where folder.auth.type != .inherit {
            return (folder.auth, folder.name)
        }
        return (collection.auth.type == .inherit ? .noAuth : collection.auth, collection.name)
    }

    func prepared(_ tab: RequestTab, resolveVariables: Bool = true) -> PreparedRequest {
        RequestPreparer.prepare(tab.draft, auth: effectiveAuth(for: tab).auth,
                                variables: resolveVariables ? scope(for: tab).values : nil)
    }

    func curl(for tab: RequestTab, resolveVariables: Bool = true) -> String {
        CurlGenerator.generate(prepared(tab, resolveVariables: resolveVariables))
    }

    /// "Collection › Folder" for the request header.
    func breadcrumb(for tab: RequestTab) -> [String] {
        guard let collection = collection(tab.collectionID) else { return [] }
        return [collection.name] + (collection.items.folderChain(to: tab.draft.id) ?? []).map(\.name)
    }

    /// A binding into a tab's draft. Reads go through the live tab, not a
    /// captured copy, so a binding held across renders never writes stale data.
    func binding<T>(_ tabID: UUID, _ keyPath: WritableKeyPath<APIRequest, T>) -> Binding<T> {
        Binding(
            get: { self.tab(tabID)?.draft[keyPath: keyPath] ?? APIRequest()[keyPath: keyPath] },
            set: { value in self.updateDraft(tabID) { $0[keyPath: keyPath] = value } }
        )
    }

    // MARK: - Tabs

    func newTab(_ request: APIRequest = APIRequest(), unsaved: Bool = false) {
        var original = request
        if unsaved {
            original = APIRequest()
            original.id = request.id
        }
        let tab = RequestTab(collectionID: nil, draft: request, original: original)
        insertTab(tab)
        focusURL()
    }

    func open(requestID: UUID, in collectionID: UUID, preview: Bool = true) {
        if let i = tabs.firstIndex(where: { $0.collectionID == collectionID && $0.draft.id == requestID }) {
            if !preview { tabs[i].isPreview = false }
            selectedTabID = tabs[i].id
            return
        }
        guard let request = collection(collectionID)?.items.request(requestID) else { return }
        let tab = RequestTab(collectionID: collectionID, draft: request, original: request, isPreview: preview)
        if preview, let i = tabs.firstIndex(where: { $0.isPreview && !$0.isDirty }) {
            discardResponse(tabs[i].id)
            tabs[i] = tab
            selectedTabID = tab.id
        } else {
            insertTab(tab)
        }
    }

    private func insertTab(_ tab: RequestTab) {
        if let selected = selectedTabID, let i = tabIndex(selected) {
            tabs.insert(tab, at: i + 1)
        } else {
            tabs.append(tab)
        }
        selectedTabID = tab.id
    }

    func updateDraft(_ tabID: UUID, _ change: (inout APIRequest) -> Void) {
        guard let i = tabIndex(tabID) else { return }
        var draft = tabs[i].draft
        change(&draft)
        guard draft != tabs[i].draft else { return }
        tabs[i].draft = draft
        tabs[i].isPreview = false
    }

    /// Replaces the request's contents with a parsed cURL, keeping its identity and name if saved.
    func apply(curl parsed: APIRequest, to tabID: UUID) {
        guard let tab = tab(tabID) else { return }
        updateDraft(tabID) { draft in
            // Keep a name the user chose; replace a placeholder or one generated from the old URL.
            let generated = draft.name == APIRequest().name || draft.name == CurlParser.requestName(for: draft.url)
            let keepName = tab.collectionID != nil || !generated
            let name = draft.name
            let id = draft.id
            draft = parsed
            draft.id = id
            if keepName { draft.name = name }
        }
        flash("Imported cURL")
    }

    func close(_ tabID: UUID) {
        guard let i = tabIndex(tabID) else { return }
        let tab = tabs[i]
        if tab.isDirty {
            switch Alerts.saveChanges(to: tab.draft.name) {
            case .cancel:
                return
            case .discard:
                break
            case .save:
                guard save(tabID) else { return }
            }
        }
        cancel(tabID)
        discardResponse(tabID)
        guard let index = tabIndex(tabID) else { return }
        tabs.remove(at: index)
        if selectedTabID == tabID {
            selectedTabID = tabs.isEmpty ? nil : tabs[min(index, tabs.count - 1)].id
        }
    }

    func closeOthers(_ tabID: UUID) {
        for tab in tabs where tab.id != tabID { close(tab.id) }
    }

    func closeAll() {
        for tab in tabs { close(tab.id) }
    }

    func duplicateTab(_ tabID: UUID) {
        guard let tab = tab(tabID), case .request(var copy) = CollectionItem.request(tab.draft).withNewIDs() else { return }
        copy.name = tab.draft.name + " Copy"
        newTab(copy, unsaved: true)
    }

    func selectTab(offset: Int) {
        guard !tabs.isEmpty else { return }
        let current = selectedTabID.flatMap(tabIndex) ?? 0
        selectedTabID = tabs[(current + offset + tabs.count) % tabs.count].id
    }

    func focusURL() { urlFocusRequest += 1 }

    private func discardResponse(_ tabID: UUID) {
        responses[tabID] = nil
        editorSections[tabID] = nil
        responseSections[tabID] = nil
    }

    // MARK: - Saving requests

    /// Saves in place, or asks where to save a scratch request. True when the save completed.
    @discardableResult
    func save(_ tabID: UUID) -> Bool {
        guard let i = tabIndex(tabID) else { return false }
        let tab = tabs[i]
        guard let ci = collectionIndex(tab.collectionID), collections[ci].items.contains(tab.draft.id) else {
            sheet = .saveRequest(tab: tabID)
            return false
        }
        collections[ci].items.modify(tab.draft.id) { $0 = .request(tab.draft) }
        tabs[i].original = tab.draft
        tabs[i].isPreview = false
        return true
    }

    func save(_ tabID: UUID, as name: String, collectionID: UUID, folderID: UUID?) {
        guard let i = tabIndex(tabID), let ci = collectionIndex(collectionID) else { return }
        var request = tabs[i].draft
        request.name = name
        // Save As on a saved request makes a second copy, not a second reference.
        if collections.contains(where: { $0.items.contains(request.id) }) {
            request.id = UUID()
        }
        if let folderID {
            collections[ci].items.append(.request(request), toFolder: folderID)
            expanded.insert(folderID)
        } else {
            collections[ci].items.append(.request(request))
        }
        expanded.insert(collectionID)
        tabs[i].collectionID = collectionID
        tabs[i].draft = request
        tabs[i].original = request
        tabs[i].isPreview = false
        sidebarSelection = request.id
        flash("Saved to \(collections[ci].name)")
    }

    // MARK: - Collections

    @discardableResult
    func addCollection(named name: String = "New Collection", rename: Bool = true) -> UUID {
        let collection = APICollection(name: name)
        collections.append(collection)
        expanded.insert(collection.id)
        sidebarMode = .collections
        if rename { renamingID = collection.id }
        return collection.id
    }

    func addRequest(collectionID: UUID, folderID: UUID? = nil) {
        guard let ci = collectionIndex(collectionID) else { return }
        var request = APIRequest()
        request.name = "New Request"
        if let folderID {
            collections[ci].items.append(.request(request), toFolder: folderID)
            expanded.insert(folderID)
        } else {
            collections[ci].items.append(.request(request))
        }
        expanded.insert(collectionID)
        open(requestID: request.id, in: collectionID, preview: false)
        focusURL()
    }

    func addFolder(collectionID: UUID, parentID: UUID? = nil) {
        guard let ci = collectionIndex(collectionID) else { return }
        let folder = Folder()
        if let parentID {
            collections[ci].items.append(.folder(folder), toFolder: parentID)
            expanded.insert(parentID)
        } else {
            collections[ci].items.append(.folder(folder))
        }
        expanded.insert(collectionID)
        renamingID = folder.id
    }

    func rename(_ id: UUID, to newName: String) {
        let name = newName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty else { return }
        if let ci = collectionIndex(id) {
            collections[ci].name = name
            return
        }
        for ci in collections.indices where collections[ci].items.contains(id) {
            collections[ci].items.modify(id) { item in
                switch item {
                case .folder(var f):
                    f.name = name
                    item = .folder(f)
                case .request(var r):
                    r.name = name
                    item = .request(r)
                }
            }
        }
        for i in tabs.indices where tabs[i].draft.id == id {
            tabs[i].draft.name = name
            tabs[i].original.name = name
        }
    }

    func delete(_ id: UUID) {
        if let ci = collectionIndex(id) {
            let c = collections[ci]
            guard Alerts.confirm("Delete “\(c.name)”?",
                                 message: "Its \(c.items.requestCount) requests will be deleted. Export it first if you might need it again.",
                                 action: "Delete Collection") else { return }
            collections.remove(at: ci)
            detachTabs { $0.collectionID == id }
            return
        }
        guard let ci = collections.firstIndex(where: { $0.items.contains(id) }),
              let item = collections[ci].items.item(id) else { return }
        if case .folder(let f) = item, !f.items.isEmpty {
            guard Alerts.confirm("Delete “\(f.name)”?",
                                 message: "The folder and its \(f.items.requestCount) requests will be deleted.",
                                 action: "Delete Folder") else { return }
        }
        collections[ci].items.remove(id)
        let remaining = collections[ci].items
        let collectionID = collections[ci].id
        detachTabs { $0.collectionID == collectionID && !remaining.contains($0.draft.id) }
    }

    /// Tabs whose request was deleted: clean ones close, edited ones become scratch requests.
    private func detachTabs(where matches: (RequestTab) -> Bool) {
        for tab in tabs where matches(tab) {
            if tab.isDirty {
                if let i = tabIndex(tab.id) { tabs[i].collectionID = nil }
            } else {
                cancel(tab.id)
                discardResponse(tab.id)
                if let i = tabIndex(tab.id) { tabs.remove(at: i) }
                if selectedTabID == tab.id { selectedTabID = tabs.last?.id }
            }
        }
    }

    func duplicate(_ id: UUID) {
        if let ci = collectionIndex(id) {
            var copy = collections[ci]
            copy.id = UUID()
            copy.name += " Copy"
            copy.items = copy.items.map { $0.withNewIDs() }
            collections.insert(copy, at: ci + 1)
            return
        }
        guard let ci = collections.firstIndex(where: { $0.items.contains(id) }),
              let item = collections[ci].items.item(id) else { return }
        var copy = item.withNewIDs()
        switch copy {
        case .folder(var f):  f.name += " Copy"; copy = .folder(f)
        case .request(var r): r.name += " Copy"; copy = .request(r)
        }
        collections[ci].items.insert(copy, beside: id, after: true)
    }

    enum DropTarget {
        case collection(UUID)
        case folder(collection: UUID, folder: UUID)
        case item(collection: UUID, item: UUID)
    }

    /// Moves an item by drag and drop, within or across collections.
    @discardableResult
    func move(_ itemID: UUID, to target: DropTarget) -> Bool {
        guard let source = collections.firstIndex(where: { $0.items.contains(itemID) }) else { return false }
        let tree = collections[source].items

        // Refuse dropping onto itself or into its own subtree.
        switch target {
        case .folder(_, let folder):
            if folder == itemID || tree.isDescendant(folder, of: itemID) { return false }
        case .item(_, let sibling):
            if sibling == itemID || tree.isDescendant(sibling, of: itemID) { return false }
        case .collection:
            break
        }

        guard let moved = collections[source].items.remove(itemID) else { return false }
        var placed = false
        switch target {
        case .collection(let cid):
            if let ci = collectionIndex(cid) {
                collections[ci].items.append(moved)
                placed = true
            }
        case .folder(let cid, let folder):
            if let ci = collectionIndex(cid) {
                placed = collections[ci].items.append(moved, toFolder: folder)
                if placed { expanded.insert(folder) }
            }
        case .item(let cid, let sibling):
            if let ci = collectionIndex(cid) {
                placed = collections[ci].items.insert(moved, beside: sibling, after: false)
            }
        }
        guard placed else {
            collections[source].items.append(moved)
            return false
        }

        let targetCollection: UUID
        switch target {
        case .collection(let c), .folder(let c, _), .item(let c, _): targetCollection = c
        }
        let movedIDs = Set(([moved] as [CollectionItem]).allRequestIDs)
        for i in tabs.indices where movedIDs.contains(tabs[i].draft.id) && tabs[i].collectionID != nil {
            tabs[i].collectionID = targetCollection
        }
        return true
    }

    func updateCollection(_ id: UUID, _ change: (inout APICollection) -> Void) {
        guard let ci = collectionIndex(id) else { return }
        change(&collections[ci])
    }

    func updateFolder(collection: UUID, folder: UUID, _ change: (inout Folder) -> Void) {
        guard let ci = collectionIndex(collection) else { return }
        collections[ci].items.modify(folder) { item in
            guard case .folder(var f) = item else { return }
            change(&f)
            item = .folder(f)
        }
    }

    // MARK: - Environments

    @discardableResult
    func addEnvironment(named name: String = "New Environment") -> UUID {
        let environment = APIEnvironment(name: name)
        environments.append(environment)
        if activeEnvironmentID == nil { activeEnvironmentID = environment.id }
        return environment.id
    }

    func duplicateEnvironment(_ id: UUID) {
        guard let i = environments.firstIndex(where: { $0.id == id }) else { return }
        var copy = environments[i]
        copy.id = UUID()
        copy.name += " Copy"
        copy.variables = copy.variables.map { var kv = $0; kv.id = UUID(); return kv }
        environments.insert(copy, at: i + 1)
    }

    func deleteEnvironment(_ id: UUID) {
        guard let i = environments.firstIndex(where: { $0.id == id }) else { return }
        guard Alerts.confirm("Delete “\(environments[i].name)”?",
                             message: "Its \(environments[i].variables.count) variables will be deleted.",
                             action: "Delete Environment") else { return }
        environments.remove(at: i)
        if activeEnvironmentID == id { activeEnvironmentID = nil }
    }

    func updateEnvironment(_ id: UUID, _ change: (inout APIEnvironment) -> Void) {
        guard let i = environments.firstIndex(where: { $0.id == id }) else { return }
        change(&environments[i])
    }

    /// For a `{{name}}` that isn't defined anywhere: add it to the active
    /// environment (creating one if needed) and open it for a value.
    func defineVariable(_ name: String) {
        let id = activeEnvironmentID ?? addEnvironment(named: "Default")
        updateEnvironment(id) { env in
            if !env.variables.contains(where: { $0.key == name }) {
                env.variables.append(KeyValue(name, ""))
            }
        }
        sheet = .environments(selected: id)
    }

    // MARK: - Sending

    func send(_ tabID: UUID) {
        guard let tab = tab(tabID) else { return }
        cancel(tabID)

        let prepared = prepared(tab)
        let unresolved = Variables.names(in: ([prepared.url] + prepared.headers.map(\.value)).joined(separator: " "))
        if let name = unresolved.first {
            responses[tabID] = .failed("{{\(name)}} isn't defined. Add it to the active environment or the collection's variables.")
            return
        }

        let options = SendOptions.current
        let started = Date()
        let snapshot = tab.draft
        let curl = CurlGenerator.generate(prepared)
        let task = Task { [weak self] in
            do {
                var result = try await HTTPClient.send(prepared, options: options)
                result.curl = curl
                self?.finish(tabID, started: started, request: snapshot, outcome: .success(result))
            } catch is CancellationError {
                // Cancelled by the user; `cancel` already reset the state.
            } catch {
                self?.finish(tabID, started: started, request: snapshot, outcome: .failure(error))
            }
        }
        responses[tabID] = .loading(started: started, task: task)
    }

    private func finish(_ tabID: UUID, started: Date, request: APIRequest, outcome: Result<HTTPResult, Error>) {
        // Ignore a request that was superseded by a newer send in the same tab.
        guard case .loading(let current, _) = responses[tabID], current == started else { return }
        var entry = HistoryEntry(request: request)
        switch outcome {
        case .success(let result):
            responses[tabID] = .done(result)
            entry.status = result.status
            entry.duration = result.duration
        case .failure(let error):
            responses[tabID] = .failed(error.localizedDescription)
            entry.error = error.localizedDescription
            entry.duration = Date().timeIntervalSince(started)
        }
        history.insert(entry, at: 0)
        if history.count > 300 { history.removeLast(history.count - 300) }
    }

    func cancel(_ tabID: UUID) {
        guard case .loading(_, let task) = responses[tabID] else { return }
        task.cancel()
        responses[tabID] = .idle
    }

    // MARK: - Import / export

    func importItems(_ result: ImportResult) {
        collections += result.collections
        environments += result.environments
        for c in result.collections { expanded.insert(c.id) }
        if activeEnvironmentID == nil, let first = result.environments.first { activeEnvironmentID = first.id }
        sidebarMode = result.collections.isEmpty && !result.environments.isEmpty ? .environments : .collections

        var parts: [String] = []
        if !result.collections.isEmpty {
            parts.append(result.collections.count == 1 ? "“\(result.collections[0].name)”" : "\(result.collections.count) collections")
        }
        if !result.environments.isEmpty {
            parts.append(result.environments.count == 1 ? "1 environment" : "\(result.environments.count) environments")
        }
        flash("Imported " + parts.joined(separator: " and "))
    }

    func exportCollection(_ id: UUID) {
        guard let c = collection(id) else { return }
        do {
            let data = try Postman.exportCollection(c)
            if FilePanels.save(data, suggestedName: "\(fileName(c.name)).postman_collection.json") { flash("Exported “\(c.name)”") }
        } catch {
            Alerts.inform("Couldn't export “\(c.name)”", message: error.localizedDescription)
        }
    }

    func exportEnvironment(_ id: UUID) {
        guard let e = environments.first(where: { $0.id == id }) else { return }
        do {
            let data = try Postman.exportEnvironment(e)
            if FilePanels.save(data, suggestedName: "\(fileName(e.name)).postman_environment.json") { flash("Exported “\(e.name)”") }
        } catch {
            Alerts.inform("Couldn't export “\(e.name)”", message: error.localizedDescription)
        }
    }

    func exportEverything() {
        do {
            let data = try Postman.exportBackup(collections: collections, environments: environments)
            let stamp = Date().formatted(.iso8601.year().month().day())
            if FilePanels.save(data, suggestedName: "Relay Backup \(stamp).json") { flash("Exported everything") }
        } catch {
            Alerts.inform("Couldn't export", message: error.localizedDescription)
        }
    }

    private func fileName(_ name: String) -> String {
        name.components(separatedBy: CharacterSet(charactersIn: "/:\\")).joined(separator: "-")
    }

    // MARK: - History

    func openHistory(_ entry: HistoryEntry) {
        var request = entry.request
        request.id = UUID()
        newTab(request)
    }

    func deleteHistory(_ id: UUID) {
        history.removeAll { $0.id == id }
    }

    func clearHistory() {
        guard Alerts.confirm("Clear history?", message: "All \(history.count) entries will be removed.", action: "Clear History") else { return }
        history.removeAll()
    }

    // MARK: - Feedback

    func flash(_ message: String) {
        toast = message
        toastTask?.cancel()
        toastTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(1.8))
            guard !Task.isCancelled else { return }
            self?.toast = nil
        }
    }

    func copy(_ text: String, _ what: String) {
        Clipboard.copy(text)
        flash("Copied \(what)")
    }
}

extension Array where Element == CollectionItem {
    var allRequestIDs: [UUID] {
        flatMap { item -> [UUID] in
            switch item {
            case .request(let r): return [r.id]
            case .folder(let f):  return f.items.allRequestIDs
            }
        }
    }
}
