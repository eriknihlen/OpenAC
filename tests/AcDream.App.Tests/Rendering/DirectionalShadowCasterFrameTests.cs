using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Vfx;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering;

public sealed class DirectionalShadowCasterFrameTests
{
    [Fact]
    public void Build_IncludesEveryHeadlineProjectionClassAndExcludesTrueTransparency()
    {
        RenderProjectionRecord[] statics =
        [
            Record(1, RenderProjectionClass.OutdoorStatic),
            Record(2, RenderProjectionClass.OutdoorStatic, building: true),
            Record(3, RenderProjectionClass.ActiveAnimatedStatic),
            Record(4, RenderProjectionClass.OutdoorStatic,
                extraFlags: RenderProjectionFlags.Translucent),
            Record(5, RenderProjectionClass.OutdoorStatic, parentCell: 0x12340101),
        ];
        RenderProjectionRecord[] dynamics =
        [
            Record(6, RenderProjectionClass.LiveDynamicRoot),
            Record(7, RenderProjectionClass.EquippedChild),
        ];
        var source = new QuerySource(statics, dynamics);
        var frame = new DirectionalShadowCasterFrame();

        frame.Build(new RenderSceneQuery(source, Generation));

        DirectionalShadowCasterKind[] kinds = frame.Casters
            .ToArray()
            .Select(static value => value.Kind)
            .ToArray();
        Assert.Contains(DirectionalShadowCasterKind.OutdoorStatic, kinds);
        Assert.Contains(DirectionalShadowCasterKind.Building, kinds);
        Assert.Contains(DirectionalShadowCasterKind.AnimatedStatic, kinds);
        Assert.Contains(DirectionalShadowCasterKind.LiveDynamic, kinds);
        Assert.Contains(DirectionalShadowCasterKind.EquippedChild, kinds);
        Assert.Equal(5, frame.Casters.Length);
        Assert.Equal(1, frame.Stats.RejectedTransparent);
        Assert.Equal(1, frame.Stats.RejectedIndoor);
        Assert.True(frame.Casters[2].UsesCurrentAnimatedTransforms);
    }

    [Fact]
    public void Build_ReportsEveryAuthoritativeCasterClassAndPreservesCountsOnStableFrame()
    {
        RenderProjectionRecord[] statics =
        [
            Record(101, RenderProjectionClass.OutdoorStatic),
            Record(102, RenderProjectionClass.OutdoorStatic, building: true),
            Record(103, RenderProjectionClass.ActiveAnimatedStatic),
        ];
        RenderProjectionRecord[] dynamics =
        [
            Record(104, RenderProjectionClass.LiveDynamicRoot,
                casterIdentity: RenderCasterIdentityKind.LocalPlayer),
            Record(105, RenderProjectionClass.LiveDynamicRoot,
                casterIdentity: RenderCasterIdentityKind.RemotePlayer),
            Record(106, RenderProjectionClass.LiveDynamicRoot,
                casterIdentity: RenderCasterIdentityKind.NonPlayerCreature),
            Record(107, RenderProjectionClass.LiveDynamicRoot,
                casterIdentity: RenderCasterIdentityKind.OtherLiveDynamic),
            Record(108, RenderProjectionClass.EquippedChild,
                casterIdentity: RenderCasterIdentityKind.EquippedChild),
        ];
        var source = new QuerySource(statics, dynamics);
        var frame = new DirectionalShadowCasterFrame();

        frame.Build(new RenderSceneQuery(source, Generation));
        DirectionalShadowCasterClassDiagnostics first = frame.Stats.CasterClasses;

        Assert.Equal(0, first.TerrainCommands);
        Assert.Equal(1, first.OutdoorStatics);
        Assert.Equal(1, first.Buildings);
        Assert.Equal(1, first.AnimatedStatics);
        Assert.Equal(1, first.LocalPlayers);
        Assert.Equal(1, first.RemotePlayers);
        Assert.Equal(1, first.NonPlayerCreatures);
        Assert.Equal(1, first.OtherLiveDynamics);
        Assert.Equal(1, first.EquippedChildren);

        frame.Build(new RenderSceneQuery(source, Generation));

        Assert.Equal(first, frame.Stats.CasterClasses);
        Assert.False(frame.Stats.TopologyRebuilt);
        Assert.Equal(0, frame.Stats.Classifications);
    }

    [Fact]
    public void Build_ReadsOnlyTwoResidentIndicesOnce_NoPViewCellOrCascadeRecull()
    {
        var source = new QuerySource(
            [Record(20, RenderProjectionClass.OutdoorStatic)],
            [Record(21, RenderProjectionClass.LiveDynamicRoot)]);
        var frame = new DirectionalShadowCasterFrame();

        frame.Build(new RenderSceneQuery(source, Generation));

        Assert.Equal(1, source.IndexCountReads);
        Assert.Equal(2, source.IndexCopies);
        Assert.Equal(
            [RenderSceneIndex.OutdoorStatic, RenderSceneIndex.OutdoorDynamic],
            source.CopiedIndices);
        Assert.Equal(2, frame.Stats.IndexCopies);
    }

