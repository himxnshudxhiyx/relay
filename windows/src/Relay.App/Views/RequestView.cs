using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Relay.Core;

namespace Relay.App.Views;

/// <summary>One tab: header, URL bar, variable chips, the editor and the response. Ported from RequestView.swift.</summary>
public class RequestView : UserControl
{
    static Workspace Ws => Workspace.Shared;
    static Settings Prefs => Settings.Shared;

    readonly TabState _tab;
    readonly ContentControl _hints = new();
    readonly ContentControl _crumbs = new();
    readonly ContentControl _editor = new();
    readonly ContentControl _send = new();
    readonly ResponsePanel _response;
    readonly TextBox _url;
    readonly TextBox _name;
    readonly Button _method;
    readonly Button _save;
    bool _syncingParams;

    public RequestView(TabState tab)
    {
        _tab = tab;
        _response = new ResponsePanel(tab);

        // Header: where it's saved, its name, cURL and Save.
        _name = new TextBox { FontSize = 18, FontWeight = FontWeight.SemiBold, Classes = { "bare" }, PlaceholderText = "Request name" };
        _name[!TextBox.TextProperty] = new Binding(nameof(ApiRequest.Name)) { Source = tab.Draft, Mode = BindingMode.TwoWay };
        var curl = UI.Button(UI.Row(4, UI.Text("cURL", 13, FontWeight.Medium), new Icon { Kind = "chevron-down", Size = 10 }), "link", null, "Copy this request as a cURL command (Ctrl+Shift+C)");
        curl.Height = 30;
        curl.Flyout = new MenuFlyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            Items =
            {
                new MenuItem { Header = "Copy cURL", Command = new AsyncRelayCommand(() => Ws.Copy(Ws.Curl(tab), "cURL")) },
                new MenuItem { Header = "Copy cURL, keeping {{variables}}", Command = new AsyncRelayCommand(() => Ws.Copy(Ws.Curl(tab, resolveVariables: false), "cURL")) },
                new Separator(),
                new MenuItem { Header = "Replace with cURL from Clipboard", Command = new AsyncRelayCommand(ReplaceFromClipboard) },
            },
        };
        _save = UI.Button("Save", "secondary", () => _ = Ws.Save(tab), "Save (Ctrl+S)");
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10, Margin = new Thickness(20, 14, 20, 10) };
        header.Children.Add(UI.Column(1, _crumbs, _name));
        Grid.SetColumn(curl, 1);
        header.Children.Add(curl);
        Grid.SetColumn(_save, 2);
        header.Children.Add(_save);

        // URL bar
        _method = new Button { Width = 100, Height = 40, Padding = new Thickness(0), Classes = { "ghost" } };
        _method.Click += (_, _) => MethodMenu().ShowAt(_method);
        _url = new TextBox { PlaceholderText = "Enter a URL, or paste a cURL command", FontSize = 13.5, Classes = { "bare", "code" }, VerticalAlignment = VerticalAlignment.Center };
        _url.Text = tab.Draft.Url;
        _url.TextChanged += (_, _) => OnUrlTyped(_url.Text ?? "");
        _url.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { Ws.Send(tab); e.Handled = true; }
        };
        // A pasted cURL fills the whole request instead of landing in the URL.
        // The clipboard is read asynchronously, so the paste is claimed up front and
        // plain text inserted by hand; otherwise the box pastes before the check finishes.
        // (The event bubbles; a tunnel handler would never see it.)
        _url.AddHandler(TextBox.PastingFromClipboardEvent, async (_, e) =>
        {
            e.Handled = true;
            if (await Dialogs.PasteText() is not { Length: > 0 } text) return;
            if (CurlParser.ParsePasted(text) is { } parsed)
            {
                Ws.ApplyCurl(parsed, tab);
                _url.Text = tab.Draft.Url;
                return;
            }
            // A URL field is one line.
            var line = text.ReplaceLineEndings(" ").Trim();
            var current = _url.Text ?? "";
            var start = Math.Min(_url.SelectionStart, _url.SelectionEnd);
            var end = Math.Max(_url.SelectionStart, _url.SelectionEnd);
            _url.Text = current[..start] + line + current[end..];
            _url.CaretIndex = start + line.Length;
        });
        var urlField = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,1,*"), Height = 40 };
        urlField.Children.Add(_method);
        var sep = new Border { Width = 1, Height = 20, [Grid.ColumnProperty] = 1 }.Bind(Border.BackgroundProperty, "Line");
        urlField.Children.Add(sep);
        _url.Margin = new Thickness(12, 0);
        Grid.SetColumn(_url, 2);
        urlField.Children.Add(_url);
        var urlBox = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = urlField }
            .Bind(Border.BackgroundProperty, "Field").Bind(Border.BorderBrushProperty, "Line");
        _url.GotFocus += (_, _) => urlBox.BorderBrush = UI.Res("Accent");
        _url.LostFocus += (_, _) => urlBox.BorderBrush = UI.Res("Line");
        var urlRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(20, 0, 20, 12) };
        urlRow.Children.Add(urlBox);
        Grid.SetColumn(_send, 1);
        urlRow.Children.Add(_send);

        var top = new StackPanel { Children = { header, urlRow, _hints, UI.Hairline() } };
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(Body());
        Content = root;

        tab.PropertyChanged += OnTabChanged;
        tab.Model.PropertyChanged += OnTabChanged;
        tab.Draft.PropertyChanged += OnDraftChanged;
        tab.Draft.Params.CollectionChanged += OnParamsChanged;
        tab.Edited += Refresh;
        Ws.PropertyChanged += OnWorkspaceChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            tab.PropertyChanged -= OnTabChanged;
            tab.Model.PropertyChanged -= OnTabChanged;
            tab.Draft.PropertyChanged -= OnDraftChanged;
            tab.Draft.Params.CollectionChanged -= OnParamsChanged;
            tab.Edited -= Refresh;
            Ws.PropertyChanged -= OnWorkspaceChanged;
        };
        WatchParams();
        Refresh();
        ShowSection();
        AttachedToVisualTree += (_, _) =>
        {
            if (tab.Draft.Url.Length == 0) Avalonia.Threading.Dispatcher.UIThread.Post(() => _url.Focus());
        };
    }

    /// <summary>The editor above the response, or beside it; the divider drags and double-click resets.</summary>
    Control Body()
    {
        if (Prefs.Layout == PaneLayout.SideBySide)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1,*") };
            grid.Children.Add(_editor);
            var splitter = new GridSplitter { [Grid.ColumnProperty] = 1, ResizeDirection = GridResizeDirection.Columns }.Bind(BackgroundProperty, "Line");
            grid.Children.Add(splitter);
            _response[Grid.ColumnProperty] = 2;
            grid.Children.Add(_response);
            return grid;
        }
        // Pinned under the URL bar at a fifth of the screen by default; drag to resize, double-click to reset.
        var defaultHeight = ScreenHeight() * 0.2 + 40;
        var rows = new Grid { RowDefinitions = new RowDefinitions($"{(Prefs.EditorHeight > 0 ? Prefs.EditorHeight : defaultHeight)},7,*") };
        rows.RowDefinitions[0].MinHeight = 140;
        rows.RowDefinitions[2].MinHeight = 200;
        rows.Children.Add(_editor);
        var handle = new GridSplitter { [Grid.RowProperty] = 1, ResizeDirection = GridResizeDirection.Rows, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeNorthSouth) };
        ToolTip.SetTip(handle, "Drag to resize. Double-click to reset.");
        handle.DragCompleted += (_, _) => Prefs.EditorHeight = rows.RowDefinitions[0].ActualHeight;
        handle.DoubleTapped += (_, _) =>
        {
            Prefs.EditorHeight = 0;
            rows.RowDefinitions[0].Height = new GridLength(defaultHeight);
        };
        rows.Children.Add(handle);
        rows.Children.Add(new Border { Height = 1, [Grid.RowProperty] = 1, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false }.Bind(Border.BackgroundProperty, "Line"));
        _response[Grid.RowProperty] = 2;
        rows.Children.Add(_response);
        return rows;
    }

    /// <summary>The height of the window's screen in layout units.</summary>
    // ponytail: read when the view is built, not on a screen change; listen to
    // Window.PositionChanged if moving between monitors should resize it.
    static double ScreenHeight()
    {
        if (Dialogs.Owner is not { } window || (window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is not { } screen) return 900;
        // Windows reports device pixels; Avalonia's macOS backend (used only for testing) already reports points.
        return OperatingSystem.IsMacOS() ? screen.Bounds.Height : screen.Bounds.Height / screen.Scaling;
    }

    void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workspace.UrlFocusRequest))
        {
            _url.Focus();
            _url.SelectAll();
        }
        else if (e.PropertyName == nameof(Workspace.ActiveEnvironmentID))
        {
            Refresh();
        }
    }

    void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RequestTab.Draft))
        {
            // Replaced wholesale (a cURL import): rebuild against the new draft.
            Ws.SelectedTab = null;
            Ws.SelectedTab = _tab;
            return;
        }
        if (e.PropertyName is nameof(TabState.Response) or nameof(TabState.EditorSection) or nameof(TabState.IsDirty) or nameof(RequestTab.CollectionID))
        {
            Refresh();
            if (e.PropertyName == nameof(TabState.EditorSection)) ShowSection();
        }
    }

    void OnDraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ApiRequest.Url) && _url.Text != _tab.Draft.Url) _url.Text = _tab.Draft.Url;
    }

    /// <summary>URL → Params, and the name follows the URL while it's still a placeholder or URL-derived.</summary>
    void OnUrlTyped(string url)
    {
        var draft = _tab.Draft;
        if (draft.Url == url) return;
        if (_tab.Model.CollectionID == null && (draft.Name == ApiRequest.DefaultName || draft.Name == CurlParser.RequestName(draft.Url)))
            draft.Name = CurlParser.RequestName(url);
        draft.Url = url;
        _syncingParams = true;
        var rows = UrlQuery.ParamsFromUrl(url, draft.Params.ToList());
        if (!rows.SequenceEqual(draft.Params))
        {
            draft.Params.Clear();
            foreach (var r in rows) draft.Params.Add(r);
        }
        _syncingParams = false;
    }

    // Params → URL
    void OnParamsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        WatchParams();
        ApplyParams();
    }

    readonly List<KeyValue> _watchedParams = [];

    void WatchParams()
    {
        foreach (var kv in _watchedParams) kv.PropertyChanged -= OnParamEdited;
        _watchedParams.Clear();
        foreach (var kv in _tab.Draft.Params)
        {
            kv.PropertyChanged += OnParamEdited;
            _watchedParams.Add(kv);
        }
    }

    void OnParamEdited(object? sender, PropertyChangedEventArgs e) => ApplyParams();

    void ApplyParams()
    {
        if (_syncingParams) return;
        var url = UrlQuery.UrlApplying(_tab.Draft.Url, _tab.Draft.Params);
        if (url != _tab.Draft.Url) _tab.Draft.Url = url;
    }

    async Task ReplaceFromClipboard()
    {
        var text = await Dialogs.PasteText();
        if (string.IsNullOrEmpty(text))
        {
            Ws.Flash("The clipboard has no text");
            return;
        }
        try
        {
            Ws.ApplyCurl(CurlParser.Parse(text), _tab);
        }
        catch (Exception e)
        {
            Ws.Flash(e.Message);
        }
    }

    MenuFlyout MethodMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var m in ApiRequest.Methods)
        {
            var label = UI.Code(m, 12.5, FontWeight.Bold);
            label.Foreground = Palette.Method(m);
            menu.Items.Add(new MenuItem
            {
                Header = label,
                Icon = m == _tab.Draft.Method ? new Icon { Kind = "check", Size = 11 } : null,
                Command = new RelayCommand(() => { _tab.Draft.Method = m; Refresh(); }),
            });
        }
        return menu;
    }

    /// <summary>Everything small that follows the tab's state.</summary>
    void Refresh()
    {
        var draft = _tab.Draft;
        var method = UI.Code(draft.Method, 13, FontWeight.Bold);
        method.Foreground = Palette.Method(draft.Method);
        _method.Content = UI.Row(5, method, new Icon { Kind = "chevron-down", Size = 9, Foreground = UI.Res("TextTertiary") }).With(r => r.HorizontalAlignment = HorizontalAlignment.Center);

        var crumbs = Ws.Breadcrumb(_tab);
        _crumbs.Content = crumbs.Count == 0
            ? UI.Row(5, new Icon { Kind = "circle", Size = 11, Thickness = 1.2 }.Bind(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, "TextSecondary"), UI.Text("Not saved to a collection", 11, default, "secondary"))
            : UI.Text(string.Join("  ›  ", crumbs), 11, default, "secondary");

        _save.Content = _tab.Model.CollectionID == null ? "Save…" : "Save";
        _save.IsEnabled = _tab.Model.CollectionID == null || _tab.IsDirty;

        if (_tab.IsLoading)
        {
            var cancel = UI.Button("Cancel", "secondary", () => Ws.Cancel(_tab), "Cancel (Ctrl+.)");
            cancel.Width = 90;
            cancel.Height = 40;
            _send.Content = cancel;
        }
        else
        {
            var send = UI.Button(UI.Row(7, UI.Text("Send", 14, FontWeight.Bold), new Icon { Kind = "arrow-right", Size = 13, Thickness = 2 }), "primary", () => Ws.Send(_tab), "Send (Ctrl+Enter)");
            send.Width = 96;
            send.Height = 40;
            send.Foreground = UI.Res("OnAccent");
            _send.Content = send;
        }
        RefreshHints();
        if (_sectionTabs != null) _sectionTabs.Content = SectionTabs();
    }

    /// <summary>One chip per {{variable}} the request uses, with the value it'll get and where from.</summary>
    void RefreshHints()
    {
        var d = _tab.Draft;
        var parts = new List<string> { d.Url };
        parts.AddRange(d.Headers.Where(h => h.Enabled).SelectMany(h => new[] { h.Key, h.Value }));
        parts.AddRange([d.Auth.Token, d.Auth.Username, d.Auth.Password, d.Auth.Key, d.Auth.Value]);
        if (d.Body.Mode.IsRaw()) parts.Add(d.Body.Raw);
        if (d.Body.Mode is BodyMode.Form or BodyMode.Multipart) parts.AddRange(d.Body.Form.Where(f => f.Enabled).Select(f => f.Value));
        var names = Variables.Names(string.Join(" ", parts));
        if (names.Count == 0)
        {
            _hints.Content = null;
            return;
        }
        var scope = Ws.Scope(_tab);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(20, 0, 20, 9) };
        foreach (var name in names) row.Children.Add(Chip(name, scope));
        _hints.Content = new ScrollViewer { Content = row, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden };
    }

    Control Chip(string name, VariableScope scope)
    {
        var source = scope.SourceOf(name);
        var color = Palette.Variable(source);
        var kv = scope.Environment.Concat(scope.Collection).FirstOrDefault(k => k.Key == name);
        var value = scope.Values.GetValueOrDefault(name, "");
        var (shown, help) = source switch
        {
            VariableScope.Source.Missing => ("not defined", $"{{{{{name}}}}} isn't defined. Click to add it to the active environment."),
            VariableScope.Source.Dynamic => ("generated", "Built-in value, generated each time you send."),
            VariableScope.Source.Environment => (kv?.IsSecret == true ? "••••••" : value.Length == 0 ? "empty" : value, $"From the “{Ws.ActiveEnvironment?.Name}” environment. Click to edit."),
            _ => (kv?.IsSecret == true ? "••••••" : value.Length == 0 ? "empty" : value, "From the collection's variables. Click to edit."),
        };
        var nameText = UI.Code(name, 11, FontWeight.SemiBold);
        nameText.Foreground = color;
        var valueText = UI.Code(shown, 11);
        valueText.Classes.Add("secondary");
        valueText.MaxWidth = 260;
        valueText.TextTrimming = TextTrimming.CharacterEllipsis;
        var chip = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3),
            BorderThickness = new Thickness(1),
            Background = Palette.Tint(color, 0.09),
            BorderBrush = Palette.Tint(color, 0.25),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = UI.Row(5, new Avalonia.Controls.Shapes.Ellipse { Width = 6, Height = 6, Fill = color }, nameText, valueText),
        };
        ToolTip.SetTip(chip, help);
        chip.Tapped += (_, _) =>
        {
            switch (source)
            {
                case VariableScope.Source.Missing: Ws.DefineVariable(name); break;
                case VariableScope.Source.Environment: _ = Dialogs.Environments(Ws.ActiveEnvironmentID); break;
                case VariableScope.Source.Collection when _tab.Model.CollectionID is { } id: _ = Dialogs.CollectionSettings(id); break;
            }
        };
        return chip;
    }

    // MARK: Editor sections

    ContentControl? _sectionTabs;
    readonly ContentControl _section = new();

    void ShowSection()
    {
        if (_sectionTabs == null)
        {
            _sectionTabs = new ContentControl { Content = SectionTabs(), Margin = new Thickness(20, 0) };
            _editor.Content = new DockPanel
            {
                Children =
                {
                    new StackPanel { Children = { _sectionTabs, UI.Hairline() }, [DockPanel.DockProperty] = Dock.Top },
                    _section,
                },
            }.Bind(BackgroundProperty, "Canvas");
        }
        _sectionTabs.Content = SectionTabs();
        var scope = () => Ws.Scope(_tab);
        _section.Content = _tab.EditorSection switch
        {
            EditorSection.Params => new KeyValueTable(_tab.Draft.Params, "Query parameters. They stay in sync with the URL.", "Parameter", "Value", scope: scope),
            EditorSection.Headers => HeadersSection(),
            EditorSection.Auth => new AuthEditor(_tab.Draft.Auth, allowsInherit: true, InheritDescription),
            _ => new BodyEditor(_tab),
        };
    }

    Control SectionTabs()
    {
        string? Count(IEnumerable<KeyValue> rows)
        {
            var n = rows.Count(r => r.Enabled && !r.IsBlank);
            return n == 0 ? null : $"{n}";
        }
        var d = _tab.Draft;
        var auth = d.Auth.Type switch { AuthType.Bearer => "Bearer", AuthType.Basic => "Basic", AuthType.ApiKey => "Key", _ => null };
        return UI.UnderlineTabs(
        [
            (EditorSection.Params, "Params", Count(d.Params)),
            (EditorSection.Headers, "Headers", Count(d.Headers)),
            (EditorSection.Body, "Body", d.Body.Mode == BodyMode.None ? null : d.Body.Mode.ChipTitle()),
            (EditorSection.Auth, "Auth", auth),
        ], _tab.EditorSection, s => _tab.EditorSection = s);
    }

    string InheritDescription()
    {
        if (_tab.Model.CollectionID == null)
            return "This request isn't in a collection, so there's nothing to inherit and no auth is sent.";
        var parentView = new TabState(new RequestTab { CollectionID = _tab.Model.CollectionID, Draft = Json.Clone(_tab.Draft), Original = _tab.Draft });
        parentView.Draft.Auth.Type = AuthType.Inherit;
        var (auth, from) = Ws.EffectiveAuth(parentView);
        parentView.Dispose();
        var source = from == null ? "the parent" : $"“{from}”";
        return auth.Type is AuthType.None or AuthType.Inherit
            ? $"Inherits from {source}, which sends no auth."
            : $"Uses {auth.Type.Title()} from {source}.";
    }

    static readonly (string Name, string Value)[] Presets =
    [
        ("Content-Type", "application/json"),
        ("Accept", "application/json"),
        ("Authorization", "Bearer {{token}}"),
        ("Cache-Control", "no-cache"),
        ("User-Agent", "Relay/1.0"),
        ("X-Request-ID", "{{$guid}}"),
    ];

    Control HeadersSection()
    {
        var addCommon = UI.Button(UI.Row(4, UI.Text("Add Common", 11.5, FontWeight.Medium), new Icon { Kind = "chevron-down", Size = 9 }), "link");
        var menu = new MenuFlyout();
        foreach (var (name, value) in Presets)
            menu.Items.Add(new MenuItem { Header = $"{name}: {value}", Command = new RelayCommand(() => _tab.Draft.Headers.Add(new KeyValue(name, value))) });
        addCommon.Flyout = menu;
        var table = new KeyValueTable(_tab.Draft.Headers, "Headers sent with the request.", "Header", "Value", accessory: addCommon, scope: () => Ws.Scope(_tab));

        // Headers Relay adds that aren't in the table, so nothing on the wire is a surprise.
        var implicitList = new StackPanel { Spacing = 4 };
        void Fill()
        {
            implicitList.Children.Clear();
            var d = _tab.Draft;
            var explicitNames = d.Headers.Where(h => h.Enabled && h.Key.Length > 0).Select(h => h.Key.ToLowerInvariant()).ToHashSet();
            var items = Ws.Prepared(_tab).WireHeaders
                .Where(h => !explicitNames.Contains(h.Name.ToLowerInvariant()))
                .Select(h =>
                {
                    var isContentType = h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase);
                    return (h.Name, Value: isContentType ? h.Value : new string('•', 8), Source: isContentType ? "from Body" : "from Auth");
                }).ToList();
            if (d.Body.Mode == BodyMode.Multipart && !explicitNames.Contains("content-type")) items.Add(("Content-Type", "multipart/form-data; boundary=…", "from Body"));
            if (!explicitNames.Contains("user-agent")) items.Add(("User-Agent", "Relay/1.0", "default"));
            if (items.Count == 0) return;
            implicitList.Children.Add(UI.Text("ADDED AUTOMATICALLY", 10, FontWeight.SemiBold, "tertiary"));
            foreach (var (name, value, source) in items)
                implicitList.Children.Add(UI.Row(6, UI.Code($"{name}:", 11, FontWeight.SemiBold).With(t => t.Classes.Add("secondary")),
                    UI.Code(value, 11).With(t => { t.Classes.Add("secondary"); t.TextTrimming = TextTrimming.CharacterEllipsis; }),
                    UI.Text($"· {source}", 10.5, default, "tertiary")));
        }
        Fill();
        _tab.Edited += Fill;
        table.DetachedFromVisualTree += (_, _) => _tab.Edited -= Fill;
        var auto = new Border { Padding = new Thickness(20, 10), Child = implicitList, [DockPanel.DockProperty] = Dock.Bottom }.Bind(Border.BackgroundProperty, "Editor");
        return new DockPanel { Children = { auto, table } };
    }
}

