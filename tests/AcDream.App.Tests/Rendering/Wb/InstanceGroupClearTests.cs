using System.Numerics;
using System.Reflection;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Meshing;
using Xunit;

namespace AcDream.App.Tests.Rendering.Wb;

public class InstanceGroupClearTests
{
    [Fact]
    public void ClassificationCache_PreservesImmutableSelectionPartDescriptors()
    {
        var cache = new EntityClassificationCache();
        Matrix4x4 restPose =
            Matrix4x4.CreateRotationZ(0.75f) *
            Matrix4x4.CreateTranslation(1f, 2f, 3f);
        CachedSelectionPart[] parts =
        [
            new CachedSelectionPart(0x0001_0002, 0x01001234u, restPose),
        ];
        GroupKey key = MakeKey(0xAA);

        cache.Populate(
            entityId: 100,
            landblockHint: 0xA9B40000u,
            batches: [new CachedBatch(key, Slot(0xAA), Matrix4x4.Identity)],
            selectionParts: parts);

        Assert.True(cache.TryGet(100, 0xA9B40000u, out EntityCacheEntry? entry));
        CachedSelectionPart part = Assert.Single(entry!.SelectionParts);
        Assert.Equal(parts[0], part);

        Matrix4x4 entityWorld = Matrix4x4.CreateTranslation(20f, 30f, 40f);
        Assert.Equal(restPose * entityWorld, part.RestPose * entityWorld);
    }

    [Fact]
    public void InstanceLightSet_PacksAndCopiesAllRetailSlotsInOrder()
    {
        int[] source = [3, 7, 11, 15, 19, 23, 27, 31];
        var packed = WbDrawDispatcher.InstanceLightSet.From(source);
        int[] destination = Enumerable.Repeat(-99, 12).ToArray();

        packed.CopyTo(destination, 2);

        Assert.Equal(source, destination[2..10]);
        Assert.Equal(-99, destination[1]);
        Assert.Equal(-99, destination[10]);
    }

    [Fact]
    public void PartitionInstanceGroups_DeferredAlphaIsNotStagedInImmediateBuffers()
    {
        var opaqueGroup = MakeGroup(TranslucencyKind.Opaque, 2, new Vector3(3f, 4f, 0f));
        var alphaGroup = MakeGroup(TranslucencyKind.AlphaBlend, 3, new Vector3(0f, 0f, 10f));
        var additiveGroup = MakeGroup(TranslucencyKind.Additive, 1, new Vector3(1f, 0f, 0f));
        var emptyGroup = new WbDrawDispatcher.InstanceGroup
        {
            Translucency = TranslucencyKind.Opaque,
        };
        var opaque = new List<WbDrawDispatcher.InstanceGroup>();
        var transparent = new List<WbDrawDispatcher.InstanceGroup>();

        var deferred = WbDrawDispatcher.PartitionInstanceGroups(
            [opaqueGroup, alphaGroup, additiveGroup, emptyGroup],
            deferTransparent: true,
            cameraWorldPosition: Vector3.Zero,
            opaque,
            transparent);

        Assert.Equal(6, deferred.VisibleInstances);
        Assert.Equal(2, deferred.ImmediateInstances);
        Assert.Equal([opaqueGroup], opaque);
        Assert.Equal([alphaGroup, additiveGroup], transparent);
        Assert.Equal(25f, opaqueGroup.SortDistance);
        Assert.Equal(100f, alphaGroup.SortDistance);

        var immediate = WbDrawDispatcher.PartitionInstanceGroups(
            [opaqueGroup, alphaGroup, additiveGroup],
            deferTransparent: false,
            cameraWorldPosition: Vector3.Zero,
            opaque,
            transparent);

        Assert.Equal(6, immediate.VisibleInstances);
        Assert.Equal(6, immediate.ImmediateInstances);
    }

    [Fact]
    public void DispatcherFingerprint_EqualDistanceAlphaUsesOriginalSubmissionOrder()
    {
        WbDrawDispatcher.InstanceGroup first = MakeCompleteGroup(
            textureSlot: 0xAA,
            submissionOrder: 0);
        WbDrawDispatcher.InstanceGroup second = MakeCompleteGroup(
            textureSlot: 0xBB,
            submissionOrder: 1);
        var scratch = new List<WbDrawDispatcher.AlphaFingerprint>();

        var forward = WbDrawDispatcher.CreateDispatcherSubmission(
            visibleInstanceCount: 2,
            immediateInstanceCount: 0,
            deferTransparent: true,
            opaque: [],
            transparent: [first, second],
            alphaScratch: scratch);
        var reversedMaterialGroups =
            WbDrawDispatcher.CreateDispatcherSubmission(
                visibleInstanceCount: 2,
                immediateInstanceCount: 0,
                deferTransparent: true,
                opaque: [],
                transparent: [second, first],
                alphaScratch: scratch);

        Assert.Equal(
            forward.TransparentDigest,
            reversedMaterialGroups.TransparentDigest);

        first.SubmissionOrders[0] = 1;
        second.SubmissionOrders[0] = 0;
        var reversedSubmission = WbDrawDispatcher.CreateDispatcherSubmission(
            visibleInstanceCount: 2,
            immediateInstanceCount: 0,
            deferTransparent: true,
            opaque: [],
            transparent: [first, second],
            alphaScratch: scratch);

        Assert.NotEqual(
            forward.TransparentDigest,
            reversedSubmission.TransparentDigest);
        Assert.Equal(
            forward.TransparentSetDigest,
            reversedSubmission.TransparentSetDigest);
    }

