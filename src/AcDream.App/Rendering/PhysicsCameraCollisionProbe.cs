using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.App.Rendering;

public sealed class PhysicsCameraCollisionProbe : ICameraCollisionProbe
{
    public const float ViewerSphereRadius = 0.3f;

    private readonly PhysicsEngine _physics;

    public PhysicsCameraCollisionProbe(PhysicsEngine physics) => _physics = physics;

    public CameraSweepResult SweepEye(Vector3 pivot, Vector3 desiredEye, uint cellId, uint selfEntityId, Vector3 playerPos)
    {
        if (cellId == 0) return new CameraSweepResult(playerPos, 0u);

        uint startCell = cellId;
        if ((cellId & 0xFFFFu) >= 0x0100u)
        {
            var (pivotCell, found) = _physics.AdjustPosition(cellId, pivot);
            if (found) startCell = pivotCell;
        }

        Vector3 begin = ToSpherePath(pivot,      ViewerSphereRadius);
        Vector3 end   = ToSpherePath(desiredEye, ViewerSphereRadius);

        var r = _physics.ResolveWithTransition(
            currentPos:     begin,
            targetPos:      end,
            cellId:         startCell,
            sphereRadius:   ViewerSphereRadius,
            sphereHeight:   0f,                    // single sphere (no head sphere)
            stepUpHeight:   0f,
            stepDownHeight: 0f,                    // no step-down / ground snap
            isOnGround:     false,
            body:           null,
            moverFlags:     ObjectInfoState.IsViewer | ObjectInfoState.PathClipped
                          | ObjectInfoState.FreeRotate | ObjectInfoState.PerfectClip,
            movingEntityId: selfEntityId);         // skip the player's own ShadowEntry

        Vector3 eye = FromSpherePath(r.Position, ViewerSphereRadius);

        if (r.Ok) return new CameraSweepResult(KeepAboveWater(eye, r.CellId), r.CellId);

        var (eyeCell, eyeFound) = _physics.AdjustPosition(cellId, desiredEye);
        if (eyeFound) return new CameraSweepResult(KeepAboveWater(desiredEye, eyeCell), eyeCell);

        // === Fallback 2 (pc:92886-92887): set_viewer(player_pos), viewer_cell = null ===
        return new CameraSweepResult(playerPos, 0u);
    }

    /// <summary>
    /// The eye never goes below the water. Outdoors, the terrain height of a
    /// water cell is the water surface, so an eye that ended up under it is
    /// lifted back to the surface plus its own radius. Cells inside a
    /// building or dungeon have no water, and an eye over terrain that is
    /// not resident is left alone.
    /// </summary>
    private Vector3 KeepAboveWater(Vector3 eye, uint eyeCellId)
    {
        if ((eyeCellId & 0xFFFFu) >= 0x0100u) return eye;
        if (_physics.SampleWaterDepth(eye.X, eye.Y) <= 0f) return eye;
        float? surface = _physics.SampleTerrainZ(eye.X, eye.Y);
        return surface is { } z ? ClampAboveWater(eye, z, ViewerSphereRadius) : eye;
    }

    internal static Vector3 ClampAboveWater(Vector3 eye, float waterSurfaceZ, float margin)
    {
        float floor = waterSurfaceZ + margin;
        return eye.Z < floor ? eye with { Z = floor } : eye;
    }

    internal static Vector3 ToSpherePath(Vector3 spherePoint, float radius)
        => spherePoint - new Vector3(0f, 0f, radius);

    internal static Vector3 FromSpherePath(Vector3 pathPoint, float radius)
        => pathPoint + new Vector3(0f, 0f, radius);
}
