#if DEBUG
using Avalonia.Controls;
using Avalonia.VisualTree;
using Relay.Core;

namespace Relay.App;

/// <summary>
/// Debug builds only: puts the app into a state for DevSnapshot (RELAY_SNAPSHOT_SETUP),
/// so screenshots show a response without sending anything over the network.
/// </summary>
static class DevSetup
{
    /// <summary>A sheet opened by "sheet=…", snapshotted alongside the window.</summary>
    public static Avalonia.Controls.Window? Sheet { get; private set; }

    /// <summary>Opens responses in Preview, for checking the PDF view.</summary>
    public static bool Preview { get; private set; }

    public static async Task Run(string script)
    {
        var ws = Workspace.Shared;
        foreach (var step in script.Split(';'))
        {
            switch (step.Trim())
            {
                case var s when s.StartsWith("url=") && ws.SelectedTab is { } t:
                    t.Draft.Url = s[4..];
                    break;
                case var s when s.StartsWith("method=") && ws.SelectedTab is { } t:
                    t.Draft.Method = s[7..];
                    break;
                case var s when s.StartsWith("body=") && ws.SelectedTab is { } t:
                    t.Draft.Body.Mode = BodyMode.Json;
                    t.Draft.Body.Raw = s[5..];
                    break;
                case "new":
                    ws.NewTab();
                    break;
                case "send" when ws.SelectedTab is { } t:
                    ws.Send(t);
                    for (var i = 0; i < 100 && t.IsLoading; i++) await Task.Delay(100);
                    break;
                case "response" when ws.SelectedTab is { } tab:
                    const string json = """{"data":{"isGstApplicable":true,"igstApplicable":false,"itemGstDetails":[{"serialNo":1,"itemAmount":"50000","gstPercent":28},{"serialNo":2,"itemAmount":"1200.50","gstPercent":18}],"total":51200.5,"note":null},"status":200}""";
                    tab.Response = new ResponseState.Done(new HttpResult
                    {
                        Status = 200,
                        Headers = [("Content-Type", "application/json"), ("Content-Length", $"{json.Length}")],
                        Body = System.Text.Encoding.UTF8.GetBytes(json),
                        Duration = 0.123,
                        Ttfb = 0.1,
                        Kind = BodyKind.Json,
                        Text = json,
                        Pretty = JsonFormat.Pretty(json),
                        SentMethod = tab.Draft.Method,
                        SentUrl = tab.Draft.Url,
                        SentHeaders = [],
                        SentBody = "",
                        FinalUrl = tab.Draft.Url,
                    });
                    break;
                case var s when s.StartsWith("sheet="):
                    Window sheet = s[6..] switch
                    {
                        "import" => new Views.ImportSheet(paste: true),
                        "environments" => new Views.EnvironmentsSheet(ws.ActiveEnvironmentID),
                        "settings" => new Views.AppSettingsSheet(),
                        "collection" => new Views.SettingsSheet(ws.Collections[0].Id, null),
                        _ => new Views.SaveRequestSheet(ws.SelectedTab!),
                    };
                    Sheet = sheet;
                    sheet.Show(Dialogs.Owner!);
                    break;
                case "pdf" when ws.SelectedTab is { } pdfTab:
                    var pdf = Convert.ToBase64String(File.ReadAllBytes("/System/Library/ProductDocuments/ProductGuides/ENERGY STAR.pdf"));
                    var body = $$"""{"data":{"fileName":"Himanshu_Pvt_Ltd_-GSTR1-B2B","pdf":"{{pdf}}"},"status":200}""";
                    Preview = true;
                    pdfTab.Response = new ResponseState.Done(new HttpResult
                    {
                        Status = 200, Headers = [("Content-Type", "application/json")], Body = System.Text.Encoding.UTF8.GetBytes(body),
                        Duration = 0.37, Kind = BodyKind.Json, Text = body, Pretty = JsonFormat.Pretty(body),
                        Files = EmbeddedFile.Find(System.Text.Encoding.UTF8.GetBytes(body)),
                        SentMethod = "POST", SentUrl = pdfTab.Draft.Url, SentHeaders = [], SentBody = "", FinalUrl = pdfTab.Draft.Url,
                    });
                    break;
                case var s when s.StartsWith("paste="):
                    // Clipboard → a real paste into the URL box, as Ctrl+V does.
                    await Dialogs.CopyText(File.ReadAllText(s[6..]));
                    TextBox? url = null;
                    for (var i = 0; i < 30 && url == null; i++)
                    {
                        await Task.Delay(100);
                        url = Dialogs.Owner!.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.PlaceholderText?.StartsWith("Enter a URL") == true);
                    }
                    if (url == null) break;
                    url.Focus();
                    url.Paste();
                    await Task.Delay(800);
                    break;
                case "welcome":
                    ws.SelectedTab = null;
                    break;
                case var s when s.StartsWith("sidebar="):
                    ws.SidebarMode = Enum.Parse<SidebarMode>(s[8..], true);
                    break;
                case var s when s.StartsWith("section="):
                    if (ws.SelectedTab is { } sectionTab) sectionTab.EditorSection = Enum.Parse<EditorSection>(s[8..], true);
                    break;
            }
        }
        await Task.Delay(400);
    }
}
#endif
