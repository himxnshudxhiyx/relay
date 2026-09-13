import SwiftUI

struct RootView: View {
    @Bindable private var ws = Workspace.shared

    var body: some View {
        NavigationSplitView {
            SidebarView()
                .navigationSplitViewColumnWidth(min: 240, ideal: 280, max: 440)
        } detail: {
            VStack(spacing: 0) {
                if !ws.tabs.isEmpty {
                    TabStripView()
                    Divider()
                }
                if let id = ws.selectedTabID, ws.tab(id) != nil {
                    // A fresh view per tab, so editors don't carry undo history or scroll position across tabs.
                    RequestView(tabID: id).id(id)
                } else {
                    WelcomeView()
                }
            }
            .background(Theme.canvas)
            .toolbar {
                ToolbarItemGroup(placement: .primaryAction) {
                    VariablesButton()
                    EnvironmentMenu()
                    LayoutButton()
                    AppearanceMenu()
                }
            }
        }
        .font(.app(13))
        .tint(Theme.accent)
        .sheet(item: $ws.sheet) { sheet in
            sheetView(sheet)
                .font(.app(13))
                .tint(Theme.accent)
        }
        .overlay(alignment: .bottom) {
            if let toast = ws.toast {
                Text(toast)
                    .font(.app(12.5, .medium))
                    .padding(.horizontal, 16)
                    .padding(.vertical, 9)
                    .background(.regularMaterial, in: Capsule())
                    .overlay(Capsule().stroke(Theme.line))
                    .shadow(color: .black.opacity(0.12), radius: 10, y: 4)
                    .padding(.bottom, 24)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
                    .allowsHitTesting(false)
            }
        }
        .animation(.spring(duration: 0.28), value: ws.toast)
    }

    @ViewBuilder
    private func sheetView(_ sheet: ActiveSheet) -> some View {
        switch sheet {
        case .importer(let paste):             ImportSheet(pasteMode: paste)
        case .environments(let selected):      EnvironmentsSheet(selection: selected)
        case .collectionSettings(let id):      CollectionSettingsSheet(collectionID: id)
        case .folderSettings(let c, let f):    FolderSettingsSheet(collectionID: c, folderID: f)
        case .saveRequest(let tab):            SaveRequestSheet(tabID: tab)
        }
    }
}

// MARK: - Toolbar

private struct EnvironmentMenu: View {
    @Bindable private var ws = Workspace.shared

    var body: some View {
        Menu {
            Picker("Environment", selection: $ws.activeEnvironmentID) {
                Text("No Environment").tag(UUID?.none)
                ForEach(ws.environments) { environment in
                    Text(environment.name).tag(UUID?.some(environment.id))
                }
            }
            .pickerStyle(.inline)
            Divider()
            Button("Manage Environments…") { ws.sheet = .environments(selected: ws.activeEnvironmentID) }
        } label: {
            Label(ws.activeEnvironment?.name ?? "No Environment", systemImage: "square.stack.3d.up")
                .labelStyle(.titleAndIcon)
        }
        .help("Active environment: variables like {{baseUrl}} come from here")
    }
}

private struct LayoutButton: View {
    @AppStorage("layout") private var layout = PaneLayout.stacked

    var body: some View {
        Button {
            layout = layout == .stacked ? .sideBySide : .stacked
        } label: {
            Label("Layout", systemImage: layout == .stacked ? "rectangle.split.2x1" : "rectangle.split.1x2")
        }
        .help(layout == .stacked ? "Show the response beside the request (⌘\\)" : "Show the response below the request (⌘\\)")
    }
}

private struct VariablesButton: View {
    @State private var open = false

    var body: some View {
        Button { open.toggle() } label: {
            Label("Variables", systemImage: "eye")
        }
        .help("Quick look at the variables in use")
        .popover(isPresented: $open, arrowEdge: .bottom) {
            VariablesPopover(close: { open = false })
                .frame(width: 480, height: 400)
                .font(.app(13))
                .tint(Theme.accent)
        }
    }
}

private struct VariablesPopover: View {
    let close: () -> Void
    private let ws = Workspace.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if let environment = ws.activeEnvironment {
                HStack {
                    VStack(alignment: .leading, spacing: 0) {
                        Text("ENVIRONMENT").font(.app(10, .semibold)).foregroundStyle(.tertiary)
                        Text(environment.name).font(.app(14, .semibold))
                    }
                    Spacer()
                    Button("Edit All…") {
                        close()
                        ws.sheet = .environments(selected: environment.id)
                    }
                    .buttonStyle(SecondaryButtonStyle(height: 26))
                }
                .padding(12)
                ScrollView {
                    KeyValueTable(
                        rows: Binding(
                            get: { ws.activeEnvironment?.variables ?? [] },
                            set: { rows in ws.updateEnvironment(environment.id) { $0.variables = rows } }
                        ),
                        keyPlaceholder: "Variable", valuePlaceholder: "Value", allowsSecrets: true
                    )
                    .padding(.horizontal, 12)
                    .padding(.bottom, 12)
                }
            } else {
                EmptyState(symbol: "square.stack.3d.up", title: "No active environment",
                           message: "Pick one from the environment menu in the toolbar, or create one.") {
                    Button("Manage Environments…") {
                        close()
                        ws.sheet = .environments(selected: nil)
                    }
                    .buttonStyle(SecondaryButtonStyle(height: 28))
                }
            }

