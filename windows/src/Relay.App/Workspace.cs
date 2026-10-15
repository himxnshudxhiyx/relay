using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Relay.Core;

namespace Relay.App;

public enum SidebarMode { Collections, Environments, History }
public enum EditorSection { Params, Headers, Body, Auth }
public enum ResponseSection { Body, Headers, Sent }

public abstract record ResponseState
{
    public sealed record Idle : ResponseState;
    public sealed record Loading(DateTime Started, CancellationTokenSource Cancel) : ResponseState;
    public sealed record Done(HttpResult Result) : ResponseState;
    public sealed record Failed(string Message) : ResponseState;
}

/// <summary>An open tab: the saved <see cref="RequestTab"/> plus what only lives while the app runs.</summary>
public partial class TabState : ObservableObject, IDisposable
{
    public RequestTab Model { get; }
    readonly Observe _watch;

    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private ResponseState _response = new ResponseState.Idle();
    [ObservableProperty] private EditorSection _editorSection = EditorSection.Body;
    [ObservableProperty] private ResponseSection _responseSection = ResponseSection.Body;

    public TabState(RequestTab model)
    {
        Model = model;
        _watch = new Observe(Changed, model);
        IsDirty = !Json.Same(model.Draft, model.Original);
    }

    public Guid Id => Model.Id;
    public ApiRequest Draft => Model.Draft;

    /// <summary>Raised on any edit to the draft (and on save), after IsDirty is updated.</summary>
    public event Action? Edited;

    void Changed()
    {
        IsDirty = !Json.Same(Model.Draft, Model.Original);
        // Editing a preview tab keeps it open.
        if (IsDirty && Model.IsPreview) Model.IsPreview = false;
        Edited?.Invoke();
    }

    public bool IsLoading => Response is ResponseState.Loading;

    public void Dispose() => _watch.Dispose();
}

/// <summary>
/// Everything the window shows, and every way to change it. Ported from
/// Workspace.swift; one instance for the app, since there's one window.
/// </summary>
public partial class Workspace : ObservableObject
{
    public static Workspace Shared { get; } = new();

    // Saved state
    public ObservableCollection<ApiCollection> Collections { get; } = [];
    public ObservableCollection<ApiEnvironment> Environments { get; } = [];
    [ObservableProperty] private Guid? _activeEnvironmentID;
    public ObservableCollection<HistoryEntry> History { get; } = [];

    public ObservableCollection<TabState> Tabs { get; } = [];
    [ObservableProperty] private TabState? _selectedTab;
    public HashSet<Guid> Expanded { get; } = [];

    // Transient state
    [ObservableProperty] private SidebarMode _sidebarMode = SidebarMode.Collections;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private Guid? _renamingID;
    /// <summary>Bumped to move focus to the URL field / sidebar search.</summary>
    [ObservableProperty] private int _urlFocusRequest;
    [ObservableProperty] private int _searchFocusRequest;
    /// <summary>Bumped when the collection tree's shape changes (add, remove, move), so the sidebar rebuilds.</summary>
    [ObservableProperty] private int _treeVersion;

    readonly Debounce _saveWorkspace;
    readonly Debounce _saveSession;
    CancellationTokenSource? _toastTimer;
    bool _loading = true;

