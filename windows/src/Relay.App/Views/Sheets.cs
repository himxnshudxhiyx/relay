using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Relay.Core;

namespace Relay.App.Views;

// Ported from Sheets.swift, SettingsView.swift and the welcome / variables views in RootView.swift.

/// <summary>A modal window with a title, content and a button row; Escape closes it.</summary>
abstract class SheetWindow : Window
{
    protected static Workspace Ws => Workspace.Shared;

    protected SheetWindow(string title, double width, double? height = null)
    {
        Title = title;
        Width = width;
        CanMinimize = false;
        CanMaximize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (height is { } h) { Height = h; CanResize = true; } else { SizeToContent = SizeToContent.Height; CanResize = false; }
        this.Bind(BackgroundProperty, "Canvas");
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    protected static Control Buttons(params Button[] buttons)
    {
        var row = UI.Row(8, buttons);
        row.HorizontalAlignment = HorizontalAlignment.Right;
        return row;
    }

    protected static Button Done(Window window) => UI.Button("Done", "primary", window.Close).With(b => b.MinWidth = 80);
}

// MARK: - Save As

class SaveRequestSheet : SheetWindow
{
    public bool Saved { get; private set; }

    readonly record struct Destination(Guid Collection, Guid? Folder, string Title);

    public SaveRequestSheet(TabState tab) : base("Save Request", 460)
    {
        var name = new TextBox { Text = tab.Draft.Name, PlaceholderText = "Request name" };
        var destinations = Ws.Collections.SelectMany(c =>
            new[] { new Destination(c.Id, null, c.Name) }.Concat(c.Items.FolderPaths().Select(p => new Destination(c.Id, p.Folder.Id, $"{c.Name} / {p.Path}")))).ToList();
        var current = destinations.FindIndex(d => d.Collection == tab.Model.CollectionID);
        var picker = new ComboBox { ItemsSource = destinations.Select(d => d.Title).ToList(), SelectedIndex = destinations.Count == 0 ? -1 : Math.Max(0, current), HorizontalAlignment = HorizontalAlignment.Stretch };
        var save = UI.Button("Save", "primary", () =>
        {
            if (picker.SelectedIndex < 0 || string.IsNullOrWhiteSpace(name.Text)) return;
            var d = destinations[picker.SelectedIndex];
            Ws.SaveAs(tab, name.Text.Trim(), d.Collection, d.Folder);
            Saved = true;
            Close();
        });
        void Update() => save.IsEnabled = picker.SelectedIndex >= 0 && !string.IsNullOrWhiteSpace(name.Text);
        name.TextChanged += (_, _) => Update();
        picker.SelectionChanged += (_, _) => Update();
        Update();
        var newCollection = UI.Button("New Collection", "secondary", () =>
        {
            var id = Ws.AddCollection("New Collection", rename: false);
            var c = Ws.Collection(id)!;
            destinations.Add(new Destination(id, null, c.Name));
            picker.ItemsSource = destinations.Select(d => d.Title).ToList();
            picker.SelectedIndex = destinations.Count - 1;
        });
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) save.Command?.Execute(null); };
        Content = UI.Column(14,
            UI.Text("Save Request", 16, FontWeight.SemiBold),
            UI.Column(6, UI.Text("Name", 12, FontWeight.Medium, "secondary"), name),
            UI.Column(6, UI.Text("Save to", 12, FontWeight.Medium, "secondary"),
                destinations.Count == 0 ? UI.Text("No collections yet. Create one to save into.", 12, default, "secondary") : picker),
            new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { newCollection, Buttons(UI.Button("Cancel", "secondary", Close), save).With(b => Grid.SetColumn(b, 1)) } })
            .With(c => c.Margin = new Thickness(22));
        Opened += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}

// MARK: - Environments

class EnvironmentsSheet : SheetWindow
{
    Guid? _selected;
    readonly StackPanel _list = new() { Spacing = 2 };
    readonly ContentControl _editor = new();