    [Fact]
    public void PriorLandscapeSelection_UsesExactCellArrayAndBuildingEffectCell()
    {
        const uint visible = 0x12340002u;
        const uint outdoorAboveTerrainRange = 0x12340041u;
        RenderProjectionRecord[] statics =
        [
            Record(201, RenderProjectionClass.OutdoorStatic),
            Record(204, RenderProjectionClass.OutdoorStatic, parentCell: visible),
            Record(205, RenderProjectionClass.OutdoorStatic, building: true,
                effectCell: visible, buildingAnchor: 0x12340100u),
            Record(206, RenderProjectionClass.OutdoorStatic, building: true,
                effectCell: 0u, buildingAnchor: visible),
        ];
        RenderProjectionRecord[] dynamics =
        [
            Record(202, RenderProjectionClass.LiveDynamicRoot),
            Record(203, RenderProjectionClass.EquippedChild),
        ];
        var source = new QuerySource(statics, dynamics);
        var frame = new DirectionalShadowCasterFrame();
        var membership = new RecordingMembership(
            new Dictionary<uint, IReadOnlyList<uint>>
            {
                [201] = [0x12340001u, visible],
                [202] = [visible],
                [203] = [outdoorAboveTerrainRange],
            });
        var visibleCells = new HashSet<uint>
        {
            visible,
            outdoorAboveTerrainRange,
        };
        var visibility = new RetailLandscapeVisibilityFrame(
            visibleCells,
            HasCompletedWorldView: true);

        frame.Build(new RenderSceneQuery(source, Generation));
        ulong topologySequence = frame.BuildSequence;
        frame.Select(in visibility, membership);

        ulong[] selected = frame.Casters.ToArray()
            .Zip(frame.SelectedCasters.ToArray())
            .Where(static pair => pair.Second)
            .Select(static pair => pair.First.Projection.Id.RawValue)
            .ToArray();
        Assert.Equal([201ul, 202ul, 203ul, 205ul], selected);
        Assert.Equal(4, frame.Stats.ActiveSelected);
        Assert.Equal(topologySequence, frame.BuildSequence);
        Assert.Equal(0x12340100u,
            frame.Casters.ToArray().Single(c => c.Projection.Id.RawValue == 205)
                .Projection.Source.BuildingShellAnchorCellId);
        Assert.DoesNotContain(204ul, selected);
        Assert.DoesNotContain(206ul, selected); // Anchor is not placement.

        var noCompletedView = new RetailLandscapeVisibilityFrame(
            visibleCells,
            HasCompletedWorldView: false);
        frame.Build(new RenderSceneQuery(source, Generation));
        frame.Select(in noCompletedView, membership);
        Assert.Equal(topologySequence, frame.BuildSequence);
        Assert.Equal(0, frame.Stats.Classifications);
        Assert.Equal(0, frame.Stats.ActiveSelected);
        Assert.DoesNotContain(true, frame.SelectedCasters.ToArray());
    }

    [Fact]
    public void WarmPriorLandscapeSelection_AllocatesZeroWithStreamingBoundedScratch()
    {
        const uint visible = 0x12340002u;
        var statics = new RenderProjectionRecord[256];
        var cells = new Dictionary<uint, IReadOnlyList<uint>>(statics.Length);
        for (int index = 0; index < statics.Length; index++)
        {
            uint entityId = checked((uint)(30_000 + index));
            statics[index] = Record(entityId, RenderProjectionClass.OutdoorStatic);
            cells.Add(entityId, index % 2 == 0 ? [visible] : [0x12340003u]);
        }
        var source = new QuerySource(statics, []);
        var frame = new DirectionalShadowCasterFrame();
        RenderSceneQuery query = new(source, Generation);
        frame.Build(in query);
        frame.Build(in query);
        var membership = new RecordingMembership(cells);
        var visibility = new RetailLandscapeVisibilityFrame(
            new HashSet<uint> { visible },
            HasCompletedWorldView: true);
        frame.Select(in visibility, membership);
        long retainedBytes = frame.RetainedScratchBytes;

        ZeroAllocationProbe.AssertAllocatesNothing(
            "DirectionalShadowCasterFrame.Select prior completed landscape",
            () => frame.Select(in visibility, membership),
            batchSize: 256);

        Assert.Equal(128, frame.Stats.ActiveSelected);
        Assert.Equal(retainedBytes, frame.RetainedScratchBytes);
        Assert.Equal(1ul, frame.BuildSequence);
        Assert.Equal(0, frame.Stats.Classifications);
    }