    Workspace()
    {
        _saveWorkspace = new Debounce(TimeSpan.FromMilliseconds(500), () => SaveWorkspace());
        _saveSession = new Debounce(TimeSpan.FromMilliseconds(800), () => SaveSession());

        var seeded = false;
        switch (Storage.Load<WorkspaceFile>(Storage.WorkspacePath))
        {
            case (Storage.State.Loaded, { } file):
                foreach (var c in file.Collections) Collections.Add(c);
                foreach (var e in file.Environments) Environments.Add(e);
                ActiveEnvironmentID = file.ActiveEnvironmentID;
                foreach (var h in file.History) History.Add(h);
                Storage.BackUpWorkspace();
                break;
            case (Storage.State.Missing, _):
                var seed = Seed.Make();
                foreach (var c in seed.Collections) { Collections.Add(c); Expanded.Add(c.Id); }
                foreach (var e in seed.Environments) Environments.Add(e);
                ActiveEnvironmentID = seed.Environments.FirstOrDefault()?.Id;
                seeded = true;
                break;
            default:
                Storage.SetAside(Storage.WorkspacePath);
                break;
        }

        if (Storage.Load<SessionFile>(Storage.SessionPath) is (Storage.State.Loaded, { } session))
        {
            var known = Collections.Select(c => c.Id).ToHashSet();
            foreach (var tab in session.Tabs)
            {
                if (tab.CollectionID is { } id && !known.Contains(id)) tab.CollectionID = null;
                AddTabState(tab, Tabs.Count);
            }
            SelectedTab = Tabs.FirstOrDefault(t => t.Id == session.SelectedTabID) ?? Tabs.FirstOrDefault();
            foreach (var id in session.Expanded) Expanded.Add(id);
        }

        _ = new Observe(() => { if (!_loading) _saveWorkspace.Schedule(); }, Collections, Environments, History);
        Tabs.CollectionChanged += (_, _) => ScheduleSessionSave();
        _loading = false;
        if (seeded) SaveWorkspace();
    }

    partial void OnActiveEnvironmentIDChanged(Guid? value)
    {
        if (!_loading) _saveWorkspace.Schedule();
    }

    partial void OnSelectedTabChanged(TabState? value) => ScheduleSessionSave();

    void ScheduleSessionSave()
    {
        if (!_loading) _saveSession.Schedule();
    }

    public void SetExpanded(Guid id, bool open)
    {
        if (open ? Expanded.Add(id) : Expanded.Remove(id)) ScheduleSessionSave();
    }

    // MARK: Persistence

    void SaveWorkspace(bool synchronously = false) => Storage.Write(new WorkspaceFile
    {
        Collections = [.. Collections],
        Environments = [.. Environments],
        ActiveEnvironmentID = ActiveEnvironmentID,
        History = [.. History],
    }, Storage.WorkspacePath, synchronously);

    void SaveSession(bool synchronously = false) => Storage.Write(new SessionFile
    {
        Tabs = [.. Tabs.Select(t => t.Model)],
        SelectedTabID = SelectedTab?.Id,
        Expanded = [.. Expanded],
    }, Storage.SessionPath, synchronously);

    /// <summary>Writes pending changes now; called on quit.</summary>
    public void Flush()
    {
        SaveWorkspace(synchronously: true);
        SaveSession(synchronously: true);
    }

    // MARK: Lookup

    public ApiEnvironment? ActiveEnvironment => Environments.FirstOrDefault(e => e.Id == ActiveEnvironmentID);
    public ApiCollection? Collection(Guid? id) => id == null ? null : Collections.FirstOrDefault(c => c.Id == id);
    public TabState? Tab(Guid id) => Tabs.FirstOrDefault(t => t.Id == id);

    /// <summary>The collection holding <paramref name="itemId"/> anywhere in its tree.</summary>
    public ApiCollection? Owner(Guid itemId) => Collections.FirstOrDefault(c => c.Items.ContainsItem(itemId));

    public VariableScope Scope(TabState tab) => new()
    {
        Collection = Collection(tab.Model.CollectionID)?.Variables.ToList() ?? [],
        Environment = ActiveEnvironment?.Variables.ToList() ?? [],
    };

    /// <summary>Variables offered while typing {{: the selected request's, or just the active environment's.</summary>
    public VariableScope CompletionScope => SelectedTab is { } tab ? Scope(tab) : new() { Environment = ActiveEnvironment?.Variables.ToList() ?? [] };

