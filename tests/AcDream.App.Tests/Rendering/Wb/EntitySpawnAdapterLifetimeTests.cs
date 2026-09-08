using System.Numerics;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class EntitySpawnAdapterLifetimeTests
{
    [Fact]
    public void NinetySixLiveOwners_DeleteAndGuidReuse_BalanceEveryMeshReference()
    {
        const int ownerCount = 96;
        const int reuseCount = 32;
        const uint guidBase = 0x74000000u;
        var meshes = new RefCountingMeshAdapter();
        var textures = new RecordingTextureLifetime();
        var adapter = new EntitySpawnAdapter(
            textures,
            _ => MakeSequencer(),
            meshes);

        for (int i = 0; i < ownerCount; i++)
        {
            uint guid = guidBase + (uint)i;
            WorldEntity entity = MakeEntity((uint)i + 1u, guid);
            entity.MeshRefs =
            [
                new MeshRef(0x01000001u, Matrix4x4.Identity),
                new MeshRef(0x01000001u, Matrix4x4.Identity),
            ];
            entity.ApplyAppearance(
                entity.MeshRefs,
                entity.PaletteOverride,
                [new PartOverride(0, 0x01001000u + (uint)i)]);
            Assert.NotNull(adapter.OnCreate(entity));
            Assert.True(adapter.SetPresentationResident(entity, resident: true));
        }

        Assert.Equal(ownerCount * 2, meshes.TotalReferenceCount);
        for (int i = 0; i < reuseCount; i++)
        {
            uint guid = guidBase + (uint)i;
            adapter.OnRemove(guid);
            Assert.Null(adapter.GetState(guid));

            WorldEntity replacement = MakeEntity(1000u + (uint)i, guid);
            replacement.ApplyAppearance(
                replacement.MeshRefs,
                replacement.PaletteOverride,
                [new PartOverride(0, 0x01002000u + (uint)i)]);
            Assert.NotNull(adapter.OnCreate(replacement));
            Assert.True(adapter.SetPresentationResident(replacement, resident: true));
        }

        for (int i = 0; i < ownerCount; i++)
        {
            uint guid = guidBase + (uint)i;
            adapter.OnRemove(guid);
            Assert.Null(adapter.GetState(guid));
        }

        Assert.Equal(0, meshes.TotalReferenceCount);
        Assert.Empty(meshes.ReferenceCounts);
        Assert.Equal(meshes.IncrementCount, meshes.DecrementCount);
        Assert.Equal(ownerCount + reuseCount, textures.ReleasedOwnerIds.Count);
        Assert.Equal(ownerCount + reuseCount, textures.ReleasedOwnerIds.Distinct().Count());
    }

    [Fact]
    public void ProjectionSuspendResume_IsIdempotentAndPreservesAnimatedState()
    {
        const uint guid = 0x74001000u;
        var meshes = new RefCountingMeshAdapter();
        var textures = new RecordingTextureLifetime();
        int sequencerCreates = 0;
        var adapter = new EntitySpawnAdapter(
            textures,
            _ =>
            {
                sequencerCreates++;
                return MakeSequencer();
            },
            meshes);
        WorldEntity entity = MakeEntity(41u, guid);
        entity.ApplyAppearance(
            [
                new MeshRef(0x01000001u, Matrix4x4.Identity),
                new MeshRef(0x01000001u, Matrix4x4.Identity),
            ],
            entity.PaletteOverride,
            [new PartOverride(0, 0x01000002u)]);

        AnimatedEntityState state = Assert.IsType<AnimatedEntityState>(adapter.OnCreate(entity));
        Assert.Equal(0, meshes.TotalReferenceCount);
        Assert.False(adapter.SetPresentationResident(entity, resident: false));
        Assert.Empty(textures.ReleasedOwnerIds);

        Assert.True(adapter.SetPresentationResident(entity, resident: true));
        Assert.False(adapter.SetPresentationResident(entity, resident: true));
        Assert.Equal(2, meshes.TotalReferenceCount);

        Assert.True(adapter.SetPresentationResident(entity, resident: false));
        Assert.False(adapter.SetPresentationResident(entity, resident: false));
        Assert.Equal(0, meshes.TotalReferenceCount);
        Assert.Equal([entity.Id], textures.ReleasedOwnerIds);
        Assert.Same(state, adapter.GetState(guid));

        Assert.True(adapter.SetPresentationResident(entity, resident: true));
        Assert.Equal(2, meshes.TotalReferenceCount);
        Assert.Same(state, adapter.GetState(guid));
        Assert.Equal(1, sequencerCreates);

        adapter.OnRemove(guid);
        Assert.Equal(0, meshes.TotalReferenceCount);
        Assert.Equal([entity.Id, entity.Id], textures.ReleasedOwnerIds);
        Assert.Equal(meshes.IncrementCount, meshes.DecrementCount);
    }

    [Fact]
    public void RemoveWhileSuspended_DoesNotReleasePresentationResourcesTwice()
    {
        const uint guid = 0x74002000u;
        var meshes = new RefCountingMeshAdapter();
        var textures = new RecordingTextureLifetime();
        var adapter = new EntitySpawnAdapter(textures, _ => MakeSequencer(), meshes);
        WorldEntity entity = MakeEntity(51u, guid);

        adapter.OnCreate(entity);
        adapter.SetPresentationResident(entity, resident: true);
        adapter.SetPresentationResident(entity, resident: false);
        int decrementsAfterSuspend = meshes.DecrementCount;

        adapter.OnRemove(guid);
        adapter.OnRemove(guid);

        Assert.Null(adapter.GetState(guid));
        Assert.Equal(0, meshes.TotalReferenceCount);
        Assert.Equal(decrementsAfterSuspend, meshes.DecrementCount);
        Assert.Equal([entity.Id], textures.ReleasedOwnerIds);
    }

    [Fact]
    public void GuidReplacement_IgnoresDelayedEdgesFromDisplacedOwner()
    {
        const uint guid = 0x74003000u;
        var meshes = new RefCountingMeshAdapter();
        var textures = new RecordingTextureLifetime();
        var adapter = new EntitySpawnAdapter(textures, _ => MakeSequencer(), meshes);
        WorldEntity oldEntity = MakeEntity(61u, guid);
        oldEntity.MeshRefs = [new MeshRef(0x01000100u, Matrix4x4.Identity)];
        WorldEntity replacement = MakeEntity(61u, guid);
        replacement.MeshRefs = [new MeshRef(0x01000200u, Matrix4x4.Identity)];

        adapter.OnCreate(oldEntity);
        adapter.SetPresentationResident(oldEntity, resident: true);
        AnimatedEntityState replacementState = Assert.IsType<AnimatedEntityState>(
            adapter.OnCreate(replacement));

        Assert.Equal(0, meshes.TotalReferenceCount);
        Assert.Equal([oldEntity.Id], textures.ReleasedOwnerIds);
        Assert.False(adapter.OnRemove(oldEntity));
        Assert.False(adapter.SetPresentationResident(oldEntity, resident: false));
        Assert.False(adapter.SetPresentationResident(oldEntity, resident: true));
        Assert.Equal(0, meshes.TotalReferenceCount);

        Assert.True(adapter.SetPresentationResident(replacement, resident: true));
        Assert.Equal(1, meshes.TotalReferenceCount);
        Assert.Same(replacementState, adapter.GetState(guid));

        adapter.OnRemove(guid);
        Assert.Equal(0, meshes.TotalReferenceCount);
        Assert.Equal([oldEntity.Id, replacement.Id], textures.ReleasedOwnerIds);
        Assert.Equal(meshes.IncrementCount, meshes.DecrementCount);
    }

    [Fact]
    public void GuidReplacement_FactoryFailure_PreservesResidentPriorOwner()
    {
        const uint guid = 0x74004000u;
        var meshes = new RefCountingMeshAdapter();
        var textures = new RecordingTextureLifetime();
        WorldEntity oldEntity = MakeEntity(71u, guid);
        oldEntity.MeshRefs = [new MeshRef(0x01000300u, Matrix4x4.Identity)];
        WorldEntity replacement = MakeEntity(72u, guid);
        var adapter = new EntitySpawnAdapter(
            textures,
            entity => ReferenceEquals(entity, replacement)
                ? throw new InvalidOperationException("replacement factory failure")
                : MakeSequencer(),
            meshes);

        AnimatedEntityState oldState = Assert.IsType<AnimatedEntityState>(adapter.OnCreate(oldEntity));
        Assert.True(adapter.SetPresentationResident(oldEntity, resident: true));

        Assert.Throws<InvalidOperationException>(() => adapter.OnCreate(replacement));

        Assert.Same(oldState, adapter.GetState(guid));
        Assert.Equal(1, meshes.TotalReferenceCount);
        Assert.Empty(textures.ReleasedOwnerIds);
        Assert.False(adapter.SetPresentationResident(replacement, resident: true));
        Assert.True(adapter.SetPresentationResident(oldEntity, resident: false));
        Assert.Equal(0, meshes.TotalReferenceCount);
        Assert.Equal([oldEntity.Id], textures.ReleasedOwnerIds);
    }

    [Fact]
    public void ProjectionSuspend_PartialReleaseFailure_RemainsResidentAndRetriesOnlyUnfinishedResources()
    {
        const uint guid = 0x74005000u;
        const ulong firstMesh = 0x01000400u;
        const ulong secondMesh = 0x01000401u;
        var meshes = new FaultInjectingMeshAdapter();
        var textures = new FaultInjectingTextureLifetime();
        var adapter = new EntitySpawnAdapter(textures, _ => MakeSequencer(), meshes);
        WorldEntity entity = MakeEntity(81u, guid);
        entity.MeshRefs =
        [
            new MeshRef((uint)firstMesh, Matrix4x4.Identity),
            new MeshRef((uint)secondMesh, Matrix4x4.Identity),
        ];

        adapter.OnCreate(entity);
        Assert.True(adapter.SetPresentationResident(entity, resident: true));
        meshes.FailNextDecrement(secondMesh);

        Assert.Throws<AggregateException>(
            () => adapter.SetPresentationResident(entity, resident: false));

        Assert.NotNull(adapter.GetState(guid));
        Assert.True(adapter.SetPresentationResident(entity, resident: true));
        Assert.Equal(2, meshes.TotalReferenceCount);
        Assert.Equal(1, textures.AttemptCount);
        Assert.Equal(1, meshes.DecrementAttempts[firstMesh]);
        Assert.Equal(1, meshes.DecrementAttempts[secondMesh]);

        Assert.True(adapter.SetPresentationResident(entity, resident: false));
        Assert.Equal(0, meshes.TotalReferenceCount);
        Assert.Equal(2, textures.AttemptCount);
        Assert.Equal(2, meshes.DecrementAttempts[firstMesh]);
        Assert.Equal(2, meshes.DecrementAttempts[secondMesh]);
        Assert.False(adapter.SetPresentationResident(entity, resident: false));
    }

    [Fact]
    public void LogicalRemove_ReleaseFailure_KeepsOwnerReachableForRetry()
    {
        const uint guid = 0x74006000u;
        const ulong meshId = 0x01000500u;
        var meshes = new FaultInjectingMeshAdapter();
        var textures = new FaultInjectingTextureLifetime(failuresBeforeSuccess: 1);
        var adapter = new EntitySpawnAdapter(textures, _ => MakeSequencer(), meshes);
        WorldEntity entity = MakeEntity(91u, guid);
        entity.MeshRefs = [new MeshRef((uint)meshId, Matrix4x4.Identity)];
        AnimatedEntityState state = Assert.IsType<AnimatedEntityState>(adapter.OnCreate(entity));
        Assert.True(adapter.SetPresentationResident(entity, resident: true));

        Assert.Throws<AggregateException>(() => adapter.OnRemove(entity));

        Assert.Same(state, adapter.GetState(guid));
        Assert.Equal(0, meshes.TotalReferenceCount);
        Assert.Equal(1, meshes.DecrementAttempts[meshId]);
        Assert.Equal(1, textures.AttemptCount);

        Assert.True(adapter.OnRemove(entity));
        Assert.Null(adapter.GetState(guid));
        Assert.Equal(1, meshes.DecrementAttempts[meshId]);
        Assert.Equal(2, textures.AttemptCount);
        Assert.Equal(1, textures.SuccessCount);
    }

    [Fact]
    public void GuidReplacement_ReleaseFailure_PublishesReplacementOnlyAfterRetrySucceeds()
    {
        const uint guid = 0x74007000u;
        const ulong oldMeshId = 0x01000600u;
        var meshes = new FaultInjectingMeshAdapter();
        var textures = new FaultInjectingTextureLifetime(failuresBeforeSuccess: 1);
        var adapter = new EntitySpawnAdapter(textures, _ => MakeSequencer(), meshes);
        WorldEntity oldEntity = MakeEntity(101u, guid);
        oldEntity.MeshRefs = [new MeshRef((uint)oldMeshId, Matrix4x4.Identity)];
        WorldEntity replacement = MakeEntity(101u, guid);
        AnimatedEntityState oldState = Assert.IsType<AnimatedEntityState>(adapter.OnCreate(oldEntity));
        Assert.True(adapter.SetPresentationResident(oldEntity, resident: true));

        Assert.Throws<AggregateException>(() => adapter.OnCreate(replacement));

        Assert.Same(oldState, adapter.GetState(guid));
        Assert.False(adapter.SetPresentationResident(replacement, resident: true));
        Assert.Equal(0, meshes.TotalReferenceCount);
        Assert.Equal(1, meshes.DecrementAttempts[oldMeshId]);

        AnimatedEntityState replacementState = Assert.IsType<AnimatedEntityState>(
            adapter.OnCreate(replacement));
        Assert.Same(replacementState, adapter.GetState(guid));
        Assert.False(adapter.OnRemove(oldEntity));
        Assert.Equal(1, meshes.DecrementAttempts[oldMeshId]);
        Assert.Equal(2, textures.AttemptCount);
    }

    [Fact]
    public void LogicalRemove_ReentrantDuplicate_DoesNotPublishRemovalBeforeCleanupCompletes()
    {
        const uint guid = 0x74008000u;
        var meshes = new FaultInjectingMeshAdapter();
        EntitySpawnAdapter? adapter = null;
        WorldEntity entity = MakeEntity(111u, guid);
        bool invokeNestedRemove = true;
        var textures = new FaultInjectingTextureLifetime(
            onRelease: () =>
            {
                if (!invokeNestedRemove)
                    return;
                invokeNestedRemove = false;
                Assert.NotNull(adapter!.GetState(guid));
                adapter.OnRemove(entity);
            });
        adapter = new EntitySpawnAdapter(textures, _ => MakeSequencer(), meshes);
        adapter.OnCreate(entity);
        Assert.True(adapter.SetPresentationResident(entity, resident: true));

        AggregateException error = Assert.Throws<AggregateException>(() => adapter.OnRemove(entity));
        Assert.Contains(
            error.Flatten().InnerExceptions,
            exception => exception is EntityPresentationRemovalDeferredException);
        Assert.NotNull(adapter.GetState(guid));
        Assert.True(adapter.OnRemove(entity));

        Assert.Null(adapter.GetState(guid));
        Assert.Equal(0, meshes.TotalReferenceCount);
        Assert.Equal(2, textures.AttemptCount);
    }

    [Fact]
    public void LogicalRemove_AfterResumeRollbackFailure_ReleasesResidualMeshReference()
    {
        const uint guid = 0x74009000u;
        const ulong firstMesh = 0x01000700u;
        const ulong secondMesh = 0x01000701u;
        var meshes = new FaultInjectingMeshAdapter();
        var textures = new FaultInjectingTextureLifetime();
        var adapter = new EntitySpawnAdapter(textures, _ => MakeSequencer(), meshes);
        WorldEntity entity = MakeEntity(121u, guid);
        entity.MeshRefs =
        [
            new MeshRef((uint)firstMesh, Matrix4x4.Identity),
            new MeshRef((uint)secondMesh, Matrix4x4.Identity),
        ];
        AnimatedEntityState state = Assert.IsType<AnimatedEntityState>(adapter.OnCreate(entity));
        meshes.FailNextIncrement(secondMesh);
        meshes.FailNextDecrement(firstMesh);

        Assert.Throws<AggregateException>(
            () => adapter.SetPresentationResident(entity, resident: true));
        Assert.Equal(1, meshes.TotalReferenceCount);
        Assert.Same(state, adapter.GetState(guid));

        Assert.True(adapter.OnRemove(entity));

        Assert.Null(adapter.GetState(guid));
        Assert.Equal(0, meshes.TotalReferenceCount);
        Assert.Equal(2, meshes.DecrementAttempts[firstMesh]);
        Assert.Equal(0, textures.AttemptCount);
    }

    [Fact]
    public void Resume_IncrementThrowsAfterCommit_RollsBackCommittedReference()
    {
        const uint guid = 0x7400A000u;
        const ulong meshId = 0x01000800u;
        var meshes = new FaultInjectingMeshAdapter();
        var adapter = new EntitySpawnAdapter(
            new FaultInjectingTextureLifetime(), _ => MakeSequencer(), meshes);
        WorldEntity entity = MakeEntity(131u, guid);
        entity.MeshRefs = [new MeshRef((uint)meshId, Matrix4x4.Identity)];
        adapter.OnCreate(entity);
        meshes.ThrowAfterNextIncrement(meshId);

        Assert.Throws<MeshReferenceMutationException>(
            () => adapter.SetPresentationResident(entity, resident: true));

        Assert.Equal(0, meshes.TotalReferenceCount);
        Assert.Equal(1, meshes.DecrementAttempts[meshId]);
        Assert.True(adapter.SetPresentationResident(entity, resident: true));
        Assert.Equal(1, meshes.TotalReferenceCount);
    }

    [Fact]
    public void Suspend_DecrementThrowsAfterCommit_RetryDoesNotDoubleDecrement()
    {
        const uint guid = 0x7400B000u;
        const ulong meshId = 0x01000900u;
        var meshes = new FaultInjectingMeshAdapter();
        var adapter = new EntitySpawnAdapter(
            new FaultInjectingTextureLifetime(), _ => MakeSequencer(), meshes);
        WorldEntity entity = MakeEntity(141u, guid);
        entity.MeshRefs = [new MeshRef((uint)meshId, Matrix4x4.Identity)];
        adapter.OnCreate(entity);
        Assert.True(adapter.SetPresentationResident(entity, resident: true));
        meshes.ThrowAfterNextDecrement(meshId);

        Assert.Throws<AggregateException>(
            () => adapter.SetPresentationResident(entity, resident: false));
        Assert.Equal(0, meshes.TotalReferenceCount);
        Assert.Equal(1, meshes.DecrementAttempts[meshId]);

        Assert.True(adapter.SetPresentationResident(entity, resident: false));
        Assert.Equal(0, meshes.TotalReferenceCount);
        Assert.Equal(1, meshes.DecrementAttempts[meshId]);
    }

    [Fact]
    public void AppearanceChange_Resident_AcquiresReplacementBeforePublicationAndRetiresOldAfter()
    {
        const uint guid = 0x7400C000u;
        const ulong oldMesh = 0x01000A00u;
        const ulong newMesh = 0x01000A01u;
        const ulong overrideMesh = 0x01000A02u;
        var meshes = new CallbackMeshAdapter();
        var adapter = new EntitySpawnAdapter(
            new RecordingTextureLifetime(), _ => MakeSequencer(), meshes);
        WorldEntity entity = MakeEntity(151u, guid);
        entity.MeshRefs = [new MeshRef((uint)oldMesh, Matrix4x4.Identity)];
        adapter.OnCreate(entity);
        Assert.True(adapter.SetPresentationResident(entity, resident: true));
        meshes.Operations.Clear();

        MeshRef[] replacement = [new MeshRef((uint)newMesh, Matrix4x4.Identity)];
        PartOverride[] overrides = [new PartOverride(0, (uint)overrideMesh)];
        Assert.True(adapter.OnAppearanceChanged(
            entity,
            replacement,
            overrides,
            () =>
            {
                Assert.Equal(1, meshes.ReferenceCounts[oldMesh]);
                Assert.Equal(1, meshes.ReferenceCounts[newMesh]);
                Assert.Equal(1, meshes.ReferenceCounts[overrideMesh]);
                meshes.Operations.Add("publish");
                entity.ApplyAppearance(replacement, paletteOverride: null, overrides);
            }));

        int publicationIndex = meshes.Operations.IndexOf("publish");
        Assert.True(meshes.Operations.IndexOf($"increment:{newMesh:X8}") < publicationIndex);
        Assert.True(meshes.Operations.IndexOf($"increment:{overrideMesh:X8}") < publicationIndex);
        Assert.True(meshes.Operations.IndexOf($"decrement:{oldMesh:X8}") > publicationIndex);
        Assert.False(meshes.ReferenceCounts.ContainsKey(oldMesh));
        Assert.Equal(1, meshes.ReferenceCounts[newMesh]);
        Assert.Equal(1, meshes.ReferenceCounts[overrideMesh]);

        Assert.True(adapter.OnRemove(entity));
        Assert.Equal(0, meshes.TotalReferenceCount);
    }

    [Fact]
    public void AppearanceChange_AfterPublicationRunsBeforeOldRetirement()
    {
        const uint guid = 0x7400C100u;
        const ulong oldMesh = 0x01000A10u;
        const ulong newMesh = 0x01000A11u;
        var meshes = new CallbackMeshAdapter();
        var adapter = new EntitySpawnAdapter(
            new RecordingTextureLifetime(), _ => MakeSequencer(), meshes);
        WorldEntity entity = MakeEntity(152u, guid);
        entity.MeshRefs = [new MeshRef((uint)oldMesh, Matrix4x4.Identity)];
        adapter.OnCreate(entity);
        Assert.True(adapter.SetPresentationResident(entity, resident: true));
        meshes.Operations.Clear();
        MeshRef[] replacement = [new MeshRef((uint)newMesh, Matrix4x4.Identity)];

        Assert.True(adapter.OnAppearanceChanged(
            entity,
            replacement,
            [],
            () =>
            {
                meshes.Operations.Add("publish");
                entity.ApplyAppearance(replacement, paletteOverride: null, []);
            },
            () =>
            {
                Assert.Same(replacement, entity.MeshRefs);
                Assert.Equal(1, meshes.ReferenceCounts[oldMesh]);
                Assert.Equal(1, meshes.ReferenceCounts[newMesh]);
                meshes.Operations.Add("after-publication");
            }));

        int afterPublication = meshes.Operations.IndexOf("after-publication");
        Assert.True(meshes.Operations.IndexOf("publish") < afterPublication);
        Assert.True(meshes.Operations.IndexOf($"decrement:{oldMesh:X8}") > afterPublication);
        Assert.True(adapter.OnRemove(entity));
        Assert.Equal(0, meshes.TotalReferenceCount);
    }

    [Fact]
    public void AppearanceChange_AfterPublicationFailureKeepsCommittedSetAndRetryRetiresOld()
    {
        const uint guid = 0x7400C200u;
        const ulong oldMesh = 0x01000A20u;
        const ulong newMesh = 0x01000A21u;
        var meshes = new RefCountingMeshAdapter();
        var adapter = new EntitySpawnAdapter(
            new RecordingTextureLifetime(), _ => MakeSequencer(), meshes);
        WorldEntity entity = MakeEntity(153u, guid);
        entity.MeshRefs = [new MeshRef((uint)oldMesh, Matrix4x4.Identity)];
        adapter.OnCreate(entity);
        Assert.True(adapter.SetPresentationResident(entity, resident: true));
        MeshRef[] replacement = [new MeshRef((uint)newMesh, Matrix4x4.Identity)];

        Assert.Throws<InvalidOperationException>(() => adapter.OnAppearanceChanged(
            entity,
            replacement,
            [],
            () => entity.ApplyAppearance(replacement, paletteOverride: null, []),
            () => throw new InvalidOperationException("injected dependent publication failure")));

        Assert.Same(replacement, entity.MeshRefs);
        Assert.Equal(1, meshes.ReferenceCounts[oldMesh]);
        Assert.Equal(1, meshes.ReferenceCounts[newMesh]);
        Assert.True(adapter.SetPresentationResident(entity, resident: true));
        Assert.False(meshes.ReferenceCounts.ContainsKey(oldMesh));
        Assert.Equal(1, meshes.ReferenceCounts[newMesh]);
        Assert.True(adapter.OnRemove(entity));
        Assert.Equal(0, meshes.TotalReferenceCount);
    }

    [Fact]
    public void AppearanceChange_WhileSuspended_OnlyReplacementSetIsAcquiredOnResume()
    {
        const uint guid = 0x7400D000u;
        const ulong oldMesh = 0x01000B00u;
        const ulong newMesh = 0x01000B01u;
        var meshes = new RefCountingMeshAdapter();
        var adapter = new EntitySpawnAdapter(
            new RecordingTextureLifetime(), _ => MakeSequencer(), meshes);
        WorldEntity entity = MakeEntity(161u, guid);
        entity.MeshRefs = [new MeshRef((uint)oldMesh, Matrix4x4.Identity)];
        adapter.OnCreate(entity);

        MeshRef[] replacement = [new MeshRef((uint)newMesh, Matrix4x4.Identity)];
        Assert.True(adapter.OnAppearanceChanged(
            entity,
            replacement,
            [],
            () => entity.ApplyAppearance(replacement, paletteOverride: null, [])));

        Assert.Equal(0, meshes.TotalReferenceCount);
        Assert.True(adapter.SetPresentationResident(entity, resident: true));
        Assert.False(meshes.ReferenceCounts.ContainsKey(oldMesh));
        Assert.Equal(1, meshes.ReferenceCounts[newMesh]);
        Assert.True(adapter.OnRemove(entity));
        Assert.Equal(0, meshes.TotalReferenceCount);
    }

    [Fact]
    public void AppearanceChange_CommittedAcquireFailure_RollsBackAndDoesNotPublish()
    {
        const uint guid = 0x7400E000u;
        const ulong oldMesh = 0x01000C00u;
        const ulong newMesh = 0x01000C01u;
        var meshes = new FaultInjectingMeshAdapter();
        var adapter = new EntitySpawnAdapter(
            new RecordingTextureLifetime(), _ => MakeSequencer(), meshes);
        WorldEntity entity = MakeEntity(171u, guid);
        entity.MeshRefs = [new MeshRef((uint)oldMesh, Matrix4x4.Identity)];
        adapter.OnCreate(entity);
        Assert.True(adapter.SetPresentationResident(entity, resident: true));
        meshes.ThrowAfterNextIncrement(newMesh);
        bool published = false;

        Assert.Throws<MeshReferenceMutationException>(() =>
            adapter.OnAppearanceChanged(
                entity,
                [new MeshRef((uint)newMesh, Matrix4x4.Identity)],
                [],
                () => published = true));

        Assert.False(published);
        Assert.Equal(1, meshes.ReferenceCounts[oldMesh]);
        Assert.False(meshes.ReferenceCounts.ContainsKey(newMesh));
        Assert.Equal(1, meshes.DecrementAttempts[newMesh]);
        Assert.True(adapter.OnRemove(entity));
        Assert.Equal(0, meshes.TotalReferenceCount);
    }

    [Fact]
    public void AppearanceChange_ReleaseFailure_RetainsMarkerForResidencyRetry()
    {
        const uint guid = 0x7400F000u;
        const ulong oldMesh = 0x01000D00u;
        const ulong newMesh = 0x01000D01u;
        var meshes = new FaultInjectingMeshAdapter();
        var adapter = new EntitySpawnAdapter(
            new RecordingTextureLifetime(), _ => MakeSequencer(), meshes);
        WorldEntity entity = MakeEntity(181u, guid);
        entity.MeshRefs = [new MeshRef((uint)oldMesh, Matrix4x4.Identity)];
        adapter.OnCreate(entity);
        Assert.True(adapter.SetPresentationResident(entity, resident: true));
        meshes.FailNextDecrement(oldMesh);
        int publications = 0;
        MeshRef[] replacement = [new MeshRef((uint)newMesh, Matrix4x4.Identity)];

        Assert.Throws<AggregateException>(() =>
            adapter.OnAppearanceChanged(
                entity,
                replacement,
                [],
                () =>
                {
                    publications++;
                    entity.ApplyAppearance(replacement, paletteOverride: null, []);
                }));

        Assert.Equal(1, publications);
        Assert.Equal(2, meshes.TotalReferenceCount);
        Assert.True(adapter.SetPresentationResident(entity, resident: true));
        Assert.Equal(1, publications);
        Assert.False(meshes.ReferenceCounts.ContainsKey(oldMesh));
        Assert.Equal(1, meshes.ReferenceCounts[newMesh]);
        Assert.Equal(2, meshes.DecrementAttempts[oldMesh]);
        Assert.True(adapter.OnRemove(entity));
        Assert.Equal(0, meshes.TotalReferenceCount);
    }

    [Fact]
    public void AppearanceChange_ReentrantRequestCannotPublishInsideOuterTransition()
    {
        const uint guid = 0x74010000u;
        const ulong oldMesh = 0x01000E00u;
        const ulong outerMesh = 0x01000E01u;
        const ulong nestedMesh = 0x01000E02u;
        var meshes = new CallbackMeshAdapter();
        var adapter = new EntitySpawnAdapter(
            new RecordingTextureLifetime(), _ => MakeSequencer(), meshes);
        WorldEntity entity = MakeEntity(191u, guid);
        entity.MeshRefs = [new MeshRef((uint)oldMesh, Matrix4x4.Identity)];
        adapter.OnCreate(entity);
        Assert.True(adapter.SetPresentationResident(entity, resident: true));
        bool nestedPublished = false;
        bool nestedResult = true;
        meshes.AfterIncrement = meshId =>
        {
            if (meshId != outerMesh)
                return;

            nestedResult = adapter.OnAppearanceChanged(
                entity,
                [new MeshRef((uint)nestedMesh, Matrix4x4.Identity)],
                [],
                () => nestedPublished = true);
        };

        MeshRef[] replacement = [new MeshRef((uint)outerMesh, Matrix4x4.Identity)];
        Assert.True(adapter.OnAppearanceChanged(
            entity,
            replacement,
            [],
            () => entity.ApplyAppearance(replacement, paletteOverride: null, [])));

        Assert.False(nestedResult);
        Assert.False(nestedPublished);
        Assert.Equal(1, meshes.ReferenceCounts[outerMesh]);
        Assert.False(meshes.ReferenceCounts.ContainsKey(oldMesh));
        Assert.False(meshes.ReferenceCounts.ContainsKey(nestedMesh));
        Assert.True(adapter.OnRemove(entity));
        Assert.Equal(0, meshes.TotalReferenceCount);
    }

    [Fact]
    public void AppearanceChange_DelayedOldIncarnationCannotAffectGuidReplacement()
    {
        const uint guid = 0x74011000u;
        const ulong oldMesh = 0x01000F00u;
        const ulong replacementMesh = 0x01000F01u;
        const ulong staleMesh = 0x01000F02u;
        var meshes = new RefCountingMeshAdapter();
        var adapter = new EntitySpawnAdapter(
            new RecordingTextureLifetime(), _ => MakeSequencer(), meshes);
        WorldEntity oldEntity = MakeEntity(201u, guid);
        oldEntity.MeshRefs = [new MeshRef((uint)oldMesh, Matrix4x4.Identity)];
        adapter.OnCreate(oldEntity);
        Assert.True(adapter.SetPresentationResident(oldEntity, resident: true));

        WorldEntity replacement = MakeEntity(201u, guid);
        replacement.MeshRefs = [new MeshRef((uint)replacementMesh, Matrix4x4.Identity)];
        adapter.OnCreate(replacement);
        Assert.True(adapter.SetPresentationResident(replacement, resident: true));
        bool stalePublished = false;

        Assert.False(adapter.OnAppearanceChanged(
            oldEntity,
            [new MeshRef((uint)staleMesh, Matrix4x4.Identity)],
            [],
            () => stalePublished = true));

        Assert.False(stalePublished);
        Assert.False(meshes.ReferenceCounts.ContainsKey(oldMesh));
        Assert.False(meshes.ReferenceCounts.ContainsKey(staleMesh));
        Assert.Equal(1, meshes.ReferenceCounts[replacementMesh]);
        Assert.True(adapter.OnRemove(replacement));
        Assert.Equal(0, meshes.TotalReferenceCount);
    }

    private static WorldEntity MakeEntity(uint id, uint serverGuid) => new()
    {
        Id = id,
        ServerGuid = serverGuid,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = [new MeshRef(0x01000001u, Matrix4x4.Identity)],
    };

    private static AnimationSequencer MakeSequencer() =>
        new(new Setup(), new MotionTable(), new NullAnimationLoader());

    private sealed class NullAnimationLoader : IAnimationLoader
    {
        public Animation? LoadAnimation(uint id) => null;
    }

    private sealed class RecordingTextureLifetime : IEntityTextureLifetime
    {
        public List<uint> ReleasedOwnerIds { get; } = new();

        public void ReleaseOwner(uint localEntityId) => ReleasedOwnerIds.Add(localEntityId);
    }

    private sealed class RefCountingMeshAdapter : IWbMeshAdapter
    {
        public Dictionary<ulong, int> ReferenceCounts { get; } = new();
        public int IncrementCount { get; private set; }
        public int DecrementCount { get; private set; }
        public int TotalReferenceCount => ReferenceCounts.Values.Sum();

        public void IncrementRefCount(ulong id)
        {
            ReferenceCounts[id] = ReferenceCounts.GetValueOrDefault(id) + 1;
            IncrementCount++;
        }

        public void DecrementRefCount(ulong id)
        {
            Assert.True(ReferenceCounts.TryGetValue(id, out int count));
            Assert.True(count > 0);
            if (count == 1)
                ReferenceCounts.Remove(id);
            else
                ReferenceCounts[id] = count - 1;
            DecrementCount++;
        }
    }

    private sealed class CallbackMeshAdapter : IWbMeshAdapter
    {
        public Dictionary<ulong, int> ReferenceCounts { get; } = new();
        public List<string> Operations { get; } = new();
        public Action<ulong>? AfterIncrement { get; set; }
        public int TotalReferenceCount => ReferenceCounts.Values.Sum();

        public void IncrementRefCount(ulong id)
        {
            ReferenceCounts[id] = ReferenceCounts.GetValueOrDefault(id) + 1;
            Operations.Add($"increment:{id:X8}");
            AfterIncrement?.Invoke(id);
        }

        public void DecrementRefCount(ulong id)
        {
            Assert.True(ReferenceCounts.TryGetValue(id, out int count));
            Assert.True(count > 0);
            if (count == 1)
                ReferenceCounts.Remove(id);
            else
                ReferenceCounts[id] = count - 1;
            Operations.Add($"decrement:{id:X8}");
        }
    }

    private sealed class FaultInjectingTextureLifetime(
        int failuresBeforeSuccess = 0,
        Action? onRelease = null) : IEntityTextureLifetime
    {
        private int _failuresRemaining = failuresBeforeSuccess;

        public int AttemptCount { get; private set; }
        public int SuccessCount { get; private set; }

        public void ReleaseOwner(uint localEntityId)
        {
            AttemptCount++;
            onRelease?.Invoke();
            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                throw new InvalidOperationException("injected texture release failure");
            }

            SuccessCount++;
        }
    }

    private sealed class FaultInjectingMeshAdapter : IWbMeshAdapter
    {
        private readonly Dictionary<ulong, int> _remainingIncrementFailures = new();
        private readonly Dictionary<ulong, int> _remainingDecrementFailures = new();
        private readonly HashSet<ulong> _throwAfterIncrement = new();
        private readonly HashSet<ulong> _throwAfterDecrement = new();

        public Dictionary<ulong, int> ReferenceCounts { get; } = new();
        public Dictionary<ulong, int> DecrementAttempts { get; } = new();
        public int TotalReferenceCount => ReferenceCounts.Values.Sum();

        public void FailNextIncrement(ulong id) =>
            _remainingIncrementFailures[id] =
                _remainingIncrementFailures.GetValueOrDefault(id) + 1;

        public void FailNextDecrement(ulong id) =>
            _remainingDecrementFailures[id] =
                _remainingDecrementFailures.GetValueOrDefault(id) + 1;

        public void ThrowAfterNextIncrement(ulong id) => _throwAfterIncrement.Add(id);
        public void ThrowAfterNextDecrement(ulong id) => _throwAfterDecrement.Add(id);

        public void IncrementRefCount(ulong id)
        {
            if (_remainingIncrementFailures.GetValueOrDefault(id) > 0)
            {
                _remainingIncrementFailures[id]--;
                throw new InvalidOperationException("injected mesh acquire failure");
            }

            ReferenceCounts[id] = ReferenceCounts.GetValueOrDefault(id) + 1;
            if (_throwAfterIncrement.Remove(id))
            {
                throw new MeshReferenceMutationException(
                    "injected post-commit mesh acquire failure",
                    mutationCommitted: true,
                    new InvalidOperationException("injected metadata failure"));
            }
        }

        public void DecrementRefCount(ulong id)
        {
            DecrementAttempts[id] = DecrementAttempts.GetValueOrDefault(id) + 1;
            if (_remainingDecrementFailures.GetValueOrDefault(id) > 0)
            {
                _remainingDecrementFailures[id]--;
                throw new InvalidOperationException("injected mesh release failure");
            }

            Assert.True(ReferenceCounts.TryGetValue(id, out int count));
            Assert.True(count > 0);
            if (count == 1)
                ReferenceCounts.Remove(id);
            else
                ReferenceCounts[id] = count - 1;
            if (_throwAfterDecrement.Remove(id))
            {
                throw new MeshReferenceMutationException(
                    "injected post-commit mesh release failure",
                    mutationCommitted: true,
                    new InvalidOperationException("injected cancellation failure"));
            }
        }
    }
}
