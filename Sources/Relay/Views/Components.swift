import SwiftUI

/// Colour-coded method label, used in the sidebar, tabs and history.
struct MethodBadge: View {
    let method: String
    var size: CGFloat = 10.5

    var body: some View {
        Text(short)
            .font(.code(size, .bold))
            .foregroundStyle(Theme.method(method))
            .lineLimit(1)
    }

    private var short: String {
        switch method.uppercased() {
        case "DELETE":  return "DEL"
        case "OPTIONS": return "OPT"
        default:        return method.uppercased()
        }
    }
}

struct StatusPill: View {
    let code: Int

    var body: some View {
        let phrase = HTTPStatus.phrase(code)
        Text(phrase.isEmpty ? "\(code)" : "\(code) \(phrase)")
            .font(.code(12, .bold))
            .foregroundStyle(Theme.status(code))
            .padding(.horizontal, 7)
            .padding(.vertical, 3)
            .background(RoundedRectangle(cornerRadius: 5).fill(Theme.status(code).opacity(0.14)))
    }
}

struct TabItem<ID: Hashable>: Identifiable {
    let id: ID
    let title: String
    var badge: String?
}

/// Text tabs with an accent underline — lighter than a segmented control,
/// and room for a count badge.
struct UnderlineTabs<ID: Hashable>: View {
    let items: [TabItem<ID>]
    @Binding var selection: ID
    @Namespace private var underline

    var body: some View {
        HStack(spacing: 22) {
            ForEach(items) { item in
                let selected = selection == item.id
                Button {
                    selection = item.id
                } label: {
                    VStack(spacing: 8) {
                        HStack(spacing: 5) {
                            Text(item.title)
                                .font(.app(13, selected ? .semibold : .regular))
                                .foregroundStyle(selected ? Color.primary : Color.secondary)
                                .lineLimit(1)
                                .fixedSize()
                            if let badge = item.badge {
                                // Never wraps: a squeezed row would otherwise break "JSON" onto two lines.
                                // Lime on the open tab, quiet grey on the rest.
                                Text(badge)
                                    .lineLimit(1)
                                    .fixedSize()
                                    .font(.code(10, .semibold))
                                    .foregroundStyle(selected ? Theme.accent : Color.secondary)
                                    .padding(.horizontal, 5)
                                    .padding(.vertical, 1)
                                    .background(RoundedRectangle(cornerRadius: 4).fill(selected ? Theme.accentSoft : Theme.selected))
                            }
                        }
                        // One underline that slides between tabs.
                        ZStack {
                            Color.clear.frame(height: 2)
                            if selected {
                                Capsule().fill(Theme.accent).frame(height: 2)
                                    .matchedGeometryEffect(id: "underline", in: underline)
                            }
                        }
                    }
                    .fixedSize(horizontal: true, vertical: false)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.top, 9)
        // Scoped here, so only the tabs animate, not the content they switch.
        .animation(.snappy(duration: 0.22), value: selection)
    }
}

/// A borderless icon button that shows a hover background.
struct IconButton: View {
    let symbol: String
    let help: String
    var size: CGFloat = 12
    var tint: Color = .secondary
    let action: () -> Void

    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: size, weight: .medium))
                .foregroundStyle(tint)
                .frame(width: 24, height: 24)
                .background(RoundedRectangle(cornerRadius: 6).fill(hovering ? Theme.hover : .clear))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .help(help)
    }
}

/// The filled accent button (Send, Save, Import).
struct PrimaryButtonStyle: ButtonStyle {
    var height: CGFloat = 32
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.app(13, .semibold))
            .foregroundStyle(Theme.onAccent)
            .padding(.horizontal, 14)
            .frame(height: height)
            .background(RoundedRectangle(cornerRadius: 8).fill(Theme.accentFill))
            .opacity(isEnabled ? (configuration.isPressed ? 0.8 : 1) : 0.45)
            .contentShape(Rectangle())
    }
}

/// The quiet outlined button next to a primary one.
struct SecondaryButtonStyle: ButtonStyle {
    var height: CGFloat = 32
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.app(13, .medium))
            .foregroundStyle(.primary)
            .padding(.horizontal, 12)
            .frame(height: height)
            .background(RoundedRectangle(cornerRadius: 8).fill(configuration.isPressed ? Theme.hover : Theme.field))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.line))
            .opacity(isEnabled ? 1 : 0.45)
            .contentShape(Rectangle())
    }
}

struct FieldChrome: ViewModifier {
    var focused = false

    func body(content: Content) -> some View {
        content
            .textFieldStyle(.plain)
            .padding(.horizontal, 10)
            .frame(minHeight: 30)
            .background(RoundedRectangle(cornerRadius: 7).fill(Theme.field))
            .overlay(RoundedRectangle(cornerRadius: 7).stroke(focused ? Theme.accent.opacity(0.7) : Theme.line))
    }
}

extension View {
    func fieldChrome(focused: Bool = false) -> some View { modifier(FieldChrome(focused: focused)) }
}

