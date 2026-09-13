import AppKit
import SwiftUI

struct RequestView: View {
    let tabID: UUID
    private let ws = Workspace.shared
    @AppStorage("layout") private var layout = PaneLayout.stacked

    var body: some View {
        if let tab = ws.tab(tabID) {
            VStack(spacing: 0) {
                RequestHeader(tab: tab)
                URLBar(tabID: tabID)
                    .padding(.horizontal, 16)
                    .padding(.bottom, 10)
                VariableHints(tab: tab)
                Divider()
                if layout == .sideBySide {
                    HSplitView {
                        RequestEditor(tab: tab)
                            .frame(minWidth: 340, maxWidth: .infinity, maxHeight: .infinity)
                        ResponsePanel(tabID: tabID)
                            .frame(minWidth: 340, maxWidth: .infinity, maxHeight: .infinity)
                    }
                } else {
                    VSplitView {
                        RequestEditor(tab: tab)
                            .frame(maxWidth: .infinity, minHeight: 170, maxHeight: .infinity)
                        ResponsePanel(tabID: tabID)
                            .frame(maxWidth: .infinity, minHeight: 200, maxHeight: .infinity)
                    }
                }
            }
        }
    }
}

private struct RequestHeader: View {
    let tab: RequestTab
    private let ws = Workspace.shared

