using System;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Rendering.Gpu;
using System.Threading;
using AcDream.Content;
using Chorizite.Core.Render.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class MeshPipelineDeviceSeamTests
{
    /// <summary>A device with the mesh pipeline's whole surface and no GL behind it.</summary>
    private sealed class ContextFreeMeshPipelineDevice(
        IGpuResourceRetirementQueue retirement,
        bool modernPath = false)
        : IMeshPipelineDevice
    {
        public IGpuResourceRetirementQueue ResourceRetirement { get; } = retirement;

        public uint InstanceVBO => 0;

        public bool HasBindless => modernPath;

        public bool HasOpenGL43 => modernPath;

        public bool HasPendingWork => false;

        public int ProcessedQueues { get; private set; }

        public void ProcessQueue() => ProcessedQueues++;

        public void Dispose()
        {
        }
    }

    private static ObjectMeshManager Build(
        RecordingGpuDevice device,
        bool modernPath = false) =>
        new(
            new ContextFreeMeshPipelineDevice(device.Retirement, modernPath),
            device,
            new NullPreparedAssetSource(),
            NullLogger<ObjectMeshManager>.Instance);

    private sealed class NullPreparedAssetSource : IPreparedAssetSource
    {
        public PreparedAssetSourceStats Stats => default;

        public CacheStats DecodedTextureCacheStats => default;

        public PreparedAssetPresence Probe(
            AcDream.Content.Pak.PakAssetType type,
            uint sourceFileId) =>
            PreparedAssetPresence.Missing;

        public PreparedAssetReadResult Read(
            in PreparedAssetRequest request,
            CancellationToken cancellationToken = default) =>
            PreparedAssetReadResult.Missing;

        public void Dispose()
        {
        }
    }

    [Fact]
    public void TheMeshPipelineConstructsAgainstADeviceWithNoGlContext()
    {
        using var device = new RecordingGpuDevice();
        using ObjectMeshManager manager = Build(device);

        Assert.False(manager.IsDisposed);
    }

    [Fact]
    public void TheArrayFactorySelectsTheRhiArmWithoutAGlPair()
    {
        using var device = new RecordingGpuDevice();
        IWorldTextureArrayFactory arrays = IWorldTextureArrayFactory.For(
            new ContextFreeMeshPipelineDevice(device.Retirement),
            device,
            NullLogger.Instance);

        Assert.IsType<RhiWorldTextureArrayFactory>(arrays);
        using IWorldTextureArray array =
            arrays.CreateClampedArray(TextureFormat.RGBA8, 32, 32, 2);
        Assert.IsType<RhiWorldTextureArray>(array);
    }

    [Fact]
    public void TheDeviceSeamStaysAtTheMeasuredSurface()
    {
        string[] members =
        [
            .. typeof(IMeshPipelineDevice)
                .GetMembers()
                // Property accessors are the same members under another name.
                .Where(member => member is not System.Reflection.MethodInfo
                {
                    IsSpecialName: true,
                })
                .Select(member => member.Name)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(
            [
                "HasBindless",
                "HasOpenGL43",
                "HasPendingWork",
                "InstanceVBO",
                "ProcessQueue",
                "ResourceRetirement",
            ],
            members);
    }

    [Fact]
    public void ConstructionBuildsNoGlObject()
    {
        using var device = new RecordingGpuDevice();
        using ObjectMeshManager manager = Build(device);

        Assert.Null(manager.GlobalBuffer);
        // Read-only policy queries still answer, which is what lets streaming
        // residence accounting keep running on a backend with no world draws.
        Assert.Equal((0, 0, 0), manager.GetPendingTextureUpdateStats());
    }

    [Fact]
    public void TheModernArenaBuildsWithoutAGlContext()
    {
        using var device = new RecordingGpuDevice();
        using ObjectMeshManager manager = Build(device, modernPath: true);

        GlobalMeshBuffer arena = Assert.IsType<GlobalMeshBuffer>(manager.GlobalBuffer);
        Assert.True(arena.HasStores);
        Assert.NotNull(arena.VertexStore);
        Assert.NotNull(arena.IndexStore);
    }

    [Fact]
    public void AMeshUploadsIntoTheArenaWithoutAGlContext()
    {
        using var device = new RecordingGpuDevice();
        using ObjectMeshManager manager = Build(device, modernPath: true);
        GlobalMeshBuffer arena = manager.GlobalBuffer!;

        var vertices = new VertexPositionNormalTexture[3];
        vertices[0].Position = new System.Numerics.Vector3(1f, 2f, 3f);
        vertices[2].Position = new System.Numerics.Vector3(7f, 8f, 9f);
        ushort[] indices = [0, 1, 2];

        GlobalMeshAllocation allocation = arena.UploadMesh(
            vertices,
            indices,
            [(0, indices.Length)]);

        Assert.Equal(3, allocation.Vertices.Length);
        Assert.Equal(3, allocation.Indices.Length);
        Assert.Equal(1, arena.UploadCount);

        Span<byte> readback = stackalloc byte[3 * VertexPositionNormalTexture.Size];
        arena.VertexStore!.Read(
            (long)allocation.Vertices.Offset * VertexPositionNormalTexture.Size,
            readback);
        var uploaded = System.Runtime.InteropServices.MemoryMarshal
            .Cast<byte, VertexPositionNormalTexture>(readback);
        Assert.Equal(new System.Numerics.Vector3(1f, 2f, 3f), uploaded[0].Position);
        Assert.Equal(new System.Numerics.Vector3(7f, 8f, 9f), uploaded[2].Position);

        Span<byte> indexBytes = stackalloc byte[3 * sizeof(ushort)];
        arena.IndexStore!.Read((long)allocation.Indices.Offset * sizeof(ushort), indexBytes);
        Assert.Equal(
            indices,
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(indexBytes).ToArray());
    }

    [Fact]
    public void CellShellBatchesKeepTheirOwnIndexRangesWhenReorderedBySurfaceIndex()
    {
        using var device = new RecordingGpuDevice();
        using ObjectMeshManager manager = Build(device, modernPath: true);
        GlobalMeshBuffer arena = manager.GlobalBuffer!;

        static AcDream.Content.TextureBatchData CellBatch(int slot, uint surfaceId, int size, ushort[] indices) => new()
        {
            Key = new AcDream.Content.TextureKey { SurfaceId = surfaceId },
            TextureData = new byte[size * size * 4],
            Indices = [.. indices],
            IsCellShell = true,
            SourceSurfaceIndex = slot,
            CullMode = DatReaderWriter.Enums.CullMode.Clockwise,
        };

        var mesh = new AcDream.Content.ObjectMeshData
        {
            ObjectId = 0x1_0000_0000UL | 0xF4180104UL,
            Vertices = new VertexPositionNormalTexture[6],
            TextureBatches =
            {
                [(8, 8, Chorizite.Core.Render.Enums.TextureFormat.RGBA8)] = [CellBatch(5, 0x08000005u, 8, [0, 1, 2])],
                [(16, 16, Chorizite.Core.Render.Enums.TextureFormat.RGBA8)] = [CellBatch(2, 0x08000002u, 16, [3, 4, 5])],
            },
        };

        ObjectRenderData data = Assert.IsType<ObjectRenderData>(manager.UploadMeshData(mesh));

        Assert.Equal(2, data.Batches.Count);
        Assert.Equal(0x08000002u, data.Batches[0].Key.SurfaceId); // ascending surface index wins
        Assert.Equal(0x08000005u, data.Batches[1].Key.SurfaceId);

        foreach (ObjectRenderBatch batch in data.Batches)
        {
            ushort[] expected = batch.Key.SurfaceId == 0x08000002u ? [3, 4, 5] : [0, 1, 2];
            var bytes = new byte[batch.IndexCount * sizeof(ushort)];
            arena.IndexStore!.Read((long)batch.FirstIndex * sizeof(ushort), bytes);
            Assert.Equal(
                expected,
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(bytes).ToArray());
        }
    }

    [Fact]
    public void AWarmedMeshCompletionAllocatesNearItsRetainedCopySize()
    {
        using var device = new RecordingGpuDevice();
        using ObjectMeshManager manager = Build(device, modernPath: true);

        Assert.NotNull(manager.UploadMeshData(
            CreateLargeMeshData(0x0100AA01u, surfaceSeed: 0x08000000u)));

        ObjectMeshData meshData =
            CreateLargeMeshData(0x0100AA02u, surfaceSeed: 0x08001000u);
        long before = GC.GetAllocatedBytesForCurrentThread();
        ObjectRenderData? uploaded = manager.UploadMeshData(meshData);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(uploaded);
        long retained =
            (long)uploaded!.CPUIndices.Length * sizeof(ushort)
            + (long)uploaded.CPUPositions.Length * 3 * sizeof(float);
        // Sanity: the fixture is actually index-heavy enough to discriminate.
        Assert.True(retained >= 480_000, $"fixture retained only {retained} bytes");
        // The LINQ regression allocates over 3x the index bytes and fails
        // this bound by more than a megabyte.
        long bound = retained + retained / 2 + 128 * 1024;
        Assert.True(
            allocated < bound,
            $"A warmed mesh completion allocated {allocated} bytes "
            + $"(retained copies {retained}, bound {bound}).");
    }

    private static ObjectMeshData CreateLargeMeshData(ulong id, uint surfaceSeed)
    {
        const int vertexCount = 1024;
        const int batchCount = 4;
        const int indicesPerBatch = 60_000;
        var data = new ObjectMeshData
        {
            ObjectId = id,
            Vertices = new VertexPositionNormalTexture[vertexCount],
        };
        var batches = new System.Collections.Generic.List<TextureBatchData>(batchCount);
        for (int b = 0; b < batchCount; b++)
        {
            var indices = new System.Collections.Generic.List<ushort>(indicesPerBatch);
            for (int i = 0; i < indicesPerBatch; i++)
                indices.Add((ushort)((i + b) % vertexCount));
            batches.Add(new TextureBatchData
            {
                Key = new TextureKey { SurfaceId = surfaceSeed + (uint)b },
                TextureData = new byte[8 * 8 * 4],
                Indices = indices,
            });
        }
        data.TextureBatches[(8, 8, TextureFormat.RGBA8)] = batches;
        return data;
    }

    [Fact]
    public void TheVulkanMeshPipelineDeviceReportsTheModernPath()
    {
        using var device = new RecordingGpuDevice();
        using var vulkanDevice =
            new AcDream.App.Rendering.Gpu.Vk.VulkanMeshPipelineDevice(device.Retirement);

        Assert.True(vulkanDevice.HasBindless);
        Assert.True(vulkanDevice.HasOpenGL43);
        Assert.False(vulkanDevice.HasPendingWork);
        Assert.Equal(0u, vulkanDevice.InstanceVBO);
        Assert.Same(device.Retirement, vulkanDevice.ResourceRetirement);
    }
}
