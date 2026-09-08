using System.Numerics;

namespace AcDream.App.Physics;

internal sealed class LocalPlayerShadowState
{
    public readonly record struct Snapshot(
        Vector3 Position,
        Quaternion Orientation,
        uint CellId);

    public Snapshot? Current { get; private set; }

    public void Set(Vector3 position, Quaternion orientation, uint cellId) =>
        Current = new Snapshot(position, orientation, cellId);

    public void Clear() => Current = null;
}