    /// <summary>The auth a tab's request really uses, and where it came from when inherited.</summary>
    public (Auth Auth, string? Source) EffectiveAuth(TabState tab)
    {
        if (tab.Draft.Auth.Type != AuthType.Inherit) return (tab.Draft.Auth, null);
        if (Collection(tab.Model.CollectionID) is not { } collection) return (Auth.NoAuth(), null);
        var chain = collection.Items.FolderChain(tab.Draft.Id) ?? [];
        for (var i = chain.Count - 1; i >= 0; i--)
            if (chain[i].Auth.Type != AuthType.Inherit) return (chain[i].Auth, chain[i].Name);
        return (collection.Auth.Type == AuthType.Inherit ? Auth.NoAuth() : collection.Auth, collection.Name);
    }

    public PreparedRequest Prepared(TabState tab, bool resolveVariables = true) =>
        RequestPreparer.Prepare(tab.Draft, EffectiveAuth(tab).Auth, resolveVariables ? Scope(tab).Values : null);

    public string Curl(TabState tab, bool resolveVariables = true) => CurlGenerator.Generate(Prepared(tab, resolveVariables));

    /// <summary>"Collection › Folder" for the request header.</summary>
    public List<string> Breadcrumb(TabState tab)
    {
        if (Collection(tab.Model.CollectionID) is not { } collection) return [];
        return [collection.Name, .. (collection.Items.FolderChain(tab.Draft.Id) ?? []).Select(f => f.Name)];
    }

    // MARK: Tabs

    TabState AddTabState(RequestTab model, int index)
    {
        var tab = new TabState(model);
        tab.Edited += ScheduleSessionSave;
        Tabs.Insert(index, tab);
        return tab;
    }

    public void NewTab(ApiRequest? request = null, bool unsaved = false)
    {
        request ??= ApiRequest.Blank();
        var original = Json.Clone(request);
        if (unsaved) original = new ApiRequest { Id = request.Id };
        InsertTab(new RequestTab { Draft = request, Original = original });
        FocusUrl();
    }

    public void Open(Guid requestId, Guid collectionId, bool preview = true)
    {
        if (Tabs.FirstOrDefault(t => t.Model.CollectionID == collectionId && t.Draft.Id == requestId) is { } existing)
        {
            if (!preview) existing.Model.IsPreview = false;
            SelectedTab = existing;
            return;
        }
        if (Collection(collectionId)?.Items.Request(requestId) is not { } request) return;
        var model = new RequestTab { CollectionID = collectionId, Draft = Json.Clone(request), Original = Json.Clone(request), IsPreview = preview };
        if (preview && Tabs.FirstOrDefault(t => t.Model.IsPreview && !t.IsDirty) is { } reusable)
        {
            var i = Tabs.IndexOf(reusable);
            Discard(reusable);
            SelectedTab = AddTabState(model, i);
        }
        else
        {
            InsertTab(model);
        }
    }

    void InsertTab(RequestTab model)
    {
        var index = SelectedTab is { } selected ? Tabs.IndexOf(selected) + 1 : Tabs.Count;
        SelectedTab = AddTabState(model, index);
    }

    void Discard(TabState tab)
    {
        Cancel(tab);
        tab.Dispose();
        Tabs.Remove(tab);
    }

    /// <summary>Replaces the request's contents with a parsed cURL, keeping its identity and name if saved.</summary>
    public void ApplyCurl(ApiRequest parsed, TabState tab)
    {
        var draft = tab.Draft;
        // Keep a name the user chose; replace a placeholder or one generated from the old URL.
        var generated = draft.Name == ApiRequest.DefaultName || draft.Name == CurlParser.RequestName(draft.Url);
        var keepName = tab.Model.CollectionID != null || !generated;
        parsed.Id = draft.Id;
        if (keepName) parsed.Name = draft.Name;
        tab.Model.Draft = parsed;
        Flash("Imported cURL");
    }

