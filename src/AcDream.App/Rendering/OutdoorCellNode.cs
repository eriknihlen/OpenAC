using System.Numerics;

namespace AcDream.App.Rendering;

public static class OutdoorCellNode
{
    public static LoadedCell Build(uint outdoorCellId) => new LoadedCell
    {
        CellId = outdoorCellId,
        SeenOutside = true,
        IsOutdoorNode = true,
        WorldTransform = Matrix4x4.Identity,
        InverseWorldTransform = Matrix4x4.Identity,
    };
}
