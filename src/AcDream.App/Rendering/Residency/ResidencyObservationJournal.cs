using System.Collections.Concurrent;

namespace AcDream.App.Rendering.Residency;

internal sealed class ResidencyObservationJournal
{
    private readonly ConcurrentDictionary<
        (AssetReference Asset, ResidencyObservationKind Kind),
        ResidencyObservation> _pending = new();
    private readonly int _maximumEntries;

    public ResidencyObservationJournal(int maximumEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        _maximumEntries = maximumEntries;
    }

    public int Count => _pending.Count;

    public bool TryPublish(in ResidencyObservation observation)
    {
        if (!observation.Asset.IsValid)
            throw new ArgumentException(
                "Residency observations require a valid asset reference.",
                nameof(observation));
        observation.Charges.Validate();

        var key = (observation.Asset, observation.Kind);
        while (true)
        {
            if (_pending.TryGetValue(key, out ResidencyObservation current))
            {
                if (observation.Frame < current.Frame)
                    return true;
                if (_pending.TryUpdate(key, observation, current))
                    return true;
                continue;
            }

            if (_pending.Count >= _maximumEntries)
                return false;
            if (_pending.TryAdd(key, observation))
                return true;
        }
    }

    public int DrainTo(List<ResidencyObservation> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int start = destination.Count;
        foreach (KeyValuePair<
                     (AssetReference Asset, ResidencyObservationKind Kind),
                     ResidencyObservation> pair in _pending)
        {
            // Remove only the exact value enumerated. If a worker replaced it
            // after enumeration, the newer value remains for the next drain.
            if (((ICollection<KeyValuePair<
                    (AssetReference Asset, ResidencyObservationKind Kind),
                    ResidencyObservation>>)_pending).Remove(pair))
            {
                destination.Add(pair.Value);
            }
        }

        return destination.Count - start;
    }
}
