using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Items;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Tests.Entities;

public sealed class ParentAttachmentStateTests
{
    [Fact]
    public void ParentEventBeforeChildCreateResolvesToCanonicalAttachment()
    {
        const uint parentGuid = 0x70000100u;
        const uint childGuid = 0x70000101u;
        var inbound = new InboundPhysicsStateController();
        var relations = new ParentAttachmentState();
        inbound.AcceptCreate(Spawn(parentGuid, instance: 9, positionSequence: 1));
        relations.Enqueue(new ParentEvent.Parsed(parentGuid, childGuid, 1, 2, 9, 5));

        Resolve(relations, inbound, childGuid);
        Assert.False(relations.TryGetProjection(childGuid, out _));

        inbound.AcceptCreate(Spawn(childGuid, instance: 3, positionSequence: 4));
        Resolve(relations, inbound, childGuid);

        Assert.True(relations.TryGetProjection(childGuid, out ParentAttachmentRelation projection));
        Assert.Equal(parentGuid, projection.ParentGuid);
        Assert.True(inbound.TryGetSnapshot(childGuid, out WorldSession.EntitySpawn child));
        Assert.NotNull(child.Position);
        Assert.Null(child.ParentGuid);
        Assert.Equal((ushort)5, child.PositionSequence);
        Assert.True(Commit(relations, inbound, projection, out child));
        Assert.Null(child.Position);
        Assert.Equal(parentGuid, child.ParentGuid);
    }

    [Theory]
    [InlineData(0x50000210u, (ushort)5)]
    [InlineData(0x70000211u, (ushort)0)]
    public void CanCommitIncarnation_MismatchedIncarnation_RefusesWithoutMutatingState(
        uint parentGuid,
        ushort liveParentIncarnation)
    {
        const uint childGuid = 0x70000212u;
        var relations = new ParentAttachmentState();
        var wrongRelation = new ParentAttachmentRelation(
            parentGuid,
            childGuid,
            ParentLocation: 0,
            PlacementId: 0,
            ParentInstanceSequence: (ushort)(liveParentIncarnation + 1),
            ChildPositionSequence: 1);
        relations.AcceptCreateObjectRelation(wrongRelation);

        Assert.False(relations.CanCommitIncarnation(
            wrongRelation,
            guid => guid == parentGuid ? liveParentIncarnation : (ushort?)null));
        Assert.True(relations.TryGetStagedProjection(childGuid, out ParentAttachmentRelation staged));
        Assert.Equal(wrongRelation, staged);
        Assert.False(relations.TryGetCommittedParent(childGuid, out _, out _));

        Assert.True(relations.CanCommitIncarnation(wrongRelation, _ => null));
    }

    [Theory]
    [InlineData(0x50000213u, (ushort)5)]
    [InlineData(0x70000214u, (ushort)0)]
    public void CommitProjection_MismatchedIncarnation_ReturnsFalseWithoutThrowingOrMutating(
        uint parentGuid,
        ushort liveParentIncarnation)
    {
        const uint childGuid = 0x70000215u;
        var relations = new ParentAttachmentState();
        var wrongRelation = new ParentAttachmentRelation(
            parentGuid,
            childGuid,
            ParentLocation: 0,
            PlacementId: 0,
            ParentInstanceSequence: (ushort)(liveParentIncarnation + 1),
            ChildPositionSequence: 1);
        relations.AcceptCreateObjectRelation(wrongRelation);

        Assert.False(relations.CommitProjection(
            wrongRelation,
            guid => guid == parentGuid ? liveParentIncarnation : (ushort?)null));
        Assert.False(relations.TryGetCommittedParent(childGuid, out _, out _));
        Assert.True(relations.TryGetStagedProjection(childGuid, out ParentAttachmentRelation staged));
        Assert.Equal(wrongRelation, staged);

        Assert.True(relations.CommitProjection(
            wrongRelation,
            guid => guid == parentGuid
                ? (ushort)(liveParentIncarnation + 1)
                : (ushort?)null));
        Assert.True(relations.TryGetCommittedParent(
            childGuid,
            out uint committedParent,
            out ushort committedInstance));
        Assert.Equal(parentGuid, committedParent);
        Assert.Equal((ushort)(liveParentIncarnation + 1), committedInstance);
    }