    [Fact]
    public void UnchangedSecondFrame_ReusesSortedTopologyWithoutIndexCopiesOrClassification()
    {
        var source = new QuerySource(
            [
                Record(30, RenderProjectionClass.OutdoorStatic, sortKey: 30),
                Record(31, RenderProjectionClass.ActiveAnimatedStatic, sortKey: 10),
            ],
            [Record(32, RenderProjectionClass.LiveDynamicRoot, sortKey: 20)]);
        var frame = new DirectionalShadowCasterFrame();
        RenderSceneQuery query = new(source, Generation);

        frame.Build(in query);
        long retained = frame.RetainedScratchBytes;
        ulong[] firstOrder = frame.Casters
            .ToArray()
            .Select(static value => value.Projection.Id.RawValue)
            .ToArray();
        int indexReads = source.IndexCountReads;
        int indexCopies = source.IndexCopies;
        frame.Build(in query);

        Assert.Equal([31ul, 32ul, 30ul], firstOrder);
        Assert.Equal([0, 1], frame.RefreshCasterSlots.ToArray());
        Assert.Equal(retained, frame.RetainedScratchBytes);
        Assert.Equal(1ul, frame.BuildSequence);
        Assert.Equal(indexReads, source.IndexCountReads);
        Assert.Equal(indexCopies, source.IndexCopies);
        Assert.False(frame.Stats.TopologyRebuilt);
        Assert.Equal(0, frame.Stats.IndexCopies);
        Assert.Equal(0, frame.Stats.Classifications);
        Assert.Equal(0, frame.Stats.DynamicTransformRefreshes);
        Assert.Empty(frame.ChangedCasterPoses.ToArray());
    }

    [Fact]
    public void TopologyRebuild_ReplacesRefreshSlotsAndRetainedAccountingIncludesThem()
    {
        RenderProjectionRecord[] statics =
        [
            Record(33, RenderProjectionClass.OutdoorStatic),
            Record(34, RenderProjectionClass.OutdoorStatic),
            Record(35, RenderProjectionClass.OutdoorStatic),
            Record(36, RenderProjectionClass.OutdoorStatic),
        ];
        var source = new QuerySource(statics, []);
        var frame = new DirectionalShadowCasterFrame();
        RenderSceneQuery query = new(source, Generation);

        frame.Build(in query);
        long staticRetainedBytes = frame.RetainedScratchBytes;

        Assert.Empty(frame.RefreshCasterSlots.ToArray());

        source.ReplaceStatics(
            [
                Record(33, RenderProjectionClass.ActiveAnimatedStatic),
                Record(34, RenderProjectionClass.ActiveAnimatedStatic),
                Record(35, RenderProjectionClass.ActiveAnimatedStatic),
                Record(36, RenderProjectionClass.ActiveAnimatedStatic),
            ],
            topologyChanged: true);
        frame.Build(in query);
        long animatedRetainedBytes = frame.RetainedScratchBytes;

        Assert.True(animatedRetainedBytes > staticRetainedBytes);
        Assert.Equal([0, 1, 2, 3], frame.RefreshCasterSlots.ToArray());

        source.ReplaceStatics(statics, topologyChanged: true);
        frame.Build(in query);
        int projectionReads = source.ProjectionReads;
        frame.Build(in query);

        Assert.Empty(frame.RefreshCasterSlots.ToArray());
        Assert.Equal(animatedRetainedBytes, frame.RetainedScratchBytes);
        Assert.Equal(projectionReads, source.ProjectionReads);
        Assert.Equal(0, frame.Stats.DynamicTransformRefreshes);
        Assert.False(frame.Stats.TopologyRebuilt);
    }

    [Fact]
    public void StableTopology_EmitsExactDynamicPoseWithoutOverwritingTopologyCaster()
    {
        RenderProjectionRecord original =
            Record(40, RenderProjectionClass.LiveDynamicRoot);
        var source = new QuerySource([], [original]);
        var frame = new DirectionalShadowCasterFrame();
        RenderSceneQuery query = new(source, Generation);
        frame.Build(in query);

        float rootX = BitConverter.Int32BitsToSingle(0x41234567);
        float partY = BitConverter.Int32BitsToSingle(0x40ABCDEF);
        Matrix4x4 root = Matrix4x4.CreateTranslation(rootX, 2f, 3f);
        Matrix4x4 part = Matrix4x4.CreateTranslation(4f, partY, 6f);
        RenderProjectionRecord moved = original with
        {
            Transform = new RenderTransform(root),
            EntityPayload = original.EntityPayload with
            {
                MeshRefs = [new MeshRef(40, part)],
            },
        };
        source.ReplaceDynamics([moved], topologyChanged: false);

        frame.Build(in query);

        RenderProjectionRecord retained = frame.Casters[0].Projection;
        Assert.Equal(original.Transform, retained.Transform);
        Assert.Same(
            original.EntityPayload.MeshRefs,
            retained.EntityPayload.MeshRefs);
        DirectionalShadowChangedPose changed =
            Assert.Single(frame.ChangedCasterPoses.ToArray());
        Assert.Equal(0, changed.CasterIndex);
        Assert.Equal(
            BitConverter.SingleToInt32Bits(rootX),
            BitConverter.SingleToInt32Bits(
                changed.Snapshot.Transform.LocalToWorld.M41));
        Assert.Equal(
            BitConverter.SingleToInt32Bits(partY),
            BitConverter.SingleToInt32Bits(
                changed.Snapshot.EntityPayload.MeshRefs[0].PartTransform.M42));
        Assert.Equal(1ul, frame.BuildSequence);
        Assert.Equal(0, source.ProjectionReads);
    }

