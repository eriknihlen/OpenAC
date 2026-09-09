using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;

namespace AcDream.App.Tests.Rendering.Gpu;

public sealed class GpuContractTests
{
    [Fact]
    public void PushConstantBlockMatchesThePinnedLayout()
    {
        Assert.Equal(GpuBindingModel.PushConstantBytes, Unsafe.SizeOf<GpuPushConstants>());
        Assert.True(GpuBindingModel.PushConstantBytes <= GpuBindingModel.MaxPushConstantBytes);

        Assert.Equal(0, (int)Marshal.OffsetOf<GpuPushConstants>(nameof(GpuPushConstants.ViewProjection)));
        Assert.Equal(64, (int)Marshal.OffsetOf<GpuPushConstants>(nameof(GpuPushConstants.DrawIdOffset)));
        Assert.Equal(68, (int)Marshal.OffsetOf<GpuPushConstants>(nameof(GpuPushConstants.LightingMode)));
        Assert.Equal(72, (int)Marshal.OffsetOf<GpuPushConstants>(nameof(GpuPushConstants.RenderPass)));
        Assert.Equal(76, (int)Marshal.OffsetOf<GpuPushConstants>(nameof(GpuPushConstants.LightDebug)));
        Assert.Equal(80, (int)Marshal.OffsetOf<GpuPushConstants>(nameof(GpuPushConstants.TextureIndexA)));
        Assert.Equal(84, (int)Marshal.OffsetOf<GpuPushConstants>(nameof(GpuPushConstants.TextureIndexB)));
        Assert.Equal(88, (int)Marshal.OffsetOf<GpuPushConstants>(nameof(GpuPushConstants.ParamA)));
        Assert.Equal(92, (int)Marshal.OffsetOf<GpuPushConstants>(nameof(GpuPushConstants.ParamB)));
    }

    [Fact]
    public void StorageBindingsMatchTheShaderSources()
    {
        Assert.Equal(0u, GpuBindingModel.StorageInstances);
        Assert.Equal(1u, GpuBindingModel.StorageBatches);
        Assert.Equal(2u, GpuBindingModel.StorageClipRegions);
        Assert.Equal(3u, GpuBindingModel.StorageClipSlots);
        Assert.Equal(4u, GpuBindingModel.StorageGlobalLights);
        Assert.Equal(5u, GpuBindingModel.StorageInstanceLightSets);
        Assert.Equal(6u, GpuBindingModel.StorageInstanceIndoor);
        Assert.Equal(7u, GpuBindingModel.StorageInstanceAlpha);
        Assert.Equal(8u, GpuBindingModel.StorageInstanceSelectionLighting);
        Assert.Equal(9u, GpuBindingModel.StorageInstanceDetailCategory);
        Assert.Equal(10u, GpuBindingModel.StorageBindingCount);
    }

    [Fact]
    public void UniformAndTextureTableLiveInSeparateSets()
    {
        // The SceneLighting UBO keeps binding=1 even though the BatchBuffer SSBO
        // also uses binding=1. GL tolerates that because its SSBO and UBO binding
        // tables are separate; Vulkan does not, so the set index disambiguates.
        Assert.Equal(GpuBindingModel.StorageBatches, GpuBindingModel.UniformSceneLighting);
        Assert.NotEqual(0u, GpuBindingModel.UniformSet);
        Assert.NotEqual(GpuBindingModel.UniformSet, GpuBindingModel.TextureTableSet);
    }

    [Fact]
    public void ClipRegionStrideMatchesTheUploadedLayout()
    {
        Assert.Equal(8, GpuBindingModel.ClipPlanesPerSlot);
        Assert.Equal(144, GpuBindingModel.ClipRegionStrideBytes);
    }

    [Fact]
    public void BlendModesCoverEveryRetailRendererState()
    {
        Assert.Equal(7, Enum.GetValues<GpuBlendMode>().Length);
        Assert.Contains(GpuBlendMode.None, Enum.GetValues<GpuBlendMode>());
        Assert.Contains(GpuBlendMode.StraightAlpha, Enum.GetValues<GpuBlendMode>());
        Assert.Contains(GpuBlendMode.PremultipliedAlpha, Enum.GetValues<GpuBlendMode>());
        Assert.Contains(GpuBlendMode.Additive, Enum.GetValues<GpuBlendMode>());
        Assert.Contains(GpuBlendMode.RawAdditive, Enum.GetValues<GpuBlendMode>());
        Assert.Contains(GpuBlendMode.InverseAlpha, Enum.GetValues<GpuBlendMode>());
        Assert.Contains(GpuBlendMode.InverseAdditive, Enum.GetValues<GpuBlendMode>());
    }

