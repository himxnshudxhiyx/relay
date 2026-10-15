using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Relay.App.Views;

namespace Relay.App;

public partial class MainWindow : Window
{
    static Workspace Ws => Workspace.Shared;

    /// <summary>Windows' minimise, maximise and close buttons (3 × 46) plus a gap.</summary>
    const double CaptionButtonsWidth = 150;
    static Settings Prefs => Settings.Shared;

    public MainWindow()
    {
        InitializeComponent();
        BuildToolbar();
        ApplyAppearance();
        Columns.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(Prefs.SidebarWidth, 240, 440));
        Columns.ColumnDefinitions[0].MinWidth = 240;
        Columns.ColumnDefinitions[0].MaxWidth = 440;

        Ws.PropertyChanged += OnWorkspaceChanged;
        Prefs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Settings.Appearance)) ApplyAppearance();
        };
        ShowDetail();
        // Colours are picked when views are built; a theme switch rebuilds them.
        Application.Current!.ActualThemeVariantChanged += (_, _) =>
        {
            Detail.Content = null;
            ShowDetail();
            TabStrip.Rebuild();
            Sidebar.Rebuild();
        };

        // Shortcuts first, before a text box takes Ctrl+L or Ctrl+D for itself.
        AddHandler(KeyDownEvent, (_, e) => { if (Commands.Handle(e)) e.Handled = true; }, RoutingStrategies.Tunnel);
        Closing += (_, _) =>
        {
            Prefs.SidebarWidth = Columns.ColumnDefinitions[0].ActualWidth;
            Ws.Flush();
        };
        // Leave room for the caption buttons Windows draws over the title bar.
        // Windows draws the icon and title at the top left and its caption buttons at
        // the top right, over our title bar: keep the toolbar clear of the buttons and
        // don't repeat the title.
        if (OperatingSystem.IsWindows())
        {
            TitleText.IsVisible = false;
            Toolbar.Margin = new Thickness(0, 0, CaptionButtonsWidth, 0);
        }
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowDecorationMarginProperty && WindowDecorationMargin.Right > 0)
                Toolbar.Margin = new Thickness(0, 0, Math.Max(WindowDecorationMargin.Right + 12, OperatingSystem.IsWindows() ? CaptionButtonsWidth : 12), 0);
        };
#if DEBUG
        DevSnapshot.Attach(this);
