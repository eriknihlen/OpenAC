using System.Collections.Generic;

namespace AcDream.Core.Rendering.Wb;

public static class EnvCellGeometryIdentity
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private const ulong PayloadMask = 0x0FFF_FFFD_FFFF_FFFFUL;
    private const ulong Namespace = 0xE000_0002_0000_0000UL;

    public static ulong Compute(
        uint environmentId,
        ushort cellStructure,
        IReadOnlyList<ushort> surfaces)
    {
        ulong hash = FnvOffsetBasis;
        AddUInt32(ref hash, environmentId);
        AddUInt16(ref hash, cellStructure);
        AddUInt32(ref hash, checked((uint)surfaces.Count));
        for (int i = 0; i < surfaces.Count; i++)
            AddUInt16(ref hash, surfaces[i]);

        return (hash & PayloadMask) | Namespace;
    }

    public static ulong ComputeLegacyWorldBuilder(
        uint environmentId,
        ushort cellStructure,
        IReadOnlyList<ushort> surfaces)
    {
        long hash = 17;
        hash = unchecked(hash * 31 + environmentId);
        hash = unchecked(hash * 31 + cellStructure);
        for (int i = 0; i < surfaces.Count; i++)
            hash = unchecked(hash * 31 + surfaces[i]);
        return (ulong)hash | 0x2_0000_0000UL;
    }

    private static void AddUInt16(ref ulong hash, ushort value)
    {
        AddByte(ref hash, (byte)value);
        AddByte(ref hash, (byte)(value >> 8));
    }

    private static void AddUInt32(ref ulong hash, uint value)
    {
        AddByte(ref hash, (byte)value);
        AddByte(ref hash, (byte)(value >> 8));
        AddByte(ref hash, (byte)(value >> 16));
        AddByte(ref hash, (byte)(value >> 24));
    }

    private static void AddByte(ref ulong hash, byte value)
    {
        hash ^= value;
        hash = unchecked(hash * FnvPrime);
    }
}
