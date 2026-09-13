import AppKit
import SwiftUI

// MARK: - Environments

struct EnvironmentsSheet: View {
    @State var selection: UUID?
    private let ws = Workspace.shared
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 0) {
                VStack(spacing: 0) {
                    List(selection: $selection) {
                        ForEach(ws.environments) { environment in
                            HStack {
                                Text(environment.name).font(.app(12.5)).lineLimit(1)
                                Spacer()
                                if ws.activeEnvironmentID == environment.id {
                                    Text("ACTIVE").font(.app(9.5, .semibold)).foregroundStyle(Theme.accent)
                                }
                            }
                            .tag(environment.id)
                            .contextMenu {
                                Button("Duplicate") { ws.duplicateEnvironment(environment.id) }
                                Button("Export as Postman Environment…") { ws.exportEnvironment(environment.id) }
                                Divider()
                                Button("Delete…", role: .destructive) { ws.deleteEnvironment(environment.id) }
                            }
                        }
                    }
                    .listStyle(.sidebar)
                    Divider()
                    HStack(spacing: 2) {
                        IconButton(symbol: "plus", help: "New environment") { selection = ws.addEnvironment() }
                        IconButton(symbol: "minus", help: "Delete environment") {
                            if let id = selection { ws.deleteEnvironment(id) }
                        }
                        .disabled(selection == nil)
                        Spacer()
                    }
                    .padding(6)
                }
                .frame(width: 220)

                Divider()

                if let id = selection, ws.environments.contains(where: { $0.id == id }) {
                    EnvironmentEditor(environmentID: id)
                } else {
                    EmptyState(symbol: "square.stack.3d.up", title: "No environment selected",
                               message: "Environments hold values like {{baseUrl}} and {{token}}. Switch between them to point the same requests at a different server.") {
                        Button("New Environment") { selection = ws.addEnvironment() }
                            .buttonStyle(PrimaryButtonStyle(height: 28))
                    }
                }
            }

            Divider()
            HStack {
                Text("Use a variable anywhere as {{name}}: URLs, headers, bodies and auth.")
                    .font(.app(11.5))
                    .foregroundStyle(.secondary)
                Spacer()
                Button("Done") { dismiss() }
                    .buttonStyle(PrimaryButtonStyle(height: 30))
                    .keyboardShortcut(.cancelAction)
            }
            .padding(12)
        }
        .frame(width: 880, height: 580)
        .background(Theme.canvas)
        .onAppear {
            if selection == nil { selection = ws.activeEnvironmentID ?? ws.environments.first?.id }
        }
    }
}

private struct EnvironmentEditor: View {
    let environmentID: UUID
    private let ws = Workspace.shared

    var body: some View {
        let environment = ws.environments.first { $0.id == environmentID } ?? APIEnvironment()
        let active = ws.activeEnvironmentID == environmentID

        VStack(spacing: 0) {
            HStack(spacing: 10) {
                TextField("Environment name", text: Binding(
                    get: { environment.name },
                    set: { name in ws.updateEnvironment(environmentID) { $0.name = name } }
                ))
                .textFieldStyle(.plain)
                .font(.app(17, .semibold))

                Spacer()

                Button {
                    ws.activeEnvironmentID = active ? nil : environmentID
                } label: {
                    Label(active ? "Active" : "Set Active", systemImage: active ? "checkmark.circle.fill" : "circle")
                        .labelStyle(.titleAndIcon)
                        .foregroundStyle(active ? Theme.accent : Color.primary)
                }
                .buttonStyle(SecondaryButtonStyle(height: 28))

                Menu {
                    Button("Duplicate") { ws.duplicateEnvironment(environmentID) }
                    Button("Export as Postman Environment…") { ws.exportEnvironment(environmentID) }
                    Divider()
                    Button("Delete…", role: .destructive) { ws.deleteEnvironment(environmentID) }
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
                .menuStyle(.borderlessButton)
                .menuIndicator(.hidden)
                .fixedSize()
            }
            .padding(.horizontal, 16)
            .padding(.top, 14)
            .padding(.bottom, 2)

            KeyValueSection(
                caption: "Use the lock to mask a secret's value on screen.",
                rows: Binding(
                    get: { ws.environments.first { $0.id == environmentID }?.variables ?? [] },
                    set: { rows in ws.updateEnvironment(environmentID) { $0.variables = rows } }
                ),
                keyPlaceholder: "Variable",
                valuePlaceholder: "Value",
                allowsSecrets: true
            )
        }
    }
}

// MARK: - Collection & folder settings

private enum SettingsPage: Hashable { case general, auth, variables }

struct CollectionSettingsSheet: View {
    let collectionID: UUID
    private let ws = Workspace.shared
    @State private var page = SettingsPage.general
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        let collection = ws.collection(collectionID) ?? APICollection()
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                Image(systemName: "shippingbox.fill").foregroundStyle(Theme.accent)
                Text(collection.name).font(.app(16, .semibold)).lineLimit(1)
                Spacer()
            }
            .padding(.horizontal, 20)
            .padding(.top, 16)

