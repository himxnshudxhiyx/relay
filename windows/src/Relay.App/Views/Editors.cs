using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Relay.Core;

namespace Relay.App.Views;

/// <summary>
/// Params, headers, form fields and variables: one row per pair, always with
/// a blank row at the end to type into, and a Bulk Edit mode ("key: value" per
/// line, // disables a row). Ported from KeyValueTable.swift and KeyValueSection.
/// </summary>
public class KeyValueTable : DockPanel
{
    readonly ObservableCollection<KeyValue> _rows;
    readonly string _keyPlaceholder, _valuePlaceholder;
    readonly bool _allowsFiles, _allowsSecrets;
    readonly StackPanel _table = new();
    readonly ContentControl _body = new();
    readonly Button _bulkToggle;
    bool _bulk;
    KeyValue _pending = new();

    public KeyValueTable(ObservableCollection<KeyValue> rows, string caption, string keyPlaceholder = "Key", string valuePlaceholder = "Value",
                         bool allowsFiles = false, bool allowsSecrets = false, Control? accessory = null, Func<VariableScope>? scope = null)
    {
        _rows = rows;
        _keyPlaceholder = keyPlaceholder;
        _valuePlaceholder = valuePlaceholder;
        _allowsFiles = allowsFiles;
        _allowsSecrets = allowsSecrets;
        Scope = scope;

        _bulkToggle = UI.Button("Bulk Edit", "link", ToggleBulk, "Edit as text, one “key: value” per line");
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(20, 10, 14, 8) };
        header.Children.Add(UI.Text(caption, 11.5, default, "secondary").With(t => t.TextTrimming = TextTrimming.CharacterEllipsis));
        if (accessory != null)
        {
            Grid.SetColumn(accessory, 1);
            header.Children.Add(accessory);
        }
        if (!allowsFiles)
        {
            Grid.SetColumn(_bulkToggle, 2);
            header.Children.Add(_bulkToggle);
        }
        SetDock(header, Dock.Top);
        Children.Add(header);
        Children.Add(_body);

