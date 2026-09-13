import AppKit
import SwiftUI

@main
struct RelayApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        Window("Relay", id: "main") {
            RootView()
                .frame(minWidth: 980, minHeight: 620)
                .themed()
        }
        .defaultSize(width: 1340, height: 860)
        .commands { RelayCommands() }

        Settings {
            SettingsView()
                .themed()
        }
    }
}

struct RelayCommands: Commands {
    private var ws: Workspace { .shared }

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("New Request") { ws.newTab() }
                .keyboardShortcut("t")
            Button("New Collection") { ws.addCollection() }
                .keyboardShortcut("n", modifiers: [.command, .shift])
            Divider()
            Button("Import…") { ws.sheet = .importer(paste: false) }
                .keyboardShortcut("o")
            Button("Paste cURL…") { ws.sheet = .importer(paste: true) }
                .keyboardShortcut("i", modifiers: [.command, .shift])
            Button("Export Everything…") { ws.exportEverything() }
        }

        CommandGroup(replacing: .saveItem) {
            Button("Save") { if let id = ws.selectedTabID { ws.save(id) } }
                .keyboardShortcut("s")
            Button("Save As…") { if let id = ws.selectedTabID { ws.sheet = .saveRequest(tab: id) } }
                .keyboardShortcut("s", modifiers: [.command, .shift])
            Divider()
            Button("Close Tab") { closeTabOrWindow() }
                .keyboardShortcut("w")
        }

        CommandMenu("Request") {
            Button("Send") { if let id = ws.selectedTabID { ws.send(id) } }
                .keyboardShortcut(.return)
            Button("Cancel") { if let id = ws.selectedTabID { ws.cancel(id) } }
                .keyboardShortcut(".")
            Divider()
            Button("Copy as cURL") {
                if let tab = ws.selectedTab { ws.copy(ws.curl(for: tab), "cURL") }
            }
            .keyboardShortcut("c", modifiers: [.command, .shift])
            Button("Copy cURL + Response") {
                guard let id = ws.selectedTabID, case .done(let result)? = ws.responses[id] else {
                    ws.flash("Send the request first")
                    return
                }
                ws.copy(Format.curlWithResponse(curl: result.curl, result: result, includeHeaders: false), "cURL and response")
            }
            .keyboardShortcut("c", modifiers: [.command, .option])
            Divider()
            Button("Go to URL") { ws.focusURL() }
                .keyboardShortcut("l")
            Button("Duplicate Tab") { if let id = ws.selectedTabID { ws.duplicateTab(id) } }
                .keyboardShortcut("d")
            Divider()
            Button("Next Tab") { ws.selectTab(offset: 1) }
                .keyboardShortcut("]", modifiers: [.command, .shift])
            Button("Previous Tab") { ws.selectTab(offset: -1) }
                .keyboardShortcut("[", modifiers: [.command, .shift])
        }

        CommandMenu("Environment") {
            Button("Manage Environments…") { ws.sheet = .environments(selected: ws.activeEnvironmentID) }
                .keyboardShortcut("e")
            Divider()
            Button("No Environment") { ws.activeEnvironmentID = nil }
                .keyboardShortcut("0", modifiers: [.command, .control])
            ForEach(Array(ws.environments.prefix(9).enumerated()), id: \.element.id) { index, environment in
                Button((ws.activeEnvironmentID == environment.id ? "✓ " : "") + environment.name) {
                    ws.activeEnvironmentID = environment.id
                }
                .keyboardShortcut(KeyEquivalent(Character("\(index + 1)")), modifiers: [.command, .control])
            }
        }

        CommandGroup(after: .toolbar) {
            AppearanceCommands()
            Divider()
            LayoutCommand()
        }
    }

    /// ⌘W closes the current tab in the main window, and behaves normally
    /// (closes the window) in Settings or when no tab is open.
    @MainActor
    private func closeTabOrWindow() {
        let key = NSApp.keyWindow
        let isSettings = key?.identifier?.rawValue.localizedCaseInsensitiveContains("settings") ?? false
        if let id = ws.selectedTabID, key?.isSheet == false, !isSettings {
            ws.close(id)
        } else {
            key?.performClose(nil)
        }
    }
}

private struct LayoutCommand: View {
    @AppStorage("layout") private var layout = PaneLayout.stacked

    var body: some View {
        Button(layout == .stacked ? "Show Response Beside Request" : "Show Response Below Request") {
            layout = layout == .stacked ? .sideBySide : .stacked
        }
        .keyboardShortcut("\\")
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationWillTerminate(_ notification: Notification) {
        MainActor.assumeIsolated { Workspace.shared.flush() }
    }

    /// Everything is saved continuously, so closing the window is quitting.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }
}
