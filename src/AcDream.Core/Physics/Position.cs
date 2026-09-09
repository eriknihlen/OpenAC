using System.Numerics;

namespace AcDream.Core.Physics;

public readonly record struct CellFrame(Vector3 Origin, Quaternion Orientation);

public readonly record struct Position(uint ObjCellId, CellFrame Frame)
{
    public Position(uint objCellId, Vector3 origin, Quaternion orientation)
        : this(objCellId, new CellFrame(origin, orientation)) { }
}
