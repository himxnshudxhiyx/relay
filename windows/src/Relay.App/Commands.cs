using Avalonia.Input;
using Relay.Core;

namespace Relay.App;

/// <summary>
/// Every menu command with its shortcut: the Mac app's menu bar, with ⌘ as Ctrl
/// and ⌃⌘ as Ctrl+Alt. One list drives both the keyboard and the ⋯ menu.
/// </summary>
static class Commands
{
    public sealed record Command(string Title, KeyGesture? Gesture, Func<Task> Run);

    static Workspace Ws => Workspace.Shared;

    static Func<Task> Sync(Action action) => () => { action(); return Task.CompletedTask; };

    static Func<Task> OnTab(Func<TabState, Task> action) => () => Ws.SelectedTab is { } tab ? action(tab) : Task.CompletedTask;
    static Func<Task> OnTab(Action<TabState> action) => OnTab(t => { action(t); return Task.CompletedTask; });

    static KeyGesture G(Key key, KeyModifiers modifiers = KeyModifiers.Control) => new(key, modifiers);

    /// <summary>Groups of commands; a null entry is a separator.</summary>
    public static readonly (string Group, Command?[] Items)[] Menu =
    [
        ("File",
        [
            new("New Request", G(Key.T), Sync(() => Ws.NewTab())),
            new("New Collection", G(Key.N, KeyModifiers.Control | KeyModifiers.Shift), Sync(() => Ws.AddCollection())),
            null,
            new("Import…", G(Key.O), () => Dialogs.Import(paste: false)),
            new("Paste cURL…", G(Key.I, KeyModifiers.Control | KeyModifiers.Shift), () => Dialogs.Import(paste: true)),
            new("Export Everything…", null, () => Ws.ExportEverything()),
            null,
            new("Save", G(Key.S), OnTab(async t => { await Ws.Save(t); })),
            new("Save As…", G(Key.S, KeyModifiers.Control | KeyModifiers.Shift), OnTab(async t => { await Dialogs.SaveRequest(t); })),
            new("Close Tab", G(Key.W), OnTab(t => Ws.Close(t))),
            null,
            new("Settings…", G(Key.OemComma), () => Dialogs.AppSettings()),
        ]),
        ("Request",
        [
            new("Send", G(Key.Enter), OnTab(t => Ws.Send(t))),
            new("Cancel", G(Key.OemPeriod), OnTab(t => Ws.Cancel(t))),
            null,
            new("Copy as cURL", G(Key.C, KeyModifiers.Control | KeyModifiers.Shift), OnTab(t => Ws.Copy(Ws.Curl(t), "cURL"))),
            new("Copy cURL + Response", G(Key.C, KeyModifiers.Control | KeyModifiers.Alt), OnTab(async t =>
            {
                if (t.Response is ResponseState.Done done)
                    await Ws.Copy(Format.CurlWithResponse(done.Result.Curl, done.Result, includeHeaders: false), "cURL and response");
                else
                    Ws.Flash("Send the request first");
            })),
            null,
            new("Go to URL", G(Key.L), Sync(() => Ws.FocusUrl())),
            new("Search Requests", G(Key.K), Sync(() => Ws.FocusSearch())),
            new("Duplicate Tab", G(Key.D), OnTab(t => Ws.DuplicateTab(t))),
            null,
            new("Next Tab", G(Key.Tab), Sync(() => Ws.SelectTab(1))),
            new("Previous Tab", G(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift), Sync(() => Ws.SelectTab(-1))),
        ]),
        ("Environment",
        [
            new("Manage Environments…", G(Key.E), () => Dialogs.Environments(Ws.ActiveEnvironmentID)),
            new("No Environment", G(Key.D0, KeyModifiers.Control | KeyModifiers.Alt), Sync(() => Ws.ActiveEnvironmentID = null)),
        ]),
        ("View",
        [
            new("Response Below / Beside", G(Key.OemBackslash), Sync(ToggleLayout)),
            new("Light Appearance", G(Key.L, KeyModifiers.Control | KeyModifiers.Alt), Sync(() => Settings.Shared.Appearance = Appearance.Light)),
            new("Dark Appearance", G(Key.D, KeyModifiers.Control | KeyModifiers.Alt), Sync(() => Settings.Shared.Appearance = Appearance.Dark)),
            new("System Appearance", null, Sync(() => Settings.Shared.Appearance = Appearance.System)),
        ]),
    ];

    static void ToggleLayout() =>
        Settings.Shared.Layout = Settings.Shared.Layout == PaneLayout.Stacked ? PaneLayout.SideBySide : PaneLayout.Stacked;

    /// <summary>Runs the command bound to this key, if any. Ctrl+Alt+1…9 picks an environment.</summary>
    public static bool Handle(KeyEventArgs e)
    {
        var modifiers = e.KeyModifiers;
        if (modifiers == (KeyModifiers.Control | KeyModifiers.Alt) && e.Key is >= Key.D1 and <= Key.D9)
        {
            var index = e.Key - Key.D1;
            if (index < Ws.Environments.Count) Ws.ActiveEnvironmentID = Ws.Environments[index].Id;
            return true;
        }
        // The backslash key reports differently across keyboard layouts.
        var key = e.Key == Key.Oem5 ? Key.OemBackslash : e.Key;
        foreach (var (_, items) in Menu)
            foreach (var command in items)
                if (command?.Gesture is { } g && g.Key == key && g.KeyModifiers == modifiers)
                {
                    _ = command.Run();
                    return true;
                }
        return false;
    }
}
