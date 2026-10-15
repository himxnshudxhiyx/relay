using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Relay.App;

/// <summary>
/// Calls back when anything in an object graph of observable models changes:
/// a property anywhere below, or an item added to or removed from any list.
/// Drives autosave and "unsaved edits", the way didSet on value types does on the Mac.
/// </summary>
sealed class Observe : IDisposable
{
    readonly Action _changed;
    readonly object[] _roots;
    readonly HashSet<object> _watched = new(ReferenceEqualityComparer.Instance);

    public Observe(Action changed, params object[] roots)
    {
        _changed = changed;
        _roots = roots;
        Subscribe();
    }

    void Subscribe()
    {
        foreach (var root in _roots) Walk(root);
    }

    void Walk(object? node)
    {
        if (node == null || node is string || !_watched.Add(node)) return;
        if (node is INotifyCollectionChanged list)
        {
            list.CollectionChanged += OnCollectionChanged;
            foreach (var item in (IEnumerable)list) Walk(item);
        }
        else if (node is INotifyPropertyChanged observable)
        {
            observable.PropertyChanged += OnPropertyChanged;
            foreach (var p in node.GetType().GetProperties())
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (typeof(INotifyPropertyChanged).IsAssignableFrom(p.PropertyType) || typeof(INotifyCollectionChanged).IsAssignableFrom(p.PropertyType))
                    Walk(p.GetValue(node));
            }
        }
    }

    void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A replaced child (a new Body, a new list) needs watching too.
        var value = sender?.GetType().GetProperty(e.PropertyName ?? "")?.GetValue(sender);
        if (value is INotifyPropertyChanged or INotifyCollectionChanged) Rewalk();
        _changed();
    }

    void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Rewalk();
        _changed();
    }

    void Rewalk()
    {
        Unsubscribe();
        Subscribe();
    }

    void Unsubscribe()
    {
        foreach (var node in _watched)
        {
            if (node is INotifyCollectionChanged list) list.CollectionChanged -= OnCollectionChanged;
            else if (node is INotifyPropertyChanged observable) observable.PropertyChanged -= OnPropertyChanged;
        }
        _watched.Clear();
    }

    public void Dispose() => Unsubscribe();
}

/// <summary>Coalesces bursts of calls (typing) into one, after a pause.</summary>
sealed class Debounce(TimeSpan delay, Action action)
{
    CancellationTokenSource? _pending;

    public void Schedule()
    {
        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        Task.Delay(delay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Avalonia.Threading.Dispatcher.UIThread.Post(action);
        }, TaskScheduler.Default);
    }

    /// <summary>Runs a pending call now (on quit).</summary>
    public void Flush()
    {
        if (_pending is { IsCancellationRequested: false } pending)
        {
            pending.Cancel();
            action();
        }
        _pending = null;
    }
}