    public EnvironmentsSheet(Guid? selected) : base("Environments", 860, 560)
    {
        _selected = selected ?? Ws.Environments.FirstOrDefault()?.Id;
        var add = UI.Button(UI.Row(6, new Icon { Kind = "plus", Size = 12 }, UI.Text("New Environment", 12.5, FontWeight.Medium)), "link", () =>
        {
            _selected = Ws.AddEnvironment();
            Fill();
        });
        var left = new DockPanel
        {
            Width = 230,
            Children =
            {
                add.With(a => { DockPanel.SetDock(a, Dock.Bottom); a.Margin = new Thickness(8); }),
                new ScrollViewer { Content = _list, Margin = new Thickness(8) },
            },
        }.Bind(BackgroundProperty, "Chrome");
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(16, 10) };
        footer.Children.Add(UI.Text("Use a variable anywhere as {{name}}: URLs, headers, bodies and auth.", 11.5, default, "secondary"));
        footer.Children.Add(Buttons(Done(this)).With(b => Grid.SetColumn(b, 1)));
        Content = new DockPanel
        {
            Children =
            {
                new StackPanel { Children = { UI.Hairline(), footer }, [DockPanel.DockProperty] = Dock.Bottom },
                left.With(l => DockPanel.SetDock(l, Dock.Left)),
                UI.Hairline(vertical: true).With(h => DockPanel.SetDock(h, Dock.Left)),
                _editor,
            },
        };
        Fill();
    }

    void Fill()
    {
        _list.Children.Clear();
        foreach (var env in Ws.Environments)
        {
            var isActive = Ws.ActiveEnvironmentID == env.Id;
            var name = UI.Text(env.Name, 12.5, env.Id == _selected ? FontWeight.SemiBold : FontWeight.Normal);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { name } };
            if (isActive) content.Children.Add(UI.Text("ACTIVE", 9.5, FontWeight.SemiBold, "accent").With(t => Grid.SetColumn(t, 1)));
            var row = new Border { Padding = new Thickness(10, 7), CornerRadius = new CornerRadius(7), Child = content, Cursor = new Cursor(StandardCursorType.Hand) };
            if (env.Id == _selected) row.Bind(BackgroundProperty, "Selected"); else row.Background = Brushes.Transparent;
            row.Tapped += (_, _) => { _selected = env.Id; Fill(); };
            _list.Children.Add(row);
        }
        var selected = Ws.Environments.FirstOrDefault(e => e.Id == _selected);
        _editor.Content = selected == null
            ? UI.EmptyState("layers", "No environment selected",
                "Environments hold values like {{baseUrl}} and {{token}}. Switch between them to point the same requests at a different server.",
                UI.Button("New Environment", "primary", () => { _selected = Ws.AddEnvironment(); Fill(); }))
            : Editor(selected);
    }

    Control Editor(ApiEnvironment env)
    {
        var name = new TextBox { FontSize = 15, FontWeight = FontWeight.SemiBold, PlaceholderText = "Environment name" };
        name[!TextBox.TextProperty] = new Binding(nameof(ApiEnvironment.Name)) { Source = env, Mode = BindingMode.TwoWay };
        name.LostFocus += (_, _) => Fill();
        var active = Ws.ActiveEnvironmentID == env.Id;
        var activate = UI.Button(active ? "Active" : "Set Active", active ? "secondary" : "primary", () => { Ws.ActiveEnvironmentID = active ? null : env.Id; Fill(); },
            active ? "Click to turn off" : "Use this environment's values");
        activate.Height = 30;
        var more = UI.IconButton("more", "More", () => { });
        more.Flyout = new MenuFlyout
        {
            Items =
            {
                new MenuItem { Header = "Duplicate", Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { Ws.DuplicateEnvironment(env); Fill(); }) },
                new MenuItem { Header = "Export as Postman Environment…", Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => Ws.ExportEnvironment(env)) },
                new Separator(),
                new MenuItem { Header = "Delete…", Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(async () => { await Ws.DeleteEnvironment(env); _selected = Ws.Environments.FirstOrDefault()?.Id; Fill(); }) },
            },
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(20, 16, 16, 4) };
        header.Children.Add(name);
        header.Children.Add(activate.With(a => Grid.SetColumn(a, 1)));
        header.Children.Add(more.With(m => Grid.SetColumn(m, 2)));
        return new DockPanel
        {
            Children =
            {
                header.With(h => DockPanel.SetDock(h, Dock.Top)),
                new KeyValueTable(env.Variables, "Use the key to mask a secret's value on screen.", "Variable", "Value", allowsSecrets: true,
                    scope: () => new VariableScope { Environment = env.Variables.ToList() }),
            },
        };
    }
}

