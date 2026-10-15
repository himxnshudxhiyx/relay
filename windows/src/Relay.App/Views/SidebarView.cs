using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Relay.Core;

namespace Relay.App.Views;

/// <summary>Collections, environments and history. Ported from SidebarView.swift.</summary>
public class SidebarView : UserControl
{
    static Workspace Ws => Workspace.Shared;

    readonly ContentControl _pane = new();
    readonly ContentControl _modes = new();

    public SidebarView()
    {
        var header = new Grid { Height = 44 };
        // The window drags from the sidebar's top too (it sits under the Mac's traffic lights / beside Windows' title).
        header.Children.Add(new Border { Background = Brushes.Transparent, [Avalonia.Controls.Chrome.WindowDecorationProperties.ElementRoleProperty] = WindowDecorationsElementRole.TitleBar });

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        _modes.Margin = new Thickness(10, 0, 10, 10);
        DockPanel.SetDock(_modes, Dock.Top);
        root.Children.Add(_modes);
        root.Children.Add(_pane);
        Content = root.Bind(BackgroundProperty, "Chrome");

        Ws.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Workspace.SidebarMode)) Rebuild();
        };
        Rebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property.Name == "ActualThemeVariant") Rebuild();
    }

    public void Rebuild()
    {
        _modes.Content = UI.Pills([(SidebarMode.Collections, "Collections"), (SidebarMode.Environments, "Environments"), (SidebarMode.History, "History")],
            Ws.SidebarMode, m => Ws.SidebarMode = m, height: 32, fontSize: 13, stretch: true);
        _pane.Content = Ws.SidebarMode switch
        {
            SidebarMode.Environments => new EnvironmentsPane(),
            SidebarMode.History => new HistoryPane(),
            _ => new CollectionsPane(),
        };
    }

    /// <summary>A strip pinned to the bottom of each pane.</summary>
    internal static Control Footer(params Control[] items)
    {
        var row = UI.Row(12, items);
        row.Margin = new Thickness(10, 0);
        return new DockPanel
        {
            Height = 40,
            Children = { UI.Hairline().With(h => DockPanel.SetDock(h, Dock.Top)), row },
        };
    }

    internal static Button FooterButton(string icon, string title, Action click)
    {
        var b = UI.Button(UI.Row(6, new Icon { Kind = icon, Size = 13 }, UI.Text(title, 12.5, FontWeight.Medium)), "link", click);
        b.Height = 28;
        return b;
    }
}

static class ControlExtensions
{
    public static T With<T>(this T control, Action<T> configure)
    {
        configure(control);
        return control;
    }
}

// MARK: - Collections

/// <summary>A row in the collection tree. Rebuilt when the tree's shape changes; names follow the model live.</summary>
partial class SidebarNode : ObservableObject
{
    public required object Model { get; init; }       // ApiCollection, Folder or ApiRequest
    public required Guid CollectionId { get; init; }
    public ObservableCollection<SidebarNode> Children { get; } = [];
    [ObservableProperty] private bool _isExpanded;
    public bool ForceExpanded { get; init; }

    public Guid Id => Model switch { ApiCollection c => c.Id, CollectionItem i => i.Id, _ => default };

    partial void OnIsExpandedChanged(bool value)
    {
        if (!ForceExpanded) Workspace.Shared.SetExpanded(Id, value);
    }
}

class CollectionsPane : DockPanel
{
    static Workspace Ws => Workspace.Shared;

    readonly TreeView _tree;
    readonly TextBox _search;
    bool _syncing;

