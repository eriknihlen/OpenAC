using System.Globalization;
using System.Reflection;
using AcDream.App.Rendering;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering;

public sealed class RenderFrameDiagnosticsControllerTests
{
    private static readonly RenderFrameOutcome Outcome = new(
        new WorldRenderFrameOutcome(
            VisibleLandblocks: 17,
            TotalLandblocks: 43,
            NormalWorldDrawn: true),
        new PrivatePresentationFrameOutcome(
            PortalViewportDrawn: false,
            ScreenshotCaptured: true));

    private static readonly RenderFrameTitleFacts TitleFacts = new(
        EntityCount: 3721,
        AnimatedEntityCount: 226,
        Calendar: new DerethDateTime.Calendar(
            117,
            DerethDateTime.MonthName.Leafcull,
            22,
            DerethDateTime.HourName.DawnsongAndHalf),
        DayFraction: 0.3125);

    [Fact]
    public void Snapshot_StartsAtTheAcceptedRuntimeDefaults()
    {
        var harness = new Harness(resourceDump: false);

        Assert.Equal(RenderFrameDiagnosticsSnapshot.Initial, harness.Controller.Snapshot);
        Assert.Equal(60.0, harness.Controller.Snapshot.Fps);
        Assert.Equal(16.7, harness.Controller.Snapshot.FrameMilliseconds);
        Assert.Empty(harness.Calls);
    }

    [Fact]
    public void Publish_BelowHalfSecondDoesNotSampleOrPublish()
    {
        var harness = new Harness(resourceDump: true);

        harness.Publish(0.499999);

        Assert.Empty(harness.Calls);
        Assert.Equal(RenderFrameDiagnosticsSnapshot.Initial, harness.Controller.Snapshot);
    }

    [Fact]
    public void Publish_AtHalfSecondPublishesTitleThenResourcesThenSnapshot()
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        var harness = new Harness(resourceDump: true);

        harness.Publish(0.2);
        harness.Publish(0.3);

