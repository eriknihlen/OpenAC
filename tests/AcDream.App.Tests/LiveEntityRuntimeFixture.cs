using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;

namespace AcDream.App.Tests;

internal static class LiveEntityRuntimeFixture
{
    private static RuntimeEntityObjectLifetime WithGeneration(
        RuntimeEntityObjectLifetime lifetime)
    {
        lifetime.BindEventContext(
            static () => new AcDream.Runtime.RuntimeGenerationToken(1UL),
            static () => 1UL);
        return lifetime;
    }

    public static LiveEntityRuntime Create(
        GpuWorldState spatial,
        ILiveEntityResourceLifecycle resources,
        uint firstLocalEntityId = RuntimeEntityDirectory.FirstLocalEntityId)
    {
        var lifetime = WithGeneration(
            new RuntimeEntityObjectLifetime(firstLocalEntityId));
        return new LiveEntityRuntime(spatial, resources, lifetime);
    }

    internal sealed class DrivenLiveEntityRuntime
    {
        internal required LiveEntityRuntime Runtime { get; init; }
        internal required RuntimeEntityObjectLifetime Lifetime { get; init; }
        internal required AcDream.Runtime.Session.RuntimeFirstEntryDriveController
            FirstEntry { get; init; }
        internal required AcDream.Runtime.Physics
            .RuntimePlacementProjectionSubscription Subscription { get; init; }

        internal void Pump() => FirstEntry.DriveAll();
    }

