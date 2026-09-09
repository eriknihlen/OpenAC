using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Meshing;
using AcDream.Core.World;
using DatReaderWriter.Enums;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class DirectionalShadowPreparedDrawTests
{
    [Theory]
    [InlineData(TranslucencyKind.Opaque, true, DirectionalShadowCasterMaterial.Opaque)]
    [InlineData(TranslucencyKind.ClipMap, true, DirectionalShadowCasterMaterial.AlphaCutout)]
    [InlineData(TranslucencyKind.AlphaBlend, false, DirectionalShadowCasterMaterial.Opaque)]
    [InlineData(TranslucencyKind.Additive, false, DirectionalShadowCasterMaterial.Opaque)]
    [InlineData(TranslucencyKind.InvAlpha, false, DirectionalShadowCasterMaterial.Opaque)]
    internal void MaterialPolicy_PreservesCutoutsAndExcludesTrueTransparency(
        TranslucencyKind source,
        bool expectedAccepted,
        DirectionalShadowCasterMaterial expectedMaterial)
    {
        bool accepted = DirectionalShadowPreparedDraws.TryClassifyMaterial(
            source,
            out DirectionalShadowCasterMaterial material);

        Assert.Equal(expectedAccepted, accepted);
        if (accepted)
            Assert.Equal(expectedMaterial, material);
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(0.0001f, true)]
    [InlineData(1f, true)]
    [InlineData(float.NaN, true)]
    internal void FadePolicy_OnlyExactOpaquePartsCast(
        float translucency,
        bool excluded)
    {
        Assert.Equal(
            excluded,
            DirectionalShadowPreparedDraws.FadeExcludesCaster(translucency));
    }

    [Fact]
    public void Complete_GroupsCommandsAndRetainsExactCurrentTransforms()
    {
        var product = new DirectionalShadowPreparedDraws();
        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(4);
        Assert.True(product.TryBegin(generation, 7, estimatedInstances: 3));
        Matrix4x4 first = Matrix4x4.CreateRotationZ(0.25f)
            * Matrix4x4.CreateTranslation(10f, 20f, 30f);
        Matrix4x4 second = Matrix4x4.CreateScale(1.25f)
            * Matrix4x4.CreateTranslation(-2f, 3f, 7f);
        Matrix4x4 leaves = Matrix4x4.CreateRotationX(0.5f)
            * Matrix4x4.CreateTranslation(100f, 200f, 12f);

        product.Add(
            firstIndex: 40,
            baseVertex: 3,
            indexCount: 18,
            GpuTextureSlot.Unassigned,
            textureLayer: 0,
            CullMode.CounterClockwise,
            DirectionalShadowCasterMaterial.Opaque,
            in first);
        product.Add(
            firstIndex: 40,
            baseVertex: 3,
            indexCount: 18,
            GpuTextureSlot.Unassigned,
            textureLayer: 0,
            CullMode.CounterClockwise,
            DirectionalShadowCasterMaterial.Opaque,
            in second);
        product.Add(
            firstIndex: 90,
            baseVertex: 11,
            indexCount: 24,
            new GpuTextureSlot(17),
            textureLayer: 6,
            CullMode.None,
            DirectionalShadowCasterMaterial.AlphaCutout,
            in leaves);
        var inputStats = new DirectionalShadowPreparationStats(
            SourceCasters: 2,
            SourceMeshRefs: 3,
            SourceParts: 3,
            SourceBatches: 5,
            PreparedInstances: 0,
            PreparedOpaqueCommands: 0,
            PreparedAlphaCutoutCommands: 0,
            RejectedTransparentBatches: 2,
            RejectedFadedParts: 1,
            MissingMeshes: 0,
            UnresolvedAlphaCutoutTextures: 0);

        product.Complete(generation, 7, in inputStats);

        Assert.Equal(3, product.Transforms.Length);
        Assert.Equal(2, product.Commands.Length);
        Assert.Equal(1, product.OpaqueCommandCount);
        Assert.Equal(1, product.AlphaCutoutCommandCount);
        Assert.Single(product.OpaqueCommands.ToArray());
        Assert.Single(product.AlphaCutoutCommands.ToArray());
        Assert.Single(product.OpaqueBatches.ToArray());
        Assert.Single(product.AlphaCutoutBatches.ToArray());
        Assert.Single(product.OpaqueRuns.ToArray());
        Assert.Single(product.AlphaCutoutRuns.ToArray());
        Assert.Equal(0, product.OpaqueRuns[0].StartCommand);
        Assert.Equal(1, product.AlphaCutoutRuns[0].StartCommand);
        Assert.Equal(2u, product.Commands[0].InstanceCount);
        Assert.Equal(0u, product.Commands[0].BaseInstance);
        Assert.Equal(1u, product.Commands[1].InstanceCount);
        Assert.Equal(2u, product.Commands[1].BaseInstance);
        Assert.Equal(DirectionalShadowCasterMaterial.Opaque, product.Batches[0].Material);
        Assert.False(product.Batches[0].TextureSlot.IsAssigned);
        Assert.Equal(DirectionalShadowCasterMaterial.AlphaCutout, product.Batches[1].Material);
        Assert.Equal(17u, product.Batches[1].TextureSlot.Index);
        Assert.Equal(6u, product.Batches[1].TextureLayer);
        Assert.Contains(first, product.Transforms.ToArray());
        Assert.Contains(second, product.Transforms.ToArray());
        Assert.Equal(leaves, product.Transforms[2]);
        Assert.Equal(3, product.Stats.PreparedInstances);
        Assert.Equal(2, product.Stats.RejectedTransparentBatches);
        Assert.Equal(1, product.Stats.RejectedFadedParts);
    }

    [Fact]
    public void SameSubsetDifferentFoliageClassificationNeverCoalescesIntoOneCasterBatch()
    {
        const uint sceneryEntityId = 0x80010203u;   // ProceduralSceneryIdAllocator
        const uint landblockStaticEntityId = 0xC0010203u; // LandblockStaticEntityIdAllocator
        uint sceneryFlags = FoliageWindClassification.Classify(
            sceneryEntityId,
            isExcluded: false,
            TranslucencyKind.ClipMap,
            meshHasCutoutSubset: true);
        uint landblockStaticFlags = FoliageWindClassification.Classify(
            landblockStaticEntityId,
            isExcluded: false,
            TranslucencyKind.ClipMap,
            meshHasCutoutSubset: true);
        Assert.Equal(FoliageWindClassification.CutoutFoliageFlag, sceneryFlags);
        Assert.Equal(0u, landblockStaticFlags);

        var product = new DirectionalShadowPreparedDraws();
        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(1);
        Assert.True(product.TryBegin(generation, 1, estimatedInstances: 2));
        Matrix4x4 sceneryTransform = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        Matrix4x4 landblockStaticTransform = Matrix4x4.CreateTranslation(9f, 8f, 7f);
        GpuTextureSlot sharedTexture = new(42);

        product.Add(
            firstIndex: 100,
            baseVertex: 5,
            indexCount: 12,
            sharedTexture,
            textureLayer: 0,
            CullMode.CounterClockwise,
            DirectionalShadowCasterMaterial.AlphaCutout,
            in sceneryTransform,
            sceneryFlags);
        product.Add(
            firstIndex: 100,
            baseVertex: 5,
            indexCount: 12,
            sharedTexture,
            textureLayer: 0,
            CullMode.CounterClockwise,
            DirectionalShadowCasterMaterial.AlphaCutout,
            in landblockStaticTransform,
            landblockStaticFlags);
        product.Complete(
            generation,
            1,
            new DirectionalShadowPreparationStats(
                SourceCasters: 2,
                SourceMeshRefs: 2,
                SourceParts: 2,
                SourceBatches: 2,
                PreparedInstances: 0,
                PreparedOpaqueCommands: 0,
                PreparedAlphaCutoutCommands: 0,
                RejectedTransparentBatches: 0,
                RejectedFadedParts: 0,
                MissingMeshes: 0,
                UnresolvedAlphaCutoutTextures: 0));

        Assert.Equal(2, product.Commands.Length);
        Assert.Equal(2, product.Batches.Length);
        Assert.All(product.Commands.ToArray(), command => Assert.Equal(1u, command.InstanceCount));

        int sceneryIndex = product.Batches.ToArray()
            .ToList()
            .FindIndex(batch => batch.FoliageFlags == FoliageWindClassification.CutoutFoliageFlag);
        int landblockStaticIndex = product.Batches.ToArray()
            .ToList()
            .FindIndex(batch => batch.FoliageFlags == 0u);
        Assert.True(sceneryIndex >= 0, "expected one prepared batch carrying the cutout foliage flag");
        Assert.True(landblockStaticIndex >= 0, "expected one prepared batch carrying zero foliage flags");
        Assert.NotEqual(sceneryIndex, landblockStaticIndex);
        Assert.Equal(
            sceneryTransform,
            product.Transforms[(int)product.Commands[sceneryIndex].BaseInstance]);
        Assert.Equal(
            landblockStaticTransform,
            product.Transforms[(int)product.Commands[landblockStaticIndex].BaseInstance]);
    }

    [Fact]
    public void SameCasterBuild_ReplaysWithoutReclassificationOrStorageGrowth()
    {
        var product = new DirectionalShadowPreparedDraws();
        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(8);
        Matrix4x4 exactMeshRefTransform = new(
            1, 2, 3, 4,
            5, 6, 7, 8,
            9, 10, 11, 12,
            13, 14, 15, 16);
        Assert.True(product.TryBegin(generation, 20, estimatedInstances: 1));
        product.Add(
            1,
            2,
            3,
            GpuTextureSlot.Unassigned,
            0,
            CullMode.Clockwise,
            DirectionalShadowCasterMaterial.Opaque,
            in exactMeshRefTransform);
        DirectionalShadowPreparationStats stats = default;
        product.Complete(generation, 20, in stats);
        long retained = product.RetainedScratchBytes;
        ulong buildSequence = product.BuildSequence;

        Assert.False(product.TryBegin(generation, 20, estimatedInstances: 100));

        Assert.Equal(buildSequence, product.BuildSequence);
        Assert.Equal(retained, product.RetainedScratchBytes);
        Assert.Equal(exactMeshRefTransform, product.Transforms[0]);
        Assert.Single(product.Commands.ToArray());
    }

    [Fact]
    public void AlternatingCasterSelection_EmitsExactContiguousInstanceRuns()
    {
        var product = new DirectionalShadowPreparedDraws();
        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(18);
        Assert.True(product.TryBegin(generation, 33, estimatedInstances: 6));
        for (int casterIndex = 0; casterIndex < 6; casterIndex++)
        {
            Matrix4x4 transform = Matrix4x4.CreateTranslation(casterIndex, 0f, 0f);
            DirectionalShadowTransformSource source =
                DirectionalShadowTransformSource.Static(casterIndex);
            product.Add(
                firstIndex: 100,
                baseVertex: 7,
                indexCount: 12,
                GpuTextureSlot.Unassigned,
                textureLayer: 0,
                CullMode.CounterClockwise,
                DirectionalShadowCasterMaterial.Opaque,
                in transform,
                in source);
        }
        DirectionalShadowPreparationStats stats = default;
        product.Complete(generation, 33, in stats);
        ulong topologySequence = product.BuildSequence;
        bool[] alternating = [true, false, true, false, true, false];

        product.ApplySelection(alternating, casterSelectionSequence: 1);

        Assert.Single(product.Commands.ToArray());
        Assert.Equal(6u, product.Commands[0].InstanceCount);
        Assert.Equal(3, product.ActiveCommands.Length);
        Assert.Equal([0u, 2u, 4u],
            product.ActiveCommands.ToArray().Select(static command => command.BaseInstance));
        Assert.All(product.ActiveCommands.ToArray(),
            static command => Assert.Equal(1u, command.InstanceCount));
        Assert.Equal(3, product.ActiveBatches.Length);
        Assert.Equal(3, product.Stats.ActiveInstances);
        Assert.Equal(3, product.Stats.ActiveCommands);
        Assert.Equal(topologySequence, product.BuildSequence);

        bool[] inverse = [false, true, false, true, false, true];
        product.ApplySelection(inverse, casterSelectionSequence: 2);
        Assert.Equal([1u, 3u, 5u],
            product.ActiveCommands.ToArray().Select(static command => command.BaseInstance));
        Assert.Equal(topologySequence, product.BuildSequence);

        ulong selectionSequence = 2;
        ZeroAllocationProbe.AssertAllocatesNothing(
            "DirectionalShadowPreparedDraws.ApplySelection alternating",
            () => product.ApplySelection(
                (selectionSequence & 1ul) == 0ul ? alternating : inverse,
                ++selectionSequence),
            batchSize: 256);
        Assert.Equal(topologySequence, product.BuildSequence);
    }

    [Fact]
    public void StableTopology_ComposesSlimPoseWithoutMutatingCasterOrAllocating()
    {
        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(10);
        Matrix4x4 originalRoot = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        Matrix4x4 originalPart = Matrix4x4.CreateTranslation(4f, 5f, 6f);
        Matrix4x4 setupPart = Matrix4x4.CreateRotationX(0.25f)
            * Matrix4x4.CreateTranslation(7f, 8f, 9f);
        DirectionalShadowCaster[] casters =
        [
            new DirectionalShadowCaster(
                Projection(originalRoot, originalPart),
                DirectionalShadowCasterKind.LiveDynamic),
        ];
        var product = new DirectionalShadowPreparedDraws();
        Assert.True(product.TryBegin(
            generation,
            casterBuildSequence: 12,
            estimatedInstances: 2,
            renderDataAvailabilityVersion: 5,
            translucencyFadeRevision: 7));
        product.MapCasterIdentity(
            0,
            casters[0].Projection.Id,
            casters[0].Projection.ProjectionClass);
        DirectionalShadowTransformSource direct =
            DirectionalShadowTransformSource.Dynamic(
                casterIndex: 0,
                meshIndex: 0,
                isSetupPart: false,
                in setupPart);
        DirectionalShadowTransformSource setup =
            DirectionalShadowTransformSource.Dynamic(
                casterIndex: 0,
                meshIndex: 0,
                isSetupPart: true,
                in setupPart);
        Matrix4x4 originalDirect = originalPart * originalRoot;
        Matrix4x4 originalSetup = setupPart * originalPart * originalRoot;
        product.Add(
            10,
            0,
            3,
            GpuTextureSlot.Unassigned,
            0,
            CullMode.Clockwise,
            DirectionalShadowCasterMaterial.Opaque,
            in originalDirect,
            in direct);
        product.Add(
            20,
            0,
            3,
            GpuTextureSlot.Unassigned,
            0,
            CullMode.Clockwise,
            DirectionalShadowCasterMaterial.Opaque,
            in originalSetup,
            in setup);
        DirectionalShadowPreparationStats stats = default;
        product.Complete(
            generation,
            12,
            in stats,
            renderDataAvailabilityVersion: 5,
            translucencyFadeRevision: 7);
        ulong topologyBuild = product.BuildSequence;

        float exactRootX = BitConverter.Int32BitsToSingle(0x41234567);
        float exactPartY = BitConverter.Int32BitsToSingle(0x40ABCDEF);
        Matrix4x4 currentRoot = Matrix4x4.CreateRotationZ(0.15f)
            * Matrix4x4.CreateTranslation(exactRootX, 12f, 13f);
        Matrix4x4 currentPart = Matrix4x4.CreateRotationY(0.35f)
            * Matrix4x4.CreateTranslation(14f, exactPartY, 16f);
        RenderProjectionRecord current = Projection(currentRoot, currentPart);
        DirectionalShadowTransformSnapshot snapshot =
            DirectionalShadowTransformSnapshot.Capture(in current);
        DirectionalShadowChangedPose[] changed = [new(0, snapshot)];

        product.RefreshDynamicTransforms(changed);

        AssertMatrixBitsEqual(currentPart * currentRoot, product.Transforms[0]);
        AssertMatrixBitsEqual(
            setupPart * currentPart * currentRoot,
            product.Transforms[1]);
        Assert.Equal(topologyBuild, product.BuildSequence);
        Assert.Equal(2, product.LastDynamicTransformRefreshCount);
        Assert.False(product.RequiresTopologyBuild(generation, 12, 5, 7));
        AssertMatrixBitsEqual(
            originalRoot,
            casters[0].Projection.Transform.LocalToWorld);
        AssertMatrixBitsEqual(
            originalPart,
            casters[0].Projection.EntityPayload.MeshRefs[0].PartTransform);

        product.RefreshDynamicTransforms(
            ReadOnlySpan<DirectionalShadowChangedPose>.Empty);
        Assert.Empty(product.DynamicTransformSlots.ToArray());
        Assert.Equal(0, product.LastDynamicTransformRefreshCount);
        AssertMatrixBitsEqual(currentPart * currentRoot, product.Transforms[0]);
        AssertMatrixBitsEqual(
            setupPart * currentPart * currentRoot,
            product.Transforms[1]);

        product.RefreshDynamicTransforms(changed, denseRefresh: true);
        Assert.True(product.LastDynamicTransformRefreshWasDense);
        Assert.Equal([0, 1], product.DynamicTransformSlots.ToArray());

        product.RefreshDynamicTransforms(changed);
        ZeroAllocationProbe.AssertAllocatesNothing(
            "DirectionalShadowPreparedDraws.RefreshDynamicTransforms",
            () => product.RefreshDynamicTransforms(changed),
            batchSize: 256);

        RenderProjectionRecord wrongId = current with
        {
            Id = RenderProjectionId.FromRaw(99),
        };
        DirectionalShadowTransformSnapshot wrongSnapshot =
            DirectionalShadowTransformSnapshot.Capture(in wrongId);
        DirectionalShadowChangedPose[] wrongIdentity = [new(0, wrongSnapshot)];
        InvalidOperationException identityFailure = Assert.Throws<
            InvalidOperationException>(
            () => product.RefreshDynamicTransforms(wrongIdentity));
        Assert.Contains("does not match", identityFailure.Message);

        DirectionalShadowChangedPose[] staleSlot = [new(1, snapshot)];
        InvalidOperationException slotFailure = Assert.Throws<
            InvalidOperationException>(
            () => product.RefreshDynamicTransforms(staleSlot));
        Assert.Contains("stale or unmapped caster index", slotFailure.Message);
    }

    [Fact]
    public void StableTopology_MapsChangedCasterToOnlyItsRetainedTransformSlots()
    {
        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(12);
        Matrix4x4 firstRoot = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        Matrix4x4 secondRoot = Matrix4x4.CreateTranslation(4f, 5f, 6f);
        Matrix4x4 firstPart = Matrix4x4.CreateTranslation(7f, 8f, 9f);
        Matrix4x4 secondPart = Matrix4x4.CreateTranslation(10f, 11f, 12f);
        DirectionalShadowCaster[] casters =
        [
            new DirectionalShadowCaster(
                Projection(firstRoot, firstPart),
                DirectionalShadowCasterKind.LiveDynamic),
            new DirectionalShadowCaster(
                Projection(secondRoot, secondPart) with
                {
                    Id = RenderProjectionId.FromRaw(2),
                },
                DirectionalShadowCasterKind.EquippedChild),
        ];
        var product = new DirectionalShadowPreparedDraws();
        Assert.True(product.TryBegin(generation, 13, estimatedInstances: 2));
        product.MapCasterIdentity(
            0,
            casters[0].Projection.Id,
            casters[0].Projection.ProjectionClass);
        product.MapCasterIdentity(
            1,
            casters[1].Projection.Id,
            casters[1].Projection.ProjectionClass);
        Matrix4x4 setupPart = Matrix4x4.Identity;
        DirectionalShadowTransformSource firstSource =
            DirectionalShadowTransformSource.Dynamic(
                0,
                0,
                false,
                in setupPart);
        DirectionalShadowTransformSource secondSource =
            DirectionalShadowTransformSource.Dynamic(
                1,
                0,
                false,
                in setupPart);
        Matrix4x4 firstWorld = firstPart * firstRoot;
        Matrix4x4 secondWorld = secondPart * secondRoot;
        product.Add(
            1, 0, 3, GpuTextureSlot.Unassigned, 0,
            CullMode.Clockwise,
            DirectionalShadowCasterMaterial.Opaque,
            in firstWorld,
            in firstSource);
        product.Add(
            2, 0, 3, GpuTextureSlot.Unassigned, 0,
            CullMode.Clockwise,
            DirectionalShadowCasterMaterial.Opaque,
            in secondWorld,
            in secondSource);
        DirectionalShadowPreparationStats stats = default;
        product.Complete(generation, 13, in stats);

        Matrix4x4 movedRoot = Matrix4x4.CreateTranslation(40f, 50f, 60f);
        Matrix4x4 movedPart = Matrix4x4.CreateTranslation(70f, 80f, 90f);
        casters[1] = casters[1] with
        {
            Projection = Projection(movedRoot, movedPart) with
            {
                Id = RenderProjectionId.FromRaw(2),
            },
        };
        product.RefreshDynamicTransforms(casters, [1]);

        Assert.Equal([0, 1], product.AllDynamicTransformSlots.ToArray());
        Assert.Equal([1], product.DynamicTransformSlots.ToArray());
        AssertMatrixBitsEqual(firstWorld, product.Transforms[0]);
        AssertMatrixBitsEqual(movedPart * movedRoot, product.Transforms[1]);
        Assert.Equal(1, product.LastDynamicTransformRefreshCount);

        product.RefreshDenseDynamicTransforms(casters);
        Assert.True(product.LastDynamicTransformRefreshWasDense);
        Assert.Equal([0, 1], product.DynamicTransformSlots.ToArray());
        Assert.Equal(2, product.LastDynamicTransformRefreshCount);
        AssertMatrixBitsEqual(firstWorld, product.Transforms[0]);
        AssertMatrixBitsEqual(movedPart * movedRoot, product.Transforms[1]);

        Matrix4x4 denseFirstRoot = Matrix4x4.CreateTranslation(100f, 101f, 102f);
        Matrix4x4 denseFirstPart = Matrix4x4.CreateTranslation(103f, 104f, 105f);
        casters[0] = casters[0] with
        {
            Projection = Projection(denseFirstRoot, denseFirstPart),
        };
        RenderProjectionRecord firstProjection = casters[0].Projection;
        RenderProjectionRecord secondProjection = casters[1].Projection;
        DirectionalShadowTransformSnapshot firstPose =
            DirectionalShadowTransformSnapshot.Capture(in firstProjection);
        DirectionalShadowTransformSnapshot secondPose =
            DirectionalShadowTransformSnapshot.Capture(in secondProjection);
        DirectionalShadowChangedPose[] reversedDenseChanges =
        [
            new(1, secondPose),
            new(0, firstPose),
        ];

        product.RefreshDynamicTransforms(reversedDenseChanges, denseRefresh: true);

        Assert.True(product.LastDynamicTransformRefreshWasDense);
        Assert.Equal([0, 1], product.DynamicTransformSlots.ToArray());
        Assert.Equal(2, product.LastDynamicTransformRefreshCount);
        AssertMatrixBitsEqual(
            denseFirstPart * denseFirstRoot,
            product.Transforms[0]);
        AssertMatrixBitsEqual(movedPart * movedRoot, product.Transforms[1]);

        ZeroAllocationProbe.AssertAllocatesNothing(
            "DirectionalShadowPreparedDraws.RefreshDynamicTransforms dense",
            () => product.RefreshDynamicTransforms(
                reversedDenseChanges,
                denseRefresh: true),
            batchSize: 256);

        Assert.True(product.TryBegin(generation, 14, estimatedInstances: 0));
        product.Complete(generation, 14, in stats);
        product.RefreshDynamicTransforms(casters, [0]);
        Assert.Empty(product.DynamicTransformSlots.ToArray());
        Assert.Equal(0, product.LastDynamicTransformRefreshCount);
    }

    [Fact]
    public void ResourceAvailabilityFadeAndPendingTextureInvalidateRetainedTopology()
    {
        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(11);
        var product = new DirectionalShadowPreparedDraws();
        Assert.True(product.TryBegin(generation, 2, 0, 20, 30));
        var pending = new DirectionalShadowPreparationStats(
            SourceCasters: 1,
            SourceMeshRefs: 1,
            SourceParts: 1,
            SourceBatches: 1,
            PreparedInstances: 0,
            PreparedOpaqueCommands: 0,
            PreparedAlphaCutoutCommands: 0,
            RejectedTransparentBatches: 0,
            RejectedFadedParts: 0,
            MissingMeshes: 1,
            UnresolvedAlphaCutoutTextures: 0);
        product.Complete(generation, 2, in pending, 20, 30);

        Assert.False(product.RequiresTopologyBuild(generation, 2, 20, 30));
        Assert.True(product.RequiresTopologyBuild(generation, 2, 21, 30));
        Assert.True(product.RequiresTopologyBuild(generation, 2, 20, 31));

        Assert.True(product.TryBegin(generation, 2, 0, 21, 30));
        pending = pending with { UnresolvedAlphaCutoutTextures = 1 };
        product.Complete(generation, 2, in pending, 21, 30);
        Assert.True(product.RequiresTopologyBuild(generation, 2, 21, 30));
    }

    [Fact]
    public void SetupComposition_UsesPublishedMeshRefTransformWithoutAnotherPose()
    {
        Matrix4x4 root = Matrix4x4.CreateRotationZ(0.1f)
            * Matrix4x4.CreateTranslation(50f, 60f, 70f);
        Matrix4x4 currentMeshRef = Matrix4x4.CreateRotationY(0.2f)
            * Matrix4x4.CreateTranslation(4f, 5f, 6f);
        Matrix4x4 authoredSetupPart = Matrix4x4.CreateRotationX(0.3f)
            * Matrix4x4.CreateTranslation(1f, 2f, 3f);

        Matrix4x4 actual = WbDrawDispatcher.ComposePartWorldMatrix(
            root,
            currentMeshRef,
            authoredSetupPart);

        Assert.Equal(authoredSetupPart * currentMeshRef * root, actual);
    }

    private static RenderProjectionRecord Projection(
        in Matrix4x4 root,
        in Matrix4x4 part) =>
        new RenderProjectionRecord() with
        {
            Id = RenderProjectionId.FromRaw(1),
            ProjectionClass = RenderProjectionClass.LiveDynamicRoot,
            Transform = new RenderTransform(root),
            EntityPayload = new RenderEntityPayload(
                [new MeshRef(0x01000001, part)],
                PaletteOverride: null,
                IsBuildingShell: false),
        };

    private static void AssertMatrixBitsEqual(
        Matrix4x4 expected,
        Matrix4x4 actual)
    {
        ReadOnlySpan<byte> expectedBits = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref expected, 1));
        ReadOnlySpan<byte> actualBits = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref actual, 1));
        Assert.True(expectedBits.SequenceEqual(actualBits));
    }

    [Fact]
    public void AWarmedTopologyRebuildAllocatesNearZero()
    {
        const int drawCount = 4096;
        var product = new DirectionalShadowPreparedDraws();
        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(9);

        BuildVariedTopology(product, generation, buildSequence: 1, drawCount);
        ulong buildSequence = 1;
        long allocated = ZeroAllocationProbe.MeasureWarmed(
            () => BuildVariedTopology(
                product,
                generation,
                ++buildSequence,
                drawCount),
            batchSize: 1);

        Assert.Equal(drawCount, product.Stats.PreparedInstances);
        Assert.True(
            allocated < 2048,
            $"A warmed directional-shadow topology rebuild allocated {allocated} bytes.");
    }

    private static void BuildVariedTopology(
        DirectionalShadowPreparedDraws product,
        RenderSceneGeneration generation,
        ulong buildSequence,
        int drawCount)
    {
        Assert.True(product.TryBegin(
            generation,
            buildSequence,
            estimatedInstances: drawCount));
        Matrix4x4 transform = Matrix4x4.Identity;
        for (int i = 0; i < drawCount; i++)
        {
            bool cutout = (i & 1) != 0;
            product.Add(
                firstIndex: (uint)((i * 37) % 1024),
                baseVertex: (i * 13) % 512,
                indexCount: 3 + (i % 5) * 3,
                cutout ? new GpuTextureSlot((uint)(i % 7)) : GpuTextureSlot.Unassigned,
                textureLayer: (uint)(i % 11),
                (i % 3) switch
                {
                    0 => CullMode.None,
                    1 => CullMode.Clockwise,
                    _ => CullMode.CounterClockwise,
                },
                cutout
                    ? DirectionalShadowCasterMaterial.AlphaCutout
                    : DirectionalShadowCasterMaterial.Opaque,
                in transform);
        }
        product.Complete(
            generation,
            buildSequence,
            new DirectionalShadowPreparationStats(
                SourceCasters: drawCount,
                SourceMeshRefs: drawCount,
                SourceParts: drawCount,
                SourceBatches: drawCount,
                PreparedInstances: 0,
                PreparedOpaqueCommands: 0,
                PreparedAlphaCutoutCommands: 0,
                RejectedTransparentBatches: 0,
                RejectedFadedParts: 0,
                MissingMeshes: 0,
                UnresolvedAlphaCutoutTextures: 0));
    }
}