    public CollectionsPane()
    {
        _search = new TextBox { Text = Ws.Search, PlaceholderText = "Search requests", Height = 32, Margin = new Thickness(10, 0, 10, 6) };
        _search.InnerLeftContent = new Icon { Kind = "search", Size = 13, Margin = new Thickness(8, 0, 2, 0) }.Bind(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, "TextTertiary");
        _search.InnerRightContent = UI.KeyHint("Ctrl K").With(k => { k.Margin = new Thickness(0, 0, 6, 0); k.Padding = new Thickness(5, 1); });
        _search.TextChanged += (_, _) =>
        {
            Ws.Search = _search.Text ?? "";
            Reload();
        };
        SetDock(_search, Dock.Top);
        Children.Add(_search);

        var footer = SidebarView.Footer(NewMenu());
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);

        _tree = new TreeView
        {
            Margin = new Thickness(4, 0),
            ItemTemplate = new FuncTreeDataTemplate<SidebarNode>((node, _) => Row(node), node => node.Children),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_tree, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        // Expansion follows the node, which follows the workspace's saved set.
        _tree.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<TreeViewItem>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(SidebarNode.IsExpanded)) { Mode = BindingMode.TwoWay }),
                new Avalonia.Styling.Setter(TreeViewItem.MinHeightProperty, 30.0),
                new Avalonia.Styling.Setter(TreeViewItem.PaddingProperty, new Thickness(0)),
            },
        });
        _tree.SelectionChanged += (_, _) =>
        {
            if (_syncing || _tree.SelectedItem is not SidebarNode { Model: ApiRequest r } node) return;
            Ws.Open(r.Id, node.CollectionId);
        };
        _tree.DoubleTapped += (_, _) =>
        {
            if (_tree.SelectedItem is SidebarNode { Model: ApiRequest r } node) Ws.Open(r.Id, node.CollectionId, preview: false);
        };
        Children.Add(Ws.Collections.Count == 0 ? Empty() : _tree);

        Ws.PropertyChanged += OnWorkspace;
        DetachedFromVisualTree += (_, _) => Ws.PropertyChanged -= OnWorkspace;
        Reload();
    }

    void OnWorkspace(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Workspace.TreeVersion):
                Reload();
                break;
            case nameof(Workspace.SearchFocusRequest):
                _search.Focus();
                _search.SelectAll();
                break;
            case nameof(Workspace.SelectedTab):
            case nameof(Workspace.RenamingID):
                Reload();
                break;
        }
    }

    static Control Empty() => UI.EmptyState("tray", "No collections",
        "Group related requests into a collection, or bring yours over from Postman.",
        UI.Column(8,
            UI.Button("New Collection", "primary", () => Ws.AddCollection()),
            UI.Button("Import…", "secondary", () => _ = Dialogs.Import(paste: false))));

    Control NewMenu()
    {
        var button = SidebarView.FooterButton("plus", "New", () => { });
        button.Flyout = new MenuFlyout
        {
            Placement = PlacementMode.TopEdgeAlignedLeft,
            Items =
            {
                new MenuItem { Header = "New Collection", Command = new RelayCommand(() => Ws.AddCollection()) },
                new MenuItem { Header = "New Request", Command = new RelayCommand(() => Ws.NewTab()) },
                new Separator(),
                new MenuItem { Header = "Import…", Command = new AsyncRelayCommand(() => Dialogs.Import(false)) },
                new MenuItem { Header = "Paste cURL…", Command = new AsyncRelayCommand(() => Dialogs.Import(true)) },
                new MenuItem { Header = "Export Everything…", Command = new AsyncRelayCommand(() => Ws.ExportEverything()) },
            },
        };
        return button;
    }

    /// <summary>Search results are always shown expanded; folders matching by name keep all their items.</summary>
    void Reload()
    {
        var query = Ws.Search.Trim();
        var nodes = new List<SidebarNode>();
        foreach (var c in Ws.Collections)
        {
            var node = new SidebarNode { Model = c, CollectionId = c.Id, ForceExpanded = query.Length > 0 };
            if (AddItems(node, c.Items, c.Id, query) || query.Length == 0)
            {
                node.IsExpanded = query.Length > 0 || Ws.Expanded.Contains(c.Id);
                nodes.Add(node);
            }
        }
        _syncing = true;
        _tree.ItemsSource = nodes;
        // Mirror the open tab in the tree.
        var selectedId = Ws.SelectedTab is { Model.CollectionID: not null } t ? t.Draft.Id : (Guid?)null;
        _tree.SelectedItem = selectedId is { } id ? Find(nodes, id) : null;
        _syncing = false;
    }

    static SidebarNode? Find(IEnumerable<SidebarNode> nodes, Guid id)
    {
        foreach (var n in nodes)
        {
            if (n.Id == id) return n;
            if (Find(n.Children, id) is { } found) return found;
        }
        return null;
    }

    /// <summary>Adds the items matching <paramref name="query"/>; true when any did.</summary>
    static bool AddItems(SidebarNode parent, IEnumerable<CollectionItem> items, Guid collectionId, string query)
    {
        var any = false;
        foreach (var item in items)
        {
            var node = new SidebarNode { Model = item, CollectionId = collectionId, ForceExpanded = query.Length > 0 };
            if (item is Folder f)
            {
                var nameMatches = query.Length > 0 && f.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
                var childMatches = AddItems(node, f.Items, collectionId, nameMatches ? "" : query);
                if (query.Length > 0 && !nameMatches && !childMatches) continue;
                node.IsExpanded = query.Length > 0 || Ws.Expanded.Contains(f.Id);
            }
            else if (item is ApiRequest r && query.Length > 0 &&
                     !new[] { r.Name, r.Url, r.Method }.Any(s => s.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            parent.Children.Add(node);
            any = true;
        }
        return any;
    }

    // MARK: Rows

    Control Row(SidebarNode node)
    {
        Control row = node.Model switch
        {
            ApiCollection c => CollectionRow(c),
            Folder f => FolderRow(f, node.CollectionId),
            ApiRequest r => RequestRow(r, node.CollectionId),
            _ => new Panel(),
        };
        row.Margin = new Thickness(0, 0, 8, 0);
        AttachDragDrop(row, node);
        return row;
    }

    Control NameView(Guid id, string name, double size, FontWeight weight, string? cls = null)
    {
        if (Ws.RenamingID == id)
        {
            var box = new TextBox { Text = name, FontSize = size, MinHeight = 24, Padding = new Thickness(4, 1) };
            var done = false;
            void Commit()
            {
                if (done) return;
                done = true;
                Ws.Rename(id, box.Text ?? "");
                Ws.RenamingID = null;
            }
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
                else if (e.Key == Key.Escape) { done = true; Ws.RenamingID = null; e.Handled = true; }
            };
            box.LostFocus += (_, _) => Commit();
            box.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); });
            return box;
        }
        var text = UI.Text(name, size, weight, cls);
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        return text;
    }

    Control CollectionRow(ApiCollection c)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(NameView(c.Id, c.Name, 13, FontWeight.SemiBold));
        var count = UI.Text($"{c.Items.RequestCount()}", 10.5, default, "tertiary");
        Grid.SetColumn(count, 1);
        grid.Children.Add(count);
        grid.ContextMenu = Menu(
            ("New Request", () => Ws.AddRequest(c.Id)),
            ("New Folder", () => Ws.AddFolder(c.Id)),
            (null, null),
            ("Auth, Variables & Settings…", () => _ = Dialogs.CollectionSettings(c.Id)),
            ("Rename", () => Ws.RenamingID = c.Id),
            ("Duplicate", () => Ws.Duplicate(c.Id)),
            ("Export as Postman Collection…", () => _ = Ws.ExportCollection(c)),
            (null, null),
            ("Delete…", () => _ = Ws.Delete(c.Id)));
        return grid;
    }

    Control FolderRow(Folder f, Guid collectionId)
    {
        var row = UI.Row(7, new Icon { Kind = "folder", Size = 13 }.Bind(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, "TextSecondary"), NameView(f.Id, f.Name, 13, FontWeight.Normal));
        if (f.Auth.Type != AuthType.Inherit)
        {
            var key = new Icon { Kind = "key", Size = 10 }.Bind(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, "TextTertiary");
            ToolTip.SetTip(key, $"Sets its own auth: {f.Auth.Type.Title()}");
            row.Children.Add(key);
        }
        row.ContextMenu = Menu(
            ("New Request", () => Ws.AddRequest(collectionId, f.Id)),
            ("New Folder", () => Ws.AddFolder(collectionId, f.Id)),
            (null, null),
            ("Auth & Settings…", () => _ = Dialogs.FolderSettings(collectionId, f.Id)),
            ("Rename", () => Ws.RenamingID = f.Id),
            ("Duplicate", () => Ws.Duplicate(f.Id)),
            (null, null),
            ("Delete…", () => _ = Ws.Delete(f.Id)));
        return row;
    }

    Control RequestRow(ApiRequest r, Guid collectionId)
    {
        var method = UI.MethodBadge(r.Method, 10);
        method.Width = 44;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(method);
        var name = NameView(r.Id, r.Name, 13, FontWeight.Normal, "secondary");
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var dirty = Ws.Tabs.Any(t => t.Model.CollectionID == collectionId && t.Draft.Id == r.Id && t.IsDirty);
        if (dirty)
        {
            var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 6, Height = 6, Margin = new Thickness(6, 0, 0, 0) }.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, "Accent");
            ToolTip.SetTip(dot, "Unsaved changes");
            Grid.SetColumn(dot, 2);
            grid.Children.Add(dot);
        }
        ToolTip.SetTip(grid, r.Url);
        grid.ContextMenu = Menu(
            ("Open in New Tab", () => Ws.Open(r.Id, collectionId, preview: false)),
            ("Copy as cURL", () =>
            {
                var tab = new TabState(new RequestTab { CollectionID = collectionId, Draft = r, Original = r });
                _ = Ws.Copy(Ws.Curl(tab), "cURL");
                tab.Dispose();
            }),
            (null, null),
            ("Rename", () => Ws.RenamingID = r.Id),
            ("Duplicate", () => Ws.Duplicate(r.Id)),
            (null, null),
            ("Delete", () => _ = Ws.Delete(r.Id)));
        return grid;
    }

    static ContextMenu Menu(params (string? Title, Action? Run)[] items)
    {
        var menu = new ContextMenu();
        foreach (var (title, run) in items)
        {
            if (title == null) menu.Items.Add(new Separator());
            else menu.Items.Add(new MenuItem { Header = title, Command = new RelayCommand(run!) });
        }
        return menu;
    }

    // MARK: Drag and drop

    static readonly Avalonia.Input.DataFormat<string> ItemFormat = Avalonia.Input.DataFormat.CreateStringApplicationFormat("relay-item");

    static void AttachDragDrop(Control row, SidebarNode node)
    {
        DragDrop.SetAllowDrop(row, true);
        if (node.Model is not ApiCollection)
        {
            PointerPressedEventArgs? pressed = null;
            Point start = default;
            row.PointerPressed += (_, e) => { pressed = e; start = e.GetPosition(row); };
            row.PointerReleased += (_, _) => pressed = null;
            row.PointerMoved += async (_, e) =>
            {
                if (pressed is not { } down || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
                var p = e.GetPosition(row);
                if (Math.Abs(p.X - start.X) + Math.Abs(p.Y - start.Y) < 6) return;
                pressed = null;
                var data = new DataTransfer();
                data.Add(DataTransferItem.Create(ItemFormat, node.Id.ToString()));
                await DragDrop.DoDragDropAsync(down, data, DragDropEffects.Move);
            };
        }
        row.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(ItemFormat) ? DragDropEffects.Move : DragDropEffects.None;
        });
        row.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (e.DataTransfer.TryGetValue(ItemFormat) is not { } text || !Guid.TryParse(text, out var id)) return;
            Workspace.DropTarget target = node.Model switch
            {
                ApiCollection c => new Workspace.DropTarget.ToCollection(c.Id),
                Folder f => new Workspace.DropTarget.IntoFolder(node.CollectionId, f.Id),
                _ => new Workspace.DropTarget.BeforeItem(node.CollectionId, node.Id),
            };
            Ws.Move(id, target);
        });
    }
}

