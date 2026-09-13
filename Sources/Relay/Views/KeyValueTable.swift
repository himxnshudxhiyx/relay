import SwiftUI

/// Editable key/value rows with a blank row always waiting at the end.
///
/// The blank row isn't stored. It has a stable id held here, and the moment
/// something is typed into it, it's appended to `rows` under that same id — so
/// SwiftUI keeps focus in the field being typed in, and a new blank row
/// appears below it.
struct KeyValueTable: View {
    @Binding var rows: [KeyValue]
    var keyPlaceholder = "Key"
    var valuePlaceholder = "Value"
    var allowsFiles = false
    var allowsSecrets = false

    @State private var phantomID = UUID()

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 0) {
                Color.clear.frame(width: 34)
                Text(keyPlaceholder.uppercased())
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 8)
                Text(valuePlaceholder.uppercased())
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 9)
                Color.clear.frame(width: trailingWidth + 4)
            }
            .font(.app(10, .semibold))
            .foregroundStyle(.tertiary)
            .frame(height: 26)
            .background(Theme.field.opacity(0.6))

            ForEach(displayRows) { row in
                Divider()
                KeyValueRow(row: binding(for: row.id), isPhantom: row.id == phantomID,
                            keyPlaceholder: keyPlaceholder, valuePlaceholder: valuePlaceholder,
                            allowsFiles: allowsFiles, allowsSecrets: allowsSecrets, trailingWidth: trailingWidth) {
                    rows.removeAll { $0.id == row.id }
                }
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.line))
    }

    private var trailingWidth: CGFloat { allowsFiles || allowsSecrets ? 52 : 28 }

    private var displayRows: [KeyValue] {
        var phantom = KeyValue()
        phantom.id = phantomID
        return rows + [phantom]
    }

    private func binding(for id: UUID) -> Binding<KeyValue> {
        Binding(
            get: {
                if let row = rows.first(where: { $0.id == id }) { return row }
                var phantom = KeyValue()
                phantom.id = id
                return phantom
            },
            set: { newValue in
                if let i = rows.firstIndex(where: { $0.id == id }) {
                    rows[i] = newValue
                } else if !newValue.isBlank {
                    rows.append(newValue)
                    phantomID = UUID()
                }
            }
        )
    }
}

private struct KeyValueRow: View {
    @Binding var row: KeyValue
    let isPhantom: Bool
    let keyPlaceholder: String
    let valuePlaceholder: String
    let allowsFiles: Bool
    let allowsSecrets: Bool
    let trailingWidth: CGFloat
    let onDelete: () -> Void

    @State private var hovering = false
    @State private var revealed = false

    var body: some View {
        HStack(spacing: 0) {
            Toggle("Enabled", isOn: $row.enabled)
                .toggleStyle(.checkbox)
                .labelsHidden()
                .frame(width: 34)
                .opacity(isPhantom ? 0 : 1)
                .disabled(isPhantom)

            VariableTextField(text: $row.key, placeholder: keyPlaceholder)
                .padding(.horizontal, 8)
                .frame(maxWidth: .infinity, alignment: .leading)

            Rectangle().fill(Theme.line).frame(width: 1)

            valueField
                .padding(.horizontal, 8)
                .frame(maxWidth: .infinity, alignment: .leading)

            HStack(spacing: 0) {
                if !isPhantom {
                    if allowsFiles {
                        IconButton(symbol: row.isFile ? "doc.fill" : "doc", help: row.isFile ? "Send text instead" : "Send a file",
                                   size: 11, tint: row.isFile ? Theme.accent : .secondary) {
                            row.isFile.toggle()
                            row.value = ""
                        }
                    }
                    if allowsSecrets {
                        IconButton(symbol: row.isSecret ? "lock.fill" : "lock.open", help: row.isSecret ? "Secret: the value is masked" : "Mark as secret",
                                   size: 11, tint: row.isSecret ? Theme.accent : .secondary) {
                            row.isSecret.toggle()
                            revealed = false
                        }
                    }
                    IconButton(symbol: "trash", help: "Remove row", size: 11, action: onDelete)
                        .opacity(hovering ? 1 : 0)
                }
            }
            .frame(width: trailingWidth, alignment: .trailing)
            .padding(.trailing, 4)
        }
        .frame(height: 32)
        .opacity(row.enabled || isPhantom ? 1 : 0.5)
        .background(hovering ? Theme.hover.opacity(0.5) : .clear)
        .onHover { hovering = $0 }
    }

    @ViewBuilder private var valueField: some View {
        if row.isFile {
            HStack(spacing: 6) {
                Text(row.value.isEmpty ? "No file chosen" : (row.value as NSString).lastPathComponent)
                    .font(.code(12))
                    .foregroundStyle(row.value.isEmpty ? .tertiary : .primary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .help(row.value)
                Spacer(minLength: 4)
                Button("Choose…") {
                    if let url = FilePanels.open() { row.value = url.path }
                }
                .buttonStyle(.plain)
                .font(.app(11.5, .medium))
                .foregroundStyle(Theme.accent)
            }
        } else if row.isSecret && !revealed {
            HStack(spacing: 2) {
                SecureField(valuePlaceholder, text: $row.value)
                    .textFieldStyle(.plain)
                    .font(.code(12))
                IconButton(symbol: "eye", help: "Show value", size: 10) { revealed = true }
            }
        } else {
            VariableTextField(text: $row.value, placeholder: valuePlaceholder)
        }
    }
}
