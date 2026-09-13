import AppKit
import SwiftUI

/// A plain-text code view for request bodies and responses.
///
/// `TextEditor` can't turn off smart quotes per view — and a curly quote
/// slipped into JSON is a bug nobody can see — and it stalls on
/// multi-megabyte responses. NSTextView with non-contiguous layout does both.
struct CodeEditor: NSViewRepresentable {
    enum Language: Hashable { case json, xml, plain }

    fileprivate let text: Binding<String>?
    fileprivate let content: String
    var language: Language
    var isEditable: Bool
    var wraps: Bool
    var highlightVariables: Bool
    var variableColor: ((String) -> NSColor)?
    var fontSize: CGFloat

    init(text: Binding<String>, language: Language, isEditable: Bool = true, wraps: Bool = true,
         highlightVariables: Bool = true, variableColor: ((String) -> NSColor)? = nil, fontSize: CGFloat = 12) {
        self.text = text
        self.content = ""
        self.language = language
        self.isEditable = isEditable
        self.wraps = wraps
        self.highlightVariables = highlightVariables
        self.variableColor = variableColor
        self.fontSize = fontSize
    }

    /// Read-only, for responses.
    init(content: String, language: Language, wraps: Bool = true, fontSize: CGFloat = 12) {
        self.text = nil
        self.content = content
        self.language = language
        self.isEditable = false
        self.wraps = wraps
        self.highlightVariables = false
        self.variableColor = nil
        self.fontSize = fontSize
    }

    fileprivate var currentText: String { text?.wrappedValue ?? content }

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> NSScrollView {
        let storage = NSTextStorage()
        let layout = NSLayoutManager()
        // Lays out only what's on screen, which is what keeps a 5 MB response scrollable.
        layout.allowsNonContiguousLayout = true
        storage.addLayoutManager(layout)
        let container = NSTextContainer(size: NSSize(width: 0, height: CGFloat.greatestFiniteMagnitude))
        container.widthTracksTextView = true
        layout.addTextContainer(container)

        let textView = CodeTextView(frame: .zero, textContainer: container)
        textView.isRichText = false
        textView.importsGraphics = false
        textView.isAutomaticQuoteSubstitutionEnabled = false
        textView.isAutomaticDashSubstitutionEnabled = false
        textView.isAutomaticTextReplacementEnabled = false
        textView.isAutomaticSpellingCorrectionEnabled = false
        textView.isContinuousSpellCheckingEnabled = false
        textView.isGrammarCheckingEnabled = false
        textView.isAutomaticLinkDetectionEnabled = false
        textView.isAutomaticDataDetectionEnabled = false
        textView.isAutomaticTextCompletionEnabled = false
        textView.smartInsertDeleteEnabled = false
        textView.allowsUndo = true
        textView.usesFindBar = true
        textView.isIncrementalSearchingEnabled = true
        textView.drawsBackground = false
        textView.textContainerInset = NSSize(width: 8, height: 8)
        textView.isVerticallyResizable = true
        textView.minSize = .zero
        textView.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        textView.isEditable = text != nil && isEditable
        textView.isSelectable = true

        let scroll = NSScrollView()
        scroll.drawsBackground = false
        scroll.borderType = .noBorder
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.documentView = textView

        let coordinator = context.coordinator
        coordinator.textView = textView
        coordinator.scrollView = scroll
        textView.delegate = coordinator
        coordinator.applyFont(fontSize)
        coordinator.applyWrapping(wraps)
        coordinator.setText(currentText, undoable: false)
        coordinator.highlightNow()
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        context.coordinator.parent = self
        context.coordinator.update()
    }

    // MARK: - Coordinator

    final class Coordinator: NSObject, NSTextViewDelegate {
        var parent: CodeEditor
        weak var textView: NSTextView?
        weak var scrollView: NSScrollView?

        /// Set while the view's text is replaced from SwiftUI, so the change
        /// isn't echoed back into the binding.
        private var isUpdating = false
        private var pendingHighlight: DispatchWorkItem?
        /// Bumped on every edit and highlight request; a background pass whose
        /// token is stale would colour the wrong ranges, so it's dropped.
        private var highlightToken = 0
        private var appliedWraps: Bool?
        private var appliedFontSize: CGFloat?
        private var appliedLanguage: Language?
        private var appliedHighlightVariables: Bool?

        init(_ parent: CodeEditor) {
            self.parent = parent
        }

        func textDidChange(_ notification: Notification) {
            guard !isUpdating, let textView else { return }
            highlightToken += 1
            parent.text?.wrappedValue = textView.string
            scheduleHighlight()
            if completesVariables { VariableCompletion.shared.update(textView) }
        }

        /// Editable views that show variables also suggest them after `{{`.
        private var completesVariables: Bool {
            parent.text != nil && parent.isEditable && parent.highlightVariables
        }

        func textView(_ textView: NSTextView, doCommandBy selector: Selector) -> Bool {
            completesVariables && VariableCompletion.shared.handle(selector, in: textView)
        }

