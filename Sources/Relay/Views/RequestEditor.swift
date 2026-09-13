import AppKit
import SwiftUI

struct RequestEditor: View {
    let tab: RequestTab
    private let ws = Workspace.shared

    var body: some View {
        let draft = tab.draft
        let section = Binding(
            get: { ws.editorSections[tab.id] ?? (draft.body.mode != .none ? .body : .params) },
            set: { ws.editorSections[tab.id] = $0 }
        )
        VStack(spacing: 0) {
            HStack {
                UnderlineTabs(items: tabItems(draft), selection: section)
                Spacer()
            }
            .padding(.horizontal, 16)
            Divider()
            Group {
                switch section.wrappedValue {
                case .params:  ParamsSection(tabID: tab.id)
                case .headers: HeadersSection(tab: tab)
                case .body:    BodyEditor(tabID: tab.id)
                case .auth:    RequestAuthSection(tab: tab)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        }
        .background(Theme.canvas)
    }

    private func tabItems(_ d: APIRequest) -> [TabItem<EditorSection>] {
        func count(_ rows: [KeyValue]) -> String? {
            let n = rows.filter { $0.enabled && !$0.isBlank }.count
            return n == 0 ? nil : "\(n)"
        }
        let authBadge: String?
        switch d.auth.type {
        case .bearer: authBadge = "Bearer"
        case .basic:  authBadge = "Basic"
        case .apiKey: authBadge = "Key"
        case .inherit, .none: authBadge = nil
        }
        return [
            TabItem(id: .params, title: "Params", badge: count(d.params)),
            TabItem(id: .headers, title: "Headers", badge: count(d.headers)),
            TabItem(id: .body, title: "Body", badge: d.body.mode == .none ? nil : d.body.mode.chipTitle),
            TabItem(id: .auth, title: "Auth", badge: authBadge),
        ]
    }
}

extension BodyMode {
    var chipTitle: String {
        switch self {
        case .none:      return "None"
        case .json:      return "JSON"
        case .text:      return "Text"
        case .xml:       return "XML"
        case .form:      return "Form"
        case .multipart: return "Multipart"
        case .file:      return "File"
        }
    }
}

extension Theme {
    static func variableNSColor(_ source: VariableScope.Source) -> NSColor {
        switch source {
        case .environment: return .dynamic(light: 0x15803D, dark: 0x4ADE80)
        case .collection:  return .dynamic(light: 0x1D4ED8, dark: 0x60A5FA)
        case .dynamic:     return .dynamic(light: 0x7E22CE, dark: 0xC084FC)
        case .missing:     return .dynamic(light: 0xB91C1C, dark: 0xF87171)
        }
    }
}

// MARK: - Key/value sections

/// A caption, a Bulk Edit toggle and a key/value table.
struct KeyValueSection<Accessory: View>: View {
    let caption: String
    @Binding var rows: [KeyValue]
    var keyPlaceholder = "Key"
    var valuePlaceholder = "Value"
    var allowsFiles = false
    var allowsSecrets = false
    @ViewBuilder var accessory: () -> Accessory

    @State private var bulk = false
    @State private var bulkText = ""

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                Text(caption)
                    .font(.app(11.5))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                Spacer(minLength: 8)
                accessory()
                if !allowsFiles {
                    Button(bulk ? "Table" : "Bulk Edit") {
                        if !bulk { bulkText = BulkEdit.text(rows) }
                        bulk.toggle()
                    }
                    .buttonStyle(.plain)
                    .font(.app(11.5, .medium))
                    .foregroundStyle(Theme.accent)
                    .help(bulk ? "Back to the table" : "Edit as text, one “key: value” per line")
                }
            }
            .padding(.horizontal, 16)
            .padding(.top, 10)
            .padding(.bottom, 8)

            if bulk {
                CodeEditor(text: $bulkText, language: .plain)
                    .background(Theme.field.opacity(0.6))
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.line))
                    .padding(.horizontal, 16)
                    .padding(.bottom, 16)
                    .onChange(of: bulkText) { _, text in
                        rows = BulkEdit.parse(text, previous: rows)
                    }
            } else {
                ScrollView {
                    KeyValueTable(rows: $rows, keyPlaceholder: keyPlaceholder, valuePlaceholder: valuePlaceholder,
                                  allowsFiles: allowsFiles, allowsSecrets: allowsSecrets)
                        .padding(.horizontal, 16)
                        .padding(.bottom, 16)
                }
            }
        }
    }
}

