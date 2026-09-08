using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.Enums;

namespace AcDream.Core.Physics;

public sealed class BuildingPhysics
{
    public required Matrix4x4 WorldTransform { get; init; }
    public required Matrix4x4 InverseWorldTransform { get; init; }
    public required IReadOnlyList<BldPortalInfo> Portals { get; init; }

    public uint ModelId { get; init; }
}

public readonly struct BldPortalInfo
{
    public BldPortalInfo(uint otherCellId, short otherPortalId, ushort flags)
    {
        OtherCellId   = otherCellId;
        OtherPortalId = otherPortalId;
        Flags         = flags;
    }

    public uint   OtherCellId   { get; }
    public short  OtherPortalId { get; }
    public ushort Flags         { get; }

    public bool ExactMatch => (Flags & (ushort)PortalFlags.ExactMatch) != 0;
}
