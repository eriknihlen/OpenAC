using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Collections.Immutable;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Scene.Arch;
using AcDream.App.Rendering.Walk;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using AcDream.Runtime.Entities;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Rendering;

public sealed class EquippedChildProjectionWithdrawalTests
{
    [Fact]
    public void WithdrawalFailure_DoesNotPublishAttachedProjectionRemoval()
    {
        bool published = false;

        ExactProjectionWithdrawalOutcome outcome =
            EquippedChildRenderController.WithdrawAttachedProjection(
                ChildRecord(),
                positionAuthorityVersion: 1,
                projectionMutationVersion: 2,
                (_, _, _) => new ExactProjectionWithdrawalOutcome(
                    ExactProjectionWithdrawalDisposition.Pending,
                    new InvalidOperationException("injected component cleanup failure")),
                () =>
                {
                    published = true;
                    return true;
                });

        Assert.IsType<InvalidOperationException>(outcome.Failure);
        Assert.Equal(ExactProjectionWithdrawalDisposition.Pending, outcome.Disposition);
        Assert.False(published);
    }

    [Fact]
    public void ExactIncarnationRejection_DoesNotPublishAttachedProjectionRemoval()
    {
        bool published = false;

        ExactProjectionWithdrawalOutcome outcome = EquippedChildRenderController.WithdrawAttachedProjection(
            ChildRecord(),
            positionAuthorityVersion: 1,
            projectionMutationVersion: 2,
            (_, _, _) => new ExactProjectionWithdrawalOutcome(
                ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null),
            () =>
            {
                published = true;
                return true;
            });

        Assert.True(published);
        Assert.Equal(ExactProjectionWithdrawalDisposition.Superseded, outcome.Disposition);
    }

    [Fact]
    public void SuccessfulWithdrawal_PublishesLocalProjectionRemovalOnce()
    {
        int publications = 0;

        ExactProjectionWithdrawalOutcome outcome = EquippedChildRenderController.WithdrawAttachedProjection(
            ChildRecord(),
            positionAuthorityVersion: 1,
            projectionMutationVersion: 2,
            (_, _, _) => new ExactProjectionWithdrawalOutcome(
                ExactProjectionWithdrawalDisposition.Completed,
                Failure: null),
            () =>
            {
                publications++;
                return true;
            });

        Assert.Equal(ExactProjectionWithdrawalDisposition.Completed, outcome.Disposition);
        Assert.Equal(1, publications);
    }

    [Fact]
    public void LogicalReplacement_RemovesExactAttachedMapWithoutWithdrawingReplacement()
    {
        int withdrawals = 0;
        using var fixture = new ControllerFixture(
            (_, _, _) =>
            {
                withdrawals++;
                return new(
                    ExactProjectionWithdrawalDisposition.Completed,
                    Failure: null);
            });
        LiveEntityRecord parent = fixture.Spawn(0x70000200u, generation: 1);
        LiveEntityRecord oldChild = fixture.Spawn(
            0x70000201u,
            generation: 1,
            LiveEntityProjectionKind.Attached);
        fixture.InstallAttached(parent, oldChild);

        LiveEntityRecord replacement = fixture.Live.RegisterAndMaterializeProjection(
            ControllerFixture.SpawnData(0x70000201u, generation: 2));

        Assert.Empty(fixture.Controller.AttachedEntityIds);
        Assert.True(fixture.Live.TryGetRecord(0x70000201u, out LiveEntityRecord current));
        Assert.Same(replacement, current);
        Assert.Equal(0, withdrawals);
    }

    [Fact]
    public void LogicalDelete_NotificationFailureLeavesRetryableTombstoneButNoAttachedLeak()
    {
        using var fixture = new ControllerFixture((_, _, _) =>
            new(ExactProjectionWithdrawalDisposition.Completed, Failure: null));
        LiveEntityRecord parent = fixture.Spawn(0x70000210u, generation: 1);
        LiveEntityRecord child = fixture.Spawn(
            0x70000211u,
            generation: 1,
            LiveEntityProjectionKind.Attached);
        fixture.InstallAttached(parent, child);
        fixture.Controller.ProjectionRemoved += Throw;

        Assert.Throws<AggregateException>(() => fixture.Live.UnregisterLiveEntity(
            new DeleteObject.Parsed(0x70000211u, InstanceSequence: 1),
            isLocalPlayer: false));
        Assert.Empty(fixture.Controller.AttachedEntityIds);

        fixture.Controller.ProjectionRemoved -= Throw;
        Assert.Equal(1, fixture.Live.RetryPendingTeardowns());

        static void Throw(LiveEntityRecord _) =>
            throw new InvalidOperationException("injected projection observer failure");
    }

    [Fact]
    public void PostCommitWithdrawalFailure_RetiresCapturedAttachedMap()
    {
        ControllerFixture? fixture = null;
        fixture = new ControllerFixture((record, positionVersion, projectionVersion) =>
        {
            bool committed = fixture!.Live.WithdrawLiveEntityProjection(
                record,
                positionVersion,
                projectionVersion);
            Assert.True(committed);
            return new(
                ExactProjectionWithdrawalDisposition.Completed,
                new InvalidOperationException("observer failed after commit"));
        });
        using (fixture)
        {
            LiveEntityRecord parent = fixture.Spawn(0x70000220u, generation: 1);
            LiveEntityRecord child = fixture.Spawn(
                0x70000221u,
                generation: 1,
                LiveEntityProjectionKind.Attached);
            fixture.InstallAttached(parent, child);

            Assert.Throws<InvalidOperationException>(() =>
                fixture.Controller.OnChildBecameUnparented(0x70000221u));
            Assert.Empty(fixture.Controller.AttachedEntityIds);
            Assert.False(child.IsSpatiallyProjected);
        }
    }

    [Fact]
    public void PendingTopLevelWithdrawal_RetryRunsAcceptedContinuationOnce()
    {
        int attempts = 0;
        ControllerFixture? fixture = null;
        fixture = new ControllerFixture((record, positionVersion, projectionVersion) =>
        {
            attempts++;
            if (attempts == 1)
            {
                return new(
                    ExactProjectionWithdrawalDisposition.Pending,
                    new InvalidOperationException("component cleanup failed"));
            }
            bool committed = fixture!.Live.WithdrawLiveEntityProjection(
                record,
                positionVersion,
                projectionVersion);
            return new(
                committed
                    ? ExactProjectionWithdrawalDisposition.Completed
                    : ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        });
        using (fixture)
        {
            LiveEntityRecord record = fixture.Spawn(0x70000230u, generation: 1);
            int continuations = 0;
            Assert.Throws<InvalidOperationException>(() =>
                fixture.Controller.OnChildBecameUnparented(
                    0x70000230u,
                    () =>
                    {
                        continuations++;
                        Assert.True(fixture.Live.RebucketLiveEntity(
                            0x70000230u,
                            0x01010001u));
                    }));

            fixture.Controller.Tick();

            Assert.Equal(2, attempts);
            Assert.Equal(1, continuations);
            Assert.True(record.IsSpatiallyProjected);
        }
    }

    [Fact]
    public void PendingUnparent_NewerPositionSupersedesRecoveryContinuation()
    {
        using var fixture = new ControllerFixture((_, _, _) =>
            new(
                ExactProjectionWithdrawalDisposition.Pending,
                new InvalidOperationException("component cleanup failed")));
        LiveEntityRecord record = fixture.Spawn(0x70000240u, generation: 1);
        int continuations = 0;
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Controller.OnChildBecameUnparented(
                0x70000240u,
                () => continuations++));

        var newer = new WorldSession.EntityPositionUpdate(
            0x70000240u,
            new CreateObject.ServerPosition(
                0x01010001u, 4f, 5f, 6f, 1f, 0f, 0f, 0f),
            Velocity: null,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: 2,
            TeleportSequence: 0,
            ForcePositionSequence: 0);
        Assert.True(fixture.Live.TryApplyPosition(
            newer,
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            out _,
            out _,
            out _));

        fixture.Controller.Tick();

        Assert.Equal(0, continuations);
        Assert.True(record.IsSpatiallyProjected);
    }

    [Fact]
    public void PostNetworkReconcile_SkipsStableTree_AndUpdatesChangedBranchParentFirst()
    {
        using var fixture = new ControllerFixture((_, _, _) =>
            new(
                ExactProjectionWithdrawalDisposition.Completed,
                Failure: null));
        LiveEntityRecord root = fixture.Spawn(0x70000241u, generation: 1);
        LiveEntityRecord child = fixture.Spawn(
            0x70000242u,
            generation: 1,
            LiveEntityProjectionKind.Attached);
        LiveEntityRecord grandchild = fixture.Spawn(
            0x70000243u,
            generation: 1,
            LiveEntityProjectionKind.Attached);
        fixture.InstallAttached(root, child);
        fixture.InstallAttached(child, grandchild);
        fixture.Poses.Publish(root.WorldEntity!, Array.Empty<Matrix4x4>());

        fixture.Controller.Tick();
        Assert.Equal(2, fixture.Controller.LastFullPoseCompositionVisits);

        fixture.Controller.ReconcileSpatialMutations();
        Assert.Equal(0, fixture.Controller.LastReconcilePoseCompositionVisits);

        root.WorldEntity!.SetPosition(new Vector3(7f, 8f, 9f));
        Assert.True(fixture.Poses.UpdateRoot(root.WorldEntity));
        fixture.Controller.ReconcileSpatialMutations();

        Assert.Equal(2, fixture.Controller.LastReconcilePoseCompositionVisits);
        Assert.Equal(root.WorldEntity.Position, child.WorldEntity!.Position);
        Assert.Equal(child.WorldEntity.Position, grandchild.WorldEntity!.Position);
    }