    [Fact]
    public void StableTopology_RefreshesOnlyChangedCasterSlots()
    {
        RenderProjectionRecord first =
            Record(41, RenderProjectionClass.LiveDynamicRoot);
        RenderProjectionRecord second =
            Record(42, RenderProjectionClass.EquippedChild);
        var source = new QuerySource([], [first, second]);
        var frame = new DirectionalShadowCasterFrame();
        RenderSceneQuery query = new(source, Generation);
        frame.Build(in query);
        RenderProjectionRecord moved = second with
        {
            Transform = new RenderTransform(
                Matrix4x4.CreateTranslation(420f, 2f, 3f)),
        };

        source.ReplaceDynamics([first, moved], topologyChanged: false);
        frame.Build(in query);

        Assert.Equal(0, source.ProjectionReads);
        Assert.Equal(
            [1],
            frame.ChangedCasterPoses.ToArray()
                .Select(static changed => changed.CasterIndex));
        Assert.Equal(first.Transform, frame.Casters[0].Projection.Transform);
        Assert.Equal(second.Transform, frame.Casters[1].Projection.Transform);
        Assert.Equal(
            moved.Transform,
            frame.ChangedCasterPoses[0].Snapshot.Transform);
    }

    [Fact]
    public void RepeatedSameId_ConsumesLatestJournalRecordWithoutSceneRead()
    {
        RenderProjectionRecord original =
            Record(46, RenderProjectionClass.LiveDynamicRoot);
        var source = new QuerySource([], [original]);
        var frame = new DirectionalShadowCasterFrame();
        RenderSceneQuery query = new(source, Generation);
        frame.Build(in query);
        RenderProjectionRecord intermediate = original with
        {
            Transform = new RenderTransform(
                Matrix4x4.CreateTranslation(100f, 101f, 102f)),
            EntityPayload = original.EntityPayload with
            {
                MeshRefs =
                [
                    new MeshRef(
                        46,
                        Matrix4x4.CreateTranslation(103f, 104f, 105f)),
                ],
            },
        };
        float rootX = BitConverter.Int32BitsToSingle(0x41234567);
        float partY = BitConverter.Int32BitsToSingle(0x40ABCDEF);
        RenderProjectionRecord latest = intermediate with
        {
            Transform = new RenderTransform(
                Matrix4x4.CreateTranslation(rootX, 201f, 202f)),
            EntityPayload = intermediate.EntityPayload with
            {
                MeshRefs =
                [
                    new MeshRef(
                        46,
                        Matrix4x4.CreateTranslation(203f, partY, 205f)),
                ],
            },
        };
        source.PublishTransformRecord(in intermediate);
        source.PublishTransformRecord(in latest);

        frame.Build(in query);

        Assert.Equal(2, frame.Stats.CopiedTransformChanges);
        Assert.Equal(1, frame.Stats.DedupedChangedCasterSlots);
        Assert.Equal(0, source.ProjectionReads);
        Assert.Equal(0, source.BatchedProjectionCopies);
        Assert.Equal(original.Transform, frame.Casters[0].Projection.Transform);
        DirectionalShadowTransformSnapshot current =
            frame.ChangedCasterPoses[0].Snapshot;
        Assert.Equal(
            BitConverter.SingleToInt32Bits(rootX),
            BitConverter.SingleToInt32Bits(current.Transform.LocalToWorld.M41));
        Assert.Equal(
            BitConverter.SingleToInt32Bits(partY),
            BitConverter.SingleToInt32Bits(
                current.EntityPayload.MeshRefs[0].PartTransform.M42));
    }

    [Fact]
    public void TransformJournalOverflow_FallsBackToExactFullDynamicRefresh()
    {
        RenderProjectionRecord first =
            Record(43, RenderProjectionClass.LiveDynamicRoot);
        RenderProjectionRecord second =
            Record(44, RenderProjectionClass.EquippedChild);
        var source = new QuerySource([], [first, second]);
        var frame = new DirectionalShadowCasterFrame();
        RenderSceneQuery query = new(source, Generation);
        frame.Build(in query);
        source.PublishTransformChanges(
            first.Id,
            DirectionalShadowTransformChangeJournal.Capacity + 1);

        frame.Build(in query);

        Assert.Equal(0, source.ProjectionReads);
        Assert.Equal(1, source.BatchedProjectionCopies);
        Assert.Equal(
            [0, 1],
            frame.ChangedCasterPoses.ToArray()
                .Select(static changed => changed.CasterIndex));
        Assert.Equal(2, frame.Stats.DynamicTransformRefreshes);
    }

