using System;
using System.Collections.Generic;
using DRWMotionCommand = DatReaderWriter.Enums.MotionCommand;

namespace AcDream.Core.Physics;

public sealed class AceModernCommandCatalog : IMotionCommandCatalog
{
    private readonly Dictionary<ushort, uint> _lookup;

    public AceModernCommandCatalog()
    {
        _lookup = BuildLookup();
    }

    public uint ReconstructFullCommand(ushort wireCommand)
    {
        if (wireCommand == 0) return 0u;
        _lookup.TryGetValue(wireCommand, out var full);
        return full;
    }

    private static Dictionary<ushort, uint> BuildLookup()
    {
        var byLow = new Dictionary<ushort, List<uint>>(512);
        foreach (DRWMotionCommand v in Enum.GetValues(typeof(DRWMotionCommand)))
        {
            uint full = (uint)v;
            ushort lo = (ushort)(full & 0xFFFFu);
            if (lo == 0) continue; // Invalid / unmappable

            if (!byLow.TryGetValue(lo, out var list))
                byLow[lo] = list = new List<uint>(1);
            if (!list.Contains(full))
                list.Add(full);
        }

        var result = new Dictionary<ushort, uint>(byLow.Count);
        foreach (var (lo, candidates) in byLow)
        {
            result[lo] = candidates.Count == 1
                ? candidates[0]
                : ResolveClassPriority(candidates);
        }

        return result;
    }

    public static uint ResolveClassPriority(IReadOnlyList<uint> candidates)
    {
        uint best = candidates[0];
        for (int i = 1; i < candidates.Count; i++)
        {
            uint candidate = candidates[i];
            if ((candidate >> 24) < (best >> 24))
                best = candidate;
        }
        return best;
    }
}