/// A password-style field with a reveal toggle, for tokens and secrets.
///
/// Starts revealed when empty or holding a `{{variable}}`: a reference isn't
/// secret, and only the plain field can suggest variables as you type.
struct SecretField: View {
    let placeholder: String
    @Binding var text: String
    @State private var revealed: Bool

    init(placeholder: String, text: Binding<String>) {
        self.placeholder = placeholder
        self._text = text
        let value = text.wrappedValue
        self._revealed = State(initialValue: value.isEmpty || value.contains("{{"))
    }

    var body: some View {
        HStack(spacing: 4) {
            Group {
                if revealed {
                    VariableTextField(text: $text, placeholder: placeholder)
                } else {
                    SecureField(placeholder, text: $text)
                        .font(.code(12))
                        .textFieldStyle(.plain)
                }
            }
            IconButton(symbol: revealed ? "eye.slash" : "eye", help: revealed ? "Hide" : "Show", size: 11) {
                revealed.toggle()
            }
        }
        .padding(.leading, 10)
        .padding(.trailing, 3)
        .frame(minHeight: 30)
        .background(RoundedRectangle(cornerRadius: 7).fill(Theme.field))
        .overlay(RoundedRectangle(cornerRadius: 7).stroke(Theme.line))
    }
}

/// Centered icon, title and hint for empty panes.
struct EmptyState<Actions: View>: View {
    let symbol: String
    let title: String
    let message: String
    @ViewBuilder var actions: () -> Actions

    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: symbol)
                .font(.system(size: 30, weight: .light))
                .foregroundStyle(.tertiary)
                .padding(.bottom, 4)
            Text(title)
                .font(.app(14, .semibold))
            Text(message)
                .font(.app(12))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 300)
            actions()
                .padding(.top, 6)
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

extension EmptyState where Actions == EmptyView {
    init(symbol: String, title: String, message: String) {
        self.init(symbol: symbol, title: title, message: message) { EmptyView() }
    }
}

/// A keyboard shortcut rendered as a small keycap.
struct KeyHint: View {
    let keys: String

    var body: some View {
        Text(keys)
            .font(.system(size: 11, weight: .medium, design: .rounded))
            .foregroundStyle(.secondary)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(RoundedRectangle(cornerRadius: 5).fill(Theme.field))
            .overlay(RoundedRectangle(cornerRadius: 5).stroke(Theme.line))
    }
}

/// A search field styled to sit in the sidebar.
struct SearchField: View {
    let placeholder: String
    @Binding var text: String
    var shortcut: String?
    /// Bump to put the cursor in the field.
    var focusRequest = 0
    @FocusState private var focused: Bool

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 11))
                .foregroundStyle(.tertiary)
            TextField(placeholder, text: $text)
                .textFieldStyle(.plain)
                .font(.app(12.5))
                .focused($focused)
                .onChange(of: focusRequest) { focused = true }
            if !text.isEmpty {
                Button { text = "" } label: {
                    Image(systemName: "xmark.circle.fill").font(.system(size: 11)).foregroundStyle(.tertiary)
                }
                .buttonStyle(.plain)
            } else if let shortcut {
                Text(shortcut)
                    .font(.system(size: 10, weight: .medium))
                    .foregroundStyle(.tertiary)
                    .padding(.horizontal, 5)
                    .padding(.vertical, 1)
                    .overlay(RoundedRectangle(cornerRadius: 4).stroke(Theme.line))
            }
        }
        .padding(.horizontal, 9)
        .frame(height: 30)
        .background(RoundedRectangle(cornerRadius: 7).fill(Theme.field))
        .overlay(RoundedRectangle(cornerRadius: 7).stroke(Theme.line))
    }
}

/// A segmented control drawn to match the theme: a recessed track with the
/// selected option raised.
struct PillPicker<ID: Hashable>: View {
    let options: [(id: ID, title: String)]
    @Binding var selection: ID
    var fontSize: CGFloat = 12
    var height: CGFloat = 24
    @Namespace private var pill

    var body: some View {
        HStack(spacing: 2) {
            ForEach(options, id: \.id) { option in
                let selected = selection == option.id
                Button { selection = option.id } label: {
                    Text(option.title)
                        .font(.app(fontSize, selected ? .medium : .regular))
                        .foregroundStyle(selected ? Color.primary : Color.secondary)
                        .lineLimit(1)
                        .minimumScaleFactor(0.75)
                        .padding(.horizontal, 6)
                        .frame(maxWidth: .infinity)
                        .frame(height: height)
                        .background {
                            if selected {
                                RoundedRectangle(cornerRadius: height / 4).fill(Theme.selected)
                                    .matchedGeometryEffect(id: "pill", in: pill)
                            }
                        }
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
        .padding(2)
        .background(RoundedRectangle(cornerRadius: 8).fill(Theme.field))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.line))
        .fixedSize(horizontal: false, vertical: true)
        .animation(.snappy(duration: 0.22), value: selection)
    }
}
