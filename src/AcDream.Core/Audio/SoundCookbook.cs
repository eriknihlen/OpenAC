using System;
using System.Collections.Generic;
using DatReaderWriter.DBObjs;
using DRWSound = DatReaderWriter.Enums.Sound;
using DRWSoundEntry = DatReaderWriter.Types.SoundEntry;

namespace AcDream.Core.Audio;

public static class SoundCookbook
{
    public static DRWSoundEntry? PickVariant(
        IReadOnlyList<DRWSoundEntry> entries,
        ISoundRandom rng)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(rng);

        if (entries.Count == 0) return null;

        int idx = (int)(rng.NextVariantRoll() * (entries.Count - 1));
        return idx < entries.Count ? entries[idx] : null;
    }

    public static bool PlayProbability(float probability, ISoundRandom rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        return rng.NextProbabilityRoll() < probability;
    }

    public static DRWSoundEntry? Select(
        IReadOnlyList<DRWSoundEntry> entries,
        ISoundRandom rng)
    {
        DRWSoundEntry? picked = PickVariant(entries, rng);
        if (picked is null) return null;
        return PlayProbability(picked.Probability, rng) ? picked : null;
    }

    public static DRWSoundEntry? Select(
        SoundTable table,
        DRWSound sound,
        ISoundRandom rng)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!table.Sounds.TryGetValue(sound, out var soundData)) return null;
        return Select(soundData.Entries, rng);
    }
}