// MARK: - Collection / folder settings

class SettingsSheet : SheetWindow
{
    enum Page { General, Auth, Variables }
    readonly Guid _collectionId;
    readonly Guid? _folderId;
    Page _page;
    readonly ContentControl _tabs = new() { Margin = new Thickness(20, 4, 20, 0) };
    readonly ContentControl _body = new();

    public SettingsSheet(Guid collectionId, Guid? folderId) : base("Settings", 640, 560)
    {
        _collectionId = collectionId;
        _folderId = folderId;
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(16, 10) };
        footer.Children.Add(UI.Text(folderId == null
            ? "Requests and folders set to “Inherit from Parent” use this collection's auth."
            : "Requests in this folder set to “Inherit” use this folder's auth.", 11.5, default, "secondary"));
        footer.Children.Add(Buttons(Done(this)).With(b => Grid.SetColumn(b, 1)));
        Content = new DockPanel
        {
            Children =
            {
                new StackPanel { Children = { UI.Hairline(), footer }, [DockPanel.DockProperty] = Dock.Bottom },
                new StackPanel { Children = { _tabs, UI.Hairline() }, [DockPanel.DockProperty] = Dock.Top },
                _body,
            },
        };
        Build();
    }

    void Build()
    {
        var collection = Ws.Collection(_collectionId);
        var folder = _folderId is { } f ? collection?.Items.Item(f) as Folder : null;
        if (collection == null || (_folderId != null && folder == null)) { Close(); return; }
        Title = folder == null ? $"{collection.Name} Settings" : $"{folder.Name} Settings";
        var auth = folder?.Auth ?? collection.Auth;
        var pages = new List<(Page, string, string?)> { (Page.General, "General", null), (Page.Auth, "Auth", auth.Type is AuthType.None or AuthType.Inherit ? null : auth.Type.Title()) };
        if (folder == null) pages.Add((Page.Variables, "Variables", collection.Variables.Count == 0 ? null : $"{collection.Variables.Count}"));
        _tabs.Content = UI.UnderlineTabs(pages, _page, p => { _page = p; Build(); });

        _body.Content = _page switch
        {
            Page.Auth => new AuthEditor(auth, allowsInherit: folder != null, () =>
            {
                var chain = collection.Items.FolderChain(folder!.Id) ?? [];
                var parent = chain.LastOrDefault(x => x.Auth.Type != AuthType.Inherit);
                var source = parent?.Name ?? collection.Name;
                var parentAuth = parent?.Auth ?? collection.Auth;
                return parentAuth.Type is AuthType.None or AuthType.Inherit
                    ? $"Inherits from “{source}”, which sends no auth."
                    : $"Uses {parentAuth.Type.Title()} from “{source}”.";
            }),
            Page.Variables => new KeyValueTable(collection.Variables, "Available to every request here. An environment variable with the same name wins.",
                "Variable", "Value", allowsSecrets: true, scope: () => new VariableScope { Collection = collection.Variables.ToList() }),
            _ => General(collection, folder),
        };
    }

    Control General(ApiCollection collection, Folder? folder)
    {
        var name = new TextBox();
        name[!TextBox.TextProperty] = new Binding(nameof(ApiCollection.Name)) { Source = (object?)folder ?? collection, Mode = BindingMode.TwoWay };
        name.LostFocus += (_, _) => Ws.TreeVersion++;
        var docs = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 140, VerticalContentAlignment = VerticalAlignment.Top, PlaceholderText = "Notes about this " + (folder == null ? "collection" : "folder") };
        docs[!TextBox.TextProperty] = new Binding("Docs") { Source = (object?)folder ?? collection, Mode = BindingMode.TwoWay };
        var stack = UI.Column(14,
            Labeled("Name", name),
            Labeled("Requests", UI.Text($"{(folder?.Items ?? collection.Items).RequestCount()}", 13)),
            Labeled("Description", docs));
        if (folder == null)
            stack.Children.Add(Labeled("", UI.Button("Export as Postman Collection…", "secondary", () => _ = Ws.ExportCollection(collection)).With(b => b.HorizontalAlignment = HorizontalAlignment.Left)));
        stack.Margin = new Thickness(20, 16);
        return new ScrollViewer { Content = stack };
    }

    static Control Labeled(string label, Control content)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("100,*"), ColumnSpacing = 12 };
        grid.Children.Add(UI.Text(label, 12, FontWeight.Medium, "secondary").With(t => t.VerticalAlignment = VerticalAlignment.Top));
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }
}

