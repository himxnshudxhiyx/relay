using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Relay.Core;

namespace Relay.App.Views;

/// <summary>The response: status, timing, body (Pretty / Raw / Preview), headers and what was sent. Ported from ResponseView.swift.</summary>
public class ResponsePanel : UserControl
{
    static Workspace Ws => Workspace.Shared;
    static Settings Prefs => Settings.Shared;

    readonly TabState _tab;
    bool _previewing;

    public ResponsePanel(TabState tab)
    {
        _tab = tab;
        this.Bind(BackgroundProperty, "Canvas");
#if DEBUG
        _previewing = DevSetup.Preview;
#endif
        tab.PropertyChanged += OnTabChanged;
        DetachedFromVisualTree += (_, _) => tab.PropertyChanged -= OnTabChanged;
        // Cross-fade between empty, loading, error and the response.
        Transitions = [new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }];
        Build();
    }

    void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabState.Response) or nameof(TabState.ResponseSection))
        {
#if DEBUG
            _previewing |= DevSetup.Preview;
#endif
            Opacity = 0.4;
            Build();
            Opacity = 1;
        }
    }

    void Build()
    {
        Content = _tab.Response switch
        {
            ResponseState.Loading loading => Loading(loading.Started),
            ResponseState.Failed failed => UI.EmptyState("warning", "Couldn't get a response", failed.Message,
                UI.Button("Try Again", "secondary", () => Ws.Send(_tab)).With(b => b.Height = 28)),
            ResponseState.Done done => Result(done.Result),
            _ => UI.EmptyState("send", "No response yet", "Send the request and the response shows up here.",
                UI.Row(4, UI.KeyHint("Ctrl"), UI.KeyHint("Enter"))),
        };
    }

    Control Loading(DateTime started)
    {
        var elapsed = UI.Code("", 12).With(t => t.Classes.Add("secondary"));
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => elapsed.Text = Format.Duration((DateTime.UtcNow - started).TotalSeconds);
        timer.Start();
        var stack = UI.Column(12,
            new ProgressBar { IsIndeterminate = true, Width = 120 },
            elapsed.With(t => t.HorizontalAlignment = HorizontalAlignment.Center),
            UI.Button("Cancel", "secondary", () => Ws.Cancel(_tab)).With(b => { b.Height = 28; b.HorizontalAlignment = HorizontalAlignment.Center; }));
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        stack.VerticalAlignment = VerticalAlignment.Center;
        stack.DetachedFromVisualTree += (_, _) => timer.Stop();
        return stack;
    }

    Control Result(HttpResult result)
    {
        var tabs = UI.UnderlineTabs(
        [
            (ResponseSection.Body, "Response", null),
            (ResponseSection.Headers, "Headers", $"{result.Headers.Count}"),
            (ResponseSection.Sent, "Sent", (string?)null),
        ], _tab.ResponseSection, s => _tab.ResponseSection = s);

        var summary = UI.Row(12,
            UI.StatusPill(result.Status),
            UI.Code(Format.Duration(result.Duration), 12).With(t =>
            {
                t.Classes.Add("secondary");
                ToolTip.SetTip(t, result.Ttfb is { } ttfb ? $"Time to first byte: {Format.Duration(ttfb)}" : "Total time");
            }),
            UI.Code(Format.Bytes(result.Body.Length), 12).With(t => { t.Classes.Add("secondary"); ToolTip.SetTip(t, "Response body size"); }),
            new Border { Width = 1, Height = 16 }.Bind(Border.BackgroundProperty, "Line"),
            UI.Button("Copy with cURL", "link", () => _ = Ws.Copy(Format.CurlWithResponse(result.Curl, result, includeHeaders: false), "cURL and response"),
                "Copy the cURL that was sent together with this response (Ctrl+Alt+C)"),
            CopyMenu(result));

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 0) };
        header.Children.Add(tabs);
        summary.Margin = new Thickness(0, 0, 0, 7);
        summary.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(summary, 1);
        header.Children.Add(summary);

        Control body = _tab.ResponseSection switch
        {
            ResponseSection.Headers => new ScrollViewer { Content = HeaderRows(result.Headers) },
            ResponseSection.Sent => Sent(result),
            _ => BodyView(result),
        };
        return new DockPanel
        {
            Children =
            {
                new StackPanel { Children = { header.Bind(BackgroundProperty, "Chrome"), UI.Hairline() }, [DockPanel.DockProperty] = Dock.Top },
                body,
            },
        };
    }

    Control CopyMenu(HttpResult result)
    {
        var button = UI.Button(UI.Row(4, UI.Text("Copy", 12.5, FontWeight.Medium), new Icon { Kind = "chevron-down", Size = 9 }), "ghost");
        button.Height = 26;
        ToolTip.SetTip(button, "More copy options: body, headers, cURL, cURL + response with headers");
        button.Flyout = new MenuFlyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            Items =
            {
                new MenuItem { Header = "Response Body", Command = new AsyncRelayCommand(() => Ws.Copy(result.Pretty ?? result.Text, "response body")) },
                new MenuItem { Header = "Response Headers", Command = new AsyncRelayCommand(() => Ws.Copy(string.Join("\n", result.Headers.Select(h => $"{h.Name}: {h.Value}")), "headers")) },
                new Separator(),
                new MenuItem { Header = "cURL + Response", Command = new AsyncRelayCommand(() => Ws.Copy(Format.CurlWithResponse(result.Curl, result, false), "cURL and response")) },
                new MenuItem { Header = "cURL + Response with Headers", Command = new AsyncRelayCommand(() => Ws.Copy(Format.CurlWithResponse(result.Curl, result, true), "cURL and response")) },
                new Separator(),
                new MenuItem { Header = "cURL That Was Sent", Command = new AsyncRelayCommand(() => Ws.Copy(result.Curl, "cURL")) },
            },
        };
        return button;
    }

    enum Mode { Pretty, Raw, Preview }

    Control BodyView(HttpResult result)
    {
        switch (result.Kind)
        {
            case BodyKind.Empty:
                return UI.EmptyState("file", "Empty body", "The server sent no content.");
            case BodyKind.Image:
                try
                {
                    using var stream = new MemoryStream(result.Body);
                    return new ScrollViewer { Content = new Image { Source = new Bitmap(stream), Stretch = Stretch.None, Margin = new Thickness(16) }, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
                }
                catch (Exception)
                {
                    return Binary(result);
                }
            case BodyKind.Binary:
                return Binary(result);
        }

        var mode = _previewing && result.Files.Count > 0 ? Mode.Preview : Prefs.PrettyResponse ? Mode.Pretty : Mode.Raw;
        var modes = new List<(Mode, string)>();
        if (result.Pretty != null) modes.AddRange([(Mode.Pretty, "Pretty"), (Mode.Raw, "Raw")]);
        if (result.Files.Count > 0) modes.Add((Mode.Preview, "Preview"));

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 8, Height = 42, Margin = new Thickness(20, 0, 12, 0) };
        if (modes.Count > 1)
            bar.Children.Add(UI.Pills(modes, mode, m =>
            {
                _previewing = m == Mode.Preview;
                if (m != Mode.Preview) Prefs.PrettyResponse = m == Mode.Pretty;
                Build();
            }).With(p => p.VerticalAlignment = VerticalAlignment.Center));
        bar.Children.Add(UI.Text(KindLabel(result), 11, default, "secondary").With(t => Grid.SetColumn(t, 1)));
        if (mode != Mode.Preview)
        {
            var tools = UI.Row(4,
                UI.Text("Ctrl+F to search", 10.5, default, "tertiary"),
                UI.IconButton(Prefs.WrapResponse ? "wrap" : "no-wrap", Prefs.WrapResponse ? "Turn off line wrap" : "Wrap long lines", () => { Prefs.WrapResponse = !Prefs.WrapResponse; Build(); }),
                UI.IconButton("download", "Save response to a file", () => _ = SaveBody(result, mode == Mode.Pretty)));
            Grid.SetColumn(tools, 3);
            bar.Children.Add(tools);
        }

        Control content = mode == Mode.Preview
            ? new FilePreview(result.Files, FileName())
            : new CodeView
            {
                IsReadOnly = true,
                HighlightVariables = false,
                Language = result.Kind == BodyKind.Json ? CodeLanguage.Json : result.Kind is BodyKind.Xml or BodyKind.Html ? CodeLanguage.Xml : CodeLanguage.Plain,
                Folds = result.Kind == BodyKind.Json,
                Wraps = Prefs.WrapResponse,
                Text = mode == Mode.Pretty ? result.Pretty ?? result.Text : result.Text,
            };
        return new DockPanel { Children = { bar.With(b => DockPanel.SetDock(b, Dock.Top)), content } };
    }

    Control Binary(HttpResult result) => UI.EmptyState("file", "Binary response",
        $"{Format.Bytes(result.Body.Length)} of {(result.ContentType.Length == 0 ? "binary data" : result.ContentType)}.",
        UI.Button("Save to File…", "secondary", () => _ = SaveBody(result, false)).With(b => b.Height = 28));

    static string KindLabel(HttpResult r) => r.Kind switch
    {
        BodyKind.Json => "JSON",
        BodyKind.Xml => "XML",
        BodyKind.Html => "HTML",
        _ => r.ContentType.Split(';')[0].Trim() is { Length: > 0 } t ? t : "Text",
    };

    string FileName() => Format.ResponseFileName(_tab.Draft.Name, _tab.Draft.Url);

    /// <summary>Saved as shown: formatted while Pretty is on, the server's bytes on Raw.</summary>
    async Task SaveBody(HttpResult result, bool pretty)
    {
        var data = pretty && result.Pretty != null ? System.Text.Encoding.UTF8.GetBytes(result.Pretty) : result.Body;
        await Dialogs.SaveFile(data, $"{FileName()}.{Extension(result)}");
    }

    static string Extension(HttpResult r) => r.Kind switch
    {
        BodyKind.Json => "json",
        BodyKind.Xml => "xml",
        BodyKind.Html => "html",
        BodyKind.Text => "txt",
        _ => r.ContentType.Split(';')[0].Trim().Split('/') is [_, var sub] && sub.Length <= 5 ? sub : "bin",
    };

    internal static Control HeaderRows(List<(string Name, string Value)> headers)
    {
        var list = new StackPanel();
        foreach (var (name, value) in headers)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), ColumnSpacing = 14, Margin = new Thickness(20, 7) };
            grid.Children.Add(new SelectableTextBlock { Text = name, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Classes = { "code" } }.Bind(SelectableTextBlock.ForegroundProperty, "TextSecondary"));
            grid.Children.Add(new SelectableTextBlock { Text = value, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, [Grid.ColumnProperty] = 1, Classes = { "code" } });
            grid.ContextMenu = new ContextMenu
            {
                Items =
                {
                    new MenuItem { Header = "Copy Value", Command = new AsyncRelayCommand(() => Dialogs.CopyText(value)) },
                    new MenuItem { Header = "Copy Header", Command = new AsyncRelayCommand(() => Dialogs.CopyText($"{name}: {value}")) },
                },
            };
            list.Children.Add(grid);
            list.Children.Add(UI.Hairline().With(h => h.Margin = new Thickness(20, 0, 0, 0)));
        }
        return list;
    }

    static Control Sent(HttpResult result)
    {
        var stack = new StackPanel { Spacing = 14, Margin = new Thickness(20, 16) };
        stack.Children.Add(UI.Row(8, UI.MethodBadge(result.SentMethod, 12), new SelectableTextBlock { Text = result.SentUrl, FontSize = 12, Classes = { "code" }, TextWrapping = TextWrapping.Wrap }));
        if (result.Redirects > 0)
            stack.Children.Add(UI.Text($"Followed {result.Redirects} redirect{(result.Redirects == 1 ? "" : "s")} to {result.FinalUrl}", 11.5, default, "secondary"));
        stack.Children.Add(UI.Text("HEADERS", 10.5, FontWeight.SemiBold, "tertiary"));
        stack.Children.Add(HeaderRows(result.SentHeaders).With(c => c.Margin = new Thickness(-20, 0)));
        if (result.SentBody.Length > 0)
        {
            stack.Children.Add(UI.Text("BODY", 10.5, FontWeight.SemiBold, "tertiary"));
            stack.Children.Add(new Border
            {
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(8),
                Child = new SelectableTextBlock
                {
                    Text = result.SentBody.Length > 20_000 ? result.SentBody[..20_000] + "\n…" : result.SentBody,
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Classes = { "code" },
                },
            }.Bind(Border.BackgroundProperty, "Field"));
        }
        stack.Children.Add(UI.Text("Windows may also add Accept-Encoding and Connection when sending.", 11, default, "tertiary"));
        return new ScrollViewer { Content = stack };
    }
}

