using System.Reflection;
using AcDream.App.Diagnostics;
using AcDream.App.Rendering;
using AcDream.UI.Abstractions.Panels.Settings;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AcDream.App.Tests.Rendering;

public sealed class RenderFrameOrchestratorTests
{
    private static readonly RenderFrameInput Input = new(
        DeltaSeconds: 1.0 / 60.0,
        ViewportWidth: 1920,
        ViewportHeight: 1080);

    [Theory]
    [InlineData(12, 40, true, false, false)]
    [InlineData(0, 0, false, false, false)]
    [InlineData(0, 0, false, true, false)]
    [InlineData(4, 17, true, false, true)]
    public void Render_PreservesOuterOrderAcrossWorldAndPresentationOutcomes(
        int visibleLandblocks,
        int totalLandblocks,
        bool normalWorldDrawn,
        bool portalViewportDrawn,
        bool screenshotCaptured)
    {
        var calls = new List<string>();
        var phases = new RecordingPhases(calls)
        {
            World = new WorldRenderFrameOutcome(
                visibleLandblocks,
                totalLandblocks,
                normalWorldDrawn),
            Presentation = new PrivatePresentationFrameOutcome(
                portalViewportDrawn,
                ScreenshotCaptured: false),
            CaptureResult = screenshotCaptured,
        };
        var orchestrator = Create(phases);

        RenderFrameOutcome outcome = orchestrator.Render(Input);

        Assert.Equal(
            [
                "gpu-begin", "measure-begin", "resources", "world", "presentation",
                "measure-end", "gpu-end", "screenshot",
                "diagnostics", "post-diagnostics",
            ],
            calls);
        Assert.Equal(phases.World, outcome.World);
        Assert.Equal(
            phases.Presentation with { ScreenshotCaptured = screenshotCaptured },
            outcome.Presentation);
        Assert.Equal(
            [
                ("resources", Input),
                ("world", Input),
                ("presentation", Input),
                ("diagnostics", Input),
                ("post-diagnostics", Input),
            ],
            phases.ObservedInputs);
        Assert.Equal(phases.World, phases.ObservedWorld);
        Assert.Equal(outcome, phases.ObservedOutcome);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(0, 0)]
    [InlineData(-1, 1080)]
    public void ZeroAreaViewport_SkipsTheFrameBeforeAnyGpuWork(int width, int height)
    {
        var calls = new List<string>();
        var phases = new RecordingPhases(calls);

        RenderFrameOutcome outcome = Create(phases).Render(
            Input with { ViewportWidth = width, ViewportHeight = height });

        Assert.True(outcome.SkippedZeroArea);
        Assert.Empty(calls);
        Assert.Empty(phases.ObservedInputs);
    }

