using System;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.Core.Audio;

public readonly record struct AmbientSoundFiring(
    AmbientSoundInstance Instance,
    float Volume,
    Vector3? Position);

public sealed class AmbientSoundScheduler
{
    private readonly PriorityQueue<AmbientSoundInstance, double> _queue = new();
    private readonly List<AmbientSoundInstance> _instances = [];
    private readonly ISoundRandom _rng;

    public AmbientSoundScheduler(ISoundRandom? rng = null) => _rng = rng ?? new SoundRandom();

    /// <summary>Every live instance, in accumulation order.</summary>
    public IReadOnlyList<AmbientSoundInstance> Instances => _instances;

    public int QueuedCount => _queue.Count;

    public float TotalSoundCount { get; private set; }

    public void BeginRebuild()
    {
        foreach (AmbientSoundInstance instance in _instances)
            instance.ResetCount();
        TotalSoundCount = 0f;
    }

    public AmbientSoundInstance Track(AmbientSoundDescriptor descriptor, uint soundTableDid)
    {
        foreach (AmbientSoundInstance existing in _instances)
        {
            if (existing.Descriptor == descriptor && existing.SoundTableDid == soundTableDid)
                return existing;
        }

        var created = new AmbientSoundInstance(descriptor, soundTableDid);
        _instances.Add(created);
        return created;
    }

    public void ContributeCell<TTable>(
        Vector3 offset,
        TTable table,
        Func<TTable, int, AmbientSoundDescriptor> descriptorAt)
        where TTable : DatReaderWriter.Types.AmbientSTBDesc
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(descriptorAt);

        float weight = AmbientSoundConstants.CalcWeight(offset);
        if (weight <= 0f)
            return;

        AmbientDirection direction = AmbientSoundConstants.CalcDirection(offset);
        TotalSoundCount += weight;

        for (int i = 0; i < table.AmbientSounds.Count; i++)
        {
            AmbientSoundInstance instance = Track(descriptorAt(table, i), table.STBId);
            instance.AddTo(weight, offset, direction);
        }
    }

    public void Contribute(AmbientSoundInstance instance, Vector3 offset)
    {
        ArgumentNullException.ThrowIfNull(instance);

        float weight = AmbientSoundConstants.CalcWeight(offset);
        if (weight <= 0f)
            return;

        instance.AddTo(weight, offset, AmbientSoundConstants.CalcDirection(offset));
        TotalSoundCount += weight;
    }

    public void EndRebuild(
        double now,
        ICollection<AmbientSoundFiring>? firings = null,
        Vector3 listenerPosition = default)
    {
        foreach (AmbientSoundInstance instance in _instances)
            instance.UpdateSound(TotalSoundCount);

        foreach (AmbientSoundInstance instance in _instances)
        {
            if (instance.OnQueue || !instance.CanHear())
                continue;

            Fire(instance, now, firings, listenerPosition);
        }
    }

    public void Tick(double now, ICollection<AmbientSoundFiring> firings, Vector3 listenerPosition)
    {
        ArgumentNullException.ThrowIfNull(firings);

        while (_queue.TryPeek(out _, out double deadline) && deadline < now)
        {
            AmbientSoundInstance instance = _queue.Dequeue();
            instance.OnQueue = false;

            if (!instance.CanHear())
                continue;

            Fire(instance, now, firings, listenerPosition);
        }
    }

    public void Clear()
    {
        foreach (AmbientSoundInstance instance in _instances)
            instance.OnQueue = false;
        _instances.Clear();
        _queue.Clear();
        TotalSoundCount = 0f;
    }

    private void Fire(
        AmbientSoundInstance instance,
        double now,
        ICollection<AmbientSoundFiring>? firings,
        Vector3 listenerPosition)
    {
        if (firings is not null && instance.PlayNow(_rng))
        {
            Vector3? position =
                instance.TryGetSoundPosition(listenerPosition, _rng, out Vector3 resolved)
                    ? resolved
                    : null;
            firings.Add(new AmbientSoundFiring(instance, instance.GetVolume(), position));
        }

        Enqueue(instance, now);
    }

    private void Enqueue(AmbientSoundInstance instance, double now)
    {
        _queue.Enqueue(instance, now + instance.GetPlayInterval(_rng));
        instance.OnQueue = true;
    }
}
