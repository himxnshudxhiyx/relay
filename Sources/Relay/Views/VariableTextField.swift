import AppKit
import SwiftUI

/// A single-line text field for anything that can hold a `{{variable}}`:
/// the URL, param and header rows, form fields, auth values.
///
/// AppKit rather than a SwiftUI `TextField`, because SwiftUI doesn't expose
/// the cursor, and suggesting variables needs to know where `{{` was typed and
/// where to put the list. It also lets the URL box catch a pasted cURL before
/// it lands (SwiftUI only reports the text afterwards, and ignores the binding
/// while the field is being edited), and turns pasted line breaks into spaces
/// instead of hiding the text above and below the field.
struct VariableTextField: NSViewRepresentable {
    @Binding var text: String
    var placeholder: String
    var fontSize: CGFloat = 12
    /// Bump to move keyboard focus into the field.
    var focusRequest = 0
    var focusOnAppear = false
    /// Return true when the pasted text was a cURL command that was applied.
    var onPasteCurl: ((String) -> Bool)? = nil
    var onSubmit: (() -> Void)? = nil
    var onFocusChange: ((Bool) -> Void)? = nil

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> PasteAwareTextField {
        let field = PasteAwareTextField()
        field.delegate = context.coordinator
        field.isBordered = false
        field.drawsBackground = false
        field.focusRingType = .none
        field.font = .code(fontSize)
        field.lineBreakMode = .byTruncatingTail
        field.usesSingleLineMode = true
        field.cell?.isScrollable = true
        field.cell?.wraps = false
        field.placeholderAttributedString = NSAttributedString(string: placeholder, attributes: [
            .font: NSFont.code(fontSize),
            .foregroundColor: NSColor.placeholderTextColor,
        ])
        field.stringValue = text
        field.onPaste = { [weak coordinator = context.coordinator] pasted in
            coordinator?.parent.onPasteCurl?(pasted) ?? false
        }
        field.onFocus = { [weak coordinator = context.coordinator] in
            coordinator?.parent.onFocusChange?(true)
        }
        field.setContentHuggingPriority(.defaultLow, for: .horizontal)
        field.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        context.coordinator.lastFocusRequest = focusRequest
        field.focusWhenInWindow = focusOnAppear
        return field
    }

    func updateNSView(_ field: PasteAwareTextField, context: Context) {
        context.coordinator.parent = self

        let shown = field.currentEditor()?.string ?? field.stringValue
        if shown != text {
            if let editor = field.currentEditor() as? NSTextView {
                // Replacing the editor's text keeps the field focused, so
                // the cursor stays put after a paste turns into a URL.
                editor.string = text
                editor.setSelectedRange(NSRange(location: (text as NSString).length, length: 0))
            } else {
                field.stringValue = text
            }
        }

        if context.coordinator.lastFocusRequest != focusRequest {
            context.coordinator.lastFocusRequest = focusRequest
            DispatchQueue.main.async {
                field.window?.makeFirstResponder(field)
                field.currentEditor()?.selectAll(nil)
            }
        }
    }

    static func dismantleNSView(_ field: PasteAwareTextField, coordinator: Coordinator) {
        if let editor = field.currentEditor() as? NSTextView {
            VariableCompletion.shared.hide(for: editor)
        }
    }

    @MainActor
    final class Coordinator: NSObject, NSTextFieldDelegate {
        var parent: VariableTextField
        var lastFocusRequest = 0

        init(_ parent: VariableTextField) {
            self.parent = parent
        }

        func controlTextDidChange(_ note: Notification) {
            guard let field = note.object as? NSTextField else { return }
            parent.text = field.stringValue
            if let editor = field.currentEditor() as? NSTextView {
                VariableCompletion.shared.update(editor)
            }
        }

        func controlTextDidEndEditing(_ note: Notification) {
            if let editor = note.userInfo?["NSFieldEditor"] as? NSTextView {
                VariableCompletion.shared.hide(for: editor)
            }
            parent.onFocusChange?(false)
        }

        func control(_ control: NSControl, textView: NSTextView, doCommandBy selector: Selector) -> Bool {
            if VariableCompletion.shared.handle(selector, in: textView) { return true }
            switch selector {
            case #selector(NSResponder.insertNewline(_:)):
                guard let onSubmit = parent.onSubmit else { return false }
                onSubmit()
                return true
            case #selector(NSResponder.moveLeft(_:)), #selector(NSResponder.moveRight(_:)),
                 #selector(NSResponder.moveToBeginningOfLine(_:)), #selector(NSResponder.moveToEndOfLine(_:)):
                VariableCompletion.shared.hide(for: textView)
                return false
            default:
                return false
            }
        }
    }
}

final class PasteAwareTextField: NSTextField, NSTextViewDelegate {
    var onPaste: ((String) -> Bool)?
    var onFocus: (() -> Void)?
    var focusWhenInWindow = false

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        guard focusWhenInWindow, window != nil else { return }
        focusWhenInWindow = false
        DispatchQueue.main.async { [weak self] in
            guard let self, let window = self.window else { return }
            window.makeFirstResponder(self)
        }
    }

    override func becomeFirstResponder() -> Bool {
        let became = super.becomeFirstResponder()
        if became { onFocus?() }
        return became
    }

    /// The field editor asks its delegate — this field — before any change,
    /// typed or pasted, so this is where a pasted cURL is intercepted.
    func textView(_ textView: NSTextView, shouldChangeTextIn range: NSRange, replacementString: String?) -> Bool {
        guard let replacement = replacementString, replacement.count > 1 else { return true }

        let trimmed = replacement.trimmingCharacters(in: .whitespacesAndNewlines)
        let lower = trimmed.lowercased()
        let looksLikeCurl = lower.hasPrefix("curl ") || lower.hasPrefix("curl.exe ") || lower.hasPrefix("$ curl ")
        if looksLikeCurl, onPaste?(trimmed) == true {
            return false
        }

        if replacement.rangeOfCharacter(from: .newlines) != nil {
            let flattened = replacement
                .replacingOccurrences(of: #"\\?[ \t]*\r?\n[ \t]*"#, with: " ", options: .regularExpression)
                .trimmingCharacters(in: .whitespaces)
            textView.insertText(flattened, replacementRange: range)
            return false
        }

        return true
    }
}
