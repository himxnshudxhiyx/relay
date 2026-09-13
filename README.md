<p align="center">
  <img src="docs/icon.png" width="120" alt="Relay app icon">
</p>

<h1 align="center">Relay</h1>

<p align="center">
  A native macOS API client, like Postman, without the clutter.
</p>

<p align="center">
  <img alt="platform" src="https://img.shields.io/badge/macOS-14%2B-black">
  <img alt="swift" src="https://img.shields.io/badge/Swift-6-orange">
  <img alt="universal" src="https://img.shields.io/badge/Apple%20silicon%20%2B%20Intel-universal-blue">
</p>

Built with SwiftUI. No dependencies, no account, no sync. Everything stays on your Mac.

---

## Features

**Requests**
- Tabs, with unsaved-change dots. A single click in the sidebar opens a preview tab that the next click reuses, so tabs don't pile up.
- Params stay in sync with the URL's query string, in both directions.
- Headers with common presets, plus an **Added automatically** list, so nothing sent on the wire is a surprise.
- Bulk Edit for params, headers and variables (`key: value` per line, `//` disables a row).
- Body: JSON (syntax highlighting, validity check, Beautify), Text, XML, Form URL-Encoded, Multipart with file uploads, Binary File.
- Auth: Bearer, Basic, API Key (header or query). Requests and folders can **inherit** auth from their folder or collection.

**Variables & environments**
- `{{variables}}` in URLs, headers, bodies and auth.
- Environments override collection variables. Mark secrets with the lock to mask them.
- Chips under the URL show every variable the request uses, its value and where it comes from. A red chip means it isn't defined; click it to add it.
- Built-ins: `{{$guid}}`, `{{$timestamp}}`, `{{$isoTimestamp}}`, `{{$randomInt}}`.

**cURL**
- **Import:** paste a cURL straight into the URL field, or use ⇧⌘I. Handles Chrome/Firefox DevTools, Postman, Bruno and terminal output: `'…'`, `"…"` and `$'…'` quoting, line continuations, `-X -H -d --data-raw --data-urlencode --json -F -u -G -I -A -b -e`, and combined flags like `-sSL`.
- **Export:** copy as cURL with values filled in, or keeping `{{variables}}`.
- **Copy cURL + Response** (⌥⌘C): the exact command that was sent, the status line and the pretty-printed body, optionally with headers. Ready to paste into a ticket or chat.

**Responses**
- Status, time (with time to first byte), size.
- Pretty/Raw JSON and XML with highlighting that keeps key order and number formatting as the server sent them. Fast on multi-megabyte bodies.
- ⌘F search, line-wrap toggle, image preview, save to file.
- Response headers, plus a **Sent** tab showing the final URL, headers, body and redirects.

**Import / export**
- Postman collections (v2.0 and v2.1), environments and full data dumps.
- Export any collection or environment as Postman v2.1 JSON, or everything as one backup file.
- Pre-request/test scripts and saved examples from Postman aren't run, but they're kept and written back on export.

**Also**
- Light, dark or system theme (⌃⌘L / ⌃⌘D). Poppins for the interface, monospace for code.
- Response below or beside the request (⌘\\).
- Drag requests and folders between folders and collections.
- History of everything you send.

## Install

```bash
./build.sh --install
```

Builds a universal `Relay.app` and copies it to `/Applications`.

### On another Mac

```bash
./build.sh --dmg
```

This writes `dist/Relay-1.0.dmg` (and a `.zip`). Copy it over, open it, and drag **Relay** to Applications.

**The first launch needs one extra step.** The app is ad-hoc signed, not notarized by Apple, so macOS blocks it the first time:

1. Open Relay once and dismiss the warning.
2. Go to **System Settings → Privacy & Security**, scroll down, and click **Open Anyway** next to Relay.

Or, in Terminal: `xattr -dr com.apple.quarantine /Applications/Relay.app`

The other Mac needs macOS 14 (Sonoma) or later. Both Apple silicon and Intel work.

## Keyboard shortcuts

| Action | Shortcut |
| --- | --- |
| Send request | ⌘↩ |
| Cancel request | ⌘. |
| New request tab | ⌘T |
| Close tab | ⌘W |
| Save / Save As | ⌘S / ⇧⌘S |
| Next / previous tab | ⇧⌘] / ⇧⌘[ |
| Duplicate tab | ⌘D |
| Go to URL field | ⌘L |
| Import file | ⌘O |
| Paste cURL | ⇧⌘I |
| Copy as cURL | ⇧⌘C |
| Copy cURL + response | ⌥⌘C |
| Manage environments | ⌘E |
| Switch environment | ⌃⌘1…9, ⌃⌘0 for none |
| Response below / beside | ⌘\\ |
| Light / dark appearance | ⌃⌘L / ⌃⌘D |

## Your data

Everything is saved as you go, in `~/Library/Application Support/Relay/`:

- `workspace.json`: collections, environments and history.
- `session.json`: open tabs, including unsaved edits.
- `workspace.backup.json`: a copy of the workspace from the last launch.

If a file ever can't be read, it's moved aside (`*.unreadable-<time>.json`) rather than overwritten. Values, including secrets, are stored as plain JSON; secret masking only hides them on screen.

## Development

```bash
swift build          # debug build
swift test           # unit tests
./build.sh --clean   # fresh release bundle in build/
```

```
Sources/Relay/
  Models/    APIRequest, collections, environments (value types, lenient Codable)
  Core/      cURL parser/generator, Postman import/export, HTTP client,
             variables, URL/query sync, JSON/XML formatting
  Store/     Workspace (all app state and actions), persistence, first-run sample
  Support/   Theme, typography, appearance, panels and alerts
  Views/     SwiftUI screens and the NSTextView-based code editor
tools/MakeIcon.swift   renders the app icon
```

Poppins is licensed under the SIL Open Font License; see `Resources/Fonts/OFL.txt`.