// MARK: - Environments

class EnvironmentsPane : DockPanel
{
    static Workspace Ws => Workspace.Shared;

    public EnvironmentsPane()
    {
        var footer = SidebarView.Footer(SidebarView.FooterButton("plus", "New Environment", () => _ = Dialogs.Environments(Ws.AddEnvironment())));
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);

        if (Ws.Environments.Count == 0)
        {
            Children.Add(UI.EmptyState("layers", "No environments",
                "An environment holds values like {{baseUrl}} and tokens, so the same request works against local, staging and production.",
                UI.Button("New Environment", "primary", () => _ = Dialogs.Environments(Ws.AddEnvironment()))));
            return;
        }
        var list = new StackPanel { Margin = new Thickness(8, 0) };
        list.Children.Add(UI.Text("Click to edit. The circle marks the active one.", 10.5, default, "tertiary").With(t => t.Margin = new Thickness(6, 2, 0, 6)));
        foreach (var env in Ws.Environments) list.Children.Add(Row(env));
        Children.Add(new ScrollViewer { Content = list });
    }

    static Control Row(ApiEnvironment env)
    {
        var active = Ws.ActiveEnvironmentID == env.Id;
        var toggle = UI.Button(new Icon { Kind = active ? "check" : "circle", Size = 14, Thickness = active ? 2 : 1.4 }.Bind(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, active ? "Accent" : "TextSecondary"),
            "icon", () => { Ws.ActiveEnvironmentID = active ? null : env.Id; Ws.SidebarMode = SidebarMode.Collections; Ws.SidebarMode = SidebarMode.Environments; },
            active ? "Active. Click to turn off." : "Make active");
        var text = UI.Column(1,
            UI.Text(env.Name, 12.5, active ? FontWeight.SemiBold : FontWeight.Normal),
            UI.Text(env.Variables.Count == 1 ? "1 variable" : $"{env.Variables.Count} variables", 10.5, default, "secondary"));
        var row = new Border
        {
            Padding = new Thickness(4, 5),
            CornerRadius = new CornerRadius(7),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = UI.Row(8, toggle, text),
        };
        row.PointerEntered += (_, _) => row.Background = UI.Res("Hover");
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.Tapped += (_, e) => { if (e.Source is not Button) _ = Dialogs.Environments(env.Id); };
        row.ContextMenu = new ContextMenu
        {
            Items =
            {
                new MenuItem { Header = active ? "Deactivate" : "Set Active", Command = new RelayCommand(() => Ws.ActiveEnvironmentID = active ? null : env.Id) },
                new MenuItem { Header = "Edit…", Command = new AsyncRelayCommand(() => Dialogs.Environments(env.Id)) },
                new MenuItem { Header = "Duplicate", Command = new RelayCommand(() => Ws.DuplicateEnvironment(env)) },
                new MenuItem { Header = "Export as Postman Environment…", Command = new AsyncRelayCommand(() => Ws.ExportEnvironment(env)) },
                new Separator(),
                new MenuItem { Header = "Delete…", Command = new AsyncRelayCommand(() => Ws.DeleteEnvironment(env)) },
            },
        };
        return row;
    }
}

