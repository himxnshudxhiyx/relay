using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Relay.App.Views;

namespace Relay.App;

/// <summary>Alerts, file pickers, the clipboard and the sheets, all modal over the main window.</summary>
static class Dialogs
{
    public static Window? Owner => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public enum SaveChoice { Save, Discard, Cancel }

    public static async Task<bool> Confirm(string title, string message, string action) =>
        await Alert(title, message, (action, true, true), ("Cancel", false, false)) == 0;

    public static Task Inform(string title, string message) => Alert(title, message, ("OK", false, false));

    public static async Task<SaveChoice> SaveChanges(string name) =>
        await Alert($"Save changes to “{name}”?", "Your edits to this request will be lost if you don't save them.",
                ("Save", false, false), ("Don't Save", true, false), ("Cancel", false, false)) switch
        {
            0 => SaveChoice.Save,
            1 => SaveChoice.Discard,
            _ => SaveChoice.Cancel,
        };

    /// <summary>A small alert; returns the index of the button pressed (Escape picks the last).</summary>
    static async Task<int> Alert(string title, string message, params (string Title, bool Destructive, bool Primary)[] buttons)
    {
        var result = buttons.Length - 1;
        var window = Sheet(title, 420);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = buttons.Length - 1; i >= 0; i--)
        {
            var index = i;
            var (text, destructive, primary) = buttons[i];
            var button = new Button { Content = text, MinWidth = 84 };
            button.Classes.Add(primary || index == 0 ? "primary" : "secondary");
            if (destructive) button.Foreground = Palette.Red;
            button.Click += (_, _) => { result = index; window.Close(); };
            row.Children.Insert(0, button);
        }
        window.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Classes = { "secondary" } },
                row,
            },
        };
        await ShowModal(window);
        return result;
    }

    /// <summary>A borderless-looking modal window in the app's surfaces.</summary>
    public static Window Sheet(string title, double width, double? height = null)
    {
        var window = new Window
        {
            Title = title,
            Width = width,
            CanResize = height != null,
            CanMinimize = false,
            CanMaximize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush?)Application.Current!.FindResource(Application.Current!.ActualThemeVariant, "Canvas"),
        };
        if (height is { } h) window.Height = h; else window.SizeToContent = SizeToContent.Height;
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) window.Close();
        };
        return window;
    }

    public static Task ShowModal(Window window) => Owner is { } owner ? window.ShowDialog(owner) : Task.CompletedTask;

    // Files

    public static async Task<bool> SaveFile(byte[] data, string suggestedName)
    {
        if (Owner?.StorageProvider is not { } storage) return false;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName.Replace('/', '-').Replace(':', '-'),
            ShowOverwritePrompt = true,
        });
        if (file == null) return false;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await stream.WriteAsync(data);
            return true;
        }
        catch (Exception e)
        {
            await Inform("Couldn't save the file", e.Message);
            return false;
        }
    }

    public static async Task<(string Name, byte[] Data)?> OpenFile(string title = "Open")
    {
        if (Owner?.StorageProvider is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        if (files.Count == 0) return null;
        await using var stream = await files[0].OpenReadAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return (files[0].Name, memory.ToArray());
    }

    /// <summary>A path for a body file or multipart upload.</summary>
    public static async Task<string?> PickFilePath()
    {
        if (Owner?.StorageProvider is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    // Clipboard

    public static async Task CopyText(string text)
    {
        if (Owner?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    public static async Task<string?> PasteText() =>
        Owner?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;

    // Sheets

    public static async Task<bool> SaveRequest(TabState tab)
    {
        var sheet = new SaveRequestSheet(tab);
        await ShowModal(sheet);
        return sheet.Saved;
    }

    public static Task Environments(Guid? selected) => ShowModal(new EnvironmentsSheet(selected));
    public static Task CollectionSettings(Guid collection) => ShowModal(new SettingsSheet(collection, null));
    public static Task FolderSettings(Guid collection, Guid folder) => ShowModal(new SettingsSheet(collection, folder));
    public static Task Import(bool paste) => ShowModal(new ImportSheet(paste));
    public static Task AppSettings() => ShowModal(new AppSettingsSheet());
}