// MARK: - Import

class ImportSheet : SheetWindow
{
    bool _paste;
    object? _preview;   // ApiRequest (cURL) or ImportResult (Postman)
    string? _error;
    string? _fileName;
    string _text = "";
    readonly ContentControl _source = new();
    readonly ContentControl _previewHost = new();
    readonly Button _commit;
    readonly ContentControl _modes = new();

    public ImportSheet(bool paste) : base("Import", 600)
    {
        _paste = paste;
        _commit = UI.Button("Import", "primary", Commit);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(UI.Text("Import", 16, FontWeight.SemiBold));
        header.Children.Add(_modes.With(m => Grid.SetColumn(m, 1)));
        Content = UI.Column(14, header, _source, _previewHost, Buttons(UI.Button("Cancel", "secondary", Close), _commit))
            .With(c => c.Margin = new Thickness(20));
        Build();
        Opened += async (_, _) =>
        {
            if (_paste && await Dialogs.PasteText() is { } clip && IsCurl(clip))
            {
                _text = clip;
                Parse(clip);
                Build();
            }
        };
    }

    void Build()
    {
        _modes.Content = UI.Pills([(false, "File"), (true, "Paste")], _paste, p => { _paste = p; _preview = null; _error = null; Build(); }, stretch: true).With(p => p.Width = 170);
        if (_paste)
        {
            var editor = new CodeView { Text = _text, Language = CodeLanguage.Plain, HighlightVariables = false, Height = 230 };
            editor.PropertyChanged += (_, e) =>
            {
                if (e.Property != CodeView.TextProperty) return;
                _text = editor.Text;
                Parse(_text);
                ShowPreview();
            };
            _source.Content = UI.Column(8,
                UI.Text("Paste a cURL command, or the JSON of a Postman collection or environment.", 12, default, "secondary"),
                new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true, Child = editor }
                    .Bind(Border.BackgroundProperty, "Field").Bind(Border.BorderBrushProperty, "Line"));
        }
        else
        {
            _source.Content = DropZone();
        }
        ShowPreview();
    }

    Control DropZone()
    {
        var zone = new Border
        {
            Height = 230,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1.5),
            Child = UI.Column(10,
                new Icon { Kind = "import", Size = 30, Thickness = 1.1, HorizontalAlignment = HorizontalAlignment.Center }.Bind(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, "TextSecondary"),
                UI.Text(_fileName ?? "Drop a file here", 13, FontWeight.SemiBold).With(t => t.HorizontalAlignment = HorizontalAlignment.Center),
                new TextBlock { Text = "Postman collections (v2.0 and v2.1), environments and data dumps, Relay backups, or a text file containing a cURL command.", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 400, Classes = { "secondary" } },
                UI.Button("Choose File…", "secondary", async () =>
                {
                    if (await Dialogs.OpenFile("Import") is { } file) Load(file.Name, file.Data);
                }).With(b => { b.Height = 28; b.HorizontalAlignment = HorizontalAlignment.Center; })).With(c => c.VerticalAlignment = VerticalAlignment.Center),
        }.Bind(Border.BackgroundProperty, "Field").Bind(Border.BorderBrushProperty, "Line");
        DragDrop.SetAllowDrop(zone, true);
        zone.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        zone.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (e.DataTransfer.TryGetFiles() is not { Length: > 0 } files) return;
            await using var stream = await ((Avalonia.Platform.Storage.IStorageFile)files[0]).OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            Load(files[0].Name, memory.ToArray());
        });
        return zone;
    }

    void Load(string name, byte[] data)
    {
        _fileName = name;
        var text = System.Text.Encoding.UTF8.GetString(data);
        if (IsCurl(text)) Parse(text); else ParseData(data);
        Build();
    }

    static bool IsCurl(string text)
    {
        var lower = text.Trim().ToLowerInvariant();
        return lower.StartsWith("curl") || lower.StartsWith("$ curl");
    }

    void Parse(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) { _preview = null; _error = null; return; }
        if (!IsCurl(trimmed)) { ParseData(System.Text.Encoding.UTF8.GetBytes(trimmed)); return; }
        try { _preview = CurlParser.Parse(trimmed); _error = null; }
        catch (Exception e) { _preview = null; _error = e.Message; }
    }

    void ParseData(byte[] data)
    {
        try
        {
            var result = Postman.ImportData(data);
            if (result.IsEmpty) { _preview = null; _error = "There's nothing to import in this file."; }
            else { _preview = result; _error = null; }
        }
        catch (Exception e) { _preview = null; _error = e.Message; }
    }

    void ShowPreview()
    {
        _commit.Content = _preview is ApiRequest ? "Open in New Tab" : "Import";
        _commit.IsEnabled = _preview != null;
        if (_error != null)
        {
            _previewHost.Content = UI.Row(6, new Icon { Kind = "warning", Size = 13, Foreground = Palette.Amber }, UI.Text(_error, 12).With(t => { t.Foreground = Palette.Amber; t.TextWrapping = TextWrapping.Wrap; }));
            return;
        }
        var stack = new StackPanel { Spacing = 6 };
        switch (_preview)
        {
            case ApiRequest r:
                stack.Children.Add(UI.Row(8, UI.MethodBadge(r.Method, 12), UI.Code(r.Url, 12).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis)));
                var parts = new List<string> { $"{r.Headers.Count} header{(r.Headers.Count == 1 ? "" : "s")}" };
                if (r.Body.Mode != BodyMode.None) parts.Add($"{r.Body.Mode.Title()} body");
                if (r.Auth.Type != AuthType.Inherit) parts.Add(r.Auth.Type.Title());
                stack.Children.Add(UI.Text(string.Join(" · ", parts), 11.5, default, "secondary"));
                break;
            case ImportResult result:
                foreach (var c in result.Collections) stack.Children.Add(UI.Row(6, new Icon { Kind = "tray", Size = 13 }, UI.Text($"{c.Name}: {c.Items.RequestCount()} requests", 12.5)));
                foreach (var e in result.Environments) stack.Children.Add(UI.Row(6, new Icon { Kind = "layers", Size = 13 }, UI.Text($"{e.Name}: {e.Variables.Count} variables", 12.5)));
                break;
            default:
                _previewHost.Content = null;
                return;
        }
        _previewHost.Content = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Child = stack }.Bind(Border.BackgroundProperty, "Field");
    }

    void Commit()
    {
        switch (_preview)
        {
            case ApiRequest r: Ws.NewTab(r, unsaved: true); break;
            case ImportResult result: Ws.Import(result); break;
            default: return;
        }
        Close();
    }
}