    [Theory]
    [InlineData(0x50000900u, (ushort)3)]
    [InlineData(0x70000901u, (ushort)0)]
    public void LedgerConvergence_ChildRemoval_ZeroesEveryTable(
        uint parentGuid,
        ushort parentIncarnation)
    {
        const uint childGuid = 0x70000902u;
        var relations = new ParentAttachmentState();
        var relation = new ParentAttachmentRelation(
            parentGuid,
            childGuid,
            ParentLocation: 0,
            PlacementId: 0,
            parentIncarnation,
            ChildPositionSequence: 1);
        relations.AcceptCreateObjectRelation(relation);
        Assert.True(relations.CommitProjection(relation));
        relations.MarkProjected(relation, ParentProjectionCandidateKind.Recovery);

        Assert.Equal(1, relations.CommittedRelationCount);
        Assert.Equal(0, relations.RecoveryRelationCount);
        Assert.NotEmpty(relations.ChildrenAttachedToParent(parentGuid, parentIncarnation));

        relations.RemoveChild(childGuid);

        Assert.Equal(0, relations.CommittedRelationCount);
        Assert.Equal(0, relations.RecoveryRelationCount);
        Assert.Equal(0, relations.StagedRelationCount);
        Assert.Equal(0, relations.UnresolvedRelationCount);
        Assert.Empty(relations.ChildrenAttachedToParent(parentGuid, parentIncarnation));
        Assert.False(relations.TryGetCommittedParent(childGuid, out _, out _));
        Assert.False(relations.HasCommittedParent(childGuid));
    }

    [Theory]
    [InlineData(0x50000920u, (ushort)2)]
    [InlineData(0x70000921u, (ushort)0)]
    public void LedgerConvergence_ParentRemoval_ZeroesEveryTable(
        uint parentGuid,
        ushort parentIncarnation)
    {
        const uint childGuid = 0x70000922u;
        var relations = new ParentAttachmentState();
        var relation = new ParentAttachmentRelation(
            parentGuid,
            childGuid,
            ParentLocation: 0,
            PlacementId: 0,
            parentIncarnation,
            ChildPositionSequence: 1);
        relations.AcceptCreateObjectRelation(relation);
        Assert.True(relations.CommitProjection(relation));
        relations.MarkProjected(relation, ParentProjectionCandidateKind.Recovery);

        relations.RemoveObject(parentGuid);

        Assert.Equal(0, relations.CommittedRelationCount);
        Assert.Equal(0, relations.RecoveryRelationCount);
        Assert.Equal(0, relations.StagedRelationCount);
        Assert.Empty(relations.ChildrenAttachedToParent(parentGuid, parentIncarnation));
        Assert.False(relations.HasCommittedParent(childGuid));
    }

    [Theory]
    [InlineData(0x50000930u, (ushort)9)]
    [InlineData(0x70000931u, (ushort)0)]
    public void LedgerConvergence_Teardown_ZeroesEveryTableWithMixedPendingState(
        uint parentGuid,
        ushort parentIncarnation)
    {
        const uint committedChildGuid = 0x70000932u;
        const uint unresolvedChildGuid = 0x70000933u;
        var relations = new ParentAttachmentState();
        var relation = new ParentAttachmentRelation(
            parentGuid,
            committedChildGuid,
            ParentLocation: 0,
            PlacementId: 0,
            parentIncarnation,
            ChildPositionSequence: 1);
        relations.AcceptCreateObjectRelation(relation);
        Assert.True(relations.CommitProjection(relation));
        relations.MarkProjected(relation, ParentProjectionCandidateKind.Recovery);
        relations.Enqueue(new ParentEvent.Parsed(
            parentGuid, unresolvedChildGuid, 0, 0, parentIncarnation, 1));

        Assert.True(relations.CommittedRelationCount > 0);
        Assert.True(relations.UnresolvedRelationCount > 0);

        relations.Clear();

        Assert.Equal(0, relations.CommittedRelationCount);
        Assert.Equal(0, relations.RecoveryRelationCount);
        Assert.Equal(0, relations.StagedRelationCount);
        Assert.Equal(0, relations.UnresolvedRelationCount);
        Assert.False(relations.HasCommittedParent(committedChildGuid));
        Assert.Empty(relations.ChildrenAttachedToParent(parentGuid, parentIncarnation));
    }

