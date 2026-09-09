using System.Reflection;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Wb;
using AcDream.App.Runtime;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Items;
using AcDream.Runtime;
using AcDream.Runtime.Entities;

namespace AcDream.App.Tests.World;

public sealed class RuntimeEntityOwnershipTests
{
    [Fact]
    public void LiveEntityRuntime_BorrowsCanonicalRuntimeDirectory()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        var runtime = new LiveEntityRuntime(
            new GpuWorldState(),
            new DelegateLiveEntityResourceLifecycle(
                static _ => { },
                static _ => { }),
            lifetime);
        FieldInfo owner = typeof(LiveEntityRuntime).GetField(
            "_entityObjects",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing Runtime lifetime root.");
        FieldInfo directory = typeof(LiveEntityRuntime).GetField(
            "_directory",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing canonical entity directory.");
        FieldInfo sidecars = typeof(LiveEntityRuntime).GetField(
            "_projections",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing App projection sidecar store.");

        Assert.Equal(typeof(RuntimeEntityDirectory), directory.FieldType);
        Assert.Same(lifetime, owner.GetValue(runtime));
        Assert.Same(lifetime.Entities, directory.GetValue(runtime));
        Assert.Equal(typeof(LiveEntityProjectionStore), sidecars.FieldType);
    }

    [Fact]
    public void ProductionComposition_HasOneRuntimeEntityObjectConstructionRoot()
    {
        string root = FindRepositoryRoot();
        string appRoot = Path.Combine(root, "src", "AcDream.App");
        string runtimeRoot = Path.Combine(root, "src", "AcDream.Runtime");
        string[] appSources = Directory.GetFiles(
            appRoot,
            "*.cs",
            SearchOption.AllDirectories);
        string[] runtimeSources = Directory.GetFiles(
            runtimeRoot,
            "*.cs",
            SearchOption.AllDirectories);

        string[] appObjectAllocators = appSources
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Studio{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains(
                "new ClientObjectTable",
                StringComparison.Ordinal))
            .ToArray();
        string[] appDirectoryAllocators = appSources
            .Where(path => File.ReadAllText(path).Contains(
                "new RuntimeEntityDirectory",
                StringComparison.Ordinal))
            .ToArray();
        string[] runtimeObjectAllocators = runtimeSources
            .Where(path => File.ReadAllText(path).Contains(
                "new ClientObjectTable",
                StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)!)
            .ToArray();
        string[] runtimeDirectoryAllocators = runtimeSources
            .Where(path => File.ReadAllText(path).Contains(
                "new RuntimeEntityDirectory",
                StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)!)
            .ToArray();

        Assert.Empty(appObjectAllocators);
        Assert.Empty(appDirectoryAllocators);
        Assert.Equal(
            ["RuntimeEntityObjectLifetime.cs"],
            runtimeObjectAllocators);
        Assert.Equal(
            ["RuntimeEntityObjectLifetime.cs"],
            runtimeDirectoryAllocators);
    }

    [Fact]
    public void AppProjectionOwners_UseExactRuntimeKeysAndNoGuidDictionary()
    {
        FieldInfo[] runtimeFields = typeof(LiveEntityRuntime).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.DoesNotContain(runtimeFields, IsGuidDictionary);

        FieldInfo[] storeFields = typeof(LiveEntityProjectionStore).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo[] exactStores = storeFields
            .Where(field => IsDictionaryWithKey(field, typeof(RuntimeEntityKey)))
            .ToArray();
        Assert.NotEmpty(exactStores);
        Assert.All(
            exactStores,
            field => Assert.Equal(
                typeof(LiveEntityRecord),
                field.FieldType.GetGenericArguments()[1]));
        Assert.DoesNotContain(storeFields, IsGuidDictionary);
    }

    [Fact]
    public void MaterializedPresentationWorksets_AreExactKeyed()
    {
        AssertExactKeyFields(
            typeof(LiveEntityPresentationController),
            "_readyOwners",
            "_suspendedShadowOwners");
        AssertExactKeyFields(typeof(LiveRenderProjectionJournal), "_byKey");
        AssertExactKeyFields(
            typeof(EntityEffectController),
            "_liveProfiles",
            "_readyLiveOwners");
        AssertExactKeyFields(
            typeof(LiveEntityLightController),
            "_trackedOwners",
            "_presentOwners");
        AssertExactKeyFields(
            typeof(EquippedChildRenderController),
            "_attachedByChild",
            "_pendingUnparentByChild",
            "_pendingOrdinaryRemovalByRoot",
            "_pendingDetachedRemovalByChild",
            "_pendingReparentRemovalByChild",
            "_pendingPoseLossRemovalByChild",
            "_pendingOrphanRemovalByChild");
        AssertExactKeyFields(typeof(LiveEntityAnimationScheduler), "_schedules");
        AssertExactKeyFields(typeof(EntitySpawnAdapter), "_ownersByKey");
        AssertExactKeyFields(
            typeof(LiveEntityLivenessTracker),
            "_deadlines",
            "_present");
        AssertExactKeyFields(
            typeof(RemoteMovementObservationTracker),
            "_lastMove");
        AssertExactKeyFields(
            typeof(GpuWorldState),
            "_liveProjectionByKey",
            "_visibleLiveProjectionCounts",
            "_visibilityBeforeMutation");
    }

    [Fact]
    public void AppAssembly_HasNoGuidKeyedLiveEntityRecordDictionary()
    {
        FieldInfo[] forbidden = typeof(LiveEntityRuntime).Assembly
            .GetTypes()
            .SelectMany(type => type.GetFields(
                BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.NonPublic))
            .Where(field =>
                IsDictionaryWithKey(field, typeof(uint))
                && field.FieldType.GetGenericArguments()[1]
                    == typeof(LiveEntityRecord))
            .ToArray();

        Assert.Empty(forbidden);
    }

    [Fact]
    public void ExactOwners_HaveNoRetainedGuidIndex()
    {
        Type[] exactOwnerTypes =
        [
            typeof(LiveEntityProjectionStore),
            typeof(LiveEntityPresentationController),
            typeof(LiveRenderProjectionJournal),
            typeof(LiveEntityLightController),
            typeof(EquippedChildRenderController),
            typeof(LiveEntityAnimationScheduler),
            typeof(EntitySpawnAdapter),
            typeof(LiveEntityLivenessTracker),
            typeof(RemoteMovementObservationTracker),
        ];

        FieldInfo[] forbidden = exactOwnerTypes
            .SelectMany(type => type.GetFields(
                BindingFlags.Instance
                | BindingFlags.NonPublic))
            .Where(field =>
                field.Name.Contains("Guid", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(forbidden);
    }

    [Fact]
    public void CanonicalRuntimeRecord_HasNoProjectionOrBackendSurface()
    {
        string[] forbiddenPropertyNames =
        [
            "WorldEntity",
            "AnimationRuntime",
            "RemoteMotionRuntime",
            "ProjectileRuntime",
            "EffectProfile",
            "ResourcesRegistered",
            "IsSpatiallyProjected",
            "IsSpatiallyVisible",
        ];
        PropertyInfo[] properties = typeof(RuntimeEntityRecord).GetProperties(
            BindingFlags.Instance | BindingFlags.Public);

        Assert.DoesNotContain(
            properties,
            property => forbiddenPropertyNames.Contains(
                property.Name,
                StringComparer.Ordinal));
        Assert.DoesNotContain(
            properties,
            property => IsPresentationType(property.PropertyType));
    }

    [Fact]
    public void CurrentRuntimeAdapters_DoNotRetainEntityOrInventoryMirrors()
    {
        FieldInfo[] adapterFields = typeof(CurrentGameRuntimeAdapter)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Contains(
            adapterFields,
            field => field.FieldType == typeof(GameRuntime));
        Assert.DoesNotContain(
            adapterFields,
            field => field.FieldType == typeof(LiveEntityRuntime)
                || field.FieldType == typeof(ClientObjectTable)
                || field.FieldType == typeof(IRuntimeEntityView)
                || field.FieldType == typeof(IRuntimeInventoryView)
                || field.FieldType == typeof(RuntimeEntityObjectLifetime));

        string root = FindRepositoryRoot();
        string liveSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "AcDream.App",
            "World",
            "LiveEntityRuntime.cs"));
        Assert.DoesNotContain("_entityObjects.PublishEntity", liveSource);
        Assert.DoesNotContain("_directory.TryApply", liveSource);
        Assert.DoesNotContain("_directory.RemoveActive", liveSource);

        string appRuntimeRoot = Path.Combine(
            root,
            "src",
            "AcDream.App",
            "Runtime");
        Assert.False(File.Exists(Path.Combine(
            appRuntimeRoot,
            "CurrentGameRuntimeViewAdapter.cs")));
        Assert.False(File.Exists(Path.Combine(
            appRuntimeRoot,
            "CurrentGameRuntimeEventAdapter.cs")));

        string rootSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "AcDream.Runtime",
            "GameRuntime.cs"));
        Assert.Contains(
            "public RuntimeEntityObjectLifetime EntityObjects",
            rootSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "public IRuntimeEntityView Entities => EntityObjects.EntityView;",
            rootSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "public IRuntimeInventoryView Inventory => EntityObjects.InventoryView;",
            rootSource,
            StringComparison.Ordinal);
    }

