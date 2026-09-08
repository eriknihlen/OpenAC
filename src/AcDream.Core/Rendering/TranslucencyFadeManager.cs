using System;
using System.Collections.Generic;

namespace AcDream.Core.Rendering;

public sealed class TranslucencyFadeManager
{
    public const float TranslucencyInstantEpsilon = 0.000199999995f;

    private sealed class Fade
    {
        public float Elapsed;
        public float Duration;
        public float Start;
        public float End;
    }

    // entityId -> partIndex -> in-flight ramp.
    private readonly Dictionary<uint, Dictionary<uint, Fade>> _activeFades = new();

    private readonly Dictionary<uint, Dictionary<uint, float>> _committed = new();

    private ulong _revision = 1;

    public ulong Revision => _revision;

    public void StartPartFade(uint entityId, uint partIndex, float start, float end, float time)
    {
        if (time <= TranslucencyInstantEpsilon)
        {
            if (_activeFades.TryGetValue(entityId, out var activeForEntity))
                activeForEntity.Remove(partIndex);
            Commit(entityId, partIndex, end);
            return;
        }

        if (!_activeFades.TryGetValue(entityId, out var fades))
        {
            fades = new Dictionary<uint, Fade>();
            _activeFades[entityId] = fades;
        }
        fades[partIndex] = new Fade { Elapsed = 0f, Duration = time, Start = start, End = end };

        Commit(entityId, partIndex, start);
    }

    public void AdvanceAll(float dt)
    {
        if (_activeFades.Count == 0) return;

        List<uint>? emptyEntities = null;
        foreach (var (entityId, fades) in _activeFades)
        {
            List<uint>? finished = null;
            foreach (var (partIndex, fade) in fades)
            {
                fade.Elapsed += dt;
                float t = fade.Duration <= TranslucencyInstantEpsilon
                    ? 1f
                    : Math.Clamp(fade.Elapsed / fade.Duration, 0f, 1f);

                float value = t >= 1f ? fade.End : fade.Start + (fade.End - fade.Start) * t;
                Commit(entityId, partIndex, value);

                if (t >= 1f)
                {
                    finished ??= new List<uint>();
                    finished.Add(partIndex);
                }
            }

            if (finished is not null)
            {
                foreach (var partIndex in finished)
                    fades.Remove(partIndex);
                if (fades.Count == 0)
                {
                    emptyEntities ??= new List<uint>();
                    emptyEntities.Add(entityId);
                }
            }
        }

        if (emptyEntities is not null)
            foreach (var entityId in emptyEntities)
                _activeFades.Remove(entityId);
    }

    public bool TryGetCurrentValue(uint entityId, uint partIndex, out float value)
    {
        if (_committed.TryGetValue(entityId, out var parts) && parts.TryGetValue(partIndex, out value))
            return true;
        value = 0f;
        return false;
    }

    /// <summary>Drop all fade state for an entity (despawn / unload).</summary>
    public void ClearEntity(uint entityId)
    {
        bool changed = _activeFades.Remove(entityId);
        changed |= _committed.Remove(entityId);
        if (changed)
            AdvanceRevision();
    }

    private void Commit(uint entityId, uint partIndex, float value)
    {
        if (!_committed.TryGetValue(entityId, out var parts))
        {
            parts = new Dictionary<uint, float>();
            _committed[entityId] = parts;
        }

        if (parts.TryGetValue(partIndex, out float prior)
            && BitConverter.SingleToInt32Bits(prior)
                == BitConverter.SingleToInt32Bits(value))
        {
            return;
        }

        parts[partIndex] = value;
        AdvanceRevision();
    }

    private void AdvanceRevision()
    {
        if (_revision == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                "Translucency fade revision space was exhausted.");
        }

        _revision++;
    }
}