    [Fact]
    public void MultipleQueuedRelationsRemainOrderedAndNewestAcceptedWins()
    {
        const uint parentGuid = 0x70000110u;
        const uint childGuid = 0x70000111u;
        var gate = new PhysicsTimestampGate();
        gate.SeedForCreateObject(4, 0, 0, 0, 0, 0, 0, 0, 3);
        var relations = new ParentAttachmentState();
        relations.Enqueue(new ParentEvent.Parsed(parentGuid, childGuid, 1, 2, 9, 5));
        relations.Enqueue(new ParentEvent.Parsed(parentGuid, childGuid, 1, 3, 9, 6));

        relations.Resolve(
            childGuid,
            _ => true,
            guid => guid == parentGuid ? (ushort)9 : null,
            update => gate.TryAcceptPositionChannelEvent(3, update.ChildPositionSequence));

        Assert.True(relations.TryGetProjection(childGuid, out ParentAttachmentRelation projection));
        Assert.Equal((ushort)5, projection.ChildPositionSequence);
        Assert.Equal((uint)2, projection.PlacementId);
        Assert.True(relations.CommitProjection(projection));
        relations.MarkProjected(
            projection,
            ParentProjectionCandidateKind.Recovery);
        relations.Resolve(
            childGuid,
            _ => true,
            guid => guid == parentGuid ? (ushort)9 : null,
            update => gate.TryAcceptPositionChannelEvent(3, update.ChildPositionSequence));
        Assert.True(relations.TryGetStagedProjection(childGuid, out projection));
        Assert.Equal((ushort)6, projection.ChildPositionSequence);
        Assert.Equal((uint)3, projection.PlacementId);
        Assert.True(relations.CommitProjection(projection));
        relations.MarkProjected(
            projection,
            ParentProjectionCandidateKind.Recovery);
        Assert.True(relations.RestoreLastAccepted(childGuid));
        Assert.True(relations.TryGetProjection(childGuid, out projection));
        Assert.Equal((ushort)6, projection.ChildPositionSequence);
    }

    [Fact]
    public void RejectedStaleRelationCannotReplaceAcceptedCreateRelation()
    {
        const uint childGuid = 0x70000121u;
        var gate = new PhysicsTimestampGate();
        gate.SeedForCreateObject(10, 0, 0, 0, 0, 0, 0, 0, 3);
        var relations = new ParentAttachmentState();
        relations.AcceptCreateObjectRelation(new ParentAttachmentRelation(
            0x70000120u, childGuid, 1, 2, 0, 10));
        relations.Enqueue(new ParentEvent.Parsed(
            0x70000122u, childGuid, 1, 4, 8, 9));

        relations.Resolve(
            childGuid,
            _ => true,
            guid => guid == 0x70000122u ? (ushort)8 : null,
            update => gate.TryAcceptPositionChannelEvent(3, update.ChildPositionSequence));

        Assert.True(relations.TryGetProjection(childGuid, out ParentAttachmentRelation projection));
        Assert.Equal(0x70000120u, projection.ParentGuid);
        Assert.Equal((ushort)10, projection.ChildPositionSequence);
    }

    [Fact]
    public void FutureParentGenerationDoesNotBlockCurrentRelationBehindIt()
    {
        const uint childGuid = 0x70000141u;
        const uint parentGuid = 0x70000140u;
        var gate = new PhysicsTimestampGate();
        gate.SeedForCreateObject(4, 0, 0, 0, 0, 0, 0, 0, 3);
        var relations = new ParentAttachmentState();
        relations.Enqueue(new ParentEvent.Parsed(
            parentGuid, childGuid, 1, 2, 10, 6));
        relations.Enqueue(new ParentEvent.Parsed(
            parentGuid, childGuid, 1, 3, 9, 5));

        relations.Resolve(
            childGuid,
            _ => true,
            guid => guid == parentGuid ? (ushort)9 : null,
            update => gate.TryAcceptPositionChannelEvent(3, update.ChildPositionSequence));

        Assert.True(relations.TryGetProjection(childGuid, out ParentAttachmentRelation projection));
        Assert.Equal((ushort)5, projection.ChildPositionSequence);
        Assert.Equal((uint)3, projection.PlacementId);
        Assert.Contains(childGuid, relations.ChildrenWaitingForParent(parentGuid));
    }

