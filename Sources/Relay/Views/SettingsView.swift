import AppKit
import SwiftUI

struct SettingsView: View {
    @AppStorage("appearance") private var appearance = AppAppearance.system
    @AppStorage("layout") private var layout = PaneLayout.stacked
    @AppStorage("timeout") private var timeout = 30.0
    @AppStorage("followRedirects") private var followRedirects = true
    @AppStorage("verifySSL") private var verifySSL = true

    var body: some View {
        Form {
            Section("Appearance") {
                Picker("Theme", selection: $appearance) {
                    ForEach(AppAppearance.allCases) { option in
                        Text(option.title).tag(option)
                    }
                }
                .pickerStyle(.segmented)
                Picker("Response", selection: $layout) {
                    Text("Below the request").tag(PaneLayout.stacked)
                    Text("Beside the request").tag(PaneLayout.sideBySide)
                }
            }

            Section("Requests") {
                Stepper(value: $timeout, in: 5...600, step: 5) {
                    Text("Timeout: \(Int(timeout)) seconds")
                }
                Toggle("Follow redirects", isOn: $followRedirects)
                VStack(alignment: .leading, spacing: 3) {
                    Toggle("Verify SSL certificates", isOn: $verifySSL)
                    Text("Turn off for local servers with self-signed certificates.")
                        .font(.app(11))
                        .foregroundStyle(.secondary)
                }
            }

            Section("Data") {
                LabeledContent("Saved in") {
                    Text(Storage.folder.path)
                        .font(.code(11))
                        .textSelection(.enabled)
                }
                HStack {
                    Button("Show in Finder") {
                        NSWorkspace.shared.activateFileViewerSelecting([Storage.workspaceURL])
                    }
                    Button("Export Everything…") { Workspace.shared.exportEverything() }
                }
            }
        }
        .formStyle(.grouped)
        .font(.app(13))
        .frame(width: 540, height: 480)
    }
}