// MARK: - App settings

class AppSettingsSheet : SheetWindow
{
    static Settings Prefs => Settings.Shared;

    public AppSettingsSheet() : base("Settings", 540)
    {
        var theme = UI.Pills([(Appearance.System, "System"), (Appearance.Light, "Light"), (Appearance.Dark, "Dark")], Prefs.Appearance, a => { Prefs.Appearance = a; Rebuild(); });
        var layout = UI.Picker([(PaneLayout.Stacked, "Below the request"), (PaneLayout.SideBySide, "Beside the request")], Prefs.Layout, l => Prefs.Layout = l);
        var timeout = new NumericUpDown { Value = (decimal)Prefs.Timeout, Minimum = 5, Maximum = 600, Increment = 5, FormatString = "0", Width = 130 };
        timeout.ValueChanged += (_, _) => Prefs.Timeout = (double)(timeout.Value ?? 30);
        var redirects = new CheckBox { Content = "Follow redirects", IsChecked = Prefs.FollowRedirects };
        redirects.IsCheckedChanged += (_, _) => Prefs.FollowRedirects = redirects.IsChecked == true;
        var ssl = new CheckBox { Content = "Verify SSL certificates", IsChecked = Prefs.VerifySsl };
        ssl.IsCheckedChanged += (_, _) => Prefs.VerifySsl = ssl.IsChecked == true;

        Content = UI.Column(18,
            Section("Appearance", Row("Theme", theme), Row("Response", layout)),
            Section("Requests",
                Row("Timeout", UI.Row(8, timeout, UI.Text("seconds", 12, default, "secondary"))),
                Row("", redirects),
                Row("", UI.Column(3, ssl, UI.Text("Turn off for local servers with self-signed certificates.", 11, default, "secondary")))),
            Section("Data",
                Row("Saved in", new SelectableTextBlock { Text = Storage.Folder, FontSize = 11, Classes = { "code" }, TextWrapping = TextWrapping.Wrap }),
                Row("", UI.Row(8,
                    UI.Button("Show in Explorer", "secondary", () => Reveal()),
                    UI.Button("Export Everything…", "secondary", () => _ = Ws.ExportEverything())))),
            Buttons(Done(this))).With(c => c.Margin = new Thickness(22));
    }

