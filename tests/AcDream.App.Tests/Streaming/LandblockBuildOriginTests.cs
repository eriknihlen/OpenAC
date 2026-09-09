using System.Reflection;
using AcDream.App.Composition;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.App.Tests.Architecture;
using AcDream.App.World;
using AcDream.Core.Terrain;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Session;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Streaming;

public sealed class LandblockBuildOriginTests
{
    private const int SpinTimeoutMs = 2000;
    private const int SpinStepMs = 10;

    [Fact]
    public void ExplicitMapZeroOrigin_IsDistinctFromUnspecifiedCompatibilityOrigin()
    {
        var mapZero = new LandblockBuildOrigin(0, 0);

        Assert.True(mapZero.IsSpecified);
        Assert.False(default(LandblockBuildOrigin).IsSpecified);
        Assert.NotEqual(default, mapZero);
    }

    [Fact]
    public async Task RequestAwareLoad_CarriesExplicitMapZeroOriginThroughFactoryAndCompletion()
    {
        const uint landblockId = 0x0000FFFFu;
        var capturedOrigin = new LandblockBuildOrigin(0, 0);
        LandblockBuildRequest? observedRequest = null;

        using var streamer = LandblockStreamer.CreateForRequests(
            loadLandblock: request =>
            {
                observedRequest = request;
                return EmptyBuild(request.LandblockId, request.Origin);
            },
            buildMeshOrNull: (_, _) => EmptyMesh());
        streamer.EnqueueLoad(new LandblockBuildRequest(
            landblockId,
            LandblockStreamJobKind.LoadNear,
            Generation: 77,
            capturedOrigin));
        streamer.Start();

        var loaded = Assert.IsType<LandblockStreamResult.Loaded>(
            await DrainFirstAsync(streamer));

        Assert.Equal(
            new LandblockBuildRequest(
                landblockId,
                LandblockStreamJobKind.LoadNear,
                77,
                capturedOrigin),
            observedRequest);
        Assert.Equal(capturedOrigin, loaded.Build.Origin);
        Assert.Equal(77ul, loaded.Generation);
    }