            HStack {
                UnderlineTabs(items: [
                    TabItem(id: SettingsPage.general, title: "General"),
                    TabItem(id: .auth, title: "Auth", badge: collection.auth.type == .none ? nil : collection.auth.type.title),
                    TabItem(id: .variables, title: "Variables", badge: collection.variables.isEmpty ? nil : "\(collection.variables.count)"),
                ], selection: $page)
                Spacer()
            }
            .padding(.horizontal, 20)
            Divider()

            Group {
                switch page {
                case .general:
                    VStack(alignment: .leading, spacing: 14) {
                        LabeledRow("Name") {
                            TextField("Name", text: binding(\.name)).font(.app(13)).fieldChrome()
                        }
                        LabeledRow("Requests") {
                            Text("\(collection.items.requestCount)").font(.app(13))
                        }
                        Text("Description")
                            .font(.app(12, .medium))
                            .foregroundStyle(.secondary)
                        TextEditor(text: binding(\.docs))
                            .font(.app(12.5))
                            .scrollContentBackground(.hidden)
                            .padding(6)
                            .background(RoundedRectangle(cornerRadius: 8).fill(Theme.field))
                            .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.line))
                            .frame(minHeight: 120)
                        Button("Export as Postman Collection…") { ws.exportCollection(collectionID) }
                            .buttonStyle(SecondaryButtonStyle(height: 28))
                    }
                    .padding(20)
                case .auth:
                    VStack(alignment: .leading, spacing: 0) {
                        Text("Requests and folders set to “Inherit from Parent” use this.")
                            .font(.app(11.5))
                            .foregroundStyle(.secondary)
                            .padding(.horizontal, 16)
                            .padding(.top, 14)
                        AuthEditor(auth: binding(\.auth), allowsInherit: false)
                    }
                case .variables:
                    KeyValueSection(caption: "Available to every request here. An environment variable with the same name wins.",
                                    rows: binding(\.variables), keyPlaceholder: "Variable", valuePlaceholder: "Value",
                                    allowsSecrets: true)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)

            Divider()
            HStack {
                Spacer()
                Button("Done") { dismiss() }
                    .buttonStyle(PrimaryButtonStyle(height: 30))
                    .keyboardShortcut(.cancelAction)
            }
            .padding(12)
        }
        .frame(width: 720, height: 560)
        .background(Theme.canvas)
    }

    private func binding<T>(_ keyPath: WritableKeyPath<APICollection, T>) -> Binding<T> {
        Binding(
            get: { ws.collection(collectionID)?[keyPath: keyPath] ?? APICollection()[keyPath: keyPath] },
            set: { value in ws.updateCollection(collectionID) { $0[keyPath: keyPath] = value } }
        )
    }
}

struct FolderSettingsSheet: View {
    let collectionID: UUID
    let folderID: UUID
    private let ws = Workspace.shared
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        let collection = ws.collection(collectionID)
        let folder = collection?.items.item(folderID).flatMap { item -> Folder? in
            if case .folder(let f) = item { return f }
            return nil
        } ?? Folder()

        VStack(spacing: 0) {
            HStack(spacing: 8) {
                Image(systemName: "folder.fill").foregroundStyle(.secondary)
                Text(folder.name).font(.app(16, .semibold)).lineLimit(1)
                Spacer()
            }
            .padding(.horizontal, 20)
            .padding(.top, 16)
            .padding(.bottom, 12)
            Divider()

            VStack(alignment: .leading, spacing: 0) {
                LabeledRow("Name") {
                    TextField("Name", text: Binding(
                        get: { folder.name },
                        set: { name in ws.updateFolder(collection: collectionID, folder: folderID) { $0.name = name } }
                    ))
                    .font(.app(13))
                    .fieldChrome()
                }
                .padding(16)

                AuthEditor(
                    auth: Binding(
                        get: { folder.auth },
                        set: { auth in ws.updateFolder(collection: collectionID, folder: folderID) { $0.auth = auth } }
                    ),
                    allowsInherit: true,
                    inherited: parentAuth(collection)
                )
            }
            .frame(maxHeight: .infinity, alignment: .top)

            Divider()
            HStack {
                Text("Requests in this folder set to “Inherit” use this folder's auth.")
                    .font(.app(11.5))
                    .foregroundStyle(.secondary)
                Spacer()
                Button("Done") { dismiss() }
                    .buttonStyle(PrimaryButtonStyle(height: 30))
                    .keyboardShortcut(.cancelAction)
            }
            .padding(12)
        }
        .frame(width: 620, height: 440)
        .background(Theme.canvas)
    }

    private func parentAuth(_ collection: APICollection?) -> (auth: Auth, source: String?)? {
        guard let collection else { return nil }
        for parent in (collection.items.folderChain(to: folderID) ?? []).reversed() where parent.auth.type != .inherit {
            return (parent.auth, parent.name)
        }
        return (collection.auth, collection.name)
    }
}

