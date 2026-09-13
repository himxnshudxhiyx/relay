import AppKit
import Observation
import SwiftUI

@Observable
final class CompletionModel {
    var items: [VariableSuggestion] = []
    var selection = 0
    var title = ""
    /// Bumped whenever the items are replaced, so the list is rebuilt rather
    /// than reusing rows (and scroll offset) from the previous filter.
    var generation = 0
    @ObservationIgnored var pick: (VariableSuggestion) -> Void = { _ in }
}

/// The `{{` suggestion list, shared by every text field and code editor.
///
/// It lives in its own borderless panel that can never become key, so the
/// field being typed in keeps focus; the field forwards ↑ ↓ ↩ ⇥ esc here.
@MainActor
final class VariableCompletion {
    static let shared = VariableCompletion()

    private let model = CompletionModel()
    private var panel: CompletionPanel?
    private weak var textView: NSTextView?
    private var range = NSRange()
    private var resignObserver: NSObjectProtocol?
    /// Set while a pick is inserted, so the resulting text change doesn't reopen the list.
    private var isInserting = false

    private init() {}

    var isVisible: Bool { panel?.isVisible == true && textView != nil }

    /// Call after the text changes.
    func update(_ textView: NSTextView) {
        guard !isInserting else { return }
        let selection = textView.selectedRange()
        guard textView.isEditable, selection.length == 0,
              let context = CompletionContext.find(in: textView.string, cursor: selection.location) else {
            return hide(for: textView)
        }
        let ws = Workspace.shared
        let items = ws.completionScope.suggestions(matching: context.prefix)
        guard !items.isEmpty else { return hide(for: textView) }

        model.items = items
        model.selection = 0
        model.generation += 1
        model.title = ws.activeEnvironment.map { "Variables · \($0.name)" } ?? "Variables"
        model.pick = { [weak self] item in self?.accept(item) }
        self.textView = textView
        range = context.range
        show(near: textView, anchor: context.range.location - 2, rows: items.count)
    }

    /// Call when the cursor moves without typing; closes the list once it has left the name.
    func selectionChanged(_ textView: NSTextView) {
        guard isVisible, textView === self.textView, !isInserting else { return }
        let selection = textView.selectedRange()
        let context = selection.length == 0 ? CompletionContext.find(in: textView.string, cursor: selection.location) : nil
        if context?.range.location != range.location { hide() }
    }

    /// Keyboard commands from the field editor. True when the list handled it.
    func handle(_ selector: Selector, in textView: NSTextView) -> Bool {
        guard isVisible, textView === self.textView, !model.items.isEmpty else { return false }
        let count = model.items.count
        switch selector {
        case #selector(NSResponder.moveDown(_:)):
            model.selection = (model.selection + 1) % count
        case #selector(NSResponder.moveUp(_:)):
            model.selection = (model.selection - 1 + count) % count
        case #selector(NSResponder.insertNewline(_:)), #selector(NSResponder.insertTab(_:)):
            accept(model.items[min(model.selection, count - 1)])
        case #selector(NSResponder.cancelOperation(_:)):
            hide()
        default:
            return false
        }
        return true
    }

    func hide(for textView: NSTextView? = nil) {
        if let textView, let current = self.textView, textView !== current { return }
        if let panel {
            panel.parent?.removeChildWindow(panel)
            panel.orderOut(nil)
        }
        if let resignObserver { NotificationCenter.default.removeObserver(resignObserver) }
        resignObserver = nil
        self.textView = nil
    }

    private func accept(_ item: VariableSuggestion) {
        guard let textView, NSMaxRange(range) <= (textView.string as NSString).length else { return hide() }
        // Inside an existing {{name}}: replace the whole name, don't add a second "}}".
        let context = CompletionContext(range: range, prefix: "")
        let (replaceRange, closed) = context.replacement(in: textView.string)
        hide()

        isInserting = true
        textView.insertText(closed ? item.name : item.name + "}}", replacementRange: replaceRange)
        let caret = replaceRange.location + (item.name as NSString).length + 2
        textView.setSelectedRange(NSRange(location: min(caret, (textView.string as NSString).length), length: 0))
        isInserting = false
    }