    [Fact]
    public void DenseChangedSet_UsesExactBulkRefreshInsteadOfSparseDictionaryWalk()
    {
        var dynamics = new RenderProjectionRecord[100];
        for (int index = 0; index < dynamics.Length; index++)
        {
            dynamics[index] = Record(
                checked((ulong)(4_500 + index)),
                RenderProjectionClass.ActiveAnimatedStatic);
        }
        var source = new QuerySource(dynamics, []);
        var frame = new DirectionalShadowCasterFrame();
        RenderSceneQuery query = new(source, Generation);
        frame.Build(in query);
        for (int index = 0; index < 75; index++)
            source.PublishTransformChanges(dynamics[index].Id, 1);

        frame.Build(in query);

        Assert.True(frame.Stats.DensityBulkRefresh);
        Assert.Equal(75, frame.Stats.CopiedTransformChanges);
        Assert.Equal(75, frame.Stats.DynamicTransformRefreshes);
        Assert.Equal(0, frame.Stats.BatchedProjectionCopyCalls);
        Assert.Equal(0, source.BatchedProjectionCopies);
        Assert.Equal(0, source.ProjectionReads);
    }

    [Fact]
    public void RemovalAndRevisit_RebuildTopologyAndDiscardPriorTransformCursor()
    {
        RenderProjectionRecord original =
            Record(45, RenderProjectionClass.LiveDynamicRoot);
        var source = new QuerySource([], [original]);
        var frame = new DirectionalShadowCasterFrame();
        RenderSceneQuery query = new(source, Generation);
        frame.Build(in query);
        source.ReplaceDynamics([], topologyChanged: true);

        frame.Build(in query);

        Assert.Empty(frame.Casters.ToArray());
        Assert.Equal(2ul, frame.BuildSequence);
        RenderProjectionRecord revisited = original with
        {
            Transform = new RenderTransform(
                Matrix4x4.CreateTranslation(450f, 2f, 3f)),
        };
        source.ReplaceDynamics([revisited], topologyChanged: true);
        frame.Build(in query);
        int projectionReads = source.ProjectionReads;
        frame.Build(in query);

        Assert.Equal(3ul, frame.BuildSequence);
        Assert.Equal(revisited.Transform, frame.Casters[0].Projection.Transform);
        Assert.Equal(projectionReads, source.ProjectionReads);
        Assert.Empty(frame.ChangedCasterPoses.ToArray());
    }

    [Fact]
    public void MembershipOrAppearanceRevision_RebuildsTopology()
    {
        RenderProjectionRecord original =
            Record(50, RenderProjectionClass.OutdoorStatic);
        var source = new QuerySource([original], []);
        var frame = new DirectionalShadowCasterFrame();
        RenderSceneQuery query = new(source, Generation);
        frame.Build(in query);

        RenderProjectionRecord changed = original with
        {
            EntityPayload = original.EntityPayload with
            {
                MeshRefs =
                [
                    new MeshRef(51, original.EntityPayload.MeshRefs[0].PartTransform)
                    {
                        SurfaceOverrides = new Dictionary<uint, uint>
                        {
                            [0x08000001] = 0x05000001,
                        },
                    },
                ],
            },
        };
        source.ReplaceStatics([changed], topologyChanged: true);

        frame.Build(in query);

        Assert.Equal(2ul, frame.BuildSequence);
        Assert.True(frame.Stats.TopologyRebuilt);
        Assert.Equal(2, frame.Stats.IndexCopies);
        Assert.Equal(1, frame.Stats.Classifications);
        Assert.Equal((uint)51, frame.Casters[0].Projection.EntityPayload.MeshRefs[0].GfxObjId);
    }

    [Fact]
    public void WarmStableFrame_AllocatesZero()
    {
        var statics = new RenderProjectionRecord[9_500];
        for (int index = 0; index < statics.Length; index++)
        {
            statics[index] = Record(
                checked((ulong)(index + 60)),
                RenderProjectionClass.OutdoorStatic);
        }
        var source = new QuerySource(
            statics,
            [Record(10_000, RenderProjectionClass.LiveDynamicRoot)]);
        var frame = new DirectionalShadowCasterFrame();
        RenderSceneQuery query = new(source, Generation);
        frame.Build(in query);
        frame.Build(in query);
        int copies = source.IndexCopies;

        ZeroAllocationProbe.AssertAllocatesNothing(
            "DirectionalShadowCasterFrame.Build stable frame",
            () => frame.Build(in query),
            batchSize: 256);
        Assert.Equal(1ul, frame.BuildSequence);
        Assert.Equal(copies, source.IndexCopies);
        Assert.Equal(0, frame.Stats.Classifications);
        Assert.Equal(0, frame.Stats.DynamicTransformRefreshes);
        Assert.False(frame.Stats.TopologyRebuilt);
    }