    var body: some View {
        let crumbs = ws.breadcrumb(for: tab)
        HStack(alignment: .center, spacing: 10) {
            VStack(alignment: .leading, spacing: 1) {
                if crumbs.isEmpty {
                    Label("Not saved to a collection", systemImage: "circle.dashed")
                        .font(.app(11))
                        .foregroundStyle(.secondary)
                } else {
                    Text(crumbs.joined(separator: "  ›  "))
                        .font(.app(11))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                TextField("Request name", text: ws.binding(tab.id, \.name))
                    .textFieldStyle(.plain)
                    .font(.app(16, .semibold))
            }

            Spacer(minLength: 12)

            Menu {
                Button("Copy cURL") { ws.copy(ws.curl(for: tab), "cURL") }
                Button("Copy cURL, keeping {{variables}}") { ws.copy(ws.curl(for: tab, resolveVariables: false), "cURL") }
                Divider()
                Button("Replace with cURL from Clipboard") { replaceFromClipboard() }
            } label: {
                Label("cURL", systemImage: "terminal")
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .help("Copy this request as a cURL command (⇧⌘C)")

            Button {
                ws.save(tab.id)
            } label: {
                Label(tab.collectionID == nil ? "Save…" : "Save", systemImage: "square.and.arrow.down")
                    .labelStyle(.titleAndIcon)
            }
            .buttonStyle(SecondaryButtonStyle(height: 30))
            .disabled(tab.collectionID != nil && !tab.isDirty)
            .help("Save (⌘S)")
        }
        .padding(.horizontal, 16)
        .padding(.top, 12)
        .padding(.bottom, 10)
    }

    private func replaceFromClipboard() {
        guard let text = NSPasteboard.general.string(forType: .string) else {
            ws.flash("The clipboard has no text")
            return
        }
        do {
            ws.apply(curl: try CurlParser.parse(text), to: tab.id)
        } catch {
            ws.flash(error.localizedDescription)
        }
    }
}

struct URLBar: View {
    let tabID: UUID
    private let ws = Workspace.shared
    @State private var focused = false

    var body: some View {
        let loading = ws.responses[tabID]?.isLoading ?? false
        HStack(spacing: 8) {
            HStack(spacing: 0) {
                MethodPicker(method: ws.binding(tabID, \.method))
                Rectangle().fill(Theme.line).frame(width: 1, height: 20)
                VariableTextField(
                    text: urlBinding,
                    placeholder: "Enter a URL, or paste a cURL command",
                    fontSize: 13,
                    focusRequest: ws.urlFocusRequest,
                    focusOnAppear: ws.tab(tabID)?.draft.url.isEmpty == true,
                    onPasteCurl: { pasted in
                        guard let parsed = Self.parseCurl(pasted) else { return false }
                        ws.apply(curl: parsed, to: tabID)
                        return true
                    },
                    onSubmit: { ws.send(tabID) },
                    onFocusChange: { focused = $0 }
                )
                .padding(.horizontal, 10)
            }
            .frame(height: 36)
            .background(RoundedRectangle(cornerRadius: 8).fill(Theme.field))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(focused ? Theme.accent.opacity(0.6) : Theme.line))

            if loading {
                Button { ws.cancel(tabID) } label: {
                    Text("Cancel").frame(width: 70)
                }
                .buttonStyle(SecondaryButtonStyle(height: 36))
                .help("Cancel (⌘.)")
            } else {
                Button { ws.send(tabID) } label: {
                    HStack(spacing: 6) {
                        Text("Send")
                        Image(systemName: "paperplane.fill").font(.system(size: 11))
                    }
                    .frame(width: 70)
                }
                .buttonStyle(PrimaryButtonStyle(height: 36))
                .help("Send (⌘↩)")
            }
        }
    }

    private var urlBinding: Binding<String> {
        Binding(
            get: { ws.tab(tabID)?.draft.url ?? "" },
            set: { newValue in
                ws.updateDraft(tabID) {
                    $0.url = newValue
                    $0.params = URLQuery.params(fromURL: newValue, previous: $0.params)
                }
            }
        )
    }

    static func parseCurl(_ text: String) -> APIRequest? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let lower = trimmed.lowercased()
        guard lower.hasPrefix("curl ") || lower.hasPrefix("curl.exe ") || lower.hasPrefix("$ curl ") else { return nil }
        // A single-line field can flatten the command's "\⏎" continuations into "\ ".
        let flattened = trimmed
            .replacingOccurrences(of: #"\\[ \t]*\r?\n"#, with: " ", options: .regularExpression)
            .replacingOccurrences(of: #"\s\\\s+(?=-)"#, with: " ", options: .regularExpression)
        return try? CurlParser.parse(flattened)
    }
}

private struct MethodPicker: View {
    @Binding var method: String
    @State private var open = false

    var body: some View {
        Button { open.toggle() } label: {
            HStack(spacing: 5) {
                Text(method)
                    .font(.code(12.5, .bold))
                    .foregroundStyle(Theme.method(method))
                Image(systemName: "chevron.down")
                    .font(.system(size: 8, weight: .bold))
                    .foregroundStyle(.tertiary)
            }
            .frame(width: 96, height: 36)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .popover(isPresented: $open, arrowEdge: .bottom) {
            VStack(alignment: .leading, spacing: 1) {
                ForEach(APIRequest.methods, id: \.self) { option in
                    MethodOption(method: option, selected: option == method) {
                        method = option
                        open = false
                    }
                }
            }
            .padding(5)
            .frame(width: 150)
        }
    }
}

private struct MethodOption: View {
    let method: String
    let selected: Bool
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack {
                Text(method)
                    .font(.code(12.5, .bold))
                    .foregroundStyle(Theme.method(method))
                Spacer()
                if selected {
                    Image(systemName: "checkmark").font(.system(size: 10, weight: .bold)).foregroundStyle(.secondary)
                }
            }
            .padding(.horizontal, 10)
            .frame(height: 26)
            .background(RoundedRectangle(cornerRadius: 6).fill(hovering ? Theme.hover : .clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

/// One chip per `{{variable}}` the request uses, showing the value it will
/// get and where from — the answer to "why is this URL wrong?" at a glance.
private struct VariableHints: View {
    let tab: RequestTab
    private let ws = Workspace.shared

    var body: some View {
        let names = Variables.names(in: referencedText)
        if !names.isEmpty {
            let scope = ws.scope(for: tab)
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    ForEach(names, id: \.self) { name in
                        chip(name, scope: scope)
                    }
                }
                .padding(.horizontal, 16)
            }
            .padding(.bottom, 9)
        }
    }

    private var referencedText: String {
        let d = tab.draft
        var parts = [d.url]
        parts += d.headers.filter(\.enabled).flatMap { [$0.key, $0.value] }
        parts += [d.auth.token, d.auth.username, d.auth.password, d.auth.key, d.auth.value]
        if d.body.mode.isRaw { parts.append(d.body.raw) }
        if d.body.mode == .form || d.body.mode == .multipart { parts += d.body.form.filter(\.enabled).map(\.value) }
        return parts.joined(separator: " ")
    }

    private func chip(_ name: String, scope: VariableScope) -> some View {
        let source = scope.source(of: name)
        let color = Theme.variable(source)
        let row = (scope.environment + scope.collection).first { $0.key == name }
        let value = scope.values[name] ?? ""

        let shown: String
        let help: String
        switch source {
        case .missing:
            shown = "not defined"
            help = "{{\(name)}} isn't defined. Click to add it to the active environment."
        case .dynamic:
            shown = "generated"
            help = "Built-in value, generated each time you send."
        case .environment:
            shown = row?.isSecret == true ? "••••••" : (value.isEmpty ? "empty" : value)
            help = "From the “\(ws.activeEnvironment?.name ?? "")” environment. Click to edit."
        case .collection:
            shown = row?.isSecret == true ? "••••••" : (value.isEmpty ? "empty" : value)
            help = "From the collection's variables. Click to edit."
        }

        return Button {
            switch source {
            case .missing:     ws.defineVariable(name)
            case .environment: ws.sheet = .environments(selected: ws.activeEnvironmentID)
            case .collection:  if let id = tab.collectionID { ws.sheet = .collectionSettings(id) }
            case .dynamic:     break
            }
        } label: {
            HStack(spacing: 5) {
                Circle().fill(color).frame(width: 6, height: 6)
                Text(name).font(.code(11, .semibold)).foregroundStyle(color)
                Text(shown)
                    .font(.code(11))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .frame(maxWidth: 260, alignment: .leading)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(Capsule().fill(color.opacity(0.09)))
            .overlay(Capsule().stroke(color.opacity(0.25)))
        }
        .buttonStyle(.plain)
        .help(help)
    }
}