    [Fact]
    public void RealScreenshotComposition_CapturesSubmittedStartupAndResizeFramesInExactOrientation()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "acdream-476-" + Guid.NewGuid().ToString("N"));
        try
        {
            var calls = new List<string>();
            var lifetime = new SubmittingPixelLifetime(calls);
            var phases = new RecordingPhases(calls);
            var controller = new FrameScreenshotController(
                (width, height) =>
                {
                    calls.Add("capture-read");
                    Assert.Equal((width, height), lifetime.SubmittedSize);
                    return FrameScreenshotController.FlipRows(
                        lifetime.SubmittedPixels,
                        width,
                        height);
                },
                directory);
            var presentation = new PrivatePresentationRenderer(
                new RecordingPortal(calls),
                new StaticFoundation(),
                entityViewports: null,
                gameplayUi: null,
                devTools: null);
            var orchestrator = new RenderFrameOrchestrator(
                lifetime,
                phases,
                phases,
                phases,
                presentation,
                phases,
                phases,
                phases,
                buildingDegrades: null,
                screenshots: new PrivateFrameScreenshot(controller));

            Rgba32[] startup =
            [
                new(1, 2, 3, 4), new(5, 6, 7, 8),
                new(9, 10, 11, 12), new(13, 14, 15, 16),
            ];
            lifetime.NextFrame(2, 2, startup);
            Assert.True(controller.TryRequest("startup", out string startupError), startupError);
            RenderFrameOutcome startupOutcome = orchestrator.Render(
                Input with { ViewportWidth = 2, ViewportHeight = 2 });

            Assert.True(startupOutcome.Presentation.ScreenshotCaptured);
            Assert.True(controller.IsComplete("startup"));
            AssertPng(Path.Combine(directory, "startup.png"), 2, 2, startup);
            AssertSubmittedCaptureOrder(calls);

            calls.Clear();
            Rgba32[] resized =
            [
                new(21, 22, 23, 24), new(25, 26, 27, 28), new(29, 30, 31, 32),
                new(33, 34, 35, 36), new(37, 38, 39, 40), new(41, 42, 43, 44),
            ];
            lifetime.NextFrame(3, 2, resized);
            Assert.True(controller.TryRequest("resized", out string resizeError), resizeError);
            RenderFrameOutcome resizedOutcome = orchestrator.Render(
                Input with { ViewportWidth = 3, ViewportHeight = 2 });

            Assert.True(resizedOutcome.Presentation.ScreenshotCaptured);
            Assert.True(controller.IsComplete("resized"));
            AssertPng(Path.Combine(directory, "resized.png"), 3, 2, resized);
            AssertSubmittedCaptureOrder(calls);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ZeroAreaLeavesRealScreenshotRequestQueuedUntilTheNextRenderableFrame()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "acdream-476-zero-" + Guid.NewGuid().ToString("N"));
        try
        {
            var calls = new List<string>();
            var lifetime = new SubmittingPixelLifetime(calls);
            var phases = new RecordingPhases(calls);
            var controller = new FrameScreenshotController(
                (width, height) => FrameScreenshotController.FlipRows(
                    lifetime.SubmittedPixels,
                    width,
                    height),
                directory);
            var orchestrator = new RenderFrameOrchestrator(
                lifetime,
                phases,
                phases,
                phases,
                phases,
                phases,
                phases,
                phases,
                buildingDegrades: null,
                screenshots: new PrivateFrameScreenshot(controller));
            Rgba32[] pixels = [new(51, 52, 53, 54)];
            lifetime.NextFrame(1, 1, pixels);
            Assert.True(controller.TryRequest("queued", out string error), error);

            RenderFrameOutcome skipped = orchestrator.Render(
                Input with { ViewportWidth = 0, ViewportHeight = 1 });

            Assert.True(skipped.SkippedZeroArea);
            Assert.False(controller.IsComplete("queued"));
            Assert.Empty(calls);

            RenderFrameOutcome rendered = orchestrator.Render(
                Input with { ViewportWidth = 1, ViewportHeight = 1 });

            Assert.True(rendered.Presentation.ScreenshotCaptured);
            Assert.True(controller.IsComplete("queued"));
            AssertPng(Path.Combine(directory, "queued.png"), 1, 1, pixels);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CaptureOffClosesThenRunsDiagnosticsWithoutCaptureWork()
    {
        var calls = new List<string>();
        var phases = new RecordingPhases(calls);
        var orchestrator = new RenderFrameOrchestrator(
            phases, phases, phases, phases, phases, phases, phases, phases);

        RenderFrameOutcome outcome = orchestrator.Render(Input);

        Assert.False(outcome.Presentation.ScreenshotCaptured);
        Assert.Equal(
            [
                "gpu-begin", "measure-begin", "resources", "world", "presentation",
                "measure-end", "gpu-end", "diagnostics", "post-diagnostics",
            ],
            calls);
    }

    [Fact]
    public void ScreenshotOutcomeIsThePostCloseCaptureResultNotAStalePresentationClaim()
    {
        var calls = new List<string>();
        var phases = new RecordingPhases(calls)
        {
            Presentation = new PrivatePresentationFrameOutcome(
                PortalViewportDrawn: true,
                ScreenshotCaptured: true),
            CaptureResult = false,
        };

        RenderFrameOutcome outcome = Create(phases).Render(Input);

        Assert.True(outcome.Presentation.PortalViewportDrawn);
        Assert.False(outcome.Presentation.ScreenshotCaptured);
        Assert.True(calls.IndexOf("gpu-end") < calls.IndexOf("screenshot"));
        Assert.Equal(outcome, phases.ObservedOutcome);
    }

    [Fact]
    public void CloseFailureDoesNotConsumeTheRealQueuedScreenshotOrRunPostCloseConsumers()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "acdream-476-close-" + Guid.NewGuid().ToString("N"));
        try
        {
            var calls = new List<string>();
            var lifetime = new SubmittingPixelLifetime(calls)
            {
                CloseFailure = new InvalidOperationException("close"),
            };
            var phases = new RecordingPhases(calls);
            int reads = 0;
            var controller = new FrameScreenshotController(
                (width, height) =>
                {
                    reads++;
                    return FrameScreenshotController.FlipRows(
                        lifetime.SubmittedPixels,
                        width,
                        height);
                },
                directory);
            var orchestrator = new RenderFrameOrchestrator(
                lifetime,
                phases,
                phases,
                phases,
                phases,
                phases,
                phases,
                phases,
                buildingDegrades: null,
                screenshots: new PrivateFrameScreenshot(controller));
            Rgba32[] pixels = [new(61, 62, 63, 64)];
            lifetime.NextFrame(1, 1, pixels);
            Assert.True(controller.TryRequest("after-close", out string error), error);

            Assert.Same(
                lifetime.CloseFailure,
                Assert.Throws<InvalidOperationException>(() => orchestrator.Render(
                    Input with { ViewportWidth = 1, ViewportHeight = 1 })));

            Assert.Equal(0, reads);
            Assert.False(controller.IsComplete("after-close"));
            Assert.DoesNotContain("diagnostics", calls);
            Assert.DoesNotContain("post-diagnostics", calls);

            calls.Clear();
            lifetime.CloseFailure = null;
            RenderFrameOutcome outcome = orchestrator.Render(
                Input with { ViewportWidth = 1, ViewportHeight = 1 });

            Assert.True(outcome.Presentation.ScreenshotCaptured);
            Assert.Equal(1, reads);
            Assert.True(controller.IsComplete("after-close"));
            AssertPng(Path.Combine(directory, "after-close.png"), 1, 1, pixels);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AcceptedRenderTicksSharedDegradeOwnerExactlyOnceAndZeroAreaDoesNot()
    {
        var calls = new List<string>();
        var phases = new RecordingPhases(calls);
        var degradation = new RecordingDegradeTick(calls);
        var orchestrator = new RenderFrameOrchestrator(
            phases, phases, phases, phases, phases, phases, phases, phases,
            degradation);

        double[] durations =
        [
            1d / 1024d, 2d / 1024d, 3d / 1024d, 4d / 1024d,
            5d / 1024d, 6d / 1024d, 7d / 1024d, 8d / 1024d,
            9d / 1024d, 10d / 1024d, 11d / 1024d, 12d / 1024d,
            13d / 1024d, 14d / 1024d, 15d / 1024d, 16d / 1024d,
            17d / 1024d, 18d / 1024d, 19d / 1024d, 20d / 1024d,
            31d / 1024d,
        ];
        for (int i = 0; i < durations.Length - 1; i++)
            orchestrator.Render(Input with { DeltaSeconds = durations[i] });
        orchestrator.Render(Input with { ViewportWidth = 0 });
        orchestrator.Render(Input with { DeltaSeconds = durations[^1] });

        Assert.Equal(durations, degradation.Durations);
        Assert.Equal(0x42C30C31u, BitConverter.SingleToUInt32Bits(degradation.Fps));
        Assert.Equal(durations.Length, calls.Count(static call => call == "degrade-tick"));
        for (int i = 0; i < calls.Count; i++)
        {
            if (calls[i] == "gpu-begin")
            {
                Assert.True(i > 0, "degrade tick must precede BeginFrame");
                Assert.Equal("degrade-tick", calls[i - 1]);
            }
        }
    }

    [Fact]
    public void BeginFailure_DoesNotAttemptAnyPhaseOrClose()
    {
        var calls = new List<string>();
        var expected = new InvalidOperationException("begin");
        var phases = new RecordingPhases(calls)
        {
            FailurePoint = "gpu-begin",
            Failure = expected,
        };

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => Create(phases).Render(Input));

        Assert.Same(expected, actual);
        Assert.Equal(["gpu-begin"], calls);
    }

    [Theory]
    [InlineData("measure-begin")]
    [InlineData("resources")]
    [InlineData("world")]
    [InlineData("presentation")]
    [InlineData("measure-end")]
    public void RenderFailure_ClosesExactlyOnceAndPropagates(string failurePoint)
    {
        var calls = new List<string>();
        var expected = new InvalidOperationException(failurePoint);
        var phases = new RecordingPhases(calls)
        {
            FailurePoint = failurePoint,
            Failure = expected,
        };

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => Create(phases).Render(Input));

        Assert.Same(expected, actual);
        Assert.Equal(ExpectedFailureCalls(failurePoint), calls);
        Assert.Equal(
            ExpectedPhaseInputs(failurePoint),
            phases.ObservedInputs);
    }

    [Theory]
    [InlineData("screenshot")]
    [InlineData("diagnostics")]
    [InlineData("post-diagnostics")]
    public void PostCloseFailure_PropagatesWithoutRecoveryOrASecondClose(string failurePoint)
    {
        var calls = new List<string>();
        var expected = new InvalidOperationException(failurePoint);
        var phases = new RecordingPhases(calls)
        {
            FailurePoint = failurePoint,
            Failure = expected,
        };

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => Create(phases).Render(Input));

        Assert.Same(expected, actual);
        Assert.Equal(1, calls.Count(static call => call == "gpu-end"));
        Assert.DoesNotContain("recovery-abort", calls);
        Assert.Equal(ExpectedPostCloseFailureCalls(failurePoint), calls);
    }

    [Fact]
    public void CloseOnlyFailure_PropagatesDirectly()
    {
        var calls = new List<string>();
        var expected = new InvalidOperationException("close");
        var phases = new RecordingPhases(calls)
        {
            CloseFailure = expected,
        };

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => Create(phases).Render(Input));

        Assert.Same(expected, actual);
        Assert.Equal(
            [
                "gpu-begin", "measure-begin", "resources", "world", "presentation",
                "measure-end", "gpu-end",
            ],
            calls);
    }

    [Theory]
    [InlineData("measure-begin")]
    [InlineData("resources")]
    [InlineData("world")]
    [InlineData("presentation")]
    [InlineData("measure-end")]
    public void RenderAndCloseFailure_AreAggregatedInCausalOrder(string failurePoint)
    {
        var calls = new List<string>();
        var renderFailure = new InvalidOperationException("render");
        var closeFailure = new InvalidOperationException("close");
        var phases = new RecordingPhases(calls)
        {
            FailurePoint = failurePoint,
            Failure = renderFailure,
            CloseFailure = closeFailure,
        };

        AggregateException actual = Assert.Throws<AggregateException>(
            () => Create(phases).Render(Input));

        Assert.Equal(
            "Rendering failed and the in-flight GPU frame could not be closed.",
            actual.Message.Split(" (")[0]);
        Assert.Equal(2, actual.InnerExceptions.Count);
        Assert.Same(renderFailure, actual.InnerExceptions[0]);
        Assert.Same(closeFailure, actual.InnerExceptions[1]);
        Assert.Equal(ExpectedFailureCalls(failurePoint), calls);
        Assert.Equal(
            ExpectedPhaseInputs(failurePoint),
            phases.ObservedInputs);
    }

    [Fact]
    public void RenderAndMeasurementCloseFailures_AreAggregatedBeforeRecoveryAndClose()
    {
        var calls = new List<string>();
        var renderFailure = new InvalidOperationException("render");
        var measurementFailure = new InvalidOperationException("measurement");
        var phases = new RecordingPhases(calls)
        {
            FailurePoint = "world",
            Failure = renderFailure,
            MeasurementCloseFailure = measurementFailure,
        };

        AggregateException actual = Assert.Throws<AggregateException>(
            () => Create(phases).Render(Input));

        Assert.Equal(2, actual.InnerExceptions.Count);
        Assert.Same(renderFailure, actual.InnerExceptions[0]);
        Assert.Same(measurementFailure, actual.InnerExceptions[1]);
        Assert.Equal(
            [
                "gpu-begin", "measure-begin", "resources", "world",
                "measure-end", "recovery-abort", "gpu-end",
            ],
            calls);
    }

    [Fact]
    public void RenderAndRecoveryFailure_AreAggregatedBeforeGpuClose()
    {
        var calls = new List<string>();
        var renderFailure = new InvalidOperationException("render");
        var recoveryFailure = new InvalidOperationException("recovery");
        var phases = new RecordingPhases(calls)
        {
            FailurePoint = "world",
            Failure = renderFailure,
            RecoveryFailure = recoveryFailure,
        };

        AggregateException actual = Assert.Throws<AggregateException>(
            () => Create(phases).Render(Input));

        Assert.Equal(2, actual.InnerExceptions.Count);
        Assert.Same(renderFailure, actual.InnerExceptions[0]);
        Assert.Same(recoveryFailure, actual.InnerExceptions[1]);
        Assert.Equal(
            [
                "gpu-begin", "measure-begin", "resources", "world",
                "measure-end", "recovery-abort", "gpu-end",
            ],
            calls);
    }

    [Fact]
    public void RenderRecoveryAndCloseFailures_AllRemainObservableInCausalOrder()
    {
        var calls = new List<string>();
        var renderFailure = new InvalidOperationException("render");
        var recoveryFailure = new InvalidOperationException("recovery");
        var closeFailure = new InvalidOperationException("close");
        var phases = new RecordingPhases(calls)
        {
            FailurePoint = "presentation",
            Failure = renderFailure,
            RecoveryFailure = recoveryFailure,
            CloseFailure = closeFailure,
        };

        AggregateException actual = Assert.Throws<AggregateException>(
            () => Create(phases).Render(Input));

        Assert.Equal(3, actual.InnerExceptions.Count);
        Assert.Same(renderFailure, actual.InnerExceptions[0]);
        Assert.Same(recoveryFailure, actual.InnerExceptions[1]);
        Assert.Same(closeFailure, actual.InnerExceptions[2]);
        Assert.Equal(
            [
                "gpu-begin", "measure-begin", "resources", "world", "presentation",
                "measure-end",
                "recovery-abort", "gpu-end",
            ],
            calls);
    }

    [Fact]
    public void Constructor_RejectsEveryMissingRequiredOwner()
    {
        var phases = new RecordingPhases([]);

        Assert.Throws<ArgumentNullException>(() => new RenderFrameOrchestrator(
            null!, phases, phases, phases, phases, phases, phases, phases));
        Assert.Throws<ArgumentNullException>(() => new RenderFrameOrchestrator(
            phases, null!, phases, phases, phases, phases, phases, phases));
        Assert.Throws<ArgumentNullException>(() => new RenderFrameOrchestrator(
            phases, phases, null!, phases, phases, phases, phases, phases));
        Assert.Throws<ArgumentNullException>(() => new RenderFrameOrchestrator(
            phases, phases, phases, null!, phases, phases, phases, phases));
        Assert.Throws<ArgumentNullException>(() => new RenderFrameOrchestrator(
            phases, phases, phases, phases, null!, phases, phases, phases));
        Assert.Throws<ArgumentNullException>(() => new RenderFrameOrchestrator(
            phases, phases, phases, phases, phases, null!, phases, phases));
        Assert.Throws<ArgumentNullException>(() => new RenderFrameOrchestrator(
            phases, phases, phases, phases, phases, phases, null!, phases));
        Assert.Throws<ArgumentNullException>(() => new RenderFrameOrchestrator(
            phases, phases, phases, phases, phases, phases, phases, null!));
    }

    [Fact]
    public void Orchestrator_UsesOnlyTheExplicitTypedOwnerGraph()
    {
        Type[] expectedFieldTypes =
        [
            typeof(IRenderFrameLifetime),
            typeof(IRenderFrameGpuMeasurement),
            typeof(IRenderFrameResourcePhase),
            typeof(IWorldSceneFramePhase),
            typeof(IPrivatePresentationFramePhase),
            typeof(IRenderFrameDiagnosticsPhase),
            typeof(IRenderFramePostDiagnosticsPhase),
            typeof(IRenderFrameFailureRecovery),
            typeof(IBuildingDegradeFrameTick),
            typeof(IPrivateFrameScreenshot),
        ];
        FieldInfo[] fields = typeof(RenderFrameOrchestrator).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.Equal(
            expectedFieldTypes.OrderBy(type => type.FullName),
            fields.Select(field => field.FieldType).OrderBy(type => type.FullName));
        Assert.All(fields, field =>
        {
            Assert.NotEqual(typeof(GameWindow), field.FieldType);
            Assert.False(typeof(Delegate).IsAssignableFrom(field.FieldType));
        });
        Assert.All(
            expectedFieldTypes,
            contract => Assert.False(contract.IsAssignableFrom(typeof(GameWindow))));
        foreach (Type contract in expectedFieldTypes.Where(type => type.IsInterface))
        {
            foreach (MethodInfo method in contract.GetMethods())
            {
                Assert.True(
                    method.ReturnType == typeof(void) || method.ReturnType.IsValueType,
                    $"{contract.Name}.{method.Name} returned owner-like type "
                    + method.ReturnType.FullName);
                Assert.All(
                    method.GetParameters(),
                    parameter => Assert.True(
                        parameter.ParameterType.IsValueType,
                        $"{contract.Name}.{method.Name} accepted owner-like type "
                        + parameter.ParameterType.FullName));
            }
        }
        Assert.True(typeof(IRenderFrameLifetime).IsAssignableFrom(
            typeof(GpuFrameFlightController)));
    }

    [Fact]
    public void FrameContracts_AreDataOnlyValuesWithoutOwnerOrDelegateReferences()
    {
        Type[] contracts =
        [
            typeof(RenderFrameInput),
            typeof(WorldRenderFrameOutcome),
            typeof(PrivatePresentationFrameOutcome),
            typeof(RenderFrameOutcome),
        ];

        foreach (Type contract in contracts)
        {
            Assert.True(contract.IsValueType);
            Assert.All(
                contract.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                field =>
                {
                    Assert.True(
                        field.FieldType.IsValueType,
                        $"{contract.Name}.{field.Name} retained owner-like type "
                        + field.FieldType.FullName);
                });
        }
    }

    private static void AssertSubmittedCaptureOrder(List<string> calls)
    {
        int measurement = calls.IndexOf("measure-end");
        int close = calls.IndexOf("gpu-end");
        int capture = calls.IndexOf("capture-read");
        int diagnostics = calls.IndexOf("diagnostics");
        int postDiagnostics = calls.IndexOf("post-diagnostics");
        Assert.True(measurement >= 0);
        Assert.True(close > measurement);
        Assert.True(capture > close);
        Assert.True(diagnostics > capture);
        Assert.True(postDiagnostics > diagnostics);
        Assert.Equal(1, calls.Count(static call => call == "gpu-end"));
    }

    private static void AssertPng(
        string path,
        int width,
        int height,
        Rgba32[] expected)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(path);
        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
        var actual = new Rgba32[checked(width * height)];
        image.CopyPixelDataTo(actual);
        Assert.Equal(expected, actual);
    }

    private sealed class SubmittingPixelLifetime(List<string> calls) : IRenderFrameLifetime
    {
        private byte[] _nextPixels = [];

        public bool IsOpen { get; private set; }
        public Exception? CloseFailure { get; set; }
        public byte[] SubmittedPixels { get; private set; } = [];
        public (int Width, int Height) SubmittedSize { get; private set; }

        public void NextFrame(int width, int height, Rgba32[] pixels)
        {
            Assert.Equal(checked(width * height), pixels.Length);
            if (SubmittedSize != (width, height) || SubmittedPixels.Length == 0)
            {
                SubmittedPixels = Enumerable.Repeat(
                        (byte)0xEE,
                        checked(width * height * 4))
                    .ToArray();
            }
            _nextPixels = pixels.SelectMany(static pixel =>
                    new[] { pixel.R, pixel.G, pixel.B, pixel.A })
                .ToArray();
            SubmittedSize = (width, height);
        }

        public void BeginFrame()
        {
            Assert.False(IsOpen);
            IsOpen = true;
            calls.Add("gpu-begin");
        }

        public void EndFrame()
        {
            Assert.True(IsOpen);
            IsOpen = false;
            calls.Add("gpu-end");
            if (CloseFailure is not null)
                throw CloseFailure;
            SubmittedPixels = [.. _nextPixels];
        }
    }

    private sealed class RecordingPortal(List<string> calls) : IPrivatePortalViewport
    {
        public void Draw(int width, int height) => calls.Add("portal");
    }

    private sealed class StaticFoundation : IRenderFrameFoundationSource
    {
        public RenderFrameFoundation Foundation { get; } = new(false, default, default);
    }

    private static string[] ExpectedFailureCalls(string failurePoint) => failurePoint switch
    {
        "measure-begin" =>
            ["gpu-begin", "measure-begin", "recovery-abort", "gpu-end"],
        "resources" =>
            [
                "gpu-begin", "measure-begin", "resources", "measure-end",
                "recovery-abort", "gpu-end",
            ],
        "world" =>
            [
                "gpu-begin", "measure-begin", "resources", "world", "measure-end",
                "recovery-abort", "gpu-end",
            ],
        "presentation" or "measure-end" =>
            [
                "gpu-begin", "measure-begin", "resources", "world", "presentation",
                "measure-end", "recovery-abort", "gpu-end",
            ],
        _ => throw new ArgumentOutOfRangeException(nameof(failurePoint)),
    };

    private static string[] ExpectedPostCloseFailureCalls(string failurePoint) => failurePoint switch
    {
        "screenshot" =>
            [
                "gpu-begin", "measure-begin", "resources", "world", "presentation",
                "measure-end", "gpu-end", "screenshot",
            ],
        "diagnostics" =>
            [
                "gpu-begin", "measure-begin", "resources", "world", "presentation",
                "measure-end", "gpu-end", "screenshot", "diagnostics",
            ],
        "post-diagnostics" =>
            [
                "gpu-begin", "measure-begin", "resources", "world", "presentation",
                "measure-end", "gpu-end", "screenshot", "diagnostics",
                "post-diagnostics",
            ],
        _ => throw new ArgumentOutOfRangeException(nameof(failurePoint)),
    };

    private static (string Phase, RenderFrameInput Input)[] ExpectedPhaseInputs(
        string failurePoint)
    {
        string[] phases = failurePoint switch
        {
            "measure-begin" => [],
            "resources" => ["resources"],
            "world" => ["resources", "world"],
            "presentation" or "measure-end" => ["resources", "world", "presentation"],
            _ => throw new ArgumentOutOfRangeException(nameof(failurePoint)),
        };
        return phases.Select(phase => (phase, Input)).ToArray();
    }

    private static RenderFrameOrchestrator Create(RecordingPhases phases) =>
        new(phases, phases, phases, phases, phases, phases, phases, phases, null, phases);

    private sealed class RecordingDegradeTick : IBuildingDegradeFrameTick
    {
        private readonly List<string> _calls;
        private readonly BuildingDegradeController _inner = new(
            () => DisplaySettings.Default);

        public RecordingDegradeTick(List<string> calls) => _calls = calls;

        public List<double> Durations { get; } = [];
        public float Fps => _inner.Fps;

        public void Tick(double elapsedSeconds)
        {
            _calls.Add("degrade-tick");
            Durations.Add(elapsedSeconds);
            _inner.Tick(elapsedSeconds);
        }
    }

    private sealed class RecordingPhases :
        IRenderFrameLifetime,
        IRenderFrameGpuMeasurement,
        IRenderFrameResourcePhase,
        IWorldSceneFramePhase,
        IPrivatePresentationFramePhase,
        IRenderFrameDiagnosticsPhase,
        IRenderFramePostDiagnosticsPhase,
        IRenderFrameFailureRecovery,
        IPrivateFrameScreenshot
    {
        private readonly List<string> _calls;

        public RecordingPhases(List<string> calls)
        {
            _calls = calls;
        }

        public string? FailurePoint { get; init; }
        public Exception? Failure { get; init; }
        public Exception? CloseFailure { get; init; }
        public Exception? RecoveryFailure { get; init; }
        public Exception? MeasurementCloseFailure { get; init; }
        public WorldRenderFrameOutcome World { get; init; } = new(7, 19, true);
        public PrivatePresentationFrameOutcome Presentation { get; init; } = new(false, false);
        public bool CaptureResult { get; init; }
        public List<(string Phase, RenderFrameInput Input)> ObservedInputs { get; } = [];
        public WorldRenderFrameOutcome ObservedWorld { get; private set; }
        public RenderFrameOutcome ObservedOutcome { get; private set; }

        void IRenderFrameLifetime.BeginFrame() => Record("gpu-begin");

        void IRenderFrameLifetime.EndFrame()
        {
            _calls.Add("gpu-end");
            if (CloseFailure is not null)
                throw CloseFailure;
        }

        void IRenderFrameGpuMeasurement.BeginFrame() => Record("measure-begin");

        void IRenderFrameGpuMeasurement.EndFrame()
        {
            Record("measure-end");
            if (MeasurementCloseFailure is not null)
                throw MeasurementCloseFailure;
        }

        public void AbortFrame()
        {
            _calls.Add("recovery-abort");
            if (RecoveryFailure is not null)
                throw RecoveryFailure;
        }

        public void Prepare(RenderFrameInput input)
        {
            ObservedInputs.Add(("resources", input));
            Record("resources");
        }

        public WorldRenderFrameOutcome Render(RenderFrameInput input)
        {
            ObservedInputs.Add(("world", input));
            Record("world");
            return World;
        }

        public PrivatePresentationFrameOutcome Render(
            RenderFrameInput input,
            WorldRenderFrameOutcome world)
        {
            ObservedInputs.Add(("presentation", input));
            ObservedWorld = world;
            Record("presentation");
            return Presentation;
        }

        public void Publish(RenderFrameInput input, RenderFrameOutcome outcome)
        {
            ObservedInputs.Add(("diagnostics", input));
            ObservedOutcome = outcome;
            Record("diagnostics");
        }

        public void Process(RenderFrameInput input, RenderFrameOutcome outcome)
        {
            ObservedInputs.Add(("post-diagnostics", input));
            ObservedOutcome = outcome;
            Record("post-diagnostics");
        }

        public bool CapturePending(int width, int height)
        {
            Record("screenshot");
            return CaptureResult;
        }

        private void Record(string call)
        {
            _calls.Add(call);
            if (FailurePoint == call && Failure is not null)
                throw Failure;
        }
    }
}