    [Fact]
    public void FutureRelationSurvivesAtomicParentGenerationReplacement()
    {
        const uint parentGuid = 0x70000150u;
        const uint childGuid = 0x70000151u;
        var inbound = new InboundPhysicsStateController();
        var relations = new ParentAttachmentState();
        var objects = new ClientObjectTable();
        inbound.AcceptCreate(Spawn(parentGuid, instance: 9, positionSequence: 1));
        inbound.AcceptCreate(Spawn(childGuid, instance: 3, positionSequence: 4));
        relations.AcceptCreateObjectRelation(new ParentAttachmentRelation(
            parentGuid, childGuid, 1, 2, 0, 4));
        relations.Enqueue(new ParentEvent.Parsed(
            parentGuid, childGuid, 1, 3, 10, 5));
        Resolve(relations, inbound, childGuid);
        objects.Ingest(MinimalWeenie(parentGuid, "generation 9"));
        objects.ObjectRemovalClassified += removal =>
        {
            if (removal.Reason == ClientObjectRemovalReason.GenerationReplacement)
                relations.EndGeneration(removal.Object.ObjectId, removal.Generation);
        };

        inbound.AcceptCreate(Spawn(parentGuid, instance: 10, positionSequence: 1));
        objects.ReplaceGeneration(MinimalWeenie(parentGuid, "generation 10"), 10);
        Resolve(relations, inbound, childGuid);

        Assert.True(relations.TryGetProjection(childGuid, out ParentAttachmentRelation projection));
        Assert.Equal(parentGuid, projection.ParentGuid);
        Assert.Equal((ushort)10, projection.ParentInstanceSequence);
        Assert.Equal((ushort)5, projection.ChildPositionSequence);
        Assert.True(Commit(relations, inbound, projection, out _));
        Assert.True(inbound.TryGetSnapshot(childGuid, out WorldSession.EntitySpawn child));
        Assert.Equal(parentGuid, child.ParentGuid);
    }

    [Fact]
    public void FutureParentRelationSurvivesChildGenerationReplacement()
    {
        const uint parentGuid = 0x70000180u;
        const uint childGuid = 0x70000181u;
        var relations = new ParentAttachmentState();
        relations.Enqueue(new ParentEvent.Parsed(
            parentGuid, childGuid, 1, 3, 10, 5));

        relations.Resolve(
            childGuid,
            _ => true,
            guid => guid == parentGuid ? (ushort)9 : null,
            _ => throw new InvalidOperationException("future parent must not apply"));
        relations.EndGeneration(childGuid, replacementGeneration: 4);

        var replacementGate = new PhysicsTimestampGate();
        replacementGate.SeedForCreateObject(4, 0, 0, 0, 0, 0, 0, 0, 4);
        relations.Resolve(
            childGuid,
            _ => true,
            guid => guid == parentGuid ? (ushort)10 : null,
            update => replacementGate.TryAcceptPositionChannelEvent(
                4, update.ChildPositionSequence));

        Assert.True(relations.TryGetProjection(childGuid, out ParentAttachmentRelation projection));
        Assert.Equal((ushort)10, projection.ParentInstanceSequence);
        Assert.Equal((ushort)5, projection.ChildPositionSequence);
    }

    [Fact]
    public void RemoveAndClearDiscardPendingAndRollbackState()
    {
        const uint parentGuid = 0x70000130u;
        const uint childGuid = 0x70000131u;
        var relations = new ParentAttachmentState();
        relations.AcceptCreateObjectRelation(new ParentAttachmentRelation(
            parentGuid, childGuid, 1, 2, 0, 4));
        relations.Enqueue(new ParentEvent.Parsed(parentGuid, childGuid, 1, 3, 9, 5));

        relations.RemoveObject(parentGuid);
        Assert.False(relations.TryGetProjection(childGuid, out _));
        Assert.False(relations.RestoreLastAccepted(childGuid));
        Assert.Empty(relations.ChildrenWaitingForParent(parentGuid));

        relations.AcceptCreateObjectRelation(new ParentAttachmentRelation(
            parentGuid, childGuid, 1, 2, 0, 4));
        relations.Clear();
        Assert.False(relations.TryGetProjection(childGuid, out _));
        Assert.False(relations.RestoreLastAccepted(childGuid));
    }

