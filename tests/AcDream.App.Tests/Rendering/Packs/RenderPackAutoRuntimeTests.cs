using AcDream.App.Plugins;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Packs;
using AcDream.Plugin.Abstractions.Rendering;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class RenderPackAutoRuntimeTests
{
    [Fact]
    public void AutoStartsMediumAndAtomicallyPublishesPreparedLowCandidateAtBoundary()
    {
        using var fixture = new Fixture();
        fixture.Activate("auto");
        FakeRuntime medium = fixture.Active;

        Assert.Equal("medium", medium.Preset.Id);
        Assert.Equal("auto", fixture.Controller.Snapshot.Selection.PresetId);
        fixture.ObserveOverBudget(AtmosphericAutoQualityController.DowngradeHysteresisFrames);

        Assert.Same(medium, fixture.Controller.ActiveRuntime);
        Assert.False(medium.Disposed);
        Assert.Equal(1, fixture.Factory.BuildCount);

        RenderPackActivationSnapshot changed = fixture.Controller.ApplyAtFrameBoundary(
            new RenderPackActivationExtent(1920, 1080, 2));
        FakeRuntime low = fixture.Active;

        Assert.Equal(RenderPackActivationState.Active, changed.State);
        Assert.Equal("auto", changed.Selection.PresetId);
        Assert.Equal("low", low.Preset.Id);
        Assert.True(low.Prepared);
        Assert.True(medium.Disposed);
        Assert.Equal(
            ["build:medium", "prepare:medium:1280x720x4", "build:low",
                "prepare:low:1920x1080x2", "dispose:medium"],
            fixture.Events);
        Assert.Equal(0, fixture.Controller.Performance.CpuSampleCount);
        Assert.Equal(AtmosphericQualityLevel.Low,
            fixture.Controller.AutoQuality!.Value.Current);

        RenderPackDiagnosticsSnapshot diagnostics = fixture.Controller.CaptureDiagnostics();
        Assert.Equal("auto", diagnostics.PresetId);
        Assert.Equal("low", diagnostics.EffectiveQuality);
    }

    [Fact]
    public void Auto_keeps_current_quality_live_while_replacement_prepares_off_side()
    {
        var scheduler = new ControlledPreparationScheduler();
        using var fixture = new Fixture(preparationScheduler: scheduler);
        fixture.Controller.Request(new RenderPackSelectionSettings(
            "auto.test", "1.0.0", "auto"));
        fixture.Controller.ApplyAtFrameBoundary(
            new RenderPackActivationExtent(1280, 720, 4));
        scheduler.CompleteNext();
        fixture.Controller.ApplyAtFrameBoundary(
            new RenderPackActivationExtent(1280, 720, 4));
        FakeRuntime medium = fixture.Active;

        fixture.ObserveOverBudget(AtmosphericAutoQualityController.DowngradeHysteresisFrames);
        RenderPackActivationSnapshot pending = fixture.Controller.ApplyAtFrameBoundary(
            new RenderPackActivationExtent(1280, 720, 4));

        Assert.Equal(RenderPackActivationState.CandidatePending, pending.State);
        Assert.Same(medium, fixture.Controller.ActiveRuntime);
        Assert.False(medium.Disposed);
        scheduler.CompleteNext();
        Assert.Same(medium, fixture.Controller.ActiveRuntime);

        fixture.Controller.ApplyAtFrameBoundary(
            new RenderPackActivationExtent(1280, 720, 4));

        Assert.Equal("low", fixture.Active.Preset.Id);
        Assert.True(medium.Disposed);
    }

    [Fact]
    public void WeakHostStartsAutoAtLowAndCannotPromoteIntoUnavailablePresets()
    {
        using var fixture = new Fixture(new RenderPackHostCapabilities(
            Enum.GetValues<RenderCapability>().ToHashSet(),
            4096,
            4,
            64L * 1024 * 1024));

        fixture.Activate("auto");

        Assert.Equal("low", fixture.Active.Preset.Id);
        Assert.Equal(AtmosphericQualityLevel.Low, fixture.Controller.AutoQuality!.Value.Current);
        fixture.Active.ResolvedGpuMilliseconds = 0.01;
        for (int i = 0; i < AtmosphericAutoQualityController.UpgradeHysteresisFrames + 1; i++)
            fixture.Observe(0.01, stable: true);
        Assert.Equal(AtmosphericQualityLevel.Low, fixture.Controller.AutoQuality!.Value.Current);
        Assert.Equal(1, fixture.Factory.BuildCount);
    }

    [Fact]
    public void AutoFailsSafelyToRetailWhenLowPersistentlyExceedsItsDeclaredBudget()
    {
        using var fixture = new Fixture();
        fixture.Activate("auto");
        fixture.ObserveOverBudget(AtmosphericAutoQualityController.DowngradeHysteresisFrames);
        fixture.Controller.ApplyAtFrameBoundary(
            new RenderPackActivationExtent(1280, 720, 4));
        FakeRuntime low = fixture.Active;
        Assert.Equal("low", low.Preset.Id);

        fixture.ObserveOverBudget(
            1
            + AtmosphericAutoQualityController.ChangeCooldownFrames
            + AtmosphericAutoQualityController.DowngradeHysteresisFrames);

        Assert.Same(low, fixture.Controller.ActiveRuntime);
        Assert.True(fixture.Controller.AutoQuality!.Value.SafeFallbackToRetailRequested);

        RenderPackActivationSnapshot fallback = fixture.Controller.ApplyAtFrameBoundary(
            new RenderPackActivationExtent(1280, 720, 4));

        Assert.Equal(RenderPackActivationState.FailedToRetail, fallback.State);
        Assert.Equal(RenderPackSelectionSettings.Retail, fallback.Selection);
        Assert.Null(fixture.Controller.ActiveRuntime);
        Assert.True(low.Disposed);
        Assert.Contains(
            "Low remained over its declared performance budget for 180 stable samples",
            fallback.Reason,
            StringComparison.Ordinal);
        Assert.Contains("GPU p99 20.000 ms (budget 12.000 ms)", fallback.Reason);
        Assert.Contains("CPU p99 20.000 ms (budget 3.000 ms)", fallback.Reason);
        Assert.Contains("resident GPU bytes 536870912 (budget 67108864)", fallback.Reason);
        Assert.Null(fixture.Controller.AutoQuality);
        Assert.Equal(0, fixture.Controller.Performance.CpuSampleCount);
    }

    [Fact]
    public void HostThatCannotSupportLowFailsAutoPreciselyToRetail()
    {
        using var fixture = new Fixture(new RenderPackHostCapabilities(
            Enum.GetValues<RenderCapability>().ToHashSet(),
            4096,
            4,
            32L * 1024 * 1024,
            MemoryPolicyDescription: "test weak-host policy"));

        fixture.Controller.Request(new RenderPackSelectionSettings(
            "auto.test", "1.0.0", "auto"));
        RenderPackActivationSnapshot snapshot = fixture.Controller.ApplyAtFrameBoundary(
            new RenderPackActivationExtent(1280, 720, 4));

        Assert.Equal(RenderPackActivationState.FailedToRetail, snapshot.State);
        Assert.Null(fixture.Controller.ActiveRuntime);
        Assert.Contains("cannot support Low", snapshot.Reason, StringComparison.Ordinal);
        Assert.Contains("33554432", snapshot.Reason, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Factory.BuildCount);
    }

    [Fact]
    public void AutoCandidateResourceFailureDisposesBothCandidatesAndFailsSafelyToRetail()
    {
        using var fixture = new Fixture();
        fixture.Activate("auto");
        FakeRuntime medium = fixture.Active;
        fixture.ObserveOverBudget(AtmosphericAutoQualityController.DowngradeHysteresisFrames);
        fixture.Factory.FailPreparePreset = "low";

        RenderPackActivationSnapshot failed = fixture.Controller.ApplyAtFrameBoundary(
            new RenderPackActivationExtent(1280, 720, 4));

        Assert.Equal(RenderPackActivationState.FailedToRetail, failed.State);
        Assert.Null(fixture.Controller.ActiveRuntime);
        Assert.True(medium.Disposed);
        FakeRuntime candidate = fixture.Factory.Runtimes[^1];
        Assert.Equal("low", candidate.Preset.Id);
        Assert.True(candidate.Disposed);
        Assert.Contains("injected low resource failure", failed.Reason, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Controller.Performance.CpuSampleCount);
    }

    [Fact]
    public void UnstableFramesAndExplicitPresetsNeverDriveAutomaticChanges()
    {
        using var auto = new Fixture();
        auto.Activate("auto");
        auto.ObserveOverBudget(1000, stable: false);
        Assert.Equal(AtmosphericQualityLevel.Medium,
            auto.Controller.AutoQuality!.Value.Current);
        Assert.Equal(0, auto.Controller.Performance.CpuSampleCount);
        Assert.Equal(1, auto.Factory.BuildCount);

        using var explicitHigh = new Fixture();
        explicitHigh.Activate("high");
        explicitHigh.ObserveOverBudget(1000);
        explicitHigh.Controller.ApplyAtFrameBoundary(
            new RenderPackActivationExtent(1280, 720, 4));
        Assert.Equal("high", explicitHigh.Active.Preset.Id);
        Assert.Null(explicitHigh.Controller.AutoQuality);
        Assert.Equal(1, explicitHigh.Factory.BuildCount);
    }

    [Fact]
    public void AutomaticQualityBooleanEnablesAutoFromAnExplicitPreset()
    {
        using var fixture = new Fixture(descriptor: DescriptorWithAutomaticSetting());
        fixture.Controller.Request(new RenderPackSelectionSettings(
            "auto.test",
            "1.0.0",
            "high")
        {
            SettingOverrides = new RenderPackSettingOverrides(
                new Dictionary<string, string> { ["automatic-quality"] = "true" }),
        });

        RenderPackActivationSnapshot snapshot = fixture.Controller.ApplyAtFrameBoundary(
            new RenderPackActivationExtent(1280, 720, 4));

        Assert.Equal(RenderPackActivationState.Active, snapshot.State);
        Assert.Equal("high", fixture.Active.Preset.Id);
        Assert.Equal(AtmosphericQualityLevel.High, fixture.Controller.AutoQuality!.Value.Current);
    }

    [Fact]
    public void AutomaticQualityBooleanCanDisableTheAutomaticSelector()
    {
        using var fixture = new Fixture(descriptor: DescriptorWithAutomaticSetting());
        fixture.Controller.Request(new RenderPackSelectionSettings(
            "auto.test",
            "1.0.0",
            "auto")
        {
            SettingOverrides = new RenderPackSettingOverrides(
                new Dictionary<string, string> { ["automatic-quality"] = "false" }),
        });

        RenderPackActivationSnapshot snapshot = fixture.Controller.ApplyAtFrameBoundary(
            new RenderPackActivationExtent(1280, 720, 4));

        Assert.Equal(RenderPackActivationState.Active, snapshot.State);
        Assert.Equal("medium", fixture.Active.Preset.Id);
        Assert.Null(fixture.Controller.AutoQuality);
    }

    [Fact]
    public void ResourceGenerationChangeResetsWindowBeforeAcceptingNewLayoutSamples()
    {
        using var fixture = new Fixture();
        fixture.Activate("auto");
        fixture.ObserveOverBudget(5);
        Assert.Equal(5, fixture.Controller.Performance.CpuSampleCount);

        fixture.Active.ResourceGeneration++;
        fixture.ObserveOverBudget(1);
        Assert.Equal(0, fixture.Controller.Performance.CpuSampleCount);
        fixture.ObserveOverBudget(1);
        Assert.Equal(1, fixture.Controller.Performance.CpuSampleCount);
    }

    [Fact]
    public void DiagnosticResetStartsACompleteFreshPerformanceWindow()
    {
        using var fixture = new Fixture();
        fixture.Activate("high");
        fixture.ObserveOverBudget(4);
        Assert.Equal(4, fixture.Controller.MinimumPerformanceSampleCount);

        Assert.True(
            fixture.Controller.TryResetPerformanceEvidence(out string error),
            error);
        Assert.Equal(0, fixture.Controller.MinimumPerformanceSampleCount);

        fixture.ObserveOverBudget(1);
        Assert.Equal(1, fixture.Controller.MinimumPerformanceSampleCount);
    }

    [Fact]
    public void DiagnosticResetCannotPerturbAutomaticQualityEvidence()
    {
        using var fixture = new Fixture();
        fixture.Activate("auto");
        fixture.ObserveOverBudget(1);

        Assert.False(
            fixture.Controller.TryResetPerformanceEvidence(out string error));
        Assert.Contains("explicit quality preset", error);
        Assert.Equal(1, fixture.Controller.MinimumPerformanceSampleCount);
    }

    [Fact]
    public void DiagnosticsSeparatePackAddedCpuAbsoluteReceiverCpuAndInclusiveGpu()
    {
        using var fixture = new Fixture();
        fixture.Activate("high");
        double[] cpu = [1, 2, 3, 4];
        double[] gpu = [4, 6, 8, 10];
        for (int i = 0; i < cpu.Length; i++)
        {
            fixture.Active.ResolvedGpuMilliseconds = gpu[i];
            fixture.Observe(cpu[i], stable: true);
        }

        RenderPackDiagnosticsSnapshot diagnostics = fixture.Controller.CaptureDiagnostics();

        Assert.Equal(4, diagnostics.Performance.CpuSampleCount);
        Assert.Equal(4, diagnostics.Performance.AbsoluteReceiverCpuSampleCount);
        Assert.Equal(4, diagnostics.Performance.GpuSampleCount);
        Assert.Equal(2, diagnostics.Performance.IncrementalCpuMillisecondsP50);
        Assert.Equal(4, diagnostics.Performance.IncrementalCpuMillisecondsP95);
        Assert.Equal(4, diagnostics.Performance.IncrementalCpuMillisecondsP99);
        Assert.Equal(0, diagnostics.Performance.AbsoluteReceiverCpuMillisecondsP50);
        Assert.Equal(6, diagnostics.Performance.InclusiveGpuMillisecondsP50);
        Assert.Equal(10, diagnostics.Performance.InclusiveGpuMillisecondsP95);
        Assert.Equal(10, diagnostics.Performance.InclusiveGpuMillisecondsP99);
        Assert.Contains(
            "perf=cpu-added:2.000/4.000/4.000ms,receiver-cpu-absolute:0.000/0.000/0.000ms,gpu-inclusive:6.000/10.000/10.000ms",
            RenderPackDiagnosticsFormatter.Format(diagnostics),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AbsoluteReceiverCpuIsDiagnosticOnlyWhileGpuRemainsInclusive()
    {
        using var fixture = new Fixture();
        fixture.Activate("high");
        fixture.Active.ResolvedGpuMilliseconds = 4.5;

        fixture.Observe(
            cpuMilliseconds: 1.25,
            stable: true,
            receiverCpuMilliseconds: 0.75);

        RenderPackPerformanceSnapshot performance = fixture.Controller.Performance;
        Assert.Equal(1, performance.CpuSampleCount);
        Assert.Equal(1.25, performance.IncrementalCpuMillisecondsP50);
        Assert.Equal(0.75, performance.AbsoluteReceiverCpuMillisecondsP50);
        Assert.Equal(4.5, performance.InclusiveGpuMillisecondsP50);
    }

    [Fact]
    public void LargeAbsoluteReceiverCpuCannotForceAutoDownButPackAddedCpuCan()
    {
        using var receiverHeavy = new Fixture();
        receiverHeavy.Activate("auto");
        receiverHeavy.Active.ResolvedGpuMilliseconds = 0.1;
        for (int i = 0; i < AtmosphericAutoQualityController.DowngradeHysteresisFrames; i++)
        {
            receiverHeavy.Observe(
                cpuMilliseconds: 0.1,
                stable: true,
                receiverCpuMilliseconds: 100);
        }

        Assert.Equal(
            AtmosphericQualityLevel.Medium,
            receiverHeavy.Controller.AutoQuality!.Value.Current);
        Assert.Equal(
            100,
            receiverHeavy.Controller.Performance.AbsoluteReceiverCpuMillisecondsP99);
        Assert.Equal(
            0.1,
            receiverHeavy.Controller.Performance.IncrementalCpuMillisecondsP99);

        using var packHeavy = new Fixture();
        packHeavy.Activate("auto");
        packHeavy.Active.ResolvedGpuMilliseconds = 0.1;
        for (int i = 0; i < AtmosphericAutoQualityController.DowngradeHysteresisFrames; i++)
        {
            packHeavy.Observe(
                cpuMilliseconds: 10,
                stable: true,
                receiverCpuMilliseconds: 0.1);
        }

        Assert.Equal(
            AtmosphericQualityLevel.Low,
            packHeavy.Controller.AutoQuality!.Value.Current);
    }

    [Fact]
    public void StablePerformanceObservationAllocatesNothingAfterWarmup()
    {
        using var fixture = new Fixture();
        fixture.Activate("high");
        for (int i = 0; i < 128; i++)
            fixture.Observe(1, stable: true);

        ZeroAllocationProbe.AssertAllocatesNothing(
            "RenderPackAutoRuntime stable observation",
            () => fixture.Observe(1, stable: true),
            batchSize: 128);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly BufferedRenderPackRegistry _registry = new();
        private readonly IDisposable _registration;

        internal Fixture(
            RenderPackHostCapabilities? capabilities = null,
            RenderPackDescriptor? descriptor = null,
            IRenderPackPreparationScheduler? preparationScheduler = null)
        {
            Factory = new FakeFactory(Events);
            _registration = _registry.Register(descriptor ?? Descriptor(), new EmptyAssets());
            Controller = new RenderPackController(
                () => RenderPackCatalog.Build(
                    _registry.Snapshot(),
                    capabilities ?? RenderPackHostCapabilities.Conformance),
                Factory,
                preparationScheduler: preparationScheduler
                    ?? InlineRenderPackPreparationScheduler.Instance);
        }

        internal List<string> Events { get; } = [];
        internal FakeFactory Factory { get; }
        internal RenderPackController Controller { get; }
        internal FakeRuntime Active => Assert.IsType<FakeRuntime>(Controller.ActiveRuntime);

        internal void Activate(string preset)
        {
            Controller.Request(new RenderPackSelectionSettings("auto.test", "1.0.0", preset));
            RenderPackActivationSnapshot snapshot = Controller.ApplyAtFrameBoundary(
                new RenderPackActivationExtent(1280, 720, 4));
            Assert.True(
                snapshot.State == RenderPackActivationState.Active,
                snapshot.Reason);
        }

        internal void ObserveOverBudget(int count, bool stable = true)
        {
            Active.ResolvedGpuMilliseconds = 20;
            Active.RetainedGpuBytes = 512L * 1024 * 1024;
            for (int i = 0; i < count; i++)
                Observe(20, stable);
        }

        internal void Observe(
            double cpuMilliseconds,
            bool stable,
            double receiverCpuMilliseconds = 0d)
        {
            var observation = new RenderPackFramePerformanceObservation(
                cpuMilliseconds,
                stable,
                1280,
                720,
                4,
                receiverCpuMilliseconds);
            Controller.ObserveActiveFrame(in observation);
        }

        public void Dispose()
        {
            Controller.Dispose();
            _registration.Dispose();
            _registry.Dispose();
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

    private sealed class FakeFactory(List<string> events) : IRenderPackRuntimeFactory
    {
        internal int BuildCount { get; private set; }
        internal string? FailPreparePreset { get; set; }
        internal List<FakeRuntime> Runtimes { get; } = [];

        public IRenderPackRuntime Build(
            RenderPackDescriptor descriptor,
            ValidatedRenderPackShaderAssets assets,
            RenderQualityPreset preset,
            IReadOnlyDictionary<string, string> userSettingOverrides)
        {
            BuildCount++;
            events.Add("build:" + preset.Id);
            var runtime = new FakeRuntime(
                descriptor,
                preset,
                events,
                string.Equals(FailPreparePreset, preset.Id, StringComparison.Ordinal));
            Runtimes.Add(runtime);
            return runtime;
        }
    }

    private sealed class FakeRuntime(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        List<string> events,
        bool failPrepare) :
        IAtmosphericWorldGraphRuntime,
        IRenderPackRuntimePerformanceSource,
        IRenderPackRuntimeDiagnosticsSource
    {
        public RenderPackDescriptor Descriptor { get; } = descriptor;
        public RenderQualityPreset Preset { get; } = preset;
        internal long ResourceGeneration { get; set; }
        internal double ResolvedGpuMilliseconds { get; set; } = 1;
        internal long RetainedGpuBytes { get; set; } = 32L * 1024 * 1024;
        internal bool Prepared { get; private set; }
        internal bool Disposed { get; private set; }

        public IGpuRenderTarget PrepareWorldTarget(int width, int height, int sampleCount)
        {
            events.Add($"prepare:{Preset.Id}:{width}x{height}x{sampleCount}");
            if (failPrepare)
                throw new InvalidOperationException($"injected {Preset.Id} resource failure");
            Prepared = true;
            ResourceGeneration++;
            return null!;
        }

        public void RenderPostProcess(IGpuFrame frame, in AtmosphericFrameInputs inputs)
        {
        }

        public RenderPackRuntimePerformanceMetrics CapturePerformanceMetrics() => new(
            ResourceGeneration,
            HasResolvedGpuMeasurement: true,
            ResolvedGpuMilliseconds,
            RetainedGpuBytes,
            TransientGpuBytes: 4L * 1024 * 1024);

        public RenderPackRuntimeDiagnostics CaptureDiagnostics() =>
            RenderPackRuntimeDiagnostics.Empty(Preset.Id);

        public void Dispose()
        {
            if (Disposed)
                return;
            Disposed = true;
            events.Add("dispose:" + Preset.Id);
        }
    }

    private sealed class EmptyAssets : IRenderPackAssets
    {
        public Stream OpenRead(string assetKey) => Stream.Null;
    }

    private static RenderPackDescriptor Descriptor() => new(
        "auto.test",
        "Auto Test",
        new Version(1, 0, 0),
        RenderPackApi.Current,
        RenderPackTier.Tier1,
        [],
        [],
        [],
        [],
        [],
        [],
        [
            Preset("low"),
            Preset("medium"),
            Preset("high"),
            Preset("auto") with { AutoEligible = false },
        ],
        [],
        null)
    {
        FeatureSummary = "Automatic-quality test render pack.",
    };

    private static RenderPackDescriptor DescriptorWithAutomaticSetting()
    {
        RenderPackDescriptor descriptor = Descriptor();
        return descriptor with
        {
            QualityPresets = descriptor.QualityPresets.Select(preset =>
                preset.Semantic == RenderQualitySemantic.Automatic
                    ? preset with
                    {
                        SettingOverrides =
                        [
                            new RenderQualitySettingOverride("automatic-quality", "true"),
                        ],
                    }
                    : preset).ToArray(),
            Settings =
            [
                new RenderSettingDeclaration(
                    "automatic-quality",
                    "Automatic quality",
                    RenderSettingKind.Boolean,
                    "false",
                    null,
                    null,
                    null,
                    [])
                {
                    Semantic = RenderSettingSemantic.AutomaticQuality,
                },
            ],
        };
    }

    private static RenderQualityPreset Preset(string id) => new RenderQualityPreset(
            id,
            char.ToUpperInvariant(id[0]) + id[1..],
            [],
            [],
            [],
            (id switch
            {
                "low" => 64L,
                "high" => 256L,
                _ => 128L,
            }) * 1024 * 1024,
            10,
            12,
            2,
            3)
        {
            Semantic = id switch
            {
                "low" => RenderQualitySemantic.Low,
                "medium" => RenderQualitySemantic.Medium,
                "high" => RenderQualitySemantic.High,
                "auto" => RenderQualitySemantic.Automatic,
                _ => RenderQualitySemantic.Custom,
            },
        };
}