    [Fact]
    public void WarmDenseChangedFrames_AllocateZeroAndReadNoSceneRecords()
    {
        var dynamics = new RenderProjectionRecord[100];
        for (int index = 0; index < dynamics.Length; index++)
        {
            dynamics[index] = Record(
                checked((ulong)(20_000 + index)),
                RenderProjectionClass.ActiveAnimatedStatic);
        }
        var source = new QuerySource(dynamics, []);
        var frame = new DirectionalShadowCasterFrame();
        RenderSceneQuery query = new(source, Generation);
        frame.Build(in query);
        source.EnsureTransformChangeCapacity(10_000);
        for (int index = 0; index < 75; index++)
            source.PublishTransformChanges(dynamics[index].Id, 1);
        frame.Build(in query);
        for (int iteration = 0; iteration < 64; iteration++)
        {
            for (int index = 0; index < 75; index++)
                source.PublishTransformChanges(dynamics[index].Id, 1);
            frame.Build(in query);
        }

        void RefreshDenseFrame()
        {
            for (int index = 0; index < 75; index++)
                source.PublishTransformChanges(dynamics[index].Id, 1);
            frame.Build(in query);
        }
        ZeroAllocationProbe.AssertAllocatesNothing(
            "DirectionalShadowCasterFrame.Build dense changed frame",
            RefreshDenseFrame,
            batchSize: 64);
        Assert.True(frame.Stats.DensityBulkRefresh);
        Assert.Equal(75, frame.Stats.DynamicTransformRefreshes);
        Assert.Equal(0, source.ProjectionReads);
        Assert.Equal(0, source.BatchedProjectionCopies);
    }

    [Fact]
    public void ProductionTopologyFingerprint_ExcludesPoseButIncludesGeometryAndSurfaceOverrides()
    {
        var entity = new WorldEntity
        {
            Id = 1,
            SourceGfxObjOrSetupId = 0x02000001,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs =
            [
                new MeshRef(
                    0x01000001,
                    Matrix4x4.CreateTranslation(1f, 2f, 3f)),
            ],
        };
        RenderSceneHash128 original = CurrentRenderSceneOracle
            .CreateDirectionalShadowTopologyFingerprint(entity);
        entity.MeshRefs =
        [
            new MeshRef(
                0x01000001,
                Matrix4x4.CreateTranslation(10f, 20f, 30f)),
        ];
        RenderSceneHash128 poseOnly = CurrentRenderSceneOracle
            .CreateDirectionalShadowTopologyFingerprint(entity);
        entity.MeshRefs =
        [
            new MeshRef(
                0x01000002,
                Matrix4x4.CreateTranslation(10f, 20f, 30f))
            {
                SurfaceOverrides = new Dictionary<uint, uint>
                {
                    [0x08000001] = 0x05000001,
                },
            },
        ];
        RenderSceneHash128 changed = CurrentRenderSceneOracle
            .CreateDirectionalShadowTopologyFingerprint(entity);

        Assert.Equal(original, poseOnly);
        Assert.NotEqual(original, changed);
    }

    private static readonly RenderSceneGeneration Generation =
        RenderSceneGeneration.FromRaw(9);

    private static RenderProjectionRecord Record(
        ulong id,
        RenderProjectionClass projectionClass,
        bool building = false,
        RenderProjectionFlags extraFlags = RenderProjectionFlags.None,
        uint parentCell = 0,
        uint effectCell = 0,
        uint buildingAnchor = 0,
        ulong? sortKey = null,
        RenderCasterIdentityKind casterIdentity =
            RenderCasterIdentityKind.Unclassified)
    {
        Matrix4x4 transform = Matrix4x4.CreateTranslation((float)id, 0f, 0f);
        return new RenderProjectionRecord() with
        {
            Id = RenderProjectionId.FromRaw(id),
            ProjectionClass = projectionClass,
            OwnerIncarnation = RenderOwnerIncarnation.FromRaw(1),
            Transform = new RenderTransform(transform),
            PreviousTransform = new PreviousRenderTransform(transform),
            MeshSet = new RenderMeshSet(RenderAssetHandle.FromRaw(id), 1, 1),
            Material = new RenderMaterialVariant(0, 0, 1),
            Residency = new RenderSpatialResidency(
                RenderSpatialBucket.FromRaw(id),
                0x1234FFFF,
                parentCell),
            Bounds = new RenderWorldBounds(Vector3.Zero, Vector3.One),
            Flags = RenderProjectionFlags.Draw
                | RenderProjectionFlags.SpatiallyResident
                | extraFlags,
            SortKey = new RenderSortKey(sortKey ?? id),
            Source = new RenderSourceMetadata(
                LocalEntityId: (uint)id,
                ServerGuid: projectionClass is RenderProjectionClass.LiveDynamicRoot
                    or RenderProjectionClass.EquippedChild
                        ? (uint)id
                        : 0,
                SourceId: (uint)id,
                ParentCellId: parentCell,
                EffectCellId: effectCell,
                BuildingShellAnchorCellId: buildingAnchor,
                TransformFingerprint: default,
                GeometryFingerprint: default,
                AppearanceFingerprint: default),
            EntityPayload = new RenderEntityPayload(
                [new MeshRef((uint)id, transform)],
                PaletteOverride: null,
                IsBuildingShell: building,
                CasterIdentity: casterIdentity),
        };
    }

