using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Packs;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Core.World;
using DatReaderWriter.Enums;

namespace AcDream.App.Tests.Rendering;

public sealed class DirectionalShadowGpuTests
{
    [Fact]
    public void CompleteCasterClassDiagnostics_AddsExactTerrainCommandCount()
    {
        DirectionalShadowCasterBuildStats stats = default;
        stats = stats with
        {
            CasterClasses = new DirectionalShadowCasterClassDiagnostics(
                TerrainCommands: 0,
                OutdoorStatics: 2,
                Buildings: 3,
                AnimatedStatics: 4,
                LocalPlayers: 5,
                RemotePlayers: 6,
                NonPlayerCreatures: 7,
                OtherLiveDynamics: 8,
                EquippedChildren: 9),
        };

        DirectionalShadowCasterClassDiagnostics completed =
            DirectionalSunShadowRenderer.CompleteCasterClassDiagnostics(
                in stats,
                terrainCommandCount: 11);

        Assert.Equal(11, completed.TerrainCommands);
        Assert.Equal(stats.CasterClasses with { TerrainCommands = 11 }, completed);
    }

    [Fact]
    public void Binding6HostLayout_MatchesCheckedInStd140Block()
    {
        Assert.Equal(336, DirectionalShadowUniforms.SizeInBytes);
        Assert.Equal(DirectionalShadowUniforms.SizeInBytes, Marshal.SizeOf<DirectionalShadowUniforms>());
        Assert.Equal(0, Offset(nameof(DirectionalShadowUniforms.WorldToClip0)));
        Assert.Equal(64, Offset(nameof(DirectionalShadowUniforms.WorldToClip1)));
        Assert.Equal(128, Offset(nameof(DirectionalShadowUniforms.WorldToClip2)));
        Assert.Equal(192, Offset(nameof(DirectionalShadowUniforms.WorldToClip3)));
        Assert.Equal(256, Offset(nameof(DirectionalShadowUniforms.SplitFarMeters)));
        Assert.Equal(272, Offset(nameof(DirectionalShadowUniforms.Control)));
        Assert.Equal(288, Offset(nameof(DirectionalShadowUniforms.BiasMeters)));
        Assert.Equal(304, Offset(nameof(DirectionalShadowUniforms.TextureAndFlags)));
        Assert.Equal(320, Offset(nameof(DirectionalShadowUniforms.LightDirectionAndSource)));
        Assert.Equal(16, Marshal.SizeOf<UInt4>());

        string common = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src", "AcDream.App", "Rendering", "Shaders",
            "directional_shadow_common.glsl"));
        Assert.Contains("ACDREAM_PACK_UBO_SET binding = 6", common, StringComparison.Ordinal);
        Assert.Contains("mat4  uShadowWorldToClip[4]", common, StringComparison.Ordinal);
        Assert.Contains("uvec4 uShadowTextureAndFlags", common, StringComparison.Ordinal);
        Assert.Contains("vec4  uShadowLightDirectionAndSource", common, StringComparison.Ordinal);
    }

    [Fact]
    public void ConservativeReceiverBias_UsesFarthestCascadeAndShaderScalesInnerMaps()
    {
        DirectionalShadowQuality quality = DirectionalShadowQuality.For(
            DirectionalShadowPreset.Low);
        var nearBias = new DirectionalShadowWorldBias(0.01f, 0.02f, 0.03f);
        var farBias = new DirectionalShadowWorldBias(0.11f, 0.12f, 0.13f);
        DirectionalShadowCascade[] cascades =
        [
            Cascade(0, 20f, nearBias),
            Cascade(1, quality.MaximumReachMeters, farBias),
        ];
        var environment = new DirectionalShadowEnvironmentState(
            DirectionalShadowGateReason.Enabled,
            Vector3.Normalize(new Vector3(0.3f, 0.4f, 0.8f)),
            1f,
            1f,
            1f,
            AuthoredCelestialShadowSourceKind.DominantMoon,
            SourceObjectIndex: 2,
            SourceGfxObjId: AuthoredCelestialShadowSourceResolver.DominantMoonGfxObjId);

        DirectionalShadowUniforms uniforms = DirectionalShadowUniforms.Create(
            cascades,
            environment,
            quality,
            new GpuTextureSlot(7));

        Assert.Equal(farBias.ConstantDepthMeters, uniforms.BiasMeters.X);
        Assert.Equal(farBias.SlopeDepthMeters, uniforms.BiasMeters.Y);
        Assert.Equal(farBias.NormalOffsetMeters, uniforms.BiasMeters.Z);

        string receiver = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src", "AcDream.App", "Rendering", "Shaders",
            "directional_shadow_receiver.glsl"));
        Assert.Contains("farDensity / max(cascadeDensity, 1e-7)", receiver,
            StringComparison.Ordinal);
        Assert.Contains("uShadowBiasMeters.xyz * acdreamShadowBiasScale(cascade)", receiver,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Uniforms_CarrySelectedCelestialDirectionAndSourceKind()
    {
        DirectionalShadowQuality quality = DirectionalShadowQuality.For(
            DirectionalShadowPreset.Low);
        DirectionalShadowCascade[] cascades =
        [
            Cascade(0, 20f, new DirectionalShadowWorldBias(0.01f, 0.02f, 0.03f)),
            Cascade(1, quality.MaximumReachMeters,
                new DirectionalShadowWorldBias(0.11f, 0.12f, 0.13f)),
        ];
        Vector3 direction = Vector3.Normalize(new Vector3(0.3f, 0.4f, 0.8f));
        var environment = new DirectionalShadowEnvironmentState(
            DirectionalShadowGateReason.Enabled,
            direction,
            1f,
            1f,
            1f,
            AuthoredCelestialShadowSourceKind.DominantMoon,
            SourceObjectIndex: 2,
            SourceGfxObjId: AuthoredCelestialShadowSourceResolver.DominantMoonGfxObjId);

        DirectionalShadowUniforms uniforms = DirectionalShadowUniforms.Create(
            cascades,
            environment,
            quality,
            new GpuTextureSlot(7));

        Assert.Equal(
            direction,
            new Vector3(
                uniforms.LightDirectionAndSource.X,
                uniforms.LightDirectionAndSource.Y,
                uniforms.LightDirectionAndSource.Z));
        Assert.Equal(
            (float)AuthoredCelestialShadowSourceKind.DominantMoon,
            uniforms.LightDirectionAndSource.W);
    }

    [Fact]
    public void UniformReachAndTerminalFadeUseResidentClampedFinalSplit()
    {
        DirectionalShadowQuality quality = DirectionalShadowQuality.For(
            DirectionalShadowPreset.Low);
        DirectionalShadowCascade[] cascades =
        [
            Cascade(0, 18f, new DirectionalShadowWorldBias(0.01f, 0.02f, 0.03f)),
            Cascade(1, 48f, new DirectionalShadowWorldBias(0.04f, 0.05f, 0.06f)),
        ];

        DirectionalShadowUniforms uniforms = DirectionalShadowUniforms.Create(
            cascades,
            EnabledEnvironment(),
            quality,
            new GpuTextureSlot(7));

        Assert.Equal(48f, uniforms.Control.Z);
        Assert.Equal(1f, uniforms.Control.W);
    }

    [Fact]
    public void MultiviewShadersSelectExactViewMatrixAndPreserveCutout()
    {
        string shaderRoot = Path.Combine(
            RepositoryRoot(),
            "src", "AcDream.App", "Rendering", "Shaders");
        string vertex = File.ReadAllText(Path.Combine(
            shaderRoot,
            "directional_shadow_world_cutout_multiview.vert"));
        string fragment = File.ReadAllText(Path.Combine(
            shaderRoot,
            "directional_shadow_world_cutout_multiview.frag"));
        Assert.Contains("GL_EXT_multiview", vertex, StringComparison.Ordinal);
        Assert.Contains("uShadowWorldToClip[int(gl_ViewIndex)]", vertex,
            StringComparison.Ordinal);
        Assert.Contains("Instances[instanceIndex].transform", vertex,
            StringComparison.Ordinal);
        Assert.Contains("texel.a < 0.05", fragment, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void DirectionalDepthTarget_RejectsLayerCountsOutsideTwoThroughFour(int layers)
    {
        using var device = new RecordingGpuDevice();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            device.CreateDirectionalDepthTarget(
                new GpuDirectionalDepthTargetDescription("bad", 1024, layers)));
    }

    [Fact]
    public void DirectionalDepthTarget_ExposesOneSampleableArrayAndLayerPasses()
    {
        using var device = new RecordingGpuDevice();
        using IGpuDirectionalDepthTarget target = device.CreateDirectionalDepthTarget(
            new GpuDirectionalDepthTargetDescription("shadow", 1536, 3));

        Assert.Equal(GpuTextureKind.Texture2DArray, target.DepthTexture.Kind);
        Assert.Equal(3, target.DepthTexture.LayerCount);
        Assert.Equal(1536, target.DepthTexture.Width);
        using IGpuFrame frame = device.BeginFrame();
        using (frame.BeginPass(GpuPassDescription.DirectionalDepth("cascade-2", target, 2)))
        {
        }
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            frame.BeginPass(GpuPassDescription.DirectionalDepth("cascade-3", target, 3)));
    }

    [Fact]
    public void DirectionalDepthMultiview_RequiresExactFullMaskAndDeviceCapability()
    {
        using var device = new RecordingGpuDevice();
        using IGpuDirectionalDepthTarget target = device.CreateDirectionalDepthTarget(
            new GpuDirectionalDepthTargetDescription("shadow", 1024, 2));
        using IGpuFrame frame = device.BeginFrame();
        using (frame.BeginPass(GpuPassDescription.DirectionalDepthMultiview(
                   "both-cascades", target, 0b11)))
        {
        }
        Assert.Equal(0b11u, Assert.Single(device.OfKind<GpuRecordedPassBegin>()).ViewMask);
        Assert.Throws<NotSupportedException>(() => frame.BeginPass(
            GpuPassDescription.DirectionalDepthMultiview("partial", target, 0b01)));

        using var unsupported = new RecordingGpuDevice
        {
            Capabilities = device.Capabilities with { SupportsMultiview = false },
        };
        using IGpuDirectionalDepthTarget unsupportedTarget = unsupported.CreateDirectionalDepthTarget(
            new GpuDirectionalDepthTargetDescription("shadow", 1024, 2));
        using IGpuFrame unsupportedFrame = unsupported.BeginFrame();
        Assert.Throws<NotSupportedException>(() => unsupportedFrame.BeginPass(
            GpuPassDescription.DirectionalDepthMultiview(
                "unsupported", unsupportedTarget, 0b11)));
    }

    [Theory]
    [InlineData(DirectionalShadowPreset.Low, 2, 768, true)]
    [InlineData(DirectionalShadowPreset.Low, 2, 768, false)]
    [InlineData(DirectionalShadowPreset.Medium, 3, 1536, false)]
    [InlineData(DirectionalShadowPreset.High, 4, 2048, false)]
    internal void Renderer_ReplaysOnePreparedProductAcrossEveryQualityCascade(
        DirectionalShadowPreset preset,
        int expectedCascades,
        int expectedResolution,
        bool multiviewCascades)
    {
        using var device = new RecordingGpuDevice();
        int baselineSlots = device.LiveTextureSlotCount;
        using var renderer = new DirectionalSunShadowRenderer(
            device,
            preset,
            multiviewCascades: multiviewCascades);
        Assert.Equal(baselineSlots + 1, device.LiveTextureSlotCount);
        RecordingGpuDirectionalDepthTarget target = Assert.Single(device.CreatedDirectionalDepthTargets);
        Assert.Equal(expectedCascades, target.Description.LayerCount);
        Assert.Equal(expectedResolution, target.Description.Resolution);
        Assert.All(device.CreatedPipelines, pipeline => Assert.False(pipeline.Description.HasColorAttachment));

        DirectionalShadowPreparedDraws world = CreateWorldDraws(device.DefaultTextureSlot);
        DirectionalShadowTerrainPreparedDraws terrain = CreateTerrainDraws();
        using IGpuBuffer worldVertices = Buffer(device, "world-v", GpuBufferUsage.Vertex);
        using IGpuBuffer worldIndices = Buffer(device, "world-i", GpuBufferUsage.Index);
        using IGpuBuffer terrainVertices = Buffer(device, "terrain-v", GpuBufferUsage.Vertex);
        using IGpuBuffer terrainIndices = Buffer(device, "terrain-i", GpuBufferUsage.Index);
        var worldGeometry = new DirectionalShadowMeshGeometry(worldVertices, worldIndices);
        var terrainGeometry = new DirectionalShadowTerrainGeometry(terrainVertices, terrainIndices);
        var environment = new DirectionalShadowEnvironmentState(
            DirectionalShadowGateReason.Enabled,
            Vector3.Normalize(new Vector3(0.2f, 0.3f, 1f)),
            0.94f,
            0.8f,
            1.25f,
            AuthoredCelestialShadowSourceKind.SecondaryMoon,
            SourceObjectIndex: 7,
            SourceGfxObjId: AuthoredCelestialShadowSourceResolver.SecondaryMoonGfxObjId);

        device.Clear();
        using IGpuFrame frame = device.BeginFrame();
        WorldTransformFrameSlice sharedTransforms = PublishSharedTransforms(
            frame,
            world.Transforms);
        DirectionalSunShadowDiagnostics diagnostics = renderer.RenderPrepared(
            frame,
            environment,
            Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 0.1f, 500f),
            cameraNearMeters: 0.1f,
            casterDepthPaddingMeters: 48f,
            world,
            terrain,
            worldGeometry,
            terrainGeometry,
            sharedTransforms);

        int expectedDraws = (multiviewCascades ? 1 : expectedCascades) * 3;
        Assert.Equal(expectedCascades, diagnostics.CascadeCount);
        Assert.Equal(expectedDraws, diagnostics.DrawCalls);
        Assert.Equal(environment.Strength, diagnostics.Strength);
        Assert.Equal(1, diagnostics.WorldOpaqueCommands);
        Assert.Equal(1, diagnostics.WorldAlphaCutoutCommands);
        Assert.Equal(1, diagnostics.TerrainCommands);
        Assert.Equal(1ul, diagnostics.WorldPreparationSequence);
        Assert.Equal(1ul, diagnostics.TerrainPreparationSequence);
        int expectedPasses = multiviewCascades ? 1 : expectedCascades;
        Assert.Equal(expectedPasses, device.OfKind<GpuRecordedPassBegin>().Count());
        Assert.Equal(expectedPasses, device.OfKind<GpuRecordedTimerScope>().Count());
        Assert.Equal(expectedPasses, device.OfKind<GpuRecordedUniformBind>()
            .Count(call => call.Binding == GpuBindingModel.UniformDirectionalShadow));
        GpuRecordedUniformBind shadowUniformBind = Assert.Single(
            device.OfKind<GpuRecordedUniformBind>()
                .DistinctBy(call => (call.BufferName, call.OffsetBytes, call.SizeBytes)),
            call => call.Binding == GpuBindingModel.UniformDirectionalShadow);
        DirectionalShadowUniforms shadowUniforms = MemoryMarshal.Read<DirectionalShadowUniforms>(
            device.RingBytes.Slice(
                checked((int)shadowUniformBind.OffsetBytes),
                checked((int)shadowUniformBind.SizeBytes)));
        Assert.Equal(
            DirectionalShadowQuality.For(preset).MaximumReachMeters,
            shadowUniforms.BiasMeters.W);
        Assert.Equal(expectedDraws, device.OfKind<GpuRecordedMultiDrawIndirect>().Count());
        Assert.Equal(2, device.OfKind<GpuRecordedRingAllocation>().Count());
        GpuRecordedRingAllocation transformAllocation = Assert.Single(
            device.OfKind<GpuRecordedRingAllocation>(),
            call => call.Usage == GpuRingUsage.Storage
                && call.ByteCount == WorldTransformCapacityPolicy.InitialBindingSizeBytes);
        RecordingGpuBuffer batchBuffer = Assert.Single(
            device.CreatedBuffers,
            buffer => buffer.Name == "directional-shadow-world-batches-1"
                && buffer.Usage.HasFlag(GpuBufferUsage.Storage)
                && buffer.Residency == GpuMemoryResidency.DeviceLocal);
        Span<byte> batchBytes = stackalloc byte[32];
        batchBuffer.Read(0, batchBytes);
        ReadOnlySpan<uint> batchWords = MemoryMarshal.Cast<byte, uint>(batchBytes);
        Assert.Equal(
            0u,
            batchWords[3]);
        Assert.Equal(
            DirectionalShadowBatchFlags.AlphaCutout,
            batchWords[7]);
        Assert.Contains(
            device.CreatedBuffers,
            buffer => buffer.Name == "directional-shadow-world-commands-1"
                && buffer.Usage.HasFlag(GpuBufferUsage.Indirect)
                && buffer.Residency == GpuMemoryResidency.DeviceLocal);
        Assert.Contains(
            device.CreatedBuffers,
            buffer => buffer.Name == "directional-shadow-terrain-commands-1"
                && buffer.Usage.HasFlag(GpuBufferUsage.Indirect)
                && buffer.Residency == GpuMemoryResidency.DeviceLocal);
        Assert.DoesNotContain(
            device.OfKind<GpuRecordedRingAllocation>(),
            call => call.Usage == GpuRingUsage.Storage
                && call.ByteCount == world.Transforms.Length * Marshal.SizeOf<Matrix4x4>());
        Assert.All(
            device.OfKind<GpuRecordedStorageBind>()
                .Where(call => call.Binding == GpuBindingModel.StorageInstances),
            call =>
            {
                Assert.Equal(transformAllocation.OffsetBytes, call.OffsetBytes);
                Assert.Equal(WorldTransformCapacityPolicy.InitialBindingSizeBytes, call.SizeBytes);
            });
        for (int cascade = 0; cascade < (multiviewCascades ? 1 : expectedCascades); cascade++)
        {
            Assert.Contains(
                device.OfKind<GpuRecordedPushConstants>(),
                call => call.Constants.RenderPass == cascade);
        }
        if (multiviewCascades)
        {
            GpuRecordedPassBegin pass = Assert.Single(device.OfKind<GpuRecordedPassBegin>());
            Assert.Equal(0b11u, pass.ViewMask);
            Assert.Equal(
                1,
                device.OfKind<GpuRecordedPipelineBind>().Count(call =>
                    call.PipelineName == "directional-shadow-world-cutout-multiview"));
            Assert.Contains(device.OfKind<GpuRecordedPipelineBind>(), call =>
                call.PipelineName == "directional-shadow-world-opaque-multiview");
            Assert.Contains(device.OfKind<GpuRecordedPipelineBind>(), call =>
                call.PipelineName == "directional-shadow-terrain-multiview");
        }
    }

    [Fact]
    public void CasterFrameBindingCarriesTheExactAtmosphericFrameBufferForTheReceiverSeam()
    {
        using var device = new RecordingGpuDevice();
        using var renderer = new DirectionalSunShadowRenderer(device, DirectionalShadowPreset.Medium);
        DirectionalShadowPreparedDraws world = CreateWorldDraws(device.DefaultTextureSlot);
        DirectionalShadowTerrainPreparedDraws terrain = CreateTerrainDraws();
        using IGpuBuffer worldVertices = Buffer(device, "world-v", GpuBufferUsage.Vertex);
        using IGpuBuffer worldIndices = Buffer(device, "world-i", GpuBufferUsage.Index);
        using IGpuBuffer terrainVertices = Buffer(device, "terrain-v", GpuBufferUsage.Vertex);
        using IGpuBuffer terrainIndices = Buffer(device, "terrain-i", GpuBufferUsage.Index);
        var worldGeometry = new DirectionalShadowMeshGeometry(worldVertices, worldIndices);
        var terrainGeometry = new DirectionalShadowTerrainGeometry(terrainVertices, terrainIndices);
        var environment = new DirectionalShadowEnvironmentState(
            DirectionalShadowGateReason.Enabled,
            Vector3.Normalize(new Vector3(0.1f, 0.2f, 1f)),
            0.9f,
            0.75f,
            1.1f,
            AuthoredCelestialShadowSourceKind.Sun,
            SourceObjectIndex: -1,
            SourceGfxObjId: 0);
        using IGpuBuffer atmosphericBuffer = Buffer(device, "test-atmospheric-frame", GpuBufferUsage.Uniform);
        var atmosphericFrame = new AtmosphericFrameBufferBinding(
            atmosphericBuffer,
            OffsetBytes: 64u,
            SizeBytes: 192u);

        device.Clear();
        using IGpuFrame frame = device.BeginFrame();
        WorldTransformFrameSlice sharedTransforms = PublishSharedTransforms(frame, world.Transforms);
        renderer.RenderPrepared(
            frame,
            environment,
            Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 0.1f, 500f),
            cameraNearMeters: 0.1f,
            casterDepthPaddingMeters: 48f,
            world,
            terrain,
            worldGeometry,
            terrainGeometry,
            sharedTransforms,
            atmosphericFrame: atmosphericFrame);

        GpuRecordedUniformBind casterAtmosphericBind = Assert.Single(
            device.OfKind<GpuRecordedUniformBind>()
                .DistinctBy(call => (call.BufferName, call.OffsetBytes, call.SizeBytes)),
            call => call.Binding == GpuBindingModel.UniformAtmosphericFrame);
        Assert.Equal("test-atmospheric-frame", casterAtmosphericBind.BufferName);
        Assert.Equal(64u, casterAtmosphericBind.OffsetBytes);
        Assert.Equal(192u, casterAtmosphericBind.SizeBytes);

        Assert.True(renderer.TryGetCurrentFrameBinding(frame, out DirectionalShadowFrameBinding binding));
        Assert.True(binding.AtmosphericFrame.IsBound);
        Assert.Same(atmosphericBuffer, binding.AtmosphericFrame.Buffer);
        Assert.Equal(atmosphericFrame.OffsetBytes, binding.AtmosphericFrame.OffsetBytes);
        Assert.Equal(atmosphericFrame.SizeBytes, binding.AtmosphericFrame.SizeBytes);
    }

    [Fact]
    public void BindDirectionalShadowReceiverEmitsAtmosphericFrameWithTheExactCasterBufferOffsetAndSize()
    {
        using var device = new RecordingGpuDevice();
        using IGpuBuffer shadowBuffer = Buffer(device, "shadow-frame", GpuBufferUsage.Uniform);
        using IGpuBuffer atmosphericBuffer = Buffer(device, "test-atmospheric-frame", GpuBufferUsage.Uniform);
        var binding = new DirectionalShadowFrameBinding(
            FrameSerial: 1,
            Enabled: true,
            Buffer: shadowBuffer,
            OffsetBytes: 0u,
            SizeBytes: 128u,
            TextureSlot: GpuTextureSlot.Unassigned,
            CascadeCount: 4,
            AtmosphericFrame: new AtmosphericFrameBufferBinding(
                atmosphericBuffer,
                OffsetBytes: 64u,
                SizeBytes: 192u));

        device.Clear();
        var target = device.CreateRenderTarget(new GpuRenderTargetDescription(
            "test-world-hdr",
            640,
            480,
            GpuTextureFormat.Rgba16FloatRenderTarget,
            GpuTextureFormat.Depth24Stencil8,
            SampleCount: 1));
        using IGpuFrame frame = device.BeginFrame();
        using (IGpuPassEncoder encoder = frame.BeginPass(new GpuPassDescription
        {
            Name = "test-world-hdr",
            Color = new GpuColorAttachment(
                target,
                GpuLoadOp.Clear,
                GpuStoreOp.Store,
                Vector4.Zero),
            Depth = new GpuDepthAttachment(GpuLoadOp.Clear, GpuStoreOp.Store, 1f, 0),
            SampleCount = 1,
        }))
        {
            WbDrawDispatcher.BindDirectionalShadowReceiver(encoder, in binding);
        }
        frame.End();

        GpuRecordedUniformBind atmosphericBind = Assert.Single(
            device.OfKind<GpuRecordedUniformBind>(),
            call => call.Binding == GpuBindingModel.UniformAtmosphericFrame);
        Assert.Equal("test-atmospheric-frame", atmosphericBind.BufferName);
        Assert.Equal(64u, atmosphericBind.OffsetBytes);
        Assert.Equal(192u, atmosphericBind.SizeBytes);

        // The shadow-map binding fired too — BindDirectionalShadowReceiver
        // is not a no-op that only happens to satisfy the assertion above.
        GpuRecordedUniformBind shadowBind = Assert.Single(
            device.OfKind<GpuRecordedUniformBind>(),
            call => call.Binding == GpuBindingModel.UniformDirectionalShadow);
        Assert.Equal("shadow-frame", shadowBind.BufferName);
    }

    [Fact]
    public void PublishDisabledReceiverBinding_IsBindableButNotValidWithZeroFlagsAndUnitDirection()
    {
        using var device = new RecordingGpuDevice();
        using var renderer = new DirectionalSunShadowRenderer(device, DirectionalShadowPreset.Low);
        using IGpuBuffer atmosphericBuffer = Buffer(device, "wind-only-frame", GpuBufferUsage.Uniform);
        var atmosphericFrame = new AtmosphericFrameBufferBinding(
            atmosphericBuffer,
            OffsetBytes: 0u,
            SizeBytes: 192u);
        device.Clear();
        using IGpuFrame frame = device.BeginFrame();

        renderer.PublishDisabledReceiverBinding(frame, atmosphericFrame);

        Assert.True(renderer.TryGetCurrentFrameBinding(frame, out DirectionalShadowFrameBinding binding));
        Assert.True(binding.IsBindableFor(frame));
        Assert.False(binding.IsValidFor(frame));
        Assert.False(binding.Enabled);
        Assert.Equal(0, binding.CascadeCount);
        Assert.False(binding.TextureSlot.IsAssigned);
        Assert.True(binding.AtmosphericFrame.IsBound);
        Assert.Same(atmosphericBuffer, binding.AtmosphericFrame.Buffer);

        DirectionalShadowUniforms written = MemoryMarshal.Read<DirectionalShadowUniforms>(
            device.RingBytes.Slice(
                (int)binding.OffsetBytes,
                DirectionalShadowUniforms.SizeInBytes));
        Assert.Equal(0u, written.TextureAndFlags.W);
        Assert.Equal(new Vector4(0f, 0f, 1f, 0f), written.LightDirectionAndSource);
    }

    [Fact]
    public void PublishDisabledReceiverBinding_NoOpsWhenAtmosphericFrameIsUnboundPreservingTodaysBehaviour()
    {
        using var device = new RecordingGpuDevice();
        using var renderer = new DirectionalSunShadowRenderer(device, DirectionalShadowPreset.Low);
        device.Clear();
        using IGpuFrame frame = device.BeginFrame();

        renderer.PublishDisabledReceiverBinding(frame, default);

        Assert.False(renderer.TryGetCurrentFrameBinding(frame, out _));
        Assert.Empty(device.OfKind<GpuRecordedRingAllocation>());
    }

    [Fact]
    public void EvaluateGateAndPublishDisabledBinding_PlayerInsideCellPublishesBindableNotValidBinding()
    {
        using var device = new RecordingGpuDevice();
        using var renderer = new DirectionalSunShadowRenderer(device, DirectionalShadowPreset.Low);
        using IGpuBuffer atmosphericBuffer = Buffer(device, "gate-indoor-frame", GpuBufferUsage.Uniform);
        var atmosphericFrame = new AtmosphericFrameBufferBinding(atmosphericBuffer, 0u, 192u);
        var input = new DirectionalSunShadowRenderInput(
            new DirectionalShadowEnvironmentInput(
                PackEnabled: true,
                PortalOrLoginCoverVisible: false,
                PlayerInsideCell: true,
                Source: AuthoredCelestialShadowSource.None(),
                Atmosphere: default),
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            new DirectionalShadowCasterFrame(),
            AtmosphericFrame: atmosphericFrame);
        device.Clear();
        using IGpuFrame frame = device.BeginFrame();

        DirectionalSunShadowDiagnostics? gated = renderer.EvaluateGateAndPublishDisabledBinding(
            frame,
            in input,
            out DirectionalShadowEnvironmentState environment,
            out long environmentGateTicks);

        Assert.NotNull(gated);
        Assert.Equal(DirectionalShadowGateReason.Indoor, gated.Value.GateReason);
        Assert.Equal(DirectionalShadowGateReason.Indoor, environment.Reason);
        Assert.True(renderer.TryGetCurrentFrameBinding(frame, out DirectionalShadowFrameBinding binding));
        Assert.True(binding.IsBindableFor(frame));
        Assert.False(binding.IsValidFor(frame));
    }

    [Fact]
    public void EvaluateGateAndPublishDisabledBinding_ResidentWindowUnavailablePublishesBindableNotValidBinding()
    {
        using var device = new RecordingGpuDevice();
        using var renderer = new DirectionalSunShadowRenderer(device, DirectionalShadowPreset.Low);
        using IGpuBuffer atmosphericBuffer = Buffer(device, "gate-resident-frame", GpuBufferUsage.Uniform);
        var atmosphericFrame = new AtmosphericFrameBufferBinding(atmosphericBuffer, 0u, 192u);
        var validSource = new AuthoredCelestialShadowSource(
            AuthoredCelestialShadowSourceKind.Sun,
            ObjectIndex: 0,
            GfxObjId: 1u,
            SurfaceToLightDirection: Vector3.UnitZ,
            ElevationSin: 0.5f,
            AuthoredEnergy: 1f);
        var validAtmosphere = new AtmosphereSnapshot(
            WeatherKind.Clear,
            Intensity: 1f,
            FogColor: Vector3.Zero,
            FogStart: 0f,
            FogEnd: 0f,
            FogMode: default,
            LightningFlash: 0f,
            Override: default);
        var input = new DirectionalSunShadowRenderInput(
            new DirectionalShadowEnvironmentInput(
                PackEnabled: true,
                PortalOrLoginCoverVisible: false,
                PlayerInsideCell: false,
                Source: validSource,
                Atmosphere: validAtmosphere,
                ActiveDayGroupMultiplier: 1f),
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            new DirectionalShadowCasterFrame(),
            CameraNearMeters: 0.1f,
            ResidentMaximumReachMeters: 0.05f,
            AtmosphericFrame: atmosphericFrame);
        device.Clear();
        using IGpuFrame frame = device.BeginFrame();

        DirectionalSunShadowDiagnostics? gated = renderer.EvaluateGateAndPublishDisabledBinding(
            frame,
            in input,
            out DirectionalShadowEnvironmentState environment,
            out long environmentGateTicks);

        Assert.NotNull(gated);
        Assert.Equal(DirectionalShadowGateReason.ResidentWindowUnavailable, gated.Value.GateReason);
        Assert.Equal(DirectionalShadowGateReason.ResidentWindowUnavailable, environment.Reason);
        Assert.True(renderer.TryGetCurrentFrameBinding(frame, out DirectionalShadowFrameBinding binding));
        Assert.True(binding.IsBindableFor(frame));
        Assert.False(binding.IsValidFor(frame));
    }

    [Fact]
    public void ShouldSelectReceiverPipelineComposesWithTheRealBindingSourceAfterADisabledPublish()
    {
        using var device = new RecordingGpuDevice();
        using var renderer = new DirectionalSunShadowRenderer(device, DirectionalShadowPreset.Low);
        using IGpuBuffer atmosphericBuffer = Buffer(device, "gate-pipeline-frame", GpuBufferUsage.Uniform);
        var atmosphericFrame = new AtmosphericFrameBufferBinding(atmosphericBuffer, 0u, 192u);
        device.Clear();
        using IGpuFrame frame = device.BeginFrame();

        renderer.PublishDisabledReceiverBinding(frame, atmosphericFrame);
        bool bindingValid = renderer.TryGetCurrentFrameBinding(frame, out _);

        Assert.True(bindingValid);
        Assert.True(DirectionalShadowReceiverPolicy.ShouldSelectReceiverPipeline(
            DirectionalShadowReceiverPolicy.AtmosphericWorldPassName,
            sourcePresent: true,
            bindingValid));
        Assert.False(DirectionalShadowReceiverPolicy.ShouldSelectReceiverPipeline(
            "vk-world",
            sourcePresent: true,
            bindingValid));
    }

    [Fact]
    public void RenderPrepared_CascadeFitterReturningZeroCascadesPublishesBindableNotValidBinding()
    {
        using var device = new RecordingGpuDevice();
        using var renderer = new DirectionalSunShadowRenderer(device, DirectionalShadowPreset.Low);
        using IGpuBuffer atmosphericBuffer = Buffer(device, "cascade-zero-frame", GpuBufferUsage.Uniform);
        var atmosphericFrame = new AtmosphericFrameBufferBinding(atmosphericBuffer, 0u, 192u);
        device.Clear();
        using IGpuFrame frame = device.BeginFrame();
        WorldTransformFrameSlice transforms = PublishSharedTransforms(
            frame,
            ReadOnlySpan<Matrix4x4>.Empty);

        DirectionalSunShadowDiagnostics diagnostics = renderer.RenderPrepared(
            frame,
            EnabledEnvironment(),
            Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(1f, 1f, 0.1f, 500f),
            0.1f,
            48f,
            new DirectionalShadowPreparedDraws(),
            new DirectionalShadowTerrainPreparedDraws(),
            null,
            null,
            transforms,
            residentMaximumReachMeters: 0.05f,
            atmosphericFrame: atmosphericFrame);

        Assert.Equal(DirectionalShadowGateReason.ResidentWindowUnavailable, diagnostics.GateReason);
        Assert.True(renderer.TryGetCurrentFrameBinding(frame, out DirectionalShadowFrameBinding binding));
        Assert.True(binding.IsBindableFor(frame));
        Assert.False(binding.IsValidFor(frame));
    }

    [Fact]
    public void BindDirectionalShadowReceiver_WithADisabledBindingEmitsBothShadowAndAtmosphericBinds()
    {
        using var device = new RecordingGpuDevice();
        using IGpuBuffer shadowBuffer = Buffer(device, "disabled-shadow-frame", GpuBufferUsage.Uniform);
        using IGpuBuffer atmosphericBuffer = Buffer(device, "disabled-atmospheric-frame", GpuBufferUsage.Uniform);
        var binding = new DirectionalShadowFrameBinding(
            FrameSerial: 1,
            Enabled: false,
            Buffer: shadowBuffer,
            OffsetBytes: 0u,
            SizeBytes: DirectionalShadowUniforms.SizeInBytes,
            TextureSlot: GpuTextureSlot.Unassigned,
            CascadeCount: 0,
            AtmosphericFrame: new AtmosphericFrameBufferBinding(
                atmosphericBuffer,
                OffsetBytes: 0u,
                SizeBytes: 192u));

        device.Clear();
        var target = device.CreateRenderTarget(new GpuRenderTargetDescription(
            "test-world-hdr",
            640,
            480,
            GpuTextureFormat.Rgba16FloatRenderTarget,
            GpuTextureFormat.Depth24Stencil8,
            SampleCount: 1));
        using IGpuFrame frame = device.BeginFrame();
        using (IGpuPassEncoder encoder = frame.BeginPass(new GpuPassDescription
        {
            Name = "test-world-hdr",
            Color = new GpuColorAttachment(
                target,
                GpuLoadOp.Clear,
                GpuStoreOp.Store,
                Vector4.Zero),
            Depth = new GpuDepthAttachment(GpuLoadOp.Clear, GpuStoreOp.Store, 1f, 0),
            SampleCount = 1,
        }))
        {
            WbDrawDispatcher.BindDirectionalShadowReceiver(encoder, in binding);
        }
        frame.End();

        GpuRecordedUniformBind shadowBind = Assert.Single(
            device.OfKind<GpuRecordedUniformBind>(),
            call => call.Binding == GpuBindingModel.UniformDirectionalShadow);
        Assert.Equal("disabled-shadow-frame", shadowBind.BufferName);
        GpuRecordedUniformBind atmosphericBind = Assert.Single(
            device.OfKind<GpuRecordedUniformBind>(),
            call => call.Binding == GpuBindingModel.UniformAtmosphericFrame);
        Assert.Equal("disabled-atmospheric-frame", atmosphericBind.BufferName);
    }

    [Fact]
    public void StableTopology_ReusesRetainedCommandBuffersWithoutFrameRingCopies()
    {
        using var device = new RecordingGpuDevice();
        using var renderer = new DirectionalSunShadowRenderer(
            device,
            DirectionalShadowPreset.Low,
            multiviewCascades: true);
        DirectionalShadowPreparedDraws world = CreateWorldDraws(
            device.DefaultTextureSlot);
        DirectionalShadowTerrainPreparedDraws terrain = CreateTerrainDraws();
        using IGpuBuffer worldVertices = Buffer(device, "world-v", GpuBufferUsage.Vertex);
        using IGpuBuffer worldIndices = Buffer(device, "world-i", GpuBufferUsage.Index);
        using IGpuBuffer terrainVertices = Buffer(device, "terrain-v", GpuBufferUsage.Vertex);
        using IGpuBuffer terrainIndices = Buffer(device, "terrain-i", GpuBufferUsage.Index);
        var worldGeometry = new DirectionalShadowMeshGeometry(
            worldVertices,
            worldIndices);
        var terrainGeometry = new DirectionalShadowTerrainGeometry(
            terrainVertices,
            terrainIndices);
        DirectionalShadowEnvironmentState environment = EnabledEnvironment();

        using (IGpuFrame frame = device.BeginFrame())
        {
            renderer.RenderPrepared(
                frame,
                environment,
                Matrix4x4.Identity,
                Matrix4x4.CreatePerspectiveFieldOfView(1f, 1f, 0.1f, 500f),
                0.1f,
                48f,
                world,
                terrain,
                worldGeometry,
                terrainGeometry,
                PublishSharedTransforms(frame, world.Transforms));
        }

        RecordingGpuBuffer[] retained = device.CreatedBuffers
            .Where(buffer => buffer.Name.StartsWith(
                "directional-shadow-",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, retained.Length);
        Assert.Equal(3, renderer.RetainedCommandBufferCount);
        Assert.Equal(retained.Sum(buffer => buffer.SizeBytes),
            renderer.RetainedCommandBufferBytes);

        device.Clear();
        int createdBefore = device.CreatedBuffers.Count;
        using (IGpuFrame frame = device.BeginFrame())
        {
            renderer.RenderPrepared(
                frame,
                environment,
                Matrix4x4.Identity,
                Matrix4x4.CreatePerspectiveFieldOfView(1f, 1f, 0.1f, 500f),
                0.1f,
                48f,
                world,
                terrain,
                worldGeometry,
                terrainGeometry,
                PublishSharedTransforms(frame, world.Transforms));
        }

        Assert.Equal(createdBefore, device.CreatedBuffers.Count);
        Assert.All(retained, buffer => Assert.False(buffer.IsDisposed));
        Assert.Equal(2, device.OfKind<GpuRecordedRingAllocation>().Count());
        Assert.DoesNotContain(
            device.OfKind<GpuRecordedRingAllocation>(),
            allocation => allocation.Usage == GpuRingUsage.Indirect);
    }

    [Fact]
    public void ActiveAlternatingRuns_UploadOnlySelectedTransformAddresses()
    {
        using var device = new RecordingGpuDevice();
        using var renderer = new DirectionalSunShadowRenderer(
            device,
            DirectionalShadowPreset.Medium);
        var world = new DirectionalShadowPreparedDraws();
        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(21);
        Assert.True(world.TryBegin(generation, 44, estimatedInstances: 6));
        for (int casterIndex = 0; casterIndex < 6; casterIndex++)
        {
            Matrix4x4 transform = Matrix4x4.CreateTranslation(casterIndex, 0f, 0f);
            DirectionalShadowTransformSource source =
                DirectionalShadowTransformSource.Static(casterIndex);
            world.Add(
                100,
                7,
                12,
                GpuTextureSlot.Unassigned,
                0,
                CullMode.CounterClockwise,
                DirectionalShadowCasterMaterial.Opaque,
                in transform,
                in source);
        }
        DirectionalShadowPreparationStats stats = default;
        world.Complete(generation, 44, in stats);
        world.ApplySelection(
            [true, false, true, false, true, false],
            casterSelectionSequence: 1);
        var terrain = new DirectionalShadowTerrainPreparedDraws();
        Assert.True(terrain.TryBegin(1, 0));
        terrain.Complete(1);
        using IGpuBuffer vertices = Buffer(device, "world-v", GpuBufferUsage.Vertex);
        using IGpuBuffer indices = Buffer(device, "world-i", GpuBufferUsage.Index);
        var geometry = new DirectionalShadowMeshGeometry(vertices, indices);

        device.Clear();
        using IGpuFrame frame = device.BeginFrame();
        WorldTransformFrameSlice transforms = PublishSharedTransforms(frame, world.Transforms);
        DirectionalSunShadowDiagnostics diagnostics = renderer.RenderPrepared(
            frame,
            EnabledEnvironment(),
            Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 3f,
                16f / 9f,
                0.1f,
                500f),
            cameraNearMeters: 0.1f,
            casterDepthPaddingMeters: 48f,
            world,
            terrain,
            geometry,
            terrainGeometry: null,
            transforms);

        RecordingGpuBuffer commands = Assert.Single(
            device.CreatedBuffers,
            buffer => buffer.Name == "directional-shadow-world-commands-2");
        Span<byte> bytes = stackalloc byte[3 * 20];
        commands.Read(0, bytes);
        ReadOnlySpan<DrawElementsIndirectCommand> uploaded =
            MemoryMarshal.Cast<byte, DrawElementsIndirectCommand>(bytes);
        Assert.Equal([0u, 2u, 4u],
            uploaded.ToArray().Select(static command => command.BaseInstance));
        Assert.All(uploaded.ToArray(),
            static command => Assert.Equal(1u, command.InstanceCount));
        Assert.All(
            device.OfKind<GpuRecordedMultiDrawIndirect>(),
            static draw => Assert.Equal(3u, draw.DrawCount));
        Assert.Equal(6, diagnostics.ResidentWorldInstances);
        Assert.Equal(3, diagnostics.ActiveWorldInstances);
        Assert.Equal(1, diagnostics.ResidentWorldCommands);
        Assert.Equal(3, diagnostics.ActiveWorldCommands);
    }

    [Fact]
    public void TopologyRebuild_SwapsRetainedBuffersAndDisposalReleasesTheCurrentSet()
    {
        using var device = new RecordingGpuDevice();
        var renderer = new DirectionalSunShadowRenderer(
            device,
            DirectionalShadowPreset.Low);
        DirectionalShadowPreparedDraws world = CreateWorldDraws(
            device.DefaultTextureSlot);
        DirectionalShadowTerrainPreparedDraws terrain = CreateTerrainDraws();
        using IGpuBuffer worldVertices = Buffer(device, "world-v", GpuBufferUsage.Vertex);
        using IGpuBuffer worldIndices = Buffer(device, "world-i", GpuBufferUsage.Index);
        using IGpuBuffer terrainVertices = Buffer(device, "terrain-v", GpuBufferUsage.Vertex);
        using IGpuBuffer terrainIndices = Buffer(device, "terrain-i", GpuBufferUsage.Index);
        var worldGeometry = new DirectionalShadowMeshGeometry(
            worldVertices,
            worldIndices);
        var terrainGeometry = new DirectionalShadowTerrainGeometry(
            terrainVertices,
            terrainIndices);
        DirectionalShadowEnvironmentState environment = EnabledEnvironment();

        using (IGpuFrame frame = device.BeginFrame())
        {
            renderer.RenderPrepared(
                frame,
                environment,
                Matrix4x4.Identity,
                Matrix4x4.CreatePerspectiveFieldOfView(1f, 1f, 0.1f, 500f),
                0.1f,
                48f,
                world,
                terrain,
                worldGeometry,
                terrainGeometry,
                PublishSharedTransforms(frame, world.Transforms));
        }
        RecordingGpuBuffer[] firstSet = device.CreatedBuffers
            .Where(buffer => buffer.Name.StartsWith(
                "directional-shadow-",
                StringComparison.Ordinal))
            .ToArray();

        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(3);
        Assert.True(world.TryBegin(generation, 8, 1));
        Matrix4x4 moved = Matrix4x4.CreateTranslation(20f, 30f, 40f);
        world.Add(
            30,
            2,
            9,
            GpuTextureSlot.Unassigned,
            0,
            CullMode.Clockwise,
            DirectionalShadowCasterMaterial.Opaque,
            in moved);
        DirectionalShadowPreparationStats stats = default;
        world.Complete(generation, 8, in stats);

        Assert.True(terrain.TryBegin(2, 1));
        var terrainRange = new DirectionalShadowTerrainRange(80, 90);
        terrain.Add(in terrainRange);
        terrain.Complete(2);
        using (IGpuFrame frame = device.BeginFrame())
        {
            renderer.RenderPrepared(
                frame,
                environment,
                Matrix4x4.Identity,
                Matrix4x4.CreatePerspectiveFieldOfView(1f, 1f, 0.1f, 500f),
                0.1f,
                48f,
                world,
                terrain,
                worldGeometry,
                terrainGeometry,
                PublishSharedTransforms(frame, world.Transforms));
        }

        Assert.All(firstSet, buffer => Assert.True(buffer.IsDisposed));
        RecordingGpuBuffer[] currentSet = device.CreatedBuffers
            .Where(buffer => buffer.Name.EndsWith("-2", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, currentSet.Length);
        Assert.All(currentSet, buffer => Assert.False(buffer.IsDisposed));

        renderer.Dispose();

        Assert.All(currentSet, buffer => Assert.True(buffer.IsDisposed));
    }

    [Theory]
    [InlineData(DirectionalShadowGateReason.Indoor)]
    [InlineData(DirectionalShadowGateReason.SelectedLightBelowHorizon)]
    [InlineData(DirectionalShadowGateReason.SelectedLightHasNoEnergy)]
    [InlineData(DirectionalShadowGateReason.NoVisibleCelestial)]
    [InlineData(DirectionalShadowGateReason.PackDisabled)]
    internal void DisabledEnvironment_RecordsNoPassOrUpload(DirectionalShadowGateReason reason)
    {
        using var device = new RecordingGpuDevice();
        using var renderer = new DirectionalSunShadowRenderer(device, DirectionalShadowPreset.Low);
        device.Clear();
        using IGpuFrame frame = device.BeginFrame();

        DirectionalSunShadowDiagnostics diagnostics = renderer.RenderPrepared(
            frame,
            new DirectionalShadowEnvironmentState(reason, Vector3.UnitZ, 0f, 0f, 1f),
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            0.1f,
            48f,
            new DirectionalShadowPreparedDraws(),
            new DirectionalShadowTerrainPreparedDraws(),
            null,
            null,
            default);

        Assert.Equal(reason, diagnostics.GateReason);
        Assert.Empty(device.OfKind<GpuRecordedPassBegin>());
        Assert.Empty(device.OfKind<GpuRecordedRingAllocation>());
    }

    [Fact]
    public void Disposal_ReleasesTextureSlotAndEveryOwnedResource()
    {
        using var device = new RecordingGpuDevice();
        int baselineSlots = device.LiveTextureSlotCount;
        var renderer = new DirectionalSunShadowRenderer(device, DirectionalShadowPreset.High);
        RecordingGpuDirectionalDepthTarget target = Assert.Single(device.CreatedDirectionalDepthTargets);
        RecordingGpuPipeline[] pipelines = device.CreatedPipelines.ToArray();
        RecordingGpuSampler sampler = device.CreatedSamplers[^1];

        Assert.Equal(GpuSamplerDescription.ShadowNearestClamp, sampler.Description);
        Assert.Equal(GpuFilter.Nearest, sampler.Description.MinFilter);
        Assert.Equal(GpuFilter.Nearest, sampler.Description.MagFilter);
        Assert.Equal(GpuAddressMode.ClampToEdge, sampler.Description.AddressU);
        Assert.Equal(GpuAddressMode.ClampToEdge, sampler.Description.AddressV);

        renderer.Dispose();

        Assert.Equal(baselineSlots, device.LiveTextureSlotCount);
        Assert.True(target.IsDisposed);
        Assert.True(sampler.IsDisposed);
        Assert.All(pipelines, pipeline => Assert.True(pipeline.IsDisposed));
    }

    [Fact]
    public void ConstructionFailure_RollsBackTargetSlotSamplerAndEarlierPipelines()
    {
        using var device = new RecordingGpuDevice();
        int baselineSlots = device.LiveTextureSlotCount;
        device.PipelineFailure = description =>
            description.Name == "directional-shadow-world-opaque"
                ? new InvalidOperationException("injected pipeline failure")
                : null;

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            new DirectionalSunShadowRenderer(device, DirectionalShadowPreset.Medium));

        Assert.Equal("injected pipeline failure", failure.Message);
        Assert.Equal(baselineSlots, device.LiveTextureSlotCount);
        Assert.True(Assert.Single(device.CreatedDirectionalDepthTargets).IsDisposed);
        Assert.True(device.CreatedSamplers[^1].IsDisposed);
        Assert.True(Assert.Single(device.CreatedPipelines).IsDisposed);
    }

    [Fact]
    public void MultiviewConstructionFailure_RetiresAllOrdinaryAndLayeredCandidates()
    {
        using var device = new RecordingGpuDevice();
        int baselineSlots = device.LiveTextureSlotCount;
        device.PipelineFailure = description =>
            description.Name == "directional-shadow-world-cutout-multiview"
                ? new InvalidOperationException("injected multiview failure")
                : null;

        Assert.Throws<InvalidOperationException>(() =>
            new DirectionalSunShadowRenderer(
                device,
                DirectionalShadowPreset.Low,
                multiviewCascades: true));

        Assert.Equal(baselineSlots, device.LiveTextureSlotCount);
        Assert.True(Assert.Single(device.CreatedDirectionalDepthTargets).IsDisposed);
        Assert.True(device.CreatedSamplers[^1].IsDisposed);
        Assert.Equal(5, device.CreatedPipelines.Count);
        Assert.All(device.CreatedPipelines, pipeline => Assert.True(pipeline.IsDisposed));
    }

    [Fact]
    public void RebuildAfterDisposal_UsesANewLiveShadowSampler()
    {
        using var device = new RecordingGpuDevice();
        var first = new DirectionalSunShadowRenderer(device, DirectionalShadowPreset.Low);
        RecordingGpuSampler firstSampler = device.CreatedSamplers[^1];
        first.Dispose();

        using var second = new DirectionalSunShadowRenderer(device, DirectionalShadowPreset.Low);
        RecordingGpuSampler secondSampler = device.CreatedSamplers[^1];

        Assert.NotSame(firstSampler, secondSampler);
        Assert.True(firstSampler.IsDisposed);
        Assert.False(secondSampler.IsDisposed);
        Assert.Equal(GpuSamplerDescription.ShadowNearestClamp, secondSampler.Description);
    }

    [Fact]
    public void ReceiverBinding_IsValidOnlyForTheProducingFrame()
    {
        using var device = new RecordingGpuDevice();
        using var renderer = new DirectionalSunShadowRenderer(
            device,
            DirectionalShadowPreset.Low);
        using (IGpuFrame frame = device.BeginFrame())
        {
            WorldTransformFrameSlice sharedTransforms = PublishSharedTransforms(
                frame,
                ReadOnlySpan<Matrix4x4>.Empty);
            renderer.RenderPrepared(
                frame,
                new DirectionalShadowEnvironmentState(
                    DirectionalShadowGateReason.Enabled,
                    Vector3.UnitZ,
                    1f,
                    1f,
                    1f,
                    AuthoredCelestialShadowSourceKind.Sun,
                    SourceObjectIndex: 0,
                    SourceGfxObjId: AuthoredCelestialShadowSourceResolver.SunGfxObjId),
                Matrix4x4.Identity,
                Matrix4x4.CreatePerspectiveFieldOfView(1f, 1f, 0.1f, 100f),
                0.1f,
                48f,
                new DirectionalShadowPreparedDraws(),
                new DirectionalShadowTerrainPreparedDraws(),
                null,
                null,
                sharedTransforms);

            Assert.True(renderer.TryGetCurrentFrameBinding(frame, out var binding));
            Assert.Equal(frame.Serial, binding.FrameSerial);
            Assert.Equal((uint)DirectionalShadowUniforms.SizeInBytes, binding.SizeBytes);
            Assert.Equal(2, binding.CascadeCount);
        }

        using IGpuFrame later = device.BeginFrame();
        Assert.False(renderer.TryGetCurrentFrameBinding(later, out _));
    }

    private static DirectionalShadowPreparedDraws CreateWorldDraws(GpuTextureSlot cutoutSlot)
    {
        var draws = new DirectionalShadowPreparedDraws();
        RenderSceneGeneration generation = RenderSceneGeneration.FromRaw(3);
        Assert.True(draws.TryBegin(generation, 7, 2));
        Matrix4x4 opaque = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        Matrix4x4 cutout = Matrix4x4.CreateRotationZ(0.3f) * Matrix4x4.CreateTranslation(4f, 5f, 6f);
        draws.Add(0, 0, 6, GpuTextureSlot.Unassigned, 0, CullMode.CounterClockwise,
            DirectionalShadowCasterMaterial.Opaque, in opaque);
        draws.Add(6, 4, 12, cutoutSlot, 2, CullMode.None,
            DirectionalShadowCasterMaterial.AlphaCutout, in cutout);
        DirectionalShadowPreparationStats stats = default;
        draws.Complete(generation, 7, in stats);
        return draws;
    }

    private static DirectionalShadowCascade Cascade(
        int index,
        float splitFarMeters,
        DirectionalShadowWorldBias bias) =>
        new(
            index,
            index == 0 ? 0.1f : 20f,
            splitFarMeters,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector2.Zero,
            10f,
            0.1f,
            48f,
            bias);

    private static DirectionalShadowEnvironmentState EnabledEnvironment() =>
        new(
            DirectionalShadowGateReason.Enabled,
            Vector3.Normalize(new Vector3(0.2f, 0.3f, 1f)),
            0.94f,
            0.8f,
            1.25f,
            AuthoredCelestialShadowSourceKind.Sun,
            SourceObjectIndex: 0,
            SourceGfxObjId: AuthoredCelestialShadowSourceResolver.SunGfxObjId);

    private static DirectionalShadowTerrainPreparedDraws CreateTerrainDraws()
    {
        var draws = new DirectionalShadowTerrainPreparedDraws();
        Assert.True(draws.TryBegin(1, 1));
        var range = new DirectionalShadowTerrainRange(20, 60);
        draws.Add(in range);
        draws.Complete(1);
        return draws;
    }

    private static IGpuBuffer Buffer(RecordingGpuDevice device, string name, GpuBufferUsage usage) =>
        device.CreateBuffer(new GpuBufferDescription(
            name,
            4096,
            usage | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));

    private static WorldTransformFrameSlice PublishSharedTransforms(
        IGpuFrame frame,
        ReadOnlySpan<Matrix4x4> transforms)
    {
        GpuRingAllocation allocation = frame.AllocateRing(
            checked((int)WorldTransformCapacityPolicy.InitialBindingSizeBytes),
            GpuRingUsage.Storage);
        if (!transforms.IsEmpty)
            MemoryMarshal.AsBytes(transforms).CopyTo(allocation.Data);
        return new WorldTransformFrameSlice(
            frame.Serial,
            allocation.Buffer,
            allocation.OffsetBytes,
            WorldTransformCapacityPolicy.InitialBindingSizeBytes,
            FirstInstance: 0,
            checked((uint)transforms.Length));
    }

    private static int Offset(string field) =>
        checked((int)Marshal.OffsetOf<DirectionalShadowUniforms>(field));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
