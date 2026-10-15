# Relay for Windows

The same API client as the Mac app, for Windows 10 and 11 (x64 and ARM).
Built with .NET 10 and [Avalonia](https://avaloniaui.net), with the same
carbon-and-lime design, features and keyboard shortcuts (Ctrl in place of ⌘).

## Install

Unzip `Relay-<version>-Windows-x64.zip` (or `-arm64` on ARM PCs) and run
`Relay.exe`. It's a single self-contained file: nothing to install, no .NET
needed.

**The first launch needs one extra click.** The exe isn't code-signed, so
SmartScreen shows "Windows protected your PC": click **More info**, then
**Run anyway**.

## Build

From a Mac or a PC with the [.NET 10 SDK](https://dot.net):

```bash
./build.sh            # dist/Relay-<version>-Windows-x64.zip and -arm64.zip
./build.sh --test     # run the tests first
```

On the Mac, `dotnet run --project src/Relay.App` runs the Windows app there too
(Avalonia is cross-platform), which is how the UI is checked without a PC.

## Your data

Saved as you go in `%APPDATA%\Relay\`: the same `workspace.json`,
`session.json` and `workspace.backup.json` as the Mac app, in the same format.
Copy `workspace.json` between a Mac (`~/Library/Application Support/Relay/`)
and a PC to move collections, environments and history across. Preferences
are in `settings.json`.

## Layout

```
src/Relay.Core/     models and logic, ported from Sources/Relay/{Models,Core,Store}
src/Relay.App/      the Avalonia app: Workspace (state and actions), Views/, Theme.axaml
tests/              xunit tests, ported from Tests/RelayTests plus Windows-specific ones
```

Differences from the Mac app: the Mac menu bar is the ⋯ menu in the title bar;
PDFs in Preview are drawn with PDFium (the engine inside Chrome) instead of PDFKit.
