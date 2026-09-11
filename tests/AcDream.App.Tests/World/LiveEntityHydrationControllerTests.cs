using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.App.Streaming;
using AcDream.App.UI;
using AcDream.App.World;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.World;

public sealed class LiveEntityHydrationControllerTests
{
    [Fact]
    public void ParentAcceptanceBridge_IsFailFastAndSingleAssignment()
    {
        var bridge = new DeferredLiveEntityParentAcceptance();
        var update = new ParentEvent.Parsed(1, 2, 3, 4, 5, 6);
        Assert.Throws<InvalidOperationException>(() => bridge.TryAccept(update));

        ParentEvent.Parsed accepted = default;
        bridge.Bind(value =>
        {
            accepted = value;
            return true;
        });

        Assert.True(bridge.TryAccept(update));
        Assert.Equal(update, accepted);
        Assert.Throws<InvalidOperationException>(() => bridge.Bind(_ => false));
    }

    [Fact]
    public void ParentAcceptanceOwnedBindingReleasesExactlyAndAllowsRebind()
    {
        var bridge = new DeferredLiveEntityParentAcceptance();
        Func<ParentEvent.Parsed, bool> first = _ => true;
        Func<ParentEvent.Parsed, bool> second = _ => false;

        IDisposable firstBinding = bridge.BindOwned(first);
        Assert.True(bridge.TryAccept(default));
        Assert.Throws<InvalidOperationException>(() => bridge.BindOwned(second));

        firstBinding.Dispose();
        using IDisposable secondBinding = bridge.BindOwned(second);
        firstBinding.Dispose();

        Assert.False(bridge.TryAccept(default));
    }

    [Fact]
    public void FreshCreate_PublishesCanonicalOrderAndReadyAfterMaterialization()
    {
        using var fixture = new Fixture(originKnown: true);
        WorldSession.EntitySpawn spawn = Spawn(Generation: 1, PositionSequence: 1);
        fixture.Materializer.BeforeMaterialize = (_, _) =>
            Assert.NotNull(fixture.Objects.Get(Guid));

        fixture.Controller.OnCreate(spawn);

        Assert.Equal(
            ["timestamps:1", "relationship:1", "materialize:LogicalRegistration:1", "ready"],
            fixture.Operations);
        Assert.True(fixture.Runtime.TryGetRecord(Guid, out LiveEntityRecord record));
        Assert.NotNull(record.WorldEntity);
        Assert.True(record.InitialHydrationCompleted);
        Assert.Equal(1, fixture.Resources.RegisterCount);
    }