    [Fact]
    public void UnparentProjectionRetainsFresherPendingParentEvent()
    {
        const uint parentGuid = 0x70000160u;
        const uint childGuid = 0x70000161u;
        var gate = new PhysicsTimestampGate();
        gate.SeedForCreateObject(4, 0, 0, 0, 0, 0, 0, 0, 3);
        var relations = new ParentAttachmentState();
        relations.AcceptCreateObjectRelation(new ParentAttachmentRelation(
            0x70000162u, childGuid, 1, 2, 0, 4));
        relations.Enqueue(new ParentEvent.Parsed(
            parentGuid, childGuid, 1, 3, 9, 5));

        relations.EndChildProjection(childGuid);
        Assert.False(relations.TryGetProjection(childGuid, out _));
        relations.Resolve(
            childGuid,
            _ => true,
            guid => guid == parentGuid ? (ushort)9 : null,
            update => gate.TryAcceptPositionChannelEvent(3, update.ChildPositionSequence));

        Assert.True(relations.TryGetProjection(childGuid, out ParentAttachmentRelation projection));
        Assert.Equal(parentGuid, projection.ParentGuid);
        Assert.Equal((ushort)5, projection.ChildPositionSequence);
    }

    [Fact]
    public void ExactParentDeleteRetainsOnlyStrictlyFutureGenerationRelation()
    {
        const uint parentGuid = 0x70000170u;
        const uint childGuid = 0x70000171u;
        var gate = new PhysicsTimestampGate();
        gate.SeedForCreateObject(4, 0, 0, 0, 0, 0, 0, 0, 3);
        var relations = new ParentAttachmentState();
        relations.Enqueue(new ParentEvent.Parsed(
            parentGuid, childGuid, 1, 2, 9, 5));
        relations.Enqueue(new ParentEvent.Parsed(
            parentGuid, childGuid, 1, 3, 10, 6));

        relations.DeleteGeneration(parentGuid, deletedGeneration: 9);
        relations.Resolve(
            childGuid,
            _ => true,
            guid => guid == parentGuid ? (ushort)10 : null,
            update => gate.TryAcceptPositionChannelEvent(3, update.ChildPositionSequence));

        Assert.True(relations.TryGetProjection(childGuid, out ParentAttachmentRelation projection));
        Assert.Equal((ushort)10, projection.ParentInstanceSequence);
        Assert.Equal((ushort)6, projection.ChildPositionSequence);
    }

    [Fact]
    public void CommittedChildrenAppendInCommitOrder()
    {
        const uint parentGuid = 0x70000200u;
        var relations = new ParentAttachmentState();

        Commit(relations, Relation(parentGuid, 0x70000201u, parentInstance: 9));
        Commit(relations, Relation(parentGuid, 0x70000202u, parentInstance: 9));
        IReadOnlyList<uint> committed = relations.ChildrenAttachedToParent(
            parentGuid,
            9);
        Commit(relations, Relation(parentGuid, 0x70000203u, parentInstance: 9));

        Assert.Same(
            committed,
            relations.ChildrenAttachedToParent(parentGuid, 9));
        Assert.Equal(
            [0x70000201u, 0x70000202u, 0x70000203u],
            committed);
    }

    [Fact]
    public void EndingChildProjectionSwapRemovesCommittedChild()
    {
        const uint parentGuid = 0x70000210u;
        var relations = new ParentAttachmentState();
        Commit(relations, Relation(parentGuid, 0x70000211u, parentInstance: 9));
        Commit(relations, Relation(parentGuid, 0x70000212u, parentInstance: 9));
        Commit(relations, Relation(parentGuid, 0x70000213u, parentInstance: 9));

        relations.EndChildProjection(0x70000211u);

        Assert.Equal(
            [0x70000213u, 0x70000212u],
            relations.ChildrenAttachedToParent(parentGuid, 9));
    }

