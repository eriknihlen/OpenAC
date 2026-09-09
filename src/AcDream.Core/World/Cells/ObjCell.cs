using System.Collections.Generic;
using System.Numerics;

namespace AcDream.Core.World.Cells;

public abstract class ObjCell
{
    public uint Id { get; }
    public Matrix4x4 WorldTransform { get; }
    public Matrix4x4 InverseWorldTransform { get; }
    public Vector3 LocalBoundsMin { get; }
    public Vector3 LocalBoundsMax { get; }
    public IReadOnlyList<CellPortal> Portals { get; }
    public IReadOnlyList<uint> StabList { get; }
    public bool SeenOutside { get; }

    public bool IsEnv => (Id & 0xFFFFu) >= 0x100u;

    protected ObjCell(uint id, Matrix4x4 worldTransform, Matrix4x4 inverseWorldTransform,
                      Vector3 localBoundsMin, Vector3 localBoundsMax,
                      IReadOnlyList<CellPortal> portals, IReadOnlyList<uint> stabList,
                      bool seenOutside)
    {
        Id = id;
        WorldTransform = worldTransform;
        InverseWorldTransform = inverseWorldTransform;
        LocalBoundsMin = localBoundsMin;
        LocalBoundsMax = localBoundsMax;
        Portals = portals;
        StabList = stabList;
        SeenOutside = seenOutside;
    }

    public abstract bool PointInCell(Vector3 worldPoint);
}
