using System.Numerics;
using AcDream.App.Plugins;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Packs;
using AcDream.App.Settings;
using AcDream.Plugin.Abstractions.Rendering;
using AcDream.UI.Abstractions.Panels.Settings;
using Silk.NET.Vulkan;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class RenderPackControllerTests
{
    private static RenderPackActivationExtent Extent => new(1280, 720, 4);

    [Fact]
    public void Discovery_buffers_descriptor_without_opening_assets()
    {
        var assets = new StubAssets();
        using var registry = new BufferedRenderPackRegistry();

        using IDisposable registration = registry.Register(Descriptor(), assets);
        IReadOnlyList<BufferedRenderPackRegistration> snapshot = registry.Snapshot();

        Assert.Single(snapshot);
        Assert.Equal("test.pack", snapshot[0].Descriptor.Id);
        Assert.Equal(0, assets.OpenCount);
    }

    [Fact]
    public void Withdrawing_registration_removes_catalog_entry()
    {
        using var registry = new BufferedRenderPackRegistry();
        IDisposable registration = registry.Register(Descriptor(), new StubAssets());
        Assert.Single(registry.Snapshot());

        registration.Dispose();

        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public void Discovery_rejects_a_missing_user_facing_feature_summary()
    {
        RenderPackValidationResult result = RenderPackValidator.ValidateDescriptor(
            Descriptor() with { FeatureSummary = "   " },
            RenderPackHostCapabilities.Conformance);

        Assert.False(result.Success);
        Assert.Equal("Pack 'test.pack' has no feature summary.", result.Reason);
    }

    [Fact]
    public void UnifiedShadowSubmissionHintRequiresTheLowDirectionalShadowGraph()
    {
        RenderQualityPreset low = Preset() with
        {
            Semantic = RenderQualitySemantic.Low,
            ExecutionHints =
                RenderQualityExecutionHints.MultiviewDirectionalShadowCascades,
        };
        RenderPackValidationResult result = RenderPackValidator.ValidateDescriptor(
            Descriptor() with { QualityPresets = [low] },
            RenderPackHostCapabilities.Conformance);

        Assert.False(result.Success);
        Assert.Contains(
            "without the directional-shadow graph",
            result.Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Retail_request_builds_nothing_and_keeps_no_runtime()
    {
        using var registry = new BufferedRenderPackRegistry();
        var factory = new StubFactory();
        using var controller = Controller(registry, factory);

        controller.Request(RenderPackSelectionSettings.Retail);
        RenderPackActivationSnapshot snapshot = controller.ApplyAtFrameBoundary(Extent);

        Assert.Equal(RenderPackActivationState.Retail, snapshot.State);
        Assert.Null(controller.ActiveRuntime);
        Assert.Equal(0, factory.BuildCount);
    }

    [Fact]
    public void Selected_noop_pack_validates_assets_then_activates_atomically()
    {
        var assets = new StubAssets();
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(Descriptor(), assets);
        var factory = new StubFactory();
        using var controller = Controller(registry, factory);
        controller.Request(Selection());

        RenderPackActivationSnapshot snapshot = controller.ApplyAtFrameBoundary(Extent);

        Assert.Equal(RenderPackActivationState.Active, snapshot.State);
        Assert.NotNull(controller.ActiveRuntime);
        Assert.Equal(1, factory.BuildCount);
        Assert.Equal(0, assets.OpenCount);
    }

    [Fact]
    public void PipelineCreationConsumesTheSingleImmutableValidatedAssetSnapshot()
    {
        const string vertexKey = "stateful.vert.spv";
        const string fragmentKey = "stateful.frag.spv";
        string shaderDirectory = Path.Combine(
            RepositoryRoot(), "src", "AcDream.App", "Rendering", "Shaders", "spv");
        byte[] expectedVertex = File.ReadAllBytes(
            Path.Combine(shaderDirectory, "atmospheric_filmic.vert.spv"));
        byte[] expectedFragment = File.ReadAllBytes(
            Path.Combine(shaderDirectory, "atmospheric_filmic.frag.spv"));
        var assets = new StatefulAssets(new Dictionary<string, byte[]>
        {
            [vertexKey] = expectedVertex,
            [fragmentKey] = expectedFragment,
        });
        RenderPackDescriptor descriptor = Descriptor() with
        {
            Id = "stateful.pack",
            RequiredCapabilities =
            [
                RenderCapability.MainWorldColorIntermediate,
                RenderCapability.FullscreenPasses,
            ],
            Passes =
            [
                new RenderPassDeclaration(
                    "filmic",
                    RenderPassHook.ToneMap,
                    vertexKey,
                    fragmentKey,
                    [RenderSemanticInput.WorldColor, RenderSemanticInput.FrameTime],
                    [],
                    []),
            ],
        };
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(descriptor, assets);
        var device = new RecordingGpuDevice();
        var factory = new AtmosphericRenderPackRuntimeFactory(device);
        using var controller = new RenderPackController(
            () => RenderPackCatalog.Build(
                registry.Snapshot(),
                RenderPackHostCapabilities.Conformance),
            factory,
            preparationScheduler: InlineRenderPackPreparationScheduler.Instance);
        controller.Request(new RenderPackSelectionSettings(
            descriptor.Id,
            descriptor.PackVersion.ToString(),
            "low"));

        RenderPackActivationSnapshot snapshot = controller.ApplyAtFrameBoundary(Extent);

        Assert.Equal(RenderPackActivationState.Active, snapshot.State);
        Assert.Equal(1, assets.OpenCount(vertexKey));
        Assert.Equal(1, assets.OpenCount(fragmentKey));
        RecordingGpuPipeline pipeline = Assert.Single(device.CreatedPipelines);
        Assert.Equal(expectedVertex, pipeline.Description.Shaders.VertexSpirv.ToArray());
        Assert.Equal(expectedFragment, pipeline.Description.Shaders.FragmentSpirv.ToArray());
    }

    [Fact]
    public void Valid_user_setting_overrides_are_forwarded_by_stable_id()
    {
        RenderPackDescriptor descriptor = Descriptor() with
        {
            Settings = Settings(),
        };
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(descriptor, new StubAssets());
        var factory = new StubFactory();
        using var controller = Controller(registry, factory);
        RenderPackSettingOverrides overrides = new Dictionary<string, string>
        {
            ["enabled"] = "true",
            ["exposure"] = "1.25",
            ["samples"] = "4",
            ["quality"] = "high",
        }.ToRenderPackOverrides();

        controller.Request(Selection() with { SettingOverrides = overrides });
        RenderPackActivationSnapshot snapshot = controller.ApplyAtFrameBoundary(Extent);

        Assert.Equal(RenderPackActivationState.Active, snapshot.State);
        Assert.Equal(overrides, factory.LastUserSettingOverrides);
    }

    [Fact]
    public void Unknown_user_setting_override_atomically_retires_to_retail()
    {
        RenderPackDescriptor descriptor = Descriptor() with { Settings = Settings() };
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(descriptor, new StubAssets());
        var factory = new StubFactory();
        using var controller = Controller(registry, factory);
        controller.Request(Selection());
        controller.ApplyAtFrameBoundary(Extent);
        StubRuntime active = Assert.IsType<StubRuntime>(controller.ActiveRuntime);

        controller.Request(Selection() with
        {
            SettingOverrides = new RenderPackSettingOverrides(
                new Dictionary<string, string> { ["removed-setting"] = "1" }),
        });
        RenderPackActivationSnapshot snapshot = controller.ApplyAtFrameBoundary(Extent);

        Assert.Equal(RenderPackActivationState.FailedToRetail, snapshot.State);
        Assert.Equal(
            "Render pack 'test.pack' has a user override for unknown setting 'removed-setting'.",
            snapshot.Reason);
        Assert.True(active.Disposed);
        Assert.Null(controller.ActiveRuntime);
        Assert.Equal(1, factory.BuildCount);
    }

    [Theory]
    [InlineData("enabled", "yes", "Boolean")]
    [InlineData("exposure", "1,25", "Float")]
    [InlineData("exposure", "1.1", "Float")]
    [InlineData("samples", "3", "Integer")]
    [InlineData("samples", "12", "Integer")]
    [InlineData("quality", "ultra", "Choice")]
    public void Invalid_user_setting_value_fails_to_retail_without_building(
        string settingId,
        string value,
        string kind)
    {
        RenderPackDescriptor descriptor = Descriptor() with { Settings = Settings() };
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(descriptor, new StubAssets());
        var factory = new StubFactory();
        using var controller = Controller(registry, factory);
        controller.Request(Selection() with
        {
            SettingOverrides = new RenderPackSettingOverrides(
                new Dictionary<string, string> { [settingId] = value }),
        });

        RenderPackActivationSnapshot snapshot = controller.ApplyAtFrameBoundary(Extent);

        Assert.Equal(RenderPackActivationState.FailedToRetail, snapshot.State);
        Assert.Equal(
            $"Render pack 'test.pack' user override '{settingId}' has invalid {kind} value '{value}'.",
            snapshot.Reason);
        Assert.Null(controller.ActiveRuntime);
        Assert.Equal(0, factory.BuildCount);
    }

    [Fact]
    public void Invalid_spirv_fails_complete_pack_to_retail_and_does_not_retry()
    {
        RenderPackDescriptor descriptor = Descriptor() with
        {
            Passes =
            [
                new RenderPassDeclaration(
                    "tone-map",
                    RenderPassHook.ToneMap,
                    "shaders/fullscreen.vert.spv",
                    "shaders/tone-map.frag.spv",
                    [RenderSemanticInput.WorldColor],
                    [],
                    []),
            ],
        };
        var assets = new StubAssets([1, 2, 3, 4]);
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(descriptor, assets);
        var factory = new StubFactory();
        using var controller = Controller(registry, factory);

        controller.Request(Selection());
        RenderPackActivationSnapshot first = controller.ApplyAtFrameBoundary(Extent);
        controller.Request(Selection());
        RenderPackActivationSnapshot second = controller.ApplyAtFrameBoundary(Extent);

        Assert.Equal(RenderPackActivationState.FailedToRetail, first.State);
        Assert.Contains("not valid SPIR-V", first.Reason, StringComparison.Ordinal);
        Assert.Equal(RenderPackActivationState.FailedToRetail, second.State);
        Assert.Contains("will not be retried", second.Reason, StringComparison.Ordinal);
        Assert.Equal(0, factory.BuildCount);
        Assert.Equal(1, assets.OpenCount);
    }

    [Fact]
    public void An_explicit_user_request_retries_a_selection_that_failed_earlier()
    {
        RenderPackDescriptor descriptor = Descriptor() with
        {
            Passes =
            [
                new RenderPassDeclaration(
                    "tone-map",
                    RenderPassHook.ToneMap,
                    "shaders/fullscreen.vert.spv",
                    "shaders/tone-map.frag.spv",
                    [RenderSemanticInput.WorldColor],
                    [],
                    []),
            ],
        };
        var assets = new StubAssets([1, 2, 3, 4]);
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(descriptor, assets);
        var factory = new StubFactory();
        using var controller = Controller(registry, factory);

        controller.Request(Selection());
        RenderPackActivationSnapshot first = controller.ApplyAtFrameBoundary(Extent);
        controller.Request(Selection(), explicitUserChoice: true);
        RenderPackActivationSnapshot second = controller.ApplyAtFrameBoundary(Extent);

        Assert.Equal(RenderPackActivationState.FailedToRetail, first.State);
        Assert.Equal(RenderPackActivationState.FailedToRetail, second.State);
        Assert.Contains("not valid SPIR-V", second.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("will not be retried", second.Reason, StringComparison.Ordinal);
        Assert.Equal(2, assets.OpenCount);
    }

    [Fact]
    public void Arbitrary_plugin_asset_exception_is_contained_as_a_retail_fallback()
    {
        RenderPackDescriptor descriptor = Descriptor() with
        {
            Passes =
            [
                new RenderPassDeclaration(
                    "tone-map",
                    RenderPassHook.ToneMap,
                    "fullscreen.vert.spv",
                    "tone-map.frag.spv",
                    [],
                    [],
                    []),
            ],
        };
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(
            descriptor,
            new ThrowingAssets(new InvalidOperationException("plugin stream failed")));
        var factory = new StubFactory();
        using var controller = Controller(registry, factory);

        controller.Request(Selection());
        RenderPackActivationSnapshot snapshot = controller.ApplyAtFrameBoundary(Extent);

        Assert.Equal(RenderPackActivationState.FailedToRetail, snapshot.State);
        Assert.Contains("plugin stream failed", snapshot.Reason, StringComparison.Ordinal);
        Assert.Null(controller.ActiveRuntime);
        Assert.Equal(0, factory.BuildCount);
    }

    [Fact]
    public void Candidate_build_failure_disposes_old_runtime_and_returns_to_retail()
    {
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(Descriptor(), new StubAssets());
        var factory = new StubFactory();
        using var controller = Controller(registry, factory);
        controller.Request(Selection());
        controller.ApplyAtFrameBoundary(Extent);
        StubRuntime first = Assert.IsType<StubRuntime>(controller.ActiveRuntime);
        factory.Failure = new InvalidOperationException("pipeline rejected");

        controller.Request(Selection() with { PresetId = "medium" });
        RenderPackActivationSnapshot failed = controller.ApplyAtFrameBoundary(Extent);

        Assert.Equal(RenderPackActivationState.FailedToRetail, failed.State);
        Assert.Contains("pipeline rejected", failed.Reason, StringComparison.Ordinal);
        Assert.True(first.Disposed);
        Assert.Null(controller.ActiveRuntime);
    }

    [Theory]
    [InlineData(Result.ErrorDeviceLost)]
    [InlineData(Result.ErrorOutOfHostMemory)]
    [InlineData(Result.ErrorOutOfDeviceMemory)]
    public void FatalVulkanCandidatePreparationDisposesCandidateAndRethrows(Result result)
    {
        RenderPackDescriptor descriptor = Descriptor();
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(descriptor, new StubAssets());
        var failure = new VulkanCallException("candidate target", result);
        var runtime = new ThrowingGraphRuntime(descriptor, Preset(), failure);
        var factory = new StubFactory
        {
            RuntimeFactory = (_, _) => runtime,
        };
        using var controller = Controller(registry, factory);
        controller.Request(Selection());

        VulkanCallException thrown = Assert.Throws<VulkanCallException>(
            () => controller.ApplyAtFrameBoundary(Extent));

        Assert.Same(failure, thrown);
        Assert.True(runtime.Disposed);
        Assert.Null(controller.ActiveRuntime);
    }

    [Fact]
    public void NonTerminalVulkanCandidateFailureDisposesCandidateAndFallsBack()
    {
        RenderPackDescriptor descriptor = Descriptor();
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(descriptor, new StubAssets());
        var runtime = new ThrowingGraphRuntime(
            descriptor,
            Preset(),
            new VulkanCallException("candidate target", Result.ErrorFormatNotSupported));
        var factory = new StubFactory
        {
            RuntimeFactory = (_, _) => runtime,
        };
        using var controller = Controller(registry, factory);
        controller.Request(Selection());

        RenderPackActivationSnapshot snapshot = controller.ApplyAtFrameBoundary(Extent);

        Assert.Equal(RenderPackActivationState.FailedToRetail, snapshot.State);
        Assert.Contains("ErrorFormatNotSupported", snapshot.Reason, StringComparison.Ordinal);
        Assert.True(runtime.Disposed);
        Assert.Null(controller.ActiveRuntime);
    }

    [Fact]
    public void Active_pack_remains_renderable_until_completed_candidate_publishes_at_boundary()
    {
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(Descriptor(), new StubAssets());
        var factory = new StubFactory();
        var scheduler = new ControlledPreparationScheduler();
        using var controller = new RenderPackController(
            () => RenderPackCatalog.Build(
                registry.Snapshot(),
                RenderPackHostCapabilities.Conformance),
            factory,
            preparationScheduler: scheduler);

        controller.Request(Selection());
        RenderPackActivationSnapshot initialPending =
            controller.ApplyAtFrameBoundary(Extent);
        Assert.Equal(RenderPackActivationState.CandidatePending, initialPending.State);
        Assert.Null(controller.ActiveRuntime);
        scheduler.CompleteNext();
        Assert.Null(controller.ActiveRuntime);
        controller.ApplyAtFrameBoundary(Extent);
        StubRuntime first = Assert.IsType<StubRuntime>(controller.ActiveRuntime);

        controller.Request(Selection() with { PresetId = "medium" });
        RenderPackActivationSnapshot replacementPending =
            controller.ApplyAtFrameBoundary(Extent);

        Assert.Equal(RenderPackActivationState.CandidatePending, replacementPending.State);
        Assert.Same(first, controller.ActiveRuntime);
        Assert.False(first.Disposed);
        scheduler.CompleteNext();
        Assert.Same(first, controller.ActiveRuntime);
        Assert.False(first.Disposed);

        RenderPackActivationSnapshot published = controller.ApplyAtFrameBoundary(Extent);

        Assert.Equal(RenderPackActivationState.Active, published.State);
        StubRuntime second = Assert.IsType<StubRuntime>(controller.ActiveRuntime);
        Assert.NotSame(first, second);
        Assert.Equal("medium", second.Preset.Id);
        Assert.True(first.Disposed);
    }

    [Fact]
    public void Preparation_failure_is_diagnostic_and_only_falls_back_at_a_boundary()
    {
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(Descriptor(), new StubAssets());
        var factory = new StubFactory();
        var scheduler = new ControlledPreparationScheduler();
        using var controller = new RenderPackController(
            () => RenderPackCatalog.Build(
                registry.Snapshot(),
                RenderPackHostCapabilities.Conformance),
            factory,
            preparationScheduler: scheduler);

        controller.Request(Selection());
        controller.ApplyAtFrameBoundary(Extent);
        scheduler.CompleteNext();
        controller.ApplyAtFrameBoundary(Extent);
        StubRuntime first = Assert.IsType<StubRuntime>(controller.ActiveRuntime);

        factory.Failure = new InvalidOperationException("background pipeline rejected");
        controller.Request(Selection() with { PresetId = "medium" });
        controller.ApplyAtFrameBoundary(Extent);
        scheduler.CompleteNext();

        Assert.Same(first, controller.ActiveRuntime);
        Assert.False(first.Disposed);
        Assert.Equal(RenderPackActivationState.CandidatePending, controller.Snapshot.State);

        RenderPackActivationSnapshot failed = controller.ApplyAtFrameBoundary(Extent);

        Assert.Equal(RenderPackActivationState.FailedToRetail, failed.State);
        Assert.Contains("could not be prepared", failed.Reason, StringComparison.Ordinal);
        Assert.Contains("background pipeline rejected", failed.Reason, StringComparison.Ordinal);
        Assert.True(first.Disposed);
        Assert.Null(controller.ActiveRuntime);
    }

    [Fact]
    public void Descriptor_validation_reports_exact_missing_capability()
    {
        RenderPackDescriptor descriptor = Descriptor() with
        {
            RequiredCapabilities = [RenderCapability.DirectionalShadowMaps],
        };
        var capabilities = new RenderPackHostCapabilities(
            new HashSet<RenderCapability>(),
            4096,
            4,
            64L * 1024 * 1024);

        RenderPackValidationResult result =
            RenderPackValidator.ValidateDescriptor(descriptor, capabilities);

        Assert.False(result.Success);
        Assert.Equal(
            "Pack 'test.pack' requires unsupported capability 'DirectionalShadowMaps'.",
            result.Reason);
    }

    [Fact]
    public void Malformed_null_preset_list_remains_a_precisely_incompatible_catalog_entry()
    {
        RenderPackDescriptor malformed = Descriptor() with
        {
            QualityPresets = null!,
        };
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(malformed, new StubAssets());

        RenderPackCatalog catalog = RenderPackCatalog.Build(
            registry.Snapshot(),
            RenderPackHostCapabilities.Conformance);

        RenderPackCatalogEntry entry = Assert.Single(catalog.Entries);
        Assert.False(entry.IsCompatible);
        Assert.Equal(
            "Pack 'test.pack' has a null quality-preset declaration list.",
            entry.IncompatibilityReason);
        Assert.Empty(entry.PresetIncompatibilityReasons);
    }

    [Fact]
    public void Diagnostics_record_retail_failure_and_active_pack_ownership_without_gpu_waits()
    {
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(Descriptor(), new StubAssets());
        var factory = new StubFactory();
        using var controller = Controller(registry, factory);

        RenderPackDiagnosticsSnapshot retail = controller.CaptureDiagnostics();
        controller.Request(Selection());
        controller.ApplyAtFrameBoundary(Extent);
        RenderPackDiagnosticsSnapshot active = controller.CaptureDiagnostics();

        Assert.True(retail.IsRetail);
        Assert.Equal(RenderPackActivationState.Retail, retail.State);
        Assert.Equal("test.pack", active.PackId);
        Assert.Equal("low", active.PresetId);
        Assert.Equal("low", active.EffectiveQuality);
        Assert.Equal(RenderPackActivationState.Active, active.State);
        Assert.Contains("pack=test.pack@1.0.0", RenderPackDiagnosticsFormatter.Format(active));
    }

    [Theory]
    [InlineData(
        AuthoredCelestialShadowSourceKind.Sun,
        5,
        0x01001348u,
        0.25f,
        -0.5f,
        0.8291562f,
        0.8291562f,
        "shadowSource=Sun/obj5/0x01001348/dir(0.2500,-0.5000,0.8292)/elevSin=0.8292")]
    [InlineData(
        AuthoredCelestialShadowSourceKind.DominantMoon,
        3,
        0x01001F6Au,
        -0.6f,
        0.2f,
        0.7745967f,
        0.7745967f,
        "shadowSource=DominantMoon/obj3/0x01001F6A/dir(-0.6000,0.2000,0.7746)/elevSin=0.7746")]
    [InlineData(
        AuthoredCelestialShadowSourceKind.SecondaryMoon,
        2,
        0x01001F67u,
        0.4f,
        0.8f,
        0.4472136f,
        0.4472136f,
        "shadowSource=SecondaryMoon/obj2/0x01001F67/dir(0.4000,0.8000,0.4472)/elevSin=0.4472")]
    [InlineData(
        AuthoredCelestialShadowSourceKind.None,
        -1,
        0u,
        0f,
        0f,
        1f,
        0f,
        "shadowSource=None/obj-1/0x00000000/dir(0.0000,0.0000,1.0000)/elevSin=0.0000")]
    internal void DiagnosticsPropagateSunMoonAndNoneMetadataToStableFormattedOutput(
        AuthoredCelestialShadowSourceKind sourceKind,
        int sourceObjectIndex,
        uint sourceGfxObjId,
        float directionX,
        float directionY,
        float directionZ,
        float elevationSin,
        string expectedFormattedSource)
    {
        var direction = new Vector3(directionX, directionY, directionZ);
        RenderPackRuntimeDiagnostics runtimeDiagnostics =
            RenderPackRuntimeDiagnostics.Empty("low") with
            {
                DirectionalShadowSourceKind = sourceKind,
                DirectionalShadowSourceObjectIndex = sourceObjectIndex,
                DirectionalShadowSourceGfxObjId = sourceGfxObjId,
                DirectionalShadowSurfaceToLightDirection = direction,
                DirectionalShadowLightElevationSin = elevationSin,
            };
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(
            Descriptor(),
            new StubAssets());
        var factory = new StubFactory
        {
            RuntimeFactory = (descriptor, preset) =>
                new DiagnosticStubRuntime(
                    descriptor,
                    preset,
                    runtimeDiagnostics),
        };
        using var controller = Controller(registry, factory);
        controller.Request(Selection());
        controller.ApplyAtFrameBoundary(Extent);

        RenderPackDiagnosticsSnapshot snapshot = controller.CaptureDiagnostics();
        string formatted = RenderPackDiagnosticsFormatter.Format(snapshot);

        Assert.Equal(sourceKind, snapshot.DirectionalShadowSourceKind);
        Assert.Equal(
            sourceObjectIndex,
            snapshot.DirectionalShadowSourceObjectIndex);
        Assert.Equal(sourceGfxObjId, snapshot.DirectionalShadowSourceGfxObjId);
        Assert.Equal(
            direction,
            snapshot.DirectionalShadowSurfaceToLightDirection);
        Assert.Equal(elevationSin, snapshot.DirectionalShadowLightElevationSin);
        Assert.Contains(expectedFormattedSource, formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void Deferred_diagnostics_are_retail_before_bind_and_after_owner_release()
    {
        var deferred = new DeferredRenderPackDiagnosticsSource();
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(Descriptor(), new StubAssets());
        var factory = new StubFactory();
        using var controller = Controller(registry, factory);

        Assert.True(deferred.CaptureDiagnostics().IsRetail);
        using (deferred.BindOwned(controller))
        {
            controller.Request(Selection());
            controller.ApplyAtFrameBoundary(Extent);
            Assert.Equal("test.pack", deferred.CaptureDiagnostics().PackId);
        }

        Assert.True(deferred.CaptureDiagnostics().IsRetail);
    }

    [Fact]
    public void Selection_binding_activates_only_at_boundary_and_persists_failed_fallback()
    {
        var storage = new SelectionStorage();
        var settings = new RuntimeSettingsController(storage, log: static _ => { });
        using var registry = new BufferedRenderPackRegistry();
        using IDisposable registration = registry.Register(Descriptor(), new StubAssets());
        var factory = new StubFactory();
        using var controller = Controller(registry, factory);
        using var binding = new RenderPackSelectionBinding(settings, controller);

        binding.ApplyAtFrameBoundary(Extent);
        settings.SaveDisplay(settings.Display with { RenderPack = Selection() });
        Assert.Null(controller.ActiveRuntime);

        binding.ApplyAtFrameBoundary(Extent);
        Assert.NotNull(controller.ActiveRuntime);

        factory.Failure = new InvalidOperationException("pipeline rejected");
        settings.SaveDisplay(settings.Display with
        {
            RenderPack = Selection() with { PresetId = "medium" },
        });
        RenderPackActivationSnapshot failed = binding.ApplyAtFrameBoundary(Extent);

        Assert.Equal(RenderPackActivationState.FailedToRetail, failed.State);
        Assert.True(settings.Display.RenderPack.IsRetail);
        Assert.Contains("pipeline rejected", controller.Snapshot.Reason, StringComparison.Ordinal);
        Assert.Null(controller.ActiveRuntime);

        factory.Failure = null;
        settings.SaveDisplay(settings.Display with
        {
            RenderPack = Selection() with { PresetId = "medium" },
        });
        RenderPackActivationSnapshot retried = binding.ApplyAtFrameBoundary(Extent);

        Assert.NotEqual(RenderPackActivationState.FailedToRetail, retried.State);
        Assert.DoesNotContain("will not be retried", retried.Reason ?? string.Empty, StringComparison.Ordinal);
        Assert.NotNull(controller.ActiveRuntime);
    }

    [Fact]
    public void Built_in_atmospheric_pack_is_a_public_contract_conformant_tier2plus_pack()
    {
        RenderPackValidationResult result = RenderPackValidator.ValidateDescriptor(
            BuiltInAtmosphericRenderPack.Descriptor,
            RenderPackHostCapabilities.Conformance);

        Assert.True(result.Success, result.Reason);
        Assert.Equal(RenderPackTier.Tier2Plus, BuiltInAtmosphericRenderPack.Descriptor.HighestTier);
        Assert.Contains(
            BuiltInAtmosphericRenderPack.Descriptor.SceneReplays,
            replay => replay.CasterClasses.HasFlag(RenderCasterClass.Terrain)
                && replay.CasterClasses.HasFlag(RenderCasterClass.OpaqueWorld)
                && replay.CasterClasses.HasFlag(RenderCasterClass.AlphaCutoutWorld)
                && replay.CasterClasses.HasFlag(RenderCasterClass.AnimatedOpaque)
                && replay.CasterClasses.HasFlag(RenderCasterClass.AnimatedAlphaCutout));
        Assert.Contains(BuiltInAtmosphericRenderPack.Descriptor.QualityPresets, value => value.Id == "low");
        Assert.Contains(BuiltInAtmosphericRenderPack.Descriptor.QualityPresets, value => value.Id == "medium");
        Assert.Contains(BuiltInAtmosphericRenderPack.Descriptor.QualityPresets, value => value.Id == "high");
        Assert.Contains(BuiltInAtmosphericRenderPack.Descriptor.QualityPresets, value => value.Id == "auto");
        Assert.Equal(
            "0.8",
            BuiltInAtmosphericRenderPack
                .Descriptor
                .Settings
                .Single(value => value.Semantic == RenderSettingSemantic.Exposure)
                .DefaultValue);
        Assert.Equal(0.25, ResourceScale("low", "volumetric"));
        Assert.Equal(0.25, ResourceScale("medium", "volumetric"));
        Assert.Equal(0.5, ResourceScale("high", "volumetric"));
        Assert.Equal(0.25, ResourceScale("low", "sun-rays"));
        Assert.Equal(0.5, ResourceScale("medium", "sun-rays"));
        Assert.Equal(0.5, ResourceScale("high", "sun-rays"));
        Assert.Equal(0.25, ResourceScale("low", "sun-mask"));
        Assert.Equal(0.5, ResourceScale("medium", "sun-mask"));
        Assert.Equal(0.5, ResourceScale("high", "sun-mask"));

        double ResourceScale(string presetId, string resourceId) =>
            BuiltInAtmosphericRenderPack
            .Descriptor
            .QualityPresets
            .Single(value => value.Id == presetId)
            .ResourceOverrides
            .Single(value => value.ResourceId == resourceId)
            .Extent!
            .Width;
    }

    [Fact]
    public void Atmospheric_executor_rejects_a_resource_semantic_with_the_wrong_shape()
    {
        RenderPackDescriptor source = BuiltInAtmosphericRenderPack.Descriptor;
        RenderPackDescriptor descriptor = source with
        {
            Resources = source.Resources.Select(value =>
                value.Semantic == RenderResourceSemantic.DirectionalShadowDepth
                    ? value with { Kind = RenderResourceKind.Image2D }
                    : value).ToArray(),
        };

        RenderPackValidationResult result = RenderPackValidator.ValidateDescriptor(
            descriptor,
            RenderPackHostCapabilities.Conformance);

        Assert.False(result.Success);
        Assert.Contains(
            "does not match the fixed atmospheric executor's kind, format, extent, usage, and lifetime contract",
            result.Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Atmospheric_executor_rejects_a_variant_semantic_with_the_wrong_shape()
    {
        RenderPackDescriptor source = BuiltInAtmosphericRenderPack.Descriptor;
        RenderPackDescriptor descriptor = source with
        {
            PipelineVariants = source.PipelineVariants.Select(value =>
                value.Semantic == RenderPipelineVariantSemantic.WorldAlphaCutoutDirectionalShadowCaster
                    ? value with { CompatibleMaterials = RenderMaterialClass.Opaque }
                    : value).ToArray(),
        };

        RenderPackValidationResult result = RenderPackValidator.ValidateDescriptor(
            descriptor,
            RenderPackHostCapabilities.Conformance);

        Assert.False(result.Success);
        Assert.Contains(
            "does not match the fixed atmospheric executor's base, material, and input contract",
            result.Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Atmospheric_executor_rejects_a_replay_that_drops_a_headline_caster_class()
    {
        RenderPackDescriptor source = BuiltInAtmosphericRenderPack.Descriptor;
        RenderPackDescriptor descriptor = source with
        {
            SceneReplays = source.SceneReplays.Select(value => value with
            {
                CasterClasses = value.CasterClasses & ~RenderCasterClass.AnimatedAlphaCutout,
            }).ToArray(),
        };

        RenderPackValidationResult result = RenderPackValidator.ValidateDescriptor(
            descriptor,
            RenderPackHostCapabilities.Conformance);

        Assert.False(result.Success);
        Assert.Contains("all five headline caster classes", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Api_v1_rejects_publicly_reserved_buffer_and_storage_declarations()
    {
        RenderPackDescriptor descriptor = Descriptor() with
        {
            Resources =
            [
                new RenderResourceDeclaration(
                    "future-buffer",
                    RenderResourceKind.Buffer,
                    RenderFormatClass.StructuredData,
                    Extent: null,
                    SizeBytes: 64,
                    RenderResourceUsage.Storage,
                    RenderResourceLifetime.ActivePack,
                    EstimatedResidentBytes: 64),
            ],
        };

        RenderPackValidationResult result = RenderPackValidator.ValidateDescriptor(
            descriptor,
            RenderPackHostCapabilities.Conformance);

        Assert.False(result.Success);
        Assert.Contains("reserved for a future render-pack API", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Api_v1_rejects_a_pass_that_exceeds_four_ordered_texture_slots()
    {
        RenderResourceDeclaration Resource(string id) => new(
            id,
            RenderResourceKind.Image2D,
            RenderFormatClass.HdrColor,
            new RenderExtentDeclaration(RenderExtentMode.RelativeToMainWorld, 0.5, 0.5),
            SizeBytes: 0,
            RenderResourceUsage.Sampled | RenderResourceUsage.ColorAttachment,
            RenderResourceLifetime.ActivePack,
            EstimatedResidentBytes: 1024);
        RenderPassDeclaration Writer(string id) => new(
            $"write-{id}",
            RenderPassHook.AtmosphereBeforeToneMap,
            "fullscreen.vert.spv",
            "write.frag.spv",
            [],
            [],
            [id]);
        string[] resourceIds = ["a", "b", "c", "d"];
        RenderPackDescriptor descriptor = Descriptor() with
        {
            Resources = resourceIds.Select(Resource).ToArray(),
            Passes =
            [
                .. resourceIds.Select(Writer),
                new RenderPassDeclaration(
                    "too-many-inputs",
                    RenderPassHook.ToneMap,
                    "fullscreen.vert.spv",
                    "tone.frag.spv",
                    [RenderSemanticInput.WorldColor],
                    resourceIds,
                    []),
            ],
        };

        RenderPackValidationResult result = RenderPackValidator.ValidateDescriptor(
            descriptor,
            RenderPackHostCapabilities.Conformance);

        Assert.False(result.Success);
        Assert.Contains("provides four ordered texture slots", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Built_in_filmic_pass_maps_exactly_to_texture_slots_a_through_d()
    {
        RenderPackDescriptor descriptor = BuiltInAtmosphericRenderPack.Descriptor;
        RenderPassDeclaration pass = descriptor.Passes.Single(value =>
            value.Id == "filmic-composite");
        IReadOnlyList<RenderPackTextureInput> slots = RenderPackTextureBindingResolver.Resolve(
            pass,
            descriptor.Resources.ToDictionary(static value => value.Id));

        Assert.Equal(4, slots.Count);
        Assert.Equal(RenderSemanticInput.WorldColor, slots[0].Semantic);
        Assert.Equal("bloom-a", slots[1].ResourceId);
        Assert.Equal("sun-rays", slots[2].ResourceId);
        Assert.Equal("volumetric", slots[3].ResourceId);
    }

    [Theory]
    [InlineData("../escape.spv")]
    [InlineData("/absolute.spv")]
    [InlineData("shaders\\escape.spv")]
    public void Selected_asset_validation_rejects_unsafe_keys(string key)
    {
        RenderPackDescriptor descriptor = Descriptor() with
        {
            Passes =
            [
                new RenderPassDeclaration(
                    "tone-map",
                    RenderPassHook.ToneMap,
                    key,
                    "safe.frag.spv",
                    [],
                    [],
                    []),
            ],
        };
        var assets = new StubAssets([3, 2, 35, 7]);

        RenderPackValidationResult result =
            RenderPackValidator.ValidateSelectedAssets(descriptor, assets);

        Assert.False(result.Success);
        Assert.Contains("unsafe asset key", result.Reason, StringComparison.Ordinal);
        Assert.Equal(0, assets.OpenCount);
    }

    private static RenderPackController Controller(
        BufferedRenderPackRegistry registry,
        StubFactory factory) => new(
            () => RenderPackCatalog.Build(
                registry.Snapshot(),
                RenderPackHostCapabilities.Conformance),
            factory,
            preparationScheduler: InlineRenderPackPreparationScheduler.Instance);

    private static RenderPackSelectionSettings Selection() => new(
        "test.pack",
        "1.0.0",
        "low");

    private static RenderPackDescriptor Descriptor() => new(
        "test.pack",
        "Test Pack",
        new Version(1, 0, 0),
        RenderPackApi.Current,
        RenderPackTier.Tier1,
        [],
        [],
        [],
        [],
        [],
        [],
        [Preset(), Preset() with { Id = "medium", DisplayName = "Medium" }],
        [],
        null)
    {
        FeatureSummary = "Test render pack.",
    };

    private static RenderQualityPreset Preset() => new(
        "low",
        "Low",
        [],
        [],
        [],
        64L * 1024 * 1024,
        2.0,
        3.0,
        0.15,
        0.50);

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static RenderSettingDeclaration[] Settings() =>
    [
        new("enabled", "Enabled", RenderSettingKind.Boolean, "false", null, null, null, []),
        new("exposure", "Exposure", RenderSettingKind.Float, "1", 0, 2, 0.25, []),
        new("samples", "Samples", RenderSettingKind.Integer, "2", 0, 10, 2, []),
        new("quality", "Quality", RenderSettingKind.Choice, "low", null, null, null,
            ["low", "high"]),
    ];

    private sealed class StubAssets(byte[]? bytes = null) : IRenderPackAssets
    {
        private readonly byte[] _bytes = bytes ?? [];

        internal int OpenCount { get; private set; }

        public Stream OpenRead(string assetKey)
        {
            OpenCount++;
            return new MemoryStream(_bytes, writable: false);
        }
    }

    private sealed class ThrowingAssets(Exception error) : IRenderPackAssets
    {
        public Stream OpenRead(string assetKey) => throw error;
    }

    private sealed class StatefulAssets(IReadOnlyDictionary<string, byte[]> firstValues)
        : IRenderPackAssets
    {
        private readonly Dictionary<string, int> _openCounts =
            new(StringComparer.Ordinal);

        internal int OpenCount(string key) => _openCounts.GetValueOrDefault(key);

        public Stream OpenRead(string assetKey)
        {
            int count = _openCounts.GetValueOrDefault(assetKey) + 1;
            _openCounts[assetKey] = count;
            if (count != 1)
            {
                // A second read is intentionally different, exposing any
                // validate-then-reopen TOCTOU path deterministically.
                return new MemoryStream([3, 2, 35, 7], writable: false);
            }
            return new MemoryStream(firstValues[assetKey], writable: false);
        }
    }

    private sealed class SelectionStorage : IRuntimeSettingsStorage
    {
        public SettingsStore? LayoutStore => null;

        public string Location => "memory://render-pack";

        public DisplaySettings Display { get; private set; } = DisplaySettings.Default;

        public DisplaySettings LoadDisplay() => Display;

        public AudioSettings LoadAudio() => AudioSettings.Default;

        public AcDream.Core.Audio.AudioMixerOptions LoadAudioMixer() =>
            AcDream.Core.Audio.AudioMixerOptions.Default;

        public void SaveAudioMixer(AcDream.Core.Audio.AudioMixerOptions mixer)
        {
        }

        public ChatSettings LoadChat() => ChatSettings.Default;

        public CharacterSettings LoadCharacter(string toonKey) => CharacterSettings.Default;

        public CameraTurningSettings LoadCameraTurning() => CameraTurningSettings.Default;

        public void SaveDisplay(DisplaySettings display) => Display = display;

        public void SaveAudio(AudioSettings audio)
        {
        }

        public void SaveChat(ChatSettings chat)
        {
        }

        public void SaveCameraTurning(CameraTurningSettings cameraTurning)
        {
        }
    }

    private sealed class StubFactory : IRenderPackRuntimeFactory
    {
        internal int BuildCount { get; private set; }

        internal IReadOnlyDictionary<string, string>? LastUserSettingOverrides { get; private set; }

        internal Exception? Failure { get; set; }

        internal Func<RenderPackDescriptor, RenderQualityPreset, IRenderPackRuntime>?
            RuntimeFactory { get; init; }

        public IRenderPackRuntime Build(
            RenderPackDescriptor descriptor,
            ValidatedRenderPackShaderAssets assets,
            RenderQualityPreset preset,
            IReadOnlyDictionary<string, string> userSettingOverrides)
        {
            BuildCount++;
            LastUserSettingOverrides = new Dictionary<string, string>(
                userSettingOverrides,
                StringComparer.OrdinalIgnoreCase);
            if (Failure is { } failure)
                throw failure;
            if (RuntimeFactory is { } create)
                return create(descriptor, preset);
            return new StubRuntime(descriptor, preset);
        }
    }

    private sealed class ControlledPreparationScheduler : IRenderPackPreparationScheduler
    {
        private readonly Queue<(Action Work, TaskCompletionSource Completion)> _pending = [];

        public Task Schedule(Action preparation)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue((preparation, completion));
            return completion.Task;
        }

        internal void CompleteNext()
        {
            (Action work, TaskCompletionSource completion) = _pending.Dequeue();
            try
            {
                work();
                completion.SetResult();
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        }
    }

    private sealed class StubRuntime(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset) : IRenderPackRuntime
    {
        public RenderPackDescriptor Descriptor { get; } = descriptor;

        public RenderQualityPreset Preset { get; } = preset;

        internal bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class DiagnosticStubRuntime(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        RenderPackRuntimeDiagnostics diagnostics)
        : IRenderPackRuntime, IRenderPackRuntimeDiagnosticsSource
    {
        public RenderPackDescriptor Descriptor { get; } = descriptor;

        public RenderQualityPreset Preset { get; } = preset;

        public RenderPackRuntimeDiagnostics CaptureDiagnostics() => diagnostics;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingGraphRuntime(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        Exception failure) : IAtmosphericWorldGraphRuntime
    {
        public RenderPackDescriptor Descriptor { get; } = descriptor;

        public RenderQualityPreset Preset { get; } = preset;

        internal bool Disposed { get; private set; }

        public IGpuRenderTarget PrepareWorldTarget(int width, int height, int sampleCount) =>
            throw failure;

        public void RenderPostProcess(IGpuFrame frame, in AtmosphericFrameInputs inputs) =>
            throw new InvalidOperationException("The candidate never activates.");

        public void Dispose() => Disposed = true;
    }
}

file static class RenderPackControllerTestExtensions
{
    internal static RenderPackSettingOverrides ToRenderPackOverrides(
        this IReadOnlyDictionary<string, string> values) => new(values);
}