        Assert.Equal(["facts", "title", "resources", "log"], harness.Calls);
        Assert.Equal(
            "acdream | 4 fps | 250.0 ms | lb 17/43 | ent 3721/anim 226 | "
            + "PY117 Leafcull 22 DawnsongAndHalf (df=0.3125)",
            harness.Title);
        Assert.Equal(
            new RenderFrameDiagnosticsSnapshot(
                Fps: 4.0,
                FrameMilliseconds: 250.0,
                VisibleLandblocks: 17,
                TotalLandblocks: 43,
                EntityCount: 3721,
                AnimatedEntityCount: 226),
            harness.Controller.Snapshot);
        Assert.Equal(
            RenderFrameDiagnosticsController.FormatGpuStream(ResourceFacts),
            harness.Line);
    }

    [Fact]
    public void Publish_WhenResourceDumpIsOffDoesNotCaptureResources()
    {
        var harness = new Harness(resourceDump: false);

        harness.Publish(0.5);

        Assert.Equal(["facts", "title"], harness.Calls);
        Assert.Null(harness.Line);
    }

    [Fact]
    public void Publish_DiscardsCadenceOvershootAfterSuccessfulPublication()
    {
        var harness = new Harness(resourceDump: false);

        harness.Publish(0.6);
        harness.Calls.Clear();
        harness.Publish(0.4);

        Assert.Empty(harness.Calls);
        Assert.Equal(1.0 / 0.6, harness.Controller.Snapshot.Fps, precision: 10);

        harness.Publish(0.1);

        Assert.Equal(["facts", "title"], harness.Calls);
        Assert.Equal(4.0, harness.Controller.Snapshot.Fps, precision: 10);
        Assert.Equal(250.0, harness.Controller.Snapshot.FrameMilliseconds, precision: 10);
    }

    [Fact]
    public void TitleFailure_DoesNotCaptureResourcesPublishSnapshotOrResetWindow()
    {
        var harness = new Harness(resourceDump: true)
        {
            TitleFailure = new InvalidOperationException("title"),
        };

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => harness.Publish(0.5));

        Assert.Same(harness.TitleFailure, actual);
        Assert.Equal(["facts", "title"], harness.Calls);
        Assert.Equal(RenderFrameDiagnosticsSnapshot.Initial, harness.Controller.Snapshot);

        harness.TitleFailure = null;
        harness.Calls.Clear();
        harness.Publish(0.0);

        Assert.Equal(["facts", "title", "resources", "log"], harness.Calls);
        Assert.Equal(4.0, harness.Controller.Snapshot.Fps, precision: 10);
        Assert.Equal(250.0, harness.Controller.Snapshot.FrameMilliseconds, precision: 10);
    }

    [Fact]
    public void ResourceLogFailure_LeavesPublishedCacheAndCadenceUntouched()
    {
        var harness = new Harness(resourceDump: true)
        {
            LogFailure = new InvalidOperationException("log"),
        };

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => harness.Publish(0.5));

        Assert.Same(harness.LogFailure, actual);
        Assert.Equal(["facts", "title", "resources", "log"], harness.Calls);
        Assert.NotNull(harness.Title);
        Assert.Equal(RenderFrameDiagnosticsSnapshot.Initial, harness.Controller.Snapshot);

        harness.LogFailure = null;
        harness.Calls.Clear();
        harness.Publish(0.0);

        Assert.Equal(["facts", "title", "resources", "log"], harness.Calls);
        Assert.Equal(4.0, harness.Controller.Snapshot.Fps, precision: 10);
    }

    [Fact]
    public void Constructor_RequiresEveryActiveNarrowSeam()
    {
        var facts = new FixedTitleFactsSource([], TitleFacts);
        var title = new RecordingTitleSink([]);
        var log = new RecordingLog([]);
        var resources = new RecordingResources([], ResourceFacts);

        Assert.Throws<ArgumentNullException>(() => new RenderFrameDiagnosticsController(
            null!, title, log, false));
        Assert.Throws<ArgumentNullException>(() => new RenderFrameDiagnosticsController(
            facts, null!, log, false));
        Assert.Throws<ArgumentNullException>(() => new RenderFrameDiagnosticsController(
            facts, title, null!, false));
        Assert.Throws<ArgumentNullException>(() => new RenderFrameDiagnosticsController(
            facts, title, log, true));

        _ = new RenderFrameDiagnosticsController(facts, title, log, false, resources: null);
        _ = new RenderFrameDiagnosticsController(facts, title, log, true, resources);
    }

    [Fact]
    public void Controller_RetainsOnlyNarrowTypedSeamsAndValueState()
    {
        FieldInfo[] fields = typeof(RenderFrameDiagnosticsController).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.DoesNotContain(fields, field => field.FieldType == typeof(GameWindow));
        Assert.DoesNotContain(fields, field => typeof(Delegate).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(fields, field =>
            field.FieldType.FullName?.Contains("Silk.NET.Windowing.IWindow", StringComparison.Ordinal) == true);
        Assert.Contains(fields, field => field.FieldType == typeof(IRenderFrameTitleFactsSource));
        Assert.Contains(fields, field => field.FieldType == typeof(IRenderFrameTitleSink));
        Assert.Contains(fields, field => field.FieldType == typeof(IRenderFrameDiagnosticLog));
        Assert.Contains(fields, field => field.FieldType == typeof(IRenderFrameResourceDiagnosticsSource));
        Assert.True(typeof(IRenderFrameDiagnosticsPhase).IsAssignableFrom(
            typeof(RenderFrameDiagnosticsController)));
    }

    [Fact]
    public void GpuStreamFormatter_PreservesTheAcceptedFieldOrderAndShape()
    {
        string actual = RenderFrameDiagnosticsController.FormatGpuStream(ResourceFacts);

        Assert.Equal(
            "[gpu-stream] emit=1 particles=2 bindings=3 wbSets=4 cellSets=5 "
            + "particleSets=6/7B uiBytes=8 portalBytes=9 clipSets=10 terrainBuffers=11 "
            + "lightBuffers=12 mesh=13/14/15 meshEst=16 meshUpload=17/18 "
            + "uploadFrame=19/20/21/22/23 bufferFrame=24/25/26/27 "
            + "staging=28/29/True/30 cpuMesh=31/32 mipmapFrame=33/34 meshCap=35 "
            + "meshPhys=36/True prepared=60/61/62/63/64 "
            + "gpuTrack=57/58/59 managed=55/56 ownedTextures=37/38 "
            + "particleTextures=39/40/41/42/43 compositeCache=44/45/46 "
            + "compositeAtlas=47/48 compositeUpload=49/50/51",
            actual);
    }

    private sealed class Harness
    {
        public readonly List<string> Calls = [];
        public readonly RenderFrameDiagnosticsController Controller;
        private readonly RecordingTitleSink _title;
        private readonly RecordingLog _log;

        public Harness(bool resourceDump)
        {
            _title = new RecordingTitleSink(Calls);
            _log = new RecordingLog(Calls);
            Controller = new RenderFrameDiagnosticsController(
                new FixedTitleFactsSource(Calls, TitleFacts),
                _title,
                _log,
                resourceDump,
                new RecordingResources(Calls, ResourceFacts));
        }

        public Exception? TitleFailure
        {
            get => _title.Failure;
            set => _title.Failure = value;
        }

        public Exception? LogFailure
        {
            get => _log.Failure;
            set => _log.Failure = value;
        }

        public string? Title => _title.Title;
        public string? Line => _log.Line;

        public void Publish(double deltaSeconds) => Controller.Publish(
            new RenderFrameInput(deltaSeconds, 1920, 1080),
            Outcome);
    }

    private sealed class FixedTitleFactsSource(
        List<string> calls,
        RenderFrameTitleFacts facts) : IRenderFrameTitleFactsSource
    {
        public RenderFrameTitleFacts Capture()
        {
            calls.Add("facts");
            return facts;
        }
    }

    private sealed class RecordingTitleSink(List<string> calls) : IRenderFrameTitleSink
    {
        public Exception? Failure { get; set; }
        public string? Title { get; private set; }

        public void SetTitle(string title)
        {
            calls.Add("title");
            if (Failure is not null)
                throw Failure;
            Title = title;
        }
    }

    private sealed class RecordingResources(
        List<string> calls,
        RenderFrameResourceDiagnosticsSnapshot snapshot)
        : IRenderFrameResourceDiagnosticsSource
    {
        public RenderFrameResourceDiagnosticsSnapshot Capture()
        {
            calls.Add("resources");
            return snapshot;
        }
    }

    private sealed class RecordingLog(List<string> calls) : IRenderFrameDiagnosticLog
    {
        public Exception? Failure { get; set; }
        public string? Line { get; private set; }

        public void WriteLine(string message)
        {
            calls.Add("log");
            if (Failure is not null)
                throw Failure;
            Line = message;
        }
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _prior = CultureInfo.CurrentCulture;

        public CultureScope(CultureInfo culture) => CultureInfo.CurrentCulture = culture;

        public void Dispose() => CultureInfo.CurrentCulture = _prior;
    }

    private static readonly RenderFrameResourceDiagnosticsSnapshot ResourceFacts = new(
        new VfxStreamResourceDiagnostics(1, 2, 3),
        new DynamicBufferResourceDiagnostics(4, 5, 6, 7, 8, 9, 10, 11, 12),
        new MeshStreamResourceDiagnostics(
            13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23,
            24, 25, 26, 27, 28, 29, true, 30, 31, 32, 33, 34, 35, 36, true,
            60, 61, 62, 63, 64),
        new TextureStreamResourceDiagnostics(
            37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51),
        new ProcessResourceDiagnostics(55, 56, 57, 58, 59));
}
