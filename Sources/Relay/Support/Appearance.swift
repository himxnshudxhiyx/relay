import AppKit
import SwiftUI

enum AppAppearance: String, CaseIterable, Identifiable {
    case system, light, dark

    var id: String { rawValue }

    var title: String {
        switch self {
        case .system: return "System"
        case .light:  return "Light"
        case .dark:   return "Dark"
        }
    }

    var symbol: String {
        switch self {
        case .system: return "circle.lefthalf.filled"
        case .light:  return "sun.max"
        case .dark:   return "moon"
        }
    }

    var nsAppearance: NSAppearance? {
        switch self {
        case .system: return nil
        case .light:  return NSAppearance(named: .aqua)
        case .dark:   return NSAppearance(named: .darkAqua)
        }
    }
}

/// Applies the chosen appearance.
///
/// Set on `NSApp` rather than with `.preferredColorScheme`: that modifier
/// doesn't reliably switch back to System once it has forced a scheme, and it
/// doesn't reach AppKit views (the code editors) or open panels.
struct Themed: ViewModifier {
    @AppStorage("appearance") private var appearance = AppAppearance.system

    func body(content: Content) -> some View {
        content
            .onAppear { NSApp.appearance = appearance.nsAppearance }
            .onChange(of: appearance) { _, value in NSApp.appearance = value.nsAppearance }
    }
}

extension View {
    func themed() -> some View { modifier(Themed()) }
}

/// Appearance switcher for the View menu (⌃⌘L / ⌃⌘D).
struct AppearanceCommands: View {
    @AppStorage("appearance") private var appearance = AppAppearance.system

    var body: some View {
        Picker("Appearance", selection: $appearance) {
            ForEach(AppAppearance.allCases) { option in
                Label(option.title, systemImage: option.symbol).tag(option)
            }
        }
        Button("Light Appearance") { appearance = .light }
            .keyboardShortcut("l", modifiers: [.command, .control])
        Button("Dark Appearance") { appearance = .dark }
            .keyboardShortcut("d", modifiers: [.command, .control])
    }
}

/// The toolbar's sun/moon menu.
struct AppearanceMenu: View {
    @AppStorage("appearance") private var appearance = AppAppearance.system

    var body: some View {
        Menu {
            Picker("Appearance", selection: $appearance) {
                ForEach(AppAppearance.allCases) { option in
                    Label(option.title, systemImage: option.symbol).tag(option)
                }
            }
            .pickerStyle(.inline)
        } label: {
            Label("Appearance", systemImage: appearance.symbol)
        }
        .help("Appearance: \(appearance.title)")
    }
}
