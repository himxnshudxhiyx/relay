import SwiftUI

struct SidebarView: View {
    @Bindable private var ws = Workspace.shared

    var body: some View {
        VStack(spacing: 0) {
            SidebarModePicker(mode: $ws.sidebarMode)
                .padding(.horizontal, 10)
                .padding(.top, 4)
                .padding(.bottom, 8)
            switch ws.sidebarMode {
            case .collections:  CollectionsPane()
            case .environments: EnvironmentsPane()
            case .history:      HistoryPane()
            }
        }
    }
}

private struct SidebarModePicker: View {
    @Binding var mode: SidebarMode

    var body: some View {
        HStack(spacing: 2) {
            item(.collections, "Collections", "tray.full")
            item(.environments, "Environments", "square.stack.3d.up")
            item(.history, "History", "clock.arrow.circlepath")
        }
        .padding(2)
        .background(RoundedRectangle(cornerRadius: 9).fill(Color.primary.opacity(0.06)))
    }

    private func item(_ value: SidebarMode, _ title: String, _ symbol: String) -> some View {
        let selected = mode == value
        return Button { mode = value } label: {
            VStack(spacing: 2) {
                Image(systemName: symbol).font(.system(size: 12, weight: .medium))
                Text(title).font(.app(10, .medium)).lineLimit(1).minimumScaleFactor(0.8)
            }
            .foregroundStyle(selected ? Theme.accent : Color.secondary)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 5)
            .background(RoundedRectangle(cornerRadius: 7).fill(selected ? Theme.canvas : .clear)
                .shadow(color: .black.opacity(selected ? 0.08 : 0), radius: 1, y: 0.5))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

private struct SidebarFooter<Content: View>: View {
    @ViewBuilder var content: () -> Content

    var body: some View {
        VStack(spacing: 0) {
            Divider()
            HStack(spacing: 12) {
                content()
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 12)
            .frame(height: 34)
        }
        .buttonStyle(.plain)
        .font(.app(12, .medium))
        .foregroundStyle(.secondary)
    }
}

// MARK: - Collections

private struct CollectionsPane: View {
    @Bindable private var ws = Workspace.shared

    var body: some View {
        VStack(spacing: 0) {
            SearchField(placeholder: "Search requests", text: $ws.search)
                .padding(.horizontal, 10)
                .padding(.bottom, 4)

            if ws.collections.isEmpty {
                EmptyState(symbol: "tray", title: "No collections",
                           message: "Group related requests into a collection, or bring yours over from Postman.") {
                    VStack(spacing: 8) {
                        Button("New Collection") { ws.addCollection() }
                            .buttonStyle(PrimaryButtonStyle(height: 28))
                        Button("Import…") { ws.sheet = .importer(paste: false) }
                            .buttonStyle(SecondaryButtonStyle(height: 28))
                    }
                }
            } else {
                List(selection: $ws.sidebarSelection) {
                    ForEach(ws.collections) { collection in
                        CollectionNode(collection: collection)
                    }
                }
                .listStyle(.sidebar)
                .onChange(of: ws.sidebarSelection) { _, id in
                    guard let id, let owner = ws.collections.first(where: { $0.items.request(id) != nil }) else { return }
                    ws.open(requestID: id, in: owner.id)
                }
            }

            SidebarFooter {
                Menu {
                    Button("New Collection") { ws.addCollection() }
                    Button("New Request") { ws.newTab() }
                    Divider()
                    Button("Import…") { ws.sheet = .importer(paste: false) }
                    Button("Paste cURL…") { ws.sheet = .importer(paste: true) }
                    Button("Export Everything…") { ws.exportEverything() }
                } label: {
                    Label("New", systemImage: "plus")
                }
                .menuStyle(.borderlessButton)
                .menuIndicator(.hidden)
                .fixedSize()
            }
        }
    }
}

extension Workspace {
    /// Search results are always shown expanded.
    func expansion(_ id: UUID) -> Binding<Bool> {
        Binding(
            get: { !self.search.isEmpty || self.expanded.contains(id) },
            set: { open in
                if open { self.expanded.insert(id) } else { self.expanded.remove(id) }
            }
        )
    }
}

extension Array where Element == CollectionItem {
    /// Requests matching `query` by name, URL or method, with the folders that lead to them.
    func filtered(_ query: String) -> [CollectionItem] {
        compactMap { item in
            switch item {
            case .request(let r):
                return [r.name, r.url, r.method].contains { $0.localizedCaseInsensitiveContains(query) } ? item : nil
            case .folder(var f):
                if f.name.localizedCaseInsensitiveContains(query) { return item }
                f.items = f.items.filtered(query)
                return f.items.isEmpty ? nil : .folder(f)
            }
        }
    }
}

@MainActor
private func dropItems(_ strings: [String], on target: Workspace.DropTarget) -> Bool {
    guard let first = strings.first, let id = UUID(uuidString: first) else { return false }
    return Workspace.shared.move(id, to: target)
}

private struct CollectionNode: View {
    let collection: APICollection
    private let ws = Workspace.shared

    var body: some View {
        let items = ws.search.isEmpty ? collection.items : collection.items.filtered(ws.search)
        if ws.search.isEmpty || !items.isEmpty {
            DisclosureGroup(isExpanded: ws.expansion(collection.id)) {
                ItemNodes(items: items, collectionID: collection.id)
            } label: {
                CollectionRow(collection: collection)
            }
        }
    }
}

private struct ItemNodes: View {
    let items: [CollectionItem]
    let collectionID: UUID

    var body: some View {
        ForEach(items) { item in
            switch item {
            case .folder(let folder):
                FolderNode(folder: folder, collectionID: collectionID)
            case .request(let request):
                RequestRow(request: request, collectionID: collectionID)
                    .tag(request.id)
            }
        }
    }
}

private struct FolderNode: View {
    let folder: Folder
    let collectionID: UUID
    private let ws = Workspace.shared

    var body: some View {
        DisclosureGroup(isExpanded: ws.expansion(folder.id)) {
            ItemNodes(items: folder.items, collectionID: collectionID)
        } label: {
            FolderRow(folder: folder, collectionID: collectionID)
        }
    }
}

private struct CollectionRow: View {
    let collection: APICollection
    private let ws = Workspace.shared

    var body: some View {
        HStack(spacing: 7) {
            Image(systemName: "shippingbox.fill")
                .font(.system(size: 11))
                .foregroundStyle(Theme.accent)
            RenamableText(id: collection.id, name: collection.name, font: .app(12.5, .semibold))
            Spacer(minLength: 4)
            Text("\(collection.items.requestCount)")
                .font(.app(10.5))
                .foregroundStyle(.tertiary)
        }
        .contentShape(Rectangle())
        .dropDestination(for: String.self) { ids, _ in dropItems(ids, on: .collection(collection.id)) }
        .contextMenu {
            Button("New Request") { ws.addRequest(collectionID: collection.id) }
            Button("New Folder") { ws.addFolder(collectionID: collection.id) }
            Divider()
            Button("Auth, Variables & Settings…") { ws.sheet = .collectionSettings(collection.id) }
            Button("Rename") { ws.renamingID = collection.id }
            Button("Duplicate") { ws.duplicate(collection.id) }
            Button("Export as Postman Collection…") { ws.exportCollection(collection.id) }
            Divider()
            Button("Delete…", role: .destructive) { ws.delete(collection.id) }
        }
    }
}

private struct FolderRow: View {
    let folder: Folder
    let collectionID: UUID
    private let ws = Workspace.shared

    var body: some View {
        HStack(spacing: 7) {
            Image(systemName: "folder")
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
            RenamableText(id: folder.id, name: folder.name, font: .app(12.5, .medium))
            Spacer(minLength: 4)
            if folder.auth.type != .inherit {
                Image(systemName: "key")
                    .font(.system(size: 9))
                    .foregroundStyle(.tertiary)
                    .help("Sets its own auth: \(folder.auth.type.title)")
            }
        }
        .contentShape(Rectangle())
        .draggable(folder.id.uuidString)
        .dropDestination(for: String.self) { ids, _ in
            dropItems(ids, on: .folder(collection: collectionID, folder: folder.id))
        }
        .contextMenu {
            Button("New Request") { ws.addRequest(collectionID: collectionID, folderID: folder.id) }
            Button("New Folder") { ws.addFolder(collectionID: collectionID, parentID: folder.id) }
            Divider()
            Button("Auth & Settings…") { ws.sheet = .folderSettings(collection: collectionID, folder: folder.id) }
            Button("Rename") { ws.renamingID = folder.id }
            Button("Duplicate") { ws.duplicate(folder.id) }
            Divider()
            Button("Delete…", role: .destructive) { ws.delete(folder.id) }
        }
    }
}

private struct RequestRow: View {
    let request: APIRequest
    let collectionID: UUID
    private let ws = Workspace.shared

    var body: some View {
        let dirty = ws.tabs.contains { $0.collectionID == collectionID && $0.draft.id == request.id && $0.isDirty }
        HStack(spacing: 6) {
            MethodBadge(method: request.method, size: 9.5)
                .frame(width: 36, alignment: .leading)
            RenamableText(id: request.id, name: request.name, font: .app(12.5))
            Spacer(minLength: 4)
            if dirty {
                Circle().fill(Theme.accent).frame(width: 6, height: 6).help("Unsaved changes")
            }
        }
        .contentShape(Rectangle())
        .help(request.url)
        .draggable(request.id.uuidString)
        .dropDestination(for: String.self) { ids, _ in
            dropItems(ids, on: .item(collection: collectionID, item: request.id))
        }
        .contextMenu {
            Button("Open in New Tab") { ws.open(requestID: request.id, in: collectionID, preview: false) }
            Button("Copy as cURL") {
                let tab = RequestTab(collectionID: collectionID, draft: request, original: request)
                ws.copy(ws.curl(for: tab), "cURL")
            }
            Divider()
            Button("Rename") { ws.renamingID = request.id }
            Button("Duplicate") { ws.duplicate(request.id) }
            Divider()
            Button("Delete", role: .destructive) { ws.delete(request.id) }
        }
    }
}

/// A name that turns into a text field while `renamingID` points at it.
private struct RenamableText: View {
    let id: UUID
    let name: String
    let font: Font

    @Bindable private var ws = Workspace.shared
    @State private var draft = ""
    @FocusState private var focused: Bool

    var body: some View {
        if ws.renamingID == id {
            TextField("Name", text: $draft)
                .textFieldStyle(.plain)
                .font(font)
                .focused($focused)
                .onAppear {
                    draft = name
                    DispatchQueue.main.async { focused = true }
                }
                .onSubmit(commit)
                .onExitCommand { ws.renamingID = nil }
                .onChange(of: focused) { _, isFocused in
                    if !isFocused { commit() }
                }
        } else {
            Text(name)
                .font(font)
                .lineLimit(1)
        }
    }

    private func commit() {
        guard ws.renamingID == id else { return }
        ws.rename(id, to: draft)
        ws.renamingID = nil
    }
}

// MARK: - Environments

private struct EnvironmentsPane: View {
    private let ws = Workspace.shared

    var body: some View {
        VStack(spacing: 0) {
            if ws.environments.isEmpty {
                EmptyState(symbol: "square.stack.3d.up", title: "No environments",
                           message: "An environment holds values like {{baseUrl}} and tokens, so the same request works against local, staging and production.") {
                    Button("New Environment") {
                        ws.sheet = .environments(selected: ws.addEnvironment())
                    }
                    .buttonStyle(PrimaryButtonStyle(height: 28))
                }
            } else {
                List {
                    Section {
                        ForEach(ws.environments) { environment in
                            EnvironmentRow(environment: environment)
                        }
                    } header: {
                        Text("Click to edit. The circle marks the active one.")
                            .font(.app(10.5))
                            .textCase(nil)
                    }
                }
                .listStyle(.sidebar)
            }

            SidebarFooter {
                Button {
                    ws.sheet = .environments(selected: ws.addEnvironment())
                } label: {
                    Label("New Environment", systemImage: "plus")
                }
            }
        }
    }
}

private struct EnvironmentRow: View {
    let environment: APIEnvironment
    private let ws = Workspace.shared

    var body: some View {
        let active = ws.activeEnvironmentID == environment.id
        HStack(spacing: 8) {
            Button {
                ws.activeEnvironmentID = active ? nil : environment.id
            } label: {
                Image(systemName: active ? "checkmark.circle.fill" : "circle")
                    .font(.system(size: 13))
                    .foregroundStyle(active ? Theme.accent : Color.secondary)
            }
            .buttonStyle(.plain)
            .help(active ? "Active. Click to turn off." : "Make active")

            VStack(alignment: .leading, spacing: 1) {
                Text(environment.name)
                    .font(.app(12.5, active ? .semibold : .regular))
                    .lineLimit(1)
                Text(environment.variables.count == 1 ? "1 variable" : "\(environment.variables.count) variables")
                    .font(.app(10.5))
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
        }
        .padding(.vertical, 2)
        .contentShape(Rectangle())
        .onTapGesture { ws.sheet = .environments(selected: environment.id) }
        .contextMenu {
            Button(active ? "Deactivate" : "Set Active") { ws.activeEnvironmentID = active ? nil : environment.id }
            Button("Edit…") { ws.sheet = .environments(selected: environment.id) }
            Button("Duplicate") { ws.duplicateEnvironment(environment.id) }
            Button("Export as Postman Environment…") { ws.exportEnvironment(environment.id) }
            Divider()
            Button("Delete…", role: .destructive) { ws.deleteEnvironment(environment.id) }
        }
    }
}

// MARK: - History

private struct HistoryPane: View {
    private let ws = Workspace.shared
    @State private var query = ""

    var body: some View {
        VStack(spacing: 0) {
            SearchField(placeholder: "Search history", text: $query)
                .padding(.horizontal, 10)
                .padding(.bottom, 4)

            if ws.history.isEmpty {
                EmptyState(symbol: "clock", title: "No history yet", message: "Every request you send is listed here.")
            } else {
                List {
                    ForEach(groups, id: \.title) { group in
                        Section(group.title) {
                            ForEach(group.entries) { entry in
                                HistoryRow(entry: entry)
                            }
                        }
                    }
                }
                .listStyle(.sidebar)
            }

            SidebarFooter {
                Button { ws.clearHistory() } label: {
                    Label("Clear History", systemImage: "trash")
                }
                .disabled(ws.history.isEmpty)
            }
        }
    }

    private var groups: [(title: String, entries: [HistoryEntry])] {
        let entries = query.isEmpty ? ws.history : ws.history.filter {
            $0.request.url.localizedCaseInsensitiveContains(query) || $0.request.name.localizedCaseInsensitiveContains(query)
        }
        let calendar = Calendar.current
        var result: [(title: String, entries: [HistoryEntry])] = []
        for entry in entries {
            let title = calendar.isDateInToday(entry.date) ? "Today"
                : calendar.isDateInYesterday(entry.date) ? "Yesterday"
                : entry.date.formatted(.dateTime.weekday(.wide).month().day())
            if result.last?.title == title {
                result[result.count - 1].entries.append(entry)
            } else {
                result.append((title, [entry]))
            }
        }
        return result
    }
}

private struct HistoryRow: View {
    let entry: HistoryEntry
    private let ws = Workspace.shared

    var body: some View {
        HStack(alignment: .top, spacing: 6) {
            MethodBadge(method: entry.request.method, size: 9.5)
                .frame(width: 36, alignment: .leading)
                .padding(.top, 2)
            VStack(alignment: .leading, spacing: 2) {
                Text(displayURL)
                    .font(.code(11))
                    .lineLimit(1)
                    .truncationMode(.middle)
                HStack(spacing: 6) {
                    if let status = entry.status {
                        Text("\(status)").font(.app(10.5, .semibold)).foregroundStyle(Theme.status(status))
                    } else {
                        Text("Failed").font(.app(10.5, .semibold)).foregroundStyle(Theme.red)
                    }
                    Text(Format.duration(entry.duration))
                    Text(entry.date.formatted(date: .omitted, time: .shortened))
                }
                .font(.app(10.5))
                .foregroundStyle(.secondary)
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { ws.openHistory(entry) }
        .help(entry.error ?? entry.request.url)
        .contextMenu {
            Button("Open in New Tab") { ws.openHistory(entry) }
            Button("Copy URL") { ws.copy(entry.request.url, "URL") }
            Divider()
            Button("Delete", role: .destructive) { ws.deleteHistory(entry.id) }
        }
    }

    private var displayURL: String {
        let url = entry.request.url
        for prefix in ["https://", "http://"] where url.hasPrefix(prefix) {
            return String(url.dropFirst(prefix.count))
        }
        return url.isEmpty ? "(no URL)" : url
    }
}