/// <summary>Base64 files found in a JSON response, decoded and shown: PDFs page by page, images as images.</summary>
class FilePreview : DockPanel
{
    readonly List<EmbeddedFile> _files;
    readonly string _fallbackName;
    int _index;

    public FilePreview(List<EmbeddedFile> files, string fallbackName)
    {
        _files = files;
        _fallbackName = fallbackName;
        Build();
    }

    EmbeddedFile File => _files[Math.Min(_index, _files.Count - 1)];

    void Build()
    {
        Children.Clear();
        var file = File;
        Control which = _files.Count > 1
            ? UI.Picker(_files.Select((f, i) => (i, f.Path)), _index, i => { _index = i; Build(); })
            : UI.Code(file.Path, 12).With(t => t.Classes.Add("secondary"));
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), ColumnSpacing = 10, Margin = new Thickness(20, 0, 20, 8) };
        bar.Children.Add(which);
        bar.Children.Add(UI.Text($"{file.Kind.ToString().ToUpperInvariant()} · {Format.Bytes(file.Data.Length)}", 11.5, default, "tertiary").With(t => Grid.SetColumn(t, 1)));
        var open = UI.Button("Open", "secondary", () => Open(file), "Open in the default app");
        open.Height = 26;
        Grid.SetColumn(open, 3);
        bar.Children.Add(open);
        var save = UI.Button("Save…", "secondary", () => _ = Dialogs.SaveFile(file.Data, $"{FileName(file)}.{file.FileExtension}"));
        save.Height = 26;
        Grid.SetColumn(save, 4);
        bar.Children.Add(save);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        var line = UI.Hairline();
        SetDock(line, Dock.Top);
        Children.Add(line);

        if (file.IsImage)
        {
            try
            {
                using var stream = new MemoryStream(file.Data);
                Children.Add(new ScrollViewer { Content = new Image { Source = new Bitmap(stream), Stretch = Stretch.None, Margin = new Thickness(16) }, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
                return;
            }
            catch (Exception) { }
        }
        else if (file.Kind == EmbeddedFileKind.Pdf && Pdf.Render(file.Data, (Dialogs.Owner?.RenderScaling ?? 1) * 1.5) is { Count: > 0 } pages)
        {
            var list = new StackPanel { Spacing = 12, Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var page in pages)
                list.Children.Add(new Border
                {
                    BoxShadow = BoxShadows.Parse("0 2 10 0 #33000000"),
                    Child = new Image { Source = page, Stretch = Stretch.Uniform, MaxWidth = page.Size.Width / ((Dialogs.Owner?.RenderScaling ?? 1) * 1.5) },
                });
            Children.Add(new ScrollViewer { Content = list });
            return;
        }
        Children.Add(UI.EmptyState("file", "Can't show this file", $"It looks like a {file.Kind.ToString().ToUpperInvariant()} but didn't decode. Save it to inspect it."));
    }

    string FileName(EmbeddedFile file)
    {
        var name = file.SuggestedName ?? _fallbackName;
        // Drop an extension the response already put on the name.
        return name.EndsWith("." + file.FileExtension, StringComparison.OrdinalIgnoreCase) ? name[..^(file.FileExtension.Length + 1)] : name;
    }

    void Open(EmbeddedFile file)
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "Relay");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{FileName(file).Replace('/', '-').Replace(':', '-')}.{file.FileExtension}");
            System.IO.File.WriteAllBytes(path, file.Data);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            _ = Dialogs.Inform("Couldn't open the file", e.Message);
        }
    }
}
