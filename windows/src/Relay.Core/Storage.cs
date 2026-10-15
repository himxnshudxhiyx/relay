namespace Relay.Core;

/// <summary>
/// Files in %APPDATA%\Relay, the same three the Mac app keeps: workspace.json
/// (collections, environments, history), session.json (open tabs) and
/// workspace.backup.json (the workspace as it was at launch). Ported from Storage.swift.
/// </summary>
public static class Storage
{
    /// <summary>RELAY_DATA_DIR overrides the folder, for testing against a copy.</summary>
    public static readonly string Folder = Environment.GetEnvironmentVariable("RELAY_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Relay");

    public static string WorkspacePath => Path.Combine(Folder, "workspace.json");
    public static string SessionPath => Path.Combine(Folder, "session.json");
    public static string BackupPath => Path.Combine(Folder, "workspace.backup.json");

    /// <summary>One write at a time, so an older snapshot can never land on disk after a newer one.</summary>
    static readonly SemaphoreSlim Gate = new(1, 1);

    public enum State { Missing, Loaded, Unreadable }

    public static (State State, T? Value) Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return (State.Missing, null);
        try
        {
            return Json.Deserialize<T>(File.ReadAllText(path)) is { } value ? (State.Loaded, value) : (State.Unreadable, null);
        }
        catch (Exception)
        {
            return (State.Unreadable, null);
        }
    }

    /// <summary>
    /// Serializes now (so later edits can't leak into this snapshot) and writes
    /// in the background, or right away on quit.
    /// </summary>
    public static Task Write<T>(T value, string path, bool synchronously = false)
    {
        var json = Json.Serialize(value);
        if (synchronously)
        {
            Gate.Wait();
            try { WriteAtomically(path, json); } finally { Gate.Release(); }
            return Task.CompletedTask;
        }
        return Task.Run(async () =>
        {
            await Gate.WaitAsync();
            try { WriteAtomically(path, json); } catch (IOException) { } finally { Gate.Release(); }
        });
    }

    static void WriteAtomically(string path, string json)
    {
        Directory.CreateDirectory(Folder);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>A copy of the workspace as it was at launch: one step of undo for anything that goes wrong during a session.</summary>
    public static void BackUpWorkspace() => Task.Run(async () =>
    {
        await Gate.WaitAsync();
        try { File.Copy(WorkspacePath, BackupPath, overwrite: true); } catch (IOException) { } finally { Gate.Release(); }
    });

    /// <summary>Moves an unreadable file aside rather than letting the next save overwrite something still recoverable by hand.</summary>
    public static void SetAside(string path)
    {
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try { File.Move(path, Path.ChangeExtension(path, $"unreadable-{stamp}.json")); } catch (IOException) { }
    }
}
