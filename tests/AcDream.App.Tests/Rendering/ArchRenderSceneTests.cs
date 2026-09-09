using System.Numerics;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Scene.Arch;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering;

public sealed class ArchRenderSceneTests
{
    [Fact]
    public void Apply_RegisterChannelUpdatesAndUnregister_PreservesTypedRecord()
    {
        RenderSceneGeneration generation = Generation(1);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord original = Record(
            id: 10,
            incarnation: 3,
            RenderProjectionClass.LiveDynamicRoot,
            x: 1,
            bucket: 0xAABB);

        RenderDeltaApplyResult register = scene.Apply(
            [RenderProjectionDelta.Register(generation, 1, original)]);

        Assert.Equal(1, register.Applied);
        Assert.Equal(1, register.Registered);
        Assert.Equal(
            new RenderProjectionCounts(1, 0, 0, 1, 0, 0),
            scene.Counts);

        RenderProjectionRecord transformed = original with
        {
            PreviousTransform = new PreviousRenderTransform(
                original.Transform.LocalToWorld),
            Transform = new RenderTransform(
                Matrix4x4.CreateTranslation(4, 5, 6)),
            Bounds = new RenderWorldBounds(
                new Vector3(3, 4, 5),
                new Vector3(5, 6, 7)),
            SortKey = new RenderSortKey(99),
        };
        RenderProjectionRecord appearance = transformed with
        {
            MeshSet = new RenderMeshSet(Asset(80), 4, 7),
            Material = new RenderMaterialVariant(12, 13, 0.5f),
            DegradeState = new RenderDegradeState(2, 8),
        };
        RenderProjectionRecord flags = appearance with
        {
            Flags = RenderProjectionFlags.Draw
                | RenderProjectionFlags.Translucent,
        };
        RenderProjectionRecord rebucketed = flags with
        {
            Residency = new RenderSpatialResidency(
                Bucket(0xCCDD),
                0xCCDD0000,
                0xCCDD0102),
        };

        RenderDeltaApplyResult updates = scene.Apply(
        [
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateTransform,
                generation,
                2,
                transformed),
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateAppearance,
                generation,
                3,
                appearance),
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateFlags,
                generation,
                4,
                flags),
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.Rebucket,
                generation,
                5,
                rebucketed),
        ]);

        Assert.Equal(4, updates.Applied);
        Assert.Equal(4, updates.Updated);
        Assert.True(scene.OpenQuery().TryGet(original.Id, out var stored));
        Assert.Equal(rebucketed.Transform, stored.Transform);
        Assert.Equal(rebucketed.Bounds, stored.Bounds);
        Assert.Equal(rebucketed.MeshSet, stored.MeshSet);
        Assert.Equal(rebucketed.Material, stored.Material);
        Assert.Equal(rebucketed.Flags, stored.Flags);
        Assert.Equal(rebucketed.Residency, stored.Residency);
        Assert.Equal(
            RenderDirtyMask.All,
            stored.DirtyMask);

        RenderDeltaApplyResult unregister = scene.Apply(
        [
            RenderProjectionDelta.Unregister(
                generation,
                6,
                original.Id,
                original.OwnerIncarnation),
        ]);

        Assert.Equal(1, unregister.Applied);
        Assert.Equal(1, unregister.Unregistered);
        Assert.Equal(default, scene.Counts);
        Assert.False(scene.OpenQuery().TryGet(original.Id, out _));
    }

    [Fact]
    public void Register_NewerIncarnationReplacesAndOldCallbacksCannotRemoveIt()
    {
        RenderSceneGeneration generation = Generation(4);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord first = Record(
            44,
            10,
            RenderProjectionClass.OutdoorStatic);
        RenderProjectionRecord replacement = Record(
            44,
            11,
            RenderProjectionClass.ActiveAnimatedStatic,
            x: 9);

        scene.Apply([RenderProjectionDelta.Register(generation, 1, first)]);
        RenderDeltaApplyResult replaced = scene.Apply(
            [RenderProjectionDelta.Register(generation, 2, replacement)]);
        RenderDeltaApplyResult staleRegister = scene.Apply(
            [RenderProjectionDelta.Register(generation, 3, first)]);
        RenderDeltaApplyResult staleDelete = scene.Apply(
        [
            RenderProjectionDelta.Unregister(
                generation,
                4,
                first.Id,
                first.OwnerIncarnation),
        ]);

        Assert.Equal(1, replaced.Replaced);
        Assert.Equal(1, replaced.Registered);
        Assert.Equal(1, staleRegister.RejectedStaleIncarnation);
        Assert.Equal(1, staleDelete.RejectedStaleIncarnation);
        Assert.Equal(
            new RenderProjectionCounts(1, 0, 0, 0, 1, 0),
            scene.Counts);
        Assert.True(scene.OpenQuery().TryGet(first.Id, out var current));
        Assert.Equal(replacement.OwnerIncarnation, current.OwnerIncarnation);
        Assert.Equal(replacement.Transform, current.Transform);
    }

    [Fact]
    public void Apply_RejectsWrongGenerationOutOfOrderAndMissingDeltas()
    {
        RenderSceneGeneration generation = Generation(7);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord record = Record(
            70,
            1,
            RenderProjectionClass.IndoorCellStatic);

        RenderDeltaApplyResult result = scene.Apply(
        [
            RenderProjectionDelta.Register(Generation(6), 1, record),
            RenderProjectionDelta.Register(generation, 2, record),
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateFlags,
                generation,
                2,
                record),
            RenderProjectionDelta.Unregister(
                generation,
                3,
                Projection(999),
                Incarnation(1)),
        ]);

        Assert.Equal(1, result.Applied);
        Assert.Equal(1, result.RejectedGeneration);
        Assert.Equal(1, result.RejectedOutOfOrderSequence);
        Assert.Equal(1, result.RejectedMissing);
        Assert.Equal(1, scene.Counts.Total);
    }

    [Fact]
    public void Clear_AdvancesGenerationReclaimsRecordsAndInvalidatesBorrowedQuery()
    {
        RenderSceneGeneration firstGeneration = Generation(1);
        using var scene = new ArchRenderScene(firstGeneration);
        RenderProjectionRecord record = Record(
            1,
            1,
            RenderProjectionClass.EquippedChild);
        scene.Apply(
            [RenderProjectionDelta.Register(firstGeneration, 1, record)]);
        RenderSceneQuery borrowed = scene.OpenQuery();

        scene.Clear(Generation(2));

        Assert.Equal(Generation(2), scene.Generation);
        Assert.Equal(default, scene.Counts);
        Assert.Equal(0, scene.Memory.EntityCount);
        Assert.Throws<InvalidOperationException>(() => _ = borrowed.Counts);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => scene.Clear(Generation(2)));

        RenderProjectionRecord next = record with
        {
            OwnerIncarnation = Incarnation(2),
        };
        RenderDeltaApplyResult applied = scene.Apply(
            [RenderProjectionDelta.Register(Generation(2), 1, next)]);
        Assert.Equal(1, applied.Registered);
    }

    [Fact]
    public void FiveProjectionClasses_UseNarrowArchetypesAndReportRetainedMemory()
    {
        RenderSceneGeneration generation = Generation(9);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionClass[] classes =
        [
            RenderProjectionClass.OutdoorStatic,
            RenderProjectionClass.IndoorCellStatic,
            RenderProjectionClass.LiveDynamicRoot,
            RenderProjectionClass.ActiveAnimatedStatic,
            RenderProjectionClass.EquippedChild,
        ];
        var deltas = new RenderProjectionDelta[classes.Length];
        for (int i = 0; i < classes.Length; i++)
        {
            RenderProjectionRecord record = Record(
                (ulong)i + 1,
                1,
                classes[i]);
            deltas[i] = RenderProjectionDelta.Register(
                generation,
                (ulong)i + 1,
                record);
        }

        scene.Apply(deltas);
        RenderSceneMemoryAccounting memory = scene.Memory;

        Assert.Equal(5, scene.Counts.Total);
        Assert.Equal(5, memory.EntityCount);
        Assert.Equal(5, memory.ArchetypeCount);
        Assert.Equal(5, memory.AllocatedChunkCount);
        Assert.True(memory.ArchEntityCapacity >= memory.EntityCount);
        Assert.True(memory.EstimatedChunkPayloadBytes > 0);
        Assert.True(memory.ProjectionLookupCapacity >= memory.EntityCount);
        Assert.True(memory.EstimatedProjectionLookupBytes > 0);
        Assert.Equal(
            memory.EstimatedChunkPayloadBytes
            + memory.EstimatedProjectionLookupBytes
            + memory.EstimatedIndexBytes,
            memory.TotalEstimatedBytes);
    }

    [Fact]
    public void DynamicSynchronization_UpdatesOnlyMatchingLiveIncarnation()
    {
        RenderSceneGeneration generation = Generation(10);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord record = Record(
            100,
            5,
            RenderProjectionClass.LiveDynamicRoot);
        scene.Apply([RenderProjectionDelta.Register(generation, 1, record)]);

        DynamicProjectionUpdate stale = new(
            record.Id,
            Incarnation(4),
            new RenderTransform(Matrix4x4.CreateTranslation(100, 0, 0)),
            record.Bounds);
        DynamicProjectionUpdate current = new(
            record.Id,
            record.OwnerIncarnation,
            new RenderTransform(Matrix4x4.CreateTranslation(8, 9, 10)),
            new RenderWorldBounds(
                new Vector3(7, 8, 9),
                new Vector3(9, 10, 11)));
        scene.SynchronizeDynamicSources(
            new DynamicProjectionSyncInput(generation, [stale, current]));

        Assert.True(scene.OpenQuery().TryGet(record.Id, out var stored));
        Assert.Equal(record.Transform.LocalToWorld, stored.PreviousTransform.LocalToWorld);
        Assert.Equal(current.Transform, stored.Transform);
        Assert.Equal(current.Bounds, stored.Bounds);
    }

    [Fact]
    public void TransformUpdateBatch_ReusesRetainedStorage()
    {
        const int count = 1_000;
        RenderSceneGeneration generation = Generation(11);
        using var scene = new ArchRenderScene(generation);
        var registrations = new RenderProjectionDelta[count];
        var updates = new RenderProjectionDelta[count];
        for (int index = 0; index < count; index++)
        {
            RenderProjectionRecord record = Record(
                (ulong)index + 1,
                1,
                RenderProjectionClass.ActiveAnimatedStatic,
                x: index);
            registrations[index] = RenderProjectionDelta.Register(
                generation,
                (ulong)index + 1,
                record);
            updates[index] = RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateTransform,
                generation,
                (ulong)count + (ulong)index + 1,
                record with
                {
                    Transform = new RenderTransform(
                        Matrix4x4.CreateTranslation(index + 1, 0, 0)),
                });
        }

        scene.Apply(registrations);

        ulong nextSequence = (ulong)count + 1;
        ZeroAllocationProbe.AssertAllocatesNothing(
            "ArchRenderScene.Apply(transform updates)",
            () =>
            {
                for (int index = 0; index < count; index++)
                {
                    updates[index] = RenderProjectionDelta.Update(
                        RenderProjectionDeltaKind.UpdateTransform,
                        generation,
                        nextSequence + (ulong)index,
                        updates[index].Record);
                }

                nextSequence += (ulong)count;
                scene.Apply(updates);
            });

        // The batch really was applied, rather than rejected as stale.
        Assert.True(scene.OpenQuery().TryGet(updates[0].Record.Id, out var applied));
        Assert.Equal(
            Matrix4x4.CreateTranslation(1, 0, 0),
            applied.Transform.LocalToWorld);
    }

    [Fact]
    public void Digest_IsStableAcrossRegistrationOrderAndBufferReuse()
    {
        RenderSceneGeneration generation = Generation(12);
        using var first = new ArchRenderScene(generation);
        using var second = new ArchRenderScene(generation);
        RenderProjectionRecord a = Record(
            1,
            1,
            RenderProjectionClass.OutdoorStatic);
        RenderProjectionRecord b = Record(
            2,
            1,
            RenderProjectionClass.LiveDynamicRoot);

        first.Apply(
        [
            RenderProjectionDelta.Register(generation, 1, a),
            RenderProjectionDelta.Register(generation, 2, b),
        ]);
        second.Apply(
        [
            RenderProjectionDelta.Register(generation, 1, b),
            RenderProjectionDelta.Register(generation, 2, a),
        ]);
        var buffer = new RenderSceneDigestBuffer();

        RenderSceneDigest firstDigest = first.BuildDigest(buffer);
        int retainedCapacity = buffer.Capacity;
        RenderSceneDigest repeatedDigest = first.BuildDigest(buffer);
        RenderSceneDigest secondDigest =
            second.BuildDigest(new RenderSceneDigestBuffer());

        Assert.Equal(firstDigest, repeatedDigest);
        Assert.Equal(firstDigest, secondDigest);
        Assert.Equal(retainedCapacity, buffer.Capacity);
    }

    [Fact]
    public void Mutation_FromAnotherThread_IsRejected()
    {
        RenderSceneGeneration generation = Generation(15);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord record = Record(
            15,
            1,
            RenderProjectionClass.LiveDynamicRoot);

        Exception? captured = null;
        var mutationThread = new Thread(() =>
        {
            try
            {
                scene.Apply(
                    [RenderProjectionDelta.Register(generation, 1, record)]);
            }
            catch (Exception error)
            {
                captured = error;
            }
        });
        mutationThread.Start();
        mutationThread.Join();

        InvalidOperationException error =
            Assert.IsType<InvalidOperationException>(captured);
        Assert.Contains("owning update thread", error.Message);
        Assert.Equal(0, scene.Counts.Total);
    }

    [Fact]
    public void IncrementalIndices_PreserveOutdoorCellDynamicAndFeatureMembership()
    {
        const uint indoorCell = 0x12340100u;
        RenderSceneGeneration generation = Generation(16);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord outdoorStatic = Record(
            1,
            1,
            RenderProjectionClass.OutdoorStatic);
        RenderProjectionRecord indoorStatic = Record(
            2,
            1,
            RenderProjectionClass.IndoorCellStatic) with
        {
            Residency = new RenderSpatialResidency(
                Bucket(indoorCell),
                0x1234FFFFu,
                indoorCell),
            Source = new RenderSourceMetadata() with
            {
                ParentCellId = indoorCell,
            },
        };
        RenderProjectionRecord animatedStatic = Record(
            3,
            1,
            RenderProjectionClass.ActiveAnimatedStatic);
        RenderProjectionRecord outdoorDynamic = Record(
            4,
            1,
            RenderProjectionClass.LiveDynamicRoot);
        RenderProjectionRecord cellDynamic = Record(
            5,
            1,
            RenderProjectionClass.LiveDynamicRoot) with
        {
            Residency = new RenderSpatialResidency(
                Bucket(indoorCell),
                0x1234FFFFu,
                indoorCell),
            Source = new RenderSourceMetadata() with
            {
                ParentCellId = indoorCell,
            },
        };
        RenderProjectionRecord equipped = Record(
            6,
            1,
            RenderProjectionClass.EquippedChild) with
        {
            Flags = RenderProjectionFlags.Draw
                | RenderProjectionFlags.Selectable
                | RenderProjectionFlags.Translucent
                | RenderProjectionFlags.LightCandidate
                | RenderProjectionFlags.PortalStraddling,
        };
        RenderProjectionRecord[] records =
        [
            outdoorStatic,
            indoorStatic,
            animatedStatic,
            outdoorDynamic,
            cellDynamic,
            equipped,
        ];
        var deltas = new RenderProjectionDelta[records.Length];
        for (int i = 0; i < records.Length; i++)
        {
            deltas[i] = RenderProjectionDelta.Register(
                generation,
                (ulong)i + 1,
                records[i]);
        }

        scene.Apply(deltas);
        RenderSceneQuery query = scene.OpenQuery();

        Assert.Equal(
            new RenderSceneIndexCounts(
                OutdoorStatic: 2,
                IndoorCellStatic: 1,
                Dynamic: 3,
                OutdoorDynamic: 2,
                PortalStraddlingDynamic: 1,
                Translucent: 1,
                Selectable: 6,
                LightCandidate: 1,
                Dirty: 6),
            query.IndexCounts);
        Assert.True(scene.Memory.EstimatedIndexBytes > 0);
    }

    [Fact]
    public void IncrementalIndices_UpdateRebucketReplaceRemoveAndDirtyAcknowledge()
    {
        const uint indoorCell = 0x22220100u;
        RenderSceneGeneration generation = Generation(17);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord original = Record(
            17,
            1,
            RenderProjectionClass.LiveDynamicRoot);
        scene.Apply(
            [RenderProjectionDelta.Register(generation, 1, original)]);
        scene.ClearDirty();
        Assert.Equal(0, scene.OpenQuery().IndexCounts.Dirty);

        RenderProjectionRecord flagged = original with
        {
            Flags = original.Flags
                | RenderProjectionFlags.Translucent
                | RenderProjectionFlags.LightCandidate
                | RenderProjectionFlags.PortalStraddling,
            Source = original.Source with { ParentCellId = indoorCell },
        };
        RenderProjectionRecord rebucketed = flagged with
        {
            Residency = new RenderSpatialResidency(
                Bucket(indoorCell),
                0x2222FFFFu,
                indoorCell),
        };
        scene.Apply(
        [
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateFlags,
                generation,
                2,
                flagged),
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.Rebucket,
                generation,
                3,
                rebucketed),
        ]);

        RenderSceneQuery updated = scene.OpenQuery();
        Assert.Equal(0, updated.IndexCounts.OutdoorDynamic);
        Assert.Equal(1, updated.IndexCounts.PortalStraddlingDynamic);
        Assert.Equal(1, updated.IndexCounts.Translucent);
        Assert.Equal(1, updated.IndexCounts.LightCandidate);
        Assert.Equal(1, updated.IndexCounts.Dirty);

        RenderProjectionRecord replacement = rebucketed with
        {
            ProjectionClass = RenderProjectionClass.IndoorCellStatic,
            OwnerIncarnation = Incarnation(2),
        };
        scene.Apply(
            [RenderProjectionDelta.Register(generation, 4, replacement)]);
        RenderSceneQuery replaced = scene.OpenQuery();
        Assert.Equal(0, replaced.IndexCounts.Dynamic);
        Assert.Equal(1, replaced.IndexCounts.IndoorCellStatic);

        scene.Apply(
        [
            RenderProjectionDelta.Unregister(
                generation,
                5,
                replacement.Id,
                replacement.OwnerIncarnation),
        ]);
        Assert.Equal(default, scene.OpenQuery().IndexCounts);
        Assert.True(scene.Memory.EstimatedIndexBytes > 0);
    }

    [Fact]
    public void IndexRevision_AdvancesOnlyWhenOrderedMembershipCanChange()
    {
        RenderSceneGeneration generation = Generation(18);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord original = Record(
            18,
            1,
            RenderProjectionClass.OutdoorStatic);
        scene.Apply(
            [RenderProjectionDelta.Register(generation, 1, original)]);
        scene.ClearDirty();
        ulong registeredRevision = scene.OpenQuery().IndexRevision;

        Matrix4x4 movedTransform =
            Matrix4x4.CreateTranslation(4, 5, 6);
        RenderProjectionRecord moved = original with
        {
            Transform = new RenderTransform(movedTransform),
            PreviousTransform =
                new PreviousRenderTransform(
                    original.Transform.LocalToWorld),
            Bounds = new RenderWorldBounds(
                new Vector3(3, 4, 5),
                new Vector3(5, 6, 7)),
        };
        scene.Apply(
        [
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateTransform,
                generation,
                2,
                moved),
        ]);

        RenderSceneQuery movedQuery = scene.OpenQuery();
        Assert.Equal(registeredRevision, movedQuery.IndexRevision);
        Assert.Equal(1, movedQuery.IndexCounts.Dirty);

        RenderProjectionRecord reordered = moved with
        {
            SortKey = new RenderSortKey(moved.SortKey.Value + 1),
        };
        scene.Apply(
        [
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateTransform,
                generation,
                3,
                reordered),
        ]);

        Assert.True(
            scene.OpenQuery().IndexRevision > registeredRevision);
    }

    [Fact]
    public void DirectionalShadowTopologyRevision_IgnoresDynamicPoseButTracksEligibilityGeometryAppearanceAndCasterIdentity()
    {
        RenderSceneGeneration generation = Generation(19);
        using var scene = new ArchRenderScene(generation);
        Matrix4x4 firstPart = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        RenderProjectionRecord original = Record(
            19,
            1,
            RenderProjectionClass.LiveDynamicRoot) with
        {
            Source = new RenderSourceMetadata(
                LocalEntityId: 19,
                ServerGuid: 19,
                SourceId: 19,
                ParentCellId: 0,
                EffectCellId: 0,
                BuildingShellAnchorCellId: 0,
                TransformFingerprint: new RenderSceneHash128(1, 1),
                GeometryFingerprint: new RenderSceneHash128(2, 2),
                AppearanceFingerprint: new RenderSceneHash128(3, 3),
                DirectionalShadowTopologyFingerprint:
                    new RenderSceneHash128(4, 4)),
            EntityPayload = new RenderEntityPayload(
                [new MeshRef(0x01000001, firstPart)],
                PaletteOverride: null,
                IsBuildingShell: false),
        };
        scene.Apply([RenderProjectionDelta.Register(generation, 1, original)]);
        ulong registered =
            scene.OpenQuery().DirectionalShadowTopologyRevision;

        Matrix4x4 movedRoot = Matrix4x4.CreateTranslation(20f, 21f, 22f);
        Matrix4x4 movedPart = Matrix4x4.CreateTranslation(23f, 24f, 25f);
        RenderProjectionRecord poseOnly = original with
        {
            Transform = new RenderTransform(movedRoot),
            MeshSet = original.MeshSet with
            {
                Handle = Asset(999),
            },
            Source = original.Source with
            {
                TransformFingerprint = new RenderSceneHash128(10, 10),
                GeometryFingerprint = new RenderSceneHash128(20, 20),
            },
            EntityPayload = original.EntityPayload with
            {
                MeshRefs = [new MeshRef(0x01000001, movedPart)],
            },
        };
        scene.Apply(
        [
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateTransform,
                generation,
                2,
                poseOnly),
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateAppearance,
                generation,
                3,
                poseOnly),
        ]);
        Assert.Equal(
            registered,
            scene.OpenQuery().DirectionalShadowTopologyRevision);

        RenderProjectionRecord hidden = poseOnly with
        {
            Flags = poseOnly.Flags & ~RenderProjectionFlags.Draw,
        };
        scene.Apply(
        [
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateFlags,
                generation,
                4,
                hidden),
        ]);
        ulong eligibilityChanged =
            scene.OpenQuery().DirectionalShadowTopologyRevision;
        Assert.True(eligibilityChanged > registered);

        RenderProjectionRecord geometryChanged = hidden with
        {
            Source = hidden.Source with
            {
                DirectionalShadowTopologyFingerprint =
                    new RenderSceneHash128(5, 5),
            },
            EntityPayload = hidden.EntityPayload with
            {
                MeshRefs =
                [
                    new MeshRef(0x01000002, movedPart)
                    {
                        SurfaceOverrides = new Dictionary<uint, uint>
                        {
                            [0x08000001] = 0x05000001,
                        },
                    },
                ],
            },
        };
        scene.Apply(
        [
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateAppearance,
                generation,
                5,
                geometryChanged),
        ]);
        ulong geometryRevision =
            scene.OpenQuery().DirectionalShadowTopologyRevision;
        Assert.True(geometryRevision > eligibilityChanged);

        var palette = new PaletteOverride(
            0x04000001,
            [new PaletteOverride.SubPaletteRange(0x04000002, 1, 2)]);
        RenderProjectionRecord appearanceChanged = geometryChanged with
        {
            Material = geometryChanged.Material with { PaletteKey = 1234 },
            Source = geometryChanged.Source with
            {
                AppearanceFingerprint = new RenderSceneHash128(6, 6),
            },
            EntityPayload = geometryChanged.EntityPayload with
            {
                PaletteOverride = palette,
            },
        };
        scene.Apply(
        [
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateAppearance,
                generation,
                6,
                appearanceChanged),
        ]);
        ulong appearanceRevision =
            scene.OpenQuery().DirectionalShadowTopologyRevision;
        Assert.True(appearanceRevision > geometryRevision);

        RenderProjectionRecord identityChanged = appearanceChanged with
        {
            EntityPayload = appearanceChanged.EntityPayload with
            {
                CasterIdentity = RenderCasterIdentityKind.RemotePlayer,
            },
        };
        scene.Apply(
        [
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateAppearance,
                generation,
                7,
                identityChanged),
        ]);
        Assert.True(
            scene.OpenQuery().DirectionalShadowTopologyRevision
                > appearanceRevision);
    }

    [Fact]
    public void DirectionalShadowTopologyRevision_TracksRareStaticTransformChanges()
    {
        RenderSceneGeneration generation = Generation(20);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord original = Record(
            20,
            1,
            RenderProjectionClass.OutdoorStatic);
        scene.Apply([RenderProjectionDelta.Register(generation, 1, original)]);
        ulong registered =
            scene.OpenQuery().DirectionalShadowTopologyRevision;
        RenderProjectionRecord moved = original with
        {
            Transform = new RenderTransform(
                Matrix4x4.CreateTranslation(100f, 101f, 102f)),
        };

        scene.Apply(
        [
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateTransform,
                generation,
                2,
                moved),
        ]);

        Assert.True(
            scene.OpenQuery().DirectionalShadowTopologyRevision > registered);
    }

    [Fact]
    public void DirectionalShadowTransformJournal_TracksRootPartAndDynamicSync_IndependentlyOfDirtyDrain()
    {
        RenderSceneGeneration generation = Generation(21);
        using var scene = new ArchRenderScene(generation);
        Matrix4x4 firstPart = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        RenderProjectionRecord original = Record(
            21,
            1,
            RenderProjectionClass.LiveDynamicRoot) with
        {
            EntityPayload = new RenderEntityPayload(
                [new MeshRef(0x01000021, firstPart)],
                PaletteOverride: null,
                IsBuildingShell: false),
        };
        scene.Apply([RenderProjectionDelta.Register(generation, 1, original)]);
        RenderSceneQuery query = scene.OpenQuery();
        ulong initialRevision = query.DirectionalShadowTransformRevision;

        Matrix4x4 movedRoot = Matrix4x4.CreateTranslation(
            BitConverter.Int32BitsToSingle(0x41234567),
            4f,
            5f);
        RenderProjectionRecord moved = original with
        {
            Transform = new RenderTransform(movedRoot),
            PreviousTransform = new PreviousRenderTransform(
                original.Transform.LocalToWorld),
        };
        scene.Apply(
        [
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateTransform,
                generation,
                2,
                moved),
        ]);
        Matrix4x4 movedPart = Matrix4x4.CreateTranslation(
            6f,
            BitConverter.Int32BitsToSingle(0x40ABCDEF),
            8f);
        RenderProjectionRecord posed = moved with
        {
            EntityPayload = moved.EntityPayload with
            {
                MeshRefs = [new MeshRef(0x01000021, movedPart)],
            },
        };
        scene.Apply(
        [
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateAppearance,
                generation,
                3,
                posed),
        ]);
        scene.ClearDirty();
        var changes = new DirectionalShadowTransformSnapshot[3];
        DirectionalShadowTransformChanges firstChanges =
            query.CopyDirectionalShadowTransformChanges(
                initialRevision,
                changes);

        Assert.False(firstChanges.RequiresFullRefresh);
        Assert.Equal(2, firstChanges.Count);
        Assert.Equal(1, firstChanges.UpdateTransformCount);
        Assert.Equal(1, firstChanges.UpdateAppearanceCount);
        Assert.Equal(0, firstChanges.DynamicSynchronizationCount);
        Assert.Equal(2, firstChanges.LiveDynamicRootCount);
        Assert.Equal(original.Id, changes[0].Id);
        Assert.Equal(original.Id, changes[1].Id);

        var synchronized = new DynamicProjectionUpdate(
            original.Id,
            original.OwnerIncarnation,
            new RenderTransform(Matrix4x4.CreateTranslation(9f, 10f, 11f)),
            posed.Bounds);
        scene.SynchronizeDynamicSources(
            new DynamicProjectionSyncInput(generation, [synchronized]));
        DirectionalShadowTransformChanges syncChanges =
            query.CopyDirectionalShadowTransformChanges(
                firstChanges.LatestRevision,
                changes);
        Assert.False(syncChanges.RequiresFullRefresh);
        Assert.Equal(1, syncChanges.Count);
        Assert.Equal(1, syncChanges.DynamicSynchronizationCount);
        Assert.Equal(1, syncChanges.LiveDynamicRootCount);
        Assert.Equal(original.Id, changes[0].Id);

        RenderProjectionRecord flagsOnly = posed with
        {
            Flags = posed.Flags ^ RenderProjectionFlags.Selectable,
        };
        scene.Apply(
        [
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateFlags,
                generation,
                4,
                flagsOnly),
        ]);
        Assert.Equal(
            syncChanges.LatestRevision,
            query.DirectionalShadowTransformRevision);
    }

    [Fact]
    public void DirectionalShadowTransformJournal_OverflowAndGenerationResetFailSafe()
    {
        RenderSceneGeneration generation = Generation(22);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord original = Record(
            22,
            1,
            RenderProjectionClass.LiveDynamicRoot);
        scene.Apply([RenderProjectionDelta.Register(generation, 1, original)]);
        RenderSceneQuery oldQuery = scene.OpenQuery();
        ulong initialRevision = oldQuery.DirectionalShadowTransformRevision;
        for (int index = 0;
             index < DirectionalShadowTransformChangeJournal.Capacity + 1;
             index++)
        {
            var update = new DynamicProjectionUpdate(
                original.Id,
                original.OwnerIncarnation,
                new RenderTransform(Matrix4x4.CreateTranslation(index + 1, 0f, 0f)),
                original.Bounds);
            scene.SynchronizeDynamicSources(
                new DynamicProjectionSyncInput(generation, [update]));
        }

        var changes = new DirectionalShadowTransformSnapshot[
            DirectionalShadowTransformChangeJournal.Capacity];
        DirectionalShadowTransformChanges overflow =
            oldQuery.CopyDirectionalShadowTransformChanges(
                initialRevision,
                changes);
        Assert.True(overflow.RequiresFullRefresh);
        Assert.Equal(0, overflow.Count);

        RenderSceneGeneration replacement = Generation(23);
        scene.Clear(replacement);
        Assert.Throws<InvalidOperationException>(
            () => _ = oldQuery.DirectionalShadowTransformRevision);
        Assert.Equal(1ul, scene.OpenQuery().DirectionalShadowTransformRevision);
    }

    [Fact]
    public void DirectionalShadowTransformJournal_IgnoresIdenticalPoseAndBoundsOnlySynchronization()
    {
        RenderSceneGeneration generation = Generation(24);
        using var scene = new ArchRenderScene(generation);
        var meshes = new List<MeshRef>
        {
            new(0x01000024, Matrix4x4.CreateTranslation(1f, 2f, 3f)),
        };
        RenderProjectionRecord original = Record(
            24,
            1,
            RenderProjectionClass.LiveDynamicRoot) with
        {
            EntityPayload = new RenderEntityPayload(
                meshes,
                PaletteOverride: null,
                IsBuildingShell: false),
        };
        scene.Apply([RenderProjectionDelta.Register(generation, 1, original)]);
        RenderSceneQuery query = scene.OpenQuery();
        ulong initial = query.DirectionalShadowTransformRevision;

        scene.Apply(
        [
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateTransform,
                generation,
                2,
                original),
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateAppearance,
                generation,
                3,
                original),
        ]);
        var boundsOnly = new DynamicProjectionUpdate(
            original.Id,
            original.OwnerIncarnation,
            original.Transform,
            new RenderWorldBounds(Vector3.One, new Vector3(2f)));
        scene.SynchronizeDynamicSources(
            new DynamicProjectionSyncInput(generation, [boundsOnly]));
        Assert.Equal(initial, query.DirectionalShadowTransformRevision);

        Matrix4x4 changedPart = Matrix4x4.CreateTranslation(4f, 5f, 6f);
        meshes[0] = new MeshRef(0x01000024, changedPart);
        scene.Apply(
        [
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateAppearance,
                generation,
                4,
                original),
        ]);
        var changes = new DirectionalShadowTransformSnapshot[1];
        DirectionalShadowTransformChanges changed =
            query.CopyDirectionalShadowTransformChanges(initial, changes);
        Assert.Equal(1, changed.Count);
        Assert.Equal(0, changed.UpdateTransformCount);
        Assert.Equal(1, changed.UpdateAppearanceCount);
        Assert.Equal(0, changed.DynamicSynchronizationCount);
        Assert.Equal(1, changed.LiveDynamicRootCount);
        Assert.Equal(original.Id, changes[0].Id);
        Assert.Equal(
            BitConverter.SingleToInt32Bits(changedPart.M41),
            BitConverter.SingleToInt32Bits(
                changes[0].EntityPayload.MeshRefs[0].PartTransform.M41));

        scene.Apply(
        [
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateAppearance,
                generation,
                5,
                original),
        ]);
        Assert.Equal(changed.LatestRevision, query.DirectionalShadowTransformRevision);
    }


    private static RenderProjectionRecord WithLocalEntityId(
        RenderProjectionRecord record,
        uint localEntityId) =>
        record with
        {
            Source = new RenderSourceMetadata(
                LocalEntityId: localEntityId,
                ServerGuid: 0,
                SourceId: 1,
                ParentCellId: record.Source.ParentCellId,
                EffectCellId: 0,
                BuildingShellAnchorCellId: 0,
                TransformFingerprint: new RenderSceneHash128(1, 1),
                GeometryFingerprint: new RenderSceneHash128(2, 2),
                AppearanceFingerprint: new RenderSceneHash128(3, 3)),
        };

    [Fact]
    public void TryGetByLocalEntityId_IgnoresEnvCellShellRecords()
    {
        const uint aliasedId = 0x8A020100u;
        RenderSceneGeneration generation = Generation(31);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord shell = WithLocalEntityId(
            Record(310, 1, RenderProjectionClass.IndoorCellStatic), aliasedId);
        RenderProjectionRecord scenery = WithLocalEntityId(
            Record(311, 1, RenderProjectionClass.OutdoorStatic), aliasedId);

        scene.Apply(
        [
            RenderProjectionDelta.Register(generation, 1, shell),
            RenderProjectionDelta.Register(generation, 2, scenery),
        ]);
        RenderSceneQuery query = scene.OpenQuery();

        Assert.True(query.TryGetByLocalEntityId(aliasedId, out RenderProjectionRecord found));
        Assert.Equal(scenery.Id, found.Id);

        // Unregistering the shell never disturbs the entity's mapping.
        scene.Apply(
        [
            RenderProjectionDelta.Unregister(
                generation,
                3,
                shell.Id,
                shell.OwnerIncarnation),
        ]);
        query = scene.OpenQuery();
        Assert.True(query.TryGetByLocalEntityId(aliasedId, out found));
        Assert.Equal(scenery.Id, found.Id);
    }

    [Fact]
    public void Update_ThatRebindsTheLocalEntityId_MovesTheIndex()
    {
        RenderSceneGeneration generation = Generation(32);
        using var scene = new ArchRenderScene(generation);
        RenderProjectionRecord original = WithLocalEntityId(
            Record(320, 1, RenderProjectionClass.LiveDynamicRoot), 19);
        scene.Apply([RenderProjectionDelta.Register(generation, 1, original)]);
        Assert.True(scene.OpenQuery().TryGetByLocalEntityId(19, out _));

        RenderProjectionRecord rebound = WithLocalEntityId(original, 20);
        scene.Apply(
        [
            RenderProjectionDelta.Update(
                RenderProjectionDeltaKind.UpdateFlags,
                generation,
                2,
                rebound),
        ]);
        RenderSceneQuery query = scene.OpenQuery();

        Assert.False(query.TryGetByLocalEntityId(19, out _));
        Assert.True(query.TryGetByLocalEntityId(20, out RenderProjectionRecord found));
        Assert.Equal(original.Id, found.Id);
    }

    private static RenderProjectionRecord Record(
        ulong id,
        ulong incarnation,
        RenderProjectionClass projectionClass,
        float x = 0,
        ulong bucket = 1)
    {
        Matrix4x4 transform = Matrix4x4.CreateTranslation(x, 2, 3);
        return new RenderProjectionRecord(
            Projection(id),
            projectionClass,
            Incarnation(incarnation),
            new RenderTransform(transform),
            new PreviousRenderTransform(transform),
            new RenderMeshSet(Asset(id + 100), 2, 1),
            new RenderMaterialVariant(id + 200, id + 300, 1),
            new RenderSpatialResidency(
                Bucket(bucket),
                (uint)(bucket << 16),
                (uint)((bucket << 16) | 0x101)),
            new RenderWorldBounds(
                new Vector3(x - 1, 1, 2),
                new Vector3(x + 1, 3, 4)),
            RenderProjectionFlags.Draw | RenderProjectionFlags.Selectable,
            new RenderDegradeState(0, 1),
            new RenderSortKey(id + 400),
            RenderDirtyMask.All);
    }

    private static RenderProjectionId Projection(ulong value) =>
        RenderProjectionId.FromRaw(value);

    private static RenderOwnerIncarnation Incarnation(ulong value) =>
        RenderOwnerIncarnation.FromRaw(value);

    private static RenderSceneGeneration Generation(ulong value) =>
        RenderSceneGeneration.FromRaw(value);

    private static RenderSpatialBucket Bucket(ulong value) =>
        RenderSpatialBucket.FromRaw(value);

    private static RenderAssetHandle Asset(ulong value) =>
        RenderAssetHandle.FromRaw(value);
}