    [Fact]
    public void IntegerVertexAttributesAreRepresentableDistinctlyFromNormalizedOnes()
    {
        Assert.NotEqual(GpuVertexFormat.UByte4Normalized, GpuVertexFormat.UByte4UInt);
        Assert.Contains(GpuVertexFormat.UByte4UInt, Enum.GetValues<GpuVertexFormat>());
    }

    [Fact]
    public void AVertexLayoutCanDeclareAPerInstanceBinding()
    {
        var layout = new GpuVertexLayout(
            [
                new GpuVertexBinding(0, 16, GpuVertexInputRate.Vertex),
                new GpuVertexBinding(1, 68, GpuVertexInputRate.Instance),
            ],
            [
                new GpuVertexAttribute(0, GpuVertexFormat.Float2, 0),
                new GpuVertexAttribute(2, GpuVertexFormat.Float4, 0, Binding: 1),
            ]);

        Assert.Equal(16u, layout.StrideOf(0));
        Assert.Equal(68u, layout.StrideOf(1));
        Assert.Equal(GpuVertexInputRate.Vertex, layout.InputRateOf(0));
        Assert.Equal(GpuVertexInputRate.Instance, layout.InputRateOf(1));

        Assert.Throws<ArgumentOutOfRangeException>(() => layout.StrideOf(2));
    }

    [Fact]
    public void ASingleBindingLayoutStillMeansOneInterleavedVertexRateBuffer()
    {
        Assert.Equal(0u, new GpuVertexAttribute(3, GpuVertexFormat.Float3, 12).Binding);

        GpuVertexLayout world = GpuVertexLayout.WorldMesh;
        GpuVertexBinding only = Assert.Single(world.Bindings);
        Assert.Equal(0u, only.Binding);
        Assert.Equal(GpuVertexInputRate.Vertex, only.InputRate);
        Assert.Equal(world.StrideBytes, only.StrideBytes);

        // And a buffer-fed pipeline declares no binding at all.
        Assert.Empty(GpuVertexLayout.None.Bindings);
        Assert.Empty(GpuVertexLayout.None.Attributes);
        Assert.Equal(0u, GpuVertexLayout.None.StrideBytes);
    }

    [Fact]
    public void ScalarIntegerVertexAttributesAreRepresentable()
    {
        Assert.Contains(GpuVertexFormat.UInt1, Enum.GetValues<GpuVertexFormat>());
        Assert.NotEqual(GpuVertexFormat.Float1, GpuVertexFormat.UInt1);
    }

    [Fact]
    public void APipelineNamesTheColorFormatItRendersInto()
    {
        var description = new GpuPipelineDescription
        {
            Name = "contract-default",
            Shaders = new GpuShaderSet("ui_text"),
            VertexLayout = GpuVertexLayout.None,
        };

        // The default has to be the render-target format, because that is what
        // the Vulkan backend already maps to the B8G8R8A8_UNORM swapchain — so
        // every pipeline written before this field existed keeps its behaviour.
        Assert.Equal(GpuTextureFormat.Rgba8UnormRenderTarget, description.ColorFormat);

        // And it has to be settable, or naming it would be decoration.
        GpuPipelineDescription single = description with { ColorFormat = GpuTextureFormat.R8Unorm };
        Assert.Equal(GpuTextureFormat.R8Unorm, single.ColorFormat);
        Assert.Equal(GpuTextureFormat.Rgba8UnormRenderTarget, description.ColorFormat);
    }

