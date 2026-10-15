using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;

namespace Relay.App.Views;

/// <summary>Open requests as tabs: method, name, unsaved dot; the open one has a lime top edge.</summary>
public class TabStripView : UserControl
{
    static Workspace Ws => Workspace.Shared;

    readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    readonly ScrollViewer _scroll;

    public TabStripView()
    {
        _scroll = new ScrollViewer
        {
            Content = _tabs,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        var add = UI.IconButton("plus", "New Request (Ctrl+T)", () => Ws.NewTab(), 14);
        add.Margin = new Thickness(6, 0);
        add.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 40 };
        grid.Children.Add(_scroll);
        Grid.SetColumn(add, 1);
        grid.Children.Add(add);
        Content = new StackPanel { Children = { grid, UI.Hairline() } }.Bind(BackgroundProperty, "Chrome");

        Ws.Tabs.CollectionChanged += (_, _) => Rebuild();
        Ws.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Workspace.SelectedTab)) Rebuild(); };
        Rebuild();
    }

    readonly List<(TabState Tab, PropertyChangedEventHandler Handler)> _watching = [];

    public void Rebuild()
    {
        foreach (var (tab, handler) in _watching)
        {
            tab.PropertyChanged -= handler;
            tab.Draft.PropertyChanged -= handler;
            tab.Model.PropertyChanged -= handler;
        }
        _watching.Clear();
        _tabs.Children.Clear();
        IsVisible = Ws.Tabs.Count > 0;
        foreach (var tab in Ws.Tabs)
        {
            var chip = new TabChip(tab);
            _tabs.Children.Add(chip);
            PropertyChangedEventHandler handler = (_, _) => chip.Refresh();
            tab.PropertyChanged += handler;
            tab.Draft.PropertyChanged += handler;
            tab.Model.PropertyChanged += handler;
            _watching.Add((tab, handler));
            if (tab == Ws.SelectedTab) Avalonia.Threading.Dispatcher.UIThread.Post(() => chip.BringIntoView());
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property.Name == "ActualThemeVariant") Rebuild();
    }

    /// <summary>URL-like names share a host, so keep both ends: "devgoapi.liv…/getSection".</summary>
    static string MiddleTrim(string name, int max = 30) =>
        name.Length <= max ? name : name[..(max * 2 / 5)] + "…" + name[^(max - max * 2 / 5 - 1)..];

    sealed class TabChip : Border
    {
        readonly TabState _tab;
        readonly TextBlock _method;
        readonly TextBlock _name;
        readonly Panel _trailing = new() { Width = 16, Height = 16 };
        bool _hovering;

        public TabChip(TabState tab)
        {
            _tab = tab;
            var selected = Ws.SelectedTab == tab;
            _method = UI.MethodBadge(tab.Draft.Method, 10.5);
            _name = new TextBlock
            {
                FontSize = 12.5,
                FontWeight = selected ? FontWeight.Medium : FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 180,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var row = UI.Row(7, _method, _name, _trailing);
            Child = row;
            Padding = new Thickness(12, 0, 7, 0);
            Height = 40;
            MaxWidth = 240;
            BorderThickness = new Thickness(0, selected ? 2 : 0, 1, 0);
            BorderBrush = UI.Res("Line");
            Background = selected ? UI.Res("Canvas") : Brushes.Transparent;
            if (selected)
            {
                // Lime top edge on the open tab, hairline on its right.
                Child = null;
                Child = new Grid
                {
                    Children =
                    {
                        new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, Background = UI.Res("Accent"), Margin = new Thickness(-12, 0, -7, 0) },
                        row,
                    },
                };
                BorderThickness = new Thickness(0, 0, 1, 0);
            }
            Cursor = new Cursor(StandardCursorType.Hand);
            PointerEntered += (_, _) => { _hovering = true; if (!selected) Background = UI.Res("Hover"); Refresh(); };
            PointerExited += (_, _) => { _hovering = false; if (!selected) Background = Brushes.Transparent; Refresh(); };
            PointerPressed += (_, e) =>
            {
                var point = e.GetCurrentPoint(this);
                if (point.Properties.IsMiddleButtonPressed) _ = Ws.Close(tab);
                else if (point.Properties.IsLeftButtonPressed) Ws.SelectedTab = tab;
            };
            ContextMenu = new ContextMenu
            {
                Items =
                {
                    new MenuItem { Header = "Close Tab", Command = new AsyncRelayCommand(() => Ws.Close(tab)) },
                    new MenuItem { Header = "Close Other Tabs", Command = new AsyncRelayCommand(() => Ws.CloseOthers(tab)) },
                    new MenuItem { Header = "Close All Tabs", Command = new AsyncRelayCommand(() => Ws.CloseAll()) },
                    new Separator(),
                    new MenuItem { Header = "Duplicate Tab", Command = new RelayCommand(() => Ws.DuplicateTab(tab)) },
                    new MenuItem { Header = "Copy as cURL", Command = new AsyncRelayCommand(() => Ws.Copy(Ws.Curl(tab), "cURL")) },
                },
            };
            Refresh();
        }

        public void Refresh()
        {
            var selected = Ws.SelectedTab == _tab;
            _method.Text = Palette.ShortMethod(_tab.Draft.Method);
            _method.Foreground = Palette.Method(_tab.Draft.Method);
            _name.Text = MiddleTrim(_tab.Draft.Name);
            _name.Foreground = UI.Res(selected ? "Text" : "TextSecondary");
            _name.Opacity = _tab.Model.IsPreview ? 0.8 : 1;
            ToolTip.SetTip(this, _tab.Model.IsPreview ? $"{_tab.Draft.Name}: preview. Edit it or double-click in the sidebar to keep it open." : _tab.Draft.Url);

            _trailing.Children.Clear();
            if (_tab.IsLoading)
            {
                _trailing.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 14, MinWidth = 14, Height = 3, VerticalAlignment = VerticalAlignment.Center });
            }
            else if (_hovering || (selected && !_tab.IsDirty))
            {
                var close = UI.IconButton("close", "Close Tab (Ctrl+W)", () => _ = Ws.Close(_tab), 10);
                close.Width = close.Height = 16;
                close.CornerRadius = new CornerRadius(8);
                _trailing.Children.Add(close);
            }
            else if (_tab.IsDirty)
            {
                _trailing.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = UI.Res("TextSecondary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            }
        }
    }
}