extension KeyValueSection where Accessory == EmptyView {
    init(caption: String, rows: Binding<[KeyValue]>, keyPlaceholder: String = "Key", valuePlaceholder: String = "Value",
         allowsFiles: Bool = false, allowsSecrets: Bool = false) {
        self.init(caption: caption, rows: rows, keyPlaceholder: keyPlaceholder, valuePlaceholder: valuePlaceholder,
                  allowsFiles: allowsFiles, allowsSecrets: allowsSecrets) { EmptyView() }
    }
}

/// "key: value" per line; a leading `//` marks a disabled row.
enum BulkEdit {
    static func text(_ rows: [KeyValue]) -> String {
        rows.filter { !$0.isBlank }
            .map { ($0.enabled ? "" : "// ") + $0.key + ": " + $0.value }
            .joined(separator: "\n")
    }

    static func parse(_ text: String, previous: [KeyValue]) -> [KeyValue] {
        var rows: [KeyValue] = []
        for line in text.components(separatedBy: .newlines) {
            var content = line.trimmingCharacters(in: .whitespaces)
            guard !content.isEmpty else { continue }
            var enabled = true
            if content.hasPrefix("//") {
                enabled = false
                content = content.dropFirst(2).trimmingCharacters(in: .whitespaces)
            }
            var row = rows.count < previous.count ? previous[rows.count] : KeyValue()
            if let colon = content.firstIndex(of: ":") {
                row.key = content[..<colon].trimmingCharacters(in: .whitespaces)
                row.value = content[content.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            } else {
                row.key = content
                row.value = ""
            }
            row.enabled = enabled
            rows.append(row)
        }
        return rows
    }
}

private struct ParamsSection: View {
    let tabID: UUID
    private let ws = Workspace.shared

    var body: some View {
        KeyValueSection(
            caption: "Query parameters. They stay in sync with the URL.",
            rows: Binding(
                get: { ws.tab(tabID)?.draft.params ?? [] },
                set: { rows in
                    ws.updateDraft(tabID) {
                        $0.params = rows
                        $0.url = URLQuery.url($0.url, applying: rows)
                    }
                }
            ),
            keyPlaceholder: "Parameter",
            valuePlaceholder: "Value"
        )
    }
}

private struct HeadersSection: View {
    let tab: RequestTab
    private let ws = Workspace.shared

    private static let presets: [(String, String)] = [
        ("Content-Type", "application/json"),
        ("Accept", "application/json"),
        ("Authorization", "Bearer {{token}}"),
        ("Cache-Control", "no-cache"),
        ("User-Agent", "Relay/1.0"),
        ("X-Request-ID", "{{$guid}}"),
    ]

