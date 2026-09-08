using System.Text.Json;
using AcDream.App.Diagnostics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Packs;
using AcDream.App.Rendering.Residency;
using AcDream.App.Rendering.Scene;
using AcDream.App.Streaming;
using AcDream.App.UI.Testing;
using AcDream.Runtime;
using AcDream.Runtime.World;
using SixLabors.ImageSharp;

namespace AcDream.App.Tests.Diagnostics;

public sealed class WorldLifecycleAutomationControllerTests
{
    private static readonly RenderFrameInput FrameInput = new(1d / 60d, 1280, 720);
    private static readonly RenderFrameOutcome FrameOutcome = new(
        new WorldRenderFrameOutcome(4, 17, NormalWorldDrawn: true),
        new PrivatePresentationFrameOutcome(
            PortalViewportDrawn: false,
            ScreenshotCaptured: true));

    [Fact]
    public void ScreenshotCapture_FlipsGlRowsAndWritesACompletePng()
    {
        string directory = NewDirectory();
        // GL order: bottom row red/green, top row blue/white.
        byte[] rgba =
        [
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255,
        ];
        var logs = new List<string>();
        var controller = new FrameScreenshotController(
            (_, _) => rgba,
            directory,
            logs.Add);

        try
        {
            Assert.True(controller.TryRequest("login_stable", out string error), error);
            Assert.False(controller.IsComplete("login_stable"));

            Assert.True(controller.CapturePending(2, 2));

            Assert.True(controller.IsComplete("login_stable"));
            string path = Path.Combine(directory, "login_stable.png");
            using Image image = Image.Load(path);
            Assert.Equal(2, image.Width);
            Assert.Equal(2, image.Height);
            Assert.Contains(logs, line => line.Contains("screenshot-complete", StringComparison.Ordinal));

            byte[] flipped = FrameScreenshotController.FlipRows(rgba, 2, 2);
            Assert.Equal(rgba.AsSpan(8, 8).ToArray(), flipped.AsSpan(0, 8).ToArray());
            Assert.Equal(rgba.AsSpan(0, 8).ToArray(), flipped.AsSpan(8, 8).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RetailScreenshotRequest_UsesFirstFreeScreenShotNumber()
    {
        string directory = NewDirectory();
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "ScreenShot00000.png"), [0]);
        var controller = new FrameScreenshotController(
            (_, _) => [255, 255, 255, 255],
            directory);

        try
        {
            Assert.True(controller.TryRequestRetailScreenshot(
                out string path,
                out string error), error);
            Assert.Equal(
                Path.Combine(directory, "ScreenShot00001.png"),
                path);
            Assert.True(controller.CapturePending(1, 1));
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ScreenshotCapture_ReportsNoWorkAndFailedCapture()
    {
        string directory = NewDirectory();
        var controller = new FrameScreenshotController(
            (_, _) => [],
            directory);

        try
        {
            Assert.False(controller.CapturePending(0, 0));
            Assert.True(controller.TryRequest("bad_frame", out string error), error);
            Assert.False(controller.CapturePending(0, 0));
            Assert.False(controller.IsComplete("bad_frame"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ScreenshotCapture_WritesRenderPackChoiceAndAtmosphereMetadata()
    {
        string directory = NewDirectory();
        var diagnostics = new RenderPackDiagnosticsSnapshot(
            RenderPackActivationState.Active,
            "acdream.atmospheric",
            "1.0.0",
            "high",
            "medium",
            FailureReason: null,
            ActivationGeneration: 7,
            RetainedGpuBytes: 64,
            TransientGpuBytes: 32,
            ImageCount: 5,
            BufferCount: 2,
            DrawCalls: 12,
            DispatchCalls: 4,
            ShadowCasterCount: 22,
            CascadeDrawCount: 44,
            CpuClassificationCalls: 1,
            SunElevationDegrees: 14.5,
            ActiveDayGroup: 2,
            Weather: "Clear",
            WeatherIntensity: 0.25,
            Outdoor: true,
            DirectionalShadowStrength: 0.8,
            Passes: [])
        {
            SharedWorldTransformUsedInstances = 68_395,
        };
        var controller = new FrameScreenshotController(
            (_, _) => [255, 255, 255, 255],
            directory,
            renderPackMetadata: () => diagnostics);
        Assert.Contains(
            "worldTransforms=68395used",
            RenderPackDiagnosticsFormatter.Format(diagnostics),
            StringComparison.Ordinal);

        try
        {
            Assert.True(controller.TryRequest("enhanced", out string error), error);
            Assert.True(controller.CapturePending(1, 1));

            using JsonDocument metadata = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory, "enhanced.metadata.json")));
            JsonElement root = metadata.RootElement;
            Assert.Equal(1, root.GetProperty("SchemaVersion").GetInt32());
            JsonElement pack = root.GetProperty("RenderPack");
            Assert.Equal("acdream.atmospheric", pack.GetProperty("PackId").GetString());
            Assert.Equal("medium", pack.GetProperty("EffectiveQuality").GetString());
            Assert.Equal(14.5, pack.GetProperty("SunElevationDegrees").GetDouble());
            Assert.True(pack.GetProperty("Outdoor").GetBoolean());
            Assert.Equal(
                68_395u,
                pack.GetProperty("SharedWorldTransformUsedInstances").GetUInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("has spaces")]
    [InlineData("name.png")]
    public void ArtifactNames_RejectPathsAndAmbiguousCharacters(string name)
    {
        Assert.False(AutomationArtifactName.TryValidate(name, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void DefaultFramebufferRead_BindsFramebufferZeroAndRestoresTheCallersBinding()
    {
        var surface = new RecordingFramebufferSurface(
            boundOnEntry: 7u,
            drawBoundOnEntry: 3u,
            fill: 0x42);

        byte[] pixels = FrameScreenshotController.ReadDefaultFramebuffer(surface, 2, 3);

        Assert.Equal(
            [
                "bind-read 0",
                "bind-draw 0",
                "samples",
                "read 2x3",
                "bind-read 7",
                "bind-draw 3",
            ],
            surface.Calls);
        Assert.Equal(0u, surface.ReadFramebufferBindingDuringRead);
        Assert.Equal(2 * 3 * 4, pixels.Length);
        Assert.All(pixels, value => Assert.Equal(0x42, value));
        Assert.Equal(7u, surface.ReadFramebufferBinding);
        Assert.Equal(3u, surface.DrawFramebufferBinding);
    }

    [Fact]
    public void DefaultFramebufferRead_RestoresTheCallersBindingWhenTheReadThrows()
    {
        var surface = new RecordingFramebufferSurface(
            boundOnEntry: 9u,
            drawBoundOnEntry: 0u,
            fill: 0)
        {
            ReadFailure = new InvalidOperationException("GL_OUT_OF_MEMORY"),
        };

        Assert.Throws<InvalidOperationException>(
            () => FrameScreenshotController.ReadDefaultFramebuffer(surface, 1, 1));

        Assert.Equal(
            ["bind-read 0", "bind-draw 0", "samples", "read 1x1", "bind-read 9", "bind-draw 0"],
            surface.Calls);
        Assert.Equal(9u, surface.ReadFramebufferBinding);
    }

    [Fact]
    public void MultisampledDefaultFramebufferRead_ResolvesBeforeReading()
    {
        var surface = new RecordingFramebufferSurface(
            boundOnEntry: 7u,
            drawBoundOnEntry: 4u,
            fill: 0x11)
        {
            Samples = 4,
        };

        byte[] pixels = FrameScreenshotController.ReadDefaultFramebuffer(surface, 2, 3);

        Assert.Equal(
            [
                "bind-read 0",
                "bind-draw 0",
                "samples",
                "create-resolve 2x3",
                "bind-draw 64",
                "blit 2x3",
                "bind-read 64",
                "read 2x3",
                "delete-resolve 64",
                "bind-read 7",
                "bind-draw 4",
            ],
            surface.Calls);

        // The blit source must still be the multisampled default framebuffer,
        // and the read source must be the resolved single-sampled one.
        Assert.Equal(0u, surface.ReadFramebufferBindingDuringBlit);
        Assert.Equal(64u, surface.DrawFramebufferBindingDuringBlit);
        Assert.Equal(64u, surface.ReadFramebufferBindingDuringRead);
        Assert.Equal(2 * 3 * 4, pixels.Length);
        Assert.All(pixels, value => Assert.Equal(0x11, value));
    }

    [Fact]
    public void MultisampledDefaultFramebufferRead_DeletesTheResolveTargetWhenTheReadThrows()
    {
        var surface = new RecordingFramebufferSurface(
            boundOnEntry: 7u,
            drawBoundOnEntry: 4u,
            fill: 0)
        {
            Samples = 4,
            ReadFailure = new InvalidOperationException("GL_OUT_OF_MEMORY"),
        };

        Assert.Throws<InvalidOperationException>(
            () => FrameScreenshotController.ReadDefaultFramebuffer(surface, 1, 1));

        Assert.Contains("delete-resolve 64", surface.Calls);
        Assert.Equal(0, surface.LiveResolveTargets);
        Assert.Equal(7u, surface.ReadFramebufferBinding);
        Assert.Equal(4u, surface.DrawFramebufferBinding);
    }

    private sealed class RecordingFramebufferSurface(
        uint boundOnEntry,
        uint drawBoundOnEntry,
        byte fill)
        : FrameScreenshotController.IDefaultFramebufferSurface
    {
        private uint _readBinding = boundOnEntry;
        private uint _drawBinding = drawBoundOnEntry;
        private uint _nextResolveName = 64u;

        public List<string> Calls { get; } = [];

        public Exception? ReadFailure { get; init; }

        public int Samples { get; init; } = 1;

        public int LiveResolveTargets { get; private set; }

        public uint? ReadFramebufferBindingDuringRead { get; private set; }

        public uint? ReadFramebufferBindingDuringBlit { get; private set; }

        public uint? DrawFramebufferBindingDuringBlit { get; private set; }

        public uint ReadFramebufferBinding => _readBinding;

        public uint DrawFramebufferBinding => _drawBinding;

        public int DefaultFramebufferSamples
        {
            get
            {
                Calls.Add("samples");
                return Samples;
            }
        }

        public void BindReadFramebuffer(uint framebuffer)
        {
            _readBinding = framebuffer;
            Calls.Add($"bind-read {framebuffer}");
        }

        public void BindDrawFramebuffer(uint framebuffer)
        {
            _drawBinding = framebuffer;
            Calls.Add($"bind-draw {framebuffer}");
        }

        public uint CreateResolveTarget(int width, int height)
        {
            Calls.Add($"create-resolve {width}x{height}");
            LiveResolveTargets++;
            return _nextResolveName++;
        }

        public void DeleteResolveTarget(uint framebuffer)
        {
            Calls.Add($"delete-resolve {framebuffer}");
            LiveResolveTargets--;
        }

        public void BlitColorNearest(int width, int height)
        {
            Calls.Add($"blit {width}x{height}");
            ReadFramebufferBindingDuringBlit = _readBinding;
            DrawFramebufferBindingDuringBlit = _drawBinding;
        }

        public void ReadRgba(int width, int height, byte[] destination)
        {
            Calls.Add($"read {width}x{height}");
            ReadFramebufferBindingDuringRead = _readBinding;
            if (ReadFailure is not null)
                throw ReadFailure;
            Array.Fill(destination, fill);
        }
    }

    [Fact]
    public void Checkpoint_WritesCanonicalRevealAndResourceSnapshot()
    {
        string directory = NewDirectory();
        var reveal = new RuntimePortalSnapshot(
            Generation: 7,
            Kind: RuntimePortalKind.Portal,
            Readiness: new RuntimeDestinationReadiness(
                Generation: 7,
                0x8A020164u,
                IsIndoor: true,
                IsUnhydratable: false,
                RequiredRenderRadius: 0,
                IsRenderNeighborhoodReady: true,
                AreCompositeTexturesReady: true,
                IsCollisionReady: true),
            Materialized: true,
            Completed: true,
            Cancelled: false,
            WorldViewportObserved: true,
            WorldSimulationAvailable: true,
            InvariantFailureCount: 0,
            WaitCueShown: false,
            PortalMaterializationCount: 3);
        var resources = EmptyResources() with
        {
            LoadedLandblocks = 1,
            WorldEntities = 42,
            RenderSceneOracle = new CurrentRenderSceneOracleSnapshot(
                Enabled: true,
                CompletedFrameSequence: 19,
                AbortedFrames: 1,
                ProjectionCount: 73,
                OutdoorStaticCount: 41,
                CellStaticCount: 20,
                DynamicCount: 12,
                CellBucketCount: 4,
                Digest: new RenderSceneHash128(
                    Low: 0x0123456789ABCDEF,
                    High: 0xFEDCBA9876543210),
                CompletedPViewFrameSequence: 19,
                AbortedPViewFrames: 0,
                PViewCandidateCount: 61,
                PViewDigest: new RenderSceneHash128(11, 12),
                DispatcherFrameSequence: 19,
                AbortedDispatcherFrames: 0,
                DispatcherDrawCount: 3,
                DispatcherEntityCount: 52,
                DispatcherMeshRefCount: 80,
                DispatcherInstanceCount: 96,
                DispatcherOpaqueGroupCount: 11,
                DispatcherTransparentGroupCount: 4,
                DispatcherDigest: new RenderSceneHash128(13, 14),
                CompletedSelectionFrameSequence: 19,
                AbortedSelectionFrames: 0,
                SelectionPartCount: 7,
                SelectionDigest: new RenderSceneHash128(15, 16)),
            RenderSceneShadow =
                RenderSceneShadowComparisonSnapshot.Disabled with
                {
                    Enabled = true,
                    ComparisonCount = 19,
                    SuccessfulComparisonCount = 19,
                    ComparedOracleFrameSequence = 19,
                    MatchedProjectionCount = 73,
                },
            TrackedGpuBytes = 1234,
            Residency = new ResidencySnapshot(
                [
                    new ResidencyDomainSnapshot(
                        ResidencyDomain.Animations,
                        EntryCount: 2,
                        OwnerCount: 0,
                        Charges: new ResidencyCharges(
                            DecodedBytes: 1024),
                        BudgetBytes: 4096,
                        Hits: 8,
                        Misses: 3,
                        Evictions: 1),
                ],
                new ResidencyCharges(DecodedBytes: 1024)),
        };
        var screenshots = new FrameScreenshotController(
            (_, _) => [0, 0, 0, 255],
            Path.Combine(directory, "screenshots"));
        int clientCloseRequests = 0;
        var controller = new WorldLifecycleAutomationController(
            () => reveal,
            () => new RuntimeWorldEnvironmentOwnershipSnapshot(
                IsInitialized: true,
                DayGroupDefinitionCount: 4,
                ActiveDayGroupCount: 1),
            () => new RuntimeWorldTransitOwnershipSnapshot(
                BufferedTeleportDestinationCount: 0,
                PendingTeleportStartCount: 0,
                ActiveTeleportCount: 0,
                AcceptedTeleportDestinationCount: 0,
                ActiveRevealCount: 0,
                PendingDestinationReadinessCount: 0,
                HostProjectionCount: 0,
                PendingHostAcknowledgementCount: 0),
            () => 3,
            _ => resources,
            screenshots,
            directory,
            requestClientClose: () => clientCloseRequests++);

        try
        {
            Assert.True(controller.IsWorldReady);
            Assert.True(controller.IsWorldViewportVisible);
            Assert.Equal(3, controller.PortalMaterializationCount);
            Assert.True(
                controller.TryRequestClientClose(out string closeError),
                closeError);
            Assert.Equal(1, clientCloseRequests);
            Assert.True(controller.TryRequestCheckpoint(
                "dungeon",
                out IRetailUiAutomationCheckpoint? request,
                out string error), error);
            Assert.NotNull(request);
            Assert.Equal(RetailUiAutomationCheckpointStatus.Pending, request.Status);

            controller.Process(FrameInput, FrameOutcome);

            Assert.Equal(RetailUiAutomationCheckpointStatus.Succeeded, request.Status);

            string line = Assert.Single(File.ReadAllLines(
                Path.Combine(directory, "world-lifecycle.checkpoints.jsonl")));
            using JsonDocument json = JsonDocument.Parse(line);
            Assert.Equal("dungeon", json.RootElement.GetProperty("name").GetString());
            Assert.Equal(7, json.RootElement.GetProperty("reveal").GetProperty("generation").GetInt64());
            Assert.Equal(
                4,
                json.RootElement.GetProperty("environmentOwnership")
                    .GetProperty("dayGroupDefinitionCount").GetInt32());
            Assert.Equal(
                0,
                json.RootElement.GetProperty("transitOwnership")
                    .GetProperty("pendingHostAcknowledgementCount")
                    .GetInt32());
            Assert.Equal(4, json.RootElement.GetProperty("render").GetProperty("world")
                .GetProperty("visibleLandblocks").GetInt32());
            Assert.True(json.RootElement.GetProperty("render").GetProperty("presentation")
                .GetProperty("screenshotCaptured").GetBoolean());
            Assert.Equal(42, json.RootElement.GetProperty("resources").GetProperty("worldEntities").GetInt32());
            JsonElement renderSceneOracle = json.RootElement
                .GetProperty("resources")
                .GetProperty("renderSceneOracle");
            Assert.True(renderSceneOracle.GetProperty("enabled").GetBoolean());
            Assert.Equal(
                19,
                renderSceneOracle.GetProperty("completedFrameSequence").GetInt64());
            Assert.Equal(
                73,
                renderSceneOracle.GetProperty("projectionCount").GetInt32());
            Assert.Equal(
                0x0123456789ABCDEFuL,
                renderSceneOracle.GetProperty("digest").GetProperty("low")
                    .GetUInt64());
            Assert.Equal(
                0xFEDCBA9876543210uL,
                renderSceneOracle.GetProperty("digest").GetProperty("high")
                    .GetUInt64());
            JsonElement renderSceneShadow = json.RootElement
                .GetProperty("resources")
                .GetProperty("renderSceneShadow");
            Assert.True(renderSceneShadow.GetProperty("enabled").GetBoolean());
            Assert.Equal(
                19,
                renderSceneShadow.GetProperty("comparisonCount").GetInt64());
            Assert.Equal(
                73,
                renderSceneShadow.GetProperty("matchedProjectionCount")
                    .GetInt32());
            Assert.Equal(
                0,
                renderSceneShadow.GetProperty("mismatchCount").GetInt64());
            JsonElement residency = json.RootElement
                .GetProperty("resources")
                .GetProperty("residency");
            Assert.Equal(
                4096,
                residency.GetProperty("totalBudgetBytes").GetInt64());
            Assert.Equal(8, residency.GetProperty("totalHits").GetInt64());
            Assert.Equal(
                1024,
                residency.GetProperty("totalCharges")
                    .GetProperty("decodedBytes")
                    .GetInt64());
            Assert.True(File.Exists(Path.Combine(directory, "checkpoint-dungeon.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CheckpointRequests_DrainInFifoOrderOnce()
    {
        string directory = NewDirectory();
        int captures = 0;
        var controller = CreateController(
            directory,
            _ =>
            {
                captures++;
                return EmptyResources();
            });

        try
        {
            Assert.True(controller.TryRequestCheckpoint(
                "first", out IRetailUiAutomationCheckpoint? first, out _));
            Assert.True(controller.TryRequestCheckpoint(
                "second", out IRetailUiAutomationCheckpoint? second, out _));

            controller.Process(FrameInput, FrameOutcome);
            controller.Process(FrameInput, FrameOutcome);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(1, first.Sequence);
            Assert.Equal(2, second.Sequence);
            Assert.Equal(RetailUiAutomationCheckpointStatus.Succeeded, first.Status);
            Assert.Equal(RetailUiAutomationCheckpointStatus.Succeeded, second.Status);
            Assert.Equal(2, captures);
            string[] lines = File.ReadAllLines(
                Path.Combine(directory, "world-lifecycle.checkpoints.jsonl"));
            Assert.Equal(2, lines.Length);
            Assert.Contains("\"name\":\"first\"", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"name\":\"second\"", lines[1], StringComparison.Ordinal);
        }
        finally
        {
            controller.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CheckpointWriteFailure_IsReportedOnTokenWithoutThrowingFromFrame()
    {
        string directory = NewDirectory();
        string blockedPath = Path.Combine(directory, "not-a-directory");
        File.WriteAllText(blockedPath, "occupied");
        var controller = CreateController(blockedPath, _ => EmptyResources());

        try
        {
            Assert.True(controller.TryRequestCheckpoint(
                "failed", out IRetailUiAutomationCheckpoint? request, out _));

            controller.Process(FrameInput, FrameOutcome);

            Assert.NotNull(request);
            Assert.Equal(RetailUiAutomationCheckpointStatus.Failed, request.Status);
            Assert.Contains("checkpoint 'failed' sequence 1 failed", request.Error);
        }
        finally
        {
            controller.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CheckpointCancellationAndShutdown_AreExplicitAndNeverWritten()
    {
        string directory = NewDirectory();
        var controller = CreateController(directory, _ => EmptyResources());

        try
        {
            Assert.True(controller.TryRequestCheckpoint(
                "caller", out IRetailUiAutomationCheckpoint? caller, out _));
            Assert.True(controller.TryRequestCheckpoint(
                "shutdown", out IRetailUiAutomationCheckpoint? shutdown, out _));
            Assert.NotNull(caller);
            Assert.NotNull(shutdown);

            controller.CancelCheckpoint(caller);
            controller.Dispose();
            controller.Process(FrameInput, FrameOutcome);

            Assert.Equal(RetailUiAutomationCheckpointStatus.Cancelled, caller.Status);
            Assert.Contains("cancelled before render-frame capture", caller.Error);
            Assert.Equal(RetailUiAutomationCheckpointStatus.Cancelled, shutdown.Status);
            Assert.Contains("cancelled during world lifecycle automation shutdown", shutdown.Error);
            Assert.False(controller.TryRequestCheckpoint(
                "late", out IRetailUiAutomationCheckpoint? late, out string error));
            Assert.Null(late);
            Assert.Contains("shutting down", error);
            Assert.False(File.Exists(Path.Combine(
                directory,
                "world-lifecycle.checkpoints.jsonl")));
        }
        finally
        {
            controller.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RenderPackPerformanceReset_IsNoOpOnlyAfterFailedToRetail()
    {
        string directory = NewDirectory();
        bool failedToRetail = true;
        int resetCalls = 0;
        var controller = new WorldLifecycleAutomationController(
            () => default,
            () => default,
            () => default,
            () => 0,
            _ => EmptyResources(),
            new FrameScreenshotController((_, _) => [], directory),
            directory,
            getRenderPackPerformanceSampleCount: () => 0,
            resetRenderPackPerformance: () =>
            {
                resetCalls++;
                return (true, string.Empty);
            },
            getRenderPackFailedToRetail: () => failedToRetail);

        try
        {
            Assert.True(controller.RenderPackFailedToRetail);
            Assert.True(controller.TryResetRenderPackPerformance(out string fallbackError));
            Assert.Empty(fallbackError);
            Assert.Equal(0, resetCalls);

            failedToRetail = false;
            Assert.True(controller.TryResetRenderPackPerformance(out string activeError));
            Assert.Empty(activeError);
            Assert.Equal(1, resetCalls);
        }
        finally
        {
            controller.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AutomationSignalReadsOnlyValidatedNamesFromOwnedArtifactDirectory()
    {
        string directory = NewDirectory();
        var controller = new WorldLifecycleAutomationController(
            () => default,
            () => default,
            () => default,
            () => 0,
            _ => EmptyResources(),
            new FrameScreenshotController((_, _) => [], directory),
            directory);

        try
        {
            Assert.True(controller.TryIsAutomationSignalPublished(
                "observer-moved", out bool missing, out string missingError),
                missingError);
            Assert.False(missing);

            string signalDirectory = Path.Combine(directory, "signals");
            Directory.CreateDirectory(signalDirectory);
            File.WriteAllText(
                Path.Combine(signalDirectory, "observer-moved.signal"),
                "published");

            Assert.True(controller.TryIsAutomationSignalPublished(
                "observer-moved", out bool published, out string publishedError),
                publishedError);
            Assert.True(published);
            Assert.False(controller.TryIsAutomationSignalPublished(
                "../outside", out bool invalid, out string invalidError));
            Assert.False(invalid);
            Assert.Contains("may contain only", invalidError);
        }
        finally
        {
            controller.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TransitionAutomationDelegatesPreserveExactDisableReenableAndResizeOwnership()
    {
        string directory = NewDirectory();
        var status = new RetailUiAutomationRenderPackStatus(
            RetailUiAutomationRenderPackState.Active,
            "acdream.atmospheric",
            "medium",
            ActivationGeneration: 1,
            FailureReason: null);
        var framebuffer = (Width: 1280, Height: 720);
        var calls = new List<string>();
        var controller = new WorldLifecycleAutomationController(
            () => default,
            () => default,
            () => default,
            () => 0,
            _ => EmptyResources(),
            new FrameScreenshotController((_, _) => [], directory),
            directory,
            getRenderPackStatus: () => status,
            selectRenderPack: preset =>
            {
                calls.Add($"select:{preset}");
                return (true, string.Empty);
            },
            disableRenderPack: () =>
            {
                calls.Add("disable-exact");
                return (true, string.Empty);
            },
            reenableRenderPack: () =>
            {
                calls.Add("reenable-exact");
                return (true, string.Empty);
            },
            getFramebufferSize: () => framebuffer,
            resizeFramebuffer: (width, height) =>
            {
                calls.Add($"resize:{width}x{height}");
                framebuffer = (width, height);
                return (true, string.Empty);
            });

        try
        {
            Assert.Equal(status, controller.RenderPackStatus);
            Assert.True(controller.TrySelectRenderPack("high", out string selectError));
            Assert.Empty(selectError);
            Assert.True(controller.TryDisableRenderPack(out string disableError));
            Assert.Empty(disableError);
            Assert.True(controller.TryReenableRenderPack(out string reenableError));
            Assert.Empty(reenableError);
            Assert.True(controller.TryResizeFramebuffer(1024, 768, out string resizeError));
            Assert.Empty(resizeError);
            Assert.Equal(1024, controller.FramebufferWidth);
            Assert.Equal(768, controller.FramebufferHeight);
            Assert.Equal(
                ["select:high", "disable-exact", "reenable-exact", "resize:1024x768"],
                calls);
        }
        finally
        {
            controller.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static WorldLifecycleAutomationController CreateController(
        string directory,
        Func<RenderFrameOutcome, WorldLifecycleResourceSnapshot> capture) =>
        new(
            () => default,
            () => default,
            () => default,
            () => 0,
            capture,
            new FrameScreenshotController((_, _) => [], directory),
            directory);

    private static WorldLifecycleResourceSnapshot EmptyResources() => new(
        0, 0, 0, 0, 0, 0, 0, CurrentRenderSceneOracleSnapshot.Disabled,
        RenderSceneShadowComparisonSnapshot.Disabled,
        RenderFrameProductComparisonSnapshot.Disabled,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0L, 0, 0L, 0L, 0L, 0L, 0, 0, 0, 0, 0, 0, 0, 0L, 0L,
        0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L,
        0, 0, 0, 0, 0, 0, 0, default,
        default, default, 0d, 0d, null);

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "acdream-world-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