    private sealed class RecordingMembership(
        IReadOnlyDictionary<uint, IReadOnlyList<uint>> cellsByEntity)
        : IDirectionalShadowCellMembership
    {
        public bool TryGetRetailCellArray(
            uint entityId,
            out IReadOnlyList<uint> cells)
        {
            if (cellsByEntity.TryGetValue(entityId, out IReadOnlyList<uint>? found))
            {
                cells = found;
                return found.Count != 0;
            }
            cells = Array.Empty<uint>();
            return false;
        }
    }

    private sealed class QuerySource : IRenderSceneQuerySource
    {
        private RenderProjectionRecord[] _statics;
        private RenderProjectionRecord[] _dynamics;

        public QuerySource(
            RenderProjectionRecord[] statics,
            RenderProjectionRecord[] dynamics)
        {
            _statics = statics;
            _dynamics = dynamics;
        }

        public int IndexCountReads { get; private set; }
        public int IndexCopies { get; private set; }
        public int ProjectionReads { get; private set; }
        public int BatchedProjectionCopies { get; private set; }
        public ulong TopologyRevision { get; private set; } = 1;
        public ulong TransformRevision { get; private set; } = 1;
        public List<RenderSceneIndex> CopiedIndices { get; } = [];
        private readonly List<DirectionalShadowTransformSnapshot>
            _transformChanges = [];

        public RenderProjectionCounts GetCounts(RenderSceneGeneration generation) =>
            throw new InvalidOperationException("The caster product must not enumerate the whole scene.");

        public RenderSceneIndexCounts GetIndexCounts(RenderSceneGeneration generation)
        {
            IndexCountReads++;
            return new RenderSceneIndexCounts(
                OutdoorStatic: _statics.Length,
                IndoorCellStatic: 0,
                Dynamic: _dynamics.Length,
                OutdoorDynamic: _dynamics.Length,
                PortalStraddlingDynamic: 0,
                Translucent: 0,
                Selectable: 0,
                LightCandidate: 0,
                Dirty: 0);
        }

        public ulong GetIndexRevision(RenderSceneGeneration generation) => 1;

        public ulong GetDirectionalShadowTopologyRevision(
            RenderSceneGeneration generation) => TopologyRevision;

        public ulong GetDirectionalShadowTransformRevision(
            RenderSceneGeneration generation) => TransformRevision;

        public DirectionalShadowTransformChanges
            CopyDirectionalShadowTransformChanges(
                RenderSceneGeneration generation,
                ulong afterRevision,
                Span<DirectionalShadowTransformSnapshot> destination)
        {
            if (afterRevision == TransformRevision)
            {
                return new DirectionalShadowTransformChanges(
                    TransformRevision,
                    0,
                    false);
            }
            ulong delta = TransformRevision - afterRevision;
            if (afterRevision == 0
                || afterRevision > TransformRevision
                || delta > (ulong)_transformChanges.Count
                || delta > (ulong)destination.Length)
            {
                return new DirectionalShadowTransformChanges(
                    TransformRevision,
                    0,
                    true);
            }
            int count = checked((int)delta);
            int start = _transformChanges.Count - count;
            for (int index = 0; index < count; index++)
                destination[index] = _transformChanges[start + index];
            return new DirectionalShadowTransformChanges(
                TransformRevision,
                count,
                false);
        }

        public bool TryGet(
            RenderSceneGeneration generation,
            RenderProjectionId id,
            out RenderProjectionRecord record)
        {
            ProjectionReads++;
            for (int index = 0; index < _statics.Length; index++)
            {
                if (_statics[index].Id == id)
                {
                    record = _statics[index];
                    return true;
                }
            }
            for (int index = 0; index < _dynamics.Length; index++)
            {
                if (_dynamics[index].Id == id)
                {
                    record = _dynamics[index];
                    return true;
                }
            }

            record = default;
            return false;
        }

