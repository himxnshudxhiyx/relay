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
    /// Read-only JSON gets a gutter of fold chevrons.
    var folds = false

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
    init(content: String, language: Language, wraps: Bool = true, folds: Bool = false, fontSize: CGFloat = 12) {
        self.text = nil
        self.folds = folds
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
        // Set per text in `setText`: see `contiguousLimit`.
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
        // Line numbers (and fold chevrons) live in a vertical ruler.
        textView.usesRuler = false
        scroll.verticalRulerView = Gutter(scrollView: scroll, coordinator: coordinator)
        scroll.hasVerticalRuler = true
        scroll.rulersVisible = true
        textView.delegate = coordinator
        coordinator.applyFont(fontSize)
        coordinator.applyWrapping(wraps)
        coordinator.setText(coordinator.prepare(currentText), undoable: false)
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
        private var appliedFolds: Bool?
        fileprivate private(set) var folding: JSONFolding?

        init(_ parent: CodeEditor) {
            self.parent = parent
        }

        func textDidChange(_ notification: Notification) {
            guard !isUpdating, let textView else { return }
            highlightToken += 1
            parent.text?.wrappedValue = textView.string
            gutter?.textChanged()
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
            if (folding?.source ?? textView.string) != text || appliedFolds != parent.folds {
                setText(prepare(text), undoable: editable)
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

        private var gutter: Gutter? { scrollView?.verticalRulerView as? Gutter }

        /// The text to show for `text`: as is, or with its folds applied.
        func prepare(_ text: String) -> String {
            appliedFolds = parent.folds
            folding = parent.folds ? JSONFolding(text) : nil
            return folding?.display ?? text
        }

        fileprivate func toggleFold(_ region: Int) {
            guard var folding, let scrollView else { return }
            folding.toggle(region)
            self.folding = folding
            // Only text below the clicked line changes, so the scroll position holds.
            let origin = scrollView.contentView.bounds.origin
            setText(folding.display, undoable: false)
            highlightNow()
            scrollView.contentView.scroll(to: origin)
            scrollView.reflectScrolledClipView(scrollView.contentView)
        }

        /// Expands the fold whose `{…}` was clicked. False when the click wasn't on one.
        fileprivate func expandFold(at offset: Int) -> Bool {
            guard let region = folding?.placeholder(at: offset) else { return false }
            toggleFold(region)
            return true
        }

        func applyFont(_ size: CGFloat) {
            guard let textView else { return }
            appliedFontSize = size
            let font = NSFont.code(size)
            textView.font = font
            gutter?.textChanged()
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
            let partial = text.utf16.count > Coordinator.contiguousLimit
            textView.layoutManager?.allowsNonContiguousLayout = partial
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
            // The full height up front, so scrolling never meets an estimate.
            if !partial, let container = textView.textContainer { textView.layoutManager?.ensureLayout(for: container) }
            gutter?.textChanged()
        }

        /// Laying out only what's on screen keeps a multi-megabyte response
        /// responsive, but its height is then an estimate corrected as you
        /// scroll, which makes the content and scroller jump. Below this size
        /// a full layout is quick, so scrolling stays exact and smooth.
        // ponytail: bodies above this still scroll with estimated height;
        // lay them out on a background thread if that becomes a complaint.
        static let contiguousLimit = 1_000_000

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
    static let key = dynamic(0x4A6600, 0xE2F59A)
    static let string = dynamic(0x4F7A00, 0xB5E35A)
    static let number = dynamic(0xB06A00, 0xF5C451)
    static let literal = dynamic(0x1F6FD1, 0x62B3F5)
    static let tag = dynamic(0x1F6FD1, 0x62B3F5)
    static let attribute = dynamic(0x4A6600, 0xE2F59A)
    static let value = dynamic(0x4F7A00, 0xB5E35A)
    static let punctuation = dynamic(0x858585, 0x6E6E6E)
    static let lineNumber = dynamic(0xBDBDBD, 0x3F3F3F)
    static let foldedChevron = dynamic(0x4A6600, 0xC6F432)
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

// MARK: - Gutter

/// Line numbers, plus a chevron beside each line that opens a foldable block.
/// Folded views keep the original numbering, so a fold shows as a jump.
private final class Gutter: NSRulerView {
    private weak var coordinator: CodeEditor.Coordinator?
    /// UTF-16 offset of each displayed line's start; rebuilt lazily after a change.
    private var lineStarts: [Int]?
    private let foldWidth: CGFloat = 14

    init(scrollView: NSScrollView, coordinator: CodeEditor.Coordinator) {
        self.coordinator = coordinator
        super.init(scrollView: scrollView, orientation: .verticalRuler)
        clientView = scrollView.documentView
        ruleThickness = 32
        // Rulers don't redraw on their own when the text scrolls or re-wraps.
        scrollView.contentView.postsBoundsChangedNotifications = true
        for (name, object) in [(NSView.boundsDidChangeNotification, scrollView.contentView as NSView),
                               (NSView.frameDidChangeNotification, scrollView.documentView)] {
            NotificationCenter.default.addObserver(self, selector: #selector(redraw), name: name, object: object)
        }
    }

    required init(coder: NSCoder) { fatalError("init(coder:) is not used") }

    override var isFlipped: Bool { true }

    @objc private func redraw() { needsDisplay = true }

    func textChanged() {
        lineStarts = nil
        needsDisplay = true
    }

    /// Built once per font size, not on every scroll frame.
    private var cached: (size: CGFloat, font: NSFont, attributes: [NSAttributedString.Key: Any])?
    private var font: NSFont { style.font }
    private var style: (font: NSFont, attributes: [NSAttributedString.Key: Any]) {
        let size = max((coordinator?.parent.fontSize ?? 12) - 1, 9)
        if let cached, cached.size == size { return (cached.font, cached.attributes) }
        let font = NSFont.code(size)
        let attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: CodePalette.lineNumber]
        cached = (size, font, attributes)
        return (font, attributes)
    }

    private static let chevrons: [Bool: NSImage] = {
        func image(_ name: String, _ color: NSColor) -> NSImage {
            let config = NSImage.SymbolConfiguration(pointSize: 8, weight: .bold).applying(.init(paletteColors: [color]))
            return NSImage(systemSymbolName: name, accessibilityDescription: nil)?.withSymbolConfiguration(config) ?? NSImage()
        }
        return [true: image("chevron.right", CodePalette.foldedChevron), false: image("chevron.down", .secondaryLabelColor)]
    }()
    private var foldColumn: CGFloat { coordinator?.folding == nil ? 0 : foldWidth }

    private func starts() -> [Int] {
        if let lineStarts { return lineStarts }
        var result = [0]
        for (i, c) in (coordinator?.textView?.string ?? "").utf16.enumerated() where c == 0x0A { result.append(i + 1) }
        lineStarts = result
        // Wide enough for the last line number, at least two digits.
        let total = result.count + (coordinator?.folding?.hiddenLines(before: .max) ?? 0)
        let digit = ("8" as NSString).size(withAttributes: [.font: font]).width
        let width = ceil(CGFloat(max(String(total).count, 2)) * digit + 16 + foldColumn)
        if width != ruleThickness { ruleThickness = width }
        return result
    }

    private func line(at offset: Int, in starts: [Int]) -> Int {
        var low = 0, high = starts.count
        while low < high {
            let mid = (low + high) / 2
            if starts[mid] <= offset { low = mid + 1 } else { high = mid }
        }
        return low - 1
    }

    /// No ruler background or baseline, just numbers and chevrons.
    override func draw(_ dirtyRect: NSRect) { drawHashMarksAndLabels(in: dirtyRect) }

    override func drawHashMarksAndLabels(in rect: NSRect) {
        guard let coordinator, let textView = coordinator.textView,
              let layout = textView.layoutManager, let container = textView.textContainer else { return }
        let starts = starts()
        let folding = coordinator.folding
        let attributes = style.attributes
        let right = ruleThickness - foldColumn - 8
        let origin = textView.textContainerOrigin

        func draw(_ number: Int, in fragment: NSRect) {
            let label = "\(number)" as NSString
            let size = label.size(withAttributes: attributes)
            let top = convert(NSPoint(x: 0, y: fragment.minY + origin.y), from: textView).y
            label.draw(at: NSPoint(x: right - size.width, y: top + (fragment.height - size.height) / 2), withAttributes: attributes)
        }

        let glyphs = layout.glyphRange(forBoundingRect: textView.visibleRect, in: container)
        layout.enumerateLineFragments(forGlyphRange: glyphs) { fragment, _, _, range, _ in
            let char = layout.characterIndexForGlyph(at: range.location)
            let index = self.line(at: char, in: starts)
            // A wrapped line is numbered once, on its first fragment.
            guard index >= 0, starts[index] == char else { return }
            draw(index + 1 + (folding?.hiddenLines(before: char) ?? 0), in: fragment)

            guard let region = folding?.markers[char], let folded = folding?.isFolded(region) else { return }
            guard let image = Gutter.chevrons[folded] else { return }
            let mid = self.convert(NSPoint(x: 0, y: fragment.midY + origin.y), from: textView).y
            let size = image.size
            image.draw(in: NSRect(x: self.ruleThickness - self.foldWidth + (self.foldWidth - size.width) / 2 - 2,
                                  y: mid - size.height / 2, width: size.width, height: size.height),
                       from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
        }
        // The empty line after a trailing newline (or in an empty editor).
        if layout.extraLineFragmentTextContainer != nil, starts.count > 1 || textView.string.isEmpty {
            draw(starts.count + (folding?.hiddenLines(before: .max) ?? 0), in: layout.extraLineFragmentRect)
        }
    }

    override func mouseDown(with event: NSEvent) {
        guard let coordinator, let folding = coordinator.folding, let textView = coordinator.textView,
              let layout = textView.layoutManager, let container = textView.textContainer else { return }
        let y = textView.convert(event.locationInWindow, from: nil).y - textView.textContainerOrigin.y
        let glyph = layout.glyphIndex(for: NSPoint(x: 0, y: y), in: container)
        var range = NSRange()
        let fragment = layout.lineFragmentRect(forGlyphAt: glyph, effectiveRange: &range)
        guard fragment.minY <= y, y < fragment.maxY,
              let region = folding.markers[layout.characterIndexForGlyph(at: range.location)] else { return }
        coordinator.toggleFold(region)
    }
}

// MARK: - Text view

/// Editing behaviour for code: soft tabs, carried indentation, and ⌘F/⌘G
/// that work even when the app's menu doesn't route them to the find bar.
private final class CodeTextView: NSTextView {
    /// A click on a folded `{…}` expands it, as in Postman.
    override func mouseDown(with event: NSEvent) {
        if !isEditable, event.clickCount == 1, let layout = layoutManager, let container = textContainer,
           let coordinator = delegate as? CodeEditor.Coordinator, coordinator.folding != nil {
            var point = convert(event.locationInWindow, from: nil)
            point.x -= textContainerOrigin.x
            point.y -= textContainerOrigin.y
            let glyph = layout.glyphIndex(for: point, in: container)
            if layout.boundingRect(forGlyphRange: NSRange(location: glyph, length: 1), in: container).contains(point),
               coordinator.expandFold(at: layout.characterIndexForGlyph(at: glyph)) {
                return
            }
        }
        super.mouseDown(with: event)
    }

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
