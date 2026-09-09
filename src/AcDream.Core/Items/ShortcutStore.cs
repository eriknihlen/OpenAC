using System;
using System.Collections.Generic;

namespace AcDream.Core.Items;

public sealed class ShortcutStore : IDisposable
{
    public const int SlotCount = 18;
    private readonly ShortcutEntry?[] _entries = new ShortcutEntry?[SlotCount];
    private ShortcutEntry[] _items = [];
    private Action? _changed;
    private long _revision;
    private long _dispatchFailureCount;
    private bool _disposed;

    public event Action? Changed
    {
        add
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _changed += value;
        }
        remove => _changed -= value;
    }

    /// <summary>Stable packed-order view of all present shortcut records.</summary>
    public IReadOnlyList<ShortcutEntry> Items => Volatile.Read(ref _items);

    public int Count => Volatile.Read(ref _items).Length;
    public long Revision => Interlocked.Read(ref _revision);
    public int SubscriberCount => _changed?.GetInvocationList().Length ?? 0;
    public long DispatchFailureCount =>
        Interlocked.Read(ref _dispatchFailureCount);
    public Exception? LastDispatchFailure { get; private set; }
    public bool IsDisposed => _disposed;

    public void Load(IEnumerable<ShortcutEntry> entries)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entries);
        Array.Clear(_entries);
        foreach (var entry in entries)
            SetCore(entry);
        PublishChanged();
    }

    /// <summary>Raw entry at <paramref name="slot"/>, or null for absent/out-of-range.</summary>
    public ShortcutEntry? GetEntry(int slot)
        => (uint)slot < SlotCount ? _entries[slot] : null;

    public ShortcutEntry?[] Snapshot() => (ShortcutEntry?[])_entries.Clone();

    /// <summary>Visible object id at <paramref name="slot"/>, or zero.</summary>
    public uint Get(int slot) => GetEntry(slot)?.ObjectId ?? 0u;

    public bool IsEmpty(int slot) => Get(slot) == 0u;

    public void Set(ShortcutEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (SetCore(entry))
            PublishChanged();
    }

    public void SetItem(int slot, uint objectId) => Set(new ShortcutEntry(slot, objectId, 0u));

    public void Remove(int slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)slot >= SlotCount || _entries[slot] is null)
            return;
        _entries[slot] = null;
        PublishChanged();
    }

    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Array.Clear(_entries);
        PublishChanged();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Array.Clear(_entries);
        RebuildItems();
        Interlocked.Increment(ref _revision);
        DispatchChanged();
        _changed = null;
    }

    private bool SetCore(ShortcutEntry entry)
    {
        if ((uint)entry.Index >= SlotCount
            || _entries[entry.Index] == entry)
        {
            return false;
        }
        _entries[entry.Index] = entry;
        return true;
    }

    private void PublishChanged()
    {
        RebuildItems();
        Interlocked.Increment(ref _revision);
        DispatchChanged();
    }

    private void RebuildItems()
    {
        var items = new List<ShortcutEntry>(SlotCount);
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i] is ShortcutEntry entry)
                items.Add(entry);
        }
        Volatile.Write(ref _items, items.ToArray());
    }

    private void DispatchChanged()
    {
        Delegate[] observers = _changed?.GetInvocationList() ?? [];
        foreach (Delegate observer in observers)
        {
            try
            {
                ((Action)observer)();
            }
            catch (Exception error)
            {
                Interlocked.Increment(ref _dispatchFailureCount);
                LastDispatchFailure = error;
            }
        }
    }
}