    [Fact]
    public void UnknownOrigin_DefersFirstMaterializationUntilLandblockRecovery()
    {
        using var fixture = new Fixture(originKnown: false);

        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));

        RuntimeEntityRecord canonical = fixture.Canonical;
        Assert.False(fixture.Runtime.TryGetRecord(Guid, out _));
        Assert.NotNull(canonical.LocalEntityId);
        Assert.Equal(0, fixture.Resources.RegisterCount);
        Assert.Empty(fixture.Materializer.Calls);

        fixture.Origin.IsKnownValue = true;
        fixture.Controller.OnLandblockLoaded(Cell);

        LiveEntityRecord record = fixture.Record;
        Assert.NotNull(record.WorldEntity);
        Assert.Single(fixture.Materializer.Calls);
        Assert.Equal(LiveProjectionPurpose.SpatialRecovery, fixture.Materializer.Calls[0].Purpose);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(1, fixture.Ready.PublishCount);
    }

    [Fact]
    public void InitialPlayerOriginRecovery_DoesNotReenterSameHydrationTransaction()
    {
        using var fixture = new Fixture(originKnown: true, playerGuid: Guid);
        fixture.Origin.AlreadyLoadedLandblocks = [Cell];

        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));

        Assert.True(fixture.Record.InitialHydrationCompleted);
        Assert.Single(fixture.Materializer.Calls);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(1, fixture.Ready.PublishCount);
    }

    [Fact]
    public void LoadedCallback_RebucketsMaterializedIdentityWithoutReactivation()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord record = fixture.Record;
        WorldEntity entity = record.WorldEntity!;

        fixture.Controller.OnLandblockLoaded(Cell);

        Assert.Same(entity, record.WorldEntity);
        Assert.Single(fixture.Materializer.Calls);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(1, fixture.Ready.PublishCount);
    }

    [Theory]
    [InlineData(0x50000123u, (ushort)7)]
    [InlineData(0x70000099u, (ushort)0)]
    public void CommittedChild_IsExcludedFromLandblockLoadCandidates(
        uint parentGuid,
        ushort parentInstanceSequence)
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord record = fixture.Record;
        WorldEntity entity = record.WorldEntity!;

        var relation = new ParentAttachmentRelation(
            parentGuid,
            Guid,
            ParentLocation: 0,
            PlacementId: 0,
            parentInstanceSequence,
            ChildPositionSequence: 1);
        fixture.Runtime.ParentAttachments.AcceptCreateObjectRelation(relation);
        Assert.True(fixture.Runtime.ParentAttachments.CommitProjection(relation));

        entity.ParentCellId = 0x02020002u;

        fixture.Controller.OnLandblockLoaded(Cell);

        Assert.Equal(0x02020002u, entity.ParentCellId);
    }

    [Fact]
    public void AppearanceAfterDeferredProjection_FirstMaterializesAndPublishesReady()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Materializer.SkipMaterializationCount = 1;
        WorldSession.EntitySpawn spawn = Spawn(Generation: 1, PositionSequence: 1);
        fixture.Controller.OnCreate(spawn);
        RuntimeEntityRecord canonical = fixture.Canonical;
        Assert.False(fixture.Runtime.TryGetRecord(Guid, out _));
        Assert.NotNull(canonical.LocalEntityId);

        Assert.True(fixture.Controller.OnAppearance(ObjDesc(2, 0x04000022u)));

        LiveEntityRecord record = fixture.Record;
        Assert.NotNull(record.WorldEntity);
        Assert.Equal(2, fixture.Materializer.Calls.Count);
        Assert.Equal(LiveProjectionPurpose.SpatialRecovery, fixture.Materializer.Calls[1].Purpose);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(1, fixture.Ready.PublishCount);
    }

    [Fact]
    public void AppearanceUpdate_ReplacesOnlyProjectionAndPreservesExactOwners()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord record = fixture.Record;
        WorldEntity entity = record.WorldEntity!;
        int applied = 0;
        fixture.Controller.AppearanceApplied += _ => applied++;

        Assert.True(fixture.Controller.OnAppearance(ObjDesc(2, 0x04000022u)));

        Assert.Same(record, fixture.Record);
        Assert.Same(entity, record.WorldEntity);
        Assert.Equal((uint)0x04000022u, record.Snapshot.BasePaletteId);
        Assert.False(record.AppearanceProjectionSynchronizationPending);
        Assert.Equal(LiveProjectionPurpose.AppearanceMutation, fixture.Materializer.Calls[^1].Purpose);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(0, fixture.Resources.UnregisterCount);
        Assert.Equal(1, applied);
    }

    [Fact]
    public void NestedAppearanceUpdate_ReplaysNewestCanonicalSnapshotAfterOuterPublication()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        int applied = 0;
        fixture.Controller.AppearanceApplied += _ => applied++;
        bool nested = false;
        bool nestedResult = true;
        fixture.Materializer.BeforeMaterialize = (_, _) =>
        {
            if (fixture.Materializer.Calls[^1].Purpose is not LiveProjectionPurpose.AppearanceMutation
                || nested)
            {
                return;
            }

            nested = true;
            nestedResult = fixture.Controller.OnAppearance(ObjDesc(3, 0x04000033u));
        };

        Assert.True(fixture.Controller.OnAppearance(ObjDesc(2, 0x04000022u)));

        Assert.False(nestedResult);
        Assert.Equal((uint)0x04000033u, fixture.Record.Snapshot.BasePaletteId);
        Assert.False(fixture.Record.AppearanceProjectionSynchronizationPending);
        Assert.Equal(
            2,
            fixture.Materializer.Calls.Count(call =>
                call.Purpose is LiveProjectionPurpose.AppearanceMutation));
        Assert.Equal(1, applied);
    }

    [Fact]
    public void AppearancePublicationFailure_RetainsObligationForLandblockRetry()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        int applied = 0;
        fixture.Controller.AppearanceApplied += _ => applied++;
        fixture.Materializer.ThrowAfterMaterializePurposeOnce =
            LiveProjectionPurpose.AppearanceMutation;

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Controller.OnAppearance(ObjDesc(2, 0x04000022u)));
        Assert.True(fixture.Record.AppearanceProjectionSynchronizationPending);
        Assert.Equal(0, applied);

        fixture.Controller.OnLandblockLoaded(Cell);

        Assert.False(fixture.Record.AppearanceProjectionSynchronizationPending);
        Assert.Equal((uint)0x04000022u, fixture.Record.Snapshot.BasePaletteId);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(1, applied);
    }

    [Fact]
    public void AttachedAppearance_UsesRelationshipProjectionWithoutWorldPosition()
    {
        const uint parentGuid = 0x70000002u;
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord record = fixture.Record;
        WorldEntity entity = record.WorldEntity!;
        Assert.True(fixture.Runtime.TryApplyCreateParent(
            new CreateParentUpdate(Guid, parentGuid, 1, 0, 1, 2),
            out _));
        Assert.True(fixture.Runtime.CommitStagedParent(
            new ParentAttachmentRelation(
                parentGuid,
                Guid,
                ParentLocation: 1,
                PlacementId: 0,
                ParentInstanceSequence: 0,
                ChildPositionSequence: 2),
            out _));
        record.ProjectionKind = LiveEntityProjectionKind.Attached;
        record.FullCellId = 0u;
        int materializerCalls = fixture.Materializer.Calls.Count;
        int relationshipCalls = 0;
        int applied = 0;
        fixture.Relationships.ApplyAttachedAppearance = (expected, version) =>
        {
            relationshipCalls++;
            Assert.Same(record, expected);
            Assert.True(fixture.Runtime.IsCurrentObjDescAuthority(expected, version));
            Assert.Null(expected.Snapshot.Position);
            Assert.Equal((uint)0x04000022u, expected.Snapshot.BasePaletteId);
            return true;
        };
        fixture.Controller.AppearanceApplied += _ => applied++;

        Assert.True(fixture.Controller.OnAppearance(ObjDesc(2, 0x04000022u)));

        Assert.Same(entity, record.WorldEntity);
        Assert.Equal(LiveEntityProjectionKind.Attached, record.ProjectionKind);
        Assert.False(record.AppearanceProjectionSynchronizationPending);
        Assert.Equal(materializerCalls, fixture.Materializer.Calls.Count);
        Assert.Equal(1, relationshipCalls);
        Assert.Equal(1, applied);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void StaleOrEqualAppearance_HasNoProjectionOrNotificationSideEffects(
        ushort sequence)
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        int calls = fixture.Materializer.Calls.Count;
        int applied = 0;
        fixture.Controller.AppearanceApplied += _ => applied++;

        Assert.False(fixture.Controller.OnAppearance(
            ObjDesc(sequence, 0x04000022u)));

        Assert.Equal(calls, fixture.Materializer.Calls.Count);
        Assert.Equal(0, applied);
        Assert.False(fixture.Record.AppearanceProjectionSynchronizationPending);
        Assert.Null(fixture.Record.Snapshot.BasePaletteId);
    }

    [Fact]
    public void AppearanceSequenceWrap_PublishesExactlyOnce()
    {
        using var fixture = new Fixture(originKnown: true);
        WorldSession.EntitySpawn spawn = Spawn(Generation: 1, PositionSequence: 1);
        spawn = spawn with
        {
            Physics = spawn.Physics!.Value with
            {
                Timestamps = spawn.Physics.Value.Timestamps with
                {
                    ObjDesc = 0xFFFF,
                },
            },
        };
        fixture.Controller.OnCreate(spawn);
        int applied = 0;
        fixture.Controller.AppearanceApplied += _ => applied++;

        Assert.True(fixture.Controller.OnAppearance(ObjDesc(0, 0x04000022u)));

        Assert.False(fixture.Record.AppearanceProjectionSynchronizationPending);
        Assert.Equal((uint)0x04000022u, fixture.Record.Snapshot.BasePaletteId);
        Assert.Equal(1, applied);
    }

    [Fact]
    public void FailedAppearance_IsClearedAtGenerationReplacementAndCannotLeak()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord retired = fixture.Record;
        fixture.Materializer.SkipMaterializationCount = 1;
        Assert.False(fixture.Controller.OnAppearance(ObjDesc(2, 0x04000022u)));
        Assert.True(retired.AppearanceProjectionSynchronizationPending);

        fixture.Controller.OnCreate(Spawn(Generation: 2, PositionSequence: 1));
        LiveEntityRecord replacement = fixture.Record;
        fixture.Controller.OnLandblockLoaded(Cell);

        Assert.NotSame(retired, replacement);
        Assert.False(retired.AppearanceProjectionSynchronizationPending);
        Assert.False(retired.AppearanceHydrationInProgress);
        Assert.False(replacement.AppearanceProjectionSynchronizationPending);
        Assert.Null(replacement.Snapshot.BasePaletteId);
    }

    [Fact]
    public void Pickup_LeavesWorldWithoutEndingLogicalOrResourceIdentity()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord record = fixture.Record;
        WorldEntity entity = record.WorldEntity!;
        fixture.Relationships.OnUnparentAction = _ =>
            fixture.Runtime.WithdrawLiveEntityProjection(record)
                ? ChildUnparentDisposition.Completed
                : ChildUnparentDisposition.Superseded;

        Assert.True(fixture.Controller.OnPickup(
            new PickupEvent.Parsed(Guid, 1, 2)));

        Assert.Same(record, fixture.Record);
        Assert.Same(entity, record.WorldEntity);
        Assert.Equal(0u, record.FullCellId);
        Assert.False(record.IsSpatiallyProjected);
        Assert.True(record.ResourcesRegistered);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(0, fixture.Resources.UnregisterCount);
        Assert.Contains($"unparent:{Guid:X8}", fixture.Operations);
    }

    [Fact]
    public void PositionAfterPickup_ReentersWithSameEntityBodyAndResources()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord record = fixture.Record;
        WorldEntity entity = record.WorldEntity!;
        PhysicsBody body = fixture.Runtime.GetOrCreatePhysicsBody(
            record.ServerGuid,
            static _ => throw new InvalidOperationException(
                "The conductor-built canonical body should already exist."));
        fixture.Relationships.OnUnparentAction = _ =>
            fixture.Runtime.WithdrawLiveEntityProjection(record)
                ? ChildUnparentDisposition.Completed
                : ChildUnparentDisposition.Superseded;
        Assert.True(fixture.Controller.OnPickup(
            new PickupEvent.Parsed(Guid, 1, 2)));
        int applied = 0;
        fixture.Controller.AppearanceApplied += _ => applied++;
        Assert.False(fixture.Controller.OnAppearance(ObjDesc(2, 0x04000022u)));
        Assert.True(record.AppearanceProjectionSynchronizationPending);
        Assert.Equal(0, applied);

        Assert.True(fixture.Runtime.TryApplyPosition(
            new WorldSession.EntityPositionUpdate(
                Guid,
                new CreateObject.ServerPosition(
                    Cell, 14f, 12f, 6f, 1f, 0f, 0f, 0f),
                Velocity: null,
                PlacementId: 0,
                IsGrounded: true,
                InstanceSequence: 1,
                PositionSequence: 3,
                TeleportSequence: 0,
                ForcePositionSequence: 0),
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            out _,
            out WorldSession.EntitySpawn accepted,
            out _));
        ulong positionAuthority = record.PositionAuthorityVersion;

        Assert.True(fixture.Controller.RecoverProjection(
            record,
            positionAuthority,
            accepted));

        Assert.True(fixture.Runtime.RebucketLiveEntity(Guid, Cell));

        Assert.Same(entity, record.WorldEntity);
        Assert.Same(body, record.PhysicsBody);
        Assert.True(record.IsSpatiallyProjected);
        Assert.Equal(Cell, record.Canonical.FullCellId);
        Assert.False(record.AppearanceProjectionSynchronizationPending);
        Assert.Equal((uint)0x04000022u, record.Snapshot.BasePaletteId);
        Assert.Equal(1, applied);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(0, fixture.Resources.UnregisterCount);
    }

    [Fact]
    public void PositionAfterInventoryOnlyCreate_ConstructsFirstSpatialProjection()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(CelllessSpawn(
            PositionSequence: 1,
            parentGuid: null));
        RuntimeEntityRecord canonical = fixture.Canonical;
        Assert.False(fixture.Runtime.TryGetRecord(Guid, out _));
        Assert.Equal(0, fixture.Resources.RegisterCount);

        Assert.True(fixture.Runtime.TryApplyPosition(
            PositionUpdate(sequence: 2, x: 14f),
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            out _,
            out _,
            out _));
        ulong authorityVersion = canonical.PositionAuthorityVersion;

        Assert.True(fixture.Controller.RecoverCanonicalProjection(
            canonical,
            authorityVersion,
            out LiveEntityRecord record));

        Assert.NotNull(record.WorldEntity);
        Assert.True(fixture.Runtime.RebucketLiveEntity(Guid, Cell));
        Assert.True(record.IsSpatiallyProjected);
        Assert.Equal(Cell, canonical.FullCellId);
        Assert.True(record.InitialHydrationCompleted);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(1, fixture.Ready.PublishCount);
        Assert.Contains(
            fixture.Materializer.Calls,
            call => call.Purpose is LiveProjectionPurpose.SpatialRecovery);

        Assert.True(fixture.Controller.RecoverCanonicalProjection(
            canonical,
            authorityVersion,
            out LiveEntityRecord same));
        Assert.Same(record, same);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(1, fixture.Ready.PublishCount);
    }

    [Fact]
    public void CanonicalProjectionRecovery_RejectsSupersededPositionAuthority()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(CelllessSpawn(
            PositionSequence: 1,
            parentGuid: null));
        RuntimeEntityRecord canonical = fixture.Canonical;
        Assert.True(fixture.Runtime.TryApplyPosition(
            PositionUpdate(sequence: 2, x: 14f),
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            out _,
            out _,
            out _));
        ulong staleAuthorityVersion = canonical.PositionAuthorityVersion;
        Assert.True(fixture.Runtime.TryApplyPosition(
            PositionUpdate(sequence: 3, x: 18f),
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            out _,
            out _,
            out _));

        Assert.False(fixture.Controller.RecoverCanonicalProjection(
            canonical,
            staleAuthorityVersion,
            out _));
        Assert.False(fixture.Runtime.TryGetRecord(Guid, out _));
        Assert.Equal(0, fixture.Resources.RegisterCount);
        Assert.Equal(0, fixture.Ready.PublishCount);
    }

    [Fact]
    public void ParentEvent_IsRetainedByRelationshipOwnerAndAcceptedThroughHydrationGate()
    {
        using var fixture = new Fixture(originKnown: true);
        const uint parentGuid = 0x70000002u;
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        fixture.Runtime.RegisterLiveEntity(
            Spawn(Generation: 4, PositionSequence: 1) with { Guid = parentGuid });
        bool accepted = false;
        fixture.Relationships.OnParentAction = update =>
            accepted = fixture.Controller.TryAcceptParentForProjection(update);
        var update = new ParentEvent.Parsed(
            parentGuid,
            Guid,
            ParentLocation: 1,
            PlacementId: 0,
            ParentInstanceSequence: 4,
            ChildPositionSequence: 2);

        fixture.Controller.OnParent(update);

        Assert.True(accepted);
        Assert.Equal((ushort)2, fixture.Record.Snapshot.PositionSequence);
        Assert.Contains($"parent:{Guid:X8}", fixture.Operations);
    }

    [Fact]
    public void SameGenerationCreateParent_AdvancesSharedPositionAndPublishesRelation()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        var update = new CreateParentUpdate(
            Guid,
            ParentGuid: 0x70000002u,
            ParentLocation: 3,
            PlacementId: 0,
            ChildInstanceSequence: 1,
            ChildPositionSequence: 2);

        Assert.True(fixture.Controller.OnCreateParentAccepted(update));

        Assert.Equal((ushort)2, fixture.Record.Snapshot.PositionSequence);
        Assert.Contains($"create-parent:{Guid:X8}", fixture.Operations);
    }

    [Fact]
    public void SameOlderNewerAndWrappedGenerations_RouteOrReplaceExactlyOnce()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 0xFFFF, PositionSequence: 1));
        LiveEntityRecord first = fixture.Record;

        fixture.Controller.OnCreate(Spawn(Generation: 0xFFFF, PositionSequence: 2));
        Assert.Equal(1, fixture.Network.ApplyCount);
        Assert.Same(first, fixture.Record);

        fixture.Controller.OnCreate(Spawn(Generation: 0xFFFE, PositionSequence: 3));
        Assert.Equal(1, fixture.Network.ApplyCount);
        Assert.Same(first, fixture.Record);

        fixture.Controller.OnCreate(Spawn(Generation: 0, PositionSequence: 4));

        Assert.NotSame(first, fixture.Record);
        Assert.Equal((ushort)0, fixture.Record.Generation);
        Assert.Equal(2, fixture.Resources.RegisterCount);
        Assert.Equal(1, fixture.Resources.UnregisterCount);
        Assert.Equal(2, fixture.Ready.PublishCount);
    }

    [Fact]
    public void RelationshipCallback_ReplacesGuidAndSupersedesOuterProjection()
    {
        using var fixture = new Fixture(originKnown: true);
        bool replaced = false;
        fixture.Relationships.OnSpawnAction = spawn =>
        {
            if (!replaced && spawn.InstanceSequence == 1)
            {
                replaced = true;
                fixture.Controller.OnCreate(Spawn(Generation: 2, PositionSequence: 2));
            }
        };

        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));

        Assert.Equal((ushort)2, fixture.Record.Generation);
        Assert.Single(fixture.Materializer.Calls);
        Assert.Equal((ushort)2, fixture.Materializer.Calls[0].Generation);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(1, fixture.Ready.PublishCount);
    }

    [Fact]
    public void RegistrationFailure_RollsBackAndLandblockRecoveryRetriesOnce()
    {
        var resources = new FailingOnceResources();
        using var fixture = new Fixture(originKnown: true, resources);

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1)));

        RuntimeEntityRecord canonical = fixture.Canonical;
        Assert.False(fixture.Runtime.TryGetRecord(Guid, out _));
        Assert.NotNull(canonical.LocalEntityId);
        Assert.Equal(1, resources.RegisterCount);
        Assert.Equal(1, resources.UnregisterCount);

        fixture.Controller.OnLandblockLoaded(Cell);

        LiveEntityRecord record = fixture.Record;
        Assert.NotNull(record.WorldEntity);
        Assert.Equal(2, resources.RegisterCount);
        Assert.Equal(1, resources.UnregisterCount);
        Assert.Equal(1, fixture.Ready.PublishCount);
    }

    [Fact]
    public void SameGenerationRetransmit_RecoversAfterRegistrationTombstoneConverges()
    {
        var resources = new FailingRegisterAndRollbackOnceResources();
        using var fixture = new Fixture(originKnown: true, resources);
        WorldSession.EntitySpawn spawn = Spawn(Generation: 1, PositionSequence: 1);

        Assert.Throws<AggregateException>(() => fixture.Controller.OnCreate(spawn));
        Assert.Equal(0, fixture.Runtime.Count);
        Assert.Equal(1, fixture.Runtime.PendingTeardownCount);
        Assert.Equal(1, fixture.Runtime.RetryPendingTeardowns());

        fixture.Controller.OnCreate(spawn);

        Assert.True(fixture.Runtime.TryGetRecord(Guid, out LiveEntityRecord recovered));
        Assert.NotNull(recovered.WorldEntity);
        Assert.True(recovered.InitialHydrationCompleted);
        Assert.Equal(2, resources.RegisterCount);
        Assert.Equal(2, resources.UnregisterCount);
        Assert.Equal(1, fixture.Ready.PublishCount);
    }

    [Fact]
    public void PriorGenerationCleanupFailure_ReportsAfterReplacementIsFullyInstalled()
    {
        var resources = new FailingUnregisterOnceResources();
        using var fixture = new Fixture(originKnown: true, resources);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));

        Assert.Throws<AggregateException>(() => fixture.Controller.OnCreate(Spawn(
            Generation: 2,
            PositionSequence: 2,
            Name: "replacement")));

        Assert.Equal((ushort)2, fixture.Record.Generation);
        Assert.True(fixture.Record.InitialHydrationCompleted);
        Assert.Equal("replacement", fixture.Objects.Get(Guid)!.Name);
        Assert.Equal(1, fixture.Runtime.PendingTeardownCount);
        Assert.Equal(1, fixture.Runtime.RetryPendingTeardowns());
        Assert.Equal(0, fixture.Runtime.PendingTeardownCount);
    }

    [Fact]
    public void UnknownDelete_ForgetsOnlyPendingOwnerState()
    {
        using var fixture = new Fixture(originKnown: true);

        Assert.False(fixture.Controller.OnDelete(
            new DeleteObject.Parsed(Guid, InstanceSequence: 1)));

        Assert.Equal([Guid], fixture.Teardown.ForgottenUnknownOwners);
        Assert.Equal(0, fixture.Teardown.TearDownCount);
        Assert.Equal(0, fixture.Runtime.Count);
    }

    [Fact]
    public void StaleDelete_DoesNotEndRemoteLiveIncarnation()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 2, PositionSequence: 1));
        LiveEntityRecord current = fixture.Record;

        Assert.False(fixture.Controller.OnDelete(
            new DeleteObject.Parsed(Guid, InstanceSequence: 1)));

        Assert.Same(current, fixture.Record);
        Assert.NotNull(fixture.Objects.Get(Guid));
        Assert.Equal(0, fixture.Teardown.TearDownCount);
        Assert.Empty(fixture.Teardown.ForgottenUnknownOwners);
    }

    [Fact]
    public void LocalPlayerDelete_IsSideEffectFreeBeforeAndAfterCreate()
    {
        using var fixture = new Fixture(originKnown: true, playerGuid: Guid);

        Assert.False(fixture.Controller.OnDelete(
            new DeleteObject.Parsed(Guid, InstanceSequence: 1)));
        Assert.Empty(fixture.Teardown.ForgottenUnknownOwners);

        fixture.Controller.OnCreate(Spawn(Generation: 2, PositionSequence: 1));
        LiveEntityRecord current = fixture.Record;

        Assert.False(fixture.Controller.OnDelete(
            new DeleteObject.Parsed(Guid, InstanceSequence: 2)));

        Assert.Same(current, fixture.Record);
        Assert.NotNull(fixture.Objects.Get(Guid));
        Assert.Equal(0, fixture.Teardown.TearDownCount);
        Assert.Empty(fixture.Teardown.ForgottenUnknownOwners);
    }

    [Fact]
    public void ExactDelete_RemovesObjectAndTearsDownOnce()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));

        Assert.True(fixture.Controller.OnDelete(
            new DeleteObject.Parsed(Guid, InstanceSequence: 1)));

        Assert.False(fixture.Runtime.TryGetRecord(Guid, out _));
        Assert.Null(fixture.Objects.Get(Guid));
        Assert.Equal(1, fixture.Teardown.TearDownCount);
        Assert.Equal(0, fixture.Runtime.PendingTeardownCount);
    }

    [Fact]
    public void Prune_DeletesTheObjectOutright()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        Assert.NotNull(fixture.Objects.Get(Guid));

        Assert.True(fixture.Controller.OnPrune(
            new LiveEntityPruneCandidate(
                fixture.Record.ProjectionKey!.Value,
                Guid)));

        Assert.False(fixture.Runtime.TryGetRecord(Guid, out _));
        Assert.Null(fixture.Objects.Get(Guid));
        Assert.Equal(1, fixture.Teardown.TearDownCount);
        Assert.Equal(0, fixture.Runtime.PendingTeardownCount);
    }

    [Fact]
    public void LoadedLandblock_DoesNotResurrectAPrunedObject()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        Assert.True(fixture.Controller.OnPrune(
            new LiveEntityPruneCandidate(
                fixture.Record.ProjectionKey!.Value,
                Guid)));

        fixture.Controller.OnLandblockLoaded(Cell);

        Assert.False(fixture.Runtime.TryGetRecord(Guid, out _));
        Assert.Null(fixture.Objects.Get(Guid));
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(1, fixture.Resources.UnregisterCount);
    }

    [Fact]
    public void RecreateAfterPrune_HydratesFreshFromTheWire()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        Assert.True(fixture.Controller.OnPrune(
            new LiveEntityPruneCandidate(
                fixture.Record.ProjectionKey!.Value,
                Guid)));

        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));

        Assert.True(fixture.Runtime.TryGetRecord(Guid, out LiveEntityRecord restored));
        Assert.Equal((ushort)1, restored.Generation);
        Assert.NotNull(restored.WorldEntity);
        Assert.NotNull(fixture.Objects.Get(Guid));
        Assert.Equal(2, fixture.Resources.RegisterCount);
        Assert.Equal(1, fixture.Resources.UnregisterCount);
    }

    [Fact]
    public void Delete_DrainsIndependentFailuresAndRetryDoesNotReplayObjectRemoval()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        int objectRemovedCalls = 0;
        fixture.Objects.ObjectRemoved += _ =>
        {
            objectRemovedCalls++;
            throw new InvalidOperationException("fixture object-table observer failure");
        };
        bool failTeardown = true;
        fixture.Teardown.TearDownAction = _ =>
        {
            if (!failTeardown)
                return;
            failTeardown = false;
            throw new InvalidOperationException("fixture component teardown failure");
        };

        AggregateException error = Assert.Throws<AggregateException>(() =>
            fixture.Controller.OnDelete(
                new DeleteObject.Parsed(Guid, InstanceSequence: 1)));

        Assert.Contains(error.Flatten().InnerExceptions,
            exception => exception.Message.Contains("object-table observer", StringComparison.Ordinal));
        Assert.Contains(error.Flatten().InnerExceptions,
            exception => exception.Message.Contains("component teardown", StringComparison.Ordinal));
        Assert.False(fixture.Runtime.TryGetRecord(Guid, out _));
        Assert.Null(fixture.Objects.Get(Guid));
        Assert.Equal(1, objectRemovedCalls);
        Assert.Equal(1, fixture.Runtime.PendingTeardownCount);

        Assert.Equal(1, fixture.Controller.RetryPendingTeardowns());
        Assert.Equal(0, fixture.Runtime.PendingTeardownCount);
        Assert.Equal(1, objectRemovedCalls);
        Assert.Equal(2, fixture.Teardown.TearDownCount);
    }

    [Fact]
    public void DeleteCallback_CanInstallNewerGenerationWithoutOldCleanupRemovingIt()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord old = fixture.Record;
        bool replaced = false;
        fixture.Teardown.TearDownAction = record =>
        {
            Assert.Same(old, record);
            if (replaced)
                return;
            replaced = true;
            LiveEntityRegistrationResult result = fixture.Runtime.RegisterLiveEntity(
                Spawn(Generation: 2, PositionSequence: 2, Name: "replacement"));
            Assert.True(result.LogicalRegistrationCreated);
        };

        Assert.True(fixture.Controller.OnDelete(
            new DeleteObject.Parsed(Guid, InstanceSequence: 1)));

        RuntimeEntityRecord replacement = fixture.Canonical;
        Assert.Equal((ushort)2, replacement.Generation);
        Assert.Equal("replacement", replacement.Snapshot.Name);
        Assert.NotNull(replacement.LocalEntityId);
        Assert.False(fixture.Runtime.TryGetRecord(Guid, out _));
        Assert.Equal(0, fixture.Runtime.PendingTeardownCount);
    }

    [Fact]
    public void PartialProjection_IsRetriedInsteadOfMistakenForCompletedHydration()
    {
        using var fixture = new Fixture(originKnown: true, playerGuid: Guid);
        bool failOnce = true;
        fixture.Materializer.AfterMaterialize = (_, _) =>
        {
            if (!failOnce)
                return;
            failOnce = false;
            throw new InvalidOperationException("fixture post-registration failure");
        };

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1)));

        LiveEntityRecord record = fixture.Record;
        WorldEntity partial = Assert.IsType<WorldEntity>(record.WorldEntity);
        Assert.False(record.InitialHydrationCompleted);

        fixture.FirstEntry.DriveAll();
        fixture.Controller.OnLandblockLoaded(Cell);

        Assert.Same(partial, record.WorldEntity);
        Assert.True(record.InitialHydrationCompleted);
        Assert.Equal(2, fixture.Materializer.Calls.Count);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(1, fixture.Ready.PublishCount);
    }

    [Fact]
    public void FailedReadyBarrier_RetriesSameProjectionOnRetransmit()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Ready.FailPublishCount = 1;

        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord record = fixture.Record;
        WorldEntity partial = Assert.IsType<WorldEntity>(record.WorldEntity);
        Assert.False(record.InitialHydrationCompleted);

        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 2));

        Assert.Same(partial, record.WorldEntity);
        Assert.True(record.InitialHydrationCompleted);
        Assert.Equal(2, fixture.Materializer.Calls.Count);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(2, fixture.Ready.PublishCount);
    }

    [Fact]
    public void TimestampCallbackReplacement_StopsOuterObjectAndProjectionTail()
    {
        using var fixture = new Fixture(originKnown: true);
        bool replaced = false;
        fixture.PublishTimestampsAction = (_, timestamps) =>
        {
            if (replaced || timestamps.Instance != 1)
                return;
            replaced = true;
            fixture.Controller.OnCreate(Spawn(
                Generation: 2,
                PositionSequence: 2,
                Name: "generation 2"));
        };

        fixture.Controller.OnCreate(Spawn(
            Generation: 1,
            PositionSequence: 1,
            Name: "generation 1"));

        Assert.Equal((ushort)2, fixture.Record.Generation);
        Assert.Equal("generation 2", fixture.Objects.Get(Guid)!.Name);
        Assert.Single(fixture.Materializer.Calls);
        Assert.Equal((ushort)2, fixture.Materializer.Calls[0].Generation);
    }

    [Fact]
    public void TimestampCallbackFresherSameGeneration_StopsOuterCreateVersion()
    {
        using var fixture = new Fixture(originKnown: true);
        bool refreshed = false;
        fixture.PublishTimestampsAction = (_, timestamps) =>
        {
            if (refreshed)
                return;
            refreshed = true;
            fixture.Controller.OnCreate(Spawn(
                Generation: 1,
                PositionSequence: 2,
                Name: "same generation newer"));
        };

        fixture.Controller.OnCreate(Spawn(
            Generation: 1,
            PositionSequence: 1,
            Name: "same generation older"));

        Assert.Equal("same generation newer", fixture.Objects.Get(Guid)!.Name);
        Assert.True(fixture.Record.InitialHydrationCompleted);
        Assert.Single(fixture.Materializer.Calls);
        Assert.Equal((ushort)1, fixture.Materializer.PositionSequences[0]);
    }

    [Fact]
    public void ObjectRemovalCallbackNewerGeneration_CannotBeOverwrittenByOuterCreate()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(
            Generation: 1,
            PositionSequence: 1,
            Name: "generation 1"));
        bool replaced = false;
        fixture.Objects.ObjectRemovalClassified += removal =>
        {
            if (replaced
                || removal.Reason is not ClientObjectRemovalReason.GenerationReplacement)
            {
                return;
            }

            replaced = true;
            fixture.Controller.OnCreate(Spawn(
                Generation: 3,
                PositionSequence: 3,
                Name: "generation 3"));
        };

        fixture.Controller.OnCreate(Spawn(
            Generation: 2,
            PositionSequence: 2,
            Name: "generation 2"));

        Assert.Equal((ushort)3, fixture.Record.Generation);
        Assert.Equal("generation 3", fixture.Objects.Get(Guid)!.Name);
        Assert.Equal((ushort)3, fixture.Materializer.Calls[^1].Generation);
        Assert.DoesNotContain(
            fixture.Materializer.Calls,
            call => call.Generation == 2);
    }

    [Fact]
    public void ObjectRemovalCallbackFresherSameGeneration_WinsOuterReplacement()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(
            Generation: 0,
            PositionSequence: 1,
            Name: "generation zero"));
        bool refreshed = false;
        fixture.Objects.ObjectRemovalClassified += removal =>
        {
            if (refreshed
                || removal.Reason is not ClientObjectRemovalReason.GenerationReplacement)
            {
                return;
            }

            refreshed = true;
            fixture.Controller.OnCreate(Spawn(
                Generation: 1,
                PositionSequence: 3,
                Name: "same generation newer"));
        };

        fixture.Controller.OnCreate(Spawn(
            Generation: 1,
            PositionSequence: 2,
            Name: "same generation older"));

        Assert.Equal((ushort)1, fixture.Record.Generation);
        Assert.Equal("same generation newer", fixture.Objects.Get(Guid)!.Name);
        Assert.True(fixture.Record.InitialHydrationCompleted);
        Assert.Equal((ushort)2, fixture.Materializer.PositionSequences[^1]);
        Assert.DoesNotContain(
            (ushort)3,
            fixture.Materializer.PositionSequences);
    }

    [Fact]
    public void MaterializerCallbackFresherSameGeneration_ReplaysCanonicalHydrationOnce()
    {
        using var fixture = new Fixture(originKnown: true);
        bool refreshed = false;
        fixture.Materializer.BeforeMaterialize = (_, spawn) =>
        {
            if (refreshed || spawn.PositionSequence != 1)
                return;
            refreshed = true;
            fixture.Controller.OnCreate(Spawn(
                Generation: 1,
                PositionSequence: 2,
                Name: "same generation newer"));
        };

        fixture.Controller.OnCreate(Spawn(
            Generation: 1,
            PositionSequence: 1,
            Name: "same generation older"));

        Assert.Equal("same generation newer", fixture.Objects.Get(Guid)!.Name);
        Assert.Equal([1, 2], fixture.Materializer.PositionSequences);
        Assert.Equal(
            LiveProjectionPurpose.CreateSupersessionRecovery,
            fixture.Materializer.Calls[^1].Purpose);
        Assert.Equal(2UL, fixture.Materializer.InstalledCreateIntegrationVersion);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(1, fixture.Ready.PublishCount);
        Assert.True(fixture.Record.InitialHydrationCompleted);
    }

    [Fact]
    public void ReadyCallbackFresherSameGeneration_ReplaysCanonicalHydrationBeforeCompletion()
    {
        using var fixture = new Fixture(originKnown: true);
        bool refreshed = false;
        fixture.Ready.BeforePublish = _ =>
        {
            if (refreshed)
                return;
            refreshed = true;
            fixture.Controller.OnCreate(Spawn(
                Generation: 1,
                PositionSequence: 2,
                Name: "same generation newer"));
        };

        fixture.Controller.OnCreate(Spawn(
            Generation: 1,
            PositionSequence: 1,
            Name: "same generation older"));

        Assert.Equal("same generation newer", fixture.Objects.Get(Guid)!.Name);
        Assert.Equal([1, 2], fixture.Materializer.PositionSequences);
        Assert.Equal(
            LiveProjectionPurpose.CreateSupersessionRecovery,
            fixture.Materializer.Calls[^1].Purpose);
        Assert.Equal(2UL, fixture.Materializer.InstalledCreateIntegrationVersion);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(2, fixture.Ready.PublishCount);
        Assert.True(fixture.Record.InitialHydrationCompleted);
    }

    [Fact]
    public void CompletedSpatialRecovery_DetectsCreateVersionDriftWithoutNestedHydrationRequest()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord record = fixture.Record;
        ulong stalePositionAuthority = record.PositionAuthorityVersion;
        bool refreshed = false;
        fixture.Materializer.BeforeMaterialize = (_, spawn) =>
        {
            if (refreshed || spawn.PositionSequence != 1)
                return;
            refreshed = true;
            fixture.Controller.OnCreate(Spawn(
                Generation: 1,
                PositionSequence: 2,
                Name: "same generation newer"));
        };

        Assert.False(fixture.Controller.RecoverProjection(
            record,
            stalePositionAuthority,
            record.Snapshot));

        Assert.True(record.InitialHydrationCompleted);
        Assert.Equal("same generation newer", fixture.Objects.Get(Guid)!.Name);
        Assert.Equal([1, 1], fixture.Materializer.PositionSequences);
        Assert.Equal(
            LiveProjectionPurpose.SpatialRecovery,
            fixture.Materializer.Calls[^1].Purpose);
        Assert.Equal(1UL, fixture.Materializer.InstalledCreateIntegrationVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedCompletedSupersession_RemainsPendingUntilExactRetry(
        bool retryFromLandblock)
    {
        using var fixture = new Fixture(
            originKnown: true,
            playerGuid: retryFromLandblock ? Guid : 0u);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord record = fixture.Record;
        WorldEntity retained = record.WorldEntity!;
        ulong positionAuthority = record.PositionAuthorityVersion;
        bool refreshed = false;
        fixture.Materializer.BeforeMaterialize = (_, spawn) =>
        {
            if (refreshed || spawn.PositionSequence != 1)
                return;
            refreshed = true;
            record.Canonical.AdvanceCreateAuthority();
        };
        fixture.Materializer.ThrowAfterMaterializePurposeOnce =
            LiveProjectionPurpose.CreateSupersessionRecovery;

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Controller.RecoverProjection(
                record,
                positionAuthority,
                record.Snapshot));

        Assert.True(record.InitialHydrationCompleted);
        Assert.True(record.CreateProjectionSynchronizationPending);
        Assert.Same(retained, record.WorldEntity);
        Assert.Equal(1, fixture.Resources.RegisterCount);

        fixture.Materializer.BeforeMaterialize = null;
        if (retryFromLandblock)
        {
            fixture.Controller.OnLandblockLoaded(Cell);
        }
        else
        {
            fixture.Controller.OnCreate(Spawn(
                Generation: 1,
                PositionSequence: 3,
                Name: "recovery v3"));
        }

        Assert.False(record.CreateProjectionSynchronizationPending);
        Assert.Same(retained, record.WorldEntity);
        Assert.Equal(
            2UL,
            fixture.Materializer.InstalledCreateIntegrationVersion);
        Assert.Equal(
            LiveProjectionPurpose.CreateSupersessionRecovery,
            fixture.Materializer.Calls[^1].Purpose);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(0, fixture.Resources.UnregisterCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleProjectionExceptionAfterCreateDrift_PersistsExactRetryObligation(
        bool retryFromLandblock)
    {
        using var fixture = new Fixture(
            originKnown: true,
            playerGuid: retryFromLandblock ? Guid : 0u);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord record = fixture.Record;
        WorldEntity retained = record.WorldEntity!;
        ulong positionAuthority = record.PositionAuthorityVersion;
        bool refreshed = false;
        fixture.Materializer.BeforeMaterialize = (_, spawn) =>
        {
            if (refreshed || spawn.PositionSequence != 1)
                return;
            refreshed = true;
            fixture.Controller.OnCreate(Spawn(
                Generation: 1,
                PositionSequence: 2,
                Name: "drift v2"));
            throw new InvalidOperationException("fixture stale projection failure");
        };

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Controller.RecoverProjection(
                record,
                positionAuthority,
                record.Snapshot));

        Assert.True(record.InitialHydrationCompleted);
        Assert.True(record.CreateProjectionSynchronizationPending);
        Assert.Same(retained, record.WorldEntity);

        fixture.Materializer.BeforeMaterialize = null;
        if (retryFromLandblock)
        {
            fixture.Controller.OnLandblockLoaded(Cell);
        }
        else
        {
            fixture.Controller.OnCreate(Spawn(
                Generation: 1,
                PositionSequence: 3,
                Name: "drift v3"));
        }

        Assert.False(record.CreateProjectionSynchronizationPending);
        Assert.Same(retained, record.WorldEntity);
        Assert.Equal(
            LiveProjectionPurpose.CreateSupersessionRecovery,
            fixture.Materializer.Calls[^1].Purpose);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(0, fixture.Resources.UnregisterCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedSupersessionReadyFailure_RetriesWholeCommitBoundary(
        bool retryFromLandblock)
    {
        using var fixture = new Fixture(
            originKnown: true,
            playerGuid: retryFromLandblock ? Guid : 0u);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord record = fixture.Record;
        WorldEntity retained = record.WorldEntity!;
        ulong positionAuthority = record.PositionAuthorityVersion;
        bool refreshed = false;
        fixture.Materializer.BeforeMaterialize = (_, spawn) =>
        {
            if (refreshed || spawn.PositionSequence != 1)
                return;
            refreshed = true;
            record.Canonical.AdvanceCreateAuthority();
        };
        fixture.Ready.FailPublishCount = 1;

        Assert.False(fixture.Controller.RecoverProjection(
            record,
            positionAuthority,
            record.Snapshot));

        Assert.True(record.CreateProjectionSynchronizationPending);
        fixture.Materializer.BeforeMaterialize = null;
        if (retryFromLandblock)
        {
            fixture.Controller.OnLandblockLoaded(Cell);
        }
        else
        {
            fixture.Controller.OnCreate(Spawn(
                Generation: 1,
                PositionSequence: 3,
                Name: "ready v3"));
        }

        Assert.False(record.CreateProjectionSynchronizationPending);
        Assert.Same(retained, record.WorldEntity);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(0, fixture.Resources.UnregisterCount);
        Assert.True(fixture.Ready.PublishCount >= 3);
    }

    [Fact]
    public void PickupSupersession_CompletesCelllessWithoutDatMaterialization()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord record = fixture.Record;
        ulong positionAuthority = record.PositionAuthorityVersion;
        fixture.Network.ApplyAction = events =>
        {
            Assert.NotNull(events.Pickup);
            Assert.True(fixture.Runtime.TryApplyPickup(events.Pickup!.Value, out _));
            Assert.True(fixture.Runtime.WithdrawLiveEntityProjectionToCellless(Guid));
        };
        bool refreshed = false;
        fixture.Materializer.BeforeMaterialize = (_, spawn) =>
        {
            if (refreshed || spawn.PositionSequence != 1)
                return;
            refreshed = true;
            fixture.Controller.OnCreate(CelllessSpawn(
                PositionSequence: 2,
                parentGuid: null));
        };

        Assert.False(fixture.Controller.RecoverProjection(
            record,
            positionAuthority,
            record.Snapshot));

        Assert.False(record.CreateProjectionSynchronizationPending);
        Assert.Equal(0u, record.FullCellId);
        Assert.False(record.IsSpatiallyProjected);
        Assert.DoesNotContain(
            fixture.Materializer.Calls,
            call => call.Purpose is LiveProjectionPurpose.CreateSupersessionRecovery);
        Assert.Equal(1, fixture.Resources.RegisterCount);
    }

    [Fact]
    public void PickupSupersedesInitialHydration_WithoutInventingRenderOwner()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Network.ApplyAction = events =>
        {
            Assert.NotNull(events.Pickup);
            Assert.True(fixture.Runtime.TryApplyPickup(events.Pickup!.Value, out _));
        };
        bool refreshed = false;
        fixture.Materializer.BeforeMaterialize = (_, spawn) =>
        {
            if (refreshed || spawn.PositionSequence != 1)
                return;
            refreshed = true;
            fixture.Controller.OnCreate(CelllessSpawn(
                PositionSequence: 2,
                parentGuid: null));
        };

        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));

        RuntimeEntityRecord canonical = fixture.Canonical;
        Assert.NotNull(canonical.LocalEntityId);
        Assert.False(fixture.Runtime.TryGetRecord(Guid, out _));
        Assert.Equal(0, fixture.Resources.RegisterCount);

        fixture.Materializer.BeforeMaterialize = null;
        fixture.Network.ApplyAction = events =>
        {
            if (events.Position is { } position)
            {
                fixture.Runtime.TryApplyPosition(
                    position,
                    isLocalPlayer: false,
                    forcePositionRotation: null,
                    currentLocalVelocity: null,
                    out _,
                    out _,
                    out _);
            }
        };
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 3));

        LiveEntityRecord record = fixture.Record;
        Assert.True(record.InitialHydrationCompleted);
        Assert.NotNull(record.WorldEntity);
        Assert.Equal(1, fixture.Resources.RegisterCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentSupersession_CompletesAtExactAttachedReadyBoundary(
        bool failFirstReadyAttempt)
    {
        const uint parentGuid = 0x70000002u;
        using var fixture = new Fixture(originKnown: true);
        fixture.Runtime.RegisterLiveEntity(
            Spawn(Generation: 1, PositionSequence: 1) with { Guid = parentGuid });
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord record = fixture.Record;
        WorldEntity retained = record.WorldEntity!;
        ulong positionAuthority = record.PositionAuthorityVersion;
        fixture.Ready.FailPublishCount = failFirstReadyAttempt ? 2 : 0;
        CreateParentUpdate? pendingParent = null;
        fixture.Network.ApplyAction = events =>
        {
            Assert.NotNull(events.Parent);
            Assert.True(fixture.Runtime.TryApplyCreateParent(
                events.Parent!.Value,
                out _));
            pendingParent = events.Parent.Value;
        };
        fixture.Relationships.OnSpawnAction = _ =>
        {
            if (pendingParent is not { } parent
                || !record.CreateProjectionSynchronizationPending)
            {
                return;
            }

            Assert.True(fixture.Runtime.CommitStagedParent(
                new ParentAttachmentRelation(
                    parent.ParentGuid,
                    parent.ChildGuid,
                    parent.ParentLocation,
                    parent.PlacementId,
                    ParentInstanceSequence: 0,
                    parent.ChildPositionSequence),
                out _));
            ulong positionVersion = record.PositionAuthorityVersion;
            Assert.True(fixture.Runtime.CommitAcceptedParentCellless(
                record,
                positionVersion));
            Assert.True(fixture.Runtime.WithdrawLiveEntityProjection(record));
            if (record.MaterializationResidence is
                AcDream.App.World.LiveEntityMaterializationResidence
                    .AwaitRuntimePlacement)
            {
                record.MaterializationResidence = AcDream.App.World
                    .LiveEntityMaterializationResidence.LegacyImmediate;
            }
            WorldEntity? attached = fixture.Runtime.MaterializeLiveEntity(
                Guid,
                Cell,
                _ => throw new InvalidOperationException("retained identity expected"),
                LiveEntityProjectionKind.Attached);
            Assert.Same(retained, attached);
            fixture.Controller.OnEntityReady(
                LiveEntityReadyCandidate.Capture(record));
        };
        bool refreshed = false;
        fixture.Materializer.BeforeMaterialize = (_, spawn) =>
        {
            if (refreshed || spawn.PositionSequence != 1)
                return;
            refreshed = true;
            fixture.Controller.OnCreate(CelllessSpawn(
                PositionSequence: 2,
                parentGuid));
            record.Canonical.AdvanceCreateAuthority();
        };

        Assert.False(fixture.Controller.RecoverProjection(
            record,
            positionAuthority,
            record.Snapshot));

        if (failFirstReadyAttempt)
        {
            Assert.True(record.CreateProjectionSynchronizationPending);
            fixture.Materializer.BeforeMaterialize = null;
            fixture.Controller.OnCreate(CelllessSpawn(
                PositionSequence: 3,
                parentGuid));
        }

        Assert.False(record.CreateProjectionSynchronizationPending);
        Assert.Equal(LiveEntityProjectionKind.Attached, record.ProjectionKind);
        Assert.Same(retained, record.WorldEntity);
        Assert.DoesNotContain(
            fixture.Materializer.Calls,
            call => call.Purpose is LiveProjectionPurpose.CreateSupersessionRecovery);
        Assert.Equal(1, fixture.Resources.RegisterCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialParentReadyFailure_RetriesCelllessReadyBoundary(
        bool retryFromLandblock)
    {
        const uint parentGuid = 0x70000002u;
        using var fixture = new Fixture(
            originKnown: true,
            playerGuid: retryFromLandblock ? Guid : 0u);
        WorldSession.EntitySpawn parentSpawn =
            Spawn(Generation: 1, PositionSequence: 1) with { Guid = parentGuid };
        var parentPosition = new CreateObject.ServerPosition(
            0x01020001u, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
        fixture.Runtime.RegisterLiveEntity(parentSpawn with
        {
            Position = parentPosition,
            Physics = parentSpawn.Physics!.Value with
            {
                Position = parentPosition,
            },
        });
        fixture.Ready.FailPublishCount = 2;
        fixture.Network.ApplyAction = events =>
        {
            if (events.Parent is { } parent)
                fixture.Runtime.TryApplyCreateParent(parent, out _);
        };
        fixture.Relationships.OnSpawnAction = _ =>
        {
            RuntimeEntityRecord canonical = fixture.Canonical;
            if (fixture.Runtime.TryGetRecord(
                    Guid,
                    out LiveEntityRecord _))
                return;

            WorldEntity? attached = fixture.Runtime.MaterializeLiveEntity(
                canonical,
                Cell,
                id => new WorldEntity
                {
                    Id = id,
                    ServerGuid = Guid,
                    SourceGfxObjOrSetupId = 0x02000001u,
                    Position = Vector3.Zero,
                    Rotation = Quaternion.Identity,
                    MeshRefs = [],
                    ParentCellId = Cell,
                },
                LiveEntityProjectionKind.Attached,
                initializeProjection: null,
                out LiveEntityRecord? record);
            Assert.NotNull(attached);
            Assert.NotNull(record);
            fixture.Controller.OnEntityReady(
                LiveEntityReadyCandidate.Capture(record!));
        };

        fixture.Controller.OnCreate(CelllessSpawn(
            PositionSequence: 1,
            parentGuid));

        LiveEntityRecord initial = fixture.Record;
        Assert.False(initial.InitialHydrationCompleted);
        Assert.Equal(LiveEntityProjectionKind.Attached, initial.ProjectionKind);
        Assert.Equal(1, fixture.Resources.RegisterCount);

        if (retryFromLandblock)
        {
            fixture.Controller.OnLandblockLoaded(Cell);
        }
        else
        {
            fixture.Controller.OnCreate(CelllessSpawn(
                PositionSequence: 2,
                parentGuid));
        }

        Assert.True(initial.InitialHydrationCompleted);
        Assert.False(initial.CreateProjectionSynchronizationPending);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(0, fixture.Resources.UnregisterCount);
    }

    [Theory]
    [InlineData(0, 1, 0, 0)]
    [InlineData(1, 1, 1, 0)]
    [InlineData(2, 1, 1, 1)]
    public void ReadyPublisher_RevalidatesExactRecordBetweenEveryStage(
        int replacementStage,
        int expectedPrepare,
        int expectedPresent,
        int expectedReplay)
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord expected = fixture.Record;
        int prepare = 0;
        int present = 0;
        int replay = 0;
        bool replaced = false;
        bool Stage(int stage)
        {
            if (!replaced && replacementStage == stage)
            {
                replaced = true;
                expected.Canonical.AdvanceCreateAuthority();
            }
            return true;
        }

        var publisher = new LiveEntityReadyPublisher(
            fixture.Runtime,
            _ => { prepare++; return Stage(0); },
            _ => { present++; return Stage(1); },
            _ => { replay++; return Stage(2); });

        Assert.False(publisher.Publish(
            LiveEntityReadyCandidate.Capture(expected)));
        Assert.Equal(expectedPrepare, prepare);
        Assert.Equal(expectedPresent, present);
        Assert.Equal(expectedReplay, replay);
        Assert.Same(expected, fixture.Record);
        Assert.Equal(2UL, fixture.Record.CreateIntegrationVersion);
    }

    [Theory]
    [InlineData(0, 1, 0, 0)]
    [InlineData(1, 1, 1, 0)]
    [InlineData(2, 1, 1, 1)]
    public void ReadyPublisher_RejectsSameIdentityProjectionChangeBetweenStages(
        int withdrawalStage,
        int expectedPrepare,
        int expectedPresent,
        int expectedReplay)
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord expected = fixture.Record;
        LiveEntityReadyCandidate candidate =
            LiveEntityReadyCandidate.Capture(expected);
        int prepare = 0;
        int present = 0;
        int replay = 0;
        bool withdrawn = false;
        bool Stage(int stage)
        {
            if (!withdrawn && withdrawalStage == stage)
            {
                withdrawn = true;
                Assert.True(fixture.Runtime.TryApplyPickup(
                    new PickupEvent.Parsed(Guid, 1, 2),
                    out _));
                Assert.True(fixture.Runtime.WithdrawLiveEntityProjection(expected));
            }
            return true;
        }

        var publisher = new LiveEntityReadyPublisher(
            fixture.Runtime,
            _ => { prepare++; return Stage(0); },
            _ => { present++; return Stage(1); },
            _ => { replay++; return Stage(2); });

        Assert.False(publisher.Publish(candidate));
        Assert.Equal(expectedPrepare, prepare);
        Assert.Equal(expectedPresent, present);
        Assert.Equal(expectedReplay, replay);
        Assert.Same(expected, fixture.Record);
        Assert.False(expected.IsSpatiallyProjected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadyPublisher_PublishesRenderProjectionAfterExistingReadyStages(
        bool projectionAccepted)
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord record = fixture.Record;
        var operations = new List<string>();
        var projection = new RecordingLiveProjectionSink(
            _ =>
            {
                operations.Add("projection");
                return projectionAccepted;
            });
        var publisher = new LiveEntityReadyPublisher(
            fixture.Runtime,
            _ => { operations.Add("effects-owner"); return true; },
            _ => { operations.Add("state"); return true; },
            _ => { operations.Add("effects-replay"); return true; },
            projection);

        bool result = publisher.Publish(
            LiveEntityReadyCandidate.Capture(record));

        Assert.Equal(projectionAccepted, result);
        Assert.Equal(
            ["effects-owner", "state", "effects-replay", "projection"],
            operations);
    }

    [Fact]
    public void ReadyPublisher_RevalidatesAfterReentrantRenderProjectionCallback()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityReadyCandidate candidate =
            LiveEntityReadyCandidate.Capture(fixture.Record);
        var projection = new RecordingLiveProjectionSink(
            _ =>
            {
                fixture.Record.Canonical.AdvanceCreateAuthority();
                return true;
            });
        var publisher = new LiveEntityReadyPublisher(
            fixture.Runtime,
            static _ => true,
            static _ => true,
            static _ => true,
            projection);

        Assert.False(publisher.Publish(candidate));
        Assert.Equal(2UL, fixture.Record.CreateIntegrationVersion);
    }

    [Fact]
    public void EquippedChildReadyCandidate_RejectsVersionAdvancedByPoseCallback()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord record = fixture.Record;
        WorldEntity entity = record.WorldEntity!;
        ulong capturedCreateIntegrationVersion = record.CreateIntegrationVersion;

        record.Canonical.AdvanceCreateAuthority();
        bool published = false;

        Assert.False(EquippedChildRenderController.PublishEntityReadyExact(
            fixture.Runtime,
            LiveEntityReadyCandidate.Capture(record) with
            {
                CreateIntegrationVersion = capturedCreateIntegrationVersion,
                WorldEntity = entity,
            },
            _ => published = true));
        Assert.False(published);
    }

    [Fact]
    public void EquippedChildReadyCandidate_RejectsPickupWithdrawalByPoseCallback()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord record = fixture.Record;
        LiveEntityReadyCandidate candidate =
            LiveEntityReadyCandidate.Capture(record);

        // Models ProjectionPoseReady accepting Pickup and withdrawing the
        // retained projection before EntityReady is emitted.
        Assert.True(fixture.Runtime.TryApplyPickup(
            new PickupEvent.Parsed(Guid, 1, 2),
            out _));
        Assert.True(fixture.Runtime.WithdrawLiveEntityProjection(record));
        bool published = false;

        Assert.False(EquippedChildRenderController.PublishEntityReadyExact(
            fixture.Runtime,
            candidate,
            _ => published = true));
        Assert.False(published);
    }

    [Fact]
    public void PositionAuthorityGuard_PreventsStaleRecoveryAfterNestedUpdate()
    {
        using var fixture = new Fixture(originKnown: true);
        fixture.Controller.OnCreate(Spawn(Generation: 1, PositionSequence: 1));
        LiveEntityRecord record = fixture.Record;
        fixture.Runtime.WithdrawLiveEntityProjection(Guid);
        ulong staleVersion = record.PositionAuthorityVersion;

        Assert.True(fixture.Runtime.TryApplyPosition(
            new WorldSession.EntityPositionUpdate(
                Guid,
                new CreateObject.ServerPosition(Cell, 12f, 10f, 5f, 1f, 0f, 0f, 0f),
                Velocity: null,
                PlacementId: 0,
                IsGrounded: true,
                InstanceSequence: 1,
                PositionSequence: 2,
                TeleportSequence: 0,
                ForcePositionSequence: 0),
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            out _,
            out WorldSession.EntitySpawn accepted,
            out _));

        Assert.False(fixture.Controller.RecoverProjection(
            record,
            staleVersion,
            accepted));
        Assert.Single(fixture.Runtime.MaterializedRecords);
        Assert.Empty(fixture.Runtime.VisibleRecords);
    }

    [Fact]
    public void DeferredNetworkSink_IsSingleAssignmentAndFailsBeforeBind()
    {
        var deferred = new DeferredLiveEntityNetworkUpdateSink();
        SameGenerationCreateObjectEvents events = default;

        Assert.Throws<InvalidOperationException>(() =>
            deferred.ApplySameGeneration(events));

        var first = new RecordingNetworkSink();
        deferred.Bind(first);
        deferred.ApplySameGeneration(events);

        Assert.Equal(1, first.ApplyCount);
        Assert.Throws<InvalidOperationException>(() =>
            deferred.Bind(new RecordingNetworkSink()));
    }

    [Fact]
    public void DeferredNetworkOwnedBindingReleasesExactlyAndAllowsRebind()
    {
        var deferred = new DeferredLiveEntityNetworkUpdateSink();
        var first = new RecordingNetworkSink();
        var second = new RecordingNetworkSink();

        IDisposable firstBinding = deferred.BindOwned(first);
        Assert.Throws<InvalidOperationException>(() => deferred.BindOwned(second));

        firstBinding.Dispose();
        using IDisposable secondBinding = deferred.BindOwned(second);
        firstBinding.Dispose();
        deferred.ApplySameGeneration(default);

        Assert.Equal(0, first.ApplyCount);
        Assert.Equal(1, second.ApplyCount);
    }


    [Fact]
    public void WholeItemDrop_DispatchesNoSplitMarkerAndEntersCanonicalCreatePlacementTransaction()
    {
        using var fixture = new Fixture(originKnown: true);
        var drop = new DropHarness(fixture);
        const uint itemGuid = 0x50000B01u;
        fixture.Controller.OnCreate(ItemSpawn(
            itemGuid, position: null, containerId: DropHarness.PlayerGuid, stackSize: null));
        bool dispatched = false;
        drop.Interaction.WorldDropDispatched += _ => dispatched = true;
        var payload = new ItemDragPayload(
            itemGuid, ItemDragSource.Inventory, SourceSlot: 0, SourceCell: new UiItemSlot());

        Assert.True(drop.Interaction.DropToWorld(payload));

        Assert.False(dispatched);

        fixture.Controller.OnCreate(ItemSpawn(
            itemGuid,
            position: new CreateObject.ServerPosition(Cell, 10f, 10f, 5f, 1f, 0f, 0f, 0f),
            containerId: 0u,
            stackSize: null,
            instanceSequence: 2));

        Assert.True(fixture.Runtime.TryGetRecord(itemGuid, out LiveEntityRecord record));
        Assert.NotNull(record.WorldEntity);
        Assert.True(record.InitialHydrationCompleted);
        Assert.Equal(1, fixture.Resources.RegisterCount);
    }

    [Fact]
    public void SplitSourceWithRetainedMovementTimestamps_StillRecovers()
    {
        using var fixture = new Fixture(originKnown: true);
        var drop = new DropHarness(fixture);
        const uint sourceGuid = 0x50000C40u;
        WorldSession.EntitySpawn baseSource = ItemSpawn(
            sourceGuid, position: null, containerId: DropHarness.PlayerGuid, stackSize: 6);
        WorldSession.EntitySpawn source = baseSource with
        {
            MovementSequence = 2,
            ServerControlSequence = 6,
            Physics = baseSource.Physics!.Value with
            {
                Timestamps = baseSource.Physics.Value.Timestamps with
                {
                    Movement = 2,
                    ServerControlledMove = 6,
                },
            },
        };
        fixture.Controller.OnCreate(source);
        Assert.True(drop.DispatchSplit(sourceGuid, stackSize: 6, splitAmount: 1));

        const uint splitResultGuid = 0x80000D01u;
        bool recovered = drop.Projection.TryRecoverUnknownPosition(
            DropPositionUpdate(splitResultGuid, x: 30f));

        Assert.True(recovered);
        Assert.True(fixture.Runtime.TryGetSnapshot(
            splitResultGuid, out WorldSession.EntitySpawn spawn));
        Assert.Equal(1, spawn.StackSize);
        Assert.Equal(0u, spawn.ContainerId);
        Assert.True(fixture.Runtime.TryGetRecord(
            splitResultGuid, out LiveEntityRecord record));
        Assert.NotNull(record.WorldEntity);

        Assert.Equal(0, spawn.MovementSequence);
        Assert.Equal(0, spawn.ServerControlSequence);
        Assert.Equal(0u, spawn.Physics!.Value.Timestamps.Movement);
        Assert.Equal(0u, spawn.Physics.Value.Timestamps.ServerControlledMove);
    }

    [Fact]
    public void SplitToWorld_NewGuidPositionRecovery_EntersSameCanonicalTransactionAsDrop()
    {
        using var fixture = new Fixture(originKnown: true);
        var drop = new DropHarness(fixture);
        const uint sourceGuid = 0x50000C01u;
        fixture.Controller.OnCreate(ItemSpawn(
            sourceGuid, position: null, containerId: DropHarness.PlayerGuid, stackSize: 10));

        Assert.True(drop.DispatchSplit(sourceGuid, stackSize: 10, splitAmount: 3));
        const uint splitResultGuid = 0x80000901u;
        Assert.False(fixture.Runtime.TryGetSnapshot(splitResultGuid, out _));

        bool recovered = drop.Projection.TryRecoverUnknownPosition(
            DropPositionUpdate(splitResultGuid, x: 15f));

        Assert.True(recovered);
        Assert.True(fixture.Runtime.TryGetSnapshot(
            splitResultGuid, out WorldSession.EntitySpawn spawn));
        Assert.Equal(3, spawn.StackSize);
        Assert.Equal(0u, spawn.ContainerId);
        Assert.True(fixture.Runtime.TryGetRecord(splitResultGuid, out LiveEntityRecord record));
        Assert.NotNull(record.WorldEntity);
        Assert.Equal(1, fixture.Resources.RegisterCount);
        Assert.Equal(splitResultGuid, drop.Selection.SelectedObjectId);
    }

    [Fact]
    public void SecondSplitDrop_ResolvesIndependentlyWithoutClobberingFirst()
    {
        using var fixture = new Fixture(originKnown: true);
        var drop = new DropHarness(fixture);
        const uint firstSource = 0x50000C10u;
        const uint secondSource = 0x50000C20u;
        fixture.Controller.OnCreate(ItemSpawn(
            firstSource, position: null, containerId: DropHarness.PlayerGuid, stackSize: 5));
        fixture.Controller.OnCreate(ItemSpawn(
            secondSource, position: null, containerId: DropHarness.PlayerGuid, stackSize: 8));

        Assert.True(drop.DispatchSplit(firstSource, stackSize: 5, splitAmount: 2));
        const uint firstResult = 0x80000A01u;
        Assert.True(drop.Projection.TryRecoverUnknownPosition(
            DropPositionUpdate(firstResult, x: 11f)));
        fixture.Objects.UpdateStackSize(firstSource, 3, 0);

        Assert.True(drop.DispatchSplit(secondSource, stackSize: 8, splitAmount: 4));
        const uint secondResult = 0x80000A02u;
        Assert.True(drop.Projection.TryRecoverUnknownPosition(
            DropPositionUpdate(secondResult, x: 12f, positionSequence: 6)));

        Assert.True(fixture.Runtime.TryGetSnapshot(
            firstResult, out WorldSession.EntitySpawn firstSpawn));
        Assert.True(fixture.Runtime.TryGetSnapshot(
            secondResult, out WorldSession.EntitySpawn secondSpawn));
        Assert.Equal(2, firstSpawn.StackSize);
        Assert.Equal(4, secondSpawn.StackSize);
        Assert.Equal(11f, firstSpawn.Position!.Value.PositionX);
        Assert.Equal(12f, secondSpawn.Position!.Value.PositionX);
        Assert.Equal(2, fixture.Resources.RegisterCount);
    }

    [Fact]
    public void UnknownPositionWithNoPendingSplit_IsRejectedWithoutCreatingAnything()
    {
        using var fixture = new Fixture(originKnown: true);
        var drop = new DropHarness(fixture);
        const uint unrelatedGuid = 0x80000B01u;

        bool recovered = drop.Projection.TryRecoverUnknownPosition(
            DropPositionUpdate(unrelatedGuid, x: 20f));

        Assert.False(recovered);
        Assert.False(fixture.Runtime.TryGetSnapshot(unrelatedGuid, out _));
        Assert.Equal(0, fixture.Resources.RegisterCount);
    }

    [Fact]
    public void NewerPositionAfterPendingConsumed_DoesNotReopenOrDoubleRegister()
    {
        using var fixture = new Fixture(originKnown: true);
        var drop = new DropHarness(fixture);
        const uint sourceGuid = 0x50000C30u;
        fixture.Controller.OnCreate(ItemSpawn(
            sourceGuid, position: null, containerId: DropHarness.PlayerGuid, stackSize: 6));
        Assert.True(drop.DispatchSplit(sourceGuid, stackSize: 6, splitAmount: 1));

        const uint firstUnknown = 0x80000C01u;
        const uint secondUnknown = 0x80000C02u;
        Assert.True(drop.Projection.TryRecoverUnknownPosition(
            DropPositionUpdate(firstUnknown, x: 21f)));

        bool secondRecovered = drop.Projection.TryRecoverUnknownPosition(
            DropPositionUpdate(secondUnknown, x: 22f, positionSequence: 6));

        Assert.False(secondRecovered);
        Assert.False(fixture.Runtime.TryGetSnapshot(secondUnknown, out _));
        Assert.True(fixture.Runtime.TryGetSnapshot(firstUnknown, out _));
        Assert.Equal(firstUnknown, drop.Selection.SelectedObjectId);
        Assert.Equal(1, fixture.Resources.RegisterCount);
    }

    private static WorldSession.EntitySpawn ItemSpawn(
        uint guid,
        CreateObject.ServerPosition? position,
        uint? containerId,
        int? stackSize,
        string name = "test item",
        ushort instanceSequence = 1)
    {
        var timestamps = new PhysicsTimestamps(
            1, 0, 1, 1, 0, 0, 0, 1, instanceSequence);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000050u,
            MotionTableId: 0x09000050u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            Guid: guid,
            Position: position,
            SetupTableId: 0x02000050u,
            AnimPartChanges: [],
            TextureChanges: [],
            SubPalettes: [],
            BasePaletteId: null,
            ObjScale: null,
            Name: name,
            ItemType: (uint)ItemType.Misc,
            MotionState: null,
            MotionTableId: 0x09000050u,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            WeenieClassId: 273u,
            StackSize: stackSize,
            StackSizeMax: 1000,
            ContainerId: containerId,
            InstanceSequence: instanceSequence,
            MovementSequence: 0,
            ServerControlSequence: 0,
            PositionSequence: 1,
            Physics: physics);
    }

    private static WorldSession.EntityPositionUpdate DropPositionUpdate(
        uint guid,
        float x,
        ushort positionSequence = 5) =>
        new(
            guid,
            new CreateObject.ServerPosition(Cell, x, 12f, 6f, 1f, 0f, 0f, 0f),
            Velocity: Vector3.Zero,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: positionSequence,
            TeleportSequence: 0,
            ForcePositionSequence: 0);

    private sealed class DropHarness
    {
        public const uint PlayerGuid = 0x50000001u;
        public readonly ItemInteractionController Interaction;
        public readonly InventoryWorldDropProjectionController Projection;
        public readonly StackSplitQuantityState SplitQuantity = new();
        public readonly AcDream.Core.Selection.SelectionState Selection = new();

        public DropHarness(Fixture fixture)
        {
            fixture.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = PlayerGuid,
                Name = "Player",
                Type = ItemType.Creature,
            });
            var shared = new InventoryTransactionState(fixture.Objects);
            var runtimeTransactions = new RuntimeInteractionTransactionState(shared);
            Interaction = new ItemInteractionController(
                fixture.Objects,
                runtimeTransactions,
                new InteractionState(),
                playerGuid: () => PlayerGuid,
                sendUse: null,
                sendUseWithTarget: null,
                sendWield: null,
                sendDrop: _ => { },
                sendSplitToWorld: (_, _) => { },
                selectedObjectId: () => Selection.SelectedObjectId ?? 0u,
                stackSplitQuantity: SplitQuantity,
                playerOnGround: () => true);
            Projection = new InventoryWorldDropProjectionController(
                Interaction,
                fixture.Objects,
                fixture.Runtime,
                fixture.Controller,
                Selection,
                () => 100.0);
        }

        public bool DispatchSplit(uint itemGuid, uint stackSize, uint splitAmount)
        {
            Selection.Select(
                itemGuid,
                AcDream.Core.Selection.SelectionChangeSource.Inventory);
            SplitQuantity.Reset(stackSize);
            SplitQuantity.SetValue(splitAmount);
            var payload = new ItemDragPayload(
                itemGuid,
                ItemDragSource.Inventory,
                SourceSlot: 0,
                SourceCell: new UiItemSlot());
            return Interaction.DropToWorld(payload);
        }
    }

    private const uint Guid = 0x70000001u;
    private const uint Cell = 0x01010001u;

    private sealed class Fixture : IDisposable
    {
        public readonly List<string> Operations = [];
        public readonly RuntimeEntityObjectLifetime EntityObjects = new();
        public ClientObjectTable Objects => EntityObjects.Objects;
        public readonly RecordingResources Resources;
        public readonly LiveEntityRuntime Runtime;
        public readonly RecordingMaterializer Materializer;
        public readonly RecordingRelationships Relationships;
        public readonly RecordingReadyPublisher Ready;
        public readonly RecordingOrigin Origin;
        public readonly RecordingNetworkSink Network = new();
        public readonly RecordingTeardownCoordinator Teardown = new();
        public readonly RecordingTimestampPublisher Timestamps;
        public readonly LiveEntityHydrationController Controller;

        public Action<uint, AcceptedPhysicsTimestamps>? PublishTimestampsAction
        {
            get => Timestamps.PublishAction;
            set => Timestamps.PublishAction = value;
        }

        public Fixture(
            bool originKnown,
            RecordingResources? resources = null,
            uint playerGuid = 0u)
        {
            Resources = resources ?? new RecordingResources();
            EntityObjects.BindEventContext(
                static () => new AcDream.Runtime.RuntimeGenerationToken(1UL),
                static () => 1UL);
            EntityObjects.Physics.SetPosition.BeginCollisionGeneration(
                Cell & 0xFFFF0000u, 1UL);
            EntityObjects.Physics.Engine.AddLandblock(
                Cell & 0xFFFF0000u,
                new AcDream.Core.Physics.TerrainSurface(
                    new byte[81], new float[256]),
                Array.Empty<AcDream.Core.Physics.CellSurface>(),
                Array.Empty<AcDream.Core.Physics.PortalPlane>(),
                worldOffsetX: 0f,
                worldOffsetY: 0f);
            EntityObjects.Physics.SetPosition.CommitCollisionGeneration(
                Cell & 0xFFFF0000u, 1UL, ready: true);

            EntityObjects.Physics.ObserveLocalWorldFrame(
                Cell,
                teleportAdvanced: false);
            Movement = new AcDream.Runtime.Gameplay.RuntimeLocalPlayerMovementState();
            IdentityState = new AcDream.Runtime.Gameplay.RuntimeLocalPlayerIdentityState();
            var publication = new AcDream.Runtime.Gameplay
                .RuntimeLocalPlayerPhysicsPublicationState(
                    EntityObjects.Entities,
                    EntityObjects.Physics,
                    Movement,
                    IdentityState);
            Movement.AttachPhysicsPublication(publication);
            EntityObjects.LocalPlayerFirstEntry.BindPublication(publication);
            IdentityState.ServerGuid = playerGuid;
            FirstEntry = new AcDream.Runtime.Session.RuntimeFirstEntryDriveController(
                EntityObjects,
                new AcDream.Runtime.GameRuntimeClock(),
                new HydrationNullCollisionSource(),
                () => AcDream.Runtime.Gameplay.PlayerMovementConstructionOptions.Fallback,
                static _ => new AcDream.Runtime.Gameplay
                    .RuntimeLocalPlayerPhysicsActivationPreparation(
                        0.48f,
                        1.835f,
                        AcDream.Runtime.Gameplay
                            .RuntimeLocalPlayerShadowDisposition.ProvenShapeless));
            var spatial = new GpuWorldState();
            spatial.AddLandblock(new LoadedLandblock(
                0x0101FFFFu,
                new DatReaderWriter.DBObjs.LandBlock(),
                Array.Empty<WorldEntity>()));
            Runtime = new LiveEntityRuntime(
                spatial,
                Resources,
                Teardown,
                EntityObjects);
            _placements = new AcDream.Runtime.Physics
                .RuntimePlacementProjectionSubscription(
                    EntityObjects.Placements,
                    static () => new AcDream.Runtime.RuntimeGenerationToken(1UL),
                    new FixturePlacementSink(Runtime));
            Materializer = new RecordingMaterializer(Runtime, Operations);
            Relationships = new RecordingRelationships(Operations);
            Ready = new RecordingReadyPublisher(Operations);
            Origin = new RecordingOrigin(originKnown);
            Timestamps = new RecordingTimestampPublisher(Operations);
            Network.ApplyAction = events =>
            {
                if (events.Position is not { } position)
                    return;

                Runtime.TryApplyPosition(
                    position,
                    isLocalPlayer: false,
                    forcePositionRotation: null,
                    currentLocalVelocity: null,
                    out _,
                    out _,
                    out _);
            };
            var identity = new LocalPlayerIdentityState
            {
                ServerGuid = playerGuid,
            };
            var deletion = new LiveEntityDeletionController(
                Runtime,
                EntityObjects,
                Teardown,
                identity);
            Controller = new LiveEntityHydrationController(
                Runtime,
                EntityObjects,
                new object(),
                Materializer,
                Relationships,
                Ready,
                Origin,
                Network,
                Timestamps,
                identity,
                deletion,
                firstEntry: FirstEntry);
        }

        public AcDream.Runtime.Session.RuntimeFirstEntryDriveController FirstEntry { get; }
        private readonly AcDream.Runtime.Physics
            .RuntimePlacementProjectionSubscription _placements;

        private sealed class FixturePlacementSink(LiveEntityRuntime runtime)
            : AcDream.Runtime.Physics.IRuntimePlacementProjectionSink
        {
            public bool TryApply(
                in AcDream.Runtime.Physics.RuntimePlacementProjectionSnapshot projection)
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
        public AcDream.Runtime.Gameplay.RuntimeLocalPlayerMovementState Movement { get; }
        public AcDream.Runtime.Gameplay.RuntimeLocalPlayerIdentityState IdentityState { get; }

        private sealed class HydrationNullCollisionSource
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

        public LiveEntityRecord Record
        {
            get
            {
                Assert.True(Runtime.TryGetRecord(Guid, out LiveEntityRecord record));
                return record;
            }
        }

        public RuntimeEntityRecord Canonical
        {
            get
            {
                Assert.True(Runtime.TryGetCanonical(
                    Guid,
                    out RuntimeEntityRecord canonical));
                return canonical;
            }
        }

        public void Dispose()
        {
            try
            {
                Runtime.Clear();
            }
            catch
            {
                // Individual failure-path tests assert the relevant exception.
            }
        }
    }

    private sealed class RecordingTimestampPublisher
        : IAcceptedLocalPhysicsTimestampPublisher
    {
        private readonly List<string> _operations;

        public RecordingTimestampPublisher(List<string> operations) =>
            _operations = operations;

        public Action<uint, AcceptedPhysicsTimestamps>? PublishAction { get; set; }

        public void Publish(uint serverGuid, AcceptedPhysicsTimestamps timestamps)
        {
            _operations.Add($"timestamps:{timestamps.Instance}");
            PublishAction?.Invoke(serverGuid, timestamps);
        }
    }

    private class RecordingResources : ILiveEntityResourceLifecycle
    {
        public int RegisterCount { get; protected set; }
        public int UnregisterCount { get; protected set; }
        public virtual void Register(WorldEntity entity) => RegisterCount++;
        public virtual void Unregister(WorldEntity entity) => UnregisterCount++;
    }

    private sealed class FailingOnceResources : RecordingResources
    {
        private bool _fail = true;

        public override void Register(WorldEntity entity)
        {
            base.Register(entity);
            if (_fail)
            {
                _fail = false;
                throw new InvalidOperationException("fixture registration failure");
            }
        }
    }

    private sealed class FailingRegisterAndRollbackOnceResources : RecordingResources
    {
        private bool _failRegister = true;
        private bool _failRollback = true;

        public override void Register(WorldEntity entity)
        {
            base.Register(entity);
            if (_failRegister)
            {
                _failRegister = false;
                throw new InvalidOperationException("fixture registration failure");
            }
        }

        public override void Unregister(WorldEntity entity)
        {
            base.Unregister(entity);
            if (_failRollback)
            {
                _failRollback = false;
                throw new InvalidOperationException("fixture rollback failure");
            }
        }
    }

    private sealed class FailingUnregisterOnceResources : RecordingResources
    {
        private bool _fail = true;

        public override void Unregister(WorldEntity entity)
        {
            base.Unregister(entity);
            if (_fail)
            {
                _fail = false;
                throw new InvalidOperationException("fixture generation cleanup failure");
            }
        }
    }

    private sealed class RecordingMaterializer(
        LiveEntityRuntime runtime,
        List<string> operations) : ILiveEntityProjectionMaterializer
    {
        public readonly List<(LiveProjectionPurpose Purpose, ushort Generation)> Calls = [];
        public readonly List<ushort> PositionSequences = [];
        public ulong? InstalledCreateIntegrationVersion { get; private set; }
        public Action<RuntimeEntityRecord, WorldSession.EntitySpawn>? BeforeMaterialize { get; set; }
        public Action<RuntimeEntityRecord, WorldSession.EntitySpawn>? AfterMaterialize { get; set; }
        public LiveProjectionPurpose? ThrowAfterMaterializePurposeOnce { get; set; }
        public int SkipMaterializationCount { get; set; }

        public bool TryMaterialize(
            RuntimeEntityRecord expectedCanonical,
            WorldSession.EntitySpawn canonicalSpawn,
            LiveProjectionPurpose purpose,
            ulong expectedCreateIntegrationVersion,
            AcDream.App.Rendering.LiveEntityAppearanceUpdateState? appearanceUpdate = null)
        {
            Calls.Add((purpose, expectedCanonical.Generation));
            PositionSequences.Add(canonicalSpawn.PositionSequence);
            operations.Add($"materialize:{purpose}:{expectedCanonical.Generation}");
            BeforeMaterialize?.Invoke(expectedCanonical, canonicalSpawn);
            if (!runtime.IsCurrentCreateIntegration(
                    expectedCanonical,
                    expectedCreateIntegrationVersion))
            {
                return false;
            }
            if (InstalledCreateIntegrationVersion is null
                || purpose is LiveProjectionPurpose.CreateSupersessionRecovery)
            {
                InstalledCreateIntegrationVersion = expectedCreateIntegrationVersion;
            }
            if (SkipMaterializationCount > 0)
            {
                SkipMaterializationCount--;
                return false;
            }
            if (canonicalSpawn.Position is not { } position
                || canonicalSpawn.SetupTableId is null)
            {
                return false;
            }

            WorldEntity? entity = runtime.MaterializeLiveEntity(
                expectedCanonical,
                position.LandblockId,
                id => new WorldEntity
                {
                    Id = id,
                    ServerGuid = canonicalSpawn.Guid,
                    SourceGfxObjOrSetupId = canonicalSpawn.SetupTableId.Value,
                    Position = new Vector3(position.PositionX, position.PositionY, position.PositionZ),
                    Rotation = Quaternion.Identity,
                    MeshRefs = [],
                    ParentCellId = position.LandblockId,
                },
                LiveEntityProjectionKind.World,
                initializeProjection: null,
                out LiveEntityRecord? expectedRecord,
                AcDream.App.World.LiveEntityMaterializationResidence
                    .AwaitRuntimePlacement);
            if (entity is not null
                && expectedRecord is not null
                && runtime.IsCurrentCreateIntegration(
                    expectedCanonical,
                    expectedCreateIntegrationVersion)
                && expectedCanonical.FullCellId != 0u
                && !runtime.HasActiveInitialCreateResidence(expectedCanonical)
                && !runtime.RebucketLiveEntity(
                    canonicalSpawn.Guid,
                    expectedCanonical.FullCellId))
            {
                return false;
            }
            if (ThrowAfterMaterializePurposeOnce == purpose)
            {
                ThrowAfterMaterializePurposeOnce = null;
                throw new InvalidOperationException(
                    "fixture supersession projection failure");
            }
            AfterMaterialize?.Invoke(expectedCanonical, canonicalSpawn);
            return entity is not null
                && expectedRecord is not null
                && runtime.IsCurrentRecord(expectedRecord);
        }

        public void ResetSessionState() { }
    }

    private sealed class RecordingRelationships(List<string> operations)
        : ILiveEntityRelationshipProjection
    {
        public Action<WorldSession.EntitySpawn>? OnSpawnAction { get; set; }
        public Action<ParentEvent.Parsed>? OnParentAction { get; set; }
        public Action<CreateParentUpdate>? OnCreateParentAction { get; set; }
        public Func<uint, ChildUnparentDisposition>? OnUnparentAction { get; set; }
        public Func<LiveEntityRecord, ulong, bool>? ApplyAttachedAppearance { get; set; }
        public void OnSpawn(WorldSession.EntitySpawn spawn)
        {
            operations.Add($"relationship:{spawn.InstanceSequence}");
            OnSpawnAction?.Invoke(spawn);
        }

        public void OnParent(ParentEvent.Parsed update)
        {
            operations.Add($"parent:{update.ChildGuid:X8}");
            OnParentAction?.Invoke(update);
        }

        public void OnCreateParentAccepted(CreateParentUpdate update)
        {
            operations.Add($"create-parent:{update.ChildGuid:X8}");
            OnCreateParentAction?.Invoke(update);
        }

        public ChildUnparentDisposition OnChildBecameUnparented(uint childGuid)
        {
            operations.Add($"unparent:{childGuid:X8}");
            return OnUnparentAction?.Invoke(childGuid)
                ?? ChildUnparentDisposition.Completed;
        }

        public bool TryApplyAttachedAppearance(
            LiveEntityRecord record,
            ulong objDescAuthorityVersion)
        {
            operations.Add($"attached-appearance:{record.ServerGuid:X8}");
            return ApplyAttachedAppearance?.Invoke(record, objDescAuthorityVersion)
                ?? false;
        }
    }

    private sealed class RecordingReadyPublisher(List<string> operations)
        : ILiveEntityReadyPublisher
    {
        public int PublishCount { get; private set; }
        public int FailPublishCount { get; set; }
        public Action<LiveEntityRecord>? BeforePublish { get; set; }
        public bool Publish(LiveEntityReadyCandidate candidate)
        {
            LiveEntityRecord expectedRecord = candidate.Record;
            PublishCount++;
            operations.Add("ready");
            BeforePublish?.Invoke(expectedRecord);
            if (FailPublishCount > 0)
            {
                FailPublishCount--;
                return false;
            }
            return true;
        }
    }

    private sealed class RecordingLiveProjectionSink(
        Func<LiveEntityReadyCandidate, bool> ready)
        : ILiveRenderProjectionSink
    {
        public bool OnEntityReady(LiveEntityReadyCandidate candidate) =>
            ready(candidate);

        public void OnProjectionVisibilityChanged(
            LiveEntityRecord record,
            bool visible)
        {
        }

        public void OnProjectionPoseReady(uint serverGuid)
        {
        }

        public void OnProjectionRemoved(LiveEntityRecord record)
        {
        }

        public void OnResourceUnregister(WorldEntity entity)
        {
        }

        public void SynchronizeActiveSources()
        {
        }
    }

    private sealed class RecordingOrigin(bool isKnown) : ILiveEntityWorldOriginCoordinator
    {
        public bool IsKnownValue { get; set; } = isKnown;
        public IReadOnlyList<uint> AlreadyLoadedLandblocks { get; set; } = [];
        public bool IsKnown => IsKnownValue;
        public LiveEntityOriginInitialization TryInitialize(WorldSession.EntitySpawn spawn) =>
            new(IsKnownValue, AlreadyLoadedLandblocks);
    }

    private sealed class RecordingNetworkSink : ILiveEntityNetworkUpdateSink
    {
        public int ApplyCount { get; private set; }
        public Action<SameGenerationCreateObjectEvents>? ApplyAction { get; set; }
        public void ApplySameGeneration(SameGenerationCreateObjectEvents events)
        {
            ApplyCount++;
            ApplyAction?.Invoke(events);
        }
    }

    private sealed class RecordingTeardownCoordinator
        : ILiveEntityTeardownCoordinator
    {
        public int TearDownCount { get; private set; }
        public List<uint> ForgottenUnknownOwners { get; } = [];
        public Action<LiveEntityRecord>? TearDownAction { get; set; }

        public void TearDown(LiveEntityRecord record)
        {
            TearDownCount++;
            TearDownAction?.Invoke(record);
        }

        public void ForgetUnknownOwner(uint serverGuid) =>
            ForgottenUnknownOwners.Add(serverGuid);
    }

    private static WorldSession.EntitySpawn Spawn(
        ushort Generation,
        ushort PositionSequence,
        string Name = "fixture")
    {
        var position = new CreateObject.ServerPosition(
            Cell, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(
            PositionSequence, 1, 1, 1, 0, 1, 0, 1, Generation);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            Guid,
            position,
            0x02000001u,
            [],
            [],
            [],
            null,
            null,
            Name,
            (uint)ItemType.Creature,
            null,
            0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: Generation,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: PositionSequence,
            Physics: physics);
    }

    private static ObjDescEvent.Parsed ObjDesc(
        ushort sequence,
        uint basePaletteId) =>
        new(
            Guid,
            new CreateObject.ModelData(
                basePaletteId,
                Array.Empty<CreateObject.SubPaletteSwap>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.AnimPartChange>()),
            InstanceSequence: 1,
            ObjDescSequence: sequence);

    private static WorldSession.EntitySpawn CelllessSpawn(
        ushort PositionSequence,
        uint? parentGuid)
    {
        var timestamps = new PhysicsTimestamps(
            PositionSequence, 1, 1, 1, 0, 1, 0, 1, 1);
        PhysicsAttachment? parent = parentGuid is { } guid
            ? new PhysicsAttachment(guid, LocationId: 1u)
            : null;
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
            Position: null,
            Movement: null,
            AnimationFrame: parentGuid is null ? null : 1u,
            SetupTableId: 0x02000001u,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: parent,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            Guid,
            Position: null,
            SetupTableId: 0x02000001u,
            AnimPartChanges: [],
            TextureChanges: [],
            SubPalettes: [],
            BasePaletteId: null,
            ObjScale: null,
            Name: parentGuid is null ? "pickup" : "parented",
            ItemType: (uint)ItemType.Creature,
            MotionState: null,
            MotionTableId: 0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: PositionSequence,
            ParentGuid: parentGuid,
            ParentLocation: parentGuid is null ? null : 1u,
            PlacementId: parentGuid is null ? null : 1u,
            Physics: physics);
    }

    private static WorldSession.EntityPositionUpdate PositionUpdate(
        ushort sequence,
        float x) =>
        new(
            Guid,
            new CreateObject.ServerPosition(
                Cell, x, 12f, 6f, 1f, 0f, 0f, 0f),
            Velocity: null,
            PlacementId: 0,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: sequence,
            TeleportSequence: 0,
            ForcePositionSequence: 0);
}