    void Rebuild()
    {
        var replacement = new AppSettingsSheet();
        Content = replacement.Content;
    }

    static void Reveal()
    {
        try
        {
            Directory.CreateDirectory(Storage.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Storage.Folder) { UseShellExecute = true });
        }
        catch (Exception) { }
    }

    static Control Section(string title, params Control[] rows) =>
        UI.Column(10, new Control[] { UI.Text(title.ToUpperInvariant(), 10.5, FontWeight.SemiBold, "tertiary") }.Concat(rows).ToArray());

    static Control Row(string label, Control content)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), ColumnSpacing = 12 };
        grid.Children.Add(UI.Text(label, 12.5, default, "secondary"));
        content.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }
}

// MARK: - Welcome

class WelcomeView : UserControl
{
    static Workspace Ws => Workspace.Shared;

    public WelcomeView()
    {
        var actions = UI.Row(14,
            Action("plus", "New Request", "Ctrl T", () => Ws.NewTab()),
            Action("import", "Import Collection", "Ctrl O", () => _ = Dialogs.Import(false)),
            Action("terminal", "Paste cURL", "Ctrl Shift I", () => _ = Dialogs.Import(true)));
        actions.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = UI.Column(28,
            UI.Column(10,
                new Image { Source = AppIcon.Bitmap, Width = 68, Height = 68, HorizontalAlignment = HorizontalAlignment.Center },
                UI.Text("Relay", 28, FontWeight.Bold).With(t => t.HorizontalAlignment = HorizontalAlignment.Center),
                UI.Text("Build a request, send it, read the response.", 13, default, "secondary").With(t => t.HorizontalAlignment = HorizontalAlignment.Center)),
            actions);
        if (Ws.History.Count > 0)
        {
            var recent = UI.Column(4, UI.Text("RECENT", 10.5, FontWeight.SemiBold, "tertiary").With(t => t.Margin = new Thickness(10, 0, 0, 0)));
            foreach (var entry in Ws.History.Take(5)) recent.Children.Add(Recent(entry));
            recent.Width = 540;
            recent.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(recent);
        }
        stack.VerticalAlignment = VerticalAlignment.Center;
        Content = stack;
    }