    [Fact]
    public void APipelineCanDeclareThatItUsesTheStencilAspect()
    {
        var description = new GpuPipelineDescription
        {
            Name = "contract-stencil",
            Shaders = new GpuShaderSet("portal_depth"),
            VertexLayout = GpuVertexLayout.None,
        };

        Assert.False(description.StencilTest);
        Assert.Equal(GpuStencilState.Default, description.Stencil);
        Assert.Equal(GpuCompareOp.Always, GpuStencilState.Default.Compare);
        Assert.Equal(GpuStencilOp.Keep, GpuStencilState.Default.Pass);

        GpuPipelineDescription punch = description with
        {
            StencilTest = true,
            Stencil = GpuStencilState.Default with
            {
                Compare = GpuCompareOp.Equal,
                Pass = GpuStencilOp.Zero,
                Reference = 1,
            },
        };
        Assert.True(punch.StencilTest);
        Assert.Equal(GpuCompareOp.Equal, punch.Stencil.Compare);
        Assert.Equal(GpuStencilOp.Zero, punch.Stencil.Pass);
        Assert.False(description.StencilTest);
    }

    [Fact]
    public void EveryStencilOperationThePortalPunchNeedsIsRepresentable()
    {
        Assert.Equal(3, Enum.GetValues<GpuStencilOp>().Length);
        Assert.Contains(GpuStencilOp.Keep, Enum.GetValues<GpuStencilOp>());
        Assert.Contains(GpuStencilOp.Zero, Enum.GetValues<GpuStencilOp>());
        Assert.Contains(GpuStencilOp.Replace, Enum.GetValues<GpuStencilOp>());
    }

    [Fact]
    public void BackbufferClear_ClearsDepthAndStencil()
    {
        GpuPassDescription pass = GpuPassDescription.BackbufferClear(
            "world",
            Vector4.Zero,
            sampleCount: 4);
        Assert.Equal(0u, pass.Depth!.Value.ClearStencil);
        Assert.Equal(1f, pass.Depth.Value.ClearDepth);
        Assert.Equal(GpuLoadOp.Clear, pass.Depth!.Value.Load);
    }

    [Fact]
    public void DepthStencilTextureFormat_MapsToDepthAndStencilAspects()
    {
        Silk.NET.Vulkan.ImageAspectFlags aspects =
            VulkanTextureFormatMapping.AspectOf(GpuTextureFormat.Depth24Stencil8);

        Assert.True(aspects.HasFlag(Silk.NET.Vulkan.ImageAspectFlags.DepthBit));
        Assert.True(aspects.HasFlag(Silk.NET.Vulkan.ImageAspectFlags.StencilBit));
    }

    [Fact]
    public void Rgba16FloatRenderTarget_HasExactVulkanFormatAndByteAccounting()
    {
        Assert.Equal(
            Silk.NET.Vulkan.Format.R16G16B16A16Sfloat,
            VulkanTextureFormatMapping.FormatOf(
                GpuTextureFormat.Rgba16FloatRenderTarget));
        Assert.True(VulkanTextureFormatMapping.IsRenderTarget(
            GpuTextureFormat.Rgba16FloatRenderTarget));
        Assert.Equal(
            8,
            VulkanTextureFormatMapping.BytesPerTexel(
                GpuTextureFormat.Rgba16FloatRenderTarget));
        Assert.Equal(
            1920 * 1080 * 8,
            VulkanTextureFormatMapping.LevelSizeBytes(
                GpuTextureFormat.Rgba16FloatRenderTarget,
                1920,
                1080));
    }

    [Fact]
    public void UniformBindingsDoNotCollide()
    {
        uint[] uniformBindings =
        [
            GpuBindingModel.UniformSceneLighting,
            GpuBindingModel.UniformTerrainTiling,
            GpuBindingModel.UniformSkyParams,
        ];

        Assert.Equal(uniformBindings.Length, uniformBindings.Distinct().Count());

        Assert.DoesNotContain(2u, uniformBindings);
    }

    [Fact]
    public void UnassignedTextureSlotIsNeverAValidIndex()
    {
        Assert.False(GpuTextureSlot.Unassigned.IsAssigned);
        Assert.True(new GpuTextureSlot(0).IsAssigned);
        Assert.Equal("slot#unassigned", GpuTextureSlot.Unassigned.ToString());
        Assert.Equal("slot#7", new GpuTextureSlot(7).ToString());
    }