    [Fact]
    public void DispatcherFingerprint_ZeroVisibleInstancesIgnoresStaleGroupStorage()
    {
        WbDrawDispatcher.InstanceGroup stale = MakeCompleteGroup(
            textureSlot: 0xAA,
            submissionOrder: 0);
        var scratch = new List<WbDrawDispatcher.AlphaFingerprint>();

        CurrentRenderDispatcherSubmission expected =
            WbDrawDispatcher.CreateDispatcherSubmission(
                visibleInstanceCount: 0,
                immediateInstanceCount: 0,
                deferTransparent: false,
                opaque: [],
                transparent: [],
                alphaScratch: scratch);
        CurrentRenderDispatcherSubmission actual =
            WbDrawDispatcher.CreateDispatcherSubmission(
                visibleInstanceCount: 0,
                immediateInstanceCount: 0,
                deferTransparent: false,
                opaque: [stale],
                transparent: [stale],
                alphaScratch: scratch);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DispatcherFingerprint_IncludesRetailDetailCategory()
    {
        WbDrawDispatcher.InstanceGroup ordinary = MakeCompleteGroup(
            textureSlot: 0xAA,
            submissionOrder: 0);
        WbDrawDispatcher.InstanceGroup building = MakeCompleteGroup(
            textureSlot: 0xAA,
            submissionOrder: 0);
        building.DetailCategories[0] = 1u;
        var scratch = new List<WbDrawDispatcher.AlphaFingerprint>();

        CurrentRenderDispatcherSubmission ordinarySubmission =
            WbDrawDispatcher.CreateDispatcherSubmission(
                visibleInstanceCount: 1,
                immediateInstanceCount: 0,
                deferTransparent: true,
                opaque: [],
                transparent: [ordinary],
                alphaScratch: scratch);
        CurrentRenderDispatcherSubmission buildingSubmission =
            WbDrawDispatcher.CreateDispatcherSubmission(
                visibleInstanceCount: 1,
                immediateInstanceCount: 0,
                deferTransparent: true,
                opaque: [],
                transparent: [building],
                alphaScratch: scratch);

        Assert.NotEqual(
            ordinarySubmission.TransparentDigest,
            buildingSubmission.TransparentDigest);
        Assert.NotEqual(
            ordinarySubmission.TransparentSetDigest,
            buildingSubmission.TransparentSetDigest);
    }

    [Fact]
    public void ClassicGroupedShapes_DeleteDeadSortCenterSidecarsButRetainWalkCyptKey()
    {
        Assert.Null(typeof(CachedBatch).GetProperty("LocalSortCenter"));
        Assert.Null(typeof(WbDrawDispatcher.InstanceGroup).GetField("LocalSortCenters"));

        Type walkBatch = typeof(WbDrawDispatcher).GetNestedType(
            "WalkClassifiedBatch",
            BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WalkClassifiedBatch was not found.");
        Assert.Equal(typeof(Vector3), walkBatch.GetProperty("LocalSortCenter")?.PropertyType);
        Assert.Equal(typeof(float), walkBatch.GetProperty("SortDistanceSq")?.PropertyType);
    }

    [Fact]
    public void AlphaSubmissionShapes_DeleteDeadCameraChainButRetainOpaqueCameraDistance()
    {
        Type dispatcher = typeof(WbDrawDispatcher);
        MethodInfo defer = dispatcher.GetMethod(
            "DeferTransparentGroups",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DeferTransparentGroups was not found.");
        MethodInfo transparentDigest = dispatcher.GetMethod(
            "BuildTransparentSubmissionDigest",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildTransparentSubmissionDigest was not found.");
        MethodInfo createSubmission = dispatcher.GetMethod(
            "CreateDispatcherSubmission",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CreateDispatcherSubmission was not found.");
        MethodInfo observeCurrent = dispatcher.GetMethod(
            "ObserveCurrentDispatcherSubmission",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ObserveCurrentDispatcherSubmission was not found.");
        MethodInfo observeClassified = dispatcher.GetMethod(
            "ObserveClassifiedDispatcherSubmission",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ObserveClassifiedDispatcherSubmission was not found.");

        Assert.Equal([typeof(Matrix4x4)], defer.GetParameters().Select(static p => p.ParameterType));
        Assert.DoesNotContain(transparentDigest.GetParameters(), static p => p.ParameterType == typeof(Vector3));
        Assert.DoesNotContain(createSubmission.GetParameters(), static p => p.ParameterType == typeof(Vector3));
        Assert.DoesNotContain(observeCurrent.GetParameters(), static p => p.ParameterType == typeof(Vector3));
        Assert.DoesNotContain(observeClassified.GetParameters(), static p => p.ParameterType == typeof(Vector3));

        MethodInfo partition = dispatcher.GetMethod(
            "PartitionInstanceGroups",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PartitionInstanceGroups was not found.");
        Assert.Contains(
            partition.GetParameters(),
            static p => p.Name == "cameraWorldPosition" && p.ParameterType == typeof(Vector3));
        Assert.Equal(
            typeof(float),
            typeof(WbDrawDispatcher.InstanceGroup).GetField("SortDistance")?.FieldType);
    }

    [Fact]
    public void CachedGroupHandle_RequiresLiveMatchingRegistration()
    {
        var group = new WbDrawDispatcher.InstanceGroup { Registration = 17 };
        var key = new GroupKey(
            FirstIndex: 0,
            BaseVertex: 0,
            IndexCount: 6,
            TextureSlot: Slot(0xAA),
            TextureLayer: 0,
            Translucency: TranslucencyKind.Opaque,
            MaterialState: RetailSetSurfaceMaterialState.Opaque,
            FoliageFlags: 0u);
        var cached = new CachedBatch(
            key,
            Slot(0xAA),
            Matrix4x4.Identity,
            Group: group,
            GroupRegistration: 17);

        Assert.True(WbDrawDispatcher.TryResolveCachedGroup(cached, out var resolved));
        Assert.Same(group, resolved);
        group.Registration = 0;
        Assert.False(WbDrawDispatcher.TryResolveCachedGroup(cached, out _));
        Assert.False(WbDrawDispatcher.TryResolveCachedGroup(
            cached with { Group = null },
            out _));
    }

    [Fact]
    public void CreateGroupFromKey_CopiesFoliageFlagsFromTheKey()
    {
        var key = new GroupKey(
            FirstIndex: 10,
            BaseVertex: 2,
            IndexCount: 18,
            TextureSlot: Slot(0x77),
            TextureLayer: 0,
            Translucency: TranslucencyKind.ClipMap,
            MaterialState: RetailSetSurfaceMaterialState.Opaque,
            FoliageFlags: 0x2u,
            CullMode: DatReaderWriter.Enums.CullMode.CounterClockwise);

        WbDrawDispatcher.InstanceGroup group =
            WbDrawDispatcher.CreateGroupFromKey(key, registration: 5, frame: 9);

        Assert.Equal(0x2u, group.FoliageFlags);
        Assert.Equal(5, group.Registration);
        Assert.Equal(9, group.LastUsedFrame);
        Assert.Equal(key.FirstIndex, group.FirstIndex);
        Assert.Equal(key.BaseVertex, group.BaseVertex);
        Assert.Equal(key.IndexCount, group.IndexCount);
        Assert.Equal(key.TextureSlot, group.TextureSlot);
        Assert.Equal(key.TextureLayer, group.TextureLayer);
        Assert.Equal(key.Translucency, group.Translucency);
        Assert.Equal(key.CullMode, group.CullMode);
    }

    [Fact]
    public void FramePrune_RetiresOnlyGroupsAbsentForWholePreviousFrame()
    {
        GroupKey liveKey = MakeKey(0xAA);
        GroupKey retiredKey = MakeKey(0xBB);
        var live = new WbDrawDispatcher.InstanceGroup
        {
            Registration = 11,
            LastUsedFrame = 9,
        };
        live.Matrices.Add(Matrix4x4.Identity);
        var retired = new WbDrawDispatcher.InstanceGroup
        {
            Registration = 12,
            LastUsedFrame = 8,
        };
        retired.Matrices.Capacity = 64;
        var groups = new Dictionary<GroupKey, WbDrawDispatcher.InstanceGroup>
        {
            [liveKey] = live,
            [retiredKey] = retired,
        };
        var retiredKeys = new List<GroupKey>();
        var staleCache = new CachedBatch(
            retiredKey,
            retiredKey.TextureSlot,
            Matrix4x4.Identity,
            Group: retired,
            GroupRegistration: retired.Registration);

        int retiredCount = WbDrawDispatcher.PruneInstanceGroupsUnusedBeforeFrame(
            groups,
            retiredKeys,
            oldestLiveFrame: 9);

        Assert.Equal(1, retiredCount);
        Assert.Single(groups);
        Assert.Same(live, groups[liveKey]);
        Assert.Single(live.Matrices);
        Assert.Equal(11, live.Registration);
        Assert.Equal(0, retired.Registration);
        Assert.Equal(0, retired.Matrices.Capacity);
        Assert.False(WbDrawDispatcher.TryResolveCachedGroup(staleCache, out _));
        Assert.Empty(retiredKeys);
    }

    [Fact]
    public void FramePrune_KeepsGroupsUsedByDifferentDrawScopesInSameFrame()
    {
        GroupKey landscapeKey = MakeKey(0xAA);
        GroupKey paperdollKey = MakeKey(0xBB);
        var landscape = new WbDrawDispatcher.InstanceGroup
        {
            Registration = 21,
            LastUsedFrame = 14,
        };
        var paperdoll = new WbDrawDispatcher.InstanceGroup
        {
            Registration = 22,
            LastUsedFrame = 14,
        };
        var groups = new Dictionary<GroupKey, WbDrawDispatcher.InstanceGroup>
        {
            [landscapeKey] = landscape,
            [paperdollKey] = paperdoll,
        };

        int retiredCount = WbDrawDispatcher.PruneInstanceGroupsUnusedBeforeFrame(
            groups,
            [],
            oldestLiveFrame: 14);

        Assert.Equal(0, retiredCount);
        Assert.Equal(2, groups.Count);
        Assert.Equal(21, landscape.Registration);
        Assert.Equal(22, paperdoll.Registration);
    }

    private static AcDream.App.Rendering.Gpu.GpuTextureSlot Slot(uint index) => new(index);
    private static GroupKey MakeKey(uint textureSlot) => new(
        FirstIndex: 0,
        BaseVertex: 0,
        IndexCount: 6,
        TextureSlot: Slot(textureSlot),
        TextureLayer: 0,
        Translucency: TranslucencyKind.Opaque,
        MaterialState: RetailSetSurfaceMaterialState.Opaque,
        FoliageFlags: 0u);

    private static WbDrawDispatcher.InstanceGroup MakeGroup(
        TranslucencyKind translucency,
        int instanceCount,
        Vector3 firstPosition)
    {
        var group = new WbDrawDispatcher.InstanceGroup { Translucency = translucency };
        for (int i = 0; i < instanceCount; i++)
            group.Matrices.Add(Matrix4x4.CreateTranslation(firstPosition + new Vector3(i, 0f, 0f)));
        return group;
    }

    private static WbDrawDispatcher.InstanceGroup MakeCompleteGroup(
        uint textureSlot,
        int submissionOrder)
    {
        var group = new WbDrawDispatcher.InstanceGroup
        {
            FirstIndex = 0,
            BaseVertex = 0,
            IndexCount = 6,
            TextureSlot = Slot(textureSlot),
            TextureLayer = 0,
            Translucency = TranslucencyKind.AlphaBlend,
        };
        group.Matrices.Add(Matrix4x4.CreateTranslation(10f, 0f, 0f));
        group.SubmissionOrders.Add(submissionOrder);
        group.Slots.Add(0u);
        group.LightSets.Add(WbDrawDispatcher.InstanceLightSet.Disabled);
        group.IndoorFlags.Add(0u);
        group.DetailCategories.Add(0u);
        group.Opacities.Add(1f);
        group.SelectionLighting.Add(new Vector2(0f, 1f));
        return group;
    }

    [Fact]
    public void ClearPerInstanceData_ClearsEveryParallelPerInstanceList()
    {
        var grp = new WbDrawDispatcher.InstanceGroup();
        grp.Matrices.Add(Matrix4x4.Identity);
        grp.SubmissionOrders.Add(0);
        grp.Slots.Add(1u);
        grp.LightSets.Add(WbDrawDispatcher.InstanceLightSet.Disabled);
        grp.IndoorFlags.Add(0u);
        grp.DetailCategories.Add(1u);
        grp.Opacities.Add(1.0f);
        grp.SelectionLighting.Add(new Vector2(0f, 1f));

        grp.ClearPerInstanceData();

        Assert.Empty(grp.Matrices);
        Assert.Empty(grp.SubmissionOrders);
        Assert.Empty(grp.Slots);
        Assert.Empty(grp.LightSets);
        Assert.Empty(grp.IndoorFlags);
        Assert.Empty(grp.DetailCategories);
        Assert.Empty(grp.Opacities);
        Assert.Empty(grp.SelectionLighting);
    }
}