        func textViewDidChangeSelection(_ notification: Notification) {
            guard let textView else { return }
            VariableCompletion.shared.selectionChanged(textView)
        }

        func textDidEndEditing(_ notification: Notification) {
            guard let textView else { return }
            VariableCompletion.shared.hide(for: textView)
        }

        func update() {
            guard let textView else { return }
            let editable = parent.text != nil && parent.isEditable
            if textView.isEditable != editable { textView.isEditable = editable }
            if appliedWraps != parent.wraps { applyWrapping(parent.wraps) }

            var restyle = appliedLanguage != parent.language || appliedHighlightVariables != parent.highlightVariables
            if appliedFontSize != parent.fontSize {
                applyFont(parent.fontSize)
                restyle = true
            }

            let text = parent.currentText
            if textView.string != text {
                setText(text, undoable: editable)
                if parent.text == nil {
                    textView.setSelectedRange(NSRange(location: 0, length: 0))
                    textView.scroll(.zero)
                }
                restyle = true
            }

            if restyle {
                highlightNow()
            } else if parent.highlightVariables {
                // The variable colour closure can't be compared, and it changes
                // when the environment does; a coalesced pass keeps it current.
                scheduleHighlight()
            }
        }

        func applyFont(_ size: CGFloat) {
            guard let textView else { return }
            appliedFontSize = size
            let font = NSFont.code(size)
            textView.font = font
            textView.typingAttributes = [.font: font, .foregroundColor: NSColor.textColor]
        }

        func applyWrapping(_ wraps: Bool) {
            guard let textView, let scrollView, let container = textView.textContainer else { return }
            appliedWraps = wraps
            let huge = CGFloat.greatestFiniteMagnitude
            if wraps {
                scrollView.hasHorizontalScroller = false
                textView.isHorizontallyResizable = false
                textView.autoresizingMask = [.width]
                textView.setFrameSize(NSSize(width: scrollView.contentSize.width, height: textView.frame.height))
                container.containerSize = NSSize(width: scrollView.contentSize.width, height: huge)
                container.widthTracksTextView = true
            } else {
                scrollView.hasHorizontalScroller = true
                container.widthTracksTextView = false
                container.containerSize = NSSize(width: huge, height: huge)
                textView.isHorizontallyResizable = true
                textView.autoresizingMask = []
            }
            textView.sizeToFit()
        }

        /// Replaces the whole text. Editable views go through
        /// `shouldChangeText`/`didChangeText` so a Prettify can be undone.
        func setText(_ text: String, undoable: Bool) {
            guard let textView, let storage = textView.textStorage else { return }
            isUpdating = true
            defer { isUpdating = false }
            highlightToken += 1

            let attributed = NSAttributedString(string: text, attributes: [
                .font: NSFont.code(parent.fontSize),
                .foregroundColor: NSColor.textColor
            ])
            let full = NSRange(location: 0, length: storage.length)
            let selection = textView.selectedRange()
            if undoable {
                guard textView.shouldChangeText(in: full, replacementString: text) else { return }
                storage.replaceCharacters(in: full, with: attributed)
                textView.didChangeText()
            } else {
                storage.setAttributedString(attributed)
            }
            let length = storage.length
            let location = min(selection.location, length)
            textView.setSelectedRange(NSRange(location: location, length: min(selection.length, length - location)))
        }

        func scheduleHighlight() {
            pendingHighlight?.cancel()
            let work = DispatchWorkItem { [weak self] in self?.highlightNow() }
            pendingHighlight = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.12, execute: work)
        }

        func highlightNow() {
            pendingHighlight?.cancel()
            pendingHighlight = nil
            guard let textView else { return }
            appliedLanguage = parent.language
            appliedHighlightVariables = parent.highlightVariables
            highlightToken += 1
            let token = highlightToken
            let text = textView.string
            let language = parent.language
            let variables = parent.highlightVariables
            let colorFor = parent.variableColor

            if text.utf8.count > 100_000 {
                Task { @MainActor [weak self] in
                    let spans = await Task.detached(priority: .userInitiated) {
                        Highlighter.spans(text, language: language, variables: variables)
                    }.value
                    guard let self, self.highlightToken == token else { return }
                    self.apply(spans, language: language, colorFor: colorFor)
                }
            } else {
                apply(Highlighter.spans(text, language: language, variables: variables), language: language, colorFor: colorFor)
            }
        }

