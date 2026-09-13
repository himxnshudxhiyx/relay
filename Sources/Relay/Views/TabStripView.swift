import SwiftUI

struct TabStripView: View {
    private let ws = Workspace.shared

    var body: some View {
        HStack(spacing: 0) {
            ScrollViewReader { proxy in
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 0) {
                        ForEach(ws.tabs) { tab in
                            TabChip(tab: tab).id(tab.id)
                        }
                    }
                }
                .onChange(of: ws.selectedTabID) { _, id in
                    guard let id else { return }
                    withAnimation(.easeOut(duration: 0.15)) { proxy.scrollTo(id) }
                }
            }
            IconButton(symbol: "plus", help: "New Request (⌘T)") { ws.newTab() }
                .padding(.horizontal, 6)
        }
        .frame(height: 36)
        .background(Theme.chrome)
    }
}

private struct TabChip: View {
    let tab: RequestTab
    private let ws = Workspace.shared
    @State private var hovering = false

    var body: some View {
        let selected = ws.selectedTabID == tab.id
        let loading = ws.responses[tab.id]?.isLoading ?? false

        HStack(spacing: 7) {
            MethodBadge(method: tab.draft.method, size: 9.5)
            Text(tab.draft.name)
                .font(.app(12, selected ? .medium : .regular))
                .foregroundStyle(selected ? Color.primary : Color.secondary)
                .opacity(tab.isPreview ? 0.8 : 1)
                .lineLimit(1)
            ZStack {
                if loading {
                    ProgressView().controlSize(.mini)
                } else if hovering || (selected && !tab.isDirty) {
                    Button { ws.close(tab.id) } label: {
                        Image(systemName: "xmark")
                            .font(.system(size: 8.5, weight: .bold))
                            .foregroundStyle(.secondary)
                            .frame(width: 16, height: 16)
                            .background(Circle().fill(hovering ? Theme.hover : .clear))
                    }
                    .buttonStyle(.plain)
                    .help("Close Tab (⌘W)")
                } else if tab.isDirty {
                    Circle().fill(Theme.accent).frame(width: 7, height: 7)
                }
            }
            .frame(width: 16, height: 16)
        }
        .padding(.leading, 12)
        .padding(.trailing, 7)
        .frame(height: 36)
        .frame(maxWidth: 230)
        .background(selected ? Theme.canvas : (hovering ? Theme.hover : .clear))
        .overlay(alignment: .top) {
            Rectangle().fill(selected ? Theme.accent : .clear).frame(height: 2)
        }
        .overlay(alignment: .trailing) {
            Rectangle().fill(Theme.line).frame(width: 1)
        }
        .contentShape(Rectangle())
        .onTapGesture { ws.selectedTabID = tab.id }
        .onHover { hovering = $0 }
        .help(tab.isPreview ? "\(tab.draft.name): preview. Edit it or double-click in the sidebar to keep it open." : tab.draft.url)
        .contextMenu {
            Button("Close Tab") { ws.close(tab.id) }
            Button("Close Other Tabs") { ws.closeOthers(tab.id) }
            Button("Close All Tabs") { ws.closeAll() }
            Divider()
            Button("Duplicate Tab") { ws.duplicateTab(tab.id) }
            Button("Copy as cURL") { ws.copy(ws.curl(for: tab), "cURL") }
        }
    }
}