    [Fact]
    public void StablePostNetworkReconcile_AllocatesNoTransitionSnapshots()
    {
        using var fixture = new ControllerFixture((_, _, _) =>
            new(
                ExactProjectionWithdrawalDisposition.Completed,
                Failure: null));
        LiveEntityRecord root = fixture.Spawn(0x70000244u, generation: 1);
        LiveEntityRecord child = fixture.Spawn(
            0x70000245u,
            generation: 1,
            LiveEntityProjectionKind.Attached);
        fixture.InstallAttached(root, child);
        fixture.Poses.Publish(root.WorldEntity!, Array.Empty<Matrix4x4>());
        fixture.Controller.Tick();
        fixture.Controller.ReconcileSpatialMutations();

        ZeroAllocationProbe.AssertAllocatesNothing(
            "LiveEntityController.ReconcileSpatialMutations",
            () => fixture.Controller.ReconcileSpatialMutations());

        Assert.Equal(0, fixture.Controller.LastReconcilePoseCompositionVisits);
    }

    [Fact]
    public void TickChild_D4_PresentationBucketMovesToTheDestinationLandblock()
    {
        using var fixture = new ControllerFixture((_, _, _) =>
            new(ExactProjectionWithdrawalDisposition.Completed, Failure: null));
        const uint oldLandblock = 0x0101FFFFu;
        const uint newCell = 0x01020001u;
        const uint newLandblock = 0x0102FFFFu;
        fixture.Spatial.AddLandblock(new LoadedLandblock(
            newLandblock, new LandBlock(), Array.Empty<WorldEntity>()));

        LiveEntityRecord parent = fixture.Spawn(0x70000260u, generation: 1);
        LiveEntityRecord child = fixture.Spawn(
            0x70000261u,
            generation: 1,
            LiveEntityProjectionKind.Attached);
        fixture.InstallAttached(parent, child);
        fixture.Poses.Publish(parent.WorldEntity!, Array.Empty<Matrix4x4>());

        var relation = new ParentAttachmentRelation(
            parent.ServerGuid, child.ServerGuid, 0, 0, 1, 1);
        fixture.CommitRenderedRelation(relation);

        var buffer = new List<KeyValuePair<uint, WorldEntity>>();
        fixture.Spatial.CopyLiveEntitiesNearLandblock(oldLandblock, 0, buffer);
        Assert.Contains(buffer, kv => kv.Value == child.WorldEntity);
        fixture.Spatial.CopyLiveEntitiesNearLandblock(newLandblock, 0, buffer);
        Assert.DoesNotContain(buffer, kv => kv.Value == child.WorldEntity);

        Assert.True(fixture.EntityObjects.CommitRebucket(
            parent.Canonical, newCell, newLandblock));
        Assert.Equal(newCell, child.Canonical.FullCellId);
        ulong childSpatialVersionBeforeTick =
            child.Canonical.SpatialAuthorityVersion;

        parent.WorldEntity!.ParentCellId = newCell;
        child.WorldEntity!.ParentCellId = 0x01010001u;

        fixture.Controller.Tick();

        fixture.Spatial.CopyLiveEntitiesNearLandblock(newLandblock, 0, buffer);
        Assert.Contains(buffer, kv => kv.Value == child.WorldEntity);
        fixture.Spatial.CopyLiveEntitiesNearLandblock(oldLandblock, 0, buffer);
        Assert.DoesNotContain(buffer, kv => kv.Value == child.WorldEntity);
        Assert.Equal(
            childSpatialVersionBeforeTick,
            child.Canonical.SpatialAuthorityVersion);
    }

    [Fact]
    public void TickChild_D4_NotAttachedDisposition_SkipsTheBucketMoveWithoutFailingTheTick()
    {
        using var fixture = new ControllerFixture((_, _, _) =>
            new(ExactProjectionWithdrawalDisposition.Completed, Failure: null));
        const uint oldLandblock = 0x0101FFFFu;
        const uint newCell = 0x01020001u;
        const uint newLandblock = 0x0102FFFFu;
        fixture.Spatial.AddLandblock(new LoadedLandblock(
            newLandblock, new LandBlock(), Array.Empty<WorldEntity>()));

        LiveEntityRecord parent = fixture.Spawn(0x70000262u, generation: 1);
        LiveEntityRecord child = fixture.Spawn(
            0x70000263u,
            generation: 1,
            LiveEntityProjectionKind.Attached);
        fixture.InstallAttached(parent, child);
        fixture.Poses.Publish(parent.WorldEntity!, Array.Empty<Matrix4x4>());

        var relation = new ParentAttachmentRelation(
            parent.ServerGuid, child.ServerGuid, 0, 0, 1, 1);
        fixture.CommitRenderedRelation(relation);

        Assert.True(fixture.EntityObjects.CommitRebucket(
            parent.Canonical, newCell, newLandblock));
        parent.WorldEntity!.ParentCellId = newCell;

        Assert.True(fixture.Live.ParentAttachments.HasCommittedParent(
            child.ServerGuid));
        fixture.Live.ParentAttachments.EndChildProjection(child.ServerGuid);
        Assert.False(fixture.Live.ParentAttachments.HasCommittedParent(
            child.ServerGuid));

        int visitsBefore = fixture.Controller.LastFullPoseCompositionVisits;
        fixture.Controller.Tick();

        Assert.Equal(visitsBefore + 1, fixture.Controller.LastFullPoseCompositionVisits);
        Assert.Contains(child.WorldEntity!.Id, fixture.Controller.AttachedEntityIds);
        var buffer = new List<KeyValuePair<uint, WorldEntity>>();
        fixture.Spatial.CopyLiveEntitiesNearLandblock(oldLandblock, 0, buffer);
        Assert.Contains(buffer, kv => kv.Value == child.WorldEntity);
        fixture.Spatial.CopyLiveEntitiesNearLandblock(newLandblock, 0, buffer);
        Assert.DoesNotContain(buffer, kv => kv.Value == child.WorldEntity);
    }

    [Fact]
    public void RebucketEquippedChildPresentation_D4_NoLiveProjection_ReturnsNoProjectionDisposition()
    {
        using var fixture = new ControllerFixture((_, _, _) =>
            new(ExactProjectionWithdrawalDisposition.Completed, Failure: null));
        LiveEntityRecord parent = fixture.Spawn(0x70000264u, generation: 1);
        LiveEntityRecord child = fixture.Spawn(
            0x70000265u,
            generation: 1,
            LiveEntityProjectionKind.Attached);
        fixture.InstallAttached(parent, child);
        fixture.Poses.Publish(parent.WorldEntity!, Array.Empty<Matrix4x4>());

        var relation = new ParentAttachmentRelation(
            parent.ServerGuid, child.ServerGuid, 0, 0, 1, 1);
        fixture.CommitRenderedRelation(relation);

        Assert.True(fixture.Live.ParentAttachments.HasCommittedParent(
            child.ServerGuid));
        fixture.RemoveProjectionOnly(child);

        EquippedChildPresentationRebucketDisposition disposition =
            fixture.Live.RebucketEquippedChildPresentation(
                child.ServerGuid,
                0x01010001u);

        Assert.Equal(
            EquippedChildPresentationRebucketDisposition.NoProjection,
            disposition);
    }

