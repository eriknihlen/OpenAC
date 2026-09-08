using System.Numerics;

namespace AcDream.App.Rendering;

public readonly record struct CameraSweepResult(Vector3 Eye, uint ViewerCellId);

public interface ICameraCollisionProbe
{
    CameraSweepResult SweepEye(Vector3 pivot, Vector3 desiredEye, uint cellId, uint selfEntityId, Vector3 playerPos);
}