// MARK: - History

class HistoryPane : DockPanel
{
    static Workspace Ws => Workspace.Shared;
    readonly StackPanel _list = new() { Margin = new Thickness(8, 0) };
    string _query = "";

    public HistoryPane()
    {
        var search = new TextBox { PlaceholderText = "Search history", Height = 32, Margin = new Thickness(10, 0, 10, 6) };
        search.InnerLeftContent = new Icon { Kind = "search", Size = 13, Margin = new Thickness(8, 0, 2, 0) }.Bind(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, "TextTertiary");
        search.TextChanged += (_, _) => { _query = search.Text ?? ""; Fill(); };
        SetDock(search, Dock.Top);
        Children.Add(search);

        var clear = SidebarView.FooterButton("trash", "Clear History", () => _ = Ws.ClearHistory());
        clear.IsEnabled = Ws.History.Count > 0;
        var footer = SidebarView.Footer(clear);
        SetDock(footer, Dock.Bottom);
        Children.Add(footer);

        if (Ws.History.Count == 0)
        {
            Children.Add(UI.EmptyState("clock", "No history yet", "Every request you send is listed here."));
            return;
        }
        Children.Add(new ScrollViewer { Content = _list });
        Ws.History.CollectionChanged += (_, _) => Fill();
        Fill();
    }