// MARK: - Save request

struct SaveRequestSheet: View {
    let tabID: UUID
    private let ws = Workspace.shared
    @State private var name = ""
    @State private var destination: Destination?
    @Environment(\.dismiss) private var dismiss

    struct Destination: Hashable {
        let collection: UUID
        let folder: UUID?
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Save Request").font(.app(16, .semibold))

            LabeledRow("Name") {
                TextField("Request name", text: $name).font(.app(13)).fieldChrome()
            }

            Text("Save to")
                .font(.app(12, .medium))
                .foregroundStyle(.secondary)

            List(selection: $destination) {
                ForEach(ws.collections) { collection in
                    Label(collection.name, systemImage: "shippingbox")
                        .font(.app(12.5, .medium))
                        .tag(Destination(collection: collection.id, folder: nil))
                    ForEach(collection.items.folderPaths(), id: \.folder.id) { entry in
                        Label(entry.path, systemImage: "folder")
                            .font(.app(12.5))
                            .padding(.leading, 18)
                            .tag(Destination(collection: collection.id, folder: entry.folder.id))
                    }
                }
            }
            .listStyle(.bordered(alternatesRowBackgrounds: false))
            .frame(minHeight: 220)

            HStack {
                Button("New Collection") {
                    let id = ws.addCollection(named: "My Collection", rename: false)
                    destination = Destination(collection: id, folder: nil)
                }
                .buttonStyle(SecondaryButtonStyle(height: 30))
                Spacer()
                Button("Cancel") { dismiss() }
                    .buttonStyle(SecondaryButtonStyle(height: 30))
                    .keyboardShortcut(.cancelAction)
                Button("Save") {
                    guard let destination else { return }
                    ws.save(tabID, as: name, collectionID: destination.collection, folderID: destination.folder)
                    dismiss()
                }
                .buttonStyle(PrimaryButtonStyle(height: 30))
                .disabled(destination == nil || name.trimmingCharacters(in: .whitespaces).isEmpty)
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 480, height: 500)
        .background(Theme.canvas)
        .onAppear {
            name = ws.tab(tabID)?.draft.name ?? "New Request"
            destination = ws.collections.first.map { Destination(collection: $0.id, folder: nil) }
        }
    }
}

// MARK: - Import

struct ImportSheet: View {
    @State var pasteMode: Bool
    private let ws = Workspace.shared
    @Environment(\.dismiss) private var dismiss

    @State private var text = ""
    @State private var preview: Preview?
    @State private var error: String?
    @State private var fileName: String?
    @State private var targeted = false

    private enum Preview {
        case postman(ImportResult)
        case curl(APIRequest)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text("Import").font(.app(16, .semibold))
                Spacer()
                Picker("Source", selection: $pasteMode) {
                    Text("File").tag(false)
                    Text("Paste").tag(true)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 170)
                .onChange(of: pasteMode) { _, _ in
                    preview = nil
                    error = nil
                }
            }