    [Fact]
    public void ReparentSwapRemovesOldEntryAndAppendsNewEntry()
    {
        const uint oldParentGuid = 0x70000220u;
        const uint newParentGuid = 0x70000221u;
        const uint childGuid = 0x70000222u;
        var relations = new ParentAttachmentState();
        Commit(relations, Relation(oldParentGuid, childGuid, parentInstance: 9));
        Commit(relations, Relation(oldParentGuid, 0x70000223u, parentInstance: 9));
        Commit(relations, Relation(oldParentGuid, 0x70000224u, parentInstance: 9));
        Commit(relations, Relation(newParentGuid, 0x70000225u, parentInstance: 4));

        Commit(relations, Relation(newParentGuid, childGuid, parentInstance: 4));

        Assert.Equal(
            [0x70000224u, 0x70000223u],
            relations.ChildrenAttachedToParent(oldParentGuid, 9));
        Assert.Equal(
            [0x70000225u, childGuid],
            relations.ChildrenAttachedToParent(newParentGuid, 4));
    }

    [Fact]
    public void CommittedChildrenAreIsolatedByParentGuidAndIncarnation()
    {
        const uint parentGuid = 0x70000230u;
        const uint otherParentGuid = 0x70000231u;
        var relations = new ParentAttachmentState();
        Commit(relations, Relation(parentGuid, 0x70000232u, parentInstance: 9));
        Commit(relations, Relation(parentGuid, 0x70000233u, parentInstance: 10));
        Commit(relations, Relation(otherParentGuid, 0x70000234u, parentInstance: 9));

        Assert.Equal(
            [0x70000232u],
            relations.ChildrenAttachedToParent(parentGuid, 9));
        Assert.Equal(
            [0x70000233u],
            relations.ChildrenAttachedToParent(parentGuid, 10));
        Assert.Equal(
            [0x70000234u],
            relations.ChildrenAttachedToParent(otherParentGuid, 9));
        Assert.Empty(relations.ChildrenAttachedToParent(otherParentGuid, 10));
    }

    private static void Resolve(
        ParentAttachmentState relations,
        InboundPhysicsStateController inbound,
        uint childGuid) =>
        relations.Resolve(
            childGuid,
            guid => inbound.TryGetSnapshot(guid, out _),
            guid => inbound.TryGetSnapshot(guid, out WorldSession.EntitySpawn spawn)
                ? spawn.InstanceSequence
                : null,
            update => inbound.TryApplyParent(update, out _));

    private static bool Commit(
        ParentAttachmentState relations,
        InboundPhysicsStateController inbound,
        ParentAttachmentRelation relation,
        out WorldSession.EntitySpawn accepted) =>
        inbound.TryCommitParent(
            relation.ChildGuid,
            relation.ParentGuid,
            relation.ParentLocation,
            relation.PlacementId,
            relation.ChildPositionSequence,
            out accepted)
        && relations.CommitProjection(relation);

    private static void Commit(
        ParentAttachmentState relations,
        ParentAttachmentRelation relation)
    {
        relations.AcceptCreateObjectRelation(relation);
        Assert.True(relations.CommitProjection(relation));
    }

    private static ParentAttachmentRelation Relation(
        uint parentGuid,
        uint childGuid,
        ushort parentInstance) =>
        new(parentGuid, childGuid, 1, 2, parentInstance, 1);

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort instance,
        ushort positionSequence)
    {
        var position = new CreateObject.ServerPosition(
            0x0101FFFFu, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(
            positionSequence, 1, 1, 1, 0, 1, 0, 1, instance);
        var physics = new PhysicsSpawnData(
            RawState: 0x408u,
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
            guid,
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: 0x408u,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: positionSequence,
            Physics: physics);
    }

    private static WeenieData MinimalWeenie(uint guid, string name) => new(
        Guid: guid,
        Name: name,
        Type: null,
        WeenieClassId: 0,
        IconId: 0,
        IconOverlayId: 0,
        IconUnderlayId: 0,
        Effects: 0,
        Value: null,
        StackSize: null,
        StackSizeMax: null,
        Burden: null,
        ContainerId: null,
        WielderId: null,
        ValidLocations: null,
        CurrentWieldedLocation: null,
        Priority: null,
        ItemsCapacity: null,
        ContainersCapacity: null,
        Structure: null,
        MaxStructure: null,
        Workmanship: null);
}