    var body: some View {
        VStack(spacing: 0) {
            KeyValueSection(caption: "Headers sent with the request.", rows: ws.binding(tab.id, \.headers),
                            keyPlaceholder: "Header", valuePlaceholder: "Value") {
                Menu {
                    ForEach(Self.presets, id: \.0) { preset in
                        Button("\(preset.0): \(preset.1)") {
                            ws.updateDraft(tab.id) { $0.headers.append(KeyValue(preset.0, preset.1)) }
                        }
                    }
                } label: {
                    Text("Add Common")
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
                .font(.app(11.5, .medium))
            }

            let implicit = implicitHeaders
            if !implicit.isEmpty {
                VStack(alignment: .leading, spacing: 4) {
                    Text("ADDED AUTOMATICALLY")
                        .font(.app(10, .semibold))
                        .foregroundStyle(.tertiary)
                    ForEach(implicit, id: \.name) { header in
                        HStack(spacing: 6) {
                            Text("\(header.name):").font(.code(11, .semibold)).foregroundStyle(.secondary)
                            Text(header.value).font(.code(11)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                            Text("· \(header.source)").font(.app(10.5)).foregroundStyle(.tertiary)
                        }
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 16)
                .padding(.vertical, 10)
                .background(Theme.field.opacity(0.5))
            }
        }
    }

    /// Headers Relay adds that aren't in the table, so nothing on the wire is a surprise.
    private var implicitHeaders: [(name: String, value: String, source: String)] {
        let explicit = Set(tab.draft.headers.filter { $0.enabled && !$0.key.isEmpty }.map { $0.key.lowercased() })
        var result = ws.prepared(tab).wireHeaders
            .filter { !explicit.contains($0.name.lowercased()) }
            .map { header -> (name: String, value: String, source: String) in
                let isContentType = header.name.caseInsensitiveCompare("Content-Type") == .orderedSame
                let masked = isContentType ? header.value : String(repeating: "•", count: 8)
                return (header.name, masked, isContentType ? "from Body" : "from Auth")
            }
        if tab.draft.body.mode == .multipart, !explicit.contains("content-type") {
            result.append(("Content-Type", "multipart/form-data; boundary=…", "from Body"))
        }
        if !explicit.contains("user-agent") {
            result.append(("User-Agent", "Relay/1.0", "default"))
        }
        return result
    }
}

// MARK: - Body

private struct BodyEditor: View {
    let tabID: UUID
    private let ws = Workspace.shared

    var body: some View {
        let tab = ws.tab(tabID)
        let request = tab?.draft ?? APIRequest()
        let mode = request.body.mode

        VStack(spacing: 0) {
            HStack(spacing: 4) {
                ForEach(BodyMode.allCases) { option in
                    ModeChip(title: option.chipTitle, selected: option == mode) {
                        ws.updateDraft(tabID) { $0.body.mode = option }
                    }
                }
                Spacer(minLength: 8)
                if mode == .json {
                    JSONStatus(text: request.body.raw)
                    Button("Beautify") { beautify(request.body.raw, xml: false) }
                        .buttonStyle(SecondaryButtonStyle(height: 24))
                } else if mode == .xml {
                    Button("Beautify") { beautify(request.body.raw, xml: true) }
                        .buttonStyle(SecondaryButtonStyle(height: 24))
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 8)

            switch mode {
            case .none:
                EmptyState(symbol: "doc", title: "No body",
                           message: "This request sends no body. Choose JSON, a form or a file above to add one.")
            case .json, .text, .xml:
                let scope = tab.map { ws.scope(for: $0) } ?? VariableScope()
                CodeEditor(text: ws.binding(tabID, \.body.raw),
                           language: mode == .json ? .json : (mode == .xml ? .xml : .plain),
                           variableColor: { Theme.variableNSColor(scope.source(of: $0)) })
                    .background(Theme.field.opacity(0.5))
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.line))
                    .padding(.horizontal, 16)
                    .padding(.bottom, 16)
            case .form:
                KeyValueSection(caption: "Sent as application/x-www-form-urlencoded.",
                                rows: ws.binding(tabID, \.body.form), keyPlaceholder: "Field")
            case .multipart:
                KeyValueSection(caption: "Sent as multipart/form-data. Use the file icon to upload a file.",
                                rows: ws.binding(tabID, \.body.form), keyPlaceholder: "Field", allowsFiles: true)
            case .file:
                FileBodyPicker(path: ws.binding(tabID, \.body.filePath))
            }
        }
    }

    private func beautify(_ raw: String, xml: Bool) {
        guard let pretty = xml ? XMLFormat.pretty(raw) : JSONFormat.pretty(raw) else {
            ws.flash(xml ? "The body isn't well-formed XML" : "The body isn't valid JSON")
            return
        }
        ws.updateDraft(tabID) { $0.body.raw = pretty }
    }
}

private struct ModeChip: View {
    let title: String
    let selected: Bool
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Text(title)
                .font(.app(11.5, selected ? .semibold : .medium))
                .foregroundStyle(selected ? Theme.accent : Color.secondary)
                .padding(.horizontal, 9)
                .frame(height: 24)
                .background(RoundedRectangle(cornerRadius: 6).fill(selected ? Theme.accent.opacity(0.12) : (hovering ? Theme.hover : .clear)))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

private struct JSONStatus: View {
    let text: String

    var body: some View {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.isEmpty {
            // Unquoted {{vars}} are valid once substituted, so judge the JSON around them.
            let probe = trimmed.replacingOccurrences(of: #"\{\{[^{}]*\}\}"#, with: "0", options: .regularExpression)
            let valid = JSONFormat.isJSON(probe)
            Label(valid ? "Valid JSON" : "Invalid JSON", systemImage: valid ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                .font(.app(11, .medium))
                .foregroundStyle(valid ? Theme.green : Theme.amber)
                .padding(.trailing, 4)
        }
    }
}

private struct FileBodyPicker: View {
    @Binding var path: String

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                Image(systemName: path.isEmpty ? "doc" : "doc.fill")
                    .foregroundStyle(path.isEmpty ? Color.secondary : Theme.accent)
                Text(path.isEmpty ? "No file chosen" : path)
                    .font(.code(12))
                    .foregroundStyle(path.isEmpty ? .secondary : .primary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer()
                Button("Choose File…") {
                    if let url = FilePanels.open() { path = url.path }
                }
                .buttonStyle(SecondaryButtonStyle(height: 26))
            }
            .padding(.horizontal, 12)
            .frame(height: 44)
            .background(RoundedRectangle(cornerRadius: 8).fill(Theme.field))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.line))
            Text("The file's bytes are sent as the body, unchanged.")
                .font(.app(11.5))
                .foregroundStyle(.secondary)
        }
        .padding(16)
    }
}

// MARK: - Auth

private struct RequestAuthSection: View {
    let tab: RequestTab
    private let ws = Workspace.shared