/// <summary>Body: mode chips, then a code editor, a form table or a file. Ported from BodyEditor in RequestEditor.swift.</summary>
class BodyEditor : DockPanel
{
    readonly TabState _tab;
    readonly ContentControl _content = new();
    readonly ContentControl _bar = new();

    public BodyEditor(TabState tab)
    {
        _tab = tab;
        _bar.Margin = new Thickness(20, 8);
        SetDock(_bar, Dock.Top);
        Children.Add(_bar);
        Children.Add(_content);
        Build();
    }

    RequestBody Body => _tab.Draft.Body;

    void Build()
    {
        var mode = Body.Mode;
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var option in Enum.GetValues<BodyMode>())
            chips.Children.Add(UI.Button(option.ChipTitle(), option == mode ? "chip selected" : "chip", () => { Body.Mode = option; Build(); }));
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        bar.Children.Add(chips);
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, [Grid.ColumnProperty] = 1 };
        bar.Children.Add(trailing);
        _bar.Content = bar;

        switch (mode)
        {
            case BodyMode.None:
                _content.Content = UI.EmptyState("file", "No body", "This request sends no body. Choose JSON, a form or a file above to add one.");
                break;
            case BodyMode.Json or BodyMode.Text or BodyMode.Xml:
                var status = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
                if (mode != BodyMode.Text) trailing.Children.Add(status);
                if (mode != BodyMode.Text)
                    trailing.Children.Add(UI.Button("Beautify", "secondary", () => Beautify(mode == BodyMode.Xml)).With(b => b.Height = 28));
                var editor = new CodeView
                {
                    Language = mode == BodyMode.Json ? CodeLanguage.Json : mode == BodyMode.Xml ? CodeLanguage.Xml : CodeLanguage.Plain,
                    Text = Body.Raw,
                    Scope = () => Workspace.Shared.Scope(_tab),
                };
                void Status()
                {
                    if (mode != BodyMode.Json) return;
                    var trimmed = Body.Raw.Trim();
                    if (trimmed.Length == 0) { status.Content = null; return; }
                    // Unquoted {{vars}} are valid once substituted, so judge the JSON around them.
                    var probe = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\{\{[^{}]*\}\}", "0");
                    var valid = JsonFormat.IsJson(probe);
                    var color = valid ? Palette.Green : Palette.Amber;
                    status.Content = UI.Row(6, new Avalonia.Controls.Shapes.Ellipse { Width = 6, Height = 6, Fill = color },
                        UI.Text(valid ? "Valid JSON" : "Invalid JSON", 12, FontWeight.Medium).With(t => t.Foreground = color));
                }
                Status();
                editor.PropertyChanged += (_, e) =>
                {
                    if (e.Property != CodeView.TextProperty) return;
                    Body.Raw = editor.Text;
                    Status();
                };
                Body.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(RequestBody.Raw) && editor.Text != Body.Raw) editor.Text = Body.Raw;
                };
                _content.Content = new Border
                {
                    Margin = new Thickness(20, 0, 20, 16),
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    ClipToBounds = true,
                    Child = editor,
                }.Bind(Border.BackgroundProperty, "Editor").Bind(Border.BorderBrushProperty, "Line");
                break;
            case BodyMode.Form:
                _content.Content = new KeyValueTable(Body.Form, "Sent as application/x-www-form-urlencoded.", "Field", scope: () => Workspace.Shared.Scope(_tab));
                break;
            case BodyMode.Multipart:
                _content.Content = new KeyValueTable(Body.Form, "Sent as multipart/form-data. Use the file icon to upload a file.", "Field", allowsFiles: true, scope: () => Workspace.Shared.Scope(_tab));
                break;
            case BodyMode.File:
                _content.Content = FilePicker();
                break;
        }
    }

    void Beautify(bool xml)
    {
        var pretty = xml ? XmlFormat.Pretty(Body.Raw) : JsonFormat.Pretty(Body.Raw);
        if (pretty == null)
        {
            Workspace.Shared.Flash(xml ? "The body isn't well-formed XML" : "The body isn't valid JSON");
            return;
        }
        Body.Raw = pretty;
    }

    Control FilePicker()
    {
        var path = UI.Code(Body.FilePath.Length == 0 ? "No file chosen" : Body.FilePath, 12);
        path.TextTrimming = TextTrimming.CharacterEllipsis;
        var choose = UI.Button("Choose File…", "secondary", async () =>
        {
            if (await Dialogs.PickFilePath() is { } p)
            {
                Body.FilePath = p;
                path.Text = p;
            }
        }).With(b => b.Height = 28);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        row.Children.Add(new Icon { Kind = "file", Size = 14 }.Bind(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, "Accent"));
        Grid.SetColumn(path, 1);
        row.Children.Add(path);
        Grid.SetColumn(choose, 2);
        row.Children.Add(choose);
        return UI.Column(8,
            new Border { Height = 44, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = row }
                .Bind(Border.BackgroundProperty, "Field").Bind(Border.BorderBrushProperty, "Line"),
            UI.Text("The file's bytes are sent as the body, unchanged.", 11.5, default, "secondary")).With(c => c.Margin = new Thickness(20, 8, 20, 16));
    }
}