    public async Task Close(TabState tab)
    {
        if (tab.IsDirty)
        {
            switch (await Dialogs.SaveChanges(tab.Draft.Name))
            {
                case Dialogs.SaveChoice.Cancel: return;
                case Dialogs.SaveChoice.Save:
                    if (!await Save(tab)) return;
                    break;
            }
        }
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        var wasSelected = SelectedTab == tab;
        Discard(tab);
        if (wasSelected) SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    public async Task CloseOthers(TabState keep)
    {
        foreach (var tab in Tabs.Where(t => t != keep).ToList()) await Close(tab);
    }

    public async Task CloseAll()
    {
        foreach (var tab in Tabs.ToList()) await Close(tab);
    }

    public void DuplicateTab(TabState tab)
    {
        var copy = tab.Draft.WithNewIds();
        copy.Name = tab.Draft.Name + " Copy";
        NewTab(copy, unsaved: true);
    }

    public void SelectTab(int offset)
    {
        if (Tabs.Count == 0) return;
        var current = SelectedTab is { } s ? Tabs.IndexOf(s) : 0;
        SelectedTab = Tabs[((current + offset) % Tabs.Count + Tabs.Count) % Tabs.Count];
    }

    public void FocusUrl() => UrlFocusRequest++;

    public void FocusSearch()
    {
        SidebarMode = SidebarMode.Collections;
        SearchFocusRequest++;
    }

    // MARK: Saving requests

    /// <summary>Saves in place, or asks where to save a scratch request. True when the save completed.</summary>
    public async Task<bool> Save(TabState tab)
    {
        if (Collection(tab.Model.CollectionID) is not { } collection || collection.Items.Parent(tab.Draft.Id) is not { } parent)
            return await Dialogs.SaveRequest(tab);
        var index = parent.IndexOf(parent.First(i => i.Id == tab.Draft.Id));
        parent[index] = Json.Clone(tab.Draft);
        tab.Model.Original = Json.Clone(tab.Draft);
        tab.Model.IsPreview = false;
        TreeVersion++;
        return true;
    }

    public void SaveAs(TabState tab, string name, Guid collectionId, Guid? folderId)
    {
        if (Collection(collectionId) is not { } collection) return;
        var request = Json.Clone(tab.Draft);
        request.Name = name;
        // Save As on a saved request makes a second copy, not a second reference.
        if (Owner(request.Id) != null) request.Id = Guid.NewGuid();
        if (folderId is { } folder)
        {
            collection.Items.AppendToFolder(request, folder);
            SetExpanded(folder, true);
        }
        else
        {
            collection.Items.Add(request);
        }
        SetExpanded(collectionId, true);
        tab.Model.CollectionID = collectionId;
        tab.Model.Draft = Json.Clone(request);
        tab.Model.Original = request;
        tab.Model.IsPreview = false;
        TreeVersion++;
        Flash($"Saved to {collection.Name}");
    }

    // MARK: Collections

    public Guid AddCollection(string name = "New Collection", bool rename = true)
    {
        var collection = new ApiCollection { Name = name };
        Collections.Add(collection);
        SetExpanded(collection.Id, true);
        SidebarMode = SidebarMode.Collections;
        TreeVersion++;
        if (rename) RenamingID = collection.Id;
        return collection.Id;
    }

    public void AddRequest(Guid collectionId, Guid? folderId = null)
    {
        if (Collection(collectionId) is not { } collection) return;
        var request = ApiRequest.Blank();
        request.Name = "New Request";
        if (folderId is { } folder)
        {
            collection.Items.AppendToFolder(request, folder);
            SetExpanded(folder, true);
        }
        else
        {
            collection.Items.Add(request);
        }
        SetExpanded(collectionId, true);
        TreeVersion++;
        Open(request.Id, collectionId, preview: false);
        FocusUrl();
    }

    public void AddFolder(Guid collectionId, Guid? parentId = null)
    {
        if (Collection(collectionId) is not { } collection) return;
        var folder = new Folder();
        if (parentId is { } parent)
        {
            collection.Items.AppendToFolder(folder, parent);
            SetExpanded(parent, true);
        }
        else
        {
            collection.Items.Add(folder);
        }
        SetExpanded(collectionId, true);
        TreeVersion++;
        RenamingID = folder.Id;
    }

    public void Rename(Guid id, string newName)
    {
        var name = newName.Trim();
        if (name.Length == 0) return;
        if (Collection(id) is { } collection)
        {
            collection.Name = name;
            return;
        }
        foreach (var c in Collections)
            if (c.Items.Item(id) is { } item) item.Name = name;
        foreach (var tab in Tabs.Where(t => t.Draft.Id == id))
        {
            tab.Model.Original.Name = name;
            tab.Draft.Name = name;
        }
    }

    public async Task Delete(Guid id)
    {
        if (Collection(id) is { } c)
        {
            if (!await Dialogs.Confirm($"Delete “{c.Name}”?",
                    $"Its {c.Items.RequestCount()} requests will be deleted. Export it first if you might need it again.", "Delete Collection"))
                return;
            Collections.Remove(c);
            TreeVersion++;
            DetachTabs(t => t.Model.CollectionID == id);
            return;
        }
        if (Owner(id) is not { } owner || owner.Items.Item(id) is not { } item) return;
        if (item is Folder f && f.Items.Count > 0 &&
            !await Dialogs.Confirm($"Delete “{f.Name}”?", $"The folder and its {f.Items.RequestCount()} requests will be deleted.", "Delete Folder"))
            return;
        owner.Items.RemoveItem(id);
        TreeVersion++;
        DetachTabs(t => t.Model.CollectionID == owner.Id && !owner.Items.ContainsItem(t.Draft.Id));
    }

    /// <summary>Tabs whose request was deleted: clean ones close, edited ones become scratch requests.</summary>
    void DetachTabs(Func<TabState, bool> matches)
    {
        foreach (var tab in Tabs.Where(matches).ToList())
        {
            if (tab.IsDirty)
            {
                tab.Model.CollectionID = null;
            }
            else
            {
                var wasSelected = SelectedTab == tab;
                Discard(tab);
                if (wasSelected) SelectedTab = Tabs.LastOrDefault();
            }
        }
    }

    public void Duplicate(Guid id)
    {
        if (Collection(id) is { } c)
        {
            var copy = Json.Clone(c);
            copy.Id = Guid.NewGuid();
            copy.Name += " Copy";
            var items = copy.Items.Select(i => i.WithNewIds()).ToList();
            copy.Items.Clear();
            foreach (var i in items) copy.Items.Add(i);
            Collections.Insert(Collections.IndexOf(c) + 1, copy);
            TreeVersion++;
            return;
        }
        if (Owner(id) is not { } owner || owner.Items.Item(id) is not { } item) return;
        var dup = item.WithNewIds();
        dup.Name += " Copy";
        owner.Items.InsertBeside(dup, id, after: true);
        TreeVersion++;
    }

    public abstract record DropTarget
    {
        public sealed record ToCollection(Guid Collection) : DropTarget;
        public sealed record IntoFolder(Guid Collection, Guid Folder) : DropTarget;
        public sealed record BeforeItem(Guid Collection, Guid Item) : DropTarget;
    }

    /// <summary>Moves an item by drag and drop, within or across collections.</summary>
    public bool Move(Guid itemId, DropTarget target)
    {
        if (Owner(itemId) is not { } source) return false;
        // Refuse dropping onto itself or into its own subtree.
        switch (target)
        {
            case DropTarget.IntoFolder(_, var folder) when folder == itemId || source.Items.IsDescendant(folder, itemId):
            case DropTarget.BeforeItem(_, var sibling) when sibling == itemId || source.Items.IsDescendant(sibling, itemId):
                return false;
        }
        if (source.Items.RemoveItem(itemId) is not { } moved) return false;

        var placed = false;
        Guid targetCollection = default;
        switch (target)
        {
            case DropTarget.ToCollection(var cid) when Collection(cid) is { } c:
                c.Items.Add(moved);
                placed = true;
                targetCollection = cid;
                break;
            case DropTarget.IntoFolder(var cid, var folder) when Collection(cid) is { } c:
                placed = c.Items.AppendToFolder(moved, folder);
                if (placed) SetExpanded(folder, true);
                targetCollection = cid;
                break;
            case DropTarget.BeforeItem(var cid, var sibling) when Collection(cid) is { } c:
                placed = c.Items.InsertBeside(moved, sibling, after: false);
                targetCollection = cid;
                break;
        }
        if (!placed)
        {
            source.Items.Add(moved);
            return false;
        }
        var movedIds = (moved is Folder f ? f.Items.AllRequests().Select(r => r.Id) : [moved.Id]).ToHashSet();
        foreach (var tab in Tabs.Where(t => movedIds.Contains(t.Draft.Id) && t.Model.CollectionID != null))
            tab.Model.CollectionID = targetCollection;
        TreeVersion++;
        return true;
    }

    // MARK: Environments

    public Guid AddEnvironment(string name = "New Environment")
    {
        var environment = new ApiEnvironment { Name = name };
        Environments.Add(environment);
        ActiveEnvironmentID ??= environment.Id;
        return environment.Id;
    }

    public void DuplicateEnvironment(ApiEnvironment environment)
    {
        var copy = Json.Clone(environment);
        copy.Id = Guid.NewGuid();
        copy.Name += " Copy";
        foreach (var kv in copy.Variables) kv.Id = Guid.NewGuid();
        Environments.Insert(Environments.IndexOf(environment) + 1, copy);
    }

    public async Task DeleteEnvironment(ApiEnvironment environment)
    {
        if (!await Dialogs.Confirm($"Delete “{environment.Name}”?", $"Its {environment.Variables.Count} variables will be deleted.", "Delete Environment"))
            return;
        Environments.Remove(environment);
        if (ActiveEnvironmentID == environment.Id) ActiveEnvironmentID = null;
    }

    /// <summary>For a {{name}} that isn't defined anywhere: add it to the active environment (creating one if needed) and open it.</summary>
    public void DefineVariable(string name)
    {
        var id = ActiveEnvironmentID ?? AddEnvironment("Default");
        var environment = Environments.First(e => e.Id == id);
        if (!environment.Variables.Any(v => v.Key == name)) environment.Variables.Add(new KeyValue(name, ""));
        _ = Dialogs.Environments(id);
    }

    // MARK: Sending

    public void Send(TabState tab)
    {
        Cancel(tab);
        var prepared = Prepared(tab);
        var unresolved = Variables.Names(string.Join(" ", new[] { prepared.Url }.Concat(prepared.Headers.Select(h => h.Value))));
        if (unresolved.Count > 0)
        {
            tab.Response = new ResponseState.Failed($"{{{{{unresolved[0]}}}}} isn't defined. Add it to the active environment or the collection's variables.");
            return;
        }

        var settings = new HttpSettings { Timeout = TimeSpan.FromSeconds(Settings.Shared.Timeout), FollowRedirects = Settings.Shared.FollowRedirects, VerifySsl = Settings.Shared.VerifySsl };
        var started = DateTime.UtcNow;
        var snapshot = Json.Clone(tab.Draft);
        var curl = CurlGenerator.Generate(prepared);
        var cancel = new CancellationTokenSource();
        var loading = new ResponseState.Loading(started, cancel);
        tab.Response = loading;
        _ = Task.Run(async () =>
        {
            HttpResult? result = null;
            Exception? error = null;
            try
            {
                result = await RelayHttp.SendAsync(prepared, settings, cancel.Token);
                result.Curl = curl;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return; // Cancelled by the user; Cancel already reset the state.
            }
            catch (Exception e)
            {
                error = e;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Finish(tab, loading, snapshot, result, error));
        });
    }

    void Finish(TabState tab, ResponseState.Loading loading, ApiRequest request, HttpResult? result, Exception? error)
    {
        // Ignore a request that was superseded by a newer send in the same tab.
        if (tab.Response != loading) return;
        var entry = new HistoryEntry { Request = request, Date = loading.Started };
        if (result != null)
        {
            tab.Response = new ResponseState.Done(result);
            entry.Status = result.Status;
            entry.Duration = result.Duration;
        }
        else
        {
            var message = error?.Message ?? "The request failed.";
            tab.Response = new ResponseState.Failed(message);
            entry.Error = message;
            entry.Duration = (DateTime.UtcNow - loading.Started).TotalSeconds;
        }
        History.Insert(0, entry);
        while (History.Count > 300) History.RemoveAt(History.Count - 1);
    }

    public void Cancel(TabState tab)
    {
        if (tab.Response is not ResponseState.Loading loading) return;
        loading.Cancel.Cancel();
        tab.Response = new ResponseState.Idle();
    }

    // MARK: Import / export

    public void Import(ImportResult result)
    {
        foreach (var c in result.Collections) { Collections.Add(c); SetExpanded(c.Id, true); }
        foreach (var e in result.Environments) Environments.Add(e);
        if (ActiveEnvironmentID == null && result.Environments.FirstOrDefault() is { } first) ActiveEnvironmentID = first.Id;
        SidebarMode = result.Collections.Count == 0 && result.Environments.Count > 0 ? SidebarMode.Environments : SidebarMode.Collections;
        TreeVersion++;

        var parts = new List<string>();
        if (result.Collections.Count > 0)
            parts.Add(result.Collections.Count == 1 ? $"“{result.Collections[0].Name}”" : $"{result.Collections.Count} collections");
        if (result.Environments.Count > 0)
            parts.Add(result.Environments.Count == 1 ? "1 environment" : $"{result.Environments.Count} environments");
        Flash("Imported " + string.Join(" and ", parts));
    }

    public async Task ExportCollection(ApiCollection c)
    {
        await Export(() => Postman.ExportCollection(c), $"{FileName(c.Name)}.postman_collection.json", $"Exported “{c.Name}”", $"Couldn't export “{c.Name}”");
    }

    public async Task ExportEnvironment(ApiEnvironment e)
    {
        await Export(() => Postman.ExportEnvironment(e), $"{FileName(e.Name)}.postman_environment.json", $"Exported “{e.Name}”", $"Couldn't export “{e.Name}”");
    }

    public async Task ExportEverything()
    {
        await Export(() => Postman.ExportBackup(Collections, Environments), $"Relay Backup {DateTime.Now:yyyy-MM-dd}.json", "Exported everything", "Couldn't export");
    }

    async Task Export(Func<byte[]> make, string suggestedName, string done, string failed)
    {
        try
        {
            if (await Dialogs.SaveFile(make(), suggestedName)) Flash(done);
        }
        catch (Exception e)
        {
            await Dialogs.Inform(failed, e.Message);
        }
    }

    static string FileName(string name) => string.Join("-", name.Split('/', ':', '\\'));

    // MARK: History

    public void OpenHistory(HistoryEntry entry)
    {
        var request = Json.Clone(entry.Request);
        request.Id = Guid.NewGuid();
        NewTab(request);
    }

    public async Task ClearHistory()
    {
        if (await Dialogs.Confirm("Clear history?", $"All {History.Count} entries will be removed.", "Clear History")) History.Clear();
    }

    // MARK: Feedback

    public void Flash(string message)
    {
        Toast = message;
        _toastTimer?.Cancel();
        var cts = _toastTimer = new CancellationTokenSource();
        Task.Delay(1800, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Avalonia.Threading.Dispatcher.UIThread.Post(() => Toast = null);
        }, TaskScheduler.Default);
    }

    public async Task Copy(string text, string what)
    {
        await Dialogs.CopyText(text);
        Flash($"Copied {what}");
    }
}