    var body: some View {
        var parentView = tab
        parentView.draft.auth = .inherit
        let inherited = ws.effectiveAuth(for: parentView)
        return AuthEditor(auth: ws.binding(tab.id, \.auth), allowsInherit: true, inherited: inherited,
                          isScratch: tab.collectionID == nil)
    }
}

struct AuthEditor: View {
    @Binding var auth: Auth
    var allowsInherit = true
    var inherited: (auth: Auth, source: String?)?
    var isScratch = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                LabeledRow("Type") {
                    Picker("Type", selection: $auth.type) {
                        ForEach(AuthType.allCases.filter { allowsInherit || $0 != .inherit }) { type in
                            Text(type.title).tag(type)
                        }
                    }
                    .labelsHidden()
                    .frame(width: 230)
                }

                switch auth.type {
                case .inherit:
                    Note(inheritDescription)
                case .none:
                    Note("No credentials are added.")
                case .bearer:
                    LabeledRow("Token") { SecretField(placeholder: "Token or {{variable}}", text: $auth.token) }
                    Note("Sent as the header “Authorization: Bearer <token>”.")
                case .basic:
                    LabeledRow("Username") {
                        VariableTextField(text: $auth.username, placeholder: "Username").fieldChrome()
                    }
                    LabeledRow("Password") { SecretField(placeholder: "Password", text: $auth.password) }
                    Note("Sent as “Authorization: Basic <base64 of username:password>”.")
                case .apiKey:
                    LabeledRow("Key") {
                        VariableTextField(text: $auth.key, placeholder: "X-API-Key").fieldChrome()
                    }
                    LabeledRow("Value") { SecretField(placeholder: "Value or {{variable}}", text: $auth.value) }
                    LabeledRow("Add to") {
                        Picker("Add to", selection: $auth.location) {
                            ForEach(APIKeyLocation.allCases) { Text($0.title).tag($0) }
                        }
                        .pickerStyle(.segmented)
                        .labelsHidden()
                        .frame(width: 230)
                    }
                }
            }
            .padding(16)
            .frame(maxWidth: 640, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private var inheritDescription: String {
        if isScratch {
            return "This request isn't in a collection, so there's nothing to inherit and no auth is sent."
        }
        guard let inherited else { return "Uses the auth of the folder or collection it's in." }
        let source = inherited.source.map { "“\($0)”" } ?? "the parent"
        switch inherited.auth.type {
        case .none, .inherit: return "Inherits from \(source), which sends no auth."
        default:              return "Uses \(inherited.auth.type.title) from \(source)."
        }
    }
}

struct LabeledRow<Content: View>: View {
    let label: String
    @ViewBuilder var content: () -> Content

    init(_ label: String, @ViewBuilder content: @escaping () -> Content) {
        self.label = label
        self.content = content
    }

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            Text(label)
                .font(.app(12, .medium))
                .foregroundStyle(.secondary)
                .frame(width: 84, alignment: .leading)
            content()
        }
    }
}

private struct Note: View {
    let text: String

    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text)
            .font(.app(11.5))
            .foregroundStyle(.secondary)
            .padding(.leading, 96)
            .fixedSize(horizontal: false, vertical: true)
    }
}
