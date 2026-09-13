import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct ResponsePanel: View {
    let tabID: UUID
    private let ws = Workspace.shared

    var body: some View {
        Group {
            switch ws.responses[tabID] ?? .idle {
            case .idle:
                EmptyState(symbol: "paperplane", title: "No response yet",
                           message: "Send the request and the response shows up here.") {
                    HStack(spacing: 4) {
                        KeyHint(keys: "⌘")
                        KeyHint(keys: "↩")
                    }
                }
            case .loading(let started, _):
                LoadingView(started: started) { ws.cancel(tabID) }
            case .failed(let message):
                EmptyState(symbol: "exclamationmark.triangle", title: "Couldn't get a response", message: message) {
                    Button("Try Again") { ws.send(tabID) }
                        .buttonStyle(SecondaryButtonStyle(height: 28))
                }
            case .done(let result):
                ResponseContent(tabID: tabID, result: result)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Theme.panel)
    }
}

private struct LoadingView: View {
    let started: Date
    let cancel: () -> Void

    var body: some View {
        VStack(spacing: 12) {
            ProgressView()
            TimelineView(.periodic(from: started, by: 0.1)) { context in
                Text(Format.duration(context.date.timeIntervalSince(started)))
                    .font(.code(12))
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }
            Button("Cancel", action: cancel)
                .buttonStyle(SecondaryButtonStyle(height: 28))
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

private struct ResponseContent: View {
    let tabID: UUID
    let result: HTTPResult
    private let ws = Workspace.shared

    @AppStorage("wrapResponse") private var wrap = true
    @AppStorage("prettyResponse") private var pretty = true

    var body: some View {
        let section = Binding(
            get: { ws.responseSections[tabID] ?? .body },
            set: { ws.responseSections[tabID] = $0 }
        )
        VStack(spacing: 0) {
            HStack(alignment: .bottom, spacing: 10) {
                UnderlineTabs(items: [
                    TabItem(id: ResponseSection.body, title: "Response"),
                    TabItem(id: .headers, title: "Headers", badge: "\(result.headers.count)"),
                    TabItem(id: .request, title: "Sent"),
                ], selection: section)
                Spacer(minLength: 8)
                ViewThatFits(in: .horizontal) {
                    summary(compact: false)
                    summary(compact: true)
                }
                .padding(.bottom, 7)
            }
            .padding(.horizontal, 16)
            Divider()

            switch section.wrappedValue {
            case .body:    bodyView
            case .headers: ScrollView { HeaderRows(headers: result.headers) }
            case .request: SentRequestView(result: result)
            }
        }
    }

    private func summary(compact: Bool) -> some View {
        HStack(spacing: 10) {
            StatusPill(code: result.status)
            if !compact {
                metric("clock", Format.duration(result.duration))
                    .help(result.ttfb.map { "Time to first byte: \(Format.duration($0))" } ?? "Total time")
                metric("arrow.down.circle", Format.bytes(result.body.count))
                    .help("Response body size")
            }
            CopyMenu(tabID: tabID, result: result)
        }
        .fixedSize()
    }

    private func metric(_ symbol: String, _ value: String) -> some View {
        HStack(spacing: 4) {
            Image(systemName: symbol).font(.system(size: 10))
            Text(value).font(.app(12, .medium)).monospacedDigit()
        }
        .foregroundStyle(.secondary)
    }

    @ViewBuilder private var bodyView: some View {
        switch result.kind {
        case .empty:
            EmptyState(symbol: "doc", title: "Empty body", message: "The server sent no content.")
        case .image:
            if let image = NSImage(data: result.body) {
                ScrollView([.horizontal, .vertical]) {
                    Image(nsImage: image)
                        .padding(16)
                }
            } else {
                binaryView
            }
        case .binary:
            binaryView
        case .json, .xml, .html, .text:
            VStack(spacing: 0) {
                HStack(spacing: 8) {
                    if result.pretty != nil {
                        Picker("Format", selection: $pretty) {
                            Text("Pretty").tag(true)
                            Text("Raw").tag(false)
                        }
                        .pickerStyle(.segmented)
                        .labelsHidden()
                        .controlSize(.small)
                        .frame(width: 120)
                    }
                    Text(kindLabel)
                        .font(.app(11))
                        .foregroundStyle(.secondary)
                    Spacer()
                    Text("⌘F to search")
                        .font(.app(10.5))
                        .foregroundStyle(.tertiary)
                    IconButton(symbol: wrap ? "text.alignleft" : "arrow.left.and.right", help: wrap ? "Turn off line wrap" : "Wrap long lines") {
                        wrap.toggle()
                    }
                    IconButton(symbol: "square.and.arrow.down", help: "Save response to a file") { saveBody() }
                }
                .padding(.horizontal, 12)
                .frame(height: 34)
                Divider()
                CodeEditor(content: pretty ? (result.pretty ?? result.text) : result.text,
                           language: language, wraps: wrap)
            }
        }
    }

    private var binaryView: some View {
        EmptyState(symbol: "doc.zipper", title: "Binary response",
                   message: "\(Format.bytes(result.body.count)) of \(result.contentType.isEmpty ? "binary data" : result.contentType).") {
            Button("Save to File…") { saveBody() }
                .buttonStyle(SecondaryButtonStyle(height: 28))
        }
    }

    private var language: CodeEditor.Language {
        switch result.kind {
        case .json:       return .json
        case .xml, .html: return .xml
        default:          return .plain
        }
    }

    private var kindLabel: String {
        switch result.kind {
        case .json: return "JSON"
        case .xml:  return "XML"
        case .html: return "HTML"
        default:    return result.contentType.components(separatedBy: ";").first ?? "Text"
        }
    }

    private func saveBody() {
        let mime = result.contentType.components(separatedBy: ";").first?.trimmingCharacters(in: .whitespaces) ?? ""
        let ext = UTType(mimeType: mime)?.preferredFilenameExtension ?? "txt"
        FilePanels.save(result.body, suggestedName: "response.\(ext)")
    }
}

/// Copy actions for the response. cURL + response is the one people reach
/// for when reporting a bug, so it gets its own button rather than a menu item.
private struct CopyMenu: View {
    let tabID: UUID
    let result: HTTPResult
    private let ws = Workspace.shared
    @State private var hovering = false

    var body: some View {
        HStack(spacing: 6) {
            Button {
                ws.copy(Format.curlWithResponse(curl: result.curl, result: result, includeHeaders: false), "cURL and response")
            } label: {
                HStack(spacing: 5) {
                    Image(systemName: "terminal").font(.system(size: 10.5, weight: .semibold))
                    Text("Copy with cURL").font(.app(12, .semibold))
                }
                .foregroundStyle(Theme.accent)
                .padding(.horizontal, 9)
                .frame(height: 24)
                .background(RoundedRectangle(cornerRadius: 6).fill(Theme.accent.opacity(hovering ? 0.18 : 0.11)))
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .onHover { hovering = $0 }
            .help("Copy the cURL that was sent together with this response (⌥⌘C)")

            menu
        }
    }

    private var menu: some View {
        Menu {
            Button("Response Body") { ws.copy(result.pretty ?? result.text, "response body") }
            Button("Response Headers") {
                ws.copy(result.headers.map { "\($0.name): \($0.value)" }.joined(separator: "\n"), "headers")
            }
            Divider()
            Button("cURL + Response") {
                ws.copy(Format.curlWithResponse(curl: result.curl, result: result, includeHeaders: false), "cURL and response")
            }
            Button("cURL + Response with Headers") {
                ws.copy(Format.curlWithResponse(curl: result.curl, result: result, includeHeaders: true), "cURL and response")
            }
            Divider()
            Button("cURL That Was Sent") { ws.copy(result.curl, "cURL") }
        } label: {
            Label("Copy", systemImage: "doc.on.doc")
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .help("More copy options: body, headers, cURL, cURL + response with headers")
    }
}

struct HeaderRows: View {
    let headers: [(name: String, value: String)]

    var body: some View {
        LazyVStack(alignment: .leading, spacing: 0) {
            ForEach(Array(headers.enumerated()), id: \.offset) { _, header in
                HStack(alignment: .firstTextBaseline, spacing: 14) {
                    Text(header.name)
                        .font(.code(11.5, .semibold))
                        .foregroundStyle(.secondary)
                        .frame(width: 200, alignment: .leading)
                        .textSelection(.enabled)
                    Text(header.value)
                        .font(.code(11.5))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .textSelection(.enabled)
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 7)
                .contextMenu {
                    Button("Copy Value") { Clipboard.copy(header.value) }
                    Button("Copy Header") { Clipboard.copy("\(header.name): \(header.value)") }
                }
                Divider().padding(.leading, 16)
            }
        }
    }
}

private struct SentRequestView: View {
    let result: HTTPResult

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    MethodBadge(method: result.sentMethod, size: 12)
                    Text(result.sentURL)
                        .font(.code(12))
                        .textSelection(.enabled)
                }
                if result.redirects > 0 {
                    Label("Followed \(result.redirects) redirect\(result.redirects == 1 ? "" : "s") to \(result.finalURL)",
                          systemImage: "arrow.turn.down.right")
                        .font(.app(11.5))
                        .foregroundStyle(.secondary)
                }

                title("Headers")
                HeaderRows(headers: result.sentHeaders)
                    .padding(.horizontal, -16)

                if !result.sentBody.isEmpty {
                    title("Body")
                    Text(result.sentBody.count > 20_000 ? String(result.sentBody.prefix(20_000)) + "\n…" : result.sentBody)
                        .font(.code(11.5))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(10)
                        .background(RoundedRectangle(cornerRadius: 8).fill(Theme.field))
                }

                Text("macOS may also add Accept-Encoding, Accept-Language and Connection when sending.")
                    .font(.app(11))
                    .foregroundStyle(.tertiary)
            }
            .padding(16)
        }
    }

    private func title(_ text: String) -> some View {
        Text(text.uppercased())
            .font(.app(10.5, .semibold))
            .foregroundStyle(.tertiary)
    }
}