            if let tab = ws.selectedTab, let collection = ws.collection(tab.collectionID), !collection.variables.isEmpty {
                Divider()
                VStack(alignment: .leading, spacing: 4) {
                    HStack {
                        Text("COLLECTION · \(collection.name)").font(.app(10, .semibold)).foregroundStyle(.tertiary)
                        Spacer()
                        Button("Edit…") {
                            close()
                            ws.sheet = .collectionSettings(collection.id)
                        }
                        .buttonStyle(.plain)
                        .font(.app(11.5, .medium))
                        .foregroundStyle(Theme.accent)
                    }
                    ForEach(collection.variables.filter { !$0.isBlank }) { variable in
                        HStack(spacing: 8) {
                            Text(variable.key).font(.code(11.5, .semibold)).foregroundStyle(Theme.blue)
                            Text(variable.isSecret ? "••••••" : variable.value)
                                .font(.code(11.5))
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                        }
                        .opacity(variable.enabled ? 1 : 0.5)
                    }
                }
                .padding(12)
            }
        }
    }
}

// MARK: - Welcome

private struct WelcomeView: View {
    private let ws = Workspace.shared

    var body: some View {
        VStack(spacing: 28) {
            VStack(spacing: 10) {
                AppGlyph(size: 68)
                Text("Relay").font(.app(28, .bold))
                Text("Build a request, send it, read the response.")
                    .font(.app(13))
                    .foregroundStyle(.secondary)
            }

            HStack(spacing: 14) {
                WelcomeAction(symbol: "plus.square.on.square", title: "New Request", keys: "⌘T") { ws.newTab() }
                WelcomeAction(symbol: "square.and.arrow.down", title: "Import Collection", keys: "⌘O") { ws.sheet = .importer(paste: false) }
                WelcomeAction(symbol: "terminal", title: "Paste cURL", keys: "⇧⌘I") { ws.sheet = .importer(paste: true) }
            }

            if !ws.history.isEmpty {
                VStack(alignment: .leading, spacing: 4) {
                    Text("RECENT")
                        .font(.app(10.5, .semibold))
                        .foregroundStyle(.tertiary)
                        .padding(.leading, 10)
                    ForEach(ws.history.prefix(5)) { entry in
                        RecentRow(entry: entry)
                    }
                }
                .frame(width: 540)
            }
        }
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

private struct RecentRow: View {
    let entry: HistoryEntry
    private let ws = Workspace.shared
    @State private var hovering = false

    var body: some View {
        Button { ws.openHistory(entry) } label: {
            HStack(spacing: 10) {
                MethodBadge(method: entry.request.method, size: 10.5)
                    .frame(width: 48, alignment: .leading)
                Text(entry.request.url)
                    .font(.code(12))
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer()
                if let status = entry.status {
                    Text("\(status)").font(.app(11.5, .semibold)).foregroundStyle(Theme.status(status))
                }
            }
            .padding(.horizontal, 10)
            .frame(height: 30)
            .background(RoundedRectangle(cornerRadius: 7).fill(hovering ? Theme.hover : .clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

private struct WelcomeAction: View {
    let symbol: String
    let title: String
    let keys: String
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            VStack(spacing: 10) {
                Image(systemName: symbol)
                    .font(.system(size: 22, weight: .regular))
                    .foregroundStyle(Theme.accent)
                Text(title).font(.app(12.5, .semibold))
                KeyHint(keys: keys)
            }
            .frame(width: 160, height: 124)
            .background(RoundedRectangle(cornerRadius: 12).fill(hovering ? Theme.accent.opacity(0.06) : Theme.panel))
            .overlay(RoundedRectangle(cornerRadius: 12).stroke(hovering ? Theme.accent.opacity(0.4) : Theme.line))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

/// The app icon, drawn in SwiftUI to match tools/MakeIcon.swift.
struct AppGlyph: View {
    let size: CGFloat

    var body: some View {
        RoundedRectangle(cornerRadius: size * 0.24, style: .continuous)
            .fill(LinearGradient(colors: [Color(nsColor: NSColor(hex: 0x7C6CFF)), Color(nsColor: NSColor(hex: 0x4338CA))],
                                 startPoint: .topLeading, endPoint: .bottomTrailing))
            .frame(width: size, height: size)
            .overlay(
                Image(systemName: "arrow.left.arrow.right")
                    .font(.system(size: size * 0.42, weight: .semibold))
                    .foregroundStyle(.white)
            )
            .shadow(color: Color(nsColor: NSColor(hex: 0x4338CA)).opacity(0.3), radius: 10, y: 5)
    }
}
