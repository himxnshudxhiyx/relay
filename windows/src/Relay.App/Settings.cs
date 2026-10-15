using CommunityToolkit.Mvvm.ComponentModel;
using Relay.Core;

namespace Relay.App;

public enum Appearance { System, Light, Dark }
public enum PaneLayout { Stacked, SideBySide }

/// <summary>Preferences (the Mac app's UserDefaults), saved to settings.json beside the workspace.</summary>
public partial class Settings : ObservableObject
{
    public static Settings Shared { get; } = Load();

    static string PathOnDisk => System.IO.Path.Combine(Storage.Folder, "settings.json");

    [ObservableProperty] private Appearance _appearance = Appearance.System;
    [ObservableProperty] private PaneLayout _layout = PaneLayout.Stacked;
    [ObservableProperty] private double _timeout = 30;
    [ObservableProperty] private bool _followRedirects = true;
    [ObservableProperty] private bool _verifySsl = true;
    [ObservableProperty] private bool _wrapResponse = true;
    [ObservableProperty] private bool _prettyResponse = true;
    /// <summary>The request editor height you dragged to; 0 means the default (a fifth of the screen).</summary>
    [ObservableProperty] private double _editorHeight;
    [ObservableProperty] private double _sidebarWidth = 280;

    static Settings Load()
    {
        var settings = Storage.Load<Settings>(PathOnDisk).Value ?? new Settings();
        settings.PropertyChanged += (_, _) => Storage.Write(settings, PathOnDisk);
        return settings;
    }
}
