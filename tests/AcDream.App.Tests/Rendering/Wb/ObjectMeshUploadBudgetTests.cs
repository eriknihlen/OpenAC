using AcDream.App.Rendering.Wb;
using AcDream.Content;
using Chorizite.Core.Render.Enums;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class ObjectMeshUploadBudgetTests
{
    [Fact]
    public void EstimateIncludesVerticesIndicesTexturesAndNestedGeometry()
    {
        var nested = new ObjectMeshData
        {
            Vertices = new VertexPositionNormalTexture[2],
            TextureBatches =
            {
                [(8, 8, TextureFormat.RGBA8)] =
                [
                    new TextureBatchData
                    {
                        TextureData = new byte[256],
                        Indices = [0, 1, 2],
                    },
                ],
            },
        };
        var setup = new ObjectMeshData
        {
            IsSetup = true,
            EnvCellGeometry = nested,
        };

        long expected = 1024
            + 2L * VertexPositionNormalTexture.Size
            + 256
            + 3L * sizeof(ushort);
        Assert.Equal(expected, ObjectMeshManager.EstimateUploadBytes(setup));
    }

    [Fact]
    public void First512RgbaLayerIncludesArrayAndWholeMipChain()
    {
        MeshUploadCost cost = ObjectMeshManager.CalculateNewAtlasFirstUploadCost(
            512,
            512,
            TextureFormat.RGBA8,
            uploadBytes: 512 * 512 * 4,
            sourceBytes: 512 * 512 * 4);

        long expectedArray = TextureAtlasManager.CalculateArrayBytes(512, 512, TextureFormat.RGBA8);
        Assert.Equal(expectedArray, cost.ArrayAllocationBytes);
        Assert.Equal(expectedArray, cost.MipmapBytes);
        Assert.Equal(1, cost.NewArrayCount);
    }

    [Fact]
    public void First1024RgbaLayerUsesOneLayerAtlasCapacity()
    {
        const int layerBytes = 1024 * 1024 * 4;

        MeshUploadCost cost = ObjectMeshManager.CalculateNewAtlasFirstUploadCost(
            1024,
            1024,
            TextureFormat.RGBA8,
            uploadBytes: layerBytes,
            sourceBytes: layerBytes);

        Assert.Equal(1, TextureAtlasManager.CalculateInitialCapacity(1024, 1024, TextureFormat.RGBA8));
        Assert.Equal(5_592_404, cost.ArrayAllocationBytes);
    }

    [Fact]
    public void First512RgbaLayerHasFixedGoldenPhysicalCosts()
    {
        const int layerBytes = 512 * 512 * 4;
        MeshUploadCost cost = ObjectMeshManager.CalculateNewAtlasFirstUploadCost(
            512,
            512,
            TextureFormat.RGBA8,
            uploadBytes: layerBytes);

        Assert.Equal(6, TextureAtlasManager.CalculateInitialCapacity(512, 512, TextureFormat.RGBA8));
        Assert.Equal(8_388_600, cost.ArrayAllocationBytes);
        Assert.Equal(8_388_600, cost.MipmapBytes);
    }

    [Fact]
    public void Five512RgbaLayersChargeOneArrayAndOneMipGeneration()
    {
        const int layerBytes = 512 * 512 * 4;

        MeshUploadCost cost = ObjectMeshManager.CalculateNewAtlasUploadCost(
            512,
            512,
            TextureFormat.RGBA8,
            [layerBytes, layerBytes, layerBytes, layerBytes, layerBytes]);

        Assert.Equal(8_388_600, cost.ArrayAllocationBytes);
        Assert.Equal(8_388_600, cost.MipmapBytes);
        Assert.Equal(1, cost.NewArrayCount);
    }

    [Fact]
    public void BoundedIndivisibleHeadRunsImmediatelyWithoutVirtualReservation()
    {
        var limits = new MeshUploadBudgetLimits(
            MaximumObjects: 4,
            MaximumSourceBytes: 100,
            MaximumArrayAllocationBytes: 100,
            MaximumMipmapBytes: 100,
            MaximumNewArrays: 2,
            MaximumSingleSourceBytes: 200,
            MaximumSingleArrayAllocationBytes: 200,
            MaximumSingleMipmapBytes: 200,
            MaximumSingleNewArrays: 4);
        var budget = new MeshUploadFrameBudget(limits);

        var oversized = new MeshUploadCost(150, 150, 150, 3);
        Assert.True(budget.TryAdmit(oversized));
        Assert.False(budget.TryAdmit(new MeshUploadCost(1, 0, 0, 0)));
        Assert.Equal(1, budget.ObjectCount);
    }

    [Fact]
    public void HeadAboveExplicitSingleOperationCeilingFailsLoudly()
    {
        var budget = new MeshUploadFrameBudget(new MeshUploadBudgetLimits(
            MaximumObjects: 4,
            MaximumSourceBytes: 100,
            MaximumArrayAllocationBytes: 100,
            MaximumMipmapBytes: 100,
            MaximumNewArrays: 1,
            MaximumSingleSourceBytes: 200));

        Assert.Throws<NotSupportedException>(
            () => budget.TryAdmit(new MeshUploadCost(250, 0, 0, 0, AdmissionKey: 17)));
    }

    [Fact]
    public void BufferMigrationCostCannotBePretendedAdmitted()
    {
        var budget = new MeshUploadFrameBudget(new MeshUploadBudgetLimits(
            MaximumObjects: 4,
            MaximumSourceBytes: 100,
            MaximumArrayAllocationBytes: 100,
            MaximumMipmapBytes: 100,
            MaximumNewArrays: 1));

        Assert.Throws<InvalidOperationException>(
            () => budget.TryAdmit(new MeshUploadCost(
                1, 0, 0, 0,
                BufferAllocationBytes: 1,
                BufferCopyBytes: 1,
                NewBufferCount: 1)));
    }

    [Fact]
    public void GlobalArenaGrowthIsGeometricAndAvoidsRepeatedPrefixCopies()
    {
        Assert.Equal(
            1_500,
            GlobalMeshBuffer.CalculateGrowthCapacity(
                capacity: 1_000,
                trailingFreeLength: 100,
                requiredContiguousLength: 300,
                growthQuantum: 50));

        Assert.Equal(
            1_000,
            GlobalMeshBuffer.CalculateGrowthCapacity(
                capacity: 1_000,
                trailingFreeLength: 300,
                requiredContiguousLength: 300,
                growthQuantum: 50));
    }

    [Fact]
    public void GlobalArenaTrimUsesThreeToOneHysteresisAndKeepsDoubleHeadroom()
    {
        Assert.True(GlobalMeshBuffer.TryCalculateTrimCapacity(
            capacity: 8_192,
            highWaterMark: 1_000,
            initialCapacity: 512,
            growthQuantum: 256,
            out int trimmed));
        Assert.Equal(2_048, trimmed);

        Assert.False(GlobalMeshBuffer.TryCalculateTrimCapacity(
            capacity: 2_048,
            highWaterMark: 1_200,
            initialCapacity: 512,
            growthQuantum: 256,
            out _));
    }

    [Fact]
    public void FrameBudgetRejectsPrefixItemWhenAnySingleDimensionWouldOverflow()
    {
        var budget = new MeshUploadFrameBudget(new MeshUploadBudgetLimits(
            MaximumObjects: 4,
            MaximumSourceBytes: 100,
            MaximumArrayAllocationBytes: 100,
            MaximumMipmapBytes: 100,
            MaximumNewArrays: 2,
            MaximumBufferUploadBytes: 100,
            MaximumBufferAllocationBytes: 100,
            MaximumBufferCopyBytes: 100,
            MaximumNewBuffers: 2));

        Assert.True(budget.TryAdmit(new MeshUploadCost(40, 40, 40, 1, BufferUploadBytes: 40)));
        Assert.True(budget.TryAdmit(new MeshUploadCost(40, 40, 40, 1, BufferUploadBytes: 40)));
        Assert.False(budget.TryAdmit(new MeshUploadCost(1, 1, 1, 0, BufferUploadBytes: 21)));
        Assert.Equal(2, budget.ObjectCount);
        Assert.Equal(80, budget.BufferUploadBytes);
    }

    [Fact]
    public void MigrationCopyChunksNeverExceedRealFrameBudget()
    {
        const long budget = 32L * 1024 * 1024;
        const long total = 100L * 1024 * 1024;

        Assert.Equal(budget, GlobalMeshBuffer.CalculateCopyChunk(total, 0, budget));
        Assert.Equal(budget, GlobalMeshBuffer.CalculateCopyChunk(total, budget, budget));
        Assert.Equal(4L * 1024 * 1024,
            GlobalMeshBuffer.CalculateCopyChunk(total, 96L * 1024 * 1024, budget));
        Assert.Equal(0, GlobalMeshBuffer.CalculateCopyChunk(total, total, budget));
    }

    [Fact]
    public void ModernArenaGeometryIsCountedOnlyByPhysicalBackingCapacity()
    {
        const long arenaCapacity = 512;
        const long geometryRanges = 100;

        long modernNonArena = ObjectMeshManager.CalculateNonArenaGeometryBytes(
            usesGlobalArena: true,
            geometryRanges);
        long legacyNonArena = ObjectMeshManager.CalculateNonArenaGeometryBytes(
            usesGlobalArena: false,
            geometryRanges);

        Assert.Equal(0, modernNonArena);
        Assert.Equal(arenaCapacity,
            ObjectMeshManager.CalculateTrackedGpuBytes(modernNonArena, arenaCapacity));
        Assert.Equal(arenaCapacity + geometryRanges,
            ObjectMeshManager.CalculateTrackedGpuBytes(legacyNonArena, arenaCapacity));
        Assert.True(ObjectMeshManager.IsWithinGpuCacheBudget(0, arenaCapacity, 600));
        Assert.False(ObjectMeshManager.IsWithinGpuCacheBudget(100, arenaCapacity, 600));
    }

    [Fact]
    public void SetupDependencyRollbackAttemptsEveryPartAndRetriesOnlyFailure()
    {
        var calls = new Dictionary<ulong, int>();
        RetryableResourceReleaseLedger rollback =
            ObjectMeshManager.CreateSetupPartRollback(
                [1, 2, 3],
                id =>
                {
                    calls[id] = calls.GetValueOrDefault(id) + 1;
                    if (id == 2 && calls[id] == 1)
                        throw new InvalidOperationException("part release");
                });

        ResourceReleaseAttempt first = rollback.Advance();
        Assert.Single(first.Failures);
        Assert.Equal(1, calls[1]);
        Assert.Equal(1, calls[2]);
        Assert.Equal(1, calls[3]);

        ResourceReleaseAttempt retry = rollback.Advance();
        Assert.Empty(retry.Failures);
        Assert.True(rollback.IsComplete);
        Assert.Equal(1, calls[1]);
        Assert.Equal(2, calls[2]);
        Assert.Equal(1, calls[3]);
    }

    [Fact]
    public void ReclamationBudgetCanSkipOversizedHeadAndAdmitSmallerCandidate()
    {
        Assert.False(ObjectMeshManager.FitsReclamationBudget(
            candidateBytes: 80,
            alreadyReclaimedBytes: 0,
            maximumBytes: 64));
        Assert.True(ObjectMeshManager.FitsReclamationBudget(
            candidateBytes: 16,
            alreadyReclaimedBytes: 0,
            maximumBytes: 64));
    }
}
