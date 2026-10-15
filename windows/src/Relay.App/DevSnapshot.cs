#if DEBUG
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Relay.App;

/// <summary>
/// Debug builds only: with RELAY_SNAPSHOT=dir set, saves the window as
/// dir/dark.png and dir/light.png, then quits. How the UI is checked from a
/// terminal, since screen capture needs a permission a build script doesn't have.
/// </summary>
static class DevSnapshot
{
    public static void Attach(Window window)
    {
        var dir = Environment.GetEnvironmentVariable("RELAY_SNAPSHOT");
        if (dir == null) return;
        var script = Environment.GetEnvironmentVariable("RELAY_SNAPSHOT_SETUP");
        window.Opened += async (_, _) =>
        {
            await Task.Delay(1500);
            if (script != null) await DevSetup.Run(script);
            foreach (var (name, variant) in new[] { ("dark", Avalonia.Styling.ThemeVariant.Dark), ("light", Avalonia.Styling.ThemeVariant.Light) })
            {
                Application.Current!.RequestedThemeVariant = variant;
                await Task.Delay(700);
                var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
                using var bitmap = new RenderTargetBitmap(new PixelSize(size.Width * 2, size.Height * 2), new Vector(192, 192));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(dir, $"{name}.png"));
                if (DevSetup.Sheet is { } sheet)
                {
                    var s = new PixelSize((int)sheet.Bounds.Width * 2, (int)sheet.Bounds.Height * 2);
                    using var sheetBitmap = new RenderTargetBitmap(s, new Vector(192, 192));
                    sheetBitmap.Render(sheet);
                    sheetBitmap.Save(Path.Combine(dir, $"sheet-{name}.png"));
                }
            }
            Dispatcher.UIThread.Post(() => { DevSetup.Sheet?.Close(); window.Close(); });
        };
    }
}
#endif
