using System;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.Core.World.Cells;

public readonly struct CellPortal
{
    public uint OtherCellId { get; }
    public ushort OtherPortalId { get; }
    public ushort PolygonId { get; }
    public ushort Flags { get; }
    public bool PortalSide => (Flags & 0x2) == 0;
    public IReadOnlyList<Vector3> PolygonLocal { get; }

    public CellPortal(uint otherCellId, ushort otherPortalId, ushort polygonId, ushort flags,
                      IReadOnlyList<Vector3>? polygonLocal = null)
    {
        OtherCellId = otherCellId;
        OtherPortalId = otherPortalId;
        PolygonId = polygonId;
        Flags = flags;
        PolygonLocal = polygonLocal ?? Array.Empty<Vector3>();
    }
}
