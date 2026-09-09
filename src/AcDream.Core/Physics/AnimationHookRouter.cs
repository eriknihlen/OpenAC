using System;
using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.Types;

namespace AcDream.Core.Physics;

public sealed class AnimationHookRouter : IAnimationHookSink
{
    private readonly object _gate = new();
    private IAnimationHookSink[] _sinks = Array.Empty<IAnimationHookSink>();

    /// <summary>
    /// Register a sink. Idempotent — adding the same instance twice is a no-op.
    /// </summary>
    public void Register(IAnimationHookSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            foreach (var existing in _sinks)
                if (ReferenceEquals(existing, sink)) return;

            var updated = new IAnimationHookSink[_sinks.Length + 1];
            Array.Copy(_sinks, updated, _sinks.Length);
            updated[_sinks.Length] = sink;
            _sinks = updated;
        }
    }

    /// <summary>
    /// Unregister a sink. No-op if not registered.
    /// </summary>
    public void Unregister(IAnimationHookSink sink)
    {
        if (sink is null) return;
        lock (_gate)
        {
            int idx = -1;
            for (int i = 0; i < _sinks.Length; i++)
                if (ReferenceEquals(_sinks[i], sink)) { idx = i; break; }
            if (idx < 0) return;

            var updated = new IAnimationHookSink[_sinks.Length - 1];
            for (int i = 0, j = 0; i < _sinks.Length; i++)
                if (i != idx) updated[j++] = _sinks[i];
            _sinks = updated;
        }
    }

    public IReadOnlyList<IAnimationHookSink> Sinks => _sinks;

    /// <inheritdoc />
    public void OnHook(uint entityId, Vector3 entityWorldPosition, AnimationHook hook)
    {
        // Snapshot — no lock in the hot path (render thread).
        var sinks = _sinks;
        for (int i = 0; i < sinks.Length; i++)
        {
            try
            {
                sinks[i].OnHook(entityId, entityWorldPosition, hook);
            }
            catch
            {
                // Swallow — one misbehaving sink must not take down the
                // entire animation tick. Individual subsystems can log
                // their own errors internally.
            }
        }
    }
}