#endif
    }

    void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Workspace.SelectedTab):
                ShowDetail();
                break;
            case nameof(Workspace.Toast):
                ShowToast(Ws.Toast);
                break;
        }
    }

    TabState? _shown;

    /// <summary>A fresh request view per tab, so editors don't carry undo history or scroll position across tabs.</summary>
    void ShowDetail()
    {
        if (Ws.SelectedTab == _shown && Detail.Content != null) return;
        _shown = Ws.SelectedTab;
        Detail.Content = _shown is { } tab ? new RequestView(tab) : new WelcomeView();
    }

    void ShowToast(string? text)
    {
        if (text != null) ToastText.Text = text;
        Toast.IsVisible = true;
        Toast.Opacity = text == null ? 0 : 1;
    }

    void ApplyAppearance()
    {
        RequestedThemeVariant = null;
        Application.Current!.RequestedThemeVariant = Prefs.Appearance switch
        {
            Appearance.Light => ThemeVariant.Light,
            Appearance.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    // MARK: Toolbar

    void BuildToolbar()
    {
        Toolbar.Children.Add(Toolbar_Variables());
        Toolbar.Children.Add(Toolbar_Environment());
        Toolbar.Children.Add(ToolButton(Prefs.Layout == PaneLayout.Stacked ? "split-h" : "split-v",
            "Response beside / below the request (Ctrl+\\)", b =>
            {
                Prefs.Layout = Prefs.Layout == PaneLayout.Stacked ? PaneLayout.SideBySide : PaneLayout.Stacked;
                ((Icon)b.Content!).Kind = Prefs.Layout == PaneLayout.Stacked ? "split-h" : "split-v";
            }));
        Toolbar.Children.Add(Toolbar_Appearance());
        Toolbar.Children.Add(Toolbar_Menu());
    }

    static Button ToolButton(string icon, string tip, Action<Button>? click = null)
    {
        var button = new Button { Content = new Icon { Kind = icon, Size = 15 }, Width = 32, Height = 30 };
        button.Classes.Add("icon");
        ToolTip.SetTip(button, tip);
        if (click != null) button.Click += (_, _) => click(button);
        return button;
    }

    Control Toolbar_Variables()
    {
        var button = ToolButton("eye", "Quick look at the variables in use");
        button.Flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight, Content = new VariablesPopover() };
        return button;
    }

    Control Toolbar_Environment()
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Medium };
        var button = new Button
        {
            Height = 30,
            Padding = new Thickness(10, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children = { new Icon { Kind = "layers", Size = 14 }, label, new Icon { Kind = "chevron-down", Size = 11, Foreground = Palette.Secondary } },
            },
        };
        button.Classes.Add("ghost");
        ToolTip.SetTip(button, "Active environment: variables like {{baseUrl}} come from here");
        void Refresh() => label.Text = Ws.ActiveEnvironment?.Name ?? "No Environment";
        Refresh();
        Ws.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Workspace.ActiveEnvironmentID)) Refresh(); };
        Ws.Environments.CollectionChanged += (_, _) => Refresh();
        button.Click += (_, _) =>
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
            void Add(string title, Guid? id) => menu.Items.Add(new MenuItem
            {
                Header = title,
                Icon = Ws.ActiveEnvironmentID == id ? new Icon { Kind = "check", Size = 12 } : null,
                Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => Ws.ActiveEnvironmentID = id),
            });
            Add("No Environment", null);
            foreach (var env in Ws.Environments) Add(env.Name, env.Id);
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Manage Environments…", InputGesture = new KeyGesture(Key.E, KeyModifiers.Control),
                Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => _ = Dialogs.Environments(Ws.ActiveEnvironmentID)) });
            menu.ShowAt(button);
        };
        return button;
    }

    Control Toolbar_Appearance()
    {
        var icon = new Icon { Size = 15 };
        void Refresh() => icon.Kind = Prefs.Appearance switch { Appearance.Light => "sun", Appearance.Dark => "moon", _ => "contrast" };
        Refresh();
        Prefs.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Settings.Appearance)) Refresh(); };
        var button = new Button { Content = icon, Width = 32, Height = 30 };
        button.Classes.Add("icon");
        ToolTip.SetTip(button, "Appearance");
        button.Click += (_, _) =>
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
            foreach (var (title, value, gesture) in new[] {
                ("System", Appearance.System, (KeyGesture?)null),
                ("Light", Appearance.Light, new KeyGesture(Key.L, KeyModifiers.Control | KeyModifiers.Alt)),
                ("Dark", Appearance.Dark, new KeyGesture(Key.D, KeyModifiers.Control | KeyModifiers.Alt)) })
                menu.Items.Add(new MenuItem
                {
                    Header = title,
                    InputGesture = gesture,
                    Icon = Prefs.Appearance == value ? new Icon { Kind = "check", Size = 12 } : null,
                    Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => Prefs.Appearance = value),
                });
            menu.ShowAt(button);
        };
        return button;
    }

    /// <summary>The Mac app's menu bar, as one menu: Windows apps of this kind don't have a menu bar.</summary>
    Control Toolbar_Menu()
    {
        var button = ToolButton("more", "Menu");
        button.Click += (_, _) =>
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
            foreach (var (group, items) in Commands.Menu)
            {
                var sub = new MenuItem { Header = group };
                foreach (var command in items)
                {
                    if (command == null)
                    {
                        sub.Items.Add(new Separator());
                        continue;
                    }
                    sub.Items.Add(new MenuItem
                    {
                        Header = command.Title,
                        InputGesture = command.Gesture,
                        Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(command.Run),
                    });
                }
                menu.Items.Add(sub);
            }
            menu.ShowAt(button);
        };
        return button;
    }
}