    [Fact]
    public void WorldMeshVertexLayoutMatchesTheMeshShaderInputs()
    {
        GpuVertexLayout layout = GpuVertexLayout.WorldMesh;

        Assert.Equal(32u, layout.StrideBytes);
        Assert.Equal(3, layout.Attributes.Length);
        Assert.Equal(new GpuVertexAttribute(0, GpuVertexFormat.Float3, 0), layout.Attributes[0]);
        Assert.Equal(new GpuVertexAttribute(1, GpuVertexFormat.Float3, 12), layout.Attributes[1]);
        Assert.Equal(new GpuVertexAttribute(2, GpuVertexFormat.Float2, 24), layout.Attributes[2]);
    }

    [Fact]
    public void MultisampledBackbufferPassResolvesWhileDepthIsDiscarded()
    {
        GpuPassDescription multisampled = GpuPassDescription.BackbufferClear("world", Vector4.Zero, sampleCount: 4);
        Assert.Equal(GpuStoreOp.Resolve, multisampled.Color.Store);
        Assert.Null(multisampled.Color.Target);
        Assert.Equal(GpuStoreOp.DontCare, multisampled.Depth!.Value.Store);
        Assert.Equal(1f, multisampled.Depth!.Value.ClearDepth);

        GpuPassDescription single = GpuPassDescription.BackbufferClear("world", Vector4.Zero, sampleCount: 1);
        Assert.Equal(GpuStoreOp.Store, single.Color.Store);
    }

    [Fact]
    public void CapabilityRecordAcceptsADeviceThatMeetsEveryRequirement()
    {
        GpuCapabilityRecord record = SupportedRecord();

        Assert.Empty(record.SupportFailures);
        Assert.True(record.IsSupported);
    }

    [Fact]
    public void CapabilityRecordNamesEveryMissingRequirement()
    {
        GpuCapabilityRecord record = SupportedRecord() with
        {
            SupportsMultiDrawIndirect = false,
            SupportsDrawParameters = false,
            SupportsTextureCompressionBc = false,
            MaxTextureTableSlots = 16,
            MaxStorageBufferBindings = 4,
            MaxPushConstantBytes = 32,
            MaxClipDistances = 0,
        };

        Assert.False(record.IsSupported);
        Assert.Equal(7, record.SupportFailures.Count);
        Assert.Contains(record.SupportFailures, failure => failure.Contains("Multi-draw-indirect", StringComparison.Ordinal));
        Assert.Contains(record.SupportFailures, failure => failure.Contains("gl_DrawID", StringComparison.Ordinal));
        Assert.Contains(record.SupportFailures, failure => failure.Contains("BC (DXT)", StringComparison.Ordinal));
        Assert.Contains(record.SupportFailures, failure => failure.Contains("clip distances", StringComparison.Ordinal));
    }

    [Fact]
    public void TimestampSupportIsOptional()
    {
        // Losing GPU timing degrades profiling; it must never refuse to start.
        GpuCapabilityRecord record = SupportedRecord() with { SupportsTimestampQueries = false };
        Assert.True(record.IsSupported);
    }

    private static GpuCapabilityRecord SupportedRecord() => new()
    {
        Backend = GpuBackendKind.Vulkan,
        DeviceName = "test-adapter",
        DriverInfo = "test-driver",
        ApiVersion = "Vulkan 1.3.0",
        MaxTextureTableSlots = GpuBindingModel.TextureTableCapacity,
        MaxStorageBufferBindings = GpuBindingModel.StorageBindingCount,
        MaxPushConstantBytes = GpuBindingModel.MaxPushConstantBytes,
        MinStorageBufferOffsetAlignment = 64,
        MinUniformBufferOffsetAlignment = 256,
        MaxClipDistances = GpuBindingModel.ClipPlanesPerSlot,
        MaxSampleCount = 8,
        MaxImageDimension2D = 16_384,
        MaxImageArrayLayers = 2_048,
        DeviceLocalMemoryBytes = 8UL * 1024 * 1024 * 1024,
        SupportsMultiDrawIndirect = true,
        SupportsDrawParameters = true,
        SupportsTextureCompressionBc = true,
        SupportsTimestampQueries = true,
        SupportsPersistentlyMappedRings = true,
        SupportsRgba16FloatRenderTargets = true,
        MaxRgba16FloatSampleCount = 8,
        SupportsSampledDepth = true,
        SupportsMultiview = true,
    };
}
