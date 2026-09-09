using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Physics;

internal enum RuntimeSetPositionMoverPreparationStatus
{
    Prepared,

    RetrySetupUnavailable,

    RetryWorldFrameUnavailable,

    RejectedAuthority,
    InvalidData,
}

internal static class RuntimeSetPositionMoverPreparationStatusExtensions
{
    internal static bool IsRetryable(
        this RuntimeSetPositionMoverPreparationStatus status) =>
        status is RuntimeSetPositionMoverPreparationStatus
                .RetrySetupUnavailable
            or RuntimeSetPositionMoverPreparationStatus
                .RetryWorldFrameUnavailable;

    internal static RuntimeSetPositionParkReason ParkReason(
        this RuntimeSetPositionMoverPreparationStatus status) =>
        status switch
        {
            RuntimeSetPositionMoverPreparationStatus.RetrySetupUnavailable =>
                RuntimeSetPositionParkReason.AwaitingSetupCollision,
            RuntimeSetPositionMoverPreparationStatus
                .RetryWorldFrameUnavailable =>
                RuntimeSetPositionParkReason.AwaitingWorldFrame,
            _ => RuntimeSetPositionParkReason.None,
        };
}

internal enum RuntimeSetPositionParkReason
{
    None,
    AwaitingSetupCollision,
    AwaitingWorldFrame,
}

internal readonly record struct RuntimeSetPositionMoverSetup(
    bool IsResolved,
    uint SetupTableId,
    FlatSetupCollision? Collision)
{
    internal static RuntimeSetPositionMoverSetup Unavailable => default;

    internal static RuntimeSetPositionMoverSetup ResolvedAbsent =>
        new(true, 0u, null);

    internal static RuntimeSetPositionMoverSetup Resolved(
        uint setupTableId,
        FlatSetupCollision collision) =>
        new(
            true,
            setupTableId != 0u
                ? setupTableId
                : throw new ArgumentOutOfRangeException(nameof(setupTableId)),
            collision ?? throw new ArgumentNullException(nameof(collision)));
}

internal readonly record struct RuntimeSetPositionMoverPreparation(
    RuntimeSetPositionMoverSetup Setup,
    RuntimeSetPositionOperationKind Kind,
    double GameTime,
    PhysicsPlacementClass PlacementClass,
    PhysicsSetPositionFlags Flags,
    Vector3 Line = default,
    float ScatterRadiusX = 0f,
    float ScatterRadiusY = 0f,
    uint ScatterAttempts = 0u,
    float ShadowWorldOffsetX = 0f,
    float ShadowWorldOffsetY = 0f,
    RuntimePortalPlacementAuthority Portal = default,
    bool ResolveWorldOffsetFromRuntimeFrame = false);

internal static class RuntimeSetPositionMoverPreparer
{
    internal static bool TryBuild(
        RuntimeEntityRecord record,
        in CreateObject.ServerPosition acceptedPosition,
        uint canonicalSetupTableId,
        RuntimeSetPositionOperationKind acceptedKind,
        RuntimePortalPlacementAuthority acceptedPortal,
        ulong velocityAuthorityVersion,
        in RuntimeSetPositionMoverPreparation preparation,
        out RuntimeSetPositionCommand command)
    {
        ArgumentNullException.ThrowIfNull(record);
        command = default;
        if (!preparation.Setup.IsResolved
            || preparation.Kind != acceptedKind
            || preparation.Portal != acceptedPortal
            || (canonicalSetupTableId == 0u
                ? preparation.Setup.SetupTableId != 0u
                    || preparation.Setup.Collision is not null
                : preparation.Setup.SetupTableId != canonicalSetupTableId
                    || preparation.Setup.Collision is null))
        {
            return false;
        }

        CreateObject.ServerPosition position = acceptedPosition;
        Vector3 cellLocal = new(
            position.PositionX,
            position.PositionY,
            position.PositionZ);
        Vector3 world = new(
            cellLocal.X + preparation.ShadowWorldOffsetX,
            cellLocal.Y + preparation.ShadowWorldOffsetY,
            cellLocal.Z);
        Quaternion orientation = new(
            position.RotationX,
            position.RotationY,
            position.RotationZ,
            position.RotationW);

        float scale = record.Snapshot.Physics?.Scale
            ?? record.Snapshot.ObjScale
            ?? 1f;
        FlatSetupCollision? setup = preparation.Setup.Collision;
        ImmutableArray<FlatCollisionSphere> spheres = setup?.Spheres
            ?? ImmutableArray<FlatCollisionSphere>.Empty;
        float stepUp = setup is not null ? setup.StepUpHeight * scale : 0f;
        float stepDown = setup is not null ? setup.StepDownHeight * scale : 0f;

        EntityCollisionFlags collisionFlags =
            EntityCollisionFlagsExt.FromPwdBitfield(
                record.Snapshot.ObjectDescriptionFlags ?? 0u);
        ObjectInfoState moverFlags = collisionFlags.ToMoverState();
        if (collisionFlags.HasFlag(EntityCollisionFlags.IsPlayer))
            moverFlags |= ObjectInfoState.IsPlayer;

        var request = new PhysicsSetPositionRequest(
            world,
            orientation,
            position.LandblockId,
            cellLocal,
            spheres,
            scale,
            stepUp,
            stepDown,
            record.FinalPhysicsState,
            moverFlags,
            record.Key?.LocalEntityId ?? 0u,
            preparation.PlacementClass,
            preparation.Flags,
            preparation.Line,
            preparation.ScatterRadiusX,
            preparation.ScatterRadiusY,
            preparation.ScatterAttempts);
        command = new RuntimeSetPositionCommand(
            request,
            preparation.Kind,
            preparation.GameTime,
            velocityAuthorityVersion,
            preparation.ShadowWorldOffsetX,
            preparation.ShadowWorldOffsetY,
            preparation.Portal);
        return true;
    }
}
