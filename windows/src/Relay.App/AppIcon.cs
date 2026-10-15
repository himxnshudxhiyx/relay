using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Relay.App;

/// <summary>The lime app icon (rendered by tools/MakeIcon.swift, like the Mac app's).</summary>
static class AppIcon
{
    public static readonly Bitmap Bitmap = new(AssetLoader.Open(new Uri("avares://Relay/Assets/icon.png")));
}