        /// Colours via text storage attributes: attribute-only edits don't
        /// touch the string or the undo stack.
        private func apply(_ spans: Highlighter.Spans, language: Language, colorFor: ((String) -> NSColor)?) {
            guard let storage = textView?.textStorage else { return }
            let length = storage.length
            let full = NSRange(location: 0, length: length)
            storage.beginEditing()
            // In JSON everything that isn't a token is punctuation or whitespace,
            // so the base colour covers punctuation without a span per comma.
            let base = language == .json && !spans.syntax.isEmpty ? CodePalette.punctuation : NSColor.textColor
            storage.addAttribute(.foregroundColor, value: base, range: full)
            storage.removeAttribute(.backgroundColor, range: full)
            for (range, kind) in spans.syntax where NSMaxRange(range) <= length {
                storage.addAttribute(.foregroundColor, value: CodePalette.color(for: kind), range: range)
            }
            for variable in spans.variables where NSMaxRange(variable.range) <= length {
                let color = colorFor?(variable.name) ?? .systemOrange
                storage.addAttributes([
                    .foregroundColor: color,
                    .backgroundColor: color.withAlphaComponent(0.14)
                ], range: variable.range)
            }
            storage.endEditing()
        }
    }
}

// MARK: - Highlighting

private enum Highlighter {
    enum Kind { case key, string, number, literal, tag, attribute, value, comment }

    struct Spans {
        var syntax: [(NSRange, Kind)] = []
        var variables: [(range: NSRange, name: String)] = []
    }

    /// Past this, colouring costs more than it helps; the text stays plain.
    static let syntaxLimit = 2_000_000

    static func spans(_ text: String, language: CodeEditor.Language, variables: Bool) -> Spans {
        var result = Spans()
        if text.utf16.count <= syntaxLimit {
            switch language {
            case .json:
                result.syntax = JSONFormat.highlightRanges(text).compactMap { range, token in
                    switch token {
                    case .key:         return (range, .key)
                    case .string:      return (range, .string)
                    case .number:      return (range, .number)
                    case .literal:     return (range, .literal)
                    case .punctuation: return nil
                    }
                }
            case .xml:
                result.syntax = XMLFormat.highlightRanges(text).map { range, token in
                    switch token {
                    case .tag:       return (range, .tag)
                    case .attribute: return (range, .attribute)
                    case .value:     return (range, .value)
                    case .comment:   return (range, .comment)
                    }
                }
            case .plain:
                break
            }
        }
        if variables { result.variables = Variables.ranges(in: text) }
        return result
    }
}

private enum CodePalette {
    static let key = dynamic(0x7C3AED, 0xC4B5FD)
    static let string = dynamic(0x15803D, 0x86EFAC)
    static let number = dynamic(0xC2410C, 0xFDBA74)
    static let literal = dynamic(0x2563EB, 0x93C5FD)
    static let tag = dynamic(0x2563EB, 0x93C5FD)
    static let attribute = dynamic(0x7C3AED, 0xC4B5FD)
    static let value = dynamic(0x15803D, 0x86EFAC)
    static let punctuation = NSColor.secondaryLabelColor
    static let comment = NSColor.tertiaryLabelColor

    static func color(for kind: Highlighter.Kind) -> NSColor {
        switch kind {
        case .key:       return key
        case .string:    return string
        case .number:    return number
        case .literal:   return literal
        case .tag:       return tag
        case .attribute: return attribute
        case .value:     return value
        case .comment:   return comment
        }
    }

    /// Resolved at draw time, so a theme switch recolours without re-highlighting.
    private static func dynamic(_ light: UInt32, _ dark: UInt32) -> NSColor {
        NSColor(name: nil) { appearance in
            let hex = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? dark : light
            return NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255,
                           green: CGFloat((hex >> 8) & 0xFF) / 255,
                           blue: CGFloat(hex & 0xFF) / 255,
                           alpha: 1)
        }
    }
}

// MARK: - Text view

/// Editing behaviour for code: soft tabs, carried indentation, and ⌘F/⌘G
/// that work even when the app's menu doesn't route them to the find bar.
private final class CodeTextView: NSTextView {
    override func insertTab(_ sender: Any?) {
        guard isEditable else { return super.insertTab(sender) }
        insertText("  ", replacementRange: selectedRange())
    }

    override func insertNewline(_ sender: Any?) {
        guard isEditable else { return super.insertNewline(sender) }
        let ns = string as NSString
        let selection = selectedRange()
        let lineStart = ns.lineRange(for: NSRange(location: selection.location, length: 0)).location
        let line = ns.substring(with: NSRange(location: lineStart, length: selection.location - lineStart))
        let indent = line.prefix { $0 == " " || $0 == "\t" }
        let opensBlock = line.trimmingCharacters(in: .whitespaces).last.map { $0 == "{" || $0 == "[" } ?? false
        insertText("\n" + indent + (opensBlock ? "  " : ""), replacementRange: selection)
    }

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        guard window?.firstResponder === self, let key = event.charactersIgnoringModifiers?.lowercased() else {
            return super.performKeyEquivalent(with: event)
        }
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        let action: NSTextFinder.Action?
        switch (key, flags) {
        case ("f", .command):             action = .showFindInterface
        case ("g", .command):             action = .nextMatch
        case ("g", [.command, .shift]):   action = .previousMatch
        default:                          action = nil
        }
        guard let action else { return super.performKeyEquivalent(with: event) }
        let item = NSMenuItem()
        item.tag = action.rawValue
        performTextFinderAction(item)
        return true
    }
}
