using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Relay.Core;

namespace Relay.App.Views;

/// <summary>Shared building blocks, the counterparts of Components.swift.</summary>
static class UI
{
    public static IBrush Res(string key) =>
        Application.Current!.TryFindResource(key, Application.Current!.ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

    /// <summary>A theme brush that follows light/dark switches.</summary>
    public static T Bind<T>(this T control, AvaloniaProperty property, string resource) where T : Control
    {
        control[!property] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(resource);
        return control;
    }

    public static TextBlock Text(string text, double size = 13, FontWeight weight = default, string? cls = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight == default ? FontWeight.Normal : weight, VerticalAlignment = VerticalAlignment.Center };
        if (cls != null) foreach (var c in cls.Split(' ')) t.Classes.Add(c);
        return t;
    }

    public static TextBlock Code(string text, double size = 12, FontWeight weight = default)
    {
        var t = Text(text, size, weight);
        t.Classes.Add("code");
        return t;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        p.Children.AddRange(children);
        return p;
    }

    public static StackPanel Column(double spacing, params Control[] children)
    {
        var p = new StackPanel { Spacing = spacing };
        p.Children.AddRange(children);
        return p;
    }

    public static Border Hairline(bool vertical = false)
    {
        var b = new Border { Height = vertical ? double.NaN : 1, Width = vertical ? 1 : double.NaN };
        b.Classes.Add("hairline");
        return b;
    }

    public static Button Button(object content, string cls, Action? click = null, string? tip = null)
    {
        var b = new Button { Content = content };
        foreach (var c in cls.Split(' ')) b.Classes.Add(c);
        if (click != null) b.Click += (_, _) => click();
        if (tip != null) ToolTip.SetTip(b, tip);
        return b;
    }

    public static Button IconButton(string icon, string tip, Action click, double size = 13) =>
        Button(new Icon { Kind = icon, Size = size }, "icon", click, tip);

    public static TextBlock MethodBadge(string method, double size = 10.5)
    {
        var t = Code(Palette.ShortMethod(method), size, FontWeight.Bold);
        t.Foreground = Palette.Method(method);
        return t;
    }

    public static Border StatusPill(int code)
    {
        var phrase = HttpStatus.Phrase(code);
        var brush = Palette.Status(code);
        return new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 3),
            Background = Palette.Tint(brush, 0.14),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = phrase.Length == 0 ? $"{code}" : $"{code} {phrase}", Foreground = brush, FontWeight = FontWeight.Bold, FontSize = 12, Classes = { "code" } },
        };
    }

    /// <summary>A small count/kind badge beside a tab title: lime on the open tab, grey on the rest.</summary>
    public static Border Badge(string text, bool selected) => new()
    {
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Background = Res(selected ? "AccentSoft" : "Selected"),
        Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Res(selected ? "Accent" : "TextSecondary"), Classes = { "code" } },
    };

    /// <summary>Text tabs with an accent underline and an optional badge.</summary>
    public static StackPanel UnderlineTabs<T>(IEnumerable<(T Id, string Title, string? Badge)> items, T selected, Action<T> select)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 22 };
        foreach (var (id, title, badge) in items)
        {
            var isSelected = EqualityComparer<T>.Default.Equals(id, selected);
            var label = Row(5, Text(title, 13));
            if (badge != null) label.Children.Add(Badge(badge, isSelected));
            var underline = new Border { Name = "Underline", Margin = new Thickness(0, 8, 0, 0) };
            var button = Button(new StackPanel { Children = { label, underline } }, isSelected ? "tab selected" : "tab", () => select(id));
            button.Padding = new Thickness(0, 9, 0, 0);
            row.Children.Add(button);
        }
        return row;
    }

    /// <summary>A segmented control: a recessed track with the selected option raised.</summary>
    public static Border Pills<T>(IEnumerable<(T Id, string Title)> options, T selected, Action<T> select, double height = 24, double fontSize = 12, bool stretch = false)
    {
        var grid = new UniformGrid { Rows = 1 };
        Panel panel = stretch ? grid : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (id, title) in options)
        {
            var isSelected = EqualityComparer<T>.Default.Equals(id, selected);
            Control label = Text(title, fontSize);
            if (stretch) label = new Viewbox { Child = label, StretchDirection = StretchDirection.DownOnly };
            var b = Button(label, isSelected ? "pill selected" : "pill", () => select(id));
            if (stretch) b.Padding = new Thickness(6, 0);
            b.Height = height;
            panel.Children.Add(b);
        }
        var border = new Border { Child = panel };
        border.Classes.Add("pills");
        return border;
    }

    public static Control EmptyState(string icon, string title, string message, Control? actions = null)
    {
        var stack = Column(10,
            new Icon { Kind = icon, Size = 30, Thickness = 1.1, Foreground = Res("TextTertiary"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) },
            new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = message, FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Center, Classes = { "secondary" } });
        if (actions != null)
        {
            actions.HorizontalAlignment = HorizontalAlignment.Center;
            actions.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(actions);
        }
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        stack.VerticalAlignment = VerticalAlignment.Center;
        stack.Margin = new Thickness(24);
        return stack;
    }

    public static Border KeyHint(string keys) => new()
    {
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(6, 2),
        BorderThickness = new Thickness(1),
        BorderBrush = Res("Line"),
        Background = Res("Field"),
        Child = new TextBlock { Text = keys, FontSize = 11, FontWeight = FontWeight.Medium, Classes = { "secondary" } },
    };

    /// <summary>A text field in the theme's chrome, with an optional placeholder.</summary>
    public static TextBox Field(string text, Action<string> changed, string placeholder = "", bool code = false)
    {
        var box = new TextBox { Text = text, PlaceholderText = placeholder };
        if (code) box.Classes.Add("code");
        box.TextChanged += (_, _) => changed(box.Text ?? "");
        return box;
    }

    /// <summary>A dropdown of enum-like options.</summary>
    public static ComboBox Picker<T>(IEnumerable<(T Value, string Title)> options, T selected, Action<T> changed)
    {
        var list = options.ToList();
        var combo = new ComboBox
        {
            ItemsSource = list.Select(o => o.Title).ToList(),
            SelectedIndex = Math.Max(0, list.FindIndex(o => EqualityComparer<T>.Default.Equals(o.Value, selected))),
            MinWidth = 160,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0) changed(list[combo.SelectedIndex].Value);
        };
        return combo;
    }
}