            if pasteMode {
                Text("Paste a cURL command, or the JSON of a Postman collection or environment.")
                    .font(.app(12))
                    .foregroundStyle(.secondary)
                CodeEditor(text: $text, language: .plain, highlightVariables: false)
                    .frame(height: 230)
                    .background(Theme.field)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.line))
                    .onChange(of: text) { _, value in parse(text: value) }
            } else {
                dropZone
            }

            previewView

            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                    .buttonStyle(SecondaryButtonStyle(height: 30))
                    .keyboardShortcut(.cancelAction)
                Button(commitTitle) { commit() }
                    .buttonStyle(PrimaryButtonStyle(height: 30))
                    .disabled(preview == nil)
            }
        }
        .padding(20)
        .frame(width: 580)
        .background(Theme.canvas)
        .onAppear {
            if pasteMode, let clip = NSPasteboard.general.string(forType: .string),
               clip.trimmingCharacters(in: .whitespacesAndNewlines).lowercased().hasPrefix("curl") {
                text = clip
                parse(text: clip)
            }
        }
    }

    private var dropZone: some View {
        VStack(spacing: 10) {
            Image(systemName: "square.and.arrow.down.on.square")
                .font(.system(size: 30, weight: .light))
                .foregroundStyle(targeted ? Theme.accent : Color.secondary)
            Text(fileName ?? "Drop a file here")
                .font(.app(13, .semibold))
            Text("Postman collections (v2.0 and v2.1), environments and data dumps, Relay backups, or a text file containing a cURL command.")
                .font(.app(11.5))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 400)
            Button("Choose File…") {
                if let url = FilePanels.open() { load(url) }
            }
            .buttonStyle(SecondaryButtonStyle(height: 28))
        }
        .frame(maxWidth: .infinity)
        .frame(height: 230)
        .background(RoundedRectangle(cornerRadius: 10).fill(targeted ? Theme.accent.opacity(0.07) : Theme.field))
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .strokeBorder(targeted ? Theme.accent : Theme.line, style: StrokeStyle(lineWidth: 1.5, dash: [6, 4]))
        )
        .dropDestination(for: URL.self) { urls, _ in
            guard let url = urls.first else { return false }
            load(url)
            return true
        } isTargeted: { targeted = $0 }
    }

    @ViewBuilder private var previewView: some View {
        if let error {
            Label(error, systemImage: "exclamationmark.triangle.fill")
                .font(.app(12))
                .foregroundStyle(Theme.amber)
        } else if let preview {
            VStack(alignment: .leading, spacing: 6) {
                switch preview {
                case .curl(let request):
                    HStack(spacing: 8) {
                        MethodBadge(method: request.method, size: 12)
                        Text(request.url).font(.code(12)).lineLimit(1).truncationMode(.middle)
                    }
                    Text(curlSummary(request))
                        .font(.app(11.5))
                        .foregroundStyle(.secondary)
                case .postman(let result):
                    ForEach(result.collections) { c in
                        Label("\(c.name): \(c.items.requestCount) requests", systemImage: "shippingbox")
                            .font(.app(12.5))
                    }
                    ForEach(result.environments) { e in
                        Label("\(e.name): \(e.variables.count) variables", systemImage: "square.stack.3d.up")
                            .font(.app(12.5))
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(12)
            .background(RoundedRectangle(cornerRadius: 8).fill(Theme.field))
        }
    }

    private var commitTitle: String {
        if case .curl = preview { return "Open in New Tab" }
        return "Import"
    }

    private func curlSummary(_ request: APIRequest) -> String {
        var parts = ["\(request.headers.count) header\(request.headers.count == 1 ? "" : "s")"]
        if request.body.mode != .none { parts.append("\(request.body.mode.title) body") }
        if request.auth.type != .inherit { parts.append(request.auth.type.title) }
        return parts.joined(separator: " · ")
    }

    private func load(_ url: URL) {
        fileName = url.lastPathComponent
        guard let data = try? Data(contentsOf: url) else {
            preview = nil
            error = "Couldn't read \(url.lastPathComponent)."
            return
        }
        if let string = String(data: data, encoding: .utf8), isCurl(string) {
            parse(text: string)
        } else {
            parse(data: data)
        }
    }

    private func isCurl(_ text: String) -> Bool {
        let lower = text.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        return lower.hasPrefix("curl") || lower.hasPrefix("$ curl")
    }

    private func parse(text: String) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            preview = nil
            error = nil
            return
        }
        if isCurl(trimmed) {
            do {
                preview = .curl(try CurlParser.parse(trimmed))
                error = nil
            } catch {
                preview = nil
                self.error = error.localizedDescription
            }
        } else {
            parse(data: Data(trimmed.utf8))
        }
    }

    private func parse(data: Data) {
        do {
            let result = try Postman.importData(data)
            if result.isEmpty {
                preview = nil
                error = "There's nothing to import in this file."
            } else {
                preview = .postman(result)
                error = nil
            }
        } catch {
            preview = nil
            self.error = error.localizedDescription
        }
    }

    private func commit() {
        switch preview {
        case .curl(let request):     ws.newTab(request, unsaved: true)
        case .postman(let result):   ws.importItems(result)
        case nil:                    return
        }
        dismiss()
    }
}
