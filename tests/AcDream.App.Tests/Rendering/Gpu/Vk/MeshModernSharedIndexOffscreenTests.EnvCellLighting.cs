using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Packs;
using AcDream.Core.Lighting;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed unsafe partial class MeshModernSharedIndexOffscreenTests
{
    [Trait("Lane", "Vulkan")]
    [Theory]
    [InlineData("mesh_modern", false)]
    [InlineData("mesh_atmospheric", true)]
    public void CommittedProductionWorldShaders_UseEnvCell47AndOrdinary8LightStrides(
        string shaderName,
        bool atmospheric)
    {
        lock (VulkanLock)
        {
            string shaderDirectory = Path.Combine(
                RepositoryRoot(), "src", "AcDream.App", "Rendering", "Shaders", "spv");
            using var host = HeadlessVulkanHost.Create(shaderDirectory);

            (Pixel envFirst, Pixel envSecond) = RenderStridePair(
                host,
                shaderName,
                atmospheric,
                lightingMode: 1,
                firstSlot: 8,
                secondSlot: 46,
                stride: 47);
            (Pixel objectFirst, Pixel objectSecond) = RenderStridePair(
                host,
                shaderName,
                atmospheric,
                lightingMode: 0,
                firstSlot: 7,
                secondSlot: 7,
                stride: 8);

            AssertRed(envFirst, $"{shaderName} EnvCell slot 8");
            AssertGreen(envSecond, $"{shaderName} EnvCell slot 46 / instance stride 47");
            AssertRed(objectFirst, $"{shaderName} ordinary slot 7");
            AssertGreen(objectSecond, $"{shaderName} ordinary instance stride 8");
        }
    }

    private static (Pixel First, Pixel Second) RenderStridePair(
        HeadlessVulkanHost host,
        string shaderName,
        bool atmospheric,
        int lightingMode,
        int firstSlot,
        int secondSlot,
        int stride)
    {
        VulkanGpuDevice device = host.Device;
        using IGpuBuffer vertices = device.CreateBuffer(new GpuBufferDescription(
            $"478-{shaderName}-vertices",
            4 * Marshal.SizeOf<Vertex>(),
            GpuBufferUsage.Vertex | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));
        using IGpuBuffer indices = device.CreateBuffer(new GpuBufferDescription(
            $"478-{shaderName}-indices",
            6 * sizeof(ushort),
            GpuBufferUsage.Index | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));
        Vertex[] vertexData =
        [
            new(new Vector3(-0.30f, -0.30f, 0f), Vector3.UnitZ, Vector2.Zero),
            new(new Vector3( 0.30f, -0.30f, 0f), Vector3.UnitZ, Vector2.UnitX),
            new(new Vector3( 0.30f,  0.30f, 0f), Vector3.UnitZ, Vector2.One),
            new(new Vector3(-0.30f,  0.30f, 0f), Vector3.UnitZ, Vector2.UnitY),
        ];
        vertices.Upload(0, MemoryMarshal.AsBytes<Vertex>(vertexData));
        indices.Upload(0, MemoryMarshal.AsBytes<ushort>([0, 1, 2, 2, 3, 0]));

        using IGpuRenderTarget target = CreateTarget(device, $"478-{shaderName}-{lightingMode}");
        using IGpuPipeline pipeline = device.CreatePipeline(new GpuPipelineDescription
        {
            Name = $"478-{shaderName}-{lightingMode}",
            Shaders = new GpuShaderSet(shaderName),
            VertexLayout = GpuVertexLayout.WorldMesh,
            Topology = GpuPrimitiveTopology.TriangleList,
            Blend = GpuBlendMode.None,
            Depth = GpuDepthState.Disabled,
            Cull = GpuCullMode.None,
            UsesRenderPackShaderAbi = atmospheric,
            SampleCount = 1,
        });

        // Pad both modes to 94 entries. The ordinary path still reads only
        // offsets 7/15, while its stride-47 sabotage deterministically reads
        // in-bounds wrong data rather than invoking driver-dependent OOB access.
        var lightIndices = Enumerable.Repeat(
            -1,
            LightManager.MaxLightsPerEnvCell * 2).ToArray();
        lightIndices[firstSlot] = 0;
        lightIndices[stride + secondSlot] = 1;
        GlobalLight[] globalLights =
        [
            DynamicLight(new Vector3(-0.48f, 0f, 1f), Vector3.UnitX),
            DynamicLight(new Vector3( 0.48f, 0f, 1f), Vector3.UnitY),
        ];

        using (IGpuFrame frame = device.BeginFrame())
        {
            using IGpuPassEncoder encoder = BeginPass(frame, target, $"478-{shaderName}-stride");
            encoder.BindPipeline(pipeline);
            GpuPushConstants constants = GpuPushConstants.Default;
            constants.LightingMode = lightingMode;
            constants.LightDebug = 3;
            encoder.SetPushConstants(constants);

            BindStorage(frame, encoder, GpuBindingModel.StorageInstances,
            [
                Matrix4x4.CreateTranslation(-0.48f, 0f, 0f),
                Matrix4x4.CreateTranslation( 0.48f, 0f, 0f),
            ]);
            BindStorage(frame, encoder, GpuBindingModel.StorageBatches,
                [new BatchData(device.DefaultTextureSlot.Index, 1f, 0u, 0u)]);
            BindStorage(frame, encoder, GpuBindingModel.StorageClipSlots, [0u, 0u]);
            BindStorage(frame, encoder, GpuBindingModel.StorageGlobalLights, globalLights);
            BindStorage(frame, encoder, GpuBindingModel.StorageInstanceLightSets, lightIndices);
            BindStorage(frame, encoder, GpuBindingModel.StorageInstanceIndoor, [1u, 1u]);
            BindStorage(frame, encoder, GpuBindingModel.StorageInstanceAlpha, [1f, 1f]);
            BindStorage(frame, encoder, GpuBindingModel.StorageInstanceSelectionLighting,
                [new Vector2(0f, 1f), new Vector2(0f, 1f)]);
            BindStorage(frame, encoder, GpuBindingModel.StorageInstanceDetailCategory, [0u, 0u]);
            BindUniform(frame, encoder, GpuBindingModel.UniformSceneLighting, default(SceneLightingUbo));
            if (atmospheric)
            {
                BindUniform(frame, encoder, GpuBindingModel.UniformAtmosphericFrame,
                    default(AtmosphericFrameUniforms));
                BindUniform(frame, encoder, GpuBindingModel.UniformDirectionalShadow,
                    ShadowUniforms(device.DefaultTextureSlot, enabled: false, -Vector3.UnitY));
            }

            encoder.BindVertexBuffer(0, vertices, 0);
            encoder.BindIndexBuffer(indices, 0, GpuIndexType.UInt16);
            encoder.DrawIndexed(6, 2, 0, 0, 0);
        }

        device.WaitIdle();
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

    private static GlobalLight DynamicLight(Vector3 position, Vector3 color) => new(
        new Vector4(position, (float)LightKind.Point),
        new Vector4(0f, 0f, 1f, 10f),
        new Vector4(color, 1f),
        new Vector4(0f, 1f, 0f, 0f));

    private static void AssertRed(Pixel pixel, string label)
    {
        Assert.True(pixel.R < 8 && pixel.G < 8 && pixel.B > 180,
            $"{label}: rgba={pixel.R},{pixel.G},{pixel.B},{pixel.A}");
    }

    private static void AssertGreen(Pixel pixel, string label)
    {
        Assert.True(pixel.R < 8 && pixel.G > 180 && pixel.B < 8,
            $"{label}: rgba={pixel.R},{pixel.G},{pixel.B},{pixel.A}");
    }
}