    void Fill()
    {
        _list.Children.Clear();
        var entries = _query.Length == 0 ? Ws.History.ToList() : Ws.History.Where(h =>
            h.Request.Url.Contains(_query, StringComparison.OrdinalIgnoreCase) || h.Request.Name.Contains(_query, StringComparison.OrdinalIgnoreCase)).ToList();
        string? group = null;
        foreach (var entry in entries.Take(300))
        {
            var local = entry.Date.ToLocalTime();
            var title = local.Date == DateTime.Today ? "Today" : local.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : local.ToString("dddd, MMMM d");
            if (title != group)
            {
                group = title;
                _list.Children.Add(UI.Text(title.ToUpperInvariant(), 10.5, FontWeight.SemiBold, "tertiary").With(t => t.Margin = new Thickness(6, 10, 0, 4)));
            }
            _list.Children.Add(Row(entry));
        }
    }

    static Control Row(HistoryEntry entry)
    {
        var url = entry.Request.Url;
        foreach (var prefix in new[] { "https://", "http://" })
            if (url.StartsWith(prefix, StringComparison.Ordinal)) { url = url[prefix.Length..]; break; }
        var method = UI.MethodBadge(entry.Request.Method, 10);
        method.Width = 40;
        method.VerticalAlignment = VerticalAlignment.Top;
        var status = entry.Status is { } s
            ? UI.Text($"{s}", 10.5, FontWeight.SemiBold).With(t => t.Foreground = Palette.Status(s))
            : UI.Text("Failed", 10.5, FontWeight.SemiBold).With(t => t.Foreground = Palette.Red);
        var details = UI.Row(6, status, UI.Text(Format.Duration(entry.Duration), 10.5, default, "secondary"),
            UI.Text(entry.Date.ToLocalTime().ToString("t"), 10.5, default, "secondary"));
        var text = UI.Column(2, UI.Code(url.Length == 0 ? "(no URL)" : url, 11).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis), details);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(method);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var row = new Border { Padding = new Thickness(6, 5), CornerRadius = new CornerRadius(7), Background = Brushes.Transparent, Child = grid, Cursor = new Cursor(StandardCursorType.Hand) };
        row.PointerEntered += (_, _) => row.Background = UI.Res("Hover");
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.Tapped += (_, _) => Ws.OpenHistory(entry);
        ToolTip.SetTip(row, entry.Error ?? entry.Request.Url);
        row.ContextMenu = new ContextMenu
        {
            Items =
            {
                new MenuItem { Header = "Open in New Tab", Command = new RelayCommand(() => Ws.OpenHistory(entry)) },
                new MenuItem { Header = "Copy URL", Command = new AsyncRelayCommand(() => Ws.Copy(entry.Request.Url, "URL")) },
                new Separator(),
                new MenuItem { Header = "Delete", Command = new RelayCommand(() => Ws.History.Remove(entry)) },
            },
        };
        return row;
    }
}