    private func show(near textView: NSTextView, anchor: Int, rows: Int) {
        guard let window = textView.window else { return }
        let panel = self.panel ?? makePanel()
        let size = NSSize(width: 420, height: CompletionList.height(rows: rows))

        var caret = textView.firstRect(forCharacterRange: NSRange(location: max(0, anchor), length: 0), actualRange: nil)
        if caret.isEmpty && caret.origin == .zero {
            caret = window.convertToScreen(textView.convert(textView.bounds, to: nil))
        }
        let screen = window.screen?.visibleFrame ?? NSScreen.main?.visibleFrame ?? .zero
        var origin = NSPoint(x: caret.minX - 10, y: caret.minY - size.height - 6)
        if origin.y < screen.minY { origin.y = caret.maxY + 6 }
        origin.x = min(max(origin.x, screen.minX + 4), screen.maxX - size.width - 4)

        panel.appearance = window.effectiveAppearance
        panel.setFrame(NSRect(origin: origin, size: size), display: true)
        if panel.parent !== window {
            panel.parent?.removeChildWindow(panel)
            window.addChildWindow(panel, ordered: .above)
        }
        panel.orderFront(nil)

        if resignObserver == nil {
            resignObserver = NotificationCenter.default.addObserver(
                forName: NSWindow.didResignKeyNotification, object: window, queue: .main
            ) { _ in
                MainActor.assumeIsolated { VariableCompletion.shared.hide() }
            }
        }
    }

    private func makePanel() -> CompletionPanel {
        let panel = CompletionPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel],
                                    backing: .buffered, defer: true)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.level = .popUpMenu
        panel.hidesOnDeactivate = true
        let host = FirstMouseHostingView(rootView: CompletionList(model: model))
        host.sizingOptions = []
        panel.contentView = host
        self.panel = panel
        return panel
    }
}

private final class CompletionPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// The panel is never key, so without this the first click on a row would be swallowed.
private final class FirstMouseHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

private struct CompletionList: View {
    let model: CompletionModel

    static let rowHeight: CGFloat = 34
    static let chrome: CGFloat = 30 + 28 + 10
    static let maxRows = 7

    static func height(rows: Int) -> CGFloat {
        chrome + CGFloat(min(rows, maxRows)) * rowHeight
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text(model.title.uppercased())
                    .font(.app(10, .semibold))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                Spacer()
                Text("\(model.items.count)")
                    .font(.app(10, .semibold))
                    .foregroundStyle(.tertiary)
            }
            .padding(.horizontal, 12)
            .frame(height: 30)
            Divider()

            ScrollViewReader { proxy in
                ScrollView {
                    VStack(spacing: 0) {
                        ForEach(Array(model.items.enumerated()), id: \.element.id) { index, item in
                            row(item, selected: index == model.selection)
                                .onTapGesture { model.pick(item) }
                        }
                    }
                    .padding(5)
                }
                .id(model.generation)
                .onChange(of: model.selection) { _, index in
                    guard model.items.indices.contains(index) else { return }
                    proxy.scrollTo(model.items[index].id)
                }
            }

            Divider()
            HStack(spacing: 12) {
                hint("↑↓", "choose")
                hint("↩", "insert")
                hint("esc", "dismiss")
                Spacer()
            }
            .padding(.horizontal, 12)
            .frame(height: 28)
        }
        .background(RoundedRectangle(cornerRadius: 10).fill(Theme.canvas))
        .clipShape(RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Theme.line))
    }

    private func row(_ item: VariableSuggestion, selected: Bool) -> some View {
        let color = Theme.variable(item.source)
        return HStack(spacing: 8) {
            Circle().fill(color).frame(width: 6, height: 6)
            Text(item.name)
                .font(.code(12, .semibold))
                .foregroundStyle(selected ? Color.primary : color)
                .lineLimit(1)
                .fixedSize()
            Text(displayValue(item))
                .font(.code(11.5))
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.middle)
            Spacer(minLength: 6)
            Text(sourceLabel(item.source))
                .font(.app(10, .medium))
                .foregroundStyle(.tertiary)
                .fixedSize()
        }
        .padding(.horizontal, 8)
        .frame(height: Self.rowHeight)
        .background(RoundedRectangle(cornerRadius: 6).fill(selected ? Theme.accent.opacity(0.18) : .clear))
        .contentShape(Rectangle())
    }

    private func displayValue(_ item: VariableSuggestion) -> String {
        switch item.source {
        case .dynamic: return "generated when sent"
        default:       return item.isSecret ? "••••••" : (item.value.isEmpty ? "empty" : item.value)
        }
    }

    private func sourceLabel(_ source: VariableScope.Source) -> String {
        switch source {
        case .environment: return "Environment"
        case .collection:  return "Collection"
        case .dynamic:     return "Built-in"
        case .missing:     return ""
        }
    }

    private func hint(_ keys: String, _ label: String) -> some View {
        HStack(spacing: 4) {
            Text(keys).font(.system(size: 10.5, weight: .semibold, design: .rounded)).foregroundStyle(.secondary)
            Text(label).font(.app(10.5)).foregroundStyle(.tertiary)
        }
    }
}