    [Fact]
    public async Task RequestAwareLoad_WhenFactoryReturnsUnspecifiedOrigin_FailsAtMapZero()
    {
        using var streamer = LandblockStreamer.CreateForRequests(
            loadLandblock: request => EmptyBuild(request.LandblockId, default));
        streamer.EnqueueLoad(new LandblockBuildRequest(
            0x0000FFFFu,
            LandblockStreamJobKind.LoadNear,
            Generation: 78,
            new LandblockBuildOrigin(0, 0)));
        streamer.Start();

        var failed = Assert.IsType<LandblockStreamResult.Failed>(
            await DrainFirstAsync(streamer));

        Assert.Contains("origin", failed.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(78ul, failed.Generation);
    }

    [Fact]
    public void CompatibilityLoader_RejectsEvenExplicitMapZeroOriginBeforeQueueing()
    {
        using var streamer = new LandblockStreamer(loadLandblock: _ => null);

        Assert.Throws<InvalidOperationException>(() => streamer.EnqueueLoad(
            new LandblockBuildRequest(
                0x0000FFFFu,
                LandblockStreamJobKind.LoadNear,
                Generation: 79,
                new LandblockBuildOrigin(0, 0))));
    }

    [Fact]
    public void RequestAwareLoader_RejectsOriginlessCompatibilityEnqueue()
    {
        using var streamer = LandblockStreamer.CreateForRequests(
            loadLandblock: request => EmptyBuild(request.LandblockId, request.Origin));

        Assert.Throws<InvalidOperationException>(() =>
            streamer.EnqueueLoad(0xA9B4FFFFu));
    }

    [Fact]
    public void RequestAwareLoader_RejectsDirectRequestWithUnspecifiedOrigin()
    {
        using var streamer = LandblockStreamer.CreateForRequests(
            loadLandblock: request => EmptyBuild(request.LandblockId, request.Origin));

        Assert.Throws<InvalidOperationException>(() => streamer.EnqueueLoad(
            new LandblockBuildRequest(
                0xA9B4FFFFu,
                LandblockStreamJobKind.LoadNear,
                Generation: 80,
                Origin: default)));
    }

    [Fact]
    public async Task TwoQueuedLoads_RetainTheirDistinctOriginAndGeneration()
    {
        var observed = new List<LandblockBuildRequest>();
        var firstOrigin = new LandblockBuildOrigin(0xA9, 0xB4);
        var secondOrigin = new LandblockBuildOrigin(0x71, 0xEC);
        using var streamer = LandblockStreamer.CreateForRequests(
            loadLandblock: request =>
            {
                observed.Add(request);
                return EmptyBuild(request.LandblockId, request.Origin);
            },
            buildMeshOrNull: (_, _) => EmptyMesh(),
            workerCount: 1);

        streamer.EnqueueLoad(new LandblockBuildRequest(
            0xA9B4FFFFu,
            LandblockStreamJobKind.LoadNear,
            Generation: 80,
            firstOrigin));
        streamer.EnqueueLoad(new LandblockBuildRequest(
            0x71ECFFFFu,
            LandblockStreamJobKind.LoadNear,
            Generation: 81,
            secondOrigin));
        streamer.Start();

        IReadOnlyList<LandblockStreamResult> completions =
            await DrainCountAsync(streamer, 2);

        Assert.Equal([firstOrigin, secondOrigin], observed.Select(request => request.Origin));
        Assert.Equal([80ul, 81ul], completions.Select(result => result.Generation));
        Assert.Equal(
            [firstOrigin, secondOrigin],
            completions
                .Cast<LandblockStreamResult.Loaded>()
                .Select(result => result.Build.Origin));
    }

    [Fact]
    public async Task QueuedPromotion_SupersedesOldFarWithItsOwnOriginAndGeneration()
    {
        var observed = new List<LandblockBuildRequest>();
        var oldOrigin = new LandblockBuildOrigin(0xA9, 0xB4);
        var newOrigin = new LandblockBuildOrigin(0x71, 0xEC);
        const uint landblockId = 0x71ECFFFFu;
        using var streamer = LandblockStreamer.CreateForRequests(
            loadLandblock: request =>
            {
                observed.Add(request);
                return EmptyBuild(request.LandblockId, request.Origin);
            },
            buildMeshOrNull: (_, _) => EmptyMesh());

        streamer.EnqueueLoad(new LandblockBuildRequest(
            landblockId,
            LandblockStreamJobKind.LoadFar,
            Generation: 82,
            oldOrigin));
        streamer.EnqueueLoad(new LandblockBuildRequest(
            landblockId,
            LandblockStreamJobKind.PromoteToNear,
            Generation: 83,
            newOrigin));
        streamer.Start();

        var promoted = Assert.IsType<LandblockStreamResult.Promoted>(
            await DrainFirstAsync(streamer));

        Assert.Equal([new LandblockBuildRequest(
            landblockId,
            LandblockStreamJobKind.PromoteToNear,
            83,
            newOrigin)], observed);
        Assert.Equal(newOrigin, promoted.Build.Origin);
        Assert.Equal(83ul, promoted.Generation);
    }

    [Fact]
    public async Task FarLoad_StripsEnvCellsAndPhysicsEvenWhenEntityListIsAlreadyEmpty()
    {
        const uint landblockId = 0xA9B4FFFFu;
        var origin = new LandblockBuildOrigin(0xA9, 0xB4);
        var physics = new PhysicsDatBundle(
            new LandBlockInfo(),
            new Dictionary<uint, EnvCell>(),
            new Dictionary<uint, DatReaderWriter.DBObjs.Environment>(),
            new Dictionary<uint, Setup>(),
            new Dictionary<uint, GfxObj>());
        var envCells = new EnvCellLandblockBuild(
            landblockId,
            Array.Empty<AcDream.App.Rendering.LoadedCell>(),
            Array.Empty<EnvCellShellPlacement>());
        using var streamer = LandblockStreamer.CreateForRequests(
            loadLandblock: request => new LandblockBuild(
                new LoadedLandblock(
                    request.LandblockId,
                    new LandBlock(),
                    Array.Empty<WorldEntity>(),
                    physics),
                envCells,
                request.Origin),
            buildMeshOrNull: (_, _) => EmptyMesh());
        streamer.EnqueueLoad(new LandblockBuildRequest(
            landblockId,
            LandblockStreamJobKind.LoadFar,
            Generation: 84,
            origin));
        streamer.Start();

#if DEBUG
        // The near-payload tripwire is config-divergent by design ("fail loud
        // in Debug builds and strip in Release" — LandblockStreamer.HandleJob):
        // in Debug the Debug.Assert fires and the VSTest host translates it
        // into a thrown DebugAssertException, which the worker's catch folds
        // into a Failed completion.
        var failed = Assert.IsType<LandblockStreamResult.Failed>(
            await DrainFirstAsync(streamer));

        Assert.Contains(
            "Far-tier factory returned Near payload",
            failed.Error,
            StringComparison.Ordinal);
        Assert.Equal(84ul, failed.Generation);
#else
        var loaded = Assert.IsType<LandblockStreamResult.Loaded>(
            await DrainFirstAsync(streamer));

        Assert.Equal(LandblockStreamTier.Far, loaded.Tier);
        Assert.Empty(loaded.Landblock.Entities);
        Assert.Same(PhysicsDatBundle.Empty, loaded.Landblock.PhysicsDats);
        Assert.Null(loaded.Build.EnvCells);
        Assert.Equal(origin, loaded.Build.Origin);
#endif
    }

    [Fact]
    public void ProductionCompositionOwnsPublishersWithoutGameWindowFacade()
    {
        MethodInfo[] windowMethods = typeof(GameWindow).GetMethods(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        string[] extractedMethodNames =
        [
            "BuildLandblockForStreaming",
            "BuildSceneryEntitiesForStreaming",
            "BuildInteriorEntitiesForStreaming",
            "BuildPhysicsDatBundle",
            "ApplyLoadedTerrain",
            "PublishLandblockStaticLightingBeforeCollision",
        ];
        Assert.DoesNotContain(
            windowMethods,
            method => extractedMethodNames.Contains(method.Name, StringComparer.Ordinal));

        FieldInfo[] windowFields = typeof(GameWindow).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(
            windowFields,
            field => field.FieldType == typeof(LandblockPresentationPipeline));
        Assert.DoesNotContain(
            windowFields,
            field => field.FieldType == typeof(LandblockRenderPublisher)
                || field.FieldType == typeof(LandblockPhysicsPublisher)
                || field.FieldType == typeof(LandblockStaticPresentationPublisher));

        IReadOnlyList<CompiledCall> windowCalls =
            CompiledCallGraph.ReadDeclared(typeof(GameWindow));
        Assert.DoesNotContain(
            windowCalls,
            call => call.Target.DeclaringType == typeof(LiveWorldOriginState)
                && call.Target.Name == nameof(LiveWorldOriginState.Recenter));

        MethodInfo compose = typeof(LivePresentationCompositionPhase).GetMethod(
            "CompletePresentation",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        IReadOnlyList<CompiledCall> composeCalls = CompiledCallGraph.Read(compose);
        int renderPublisher = RequiredCallIndex(
            composeCalls,
            typeof(LandblockRenderPublisher),
            ".ctor");
        int physicsPublisher = RequiredCallIndex(
            composeCalls,
            typeof(LandblockPhysicsPublisher),
            ".ctor");
        int staticPublisher = RequiredCallIndex(
            composeCalls,
            typeof(LandblockStaticPresentationPublisher),
            ".ctor");
        int pipeline = RequiredCallIndex(
            composeCalls,
            typeof(LandblockPresentationPipeline),
            ".ctor");
        Assert.True(renderPublisher < physicsPublisher);
        Assert.True(physicsPublisher < staticPublisher);
        Assert.True(staticPublisher < pipeline);

    }

    [Fact]
    public void OriginRecenterWaitsForRetirementBeforeOriginAndDestinationCommits()
    {
        MethodInfo advance = typeof(StreamingOriginRecenterCoordinator).GetMethod(
            nameof(StreamingOriginRecenterCoordinator.Advance))!;
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(advance);
        int retirement = RequiredCallIndex(
            calls,
            typeof(StreamingController),
            "IsOriginRecenterRetirementComplete");
        int origin = RequiredCallIndex(
            calls,
            typeof(LiveWorldOriginState),
            nameof(LiveWorldOriginState.Recenter));
        int destination = RequiredCallIndex(
            calls,
            typeof(StreamingController),
            "TryCommitOriginRecenter");

        Assert.True(retirement < origin);
        Assert.True(origin < destination);
    }

    [Fact]
    public void GameWindowShutdownKeepsStreamerAliveUntilSessionResetConverges()
    {
        MethodInfo manifest = typeof(GameWindowShutdownManifest).GetMethod(
            nameof(GameWindowShutdownManifest.Create))!;
        IReadOnlyList<string> labels = CompiledCallGraph.ReadStringLiterals(manifest);
        AssertAppearsInOrder(
            labels,
            "host and session barriers",
            "game runtime session",
            "session dependents",
            "streamer");

        IReadOnlyList<CompiledCall> references =
            CompiledCallGraph.ReadMethodReferences(manifest);
        int stopSession = RequiredCallIndex(
            references,
            typeof(GameRuntime),
            nameof(GameRuntime.StopSession));
        int streamerDispose = Enumerable.Range(stopSession + 1, references.Count - stopSession - 1)
            .FirstOrDefault(
                index => Calls(
                    references[index].Target,
                    typeof(LandblockStreamer),
                    nameof(LandblockStreamer.Dispose)),
                -1);
        Assert.True(streamerDispose > stopSession, "Missing later streamer-disposal operation.");

        MethodInfo runtimeStop = typeof(GameRuntime).GetMethod(nameof(GameRuntime.StopSession))!;
        IReadOnlyList<CompiledCall> runtimeCalls = CompiledCallGraph.Read(runtimeStop);
        int sessionDispose = RequiredCallIndex(
            runtimeCalls,
            typeof(LiveSessionController),
            nameof(LiveSessionController.Dispose));
        int completionBarrier = RequiredCallIndex(
            runtimeCalls,
            typeof(LiveSessionController),
            "get_IsDisposalComplete");
        Assert.True(sessionDispose < completionBarrier);
        Assert.Contains(
            "The Runtime session shutdown was deferred by a re-entrant callback.",
            CompiledCallGraph.ReadStringLiterals(runtimeStop));
    }

    private static LandblockBuild EmptyBuild(uint landblockId, LandblockBuildOrigin origin) =>
        new(
            new LoadedLandblock(
                landblockId,
                new LandBlock(),
                Array.Empty<WorldEntity>()),
            Origin: origin);

    private static LandblockMeshData EmptyMesh() =>
        new(Array.Empty<TerrainVertex>(), Array.Empty<uint>());

    private static async Task<LandblockStreamResult> DrainFirstAsync(
        LandblockStreamer streamer) =>
        (await DrainCountAsync(streamer, 1))[0];

    private static async Task<IReadOnlyList<LandblockStreamResult>> DrainCountAsync(
        LandblockStreamer streamer,
        int count)
    {
        var results = new List<LandblockStreamResult>(count);
        for (int i = 0; i < SpinTimeoutMs / SpinStepMs && results.Count < count; i++)
        {
            results.AddRange(streamer.DrainCompletions(count - results.Count));
            if (results.Count < count)
                await Task.Delay(SpinStepMs);
        }

        Assert.Equal(count, results.Count);
        return results;
    }

    private static int RequiredCallIndex(
        IReadOnlyList<CompiledCall> calls,
        Type declaringType,
        string methodName)
    {
        int index = CompiledCallGraph.IndexOf(calls, declaringType, methodName);
        Assert.True(index >= 0, $"Missing compiled call: {declaringType.Name}.{methodName}");
        return index;
    }

    private static bool Calls(
        MethodBase method,
        Type declaringType,
        string methodName) =>
        method.GetMethodBody() is not null
        && CompiledCallGraph.IndexOf(
            CompiledCallGraph.Read(method),
            declaringType,
            methodName) >= 0;

    private static void AssertAppearsInOrder(
        IReadOnlyList<string> values,
        params string[] expected)
    {
        int cursor = -1;
        foreach (string value in expected)
        {
            int next = Enumerable.Range(cursor + 1, values.Count - cursor - 1)
                .FirstOrDefault(index => values[index] == value, -1);
            Assert.True(next > cursor, $"Missing or out-of-order metadata string: {value}");
            cursor = next;
        }
    }
}
