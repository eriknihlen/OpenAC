using System.Text.RegularExpressions;

namespace AcDream.App.Tests.Runtime;

public sealed class RuntimePhysicsOwnershipTests
{
    [Fact]
    public void ProductionHostsUseSharedPlacementSubscriptionWithoutDirectChannel()
    {
        string root = FindRepositoryRoot();
        foreach (string relative in new[]
                 {
                     Path.Combine("src", "AcDream.App"),
                     Path.Combine("src", "AcDream.Headless"),
                 })
        {
            string[] sources = Directory.EnumerateFiles(
                    Path.Combine(root, relative),
                    "*.cs",
                    SearchOption.AllDirectories)
                .Select(File.ReadAllText)
                .ToArray();
            Assert.DoesNotContain(
                sources,
                source => source.Contains(
                    ".Placements.",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(
                sources,
                source => source.Contains(
                    "RuntimePlacementProjectionChannel",
                    StringComparison.Ordinal));
            Assert.Contains(
                sources,
                source => source.Contains(
                    "RuntimePlacementProjectionSubscription",
                    StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ProductionAppBorrowsTheRuntimePhysicsWorld()
    {
        string root = FindRepositoryRoot();
        string appRoot = Path.Combine(root, "src", "AcDream.App");
        string gameWindow = File.ReadAllText(Path.Combine(
            appRoot,
            "Rendering",
            "GameWindow.cs"));
        string app = string.Join(
            "\n",
            Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        Assert.Empty(Regex.Matches(
            app,
            @"new\s+(?:AcDream\.Core\.Physics\.)?PhysicsEngine\s*(?:\(|\{)"));
        Assert.DoesNotContain(
            "PhysicsDataCache.CreateProduction()",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "_runtimeEntityObjects.Physics.Engine",
            gameWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "_runtimeEntityObjects.Physics.DataCache",
            gameWindow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GraphicalAndHeadlessHostsBorrowOneRuntimeCollisionAuthority()
    {
        string root = FindRepositoryRoot();
        string appRoot = Path.Combine(root, "src", "AcDream.App");
        string headlessRoot = Path.Combine(root, "src", "AcDream.Headless");
        string gameWindow = File.ReadAllText(Path.Combine(
            appRoot,
            "Rendering",
            "GameWindow.cs"));
        string headlessProjection = File.ReadAllText(Path.Combine(
            headlessRoot,
            "Hosting",
            "HeadlessSessionWorldProjection.cs"));
        string productionHosts = string.Join(
            "\n",
            Directory.EnumerateFiles(
                    appRoot,
                    "*.cs",
                    SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(
                    headlessRoot,
                    "*.cs",
                    SearchOption.AllDirectories))
                .Select(File.ReadAllText));

        Assert.Contains(
            "private RuntimeEntityObjectLifetime _runtimeEntityObjects =>",
            gameWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "_runtime.EntityObjects;",
            gameWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "_runtimeEntityObjects.Physics",
            gameWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "private readonly GameRuntime _runtime;",
            headlessProjection,
            StringComparison.Ordinal);
        Assert.Contains(
            "_runtime.EntityObjects.Physics",
            headlessProjection,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "new RuntimeCollisionReportingState",
            productionHosts,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "class RuntimeCollisionReportingState",
            productionHosts,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HandleSetPositionCollisions(",
            productionHosts,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CollidingWithEnvironment",
            productionHosts,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppPhysicsFilesArePresentationAndPreparedAssetAdapters()
    {
        string root = FindRepositoryRoot();
        string appRoot = Path.Combine(root, "src", "AcDream.App");
        string runtimeRoot = Path.Combine(root, "src", "AcDream.Runtime");
        string remoteAdapter = File.ReadAllText(Path.Combine(
            appRoot,
            "Physics",
            "RemotePhysicsUpdater.cs"));
        string ordinaryAdapter = File.ReadAllText(Path.Combine(
            appRoot,
            "Physics",
            "LiveEntityOrdinaryPhysicsUpdater.cs"));
        string liveRuntime = File.ReadAllText(Path.Combine(
            appRoot,
            "World",
            "LiveEntityRuntime.cs"));
        string collisionPublisher = File.ReadAllText(Path.Combine(
            appRoot,
            "Streaming",
            "LandblockPhysicsPublisher.cs"));
        string runtimeRemote = File.ReadAllText(Path.Combine(
            runtimeRoot,
            "Physics",
            "RuntimeRemotePhysicsUpdater.cs"));
        string runtimeOrdinary = File.ReadAllText(Path.Combine(
            runtimeRoot,
            "Physics",
            "RuntimeOrdinaryPhysicsUpdater.cs"));
        string runtimePhysics = File.ReadAllText(Path.Combine(
            runtimeRoot,
            "Physics",
            "RuntimePhysicsState.cs"));
        string app = string.Join(
            "\n",
            Directory.EnumerateFiles(
                    appRoot,
                    "*.cs",
                    SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        Assert.DoesNotContain(
            "ResolveWithTransition(",
            remoteAdapter,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RetailObjectManagerTail.Run(",
            remoteAdapter,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolveWithTransition(",
            runtimeRemote,
            StringComparison.Ordinal);
        Assert.Contains(
            "RetailObjectManagerTail.Run(",
            runtimeRemote,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ResolveWithTransition(",
            ordinaryAdapter,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RebucketLiveEntity(",
            ordinaryAdapter,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolveWithTransition(",
            runtimeOrdinary,
            StringComparison.Ordinal);

        Assert.Contains(
            "_physics.GetOrCreatePhysicsBody(",
            liveRuntime,
            StringComparison.Ordinal);
        Assert.Contains(
            "_physics.TryGetPhysicsHost(",
            liveRuntime,
            StringComparison.Ordinal);
        Assert.Contains(
            "BeginCollisionAdmission(",
            collisionPublisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "StageCollisionAssets(",
            collisionPublisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "CommitCollisionGeneration(",
            collisionPublisher,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".AddLandblock(",
            collisionPublisher,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".RemoveLandblock(",
            collisionPublisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "public PhysicsBody GetOrCreatePhysicsBody(",
            runtimePhysics,
            StringComparison.Ordinal);
        Assert.Contains(
            "public bool TryGetPhysicsHost(",
            runtimePhysics,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetOrCreateRemoteMotion(",
            runtimePhysics,
            StringComparison.Ordinal);
        Assert.Contains(
            "_physics.TryCommitAuthoritativeVector(",
            liveRuntime,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TryActivateOrdinaryObject(",
            liveRuntime,
            StringComparison.Ordinal);
        Assert.Contains(
            "_entityObjects.CompleteProjectionRetirement(",
            liveRuntime,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "record.RemoteMotionRuntime =",
            liveRuntime,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "record.PhysicsBody =",
            liveRuntime,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new RemoteMotion(",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LiveEntityRemoteMotionRuntimeView",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ILiveEntityRemoteMotionRuntime",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ILiveEntityRemotePlacementRuntime",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LocalPlayerControllerSlot",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ILocalPlayerControllerSource",
            app,
            StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            appRoot,
            "Physics",
            "RemotePhysicsBodyInitializer.cs")));
        Assert.False(typeof(AcDream.App.World.LiveEntityRecord)
            .GetProperty(nameof(AcDream.App.World.LiveEntityRecord.PhysicsBody))!
            .CanWrite);
        Assert.False(typeof(AcDream.App.World.LiveEntityRecord)
            .GetProperty(nameof(AcDream.App.World.LiveEntityRecord.RemoteMotionRuntime))!
            .CanWrite);
        Assert.False(typeof(AcDream.App.World.LiveEntityRecord)
            .GetProperty(nameof(AcDream.App.World.LiveEntityRecord.PhysicsHost))!
            .CanWrite);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AcDream.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("AcDream.slnx was not found.");
    }
}