    public static DrivenLiveEntityRuntime CreateDriven(
        GpuWorldState spatial,
        ILiveEntityResourceLifecycle resources,
        uint landblockId = 0x01010000u)
    {
        var lifetime = WithGeneration(new RuntimeEntityObjectLifetime());
        lifetime.Physics.SetPosition.BeginCollisionGeneration(
            landblockId & 0xFFFF0000u, 1UL);
        lifetime.Physics.Engine.AddLandblock(
            landblockId & 0xFFFF0000u,
            new AcDream.Core.Physics.TerrainSurface(
                new byte[81], new float[256]),
            Array.Empty<AcDream.Core.Physics.CellSurface>(),
            Array.Empty<AcDream.Core.Physics.PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        lifetime.Physics.SetPosition.CommitCollisionGeneration(
            landblockId & 0xFFFF0000u, 1UL, ready: true);

        lifetime.Physics.ObserveLocalWorldFrame(
            (landblockId & 0xFFFF0000u) | 0x0001u,
            teleportAdvanced: false);
        var runtime = new LiveEntityRuntime(spatial, resources, lifetime);
        var movement = new AcDream.Runtime.Gameplay
            .RuntimeLocalPlayerMovementState();
        var identity = new AcDream.Runtime.Gameplay
            .RuntimeLocalPlayerIdentityState();
        var publication = new AcDream.Runtime.Gameplay
            .RuntimeLocalPlayerPhysicsPublicationState(
                lifetime.Entities,
                lifetime.Physics,
                movement,
                identity);
        movement.AttachPhysicsPublication(publication);
        lifetime.LocalPlayerFirstEntry.BindPublication(publication);
        var firstEntry = new AcDream.Runtime.Session
            .RuntimeFirstEntryDriveController(
                lifetime,
                new AcDream.Runtime.GameRuntimeClock(),
                new SphereCollisionSource(),
                static () => AcDream.Runtime.Gameplay
                    .PlayerMovementConstructionOptions.Fallback,
                static _ => new AcDream.Runtime.Gameplay
                    .RuntimeLocalPlayerPhysicsActivationPreparation(
                        0.48f,
                        1.835f,
                        AcDream.Runtime.Gameplay
                            .RuntimeLocalPlayerShadowDisposition
                            .ProvenShapeless));
        var subscription = new AcDream.Runtime.Physics
            .RuntimePlacementProjectionSubscription(
                lifetime.Placements,
                static () => new AcDream.Runtime.RuntimeGenerationToken(1UL),
                new AckOnlyPlacementSink(runtime));

        return new DrivenLiveEntityRuntime
        {
            Runtime = runtime,
            Lifetime = lifetime,
            FirstEntry = firstEntry,
            Subscription = subscription,
        };
    }

    private sealed class AckOnlyPlacementSink(LiveEntityRuntime runtime)
        : AcDream.Runtime.Physics.IRuntimePlacementProjectionSink
    {
        public bool TryApply(
            in AcDream.Runtime.Physics.RuntimePlacementProjectionSnapshot
                projection)
        {
            if (projection.Kind is AcDream.Runtime.Physics
                    .RuntimePlacementProjectionKind.Discard)
            {
                return true;
            }
            if (projection.Kind is AcDream.Runtime.Physics
                    .RuntimePlacementProjectionKind.ExecutorCompleted)
            {
                return projection.Token.ExactCellId == 0u
                    || runtime.TryApplyInitialCreateCompletionPresentation(
                        in projection);
            }
            return !runtime.HasActiveInitialCreateResidence(
                    projection.Token.Entity)
                && runtime.TryApplyRuntimePlacementProjection(in projection);
        }
    }

    private sealed class SphereCollisionSource
        : AcDream.Content.IPreparedCollisionSource
    {
        public AcDream.Content.PreparedAssetPresence ProbeCollision(
            AcDream.Content.Pak.PakAssetType type,
            uint sourceFileId) =>
            AcDream.Content.PreparedAssetPresence.Available;

        public AcDream.Content.PreparedCollisionReadResult<
            AcDream.Core.Physics.FlatSetupCollision> ReadSetupCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            AcDream.Content.PreparedCollisionReadResult<
                AcDream.Core.Physics.FlatSetupCollision>.Loaded(
                    new AcDream.Core.Physics.FlatSetupCollision(
                        System.Collections.Immutable.ImmutableArray<
                            AcDream.Core.Physics.FlatCollisionCylinder>.Empty,
                        [new AcDream.Core.Physics.FlatCollisionSphere(
                            System.Numerics.Vector3.Zero, 0.48f)],
                        height: 0f,
                        radius: 0f,
                        stepUpHeight: 0.4f,
                        stepDownHeight: 0.4f));

        public AcDream.Content.PreparedCollisionReadResult<
            AcDream.Core.Physics.FlatGfxObjCollisionAsset>
            ReadGfxObjCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionReadResult<
            AcDream.Core.Physics.FlatCellStructureCollisionAsset>
            ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionReadResult<
            AcDream.Core.Physics.FlatEnvCellTopology> ReadEnvCellTopology(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public AcDream.Content.PreparedCollisionSourceStats CollisionStats =>
            default;

        public void Dispose()
        {
        }
    }

    public static LiveEntityRuntime Create(
        GpuWorldState spatial,
        ILiveEntityResourceLifecycle resources,
        PhysicsEngine physicsEngine,
        uint firstLocalEntityId = RuntimeEntityDirectory.FirstLocalEntityId)
    {
        var lifetime = WithGeneration(new RuntimeEntityObjectLifetime(
            physicsEngine,
            firstLocalEntityId));
        return new LiveEntityRuntime(spatial, resources, lifetime);
    }

    public static LiveEntityRuntime Create(
        GpuWorldState spatial,
        ILiveEntityResourceLifecycle resources,
        Action<LiveEntityRecord> tearDownRuntimeComponents,
        uint firstLocalEntityId = RuntimeEntityDirectory.FirstLocalEntityId)
    {
        var lifetime = WithGeneration(
            new RuntimeEntityObjectLifetime(firstLocalEntityId));
        return new LiveEntityRuntime(
            spatial,
            resources,
            tearDownRuntimeComponents,
            lifetime);
    }

    public static LiveEntityRuntime Create(
        GpuWorldState spatial,
        ILiveEntityResourceLifecycle resources,
        ILiveEntityRuntimeComponentLifecycle runtimeComponentLifecycle,
        uint firstLocalEntityId = RuntimeEntityDirectory.FirstLocalEntityId)
    {
        var lifetime = WithGeneration(
            new RuntimeEntityObjectLifetime(firstLocalEntityId));
        return new LiveEntityRuntime(
            spatial,
            resources,
            runtimeComponentLifecycle,
            lifetime);
    }

    public static LiveEntityRuntime Create(
        GpuWorldState spatial,
        ILiveEntityResourceLifecycle resources,
        ILiveEntityRuntimeComponentLifecycle runtimeComponentLifecycle,
        PhysicsEngine physicsEngine,
        uint firstLocalEntityId = RuntimeEntityDirectory.FirstLocalEntityId)
    {
        var lifetime = WithGeneration(new RuntimeEntityObjectLifetime(
            physicsEngine,
            firstLocalEntityId));
        return new LiveEntityRuntime(
            spatial,
            resources,
            runtimeComponentLifecycle,
            lifetime);
    }
}
