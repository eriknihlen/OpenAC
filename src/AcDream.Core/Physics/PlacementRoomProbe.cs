using System.Collections.Immutable;
using System.Numerics;

namespace AcDream.Core.Physics;

/// <summary>What a room check found at the spot it looked at.</summary>
internal enum PlacementRoom
{
    /// <summary>The check could not be made: the spot is not loaded, or the body has no shape.</summary>
    Unknown = 0,

    /// <summary>The body can be set down at the spot.</summary>
    Clear,

    /// <summary>Something solid is in the way at every height tried.</summary>
    Blocked,
}

/// <summary>The body a room check sets down: its collision spheres and how far it steps up and down.</summary>
internal readonly record struct PlacementRoomBody(
    ImmutableArray<FlatCollisionSphere> Spheres,
    float Scale,
    float StepUpHeight,
    float StepDownHeight,
    uint SelfEntityId);

/// <summary>The answer of a room check and, when clear, where the body would stand.</summary>
internal readonly record struct PlacementRoomResult(
    PlacementRoom Room,
    uint CellId = 0u,
    Vector3 CellLocalPosition = default,
    Vector3 Position = default);

/// <summary>
/// Asks whether a body could be set down a given distance in front of a
/// standing one -- the room a summoned pet needs -- using the same placement a
/// newly created object gets when it enters the world. Nothing is committed:
/// the placement runs in a scratch transition and only its answer is read.
///
/// The spot must be free where it is: sliding aside to a free spot, which an
/// object entering the world may do, is not allowed, because a pet that is
/// slid behind a wall is exactly what the check is there to avoid. Live
/// creatures and players standing there do not count, as they move on; walls,
/// buildings, terrain, doors and other objects do.
///
/// The spot is tried at a short ladder of heights around the feet, the first
/// that fits winning: a hand's width up, then climbing in 10 cm steps to 70 cm
/// for ground that rises ahead, then dropping in about 19 cm steps to 66 cm
/// below for ground that falls away or a low ceiling. See the research note
/// on pet devices and the summon-room check.
/// </summary>
internal static class PlacementRoomProbe
{
    /// <summary>The farthest ahead a check may look, in metres.</summary>
    internal const float MaximumDistanceMeters = 10f;

    /// <summary>
    /// Heights above the standing body's feet the spot is tried at, in
    /// metres, in order.
    /// </summary>
    internal static readonly ImmutableArray<float> Lifts =
    [
        0.1f,
        0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f,
        -0.09075f, -0.2815f, -0.47225f, -0.663f,
    ];

    /// <summary>
    /// Looks <paramref name="distanceMeters"/> ahead of a body standing at
    /// <paramref name="position"/> (world space) in
    /// <paramref name="cellId"/> at <paramref name="cellLocalPosition"/>,
    /// facing along <paramref name="orientation"/>.
    /// </summary>
    internal static PlacementRoomResult Check(
        PhysicsEngine engine,
        in PlacementRoomBody body,
        Vector3 position,
        Quaternion orientation,
        uint cellId,
        Vector3 cellLocalPosition,
        float distanceMeters)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (cellId == 0u
            || body.Spheres.IsDefaultOrEmpty
            || !(distanceMeters > 0f)
            || distanceMeters > MaximumDistanceMeters)
        {
            return new PlacementRoomResult(PlacementRoom.Unknown);
        }

        Vector3 forward = Vector3.Transform(Vector3.UnitY, orientation);
        forward.Z = 0f;
        if (forward.LengthSquared() < PhysicsGlobals.EpsilonSq)
            return new PlacementRoomResult(PlacementRoom.Unknown);
        Vector3 ahead = Vector3.Normalize(forward) * distanceMeters;

        foreach (float lift in Lifts)
        {
            Vector3 offset = ahead + new Vector3(0f, 0f, lift);
            var request = new PhysicsSetPositionRequest(
                position + offset,
                orientation,
                cellId,
                cellLocalPosition + offset,
                body.Spheres,
                body.Scale,
                body.StepUpHeight,
                body.StepDownHeight,
                MoverFlags: ObjectInfoState.IgnoreCreatures,
                MovingEntityId: body.SelfEntityId,
                Flags: PhysicsSetPositionFlags.Placement);
            PhysicsSetPositionResult result = engine.SetPosition(request);
            if (result.Residence == PhysicsResidenceDisposition.DeferredCell)
                return new PlacementRoomResult(PlacementRoom.Unknown);
            if (result.IsCommitted)
            {
                return new PlacementRoomResult(
                    PlacementRoom.Clear,
                    result.CellId,
                    result.CellLocalPosition,
                    result.Position);
            }
        }
        return new PlacementRoomResult(PlacementRoom.Blocked);
    }
}
