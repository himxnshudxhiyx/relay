using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;

namespace Relay.App.Views;

/// <summary>
/// Fold chevrons beside the line numbers, as in the Mac app: ⌄ on a line that
/// opens a block, › (lime) when it's folded. Replaces AvaloniaEdit's boxed
/// +/− margin and its guide lines.
/// </summary>
sealed class FoldChevrons(FoldingManager manager) : AbstractMargin
{
    static readonly Geometry Down = Geometry.Parse("M 0,0 L 3.5,3.5 L 7,0");
    static readonly Geometry Right = Geometry.Parse("M 0,0 L 3.5,3.5 L 0,7");

    protected override Size MeasureOverride(Size availableSize) => new(16, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView != null) oldTextView.VisualLinesChanged -= Redraw;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView != null) newTextView.VisualLinesChanged += Redraw;
    }

    void Redraw(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>The block opening on this visual line, if any.</summary>
    FoldingSection? SectionOn(VisualLine line)
    {
        var first = line.FirstDocumentLine;
        var section = manager.GetNextFolding(first.Offset);
        return section != null && section.StartOffset <= first.EndOffset + 1 ? section : null;
    }

    public override void Render(DrawingContext context)
    {
        if (TextView is not { VisualLinesValid: true } view) return;
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var open = new Pen(Palette.Secondary, 1.3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var folded = new Pen(Palette.Accent, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (var line in view.VisualLines)
        {
            if (SectionOn(line) is not { } section) continue;
            var middle = line.VisualTop - view.VerticalOffset + line.Height / 2;
            var shape = section.IsFolded ? Right : Down;
            var size = shape.Bounds.Size;
            using (context.PushTransform(Matrix.CreateTranslation((Bounds.Width - size.Width) / 2 - 2, middle - size.Height / 2)))
                context.DrawGeometry(null, section.IsFolded ? folded : open, shape);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (TextView is not { VisualLinesValid: true } view) return;
        var y = e.GetPosition(this).Y + view.VerticalOffset;
        var line = view.GetVisualLineFromVisualTop(y);
        if (line == null || SectionOn(line) is not { } section) return;
        section.IsFolded = !section.IsFolded;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var view = TextView;
        var line = view is { VisualLinesValid: true } ? view.GetVisualLineFromVisualTop(e.GetPosition(this).Y + view.VerticalOffset) : null;
        Cursor = line != null && SectionOn(line) != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }
}
