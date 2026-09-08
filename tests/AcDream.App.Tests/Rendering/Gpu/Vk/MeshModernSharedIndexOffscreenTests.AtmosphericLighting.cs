using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Packs;
using AcDream.Core.Lighting;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed unsafe partial class MeshModernSharedIndexOffscreenTests
{
    [Trait("Lane", "Vulkan")]
    [Fact]
    public void CommittedProductionAtmosphericReceivers_KeepAuthoredLightAcrossShadowGate()
    {
        lock (VulkanLock)
        {
            string shaderDirectory = Path.Combine(
                RepositoryRoot(), "src", "AcDream.App", "Rendering", "Shaders", "spv");
            using var host = HeadlessVulkanHost.Create(shaderDirectory);

            (Pixel meshOff, Pixel meshOn) = RenderMeshLightingPair(host);
            (Pixel terrainOff, Pixel terrainOn) = RenderTerrainLightingPair(host);

            Assert.Equal(meshOff, meshOn);
            Assert.Equal(terrainOff, terrainOn);
            AssertAuthoredHalfIntensity(meshOff, "mesh");
            AssertAuthoredHalfIntensity(terrainOff, "terrain");
        }
    }

    [Fact]
    public void AtmosphericLightingPixelWitness_IsOwnedByTheDedicatedVulkanLane()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "tests", "AcDream.App.Tests", "Rendering", "Gpu", "Vk",
            "MeshModernSharedIndexOffscreenTests.AtmosphericLighting.cs"));
        Assert.Matches(
            new Regex(
                @"\[Trait\(""Lane"", ""Vulkan""\)\]\s*"
                    + @"\[Fact\]\s*public void CommittedProductionAtmosphericReceivers_KeepAuthoredLightAcrossShadowGate",
                RegexOptions.Singleline),
            source);
        Assert.Equal(1, Count(source, "[Trait(\"Lane\", \"Vulkan\")]"));
    }

    private static (Pixel Disabled, Pixel Enabled) RenderMeshLightingPair(HeadlessVulkanHost host)
    {
        VulkanGpuDevice device = host.Device;
        using IGpuBuffer vertices = device.CreateBuffer(new GpuBufferDescription(
            "s5-469-mesh-vertices",
            4 * Marshal.SizeOf<Vertex>(),
            GpuBufferUsage.Vertex | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));
        using IGpuBuffer indices = device.CreateBuffer(new GpuBufferDescription(
            "s5-469-mesh-indices",
            6 * sizeof(ushort),
            GpuBufferUsage.Index | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));
        Vertex[] vertexData =
        [
            new(new Vector3(-0.32f, -0.32f, 0f), Vector3.UnitZ, Vector2.Zero),
            new(new Vector3( 0.32f, -0.32f, 0f), Vector3.UnitZ, Vector2.UnitX),
            new(new Vector3( 0.32f,  0.32f, 0f), Vector3.UnitZ, Vector2.One),
            new(new Vector3(-0.32f,  0.32f, 0f), Vector3.UnitZ, Vector2.UnitY),
        ];
        vertices.Upload(0, MemoryMarshal.AsBytes<Vertex>(vertexData));
        indices.Upload(0, MemoryMarshal.AsBytes<ushort>([0, 1, 2, 2, 3, 0]));

        using IGpuRenderTarget target = CreateTarget(device, "s5-469-mesh-offscreen");
        using IGpuPipeline pipeline = device.CreatePipeline(new GpuPipelineDescription
        {
            Name = "s5-469-mesh-atmospheric",
            Shaders = new GpuShaderSet("mesh_atmospheric"),
            VertexLayout = GpuVertexLayout.WorldMesh,
            Topology = GpuPrimitiveTopology.TriangleList,
            Blend = GpuBlendMode.None,
            Depth = GpuDepthState.Disabled,
            Cull = GpuCullMode.None,
            UsesRenderPackShaderAbi = true,
            SampleCount = 1,
        });
        Assert.False(pipeline.Description.Shaders.HasEmbeddedSpirv);
        Assert.Equal("mesh_atmospheric", pipeline.Description.Shaders.Name);

        using (IGpuFrame frame = device.BeginFrame())
        {
            using IGpuPassEncoder encoder = BeginPass(frame, target, "s5-469-mesh-lighting");
            encoder.BindPipeline(pipeline);
            encoder.SetPushConstants(GpuPushConstants.Default);

            BindStorage(frame, encoder, GpuBindingModel.StorageInstances,
            [
                Matrix4x4.CreateTranslation(-0.48f, 0f, 0f),
                Matrix4x4.CreateTranslation( 0.48f, 0f, 0f),
            ]);
            BindStorage(frame, encoder, GpuBindingModel.StorageBatches,
                [new BatchData(device.DefaultTextureSlot.Index, 1f, 0u, 0u)]);
            BindStorage(frame, encoder, GpuBindingModel.StorageClipSlots, [0u, 0u]);
            BindStorage(frame, encoder, GpuBindingModel.StorageGlobalLights, [GlobalLight.Zero]);
            BindStorage(frame, encoder, GpuBindingModel.StorageInstanceLightSets,
                Enumerable.Repeat(-1, 16).ToArray());
            BindStorage(frame, encoder, GpuBindingModel.StorageInstanceIndoor, [0u, 0u]);
            BindStorage(frame, encoder, GpuBindingModel.StorageInstanceAlpha, [1f, 1f]);
            BindStorage(frame, encoder, GpuBindingModel.StorageInstanceSelectionLighting,
                [new Vector2(0f, 1f), new Vector2(0f, 1f)]);
            BindStorage(frame, encoder, GpuBindingModel.StorageInstanceDetailCategory, [0u, 0u]);
            BindUniform(frame, encoder, GpuBindingModel.UniformSceneLighting, AuthoredHalfLight());
            BindUniform(frame, encoder, GpuBindingModel.UniformAtmosphericFrame, default(AtmosphericFrameUniforms));

            encoder.BindVertexBuffer(0, vertices, 0);
            encoder.BindIndexBuffer(indices, 0, GpuIndexType.UInt16);
            BindUniform(frame, encoder, GpuBindingModel.UniformDirectionalShadow,
                ShadowUniforms(device.DefaultTextureSlot, enabled: false, -Vector3.UnitY));
            encoder.DrawIndexed(6, 1, 0, 0, 0);
            BindUniform(frame, encoder, GpuBindingModel.UniformDirectionalShadow,
                ShadowUniforms(device.DefaultTextureSlot, enabled: true, Vector3.UnitX));
            encoder.DrawIndexed(6, 1, 0, 0, 1);
        }

        device.WaitIdle();
        return ReadLightingPair(host, target);
    }

    private static (Pixel Disabled, Pixel Enabled) RenderTerrainLightingPair(HeadlessVulkanHost host)
    {
        VulkanGpuDevice device = host.Device;
        TerrainVertex[] terrain =
        [
            .. TerrainQuad(-0.48f),
            .. TerrainQuad(0.48f),
        ];
        using IGpuBuffer vertices = device.CreateBuffer(new GpuBufferDescription(
            "s5-469-terrain-vertices",
            terrain.Length * Marshal.SizeOf<TerrainVertex>(),
            GpuBufferUsage.Vertex | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));
        vertices.Upload(0, MemoryMarshal.AsBytes<TerrainVertex>(terrain));

        using IGpuRenderTarget target = CreateTarget(device, "s5-469-terrain-offscreen");
        using IGpuPipeline pipeline = device.CreatePipeline(new GpuPipelineDescription
        {
            Name = "s5-469-terrain-atmospheric",
            Shaders = new GpuShaderSet("terrain_atmospheric"),
            VertexLayout = TerrainModernRenderer.TerrainVertexLayout,
            Topology = GpuPrimitiveTopology.TriangleList,
            Blend = GpuBlendMode.None,
            Depth = GpuDepthState.Disabled,
            Cull = GpuCullMode.None,
            UsesRenderPackShaderAbi = true,
            SampleCount = 1,
        });
        Assert.False(pipeline.Description.Shaders.HasEmbeddedSpirv);
        Assert.Equal("terrain_atmospheric", pipeline.Description.Shaders.Name);

        using (IGpuFrame frame = device.BeginFrame())
        {
            using IGpuPassEncoder encoder = BeginPass(frame, target, "s5-469-terrain-lighting");
            encoder.BindPipeline(pipeline);
            GpuPushConstants constants = GpuPushConstants.Default;
            constants.TextureIndexA = device.DefaultTextureSlot.Index;
            constants.TextureIndexB = device.DefaultTextureSlot.Index;
            encoder.SetPushConstants(constants);
            BindUniform(frame, encoder, GpuBindingModel.UniformSceneLighting, AuthoredHalfLight());
            BindTerrainTiling(frame, encoder);
            encoder.BindVertexBuffer(0, vertices, 0);

            BindUniform(frame, encoder, GpuBindingModel.UniformDirectionalShadow,
                ShadowUniforms(device.DefaultTextureSlot, enabled: false, -Vector3.UnitY));
            encoder.Draw(6, 1, 0, 0);
            BindUniform(frame, encoder, GpuBindingModel.UniformDirectionalShadow,
                ShadowUniforms(device.DefaultTextureSlot, enabled: true, Vector3.UnitX));
            encoder.Draw(6, 1, 6, 0);
        }

        device.WaitIdle();
        return ReadLightingPair(host, target);
    }

    private static IGpuRenderTarget CreateTarget(VulkanGpuDevice device, string name) =>
        device.CreateRenderTarget(new GpuRenderTargetDescription(
            name,
            Extent,
            Extent,
            GpuTextureFormat.Rgba8UnormRenderTarget,
            DepthFormat: null,
            SampleCount: 1));

    private static IGpuPassEncoder BeginPass(IGpuFrame frame, IGpuRenderTarget target, string name) =>
        frame.BeginPass(new GpuPassDescription
        {
            Name = name,
            Color = new GpuColorAttachment(
                target,
                GpuLoadOp.Clear,
                GpuStoreOp.Store,
                new Vector4(0f, 0f, 0f, 1f)),
            Depth = null,
            SampleCount = 1,
        });

    private static SceneLightingUbo AuthoredHalfLight() => new()
    {
        Light0 = new UboLight
        {
            PosAndKind = new Vector4(0f, 0f, 0f, 0f),
            DirAndRange = new Vector4(0f, 0f, -0.5f, 0f),
            ColorAndIntensity = Vector4.One,
            ConeAngleEtc = Vector4.Zero,
        },
        CellAmbient = new Vector4(0f, 0f, 0f, 1f),
    };

    private static DirectionalShadowUniforms ShadowUniforms(
        GpuTextureSlot texture,
        bool enabled,
        Vector3 celestialSurfaceToLight) => new(
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector4.Zero,
            new Vector4(0f, 1f, 0f, 1f),
            Vector4.Zero,
            new UInt4(texture.Index, 0u, 1u, enabled ? 1u : 0u),
            new Vector4(celestialSurfaceToLight, 1f));

    private static void BindTerrainTiling(IGpuFrame frame, IGpuPassEncoder encoder)
    {
        GpuRingAllocation allocation = frame.AllocateRing(
            TerrainTextureTilingTable.UniformBufferBytes,
            GpuRingUsage.Uniform);
        allocation.Data.Clear();
        allocation.AsSpan<float>()[0] = 1f;
        encoder.BindUniformBuffer(
            GpuBindingModel.UniformTerrainTiling,
            allocation.Buffer,
            allocation.OffsetBytes,
            (uint)allocation.Data.Length);
    }

    private static TerrainVertex[] TerrainQuad(float x) =>
    [
        new(new Vector3(x - 0.32f, -0.32f, 0.5f)),
        new(new Vector3(x + 0.32f, -0.32f, 0.5f)),
        new(new Vector3(x + 0.32f,  0.32f, 0.5f)),
        new(new Vector3(x - 0.32f, -0.32f, 0.5f)),
        new(new Vector3(x + 0.32f,  0.32f, 0.5f)),
        new(new Vector3(x - 0.32f,  0.32f, 0.5f)),
    ];

    private static (Pixel Disabled, Pixel Enabled) ReadLightingPair(
        HeadlessVulkanHost host,
        IGpuRenderTarget target)
    {
        VulkanGpuRenderTarget vkTarget = Assert.IsType<VulkanGpuRenderTarget>(target);
        byte[] pixels = ReadBack(
            host.Vk,
            host.PhysicalDevice,
            host.LogicalDevice,
            host.Queue,
            host.QueueFamily,
            vkTarget.ColorResult.Image);
        return (PixelAt(pixels, 16, 32), PixelAt(pixels, 48, 32));
    }

    private static Pixel PixelAt(byte[] pixels, int x, int y)
    {
        int offset = ((y * Extent) + x) * 4;
        return new Pixel(pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }

    private static void AssertAuthoredHalfIntensity(Pixel pixel, string receiver)
    {
        Assert.InRange(pixel.R, (byte)126, (byte)129);
        Assert.InRange(pixel.G, (byte)126, (byte)129);
        Assert.InRange(pixel.B, (byte)126, (byte)129);
        Assert.True(pixel.A >= 253, $"{receiver} alpha was {pixel.A}, expected opaque.");
    }

    private static int Count(string text, string token)
    {
        int count = 0;
        for (int index = 0; (index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0;)
        {
            count++;
            index += token.Length;
        }
        return count;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct TerrainVertex(
        Vector3 Position,
        Vector3 Normal,
        uint Packed0,
        uint Packed1,
        uint Packed2,
        uint Packed3)
    {
        internal TerrainVertex(Vector3 position)
            : this(position, Vector3.UnitZ, 0xFFFF_FF00u, uint.MaxValue, uint.MaxValue, 0u)
        {
        }
    }

    private readonly record struct Pixel(byte R, byte G, byte B, byte A);
}