    private static bool IsPresentationType(Type type)
    {
        string? ns = type.Namespace;
        return ns?.StartsWith("AcDream.App", StringComparison.Ordinal) == true
            || ns?.StartsWith("AcDream.UI", StringComparison.Ordinal) == true
            || ns?.StartsWith("Silk.NET", StringComparison.Ordinal) == true;
    }

    private static bool IsGuidDictionary(FieldInfo field) =>
        IsDictionaryWithKey(field, typeof(uint));

    private static void AssertExactKeyFields(
        Type owner,
        params string[] fieldNames)
    {
        foreach (string fieldName in fieldNames)
        {
            FieldInfo field = owner.GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    $"{owner.Name}.{fieldName} is missing.");

            Assert.True(
                IsCollectionWithKey(field, typeof(RuntimeEntityKey)),
                $"{owner.Name}.{fieldName} must be keyed by RuntimeEntityKey, "
                + $"but was {field.FieldType}.");
        }
    }

    private static bool IsCollectionWithKey(FieldInfo field, Type keyType)
    {
        if (!field.FieldType.IsGenericType)
            return false;

        Type generic = field.FieldType.GetGenericTypeDefinition();
        return (generic == typeof(Dictionary<,>)
                || generic == typeof(HashSet<>))
            && field.FieldType.GetGenericArguments()[0] == keyType;
    }

    private static bool IsDictionaryWithKey(FieldInfo field, Type keyType) =>
        field.FieldType.IsGenericType
        && field.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)
        && field.FieldType.GetGenericArguments()[0] == keyType;

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find AcDream.slnx above {AppContext.BaseDirectory}.");
    }
}
