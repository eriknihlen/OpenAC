using System.Numerics;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Entities;

internal readonly record struct RuntimeRemoteBodyConstructionReceipt(
    uint MotionTableId,
    bool MotionTableGatePassed,
    bool MovementBranch,
    bool LastMoveWasAutonomous,
    bool PlacementFrameStaged,
    float Friction,
    bool FrictionApplied,
    float Elasticity,
    float TranslucencyOriginal,
    bool TranslucencyApplied,
    /// <summary>True when a present, finite wire velocity was applied via set_velocity.</summary>
    bool VelocityApplied,
    /// <summary>True when a present, finite wire omega was written (raw field, no setter).</summary>
    bool OmegaApplied);

internal static class RuntimeRemoteBodyDescription
{
    private const float DefaultDescFriction = 0.95f;

    private const float DefaultDescElasticity = 0.05f;

    private const float MaxElasticity = 0.1f;

    internal static PhysicsBody Construct(
        RuntimeEntityRecord record,
        PhysicsSpawnData? description,
        in RuntimeSetPositionCommand preparedCommand,
        out RuntimeRemoteBodyConstructionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(record);
        var body = new PhysicsBody();

        uint motionTableId = description?.MotionTableId ?? 0u;
        const bool motionTableGatePassed = true;

        // 2./3. Sound table + physics-script table (steps 2-3) — snapshot/
        // presentation-side; deliberately untouched here.

        bool movementBranch = description?.Movement is { } movementData
            && !movementData.RawData.IsEmpty;
        bool lastMoveWasAutonomous = false;
        bool placementFrameStaged = false;
        if (movementBranch)
        {
            lastMoveWasAutonomous =
                description!.Value.Movement!.Value.IsAutonomous ?? false;
            body.LastMoveWasAutonomous = lastMoveWasAutonomous;
        }
        else
        {
            body.Orientation = preparedCommand.Physics.Orientation;
            body.StageDormantCellFrame(
                preparedCommand.Physics.CellId,
                preparedCommand.Physics.Position,
                preparedCommand.Physics.CellLocalPosition);
            placementFrameStaged = true;
        }

        body.State = record.FinalPhysicsState;


        float friction = description?.Friction ?? DefaultDescFriction;
        bool frictionApplied = friction >= 0.0f && friction <= 1.0f;
        if (frictionApplied)
            body.Friction = friction;

        float elasticity = description?.Elasticity ?? DefaultDescElasticity;
        body.Elasticity = !(elasticity >= 0f)
            ? 0f
            : elasticity <= MaxElasticity
                ? elasticity
                : MaxElasticity;

        float translucency = description?.Translucency ?? 0f;
        bool translucencyApplied = translucency != 0.0f;

        bool velocityApplied = false;
        if (description?.Velocity is { } velocity && IsFinite(velocity))
        {
            body.set_velocity(velocity);
            velocityApplied = true;
        }

        bool omegaApplied = false;
        if (description?.AngularVelocity is { } omega && IsFinite(omega))
        {
            body.Omega = omega;
            omegaApplied = true;
        }


        receipt = new RuntimeRemoteBodyConstructionReceipt(
            motionTableId,
            motionTableGatePassed,
            movementBranch,
            lastMoveWasAutonomous,
            placementFrameStaged,
            body.Friction,
            frictionApplied,
            body.Elasticity,
            translucency,
            translucencyApplied,
            velocityApplied,
            omegaApplied);
        return body;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z);
}
