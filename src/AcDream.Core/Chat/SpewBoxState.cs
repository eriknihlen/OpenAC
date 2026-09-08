using System;
using System.Collections.Generic;
using System.Threading;

namespace AcDream.Core.Chat;

public readonly record struct SpewBoxEntry(string Text, double ExpiresAtSeconds);

public sealed class SpewBoxState
{
    public const int MaxConcurrentItems = 4;

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly Queue<string> _pending = new();
    private readonly List<SpewBoxEntry> _visible = new();
    private long _revision;

    public long Revision => Interlocked.Read(ref _revision);

    public int Count
    {
        get { lock (_gate) return _visible.Count; }
    }

    public void Enqueue(string text)
    {
        lock (_gate)
            _pending.Enqueue(text);
    }

    public void Tick(double nowSeconds)
    {
        bool changed;
        lock (_gate)
        {
            changed = _pending.Count > 0;
            while (_pending.Count > 0)
            {
                string text = _pending.Dequeue();

                if (_visible.Count > 0 && _visible[0].Text == text)
                    _visible.RemoveAt(0);

                _visible.Insert(0, new SpewBoxEntry(text, nowSeconds + DefaultLifetime.TotalSeconds));

                while (_visible.Count > MaxConcurrentItems)
                    _visible.RemoveAt(_visible.Count - 1);
            }

            int removed = _visible.RemoveAll(e => e.ExpiresAtSeconds <= nowSeconds);
            changed |= removed > 0;
        }

        if (changed)
            Interlocked.Increment(ref _revision);
    }

    public SpewBoxEntry[] Snapshot()
    {
        lock (_gate)
            return _visible.ToArray();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _pending.Clear();
            _visible.Clear();
        }
        Interlocked.Increment(ref _revision);
    }
}