        public bool TryGetByLocalEntityId(
            RenderSceneGeneration generation,
            uint localEntityId,
            out RenderProjectionRecord record)
        {
            for (int index = 0; index < _statics.Length; index++)
            {
                if (_statics[index].Source.LocalEntityId == localEntityId)
                {
                    record = _statics[index];
                    return true;
                }
            }
            for (int index = 0; index < _dynamics.Length; index++)
            {
                if (_dynamics[index].Source.LocalEntityId == localEntityId)
                {
                    record = _dynamics[index];
                    return true;
                }
            }

            record = default;
            return false;
        }

        public int CopyById(
            RenderSceneGeneration generation,
            ReadOnlySpan<RenderProjectionId> ids,
            Span<RenderProjectionRecord> destination)
        {
            BatchedProjectionCopies++;
            if (destination.Length < ids.Length)
                throw new ArgumentException("Destination is too small.", nameof(destination));
            for (int outputIndex = 0; outputIndex < ids.Length; outputIndex++)
            {
                bool found = false;
                for (int index = 0; index < _statics.Length; index++)
                {
                    if (_statics[index].Id != ids[outputIndex])
                        continue;
                    destination[outputIndex] = _statics[index];
                    found = true;
                    break;
                }
                if (!found)
                {
                    for (int index = 0; index < _dynamics.Length; index++)
                    {
                        if (_dynamics[index].Id != ids[outputIndex])
                            continue;
                        destination[outputIndex] = _dynamics[index];
                        found = true;
                        break;
                    }
                }
                if (!found)
                    throw new InvalidOperationException("Missing projection.");
            }
            return ids.Length;
        }

        public int CopyTo(
            RenderSceneGeneration generation,
            RenderProjectionClass? projectionClass,
            Span<RenderProjectionRecord> destination) =>
            throw new InvalidOperationException("The caster product must not enumerate the whole scene.");

        public int CopyIndexTo(
            RenderSceneGeneration generation,
            RenderSceneIndex index,
            Span<RenderProjectionRecord> destination)
        {
            IndexCopies++;
            CopiedIndices.Add(index);
            RenderProjectionRecord[] values = index switch
            {
                RenderSceneIndex.OutdoorStatic => _statics,
                RenderSceneIndex.OutdoorDynamic => _dynamics,
                _ => throw new InvalidOperationException(
                    $"Unexpected directional-shadow source index {index}."),
            };
            values.CopyTo(destination);
            return values.Length;
        }


        public void ReplaceStatics(
            RenderProjectionRecord[] values,
            bool topologyChanged)
        {
            _statics = values;
            if (topologyChanged)
                TopologyRevision++;
        }

        public void ReplaceDynamics(
            RenderProjectionRecord[] values,
            bool topologyChanged)
        {
            RenderProjectionRecord[] previous = _dynamics;
            _dynamics = values;
            if (topologyChanged)
                TopologyRevision++;
            else
            {
                for (int index = 0; index < values.Length; index++)
                {
                    RenderProjectionRecord current = values[index];
                    RenderProjectionRecord prior = previous.FirstOrDefault(
                        value => value.Id == current.Id);
                    if (prior.Id == current.Id
                        && prior.Transform == current.Transform
                        && PartTransformsEqual(in prior, in current))
                    {
                        continue;
                    }
                    PublishTransformChanges(current.Id, 1);
                }
            }
        }

        public void PublishTransformChanges(RenderProjectionId id, int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            RenderProjectionRecord record = Find(id);
            for (int index = 0; index < count; index++)
            {
                _transformChanges.Add(
                    DirectionalShadowTransformSnapshot.Capture(in record));
                TransformRevision++;
            }
        }

        public void PublishTransformRecord(in RenderProjectionRecord record)
        {
            _transformChanges.Add(
                DirectionalShadowTransformSnapshot.Capture(in record));
            TransformRevision++;
        }

        public void EnsureTransformChangeCapacity(int capacity) =>
            _transformChanges.EnsureCapacity(capacity);

        private RenderProjectionRecord Find(RenderProjectionId id)
        {
            for (int index = 0; index < _statics.Length; index++)
            {
                if (_statics[index].Id == id)
                    return _statics[index];
            }
            for (int index = 0; index < _dynamics.Length; index++)
            {
                if (_dynamics[index].Id == id)
                    return _dynamics[index];
            }
            throw new InvalidOperationException("Missing projection.");
        }

        private static bool PartTransformsEqual(
            in RenderProjectionRecord left,
            in RenderProjectionRecord right)
        {
            IReadOnlyList<MeshRef> leftMeshes = left.EntityPayload.MeshRefs;
            IReadOnlyList<MeshRef> rightMeshes = right.EntityPayload.MeshRefs;
            if (leftMeshes.Count != rightMeshes.Count)
                return false;
            for (int index = 0; index < leftMeshes.Count; index++)
            {
                if (leftMeshes[index].PartTransform
                    != rightMeshes[index].PartTransform)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