        _rows.CollectionChanged += OnRowsChanged;
        DetachedFromVisualTree += (_, _) => _rows.CollectionChanged -= OnRowsChanged;
        ShowTable();
    }

    public Func<VariableScope>? Scope { get; }

    void ToggleBulk()
    {
        _bulk = !_bulk;
        _bulkToggle.Content = _bulk ? "Table" : "Bulk Edit";
        if (!_bulk)
        {
            ShowTable();
            return;
        }
        var editor = new CodeView { Text = BulkEdit.Text(_rows), Language = CodeLanguage.Plain, Scope = Scope };
        editor.PropertyChanged += (_, e) =>
        {
            if (e.Property != CodeView.TextProperty) return;
            var parsed = BulkEdit.Parse(editor.Text, _rows.ToList());
            Replace(parsed);
        };
        _body.Content = new Border
        {
            Margin = new Thickness(20, 0, 20, 16),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = editor,
        }.Bind(Border.BorderBrushProperty, "Line").Bind(Border.BackgroundProperty, "Editor");
    }

    bool _replacing;

    void Replace(IList<KeyValue> rows)
    {
        if (rows.SequenceEqual(_rows)) return;
        _replacing = true;
        _rows.Clear();
        foreach (var r in rows) _rows.Add(r);
        _replacing = false;
    }

    void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_bulk || _replacing) return;
        // The blank row being typed into became real: keep its controls (and focus), add a fresh blank row.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems?.Count == 1 && e.NewItems[0] == _pending)
        {
            _pending = new KeyValue();
            _table.Children.Add(RowView(_pending, isPending: true));
            return;
        }
        ShowTable();
    }

    void ShowTable()
    {
        _table.Children.Clear();
        var header = new Grid { ColumnDefinitions = Columns(), Height = 26 }.Bind(Grid.BackgroundProperty, "Field");
        header.Children.Add(Cell(UI.Text(_keyPlaceholder.ToUpperInvariant(), 10, FontWeight.SemiBold, "tertiary"), 1));
        header.Children.Add(Cell(UI.Text("VALUE", 10, FontWeight.SemiBold, "tertiary"), 2));
        _table.Children.Add(header);
        foreach (var row in _rows) _table.Children.Add(RowView(row, isPending: false));
        _pending = new KeyValue();
        _table.Children.Add(RowView(_pending, isPending: true));
        var box = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = _table,
            Margin = new Thickness(20, 0, 20, 16),
            VerticalAlignment = VerticalAlignment.Top,
        }.Bind(Border.BorderBrushProperty, "Line");
        _body.Content = new ScrollViewer { Content = box };
    }

    static ColumnDefinitions Columns() => new("32,*,1.4*,32");

    static Control Cell(Control content, int column)
    {
        Grid.SetColumn(content, column);
        content.Margin = new Thickness(column == 0 ? 0 : 8, 0, 4, 0);
        return content;
    }

    Control RowView(KeyValue kv, bool isPending)
    {
        var grid = new Grid { ColumnDefinitions = Columns(), MinHeight = 34 };
        grid.Children.Add(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top, [Grid.ColumnSpanProperty] = 4 }.Bind(Border.BackgroundProperty, "Line"));

        var check = new CheckBox { IsChecked = kv.Enabled, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = !isPending };
        check[!CheckBox.IsCheckedProperty] = new Binding(nameof(KeyValue.Enabled)) { Source = kv, Mode = BindingMode.TwoWay };
        grid.Children.Add(Cell(check, 0));

        var key = Box(kv, nameof(KeyValue.Key), _keyPlaceholder, isPending);
        grid.Children.Add(Cell(key, 1));
        grid.Children.Add(Cell(ValueCell(kv, isPending), 2));

        if (!isPending)
        {
            var remove = UI.IconButton("close", "Remove", () => _rows.Remove(kv), 10);
            remove.Opacity = 0;
            grid.PointerEntered += (_, _) => remove.Opacity = 1;
            grid.PointerExited += (_, _) => remove.Opacity = 0;
            grid.Children.Add(Cell(remove, 3));
        }
        // Disabled rows stay visible, dimmed: they're kept but not sent.
        void Dim() => key.Opacity = kv.Enabled || isPending ? 1 : 0.5;
        kv.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(KeyValue.Enabled)) Dim(); };
        Dim();
        return grid;
    }

    Control ValueCell(KeyValue kv, bool isPending)
    {
        if (_allowsFiles && kv.IsFile)
        {
            var path = UI.Code(kv.Value.Length == 0 ? "No file chosen" : Path.GetFileName(kv.Value), 12);
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(path, kv.Value);
            var choose = UI.Button("Choose…", "link", async () =>
            {
                if (await Dialogs.PickFilePath() is { } p) { kv.Value = p; path.Text = Path.GetFileName(p); }
            });
            var asText = UI.IconButton("file", "Send as text instead", () => { kv.IsFile = false; kv.Value = ""; ShowTable(); }, 12);
            return UI.Row(6, asText, choose, path);
        }
        var value = Box(kv, nameof(KeyValue.Value), _valuePlaceholder, isPending);
        if (!_allowsSecrets && !_allowsFiles) return value;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(value);
        Control extra;
        if (_allowsSecrets)
        {
            // Secrets are masked on screen only; they're stored as plain JSON like everything else.
            void Mask() => value.PasswordChar = kv.IsSecret ? '•' : default;
            Mask();
            var icon = new Icon { Kind = kv.IsSecret ? "key" : "eye", Size = 12 };
            extra = UI.Button(icon, "icon", () =>
            {
                kv.IsSecret = !kv.IsSecret;
                icon.Kind = kv.IsSecret ? "key" : "eye";
                Mask();
            }, "Mark as secret (masked on screen)");
        }
        else
        {
            extra = UI.IconButton("file", "Upload a file instead", () =>
            {
                if (isPending) _rows.Add(kv);
                kv.IsFile = true;
                kv.Value = "";
                ShowTable();
            }, 12);
        }
        extra.Width = extra.Height = 24;
        Grid.SetColumn(extra, 1);
        grid.Children.Add(extra);
        return grid;
    }

    TextBox Box(KeyValue kv, string property, string placeholder, bool isPending)
    {
        var box = new TextBox { PlaceholderText = placeholder, FontSize = 12 };
        box.Classes.Add("bare");
        box.Classes.Add("code");
        box[!TextBox.TextProperty] = new Binding(property) { Source = kv, Mode = BindingMode.TwoWay };
        if (isPending)
            box.TextChanged += (_, _) =>
            {
                if (kv == _pending && !string.IsNullOrEmpty(box.Text) && !_rows.Contains(kv)) _rows.Add(kv);
            };
        return box;
    }
}

