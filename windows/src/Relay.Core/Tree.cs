using System.Collections.ObjectModel;

namespace Relay.Core;

/// <summary>Lookups and edits on a collection's item tree, addressed by id. Ported from Tree.swift.</summary>
public static class Tree
{
    public static CollectionItem? Item(this IEnumerable<CollectionItem> items, Guid id)
    {
        foreach (var item in items)
        {
            if (item.Id == id) return item;
            if (item is Folder f && f.Items.Item(id) is { } found) return found;
        }
        return null;
    }

    public static ApiRequest? Request(this IEnumerable<CollectionItem> items, Guid id) => items.Item(id) as ApiRequest;

    public static bool ContainsItem(this IEnumerable<CollectionItem> items, Guid id) => items.Item(id) != null;

    /// <summary>Folders enclosing <paramref name="id"/>, outermost first; null when it isn't in this tree.</summary>
    public static List<Folder>? FolderChain(this IEnumerable<CollectionItem> items, Guid id)
    {
        foreach (var item in items)
        {
            if (item.Id == id) return [];
            if (item is Folder f && f.Items.FolderChain(id) is { } chain) return [f, .. chain];
        }
        return null;
    }

    /// <summary>The list that directly holds <paramref name="id"/>.</summary>
    public static ObservableCollection<CollectionItem>? Parent(this ObservableCollection<CollectionItem> items, Guid id)
    {
        if (items.Any(i => i.Id == id)) return items;
        foreach (var f in items.OfType<Folder>())
            if (f.Items.Parent(id) is { } parent) return parent;
        return null;
    }

    public static CollectionItem? RemoveItem(this ObservableCollection<CollectionItem> items, Guid id)
    {
        var parent = items.Parent(id);
        var item = parent?.FirstOrDefault(i => i.Id == id);
        if (item != null) parent!.Remove(item);
        return item;
    }

    /// <summary>Inserts beside <paramref name="sibling"/>, wherever in the tree it is.</summary>
    public static bool InsertBeside(this ObservableCollection<CollectionItem> items, CollectionItem item, Guid sibling, bool after)
    {
        var parent = items.Parent(sibling);
        if (parent == null) return false;
        var i = parent.IndexOf(parent.First(x => x.Id == sibling));
        parent.Insert(after ? i + 1 : i, item);
        return true;
    }

    public static bool AppendToFolder(this IEnumerable<CollectionItem> items, CollectionItem item, Guid folderId)
    {
        if (items.Item(folderId) is not Folder f) return false;
        f.Items.Add(item);
        return true;
    }

    public static int RequestCount(this IEnumerable<CollectionItem> items) =>
        items.Sum(i => i is Folder f ? f.Items.RequestCount() : 1);

    /// <summary>Every folder, depth first, with its path ("Users / Admin").</summary>
    public static IEnumerable<(Folder Folder, string Path)> FolderPaths(this IEnumerable<CollectionItem> items, string prefix = "")
    {
        foreach (var f in items.OfType<Folder>())
        {
            var path = prefix.Length == 0 ? f.Name : $"{prefix} / {f.Name}";
            yield return (f, path);
            foreach (var child in f.Items.FolderPaths(path)) yield return child;
        }
    }

    /// <summary>Every request, depth first.</summary>
    public static IEnumerable<ApiRequest> AllRequests(this IEnumerable<CollectionItem> items) =>
        items.SelectMany(i => i is Folder f ? f.Items.AllRequests() : [(ApiRequest)i]);

    /// <summary>Does <paramref name="ancestor"/> contain <paramref name="id"/> anywhere below it?</summary>
    public static bool IsDescendant(this IEnumerable<CollectionItem> items, Guid id, Guid ancestor) =>
        items.Item(ancestor) is Folder f && f.Items.ContainsItem(id);

    /// <summary>A deep copy with fresh ids, for Duplicate.</summary>
    public static T WithNewIds<T>(this T item) where T : CollectionItem
    {
        var copy = Json.Clone<CollectionItem>(item);
        Renew(copy);
        return (T)copy;

        static void Renew(CollectionItem i)
        {
            i.Id = Guid.NewGuid();
            if (i is ApiRequest r)
            {
                foreach (var kv in r.Params.Concat(r.Headers).Concat(r.Body.Form)) kv.Id = Guid.NewGuid();
            }
            else if (i is Folder f)
            {
                foreach (var child in f.Items) Renew(child);
            }
        }
    }

    /// <summary>The auth a request actually uses: its own, or the nearest parent's when set to inherit.</summary>
    public static Auth EffectiveAuth(this ApiCollection collection, ApiRequest request)
    {
        if (request.Auth.Type != AuthType.Inherit) return request.Auth;
        var chain = collection.Items.FolderChain(request.Id) ?? [];
        for (var i = chain.Count - 1; i >= 0; i--)
            if (chain[i].Auth.Type != AuthType.Inherit) return chain[i].Auth;
        return collection.Auth.Type == AuthType.Inherit ? Auth.NoAuth() : collection.Auth;
    }
}