    static Control Action(string icon, string title, string keys, Action run)
    {
        var card = new Border
        {
            Width = 160,
            Height = 124,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = UI.Column(10,
                new Icon { Kind = icon, Size = 22, HorizontalAlignment = HorizontalAlignment.Center }.Bind(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, "Accent"),
                UI.Text(title, 12.5, FontWeight.SemiBold).With(t => t.HorizontalAlignment = HorizontalAlignment.Center),
                UI.KeyHint(keys).With(k => k.HorizontalAlignment = HorizontalAlignment.Center)).With(c => c.VerticalAlignment = VerticalAlignment.Center),
        }.Bind(Border.BackgroundProperty, "Field").Bind(Border.BorderBrushProperty, "Line");
        card.PointerEntered += (_, _) => { card.Background = UI.Res("AccentSoft"); card.BorderBrush = Palette.Tint(UI.Res("Accent"), 0.4); };
        card.PointerExited += (_, _) => { card.Background = UI.Res("Field"); card.BorderBrush = UI.Res("Line"); };
        card.Tapped += (_, _) => run();
        return card;
    }

    static Control Recent(HistoryEntry entry)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("48,*,Auto"), Height = 30, Margin = new Thickness(10, 0) };
        grid.Children.Add(UI.MethodBadge(entry.Request.Method));
        grid.Children.Add(UI.Code(entry.Request.Url, 12).With(t => { Grid.SetColumn(t, 1); t.TextTrimming = TextTrimming.CharacterEllipsis; }));
        if (entry.Status is { } s) grid.Children.Add(UI.Text($"{s}", 11.5, FontWeight.SemiBold).With(t => { t.Foreground = Palette.Status(s); Grid.SetColumn(t, 2); }));
        var row = new Border { CornerRadius = new CornerRadius(7), Child = grid, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        row.PointerEntered += (_, _) => row.Background = UI.Res("Hover");
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.Tapped += (_, _) => Ws.OpenHistory(entry);
        return row;
    }
}

// MARK: - Variables popover

class VariablesPopover : UserControl
{
    static Workspace Ws => Workspace.Shared;

    public VariablesPopover()
    {
        Width = 480;
        Height = 400;
        AttachedToVisualTree += (_, _) => Build();
    }

    void Build()
    {
        var stack = new DockPanel();
        if (Ws.ActiveEnvironment is { } env)
        {
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12) };
            header.Children.Add(UI.Column(0, UI.Text("ENVIRONMENT", 10, FontWeight.SemiBold, "tertiary"), UI.Text(env.Name, 14, FontWeight.SemiBold)));
            header.Children.Add(UI.Button("Edit All…", "secondary", () => _ = Dialogs.Environments(env.Id)).With(b => { b.Height = 26; Grid.SetColumn(b, 1); }));
            DockPanel.SetDock(header, Dock.Top);
            stack.Children.Add(header);
        }
        if (Ws.SelectedTab is { } tab && Ws.Collection(tab.Model.CollectionID) is { } collection && collection.Variables.Any(v => !v.IsBlank))
        {
            var vars = UI.Column(4, UI.Text($"COLLECTION · {collection.Name}", 10, FontWeight.SemiBold, "tertiary"));
            foreach (var v in collection.Variables.Where(v => !v.IsBlank))
                vars.Children.Add(UI.Row(8, UI.Code(v.Key, 11.5, FontWeight.SemiBold).With(t => t.Foreground = Palette.Blue),
                    UI.Code(v.IsSecret ? "••••••" : v.Value, 11.5).With(t => { t.Classes.Add("secondary"); t.TextTrimming = TextTrimming.CharacterEllipsis; })).With(r => r.Opacity = v.Enabled ? 1 : 0.5));
            var box = new StackPanel { Children = { UI.Hairline(), vars.With(v => v.Margin = new Thickness(12)) } };
            DockPanel.SetDock(box, Dock.Bottom);
            stack.Children.Add(box);
        }
        stack.Children.Add(Ws.ActiveEnvironment is { } active
            ? new KeyValueTable(active.Variables, "", "Variable", "Value", allowsSecrets: true)
            : UI.EmptyState("layers", "No active environment", "Pick one from the environment menu in the title bar, or create one.",
                UI.Button("Manage Environments…", "secondary", () => _ = Dialogs.Environments(null)).With(b => b.Height = 28)));
        Content = stack;
    }
}