/// <summary>Auth for a request, folder or collection. Ported from AuthEditor in RequestEditor.swift.</summary>
public class AuthEditor : ScrollViewer
{
    readonly Auth _auth;
    readonly bool _allowsInherit;
    readonly Func<string> _inheritDescription;
    readonly StackPanel _form = new() { Spacing = 14, Margin = new Thickness(20, 16), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };

    public AuthEditor(Auth auth, bool allowsInherit, Func<string> inheritDescription)
    {
        _auth = auth;
        _allowsInherit = allowsInherit;
        _inheritDescription = inheritDescription;
        Content = _form;
        Build();
    }

    void Build()
    {
        _form.Children.Clear();
        var types = Enum.GetValues<AuthType>().Where(t => _allowsInherit || t != AuthType.Inherit)
            .OrderBy(t => t == AuthType.Inherit ? 0 : t == AuthType.None ? 1 : 2 + (int)t)
            .Select(t => (t, t.Title()));
        _form.Children.Add(Labeled("Type", UI.Picker(types, _auth.Type, t => { _auth.Type = t; Build(); }).With(c => c.Width = 230)));
        switch (_auth.Type)
        {
            case AuthType.Inherit:
                _form.Children.Add(Note(_inheritDescription()));
                break;
            case AuthType.None:
                _form.Children.Add(Note("No credentials are added."));
                break;
            case AuthType.Bearer:
                _form.Children.Add(Labeled("Token", Secret(nameof(Auth.Token), "Token or {{variable}}")));
                _form.Children.Add(Note("Sent as the header “Authorization: Bearer <token>”."));
                break;
            case AuthType.Basic:
                _form.Children.Add(Labeled("Username", Plain(nameof(Auth.Username), "Username")));
                _form.Children.Add(Labeled("Password", Secret(nameof(Auth.Password), "Password")));
                _form.Children.Add(Note("Sent as “Authorization: Basic <base64 of username:password>”."));
                break;
            case AuthType.ApiKey:
                _form.Children.Add(Labeled("Key", Plain(nameof(Auth.Key), "X-API-Key")));
                _form.Children.Add(Labeled("Value", Secret(nameof(Auth.Value), "Value or {{variable}}")));
                _form.Children.Add(Labeled("Add to", UI.Pills([(ApiKeyLocation.Header, "Header"), (ApiKeyLocation.Query, "Query Param")],
                    _auth.Location, l => { _auth.Location = l; Build(); }).With(p => p.HorizontalAlignment = HorizontalAlignment.Left)));
                break;
        }
    }

    TextBox Plain(string property, string placeholder)
    {
        var box = new TextBox { PlaceholderText = placeholder, Classes = { "code" } };
        box[!TextBox.TextProperty] = new Binding(property) { Source = _auth, Mode = BindingMode.TwoWay };
        return box;
    }

    /// <summary>Masked, with a reveal toggle. Starts revealed when empty or a {{variable}}: a reference isn't secret.</summary>
    Control Secret(string property, string placeholder)
    {
        var box = Plain(property, placeholder);
        var value = box.Text ?? (string?)typeof(Auth).GetProperty(property)!.GetValue(_auth) ?? "";
        var revealed = value.Length == 0 || value.Contains("{{");
        box.PasswordChar = revealed ? default : '•';
        var icon = new Icon { Kind = revealed ? "eye-off" : "eye", Size = 13 };
        var toggle = UI.Button(icon, "icon", () =>
        {
            revealed = !revealed;
            box.PasswordChar = revealed ? default : '•';
            icon.Kind = revealed ? "eye-off" : "eye";
        }, "Show / hide");
        box.InnerRightContent = toggle;
        return box;
    }

    static Control Labeled(string label, Control content)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("84,*"), ColumnSpacing = 12 };
        grid.Children.Add(UI.Text(label, 12, FontWeight.Medium, "secondary"));
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    static Control Note(string text) => new TextBlock
    {
        Text = text,
        FontSize = 11.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(96, 0, 0, 0),
        Classes = { "secondary" },
    };
}
