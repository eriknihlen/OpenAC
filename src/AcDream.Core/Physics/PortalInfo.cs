namespace AcDream.Core.Physics;

public readonly struct PortalInfo
{
    public PortalInfo(ushort otherCellId, ushort polygonId, ushort flags)
    {
        OtherCellId = otherCellId;
        PolygonId   = polygonId;
        Flags       = flags;
    }

    public ushort OtherCellId { get; }
    public ushort PolygonId   { get; }
    public ushort Flags       { get; }

    public bool PortalSide => (Flags & 2) == 0;
}