    [Fact]
    public void AcceptedValidParent_WithdrawsWorldProjectionBeforePosePrerequisitesExist()
    {
        ControllerFixture? fixture = null;
        fixture = new ControllerFixture((record, positionVersion, projectionVersion) =>
        {
            bool completed = fixture!.Live.WithdrawLiveEntityProjection(
                record,
                positionVersion,
                projectionVersion);
            return new(
                completed
                    ? ExactProjectionWithdrawalDisposition.Completed
                    : ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        });
        using (fixture)
        {
            fixture.Spawn(0x70000250u, generation: 1);
            LiveEntityRecord child = fixture.Spawn(0x70000251u, generation: 1);

            fixture.Controller.OnParentEvent(new ParentEvent.Parsed(
                ParentGuid: 0x70000250u,
                ChildGuid: 0x70000251u,
                ParentLocation: 0,
                PlacementId: 0,
                ParentInstanceSequence: 1,
                ChildPositionSequence: 2));

            Assert.False(child.IsSpatiallyProjected);
            Assert.True(fixture.Live.ParentAttachments.TryGetProjection(
                0x70000251u,
                out _));
        }
    }

    [Fact]
    public void InvalidParent_ConsumesPositionTimestampButPreservesWorldProjection()
    {
        ControllerFixture? fixture = null;
        fixture = new ControllerFixture((record, positionVersion, projectionVersion) =>
        {
            bool completed = fixture!.Live.WithdrawLiveEntityProjection(
                record,
                positionVersion,
                projectionVersion);
            return new(
                completed
                    ? ExactProjectionWithdrawalDisposition.Completed
                    : ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        });
        using (fixture)
        {
            fixture.Spawn(0x70000252u, generation: 1);
            LiveEntityRecord child = fixture.Spawn(0x70000253u, generation: 1);

            fixture.Controller.OnParentEvent(new ParentEvent.Parsed(
                ParentGuid: 0x70000252u,
                ChildGuid: 0x70000253u,
                ParentLocation: 1,
                PlacementId: 0,
                ParentInstanceSequence: 1,
                ChildPositionSequence: 2));

            Assert.True(child.IsSpatiallyProjected);
            Assert.NotEqual(0u, child.FullCellId);
            Assert.True(fixture.Live.TryGetSnapshot(
                child.ServerGuid,
                out WorldSession.EntitySpawn snapshot));
            Assert.NotNull(snapshot.Position);
            Assert.Null(snapshot.ParentGuid);
            Assert.Equal((ushort)2, snapshot.PositionSequence);
            Assert.False(fixture.Live.ParentAttachments.TryGetProjection(
                child.ServerGuid,
                out _));
        }
    }

    [Fact]
    public void QueuedValidThenInvalidParent_LeavesValidParentCommitted()
    {
        ControllerFixture? fixture = null;
        fixture = new ControllerFixture((record, positionVersion, projectionVersion) =>
        {
            bool completed = fixture!.Live.WithdrawLiveEntityProjection(
                record,
                positionVersion,
                projectionVersion);
            return new(
                completed
                    ? ExactProjectionWithdrawalDisposition.Completed
                    : ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        });
        using (fixture)
        {
            LiveEntityRecord validParent = fixture.Spawn(0x70000259u, generation: 1);
            LiveEntityRecord invalidParent = fixture.Spawn(0x7000025Au, generation: 1);
            const uint childGuid = 0x7000025Bu;
            fixture.Controller.OnParentEvent(new ParentEvent.Parsed(
                validParent.ServerGuid, childGuid, 0, 0, 1, 1));
            fixture.Controller.OnParentEvent(new ParentEvent.Parsed(
                invalidParent.ServerGuid, childGuid, 1, 0, 1, 2));

            RuntimeEntityRecord childIdentity = fixture.RegisterOnly(
                childGuid,
                generation: 1,
                hasPosition: true);
            LiveEntityRecord child = fixture.Materialize(childIdentity);
            Assert.True(fixture.Live.TryGetSnapshot(
                childGuid,
                out WorldSession.EntitySpawn childSpawn));
            fixture.Controller.OnSpawn(childSpawn);

            Assert.True(fixture.Live.TryGetSnapshot(childGuid, out childSpawn));
            Assert.Equal(validParent.ServerGuid, childSpawn.ParentGuid);
            Assert.Null(childSpawn.Position);
            Assert.Equal((ushort)2, childSpawn.PositionSequence);
            var valid = new ParentAttachmentRelation(
                validParent.ServerGuid, childGuid, 0, 0, 1, 1);
            Assert.True(fixture.Live.ParentAttachments.IsCommitted(valid));
            Assert.False(fixture.Live.ParentAttachments.TryGetStagedProjection(
                childGuid,
                out _));
        }
    }

    [Fact]
    public void NoPositionCreateParent_CommitsAfterParentPartArrayValidation()
    {
        using var fixture = new ControllerFixture((_, _, _) =>
            new ExactProjectionWithdrawalOutcome(
                ExactProjectionWithdrawalDisposition.Completed,
                Failure: null));
        LiveEntityRecord parent = fixture.Spawn(0x70000254u, generation: 1);
        RuntimeEntityRecord child = fixture.RegisterOnly(
            0x70000255u,
            generation: 1,
            hasPosition: false);
        var update = new CreateParentUpdate(
            child.ServerGuid,
            parent.ServerGuid,
            ParentLocation: 0,
            PlacementId: 0,
            ChildInstanceSequence: 1,
            ChildPositionSequence: 1);

        fixture.CompleteFirstEntry();
        Assert.True(fixture.Live.TryApplyCreateParent(update, out _));
        fixture.Controller.OnCreateParentAccepted(update);

        Assert.True(fixture.Live.TryGetSnapshot(
            child.ServerGuid,
            out WorldSession.EntitySpawn snapshot));
        Assert.Equal(parent.ServerGuid, snapshot.ParentGuid);
        Assert.Null(snapshot.Position);
        Assert.False(fixture.Live.TryGetRecord(child.ServerGuid, out _));
        var relation = new ParentAttachmentRelation(
            parent.ServerGuid,
            child.ServerGuid,
            0,
            0,
            1,
            1);
        Assert.True(fixture.Live.ParentAttachments.IsCommitted(relation));

        fixture.Poses.Publish(
            parent.WorldEntity!,
            Array.Empty<Matrix4x4>());
        fixture.Controller.OnPosePublished(parent.ServerGuid);

        Assert.True(fixture.Live.TryGetRecord(
            child.ServerGuid,
            out LiveEntityRecord childProjection));
        Assert.NotNull(childProjection.WorldEntity);
        Assert.True(childProjection.IsSpatiallyProjected);
        Assert.Equal(
            LiveEntityProjectionKind.Attached,
            childProjection.ProjectionKind);
    }

    [Fact]
    public void SpawnParentWithoutAnimationFrame_UsesRetailPlacementZero()
    {
        using var fixture = new ControllerFixture((_, _, _) =>
            new ExactProjectionWithdrawalOutcome(
                ExactProjectionWithdrawalDisposition.Completed,
                Failure: null));
        LiveEntityRecord parent = fixture.Spawn(0x70000270u, generation: 1);
        WorldSession.EntitySpawn childSpawn = ControllerFixture.SpawnData(
            0x70000271u,
            generation: 1);
        childSpawn = childSpawn with
        {
            Position = null,
            ParentGuid = parent.ServerGuid,
            ParentLocation = 0,
            PlacementId = null,
            PositionSequence = 0,
            Physics = childSpawn.Physics!.Value with
            {
                Position = null,
                Parent = new PhysicsAttachment(parent.ServerGuid, 0u),
                AnimationFrame = null,
            },
        };
        fixture.Live.RegisterLiveEntity(childSpawn);
        fixture.Controller.OnSpawn(childSpawn);

        var expected = new ParentAttachmentRelation(
            parent.ServerGuid,
            childSpawn.Guid,
            ParentLocation: 0,
            PlacementId: 0,
            ParentInstanceSequence: 1,
            ChildPositionSequence: 0);
        Assert.True(fixture.Live.ParentAttachments.IsCommitted(expected));
        fixture.Poses.Publish(parent.WorldEntity!, Array.Empty<Matrix4x4>());
        fixture.Controller.OnPosePublished(parent.ServerGuid);
        Assert.True(fixture.Live.TryGetRecord(
            childSpawn.Guid,
            out LiveEntityRecord child));
        Assert.Equal(LiveEntityProjectionKind.Attached, child.ProjectionKind);
        Assert.NotNull(child.WorldEntity);
    }

    [Theory]
    [InlineData(0x50000777u, (ushort)9)]
    [InlineData(0x70000280u, (ushort)0)]
    public void OnSpawn_CreateObjectRelation_LateBindsToParentLiveIncarnationAndPropagatesCell(
        uint parentGuid,
        ushort parentIncarnation)
    {
        using var fixture = new ControllerFixture((_, _, _) =>
            new ExactProjectionWithdrawalOutcome(
                ExactProjectionWithdrawalDisposition.Completed,
                Failure: null));
        LiveEntityRecord parent = fixture.Spawn(parentGuid, generation: parentIncarnation);
        Assert.NotEqual(0u, parent.Canonical.FullCellId);

        WorldSession.EntitySpawn childSpawn = ControllerFixture.SpawnData(
            0x7000028Fu,
            generation: 1);
        childSpawn = childSpawn with
        {
            Position = null,
            ParentGuid = parent.ServerGuid,
            ParentLocation = 0,
            PlacementId = null,
            PositionSequence = 0,
            Physics = childSpawn.Physics!.Value with
            {
                Position = null,
                Parent = new PhysicsAttachment(parent.ServerGuid, 0u),
                AnimationFrame = null,
            },
        };
        fixture.Live.RegisterLiveEntity(childSpawn);

        fixture.Controller.OnSpawn(childSpawn);

        Assert.True(fixture.Live.ParentAttachments.TryGetCommittedParent(
            childSpawn.Guid,
            out uint committedParentGuid,
            out ushort committedInstance));
        Assert.Equal(parent.ServerGuid, committedParentGuid);
        Assert.Equal(parentIncarnation, committedInstance);

        Assert.True(fixture.Live.TryGetCanonical(
            childSpawn.Guid,
            out RuntimeEntityRecord childCanonical));
        Assert.Equal(parent.Canonical.FullCellId, childCanonical.FullCellId);

        const uint newCell = 0x01020001u;
        const uint newLandblock = 0x0102FFFFu;
        fixture.Spatial.AddLandblock(new LoadedLandblock(
            newLandblock, new LandBlock(), Array.Empty<WorldEntity>()));
        Assert.True(fixture.EntityObjects.CommitRebucket(
            parent.Canonical, newCell, newLandblock));
        Assert.Equal(newCell, childCanonical.FullCellId);
    }

    [Theory]
    [InlineData(0x50000821u, (ushort)6)]
    [InlineData(0x70000299u, (ushort)0)]
    public void PrepareAndTryRealize_MismatchedIncarnation_RefusesBeforeCanonicalCommit(
        uint parentGuid,
        ushort parentIncarnation)
    {
        using var fixture = new ControllerFixture((_, _, _) =>
            new ExactProjectionWithdrawalOutcome(
                ExactProjectionWithdrawalDisposition.Completed,
                Failure: null));
        LiveEntityRecord parent = fixture.Spawn(parentGuid, generation: parentIncarnation);
        const uint childGuid = 0x700002A0u;
        fixture.RegisterOnly(childGuid, generation: 1, hasPosition: true);

        var wrongRelation = new ParentAttachmentRelation(
            parent.ServerGuid,
            childGuid,
            ParentLocation: 0,
            PlacementId: 0,
            ParentInstanceSequence: (ushort)(parentIncarnation + 1),
            ChildPositionSequence: 0);
        fixture.Live.ParentAttachments.AcceptCreateObjectRelation(wrongRelation);

        fixture.Controller.OnWorldEntityRegistered(parent.ServerGuid);

        Assert.True(fixture.Live.TryGetSnapshot(
            childGuid,
            out WorldSession.EntitySpawn snapshot));
        Assert.Null(snapshot.ParentGuid);
        Assert.NotNull(snapshot.Position);
        Assert.False(fixture.Live.ParentAttachments.TryGetCommittedParent(
            childGuid,
            out _,
            out _));
        // The refused relation does not linger and block Resolve forever -
        // it is rejected, not stranded.
        Assert.False(fixture.Live.ParentAttachments.TryGetStagedProjection(
            childGuid,
            out _));
    }

    [Theory]
    [InlineData(0x50000811u, (ushort)4)]
    [InlineData(0x70000291u, (ushort)0)]
    public void OnCreateParentAccepted_ParentNotYetKnown_RefusesWithoutStateOrCrash(
        uint parentGuid,
        ushort parentIncarnation)
    {
        using var fixture = new ControllerFixture((_, _, _) =>
            new ExactProjectionWithdrawalOutcome(
                ExactProjectionWithdrawalDisposition.Completed,
                Failure: null));
        const uint childGuid = 0x7000029Fu;
        fixture.RegisterOnly(childGuid, generation: 1, hasPosition: false);
        fixture.CompleteFirstEntry();

        var update = new CreateParentUpdate(
            childGuid,
            parentGuid,
            ParentLocation: 0,
            PlacementId: 0,
            ChildInstanceSequence: 1,
            ChildPositionSequence: 1);

        Assert.True(fixture.Live.TryApplyCreateParent(update, out _));
        fixture.Controller.OnCreateParentAccepted(update);
        Assert.False(fixture.Live.ParentAttachments.TryGetStagedProjection(
            childGuid,
            out _));
        Assert.False(fixture.Live.ParentAttachments.TryGetCommittedParent(
            childGuid,
            out _,
            out _));
        Assert.Equal(0, fixture.Live.ParentAttachments.UnresolvedRelationCount);

        LiveEntityRecord parent = fixture.Spawn(parentGuid, generation: parentIncarnation);
        fixture.Controller.OnWorldEntityRegistered(parent.ServerGuid);
        Assert.False(fixture.Live.ParentAttachments.TryGetCommittedParent(
            childGuid,
            out _,
            out _));
    }

    [Fact]
    public void ObjDesc_ReprojectsAttachedChildWithoutWorldPositionOrIdentityChange()
    {
        ControllerFixture? fixture = null;
        fixture = new ControllerFixture((record, positionVersion, projectionVersion) =>
        {
            bool completed = fixture!.Live.WithdrawLiveEntityProjection(
                record,
                positionVersion,
                projectionVersion);
            return new(
                completed
                    ? ExactProjectionWithdrawalDisposition.Completed
                    : ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        });
        using (fixture)
        {
            LiveEntityRecord parent = fixture.Spawn(0x70000272u, generation: 1);
            WorldSession.EntitySpawn childSpawn = ControllerFixture.SpawnData(
                0x70000273u,
                generation: 1);
            childSpawn = childSpawn with
            {
                Position = null,
                ParentGuid = parent.ServerGuid,
                ParentLocation = 0,
                PlacementId = 0,
                PositionSequence = 0,
                Physics = childSpawn.Physics!.Value with
                {
                    Position = null,
                    Parent = new PhysicsAttachment(parent.ServerGuid, 0u),
                    AnimationFrame = 0,
                },
            };
            fixture.Live.RegisterLiveEntity(childSpawn);
            fixture.Controller.OnSpawn(childSpawn);
            fixture.Poses.Publish(parent.WorldEntity!, Array.Empty<Matrix4x4>());
            fixture.Controller.OnPosePublished(parent.ServerGuid);
            Assert.True(fixture.Live.TryGetRecord(
                childSpawn.Guid,
                out LiveEntityRecord child));
            WorldEntity entity = child.WorldEntity!;
            Assert.Null(entity.PaletteOverride);
            WorldSession.EntitySpawn grandchildSpawn = ControllerFixture.SpawnData(
                0x70000274u,
                generation: 1);
            grandchildSpawn = grandchildSpawn with
            {
                Position = null,
                ParentGuid = child.ServerGuid,
                ParentLocation = 0,
                PlacementId = 0,
                PositionSequence = 0,
                Physics = grandchildSpawn.Physics!.Value with
                {
                    Position = null,
                    Parent = new PhysicsAttachment(child.ServerGuid, 0u),
                    AnimationFrame = 0,
                },
            };
            fixture.Live.RegisterLiveEntity(grandchildSpawn);
            fixture.Controller.OnSpawn(grandchildSpawn);
            Assert.True(fixture.Live.TryGetRecord(
                grandchildSpawn.Guid,
                out LiveEntityRecord grandchild));
            WorldEntity grandchildEntity = grandchild.WorldEntity!;
            Assert.Equal(
                LiveEntityProjectionKind.Attached,
                grandchild.ProjectionKind);

            Assert.True(fixture.Live.TryApplyObjDesc(
                new ObjDescEvent.Parsed(
                    child.ServerGuid,
                    new CreateObject.ModelData(
                        BasePaletteId: 0x04000022u,
                        [new CreateObject.SubPaletteSwap(
                            0x0F000033u,
                            Offset: 4,
                            Length: 8)],
                        Array.Empty<CreateObject.TextureChange>(),
                        Array.Empty<CreateObject.AnimPartChange>()),
                    InstanceSequence: 1,
                    ObjDescSequence: 1),
                out _));
            Assert.Null(child.Snapshot.Position);

            Assert.True(fixture.Controller.TryApplyAttachedAppearance(
                child,
                child.ObjDescAuthorityVersion));

            Assert.Same(entity, child.WorldEntity);
            Assert.True(child.IsSpatiallyProjected);
            Assert.Equal(LiveEntityProjectionKind.Attached, child.ProjectionKind);
            Assert.Equal((uint)0x04000022u, child.Snapshot.BasePaletteId);
            Assert.NotNull(entity.PaletteOverride);
            Assert.Equal(0x04000022u, entity.PaletteOverride!.BasePaletteId);
            PaletteOverride.SubPaletteRange range =
                Assert.Single(entity.PaletteOverride.SubPalettes);
            Assert.Equal(0x0F000033u, range.SubPaletteId);
            Assert.Equal((byte)4, range.Offset);
            Assert.Equal((byte)8, range.Length);
            Assert.Same(grandchildEntity, grandchild.WorldEntity);
            Assert.True(grandchild.IsSpatiallyProjected);
            Assert.Equal(
                LiveEntityProjectionKind.Attached,
                grandchild.ProjectionKind);
        }
    }

    [Fact]
    public void ChildWithoutSetup_StillCommitsLogicalParenting()
    {
        ControllerFixture? fixture = null;
        fixture = new ControllerFixture((record, positionVersion, projectionVersion) =>
        {
            bool completed = fixture!.Live.WithdrawLiveEntityProjection(
                record,
                positionVersion,
                projectionVersion);
            return new(
                completed
                    ? ExactProjectionWithdrawalDisposition.Completed
                    : ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        });
        using (fixture)
        {
            LiveEntityRecord parent = fixture.Spawn(0x7000025Cu, generation: 1);
            RuntimeEntityRecord childIdentity = fixture.RegisterOnly(
                0x7000025Du,
                generation: 1,
                hasPosition: true,
                hasSetup: false);
            LiveEntityRecord child = fixture.Materialize(childIdentity);
            child.HasPartArray = false;
            var update = new ParentEvent.Parsed(
                parent.ServerGuid, child.ServerGuid, 0, 0, 1, 1);

            fixture.Controller.OnParentEvent(update);

            Assert.True(fixture.Live.TryGetSnapshot(
                child.ServerGuid,
                out WorldSession.EntitySpawn snapshot));
            Assert.Equal(parent.ServerGuid, snapshot.ParentGuid);
            Assert.Null(snapshot.Position);
            Assert.False(child.IsSpatiallyProjected);
            Assert.True(fixture.Live.ParentAttachments.IsCommitted(new(
                parent.ServerGuid, child.ServerGuid, 0, 0, 1, 1)));
        }
    }

    [Fact]
    public void EmbeddedSameGenerationCreateParent_UsesStagedRoute()
    {
        ControllerFixture? fixture = null;
        fixture = new ControllerFixture((record, positionVersion, projectionVersion) =>
        {
            bool completed = fixture!.Live.WithdrawLiveEntityProjection(
                record,
                positionVersion,
                projectionVersion);
            return new(
                completed
                    ? ExactProjectionWithdrawalDisposition.Completed
                    : ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        });
        using (fixture)
        {
        LiveEntityRecord parent = fixture.Spawn(0x7000025Eu, generation: 1);
        LiveEntityRecord child = fixture.Spawn(0x7000025Fu, generation: 1);
        PhysicsTimestamps timestamps = new(
            Position: 1,
            Movement: 0,
            State: 0,
            Vector: 0,
            Teleport: 0,
            ServerControlledMove: 0,
            ForcePosition: 0,
            ObjDesc: 0,
            Instance: 1);
        PhysicsSpawnData physics = new(
            RawState: 0x408u,
            Position: null,
            Movement: null,
            AnimationFrame: 0,
            SetupTableId: 0x02000001u,
            MotionTableId: null,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: new PhysicsAttachment(parent.ServerGuid, 0),
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
        WorldSession.EntitySpawn incoming = ControllerFixture.SpawnData(
            child.ServerGuid,
            generation: 1) with
        {
            Position = null,
            PositionSequence = 1,
            PlacementId = 0,
            ParentGuid = parent.ServerGuid,
            ParentLocation = 0,
            Physics = physics,
        };

        LiveEntityRegistrationResult refresh = fixture.Live.RegisterLiveEntity(incoming);
        CreateParentUpdate update = Assert.IsType<CreateParentUpdate>(
            refresh.Inbound.SameGenerationEvents!.Value.Parent);
        Assert.True(fixture.Live.TryApplyCreateParent(update, out _));
        fixture.Controller.OnCreateParentAccepted(update);

        Assert.True(fixture.Live.TryGetSnapshot(
            child.ServerGuid,
            out WorldSession.EntitySpawn snapshot));
        Assert.Equal(parent.ServerGuid, snapshot.ParentGuid);
        Assert.Null(snapshot.Position);
        }
    }

    [Fact]
    public void NewWaitingParent_DoesNotDisplaceCommittedRecovery()
    {
        ControllerFixture? fixture = null;
        fixture = new ControllerFixture((record, positionVersion, projectionVersion) =>
        {
            bool completed = fixture!.Live.WithdrawLiveEntityProjection(
                record,
                positionVersion,
                projectionVersion);
            return new(
                completed
                    ? ExactProjectionWithdrawalDisposition.Completed
                    : ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        });
        using (fixture)
        {
            LiveEntityRecord oldParent = fixture.Spawn(0x70000256u, generation: 1);
            LiveEntityRecord child = fixture.Spawn(
                0x70000257u,
                generation: 1,
                LiveEntityProjectionKind.Attached);
            RuntimeEntityRecord waitingParent = fixture.RegisterOnly(
                0x70000258u,
                generation: 1,
                hasPosition: true);
            var oldRelation = new ParentAttachmentRelation(
                oldParent.ServerGuid,
                child.ServerGuid,
                0,
                0,
                1,
                1);
            fixture.Live.ParentAttachments.AcceptCreateObjectRelation(oldRelation);
            Assert.True(fixture.Live.TryApplyParent(new ParentEvent.Parsed(
                oldRelation.ParentGuid,
                oldRelation.ChildGuid,
                oldRelation.ParentLocation,
                oldRelation.PlacementId,
                oldRelation.ParentInstanceSequence,
                oldRelation.ChildPositionSequence), out _));
            Assert.True(fixture.Live.CommitStagedParent(oldRelation, out _));
            Assert.True(fixture.Live.ParentAttachments.CommitProjection(oldRelation));
            fixture.Live.ParentAttachments.MarkProjected(
                oldRelation,
                ParentProjectionCandidateKind.Recovery);
            fixture.InstallAttached(oldParent, child);

            var newer = new ParentEvent.Parsed(
                waitingParent.ServerGuid,
                child.ServerGuid,
                ParentLocation: 0,
                PlacementId: 0,
                ParentInstanceSequence: 1,
                ChildPositionSequence: 2);
            fixture.Controller.OnParentEvent(newer);
            Assert.True(fixture.Live.ParentAttachments.TryGetStagedProjection(
                child.ServerGuid,
                out ParentAttachmentRelation staged));
            Assert.Equal(waitingParent.ServerGuid, staged.ParentGuid);

            fixture.Controller.Tick();

            Assert.Empty(fixture.Controller.AttachedEntityIds);
            Assert.True(fixture.Live.ParentAttachments.TryGetStagedProjection(
                child.ServerGuid,
                out staged));
            Assert.Equal(waitingParent.ServerGuid, staged.ParentGuid);
            Assert.True(fixture.Live.ParentAttachments.TryGetRecoveryProjection(
                child.ServerGuid,
                out ParentAttachmentRelation recovery));
            Assert.Equal(oldParent.ServerGuid, recovery.ParentGuid);

            fixture.Materialize(waitingParent);
            fixture.Controller.OnWorldEntityRegistered(waitingParent.ServerGuid);

            Assert.True(fixture.Live.ParentAttachments.IsCommitted(staged));
            Assert.True(fixture.Live.ParentAttachments.TryGetRecoveryProjection(
                child.ServerGuid,
                out ParentAttachmentRelation committedRecovery));
            Assert.Equal(staged, committedRecovery);
            Assert.True(fixture.Live.TryGetSnapshot(
                child.ServerGuid,
                out WorldSession.EntitySpawn snapshot));
            Assert.Equal(waitingParent.ServerGuid, snapshot.ParentGuid);
        }
    }

    [Fact]
    public void OrdinaryRemoval_PendingFailureRetriesExactCaptureOnTick()
    {
        int attempts = 0;
        ControllerFixture? fixture = null;
        fixture = new ControllerFixture((record, positionVersion, projectionVersion) =>
        {
            attempts++;
            if (attempts == 1)
            {
                return new(
                    ExactProjectionWithdrawalDisposition.Pending,
                    new InvalidOperationException("ordinary cleanup failed"));
            }
            bool completed = fixture!.Live.WithdrawLiveEntityProjection(
                record,
                positionVersion,
                projectionVersion);
            return new(
                completed
                    ? ExactProjectionWithdrawalDisposition.Completed
                    : ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        });
        using (fixture)
        {
            LiveEntityRecord parent = fixture.Spawn(0x70000260u, generation: 1);
            LiveEntityRecord child = fixture.Spawn(
                0x70000261u,
                generation: 1,
                LiveEntityProjectionKind.Attached);
            fixture.InstallAttached(parent, child);
            fixture.Live.ParentAttachments.AcceptCreateObjectRelation(new(
                parent.ServerGuid,
                child.ServerGuid,
                ParentLocation: 0,
                PlacementId: 0,
                ParentInstanceSequence: 1,
                ChildPositionSequence: 1));

            TargetInvocationException error = Assert.Throws<TargetInvocationException>(() =>
                fixture.InvokeOrdinaryRemoval(0x70000261u));
            Assert.IsType<InvalidOperationException>(error.InnerException);

            fixture.Controller.Tick();

            Assert.Equal(2, attempts);
            Assert.Empty(fixture.Controller.AttachedEntityIds);
            Assert.False(child.IsSpatiallyProjected);
            Assert.False(fixture.Live.ParentAttachments.TryGetProjection(
                child.ServerGuid,
                out _));
        }
    }

    [Fact]
    public void DetachedRemoval_PendingFailureRetriesAndPreservesRollbackHistory()
    {
        int attempts = 0;
        ControllerFixture? fixture = null;
        fixture = new ControllerFixture((record, positionVersion, projectionVersion) =>
        {
            attempts++;
            if (attempts == 1)
            {
                return new(
                    ExactProjectionWithdrawalDisposition.Pending,
                    new InvalidOperationException("detached cleanup failed"));
            }
            bool completed = fixture!.Live.WithdrawLiveEntityProjection(
                record,
                positionVersion,
                projectionVersion);
            return new(
                completed
                    ? ExactProjectionWithdrawalDisposition.Completed
                    : ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        });
        using (fixture)
        {
            LiveEntityRecord parent = fixture.Spawn(0x70000262u, generation: 1);
            LiveEntityRecord child = fixture.Spawn(
                0x70000263u,
                generation: 1,
                LiveEntityProjectionKind.Attached);
            fixture.InstallAttached(parent, child);
            var relation = new ParentAttachmentRelation(
                parent.ServerGuid,
                child.ServerGuid,
                ParentLocation: 0,
                PlacementId: 0,
                ParentInstanceSequence: 1,
                ChildPositionSequence: 1);
            fixture.Live.ParentAttachments.AcceptCreateObjectRelation(relation);
            Assert.True(fixture.Live.ParentAttachments.CommitProjection(relation));
            fixture.Live.ParentAttachments.MarkProjected(
                relation,
                ParentProjectionCandidateKind.Recovery);

            TargetInvocationException error = Assert.Throws<TargetInvocationException>(() =>
                fixture.InvokeDetachedRemoval(child.ServerGuid));
            Assert.IsType<InvalidOperationException>(error.InnerException);

            fixture.Controller.Tick();

            Assert.Equal(2, attempts);
            Assert.Empty(fixture.Controller.AttachedEntityIds);
            Assert.False(child.IsSpatiallyProjected);
            Assert.True(fixture.Live.ParentAttachments.RestoreLastAccepted(
                child.ServerGuid));
            Assert.True(fixture.Live.ParentAttachments.TryGetRecoveryProjection(
                child.ServerGuid,
                out ParentAttachmentRelation restored));
            Assert.Equal(relation, restored);
        }
    }

    [Fact]
    public void Unparent_WithdrawsCompleteAttachedSubtreeAndRecoversDescendants()
    {
        ControllerFixture? fixture = null;
        fixture = new ControllerFixture((record, positionVersion, projectionVersion) =>
        {
            bool completed = fixture!.Live.WithdrawLiveEntityProjection(
                record,
                positionVersion,
                projectionVersion);
            return new(
                completed
                    ? ExactProjectionWithdrawalDisposition.Completed
                    : ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        });
        using (fixture)
        {
            LiveEntityRecord parent = fixture.Spawn(0x70000264u, generation: 1);
            LiveEntityRecord child = fixture.Spawn(
                0x70000265u,
                generation: 1,
                LiveEntityProjectionKind.Attached);
            LiveEntityRecord grandchild = fixture.Spawn(
                0x70000266u,
                generation: 1,
                LiveEntityProjectionKind.Attached);
            fixture.InstallAttached(parent, child);
            fixture.InstallAttached(child, grandchild);
            var childRelation = new ParentAttachmentRelation(
                parent.ServerGuid, child.ServerGuid, 0, 0, 1, 1);
            var grandchildRelation = new ParentAttachmentRelation(
                child.ServerGuid, grandchild.ServerGuid, 0, 0, 1, 1);
            fixture.CommitRenderedRelation(childRelation);
            fixture.CommitRenderedRelation(grandchildRelation);

            ChildUnparentDisposition result =
                fixture.Controller.OnChildBecameUnparented(child.ServerGuid);

            Assert.Equal(ChildUnparentDisposition.Completed, result);
            Assert.Empty(fixture.Controller.AttachedEntityIds);
            Assert.False(child.IsSpatiallyProjected);
            Assert.False(grandchild.IsSpatiallyProjected);
            Assert.False(fixture.Live.ParentAttachments.RestoreLastAccepted(
                child.ServerGuid));
            Assert.True(fixture.Live.ParentAttachments.TryGetRecoveryProjection(
                grandchild.ServerGuid,
                out ParentAttachmentRelation recovered));
            Assert.Equal(grandchildRelation, recovered);
        }
    }

    [Fact]
    public void PendingDetachedRemoval_RollbackCancelsRetryAndKeepsProjection()
    {
        int attempts = 0;
        using var fixture = new ControllerFixture((_, _, _) =>
        {
            attempts++;
            return new(
                ExactProjectionWithdrawalDisposition.Pending,
                new InvalidOperationException("component withdrawal failed"));
        });
        LiveEntityRecord parent = fixture.Spawn(0x70000267u, generation: 1);
        LiveEntityRecord child = fixture.Spawn(
            0x70000268u,
            generation: 1,
            LiveEntityProjectionKind.Attached);
        fixture.InstallAttached(parent, child);
        fixture.CommitRenderedRelation(new(
            parent.ServerGuid, child.ServerGuid, 0, 0, 1, 1));
        fixture.Poses.Publish(parent.WorldEntity!, Array.Empty<Matrix4x4>());
        fixture.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = child.ServerGuid,
            WielderId = parent.ServerGuid,
            CurrentlyEquippedLocation = EquipMask.MeleeWeapon,
        });

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Objects.MoveItemOptimistic(
                child.ServerGuid,
                newContainerId: 0x50000001u,
                newSlot: 0));
        Assert.True(fixture.Objects.RollbackMove(child.ServerGuid));
        fixture.Controller.Tick();

        Assert.Equal(1, attempts);
        Assert.True(child.IsSpatiallyProjected);
        Assert.Single(fixture.Controller.AttachedEntityIds);
    }

    [Fact]
    public void InventoryPutObjectIn3D_DoesNotWithdrawRecoveredWorldProjection()
    {
        int withdrawals = 0;
        ControllerFixture? fixture = null;
        fixture = new ControllerFixture((record, positionVersion, projectionVersion) =>
        {
            withdrawals++;
            bool completed = fixture!.Live.WithdrawLiveEntityProjection(
                record,
                positionVersion,
                projectionVersion);
            return new(
                completed
                    ? ExactProjectionWithdrawalDisposition.Completed
                    : ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        });
        using (fixture)
        {
            const uint guid = 0x7000026Au;
            const uint player = 0x50000001u;
            LiveEntityRecord dropped = fixture.Spawn(guid, generation: 1);
            fixture.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = guid,
                ContainerId = player,
                CurrentlyEquippedLocation = EquipMask.None,
            });

            Assert.True(fixture.Objects.ApplyConfirmedServerMove(
                guid,
                newContainerId: 0u,
                newWielderId: 0u));

            Assert.Equal(0, withdrawals);
            Assert.True(dropped.IsSpatiallyProjected);
            Assert.Equal(LiveEntityProjectionKind.World, dropped.ProjectionKind);
        }
    }

    [Fact]
    public void OrphanRollback_PendingFailureRetainsExactRetryOwner()
    {
        int attempts = 0;
        ControllerFixture? fixture = null;
        fixture = new ControllerFixture((record, positionVersion, projectionVersion) =>
        {
            attempts++;
            if (attempts == 1)
            {
                return new(
                    ExactProjectionWithdrawalDisposition.Pending,
                    new InvalidOperationException("orphan component cleanup failed"));
            }
            bool completed = fixture!.Live.WithdrawLiveEntityProjection(
                record,
                positionVersion,
                projectionVersion);
            return new(
                completed
                    ? ExactProjectionWithdrawalDisposition.Completed
                    : ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        });
        using (fixture)
        {
            LiveEntityRecord orphan = fixture.Spawn(0x70000269u, generation: 1);

            TargetInvocationException error = Assert.Throws<TargetInvocationException>(() =>
                fixture.InvokeOrphanRemoval(orphan));
            Assert.IsType<InvalidOperationException>(error.InnerException);

            fixture.Controller.Tick();

            Assert.Equal(2, attempts);
            Assert.False(orphan.IsSpatiallyProjected);
        }
    }

    [Fact]
    public void RecoveryContinuation_PostCommitFailureIsNotReplayed()
    {
        ControllerFixture? fixture = null;
        fixture = new ControllerFixture((record, positionVersion, projectionVersion) =>
        {
            bool completed = fixture!.Live.WithdrawLiveEntityProjection(
                record,
                positionVersion,
                projectionVersion);
            return new(
                completed
                    ? ExactProjectionWithdrawalDisposition.Completed
                    : ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        });
        using (fixture)
        {
            LiveEntityRecord record = fixture.Spawn(0x70000270u, generation: 1);
            int continuations = 0;
            Assert.Throws<InvalidOperationException>(() =>
                fixture.Controller.OnChildBecameUnparented(
                    0x70000270u,
                    () =>
                    {
                        continuations++;
                        Assert.True(fixture.Live.RebucketLiveEntity(
                            0x70000270u,
                            0x01010001u));
                        throw new InvalidOperationException("observer failed after recovery");
                    }));

            fixture.Controller.Tick();

            Assert.Equal(1, continuations);
            Assert.True(record.IsSpatiallyProjected);
        }
    }

    [Fact]
    public void OnSpawn_ColdEffectivePartsPublishPreparedMembershipIntoWalkAndWithdrawCleanly()
    {
        const uint basePart = 0x0100E001u;
        const uint originalPart = 0x0100E002u;
        const uint replacementPart = 0x0100E003u;
        const uint absentDatPart = 0x0100E004u;
        const uint parentGuid = 0x70000E00u;
        const uint childGuid = 0x70000E01u;

        Setup setup = AttachedSetup(basePart, originalPart, absentDatPart);
        var gfxObjs = new Dictionary<uint, GfxObj>
        {
            [basePart] = DrawableGfx(0x0800E001u),
            [originalPart] = DrawableGfx(0x0800E002u),
            [replacementPart] = DrawableGfx(0x0800E003u),
        };
        var prepared = new RecordingPreparedCollisionSource(
            basePart,
            originalPart,
            replacementPart);
        ControllerFixture? fixture = null;
        fixture = new ControllerFixture(
            (record, positionVersion, projectionVersion) =>
            {
                bool completed = fixture!.Live.WithdrawLiveEntityProjection(
                    record,
                    positionVersion,
                    projectionVersion);
                return new ExactProjectionWithdrawalOutcome(
                    completed
                        ? ExactProjectionWithdrawalDisposition.Completed
                        : ExactProjectionWithdrawalDisposition.Superseded,
                    Failure: null);
            },
            setup,
            gfxObjs,
            prepared);
        using (fixture)
        using (var scene = new ArchRenderScene(RenderSceneGeneration.FromRaw(1)))
        {
            Assert.Equal(0, fixture.PhysicsData.FlatGfxObjCount);
            LiveEntityRecord parent = fixture.Spawn(parentGuid, generation: 1);
            fixture.RegisterParentRetailMembership(parent);
            Assert.True(fixture.Shadows.TryGetRetailCellArray(
                parent.WorldEntity!.Id,
                out IReadOnlyList<uint> parentCells));
            Assert.NotEmpty(parentCells);

            var journal = new RenderProjectionJournal(
                RenderSceneGeneration.FromRaw(1));
            var projections = new LiveRenderProjectionJournal(
                fixture.Live,
                journal,
                new GpuWorldRenderTraversalOrderSource(fixture.Spatial));
            Assert.True(projections.OnEntityReady(
                LiveEntityReadyCandidate.Capture(parent)));

            int readyCount = 0;
            fixture.Controller.EntityReady += candidate =>
            {
                readyCount++;
                Assert.True(projections.OnEntityReady(candidate));
            };
            fixture.Controller.ProjectionPoseReady +=
                projections.OnProjectionPoseReady;
            fixture.Controller.ProjectionRemoved +=
                projections.OnProjectionRemoved;
            fixture.Poses.Publish(
                parent.WorldEntity,
                Array.Empty<Matrix4x4>());

            WorldSession.EntitySpawn childSpawn = ControllerFixture.SpawnData(
                childGuid,
                generation: 1) with
            {
                Position = null,
                ParentGuid = parentGuid,
                ParentLocation = 0,
                PlacementId = 0,
                AnimPartChanges =
                [
                    new CreateObject.AnimPartChange(1, replacementPart),
                ],
            };
            childSpawn = childSpawn with
            {
                Physics = childSpawn.Physics!.Value with
                {
                    Position = null,
                    Parent = new PhysicsAttachment(parentGuid, 0u),
                    AnimationFrame = 0,
                },
            };
            fixture.Live.RegisterLiveEntity(childSpawn);

            fixture.Controller.OnSpawn(childSpawn);

            Assert.Equal(1, readyCount);
            Assert.True(fixture.Live.TryGetRecord(
                childGuid,
                out LiveEntityRecord child));
            Assert.Equal(LiveEntityProjectionKind.Attached, child.ProjectionKind);
            Assert.NotNull(child.WorldEntity);
            Assert.Equal(
                [basePart, replacementPart, absentDatPart],
                child.WorldEntity.MeshRefs.Select(static part => part.GfxObjId));

            uint childLocalId = child.WorldEntity.Id;
            Assert.True(fixture.Shadows.TryGetRetailCellArray(
                childLocalId,
                out IReadOnlyList<uint> childCells));
            Assert.Equal(parentCells, childCells);
            Assert.Empty(fixture.Shadows.GetOwnerCells(childLocalId));
            foreach (uint cellId in childCells)
            {
                RetailPartEntry[] childRows = fixture.Shadows
                    .GetRetailPartEntriesInCell(cellId)
                    .Where(row => row.EntityId == childLocalId)
                    .ToArray();
                Assert.Equal(2, childRows.Length);
                Assert.Equal((0, basePart),
                    (childRows[0].PartIndex, childRows[0].GfxObjId));
                Assert.Equal((1, replacementPart),
                    (childRows[1].PartIndex, childRows[1].GfxObjId));
            }

            journal.DrainTo(scene);
            var walk = new WalkProductionWorldData(
                new WalkBuildingRegistry(),
                fixture.Shadows);
            walk.BeginFrame(
                scene.OpenQuery(),
                ControllerFixture.Landblock,
                renderCenterLbX: 1,
                renderCenterLbY: 1);
            foreach (uint cellId in childCells)
            {
                RenderProjectionRecord projected = Assert.Single(
                    walk.GetCellDynamics(cellId).Records,
                    record => record.Source.LocalEntityId == childLocalId);
                Assert.Equal(
                    RenderProjectionClass.EquippedChild,
                    projected.ProjectionClass);
            }
            Assert.Equal(0, walk.UnregisteredRenderMembershipCount);

            Assert.Equal([basePart, replacementPart], prepared.GfxReads);
            Assert.NotNull(fixture.PhysicsData.GetFlatGfxObj(basePart));
            Assert.NotNull(fixture.PhysicsData.GetFlatGfxObj(replacementPart));
            Assert.Null(fixture.PhysicsData.GetFlatGfxObj(originalPart));
            Assert.Null(fixture.PhysicsData.GetFlatGfxObj(absentDatPart));

            Assert.True(fixture.Live.TryApplyObjDesc(
                new ObjDescEvent.Parsed(
                    childGuid,
                    new CreateObject.ModelData(
                        BasePaletteId: null,
                        Array.Empty<CreateObject.SubPaletteSwap>(),
                        Array.Empty<CreateObject.TextureChange>(),
                        [new CreateObject.AnimPartChange(1, replacementPart)]),
                    InstanceSequence: 1,
                    ObjDescSequence: 1),
                out _));
            Assert.True(fixture.Controller.TryApplyAttachedAppearance(
                child,
                child.ObjDescAuthorityVersion));
            Assert.Equal([basePart, replacementPart], prepared.GfxReads);
            foreach (uint cellId in childCells)
            {
                RetailPartEntry[] childRows = fixture.Shadows
                    .GetRetailPartEntriesInCell(cellId)
                    .Where(row => row.EntityId == childLocalId)
                    .ToArray();
                Assert.Equal(2, childRows.Length);
                Assert.Equal([basePart, replacementPart],
                    childRows.Select(static row => row.GfxObjId));
            }

            Assert.Equal(
                ChildUnparentDisposition.Completed,
                fixture.Controller.OnChildBecameUnparented(childGuid));
            Assert.False(fixture.Shadows.TryGetRetailCellArray(
                childLocalId,
                out _));
            foreach (uint cellId in parentCells)
            {
                IReadOnlyList<RetailPartEntry> rows =
                    fixture.Shadows.GetRetailPartEntriesInCell(cellId);
                Assert.DoesNotContain(rows, row => row.EntityId == childLocalId);
                Assert.Contains(rows, row =>
                    row.EntityId == parent.WorldEntity.Id);
            }
        }
    }

    [Fact]
    public void OnSpawn_MissingPreparedEffectivePartFailsBeforeProjectionOrMembership()
    {
        const uint part = 0x0100E010u;
        const uint parentGuid = 0x70000E10u;
        const uint childGuid = 0x70000E11u;
        Setup setup = AttachedSetup(part);
        var gfxObjs = new Dictionary<uint, GfxObj>
        {
            [part] = DrawableGfx(0x0800E010u),
        };
        var prepared = new RecordingPreparedCollisionSource(part);
        prepared.MissingIds.Add(part);
        using var fixture = new ControllerFixture(
            static (_, _, _) => new ExactProjectionWithdrawalOutcome(
                ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null),
            setup,
            gfxObjs,
            prepared);
        LiveEntityRecord parent = fixture.Spawn(parentGuid, generation: 1);
        fixture.RegisterParentRetailMembership(parent);
        fixture.Poses.Publish(parent.WorldEntity!, Array.Empty<Matrix4x4>());
        int readyCount = 0;
        fixture.Controller.EntityReady += _ => readyCount++;

        WorldSession.EntitySpawn childSpawn = ControllerFixture.SpawnData(
            childGuid,
            generation: 1) with
        {
            Position = null,
            ParentGuid = parentGuid,
            ParentLocation = 0,
            PlacementId = 0,
        };
        childSpawn = childSpawn with
        {
            Physics = childSpawn.Physics!.Value with
            {
                Position = null,
                Parent = new PhysicsAttachment(parentGuid, 0u),
                AnimationFrame = 0,
            },
        };
        fixture.Live.RegisterLiveEntity(childSpawn);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => fixture.Controller.OnSpawn(childSpawn));

        Assert.Contains("Missing", error.Message, StringComparison.Ordinal);
        Assert.Equal([part], prepared.GfxReads);
        Assert.Equal(0, readyCount);
        Assert.False(fixture.Live.TryGetRecord(childGuid, out _));
        Assert.Empty(fixture.Controller.AttachedEntityIds);
        Assert.True(fixture.Live.TryGetCanonical(
            childGuid,
            out RuntimeEntityRecord childCanonical));
        Assert.NotNull(childCanonical.LocalEntityId);
        Assert.False(fixture.Shadows.TryGetRetailCellArray(
            childCanonical.LocalEntityId!.Value,
            out _));
        Assert.Equal(0, fixture.PhysicsData.GfxObjCount);
        Assert.Equal(0, fixture.PhysicsData.FlatGfxObjCount);
    }

    private static Setup AttachedSetup(params uint[] parts)
    {
        var setup = new Setup
        {
            HoldingLocations =
            {
                [(ParentLocation)0] = new LocationType
                {
                    PartId = -1,
                    Frame = new Frame { Orientation = Quaternion.Identity },
                },
            },
        };
        for (int i = 0; i < parts.Length; i++)
            setup.Parts.Add(parts[i]);
        return setup;
    }

    private static GfxObj DrawableGfx(uint surfaceId) => new()
    {
        Surfaces = { surfaceId },
        VertexArray = new VertexArray
        {
            Vertices =
            {
                [0] = new SWVertex
                {
                    Origin = Vector3.Zero,
                    Normal = Vector3.UnitZ,
                },
                [1] = new SWVertex
                {
                    Origin = Vector3.UnitX,
                    Normal = Vector3.UnitZ,
                },
                [2] = new SWVertex
                {
                    Origin = Vector3.UnitY,
                    Normal = Vector3.UnitZ,
                },
            },
        },
        Polygons =
        {
            [0] = new Polygon
            {
                PosSurface = 0,
                NegSurface = -1,
                VertexIds = { 0, 1, 2 },
            },
        },
    };

    private static LiveEntityRecord ChildRecord() =>
        LiveEntityTestFixture.CreateExactProjectionRecord(
        new WorldSession.EntitySpawn(
            0x70000100u,
            new CreateObject.ServerPosition(
                0x01010001u, 0f, 0f, 0f, 1f, 0f, 0f, 0f),
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: null,
            Name: "attached fixture",
            ItemType: null,
            MotionState: null,
            MotionTableId: null,
            InstanceSequence: 1));

    private sealed class RecordingPreparedCollisionSource(
        params uint[] availableIds) : IPreparedCollisionSource
    {
        private static readonly FlatPhysicsBsp EmptyPhysics = new(
            -1,
            ImmutableArray<FlatPhysicsBspNode>.Empty,
            ImmutableArray<int>.Empty,
            FlatPolygonTable.Empty);
        private readonly HashSet<uint> _availableIds = [.. availableIds];

        internal List<uint> GfxReads { get; } = [];
        internal HashSet<uint> MissingIds { get; } = [];

        public PreparedAssetPresence ProbeCollision(
            PakAssetType type,
            uint sourceFileId) =>
            type == PakAssetType.GfxObjCollision
                && _availableIds.Contains(sourceFileId)
                && !MissingIds.Contains(sourceFileId)
                    ? PreparedAssetPresence.Available
                    : PreparedAssetPresence.Missing;

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
            ReadGfxObjCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default)
        {
            GfxReads.Add(sourceFileId);
            if (!_availableIds.Contains(sourceFileId)
                || MissingIds.Contains(sourceFileId))
            {
                return PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
                    .Missing;
            }

            var bounds = new FlatGfxObjVisualBounds(
                new Vector3(-0.5f),
                new Vector3(0.5f),
                Vector3.Zero,
                0.8660254f,
                new Vector3(0.5f));
            return PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
                .Loaded(new FlatGfxObjCollisionAsset(
                    EmptyPhysics,
                    BoundingSphere: null,
                    bounds));
        }

        public PreparedCollisionReadResult<FlatSetupCollision>
            ReadSetupCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<FlatSetupCollision>.Missing;

        public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
            ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<FlatCellStructureCollisionAsset>.Missing;

        public PreparedCollisionReadResult<FlatEnvCellTopology>
            ReadEnvCellTopology(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<FlatEnvCellTopology>.Missing;

        public PreparedCollisionSourceStats CollisionStats => default;

        public void Dispose()
        {
        }
    }

    private sealed class ControllerFixture : IDisposable
    {
        private const uint Cell = 0x01010001u;
        internal const uint Landblock = 0x0101FFFFu;
        private readonly DeferredLiveEntityRuntimeComponentLifecycle _lifecycle = new();
        private readonly Setup _setup;
        private readonly AcDream.Runtime.Session.RuntimeFirstEntryDriveController _firstEntry;

        private sealed class NullCollisionSource
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
                    AcDream.Core.Physics.FlatSetupCollision>.Missing;

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

        internal ControllerFixture(
            Func<LiveEntityRecord, ulong, ulong, ExactProjectionWithdrawalOutcome>
                withdraw,
            Setup? setup = null,
            IReadOnlyDictionary<uint, GfxObj>? gfxObjs = null,
            IPreparedCollisionSource? preparedCollision = null)
        {
            Spatial.AddLandblock(new LoadedLandblock(
                (Cell & 0xFFFF0000u) | 0xFFFFu,
                new LandBlock(),
                Array.Empty<WorldEntity>()));
            EntityObjects = new RuntimeEntityObjectLifetime();
            EntityObjects.BindEventContext(
                static () => new AcDream.Runtime.RuntimeGenerationToken(1UL),
                static () => 1UL);
            _firstEntry = new AcDream.Runtime.Session.RuntimeFirstEntryDriveController(
                EntityObjects,
                new AcDream.Runtime.GameRuntimeClock(),
                new NullCollisionSource(),
                () => AcDream.Runtime.Gameplay.PlayerMovementConstructionOptions.Fallback,
                static _ => new AcDream.Runtime.Gameplay.RuntimeLocalPlayerPhysicsActivationPreparation(
                    0.48f,
                    1.835f,
                    AcDream.Runtime.Gameplay.RuntimeLocalPlayerShadowDisposition.ProvenShapeless));
            Live = new LiveEntityRuntime(
                Spatial,
                new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }),
                _lifecycle,
                EntityObjects);
            _setup = setup ?? new Setup
            {
                HoldingLocations =
                {
                    [(ParentLocation)0] = new LocationType
                    {
                        PartId = -1,
                        Frame = new Frame { Orientation = Quaternion.Identity },
                    },
                },
            };
            IDatReaderWriter dat = DispatchProxy.Create<IDatReaderWriter, NullDatProxy>();
            ((NullDatProxy)(object)dat).Setup = _setup;
            ((NullDatProxy)(object)dat).GfxObjs = gfxObjs;
            Shadows = new ShadowObjectRegistry();
            PhysicsData = new PhysicsDataCache();
            Action<uint, GfxObj> publishGfxObj = preparedCollision is null
                ? static (_, _) => { }
                : new LiveCollisionAssetPublisher(
                    PhysicsData,
                    preparedCollision).CacheGfxObj;
            Controller = new EquippedChildRenderController(
                dat,
                new object(),
                Objects,
                Live,
                Poses,
                update => Live.TryApplyParent(update, out _),
                withdraw,
                Shadows,
                PhysicsData,
                publishGfxObj);
            _lifecycle.Bind(new DelegateLiveEntityRuntimeComponentLifecycle(
                Controller.OnLogicalTeardown));
        }

        internal GpuWorldState Spatial { get; } = new();
        internal RuntimeEntityObjectLifetime EntityObjects { get; }

        internal void CompleteFirstEntry()
        {
            _firstEntry.DriveAll();
        }
        internal ClientObjectTable Objects { get; } = new();
        internal EntityEffectPoseRegistry Poses { get; } = new();
        internal LiveEntityRuntime Live { get; }
        internal EquippedChildRenderController Controller { get; }
        internal ShadowObjectRegistry Shadows { get; }
        internal PhysicsDataCache PhysicsData { get; }

        internal void RegisterParentRetailMembership(LiveEntityRecord parent)
        {
            var sphere = new FlatCollisionSphere(Vector3.Zero, 1f);
            IReadOnlyList<ShadowShape> parts =
            [
                ShadowShape.Bsp(
                    0x0100FFFFu,
                    Vector3.Zero,
                    Quaternion.Identity,
                    scale: 1f,
                    ShadowPartGeometry.Create(sphere, visualBounds: null)),
            ];
            Shadows.RegisterMultiPart(
                parent.WorldEntity!.Id,
                parent.WorldEntity.Position,
                parent.WorldEntity.Rotation,
                parts,
                state: 0u,
                flags: EntityCollisionFlags.None,
                worldOffsetX: 0f,
                worldOffsetY: 0f,
                landblockId: Landblock,
                seedCellId: Cell,
                isStatic: false,
                partArray: parts);
        }

        internal LiveEntityRecord Spawn(
            uint guid,
            ushort generation,
            LiveEntityProjectionKind kind = LiveEntityProjectionKind.World)
        {
            RuntimeEntityRecord canonical =
                RegisterOnly(guid, generation, hasPosition: true);
            return Materialize(canonical, kind);
        }

        internal RuntimeEntityRecord RegisterOnly(
            uint guid,
            ushort generation,
            bool hasPosition,
            bool hasSetup = true)
        {
            WorldSession.EntitySpawn spawn = SpawnData(guid, generation);
            if (!hasPosition)
            {
                spawn = spawn with
                {
                    Position = null,
                    Physics = spawn.Physics!.Value with { Position = null },
                };
            }
            if (!hasSetup)
                spawn = spawn with { SetupTableId = null };
            return Assert.IsType<RuntimeEntityRecord>(
                Live.RegisterLiveEntity(spawn).Canonical);
        }

        internal LiveEntityRecord Materialize(
            RuntimeEntityRecord canonical,
            LiveEntityProjectionKind kind = LiveEntityProjectionKind.World)
        {
            WorldEntity? entity = Live.MaterializeLiveEntity(
                canonical,
                Cell,
                id => new WorldEntity
                {
                    Id = id,
                    ServerGuid = canonical.ServerGuid,
                    SourceGfxObjOrSetupId = 0x02000001u,
                    Position = Vector3.Zero,
                    Rotation = Quaternion.Identity,
                    MeshRefs = Array.Empty<MeshRef>(),
                    ParentCellId = Cell,
                },
                kind,
                initializeProjection: null,
                out LiveEntityRecord? projected);
            Assert.NotNull(entity);
            LiveEntityRecord record =
                Assert.IsType<LiveEntityRecord>(projected);
            record.HasPartArray = true;
            return record;
        }

        internal static WorldSession.EntitySpawn SpawnData(uint guid, ushort generation)
        {
            var position = new CreateObject.ServerPosition(
                Cell, 0f, 0f, 0f, 1f, 0f, 0f, 0f);
            var physics = new PhysicsSpawnData(
                RawState: 0u,
                Position: position,
                Movement: null,
                AnimationFrame: null,
                SetupTableId: 0x02000001u,
                MotionTableId: null,
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
                Timestamps: new PhysicsTimestamps(
                    Position: 0,
                    Movement: 0,
                    State: 0,
                    Vector: 0,
                    Teleport: 0,
                    ServerControlledMove: 0,
                    ForcePosition: 0,
                    ObjDesc: 0,
                    Instance: generation));
            return new WorldSession.EntitySpawn(
                guid,
                position,
                0x02000001u,
                Array.Empty<CreateObject.AnimPartChange>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.SubPaletteSwap>(),
                BasePaletteId: null,
                ObjScale: null,
                Name: "attached fixture",
                ItemType: null,
                MotionState: null,
                MotionTableId: null,
                InstanceSequence: generation,
                Physics: physics);
        }

        internal void InstallAttached(
            LiveEntityRecord parent,
            LiveEntityRecord child)
        {
            Type attachedType = typeof(EquippedChildRenderController).GetNestedType(
                "AttachedChild",
                BindingFlags.NonPublic)!;
            object attached = Activator.CreateInstance(
                attachedType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args:
                [
                    parent,
                    child,
                    parent.ServerGuid,
                    child.ServerGuid,
                    (ParentLocation)0,
                    (Placement)0,
                    _setup,
                    _setup,
                    Array.Empty<MeshRef>(),
                    Array.Empty<bool>(),
                    Array.Empty<Matrix4x4>(),
                    Array.Empty<MeshRef>(),
                    1f,
                    child.WorldEntity!,
                ],
                culture: null)!;
            FieldInfo mapField = typeof(EquippedChildRenderController).GetField(
                "_attachedByChild",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var map = (IDictionary)mapField.GetValue(Controller)!;
            map.Add(child.ProjectionKey!.Value, attached);
        }

        internal void RemoveProjectionOnly(LiveEntityRecord record)
        {
            FieldInfo projectionsField = typeof(LiveEntityRuntime).GetField(
                "_projections",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            object projections = projectionsField.GetValue(Live)!;
            MethodInfo removeActive = projections.GetType().GetMethod(
                "RemoveActive",
                BindingFlags.Instance | BindingFlags.Public)!;
            Assert.True((bool)removeActive.Invoke(projections, [record])!);
        }

        internal void CommitRenderedRelation(ParentAttachmentRelation relation)
        {
            Live.ParentAttachments.AcceptCreateObjectRelation(relation);
            Assert.True(Live.ParentAttachments.CommitProjection(relation));
            Live.ParentAttachments.MarkProjected(
                relation,
                ParentProjectionCandidateKind.Recovery);
        }

        internal void InvokeOrdinaryRemoval(uint guid)
        {
            MethodInfo method = typeof(EquippedChildRenderController).GetMethod(
                "TearDownCurrentObjectProjections",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            method.Invoke(Controller, [guid]);
        }

        internal void InvokeDetachedRemoval(uint guid)
        {
            MethodInfo method = typeof(EquippedChildRenderController).GetMethod(
                "BeginDetachedRemoval",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            method.Invoke(Controller, [guid]);
        }

        internal void InvokeOrphanRemoval(LiveEntityRecord record)
        {
            FieldInfo mapField = typeof(EquippedChildRenderController).GetField(
                "_pendingOrphanRemovalByChild",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            object map = mapField.GetValue(Controller)!;
            MethodInfo method = typeof(EquippedChildRenderController).GetMethod(
                "BeginProjectionSubtreeWithdrawal",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            method.Invoke(Controller, [map, record, false, false]);
        }

        public void Dispose() => Controller.Dispose();
    }

    private class NullDatProxy : DispatchProxy
    {
        internal Setup? Setup { get; set; }
        internal IReadOnlyDictionary<uint, GfxObj>? GfxObjs { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "Get"
                && targetMethod.ReturnType == typeof(Setup))
            {
                return Setup;
            }
            if (targetMethod?.Name == "Get"
                && targetMethod.ReturnType == typeof(GfxObj)
                && args is [uint id])
            {
                return GfxObjs is not null
                    && GfxObjs.TryGetValue(id, out GfxObj? gfxObj)
                        ? gfxObj
                        : null;
            }
            if (targetMethod?.ReturnType == typeof(void))
                return null;
            if (targetMethod?.ReturnType.IsValueType == true)
                return Activator.CreateInstance(targetMethod.ReturnType);
            return null;
        }
    }
}
