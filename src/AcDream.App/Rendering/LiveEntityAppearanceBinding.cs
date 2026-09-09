using System.Numerics;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime.Physics;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Rendering;

internal sealed record LiveEntityAppearanceUpdateState(
    WorldEntity Entity,
    LiveEntityAnimationState? Animation);

internal sealed record LiveEntityAppearanceCollisionUpdate(
    LiveEntityRecord Record,
    WorldEntity Entity,
    LiveEntityCollisionRegistration? Registration);

internal static class LiveEntityAppearanceBinding
{
    public static LiveEntityAppearanceUpdateState? Capture(
        LiveEntityRuntime runtime,
        uint serverGuid)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!runtime.TryGetRecord(serverGuid, out LiveEntityRecord record)
            || record.WorldEntity is not { } entity)
        {
            return null;
        }

        return new LiveEntityAppearanceUpdateState(
            entity,
            record.AnimationRuntime as LiveEntityAnimationState);
    }

    public static void RebindAnimation(
        LiveEntityAnimationState animation,
        WorldEntity entity,
        Setup setup,
        float scale,
        IReadOnlyList<LiveAnimationPartTemplate> partTemplate,
        IReadOnlyList<bool> partAvailability)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(partTemplate);
        ArgumentNullException.ThrowIfNull(partAvailability);

        animation.Entity = entity;
        animation.Setup = setup;
        animation.Scale = scale;
        animation.PartTemplate = partTemplate;
        animation.PartAvailability = partAvailability;
        animation.InvalidatePresentationPoses();
    }

    public static LiveEntityAppearanceCollisionUpdate? PrepareCollision(
        LiveEntityRuntime runtime,
        LiveEntityCollisionBuilder builder,
        WorldEntity entity,
        Setup setup,
        IReadOnlyList<uint> effectivePartGfxObjIds,
        WorldSession.EntitySpawn spawn,
        Vector3 worldOrigin)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(effectivePartGfxObjIds);
        if (!runtime.TryGetRecord(spawn.Guid, out LiveEntityRecord record)
            || !ReferenceEquals(record.WorldEntity, entity))
        {
            return null;
        }

        return new LiveEntityAppearanceCollisionUpdate(
            record,
            entity,
            builder.Build(
                entity,
                setup,
                effectivePartGfxObjIds,
                spawn,
                record.ServerGuid,
                record.Generation,
                record.WorldEntity!,
                record.FinalPhysicsState,
                worldOrigin,
                retainEmptyPayload: true));
    }

    public static bool CommitCollision(
        LiveEntityRuntime runtime,
        ShadowObjectRegistry registry,
        LiveEntityAppearanceCollisionUpdate update)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(update);
        if (!runtime.TryGetRecord(update.Record.ServerGuid, out LiveEntityRecord current)
            || !ReferenceEquals(current, update.Record)
            || !ReferenceEquals(current.WorldEntity, update.Entity))
        {
            return false;
        }

        LiveEntityCollisionBuilder.ReconcileAppearance(
            registry,
            update.Entity.Id,
            update.Registration,
            suspendIfNew: !current.IsSpatiallyVisible
                || current.ProjectionKind is not LiveEntityProjectionKind.World
                || (current.FinalPhysicsState & PhysicsStateFlags.Hidden) != 0);
        return true;
    }
}
