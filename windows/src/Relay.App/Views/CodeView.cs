using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Search;
using Relay.Core;

namespace Relay.App.Views;

public enum CodeLanguage { Plain, Json, Xml }

/// <summary>
/// The code editor for request bodies and responses: line numbers, syntax
/// colours, {{variable}} colours and completion, folding for read-only JSON
/// (click a {…} to open it, like Postman), and Ctrl+F search. AvaloniaEdit
/// does the layout the Mac app's NSTextView did.
/// </summary>
public class CodeView : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<CodeView, string>(nameof(Text), "", defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<CodeLanguage> LanguageProperty = AvaloniaProperty.Register<CodeView, CodeLanguage>(nameof(Language));
    public static readonly StyledProperty<bool> IsReadOnlyProperty = AvaloniaProperty.Register<CodeView, bool>(nameof(IsReadOnly));
    public static readonly StyledProperty<bool> WrapsProperty = AvaloniaProperty.Register<CodeView, bool>(nameof(Wraps), true);
    public static readonly StyledProperty<bool> FoldsProperty = AvaloniaProperty.Register<CodeView, bool>(nameof(Folds));
    public static readonly StyledProperty<bool> HighlightVariablesProperty = AvaloniaProperty.Register<CodeView, bool>(nameof(HighlightVariables), true);

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public CodeLanguage Language { get => GetValue(LanguageProperty); set => SetValue(LanguageProperty, value); }
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public bool Wraps { get => GetValue(WrapsProperty); set => SetValue(WrapsProperty, value); }
    public bool Folds { get => GetValue(FoldsProperty); set => SetValue(FoldsProperty, value); }
    public bool HighlightVariables { get => GetValue(HighlightVariablesProperty); set => SetValue(HighlightVariablesProperty, value); }

    /// <summary>Where {{variables}} get their colour and completions; null shows them in one colour.</summary>
    public Func<VariableScope>? Scope { get; set; }

    readonly TextEditor _editor;
    readonly Colorizer _colorizer;
    FoldingManager? _folding;
    CompletionWindow? _completion;
    bool _updating;

    public CodeView()
    {
        _editor = new TextEditor
        {
            ShowLineNumbers = true,
            FontFamily = (FontFamily)Application.Current!.FindResource("CodeFont")!,
            FontSize = 12.5,
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 8, 8, 8),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            WordWrap = true,
        };
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.IndentationSize = 2;
        _editor.Options.HighlightCurrentLine = false;
        _editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x9B, 0xC2, 0x1F));
        _editor.TextArea.SelectionForeground = null;
        _editor.TextArea.SelectionCornerRadius = 0;
        _editor.LineNumbersMargin = new Thickness(4, 0, 10, 0);
        SearchPanel.Install(_editor);

        _colorizer = new Colorizer(this);
        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        _editor.TextChanged += OnEditorTextChanged;
        _editor.TextArea.TextEntered += OnTextEntered;
        // Tunnel, so a click on a folded {…} expands it before the editor moves the caret.
        _editor.TextArea.AddHandler(PointerPressedEvent, OnPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Content = _editor;
        ApplyTheme();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            if (_editor.Text != Text) SetText(Text ?? "");
        }
        else if (change.Property == IsReadOnlyProperty)
        {
            _editor.IsReadOnly = IsReadOnly;
        }
        else if (change.Property == WrapsProperty)
        {
            _editor.WordWrap = Wraps;
        }
        else if (change.Property == LanguageProperty || change.Property == HighlightVariablesProperty)
        {
            _colorizer.Invalidate();
            _editor.TextArea.TextView.Redraw();
        }
        else if (change.Property == FoldsProperty)
        {
            UpdateFolding();
        }
        else if (change.Property.Name == nameof(ActualThemeVariant))
        {
            ApplyTheme();
        }
    }

    void ApplyTheme()
    {
        _editor.LineNumbersForeground = Palette.LineNumber;
        _editor.Foreground = Palette.Text;
        _editor.TextArea.CaretBrush = Palette.Text;
        _colorizer?.Invalidate();
        _editor.TextArea.TextView.Redraw();
    }

    void SetText(string text)
    {
        _updating = true;
        _editor.Document.Text = text;
        _updating = false;
        _colorizer.Invalidate();
        if (IsReadOnly) _editor.ScrollToHome();
        UpdateFolding();
    }

    void OnEditorTextChanged(object? sender, EventArgs e)
    {
        _colorizer.Invalidate();
        if (_updating) return;
        Text = _editor.Text;
    }

    /// <summary>Folding for read-only JSON: one region per multi-line {…}/[…], shown folded as {…} like the Mac app.</summary>
    void UpdateFolding()
    {
        if (!Folds)
        {
            if (_folding != null)
            {
                var margins = _editor.TextArea.LeftMargins;
                foreach (var m in margins.OfType<FoldChevrons>().ToList()) margins.Remove(m);
                FoldingManager.Uninstall(_folding);
                _folding = null;
            }
            return;
        }
        if (_folding == null)
        {
            _folding = FoldingManager.Install(_editor.TextArea);
            var margins = _editor.TextArea.LeftMargins;
            var index = margins.IndexOf(margins.OfType<FoldingMargin>().First());
            margins.RemoveAt(index);
            margins.Insert(index, new FoldChevrons(_folding));
        }
        var text = _editor.Text;
        var regions = JsonFolding.Regions(text.AsSpan())
            .Select(r => new NewFolding(r.Open + 1, r.Close) { Name = "…" })
            .OrderBy(f => f.StartOffset);
        _folding.UpdateFoldings(regions, -1);
    }

    void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_folding == null || !e.GetCurrentPoint(_editor.TextArea).Properties.IsLeftButtonPressed) return;
        var view = _editor.TextArea.TextView;
        var point = e.GetPosition(view) + view.ScrollOffset;
        if (view.GetPositionFloor(point) is not { } position) return;
        var offset = _editor.Document.GetOffset(position.Location);
        // A folded section collapses to its first offset; the click lands just before or on it.
        var section = _folding.AllFoldings.FirstOrDefault(f => f.IsFolded && offset >= f.StartOffset - 1 && offset <= f.StartOffset + 1);
        if (section == null) return;
        section.IsFolded = false;
        e.Handled = true;
    }

    // {{ completion: suggest variables while a name is being typed.
    void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (IsReadOnly || !HighlightVariables || Scope == null) return;
        var caret = _editor.CaretOffset;
        if (CompletionContext.Find(_editor.Text, caret) is not { } context)
        {
            _completion?.Close();
            return;
        }
        var suggestions = Scope().Suggestions(context.Prefix);
        if (suggestions.Count == 0)
        {
            _completion?.Close();
            return;
        }
        _completion?.Close();
        _completion = new CompletionWindow(_editor.TextArea) { StartOffset = context.Start, CloseWhenCaretAtBeginning = true };
        foreach (var s in suggestions) _completion.CompletionList.CompletionData.Add(new Suggestion(s));
        _completion.Closed += (_, _) => _completion = null;
        _completion.Show();
    }

    sealed class Suggestion(VariableSuggestion s) : ICompletionData
    {
        public IImage? Image => null;
        public string Text => s.Name;
        public object Content => s.Name;
        public object? Description => s.IsSecret ? "••••••" : s.Value;
        public double Priority => 0;

        public void Complete(AvaloniaEdit.Editing.TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            var text = textArea.Document.Text;
            if (CompletionContext.Find(text, textArea.Caret.Offset) is not { } context) return;
            var (start, length, closed) = context.Replacement(text);
            textArea.Document.Replace(start, length, closed ? s.Name : s.Name + "}}");
        }
    }

    /// <summary>
    /// Colours JSON/XML tokens and {{variables}}. Token spans are worked out once
    /// per edit for the whole document and reused for every line drawn.
    /// </summary>
    sealed class Colorizer(CodeView owner) : DocumentColorizingTransformer
    {
        /// <summary>Past this, colouring costs more than it helps; the text stays plain.</summary>
        const int SyntaxLimit = 2_000_000;
        List<(int Start, int Length, IBrush Brush)>? _spans;
        List<(int Start, int Length, string Name)>? _variables;

        public void Invalidate()
        {
            _spans = null;
            _variables = null;
        }

        void Build(string text)
        {
            _spans = [];
            if (text.Length <= SyntaxLimit)
            {
                if (owner.Language == CodeLanguage.Json)
                {
                    foreach (var (start, length, token) in JsonFormat.HighlightRanges(text))
                    {
                        var brush = token switch
                        {
                            JsonFormat.Token.Key => Palette.Key,
                            JsonFormat.Token.String => Palette.String,
                            JsonFormat.Token.Number => Palette.Number,
                            JsonFormat.Token.Literal => Palette.Literal,
                            _ => Palette.Punctuation,
                        };
                        _spans.Add((start, length, brush));
                    }
                }
                else if (owner.Language == CodeLanguage.Xml)
                {
                    foreach (var (start, length, token) in XmlFormat.HighlightRanges(text))
                    {
                        var brush = token switch
                        {
                            XmlFormat.Token.Tag => Palette.Literal,
                            XmlFormat.Token.Attribute => Palette.Key,
                            XmlFormat.Token.Value => Palette.String,
                            _ => Palette.Comment,
                        };
                        _spans.Add((start, length, brush));
                    }
                }
            }
            _variables = owner.HighlightVariables ? Variables.Ranges(text) : [];
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (_spans == null) Build(CurrentContext.Document.Text);
            int lineStart = line.Offset, lineEnd = line.EndOffset;
            // Spans are sorted by start: binary search to the first that could touch this line.
            var spans = _spans!;
            int lo = 0, hi = spans.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (spans[mid].Start + spans[mid].Length <= lineStart) lo = mid + 1; else hi = mid;
            }
            for (var i = lo; i < spans.Count && spans[i].Start < lineEnd; i++)
            {
                var (start, length, brush) = spans[i];
                var s = Math.Max(start, lineStart);
                var e = Math.Min(start + length, lineEnd);
                if (e > s) ChangeLinePart(s, e, el => el.TextRunProperties.SetForegroundBrush(brush));
            }
            if (_variables!.Count == 0) return;
            var scope = owner.Scope?.Invoke();
            foreach (var (start, length, name) in _variables)
            {
                var s = Math.Max(start, lineStart);
                var e = Math.Min(start + length, lineEnd);
                if (e <= s) continue;
                var brush = scope == null ? Palette.Amber : Palette.Variable(scope.SourceOf(name));
                var background = Palette.Tint(brush, 0.14);
                ChangeLinePart(s, e, el =>
                {
                    el.TextRunProperties.SetForegroundBrush(brush);
                    el.TextRunProperties.SetBackgroundBrush(background);
                });
            }
        }
    }

    /// <summary>Re-colours variables after the environment changed (their colour shows where they're defined).</summary>
    public void Refresh()
    {
        _colorizer.Invalidate();
        _editor.TextArea.TextView.Redraw();
    }

    public void Focus(int? caret = null)
    {
        _editor.TextArea.Focus();
        if (caret is { } c) _editor.CaretOffset = Math.Min(c, _editor.Document.TextLength);
    }
}
