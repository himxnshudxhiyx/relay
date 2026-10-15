using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Relay.App.Views;

/// <summary>
/// Line icons as vector paths on a 16×16 grid, drawn in the control's Foreground.
/// Stand-ins for the SF Symbols the Mac app uses; Windows' icon fonts aren't on
/// every machine (or on the Mac where this is tested).
/// </summary>
public class Icon : UserControl
{
    static readonly Dictionary<string, string> Paths = new()
    {
        ["chevron-down"] = "M 4,6 L 8,10 L 12,6",
        ["chevron-right"] = "M 6,4 L 10,8 L 6,12",
        ["plus"] = "M 8,2.5 V 13.5 M 2.5,8 H 13.5",
        ["close"] = "M 4,4 L 12,12 M 12,4 L 4,12",
        ["eye"] = "M 1.5,8 C 4,3.5 12,3.5 14.5,8 C 12,12.5 4,12.5 1.5,8 Z M 8,5.75 A 2.25,2.25 0 1 1 7.99,5.75 Z",
        ["eye-off"] = "M 1.5,8 C 4,3.5 12,3.5 14.5,8 C 12,12.5 4,12.5 1.5,8 Z M 2.5,2.5 L 13.5,13.5",
        ["layers"] = "M 8,2 L 14.5,5.5 L 8,9 L 1.5,5.5 Z M 1.5,8.5 L 8,12 L 14.5,8.5",
        ["split-h"] = "M 2,3 H 14 V 13 H 2 Z M 8,3 V 13",
        ["split-v"] = "M 2,3 H 14 V 13 H 2 Z M 2,8 H 14",
        ["contrast"] = "M 8,1.75 A 6.25,6.25 0 1 0 8.01,1.75 Z M 8,1.75 V 14.25",
        ["sun"] = "M 8,5 A 3,3 0 1 0 8.01,5 Z M 8,1 V 2.5 M 8,13.5 V 15 M 1,8 H 2.5 M 13.5,8 H 15 M 3,3 L 4,4 M 12,12 L 13,13 M 13,3 L 12,4 M 4,12 L 3,13",
        ["moon"] = "M 13,10 A 6,6 0 1 1 6,3 A 4.5,4.5 0 0 0 13,10 Z",
        ["more"] = "M 3.5,8 A 0.6,0.6 0 1 0 3.49,8 Z M 8,8 A 0.6,0.6 0 1 0 7.99,8 Z M 12.5,8 A 0.6,0.6 0 1 0 12.49,8 Z",
        ["folder"] = "M 1.75,4 Q 1.75,3 2.75,3 H 6 L 7.5,4.5 H 13.25 Q 14.25,4.5 14.25,5.5 V 12 Q 14.25,13 13.25,13 H 2.75 Q 1.75,13 1.75,12 Z",
        ["search"] = "M 7,2.5 A 4.5,4.5 0 1 1 6.99,2.5 Z M 10.5,10.5 L 14,14",
        ["arrow-right"] = "M 2,8 H 14 M 9.5,3.5 L 14,8 L 9.5,12.5",
        ["send"] = "M 14.5,1.5 L 1.5,7 L 7,9 L 9,14.5 Z M 14.5,1.5 L 7,9",
        ["download"] = "M 8,2 V 10.5 M 4.5,7 L 8,10.5 L 11.5,7 M 2.5,13.5 H 13.5",
        ["wrap"] = "M 2,4 H 14 M 2,8 H 11.5 A 2.25,2.25 0 0 1 11.5,12.5 H 8 M 10,10.5 L 8,12.5 L 10,14.5 M 2,12.5 H 5",
        ["no-wrap"] = "M 1.5,8 H 14.5 M 4.5,5 L 1.5,8 L 4.5,11 M 11.5,5 L 14.5,8 L 11.5,11",
        ["copy"] = "M 5.5,5.5 H 13.5 V 14 H 5.5 Z M 2.5,10.5 V 2 H 10.5",
        ["trash"] = "M 2.5,4 H 13.5 M 6,4 V 2.5 H 10 V 4 M 4,4 L 4.75,14 H 11.25 L 12,4 M 6.75,6.5 V 11.5 M 9.25,6.5 V 11.5",
        ["key"] = "M 5,8 A 3,3 0 1 1 4.99,8 Z M 8,8 H 14.5 M 12.5,8 V 10.5 M 10.5,8 V 10",
        ["file"] = "M 3.5,1.5 H 9.5 L 12.5,4.5 V 14.5 H 3.5 Z M 9.5,1.5 V 4.5 H 12.5",
        ["clock"] = "M 8,1.75 A 6.25,6.25 0 1 0 8.01,1.75 Z M 8,4.5 V 8 L 10.5,9.5",
        ["terminal"] = "M 2,3 H 14 V 13 H 2 Z M 4.5,6 L 6.5,8 L 4.5,10 M 8,10.5 H 11.5",
        ["warning"] = "M 8,2 L 14.5,13.5 H 1.5 Z M 8,6.5 V 9.5 M 8,11.5 V 11.6",
        ["tray"] = "M 1.5,9 L 3.5,3 H 12.5 L 14.5,9 V 13.5 H 1.5 Z M 1.5,9 H 5.5 L 6.5,10.5 H 9.5 L 10.5,9 H 14.5",
        ["import"] = "M 8,1.5 V 9.5 M 5,6.5 L 8,9.5 L 11,6.5 M 2.5,8 V 14 H 13.5 V 8",
        ["sidebar"] = "M 2,3 H 14 V 13 H 2 Z M 6,3 V 13",
        ["check"] = "M 3,8.5 L 6.5,12 L 13,4.5",
        ["circle"] = "M 8,2 A 6,6 0 1 0 8.01,2 Z",
        ["dot"] = "M 8,5 A 3,3 0 1 0 8.01,5 Z",
    };

    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<Icon, string>(nameof(Kind), "");
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Icon, double>(nameof(Size), 14);
    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<Icon, double>(nameof(Thickness), 1.5);
    public static readonly StyledProperty<bool> FilledProperty = AvaloniaProperty.Register<Icon, bool>(nameof(Filled));

    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public bool Filled { get => GetValue(FilledProperty); set => SetValue(FilledProperty, value); }

    readonly Avalonia.Controls.Shapes.Path _path = new() { StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
    readonly Viewbox _box;

    public Icon()
    {
        // A 16×16 canvas keeps every icon's stroke at the same visual weight.
        var canvas = new Canvas { Width = 16, Height = 16, Children = { _path } };
        _box = new Viewbox { Child = canvas, Width = 14, Height = 14 };
        Content = _box;
        IsHitTestVisible = false;
        Update();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == ForegroundProperty || change.Property == SizeProperty
            || change.Property == ThicknessProperty || change.Property == FilledProperty)
            Update();
    }

    void Update()
    {
        _path.Data = Paths.TryGetValue(Kind ?? "", out var d) ? Geometry.Parse(d) : null;
        _path.Stroke = Foreground;
        _path.StrokeThickness = Thickness;
        _path.Fill = Filled ? Foreground : null;
        _box.Width = _box.Height = Size;
    }
}
