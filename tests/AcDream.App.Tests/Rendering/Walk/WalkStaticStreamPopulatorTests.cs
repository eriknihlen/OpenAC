using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Selection;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Walk;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Content;
using AcDream.Core.Meshing;
using AcDream.Core.World;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib.IO;
using Microsoft.Extensions.Logging.Abstractions;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed class WalkStaticStreamPopulatorTests
{
    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class RecordingSelectionSink : IRetailSelectionRenderSink
    {
        public readonly List<(uint ServerGuid, uint LocalEntityId, int PartIndex, uint GfxObjId, Matrix4x4 LocalToWorld)>
            Calls = new();

        public void AddVisiblePart(
            uint serverGuid, uint localEntityId, int partIndex, uint gfxObjId, Matrix4x4 partWorld) =>
            Calls.Add((serverGuid, localEntityId, partIndex, gfxObjId, partWorld));
    }

    private sealed class FixedWalkViews(params uint[] slots) : IWalkLookInViewSource
    {
        public IReadOnlyList<uint> LookInCellTurns { get; } = [0x8C040112u];

        public bool SphereVisibleInLookInTurn(
            int routeIndex,
            in Vector3 center,
            float radius,
            bool testSphere = true) => slots.Length != 0;
    }


    private static RenderProjectionRecord MakeRecord(
        uint localEntityId,
        uint serverGuid,
        Vector3 position,
        IReadOnlyList<MeshRef> meshRefs,
        bool isBuildingShell = false,
        uint parentCellId = 0u,
        RenderCasterIdentityKind casterIdentity =
            RenderCasterIdentityKind.Unclassified) =>
        new(
            Id: RenderProjectionId.FromRaw(localEntityId),
            ProjectionClass: RenderProjectionClass.OutdoorStatic,
            OwnerIncarnation: RenderOwnerIncarnation.FromRaw(1),
            Transform: new RenderTransform(Matrix4x4.CreateTranslation(position)),
            PreviousTransform: default,
            MeshSet: default,
            Material: default,
            Residency: default,
            Bounds: default,
            Flags: RenderProjectionFlags.Draw,
            DegradeState: default,
            SortKey: new RenderSortKey(0),
            DirtyMask: default,
            Source: new RenderSourceMetadata(
                LocalEntityId: localEntityId,
                ServerGuid: serverGuid,
                SourceId: 0,
                ParentCellId: parentCellId,
                EffectCellId: 0,
                BuildingShellAnchorCellId: 0,
                TransformFingerprint: default,
                GeometryFingerprint: default,
                AppearanceFingerprint: default),
            EntityPayload: new RenderEntityPayload(
                MeshRefs: meshRefs,
                PaletteOverride: null,
                IsBuildingShell: isBuildingShell,
                CasterIdentity: casterIdentity));

    private static ObjectRenderBatch MakeBatch(
        uint surfaceId,
        TranslucencyKind translucency,
        uint firstIndex,
        int baseVertex,
        int indexCount,
        uint textureSlotIndex,
        uint textureLayer = 0,
        CullMode cullMode = CullMode.CounterClockwise,
        RetailSetSurfaceMaterialState? materialState = null) =>
        new()
        {
            Key = new TextureKey { SurfaceId = surfaceId, IsSolid = false },
            Translucency = translucency,
            MaterialState = materialState ?? RetailSetSurfaceMaterialState.Opaque,
            FirstIndex = firstIndex,
            BaseVertex = (uint)baseVertex,
            IndexCount = indexCount,
            TextureSlot = new GpuTextureSlot(textureSlotIndex),
            TextureIndex = (int)textureLayer,
        };

    private static ObjectRenderData MakeFlatMesh(params ObjectRenderBatch[] batches) =>
        new() { Batches = new List<ObjectRenderBatch>(batches) };

    private static ObjectRenderData MakeSortedMesh(
        Vector3 sortCenter,
        params ObjectRenderBatch[] batches) =>
        new() { SortCenter = sortCenter, Batches = new List<ObjectRenderBatch>(batches) };


    private static void InjectRenderData(ObjectMeshManager manager, ulong id, ObjectRenderData data)
    {
        FieldInfo field = typeof(ObjectMeshManager).GetField(
            "_renderData", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "ObjectMeshManager._renderData field not found — test relies on this exact name.");
        var dict = (ConcurrentDictionary<ulong, ObjectRenderData>)field.GetValue(manager)!;
        dict[id] = data;
    }

    [Fact]
    public void PopulateCellObjects_UsesAuthoredSortCenterAndStableFarToNearOrder()
    {
        using var fx = new DispatcherFixture();
        const ulong nearOrigin = 0x0100_0C01UL;
        const ulong farAuthoredCenter = 0x0100_0C02UL;
        InjectRenderData(fx.Manager, nearOrigin, MakeSortedMesh(
            Vector3.Zero,
            MakeBatch(101, TranslucencyKind.AlphaBlend, 101, 0, 3, 1)));
        InjectRenderData(fx.Manager, farAuthoredCenter, MakeSortedMesh(
            new Vector3(10, 0, 0),
            MakeBatch(202, TranslucencyKind.AlphaBlend, 202, 0, 3, 2)));

        RenderProjectionRecord[] records =
        [
            MakeRecord(1, 0, new Vector3(5, 0, 0), [new MeshRef((uint)nearOrigin, Matrix4x4.Identity)]),
            MakeRecord(2, 0, Vector3.Zero, [new MeshRef((uint)farAuthoredCenter, Matrix4x4.Identity)]),
        ];
        var alpha = new List<WbDrawDispatcher.WalkClassifiedBatch>();
        var stream = new OrderedDrawStream();
        var populator = new WalkStaticStreamPopulator(fx.Dispatcher);

        populator.PopulateCellObjects(
            stream,
            WalkDrawStage.CellStatic,
            0x8C040112u,
            records,
            0x8C04u,
            Vector3.Zero,
            Matrix4x4.Identity,
            views: null,
            viewRouteIndex: -1,
            alpha);

        Assert.Empty(stream.Keys);
        Assert.Equal([202u, 101u], alpha.Select(static batch => batch.Key.FirstIndex));
        Assert.Equal([100f, 25f], alpha.Select(static batch => batch.SortDistanceSq));
    }

    [Fact]
    public void PopulateCellObjects_EqualCyptRetainsEntityPartAndSubsetOrder()
    {
        using var fx = new DispatcherFixture();
        const ulong setup = 0x1000_0C10UL;
        const ulong firstPart = 0x0100_0C11UL;
        const ulong secondPart = 0x0100_0C12UL;
        InjectRenderData(fx.Manager, firstPart, MakeSortedMesh(
            Vector3.Zero,
            MakeBatch(11, TranslucencyKind.AlphaBlend, 11, 0, 3, 1),
            MakeBatch(12, TranslucencyKind.AlphaBlend, 12, 0, 3, 2)));
        InjectRenderData(fx.Manager, secondPart, MakeSortedMesh(
            Vector3.Zero,
            MakeBatch(21, TranslucencyKind.AlphaBlend, 21, 0, 3, 3),
            MakeBatch(22, TranslucencyKind.AlphaBlend, 22, 0, 3, 4)));
        InjectRenderData(fx.Manager, setup, new ObjectRenderData
        {
            IsSetup = true,
            SetupParts =
            [
                (firstPart, Matrix4x4.CreateTranslation(10, 0, 0)),
                (secondPart, Matrix4x4.CreateTranslation(0, 10, 0)),
            ],
        });
        RenderProjectionRecord record = MakeRecord(
            10, 0, Vector3.Zero, [new MeshRef((uint)setup, Matrix4x4.Identity)]);
        var alpha = new List<WbDrawDispatcher.WalkClassifiedBatch>();

        new WalkStaticStreamPopulator(fx.Dispatcher).PopulateCellObjects(
            new OrderedDrawStream(),
            WalkDrawStage.CellStatic,
            0x8C040112u,
            [record],
            0x8C04u,
            Vector3.Zero,
            Matrix4x4.Identity,
            views: null,
            viewRouteIndex: -1,
            alpha);

        Assert.Equal([11u, 12u, 21u, 22u], alpha.Select(static batch => batch.Key.FirstIndex));
        Assert.All(alpha, static batch => Assert.Equal(100f, batch.SortDistanceSq));
    }

    [Fact]
    public void PopulateCellObjects_InterleavesStaticAndDynamicOpaquePartsByCypt()
    {
        using var fx = new DispatcherFixture();
        const ulong nearStatic = 0x0100_0C21UL;
        const ulong farDynamic = 0x0100_0C22UL;
        const ulong middleStatic = 0x0100_0C23UL;
        InjectRenderData(fx.Manager, nearStatic, MakeFlatMesh(
            MakeBatch(1, TranslucencyKind.Opaque, 1, 0, 3, 1)));
        InjectRenderData(fx.Manager, farDynamic, MakeFlatMesh(
            MakeBatch(2, TranslucencyKind.Opaque, 2, 0, 3, 2)));
        InjectRenderData(fx.Manager, middleStatic, MakeFlatMesh(
            MakeBatch(3, TranslucencyKind.Opaque, 3, 0, 3, 3)));
        RenderProjectionRecord dynamic = MakeRecord(
            2, 0, new Vector3(30, 0, 0), [new MeshRef((uint)farDynamic, Matrix4x4.Identity)])
            with { ProjectionClass = RenderProjectionClass.LiveDynamicRoot };
        RenderProjectionRecord[] records =
        [
            MakeRecord(1, 0, new Vector3(10, 0, 0), [new MeshRef((uint)nearStatic, Matrix4x4.Identity)]),
            dynamic,
            MakeRecord(3, 0, new Vector3(20, 0, 0), [new MeshRef((uint)middleStatic, Matrix4x4.Identity)]),
        ];
        var stream = new OrderedDrawStream();

        new WalkStaticStreamPopulator(fx.Dispatcher).PopulateCellObjects(
            stream,
            WalkDrawStage.CellStatic,
            0x8C040112u,
            records,
            0x8C04u,
            Vector3.Zero,
            Matrix4x4.Identity,
            views: null,
            viewRouteIndex: -1,
            []);

        Assert.Equal([2u, 3u, 1u], stream.Keys.Select(static key => key.FirstIndex));
        Assert.Equal(
            [WalkDrawStage.Dynamic, WalkDrawStage.CellStatic, WalkDrawStage.CellStatic],
            stream.Stages);
    }

    [Fact]
    public void WorldAlphaCyptContract_RetainsTheKeyWithoutDeadCameraSubmitThreading()
    {
        Type batchType = typeof(WbDrawDispatcher).GetNestedType(
            "WalkClassifiedBatch", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WalkClassifiedBatch was not found.");
        PropertyInfo sortDistance = batchType.GetProperty("SortDistanceSq")
            ?? throw new InvalidOperationException("SortDistanceSq was not retained.");
        Assert.Equal(typeof(float), sortDistance.PropertyType);

        MethodInfo submit = typeof(WbDrawDispatcher).GetMethod(
            "SubmitWalkAlphaInstance", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SubmitWalkAlphaInstance was not found.");
        ParameterInfo[] parameters = submit.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal("batch", parameters[0].Name);
        Assert.Equal(typeof(Matrix4x4), parameters[1].ParameterType);
        Assert.DoesNotContain(parameters, static parameter => parameter.ParameterType == typeof(Vector3));

        string root = FindRepoRoot();
        string populator = File.ReadAllText(Path.Combine(
            root, "src", "AcDream.App", "Rendering", "Walk", "WalkStaticStreamPopulator.cs"));
        Assert.Contains("batch.LocalSortCenter, batch.Transform", populator);
        Assert.Contains("SortDistanceSq = Vector3.DistanceSquared", populator);
        Assert.Contains("value.Batch.SortDistanceSq >", populator);
    }

    [Fact]
    public void ClassicGroupedAlphaSourceTruth_DeletesDeadOrderingChainAndPreservesLiveOwners()
    {
        string root = FindRepoRoot();
        string dispatcher = File.ReadAllText(Path.Combine(
            root, "src", "AcDream.App", "Rendering", "Wb", "WbDrawDispatcher.cs"));
        string cachedBatch = File.ReadAllText(Path.Combine(
            root, "src", "AcDream.App", "Rendering", "Wb", "CachedBatch.cs"));
        string walkClassifier = File.ReadAllText(Path.Combine(
            root, "src", "AcDream.App", "Rendering", "Wb", "WbDrawDispatcher.WalkClassify.cs"));
        string alphaProduction = dispatcher + cachedBatch + walkClassifier;

        Assert.Contains(
            "Vector3.DistanceSquared(cameraWorldPosition, groupPosition)",
            dispatcher,
            StringComparison.Ordinal);
        Assert.Contains("group.SortDistance =", dispatcher, StringComparison.Ordinal);
        Assert.Contains("Vector3 LocalSortCenter", walkClassifier, StringComparison.Ordinal);
        Assert.Contains("float SortDistanceSq", walkClassifier, StringComparison.Ordinal);
        Assert.DoesNotContain("RetailAlphaOrdering", alphaProduction, StringComparison.Ordinal);
        Assert.DoesNotContain("FlushFartherThan(", alphaProduction, StringComparison.Ordinal);
        Assert.DoesNotContain("viewerDistance", alphaProduction, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = cameraWorldPosition", dispatcher, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "AcDream.slnx")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate AcDream.slnx.");
    }


    [Fact]
    public void ClassifyEntityForWalk_OneOpaqueAndOneTranslucentPart_YieldsBatchesInRecordOrderWithCorrectIsOpaque()
    {
        using var fx = new DispatcherFixture();
        const ulong opaqueGfxObj = 0x0100_0001UL;
        const ulong alphaGfxObj = 0x0100_0002UL;
        InjectRenderData(fx.Manager, opaqueGfxObj, MakeFlatMesh(
            MakeBatch(0x08000001u, TranslucencyKind.Opaque, firstIndex: 0, baseVertex: 0, indexCount: 3, textureSlotIndex: 1)));
        InjectRenderData(fx.Manager, alphaGfxObj, MakeFlatMesh(
            MakeBatch(0x08000002u, TranslucencyKind.AlphaBlend, firstIndex: 3, baseVertex: 4,
                indexCount: 6, textureSlotIndex: 2,
                materialState: RetailSetSurfaceMaterialState.Resolve(
                    SurfaceType.Alpha | SurfaceType.Additive,
                    texturePresent: true,
                    textureHasPalette: false))));

        var meshRefs = new[]
        {
            new MeshRef((uint)opaqueGfxObj, Matrix4x4.CreateTranslation(1, 0, 0)),
            new MeshRef((uint)alphaGfxObj, Matrix4x4.CreateTranslation(0, 1, 0)),
        };
        RenderProjectionRecord record = MakeRecord(
            localEntityId: 100, serverGuid: 0, position: new Vector3(5, 6, 7), meshRefs);

        var batches = new List<WbDrawDispatcher.WalkClassifiedBatch>();
        var selectionParts = new List<WbDrawDispatcher.WalkClassifiedSelectionPart>();
        fx.Dispatcher.ClassifyEntityForWalk(in record, tupleLandblockId: 0x8C04u, batches, selectionParts);

        Assert.Equal(2, batches.Count);

        WbDrawDispatcher.WalkClassifiedBatch opaque = batches[0];
        Assert.True(opaque.IsOpaque);
        Assert.Equal(TranslucencyKind.Opaque, opaque.Key.Translucency);
        Assert.Equal(0u, opaque.Key.FirstIndex);
        Assert.Equal(3, opaque.Key.IndexCount);
        Assert.Equal(1u, opaque.Key.TextureSlot.Index);
        Assert.Equal(1f, opaque.Alpha);
        Assert.Equal(meshRefs[0].PartTransform * record.Transform.LocalToWorld, opaque.Transform);

        WbDrawDispatcher.WalkClassifiedBatch translucent = batches[1];
        Assert.False(translucent.IsOpaque);
        Assert.Equal(TranslucencyKind.AlphaBlend, translucent.Key.Translucency);
        Assert.Equal(3u, translucent.Key.FirstIndex);
        Assert.Equal(6, translucent.Key.IndexCount);
        Assert.Equal(2u, translucent.Key.TextureSlot.Index);
        Assert.Equal(RetailSetSurfaceBlend.AlphaAdditive, translucent.Key.MaterialState.Blend);
        Assert.Equal(meshRefs[1].PartTransform * record.Transform.LocalToWorld, translucent.Transform);

        Assert.Equal(2, selectionParts.Count);
        Assert.Equal(100u, selectionParts[0].LocalEntityId);
        Assert.Equal(0, selectionParts[0].PartIndex);
        Assert.Equal((uint)opaqueGfxObj, selectionParts[0].GfxObjId);
        Assert.Equal(opaque.Transform, selectionParts[0].LocalToWorld);
        Assert.Equal(1, selectionParts[1].PartIndex);
        Assert.Equal((uint)alphaGfxObj, selectionParts[1].GfxObjId);
    }

    [Fact]
    public void ClassifyEntityForWalk_NoDrawProjectileRecord_SubmitsNeitherMeshNorSelection()
    {
        using var fx = new DispatcherFixture();
        const ulong projectileGfxObj = 0x0100_0003UL;
        InjectRenderData(fx.Manager, projectileGfxObj, MakeFlatMesh(
            MakeBatch(
                0x08000003u,
                TranslucencyKind.Opaque,
                firstIndex: 0,
                baseVertex: 0,
                indexCount: 3,
                textureSlotIndex: 1)));

        RenderProjectionRecord projectile = MakeRecord(
            localEntityId: 101,
            serverGuid: 0x7000_0101u,
            position: Vector3.Zero,
            meshRefs: [new MeshRef((uint)projectileGfxObj, Matrix4x4.Identity)])
            with
            {
                Flags = RenderProjectionFlags.SpatiallyResident
                    | RenderProjectionFlags.Selectable
                    | RenderProjectionFlags.Hidden,
            };

        var batches = new List<WbDrawDispatcher.WalkClassifiedBatch>();
        var selectionParts =
            new List<WbDrawDispatcher.WalkClassifiedSelectionPart>();

        fx.Dispatcher.ClassifyEntityForWalk(
            in projectile,
            tupleLandblockId: 0x8C04u,
            batches,
            selectionParts,
            liveDynamic: true);

        Assert.Empty(batches);
        Assert.Empty(selectionParts);
    }

    [Fact]
    public void ClassifyEntityForWalk_SetupComposite_EncodesPartAndSetupPartIndexLikePackedRoute()
    {
        using var fx = new DispatcherFixture();
        const ulong setupGfxObj = 0x1000_0010UL;
        const ulong trunkGfxObj = 0x0100_0011UL;
        const ulong leavesGfxObj = 0x0100_0012UL;

        InjectRenderData(fx.Manager, trunkGfxObj, MakeFlatMesh(
            MakeBatch(0x08000011u, TranslucencyKind.Opaque, 0, 0, 3, 1)));
        InjectRenderData(fx.Manager, leavesGfxObj, MakeFlatMesh(
            MakeBatch(0x08000012u, TranslucencyKind.ClipMap, 3, 4, 6, 2)));
        InjectRenderData(fx.Manager, setupGfxObj, new ObjectRenderData
        {
            IsSetup = true,
            SetupParts = new List<(ulong GfxObjId, Matrix4x4 Transform)>
            {
                (trunkGfxObj, Matrix4x4.CreateTranslation(0, 0, 1)),
                (leavesGfxObj, Matrix4x4.CreateTranslation(0, 0, 2)),
            },
        });

        var meshRefs = new[] { new MeshRef((uint)setupGfxObj, Matrix4x4.Identity) };
        RenderProjectionRecord record = MakeRecord(200, 0, Vector3.Zero, meshRefs);

        var batches = new List<WbDrawDispatcher.WalkClassifiedBatch>();
        var selectionParts = new List<WbDrawDispatcher.WalkClassifiedSelectionPart>();
        fx.Dispatcher.ClassifyEntityForWalk(in record, 0x8C04u, batches, selectionParts);

        Assert.Equal(2, batches.Count);
        Assert.Equal(2, selectionParts.Count);
        // partIndex=0 (the entity's single top-level MeshRef) << 16 | setupPartIndex.
        Assert.Equal(0, selectionParts[0].PartIndex);
        Assert.Equal(1, selectionParts[1].PartIndex);
        Assert.Equal((uint)trunkGfxObj, selectionParts[0].GfxObjId);
        Assert.Equal((uint)leavesGfxObj, selectionParts[1].GfxObjId);
    }

    [Fact]
    public void SelectedBuildingShellUsesExactlySelectedGfxAndPrecomputedPartZeroTransform()
    {
        using var fx = new DispatcherFixture();
        const uint baseGfx = 0x0100_0031u;
        const uint selectedGfx = 0x0100_0032u;
        InjectRenderData(fx.Manager, baseGfx, MakeFlatMesh(
            MakeBatch(0x08000031u, TranslucencyKind.Opaque, 0, 0, 3, 1)));
        InjectRenderData(fx.Manager, selectedGfx, MakeFlatMesh(
            MakeBatch(0x08000032u, TranslucencyKind.Opaque, 3, 4, 6, 2)));
        Matrix4x4 root = Matrix4x4.CreateTranslation(8, 9, 10);
        Matrix4x4 partZero = Matrix4x4.CreateScale(2f)
            * Matrix4x4.CreateTranslation(1, 2, 3);
        var surfaceOverrides = new Dictionary<uint, uint>
        {
            [0x08000032u] = 0x05000032u,
        };
        RenderProjectionRecord record = MakeRecord(
            231, 0x7000_0231u, new Vector3(8, 9, 10),
            [new MeshRef(baseGfx, Matrix4x4.Identity)
            {
                SurfaceOverrides = surfaceOverrides,
            }],
            isBuildingShell: true);
        var batches = new List<WbDrawDispatcher.WalkClassifiedBatch>();
        var selections = new List<WbDrawDispatcher.WalkClassifiedSelectionPart>();
        var selected = new WalkBuildingSelection(selectedGfx, null, 2, 1u);

        fx.Dispatcher.ClassifyEntityForWalk(
            in record, 0x8C04u, batches, selections,
            buildingSelection: selected,
            buildingPartTransform: partZero);

        WbDrawDispatcher.WalkClassifiedBatch batch = Assert.Single(batches);
        Assert.Equal(3u, batch.Key.FirstIndex);
        Assert.True(batch.Key.TextureSlot.IsAssigned);
        Assert.NotEqual(new GpuTextureSlot(2), batch.Key.TextureSlot);
        Assert.Equal(partZero * root, batch.Transform);
        var selection = Assert.Single(selections);
        Assert.Equal(selectedGfx, selection.GfxObjId);
        Assert.Equal(partZero * root, selection.LocalToWorld);
    }

    [Fact]
    public void SelectedBuildingShellMissDoesNotFallBackToResidentBaseGfx()
    {
        using var fx = new DispatcherFixture();
        const uint baseGfx = 0x0100_0041u;
        const uint missingSelectedGfx = 0x0100_0042u;
        InjectRenderData(fx.Manager, baseGfx, MakeFlatMesh(
            MakeBatch(0x08000041u, TranslucencyKind.Opaque, 0, 0, 3, 1)));
        RenderProjectionRecord record = MakeRecord(
            241, 0, Vector3.Zero,
            [new MeshRef(baseGfx, Matrix4x4.Identity)], isBuildingShell: true);
        var batches = new List<WbDrawDispatcher.WalkClassifiedBatch>();
        var selections = new List<WbDrawDispatcher.WalkClassifiedSelectionPart>();
        var selected = new WalkBuildingSelection(missingSelectedGfx, null, 1, 1u);

        fx.Dispatcher.ClassifyEntityForWalk(
            in record, 0x8C04u, batches, selections,
            buildingSelection: selected,
            buildingPartTransform: Matrix4x4.Identity);

        Assert.Empty(batches);
        Assert.Empty(selections);

        InjectRenderData(fx.Manager, missingSelectedGfx, MakeFlatMesh(
            MakeBatch(0x08000042u, TranslucencyKind.Opaque, 3, 4, 6, 2)));
        fx.Dispatcher.ClassifyEntityForWalk(
            in record, 0x8C04u, batches, selections,
            buildingSelection: selected,
            buildingPartTransform: Matrix4x4.Identity);
        Assert.Single(batches);
        Assert.Equal(missingSelectedGfx, Assert.Single(selections).GfxObjId);
    }

    [Fact]
    public void WalkFrameBoundary_DeduplicatesMissWithinFrameAndRearmsNextAcceptedFrame()
    {
        const uint missingGfx = 0x01000049u;
        var source = new RecordingPreparedAssetSource();
        using var fx = new DispatcherFixture(preparedAssets: source);
        RenderProjectionRecord record = MakeRecord(
            249, 0, Vector3.Zero,
            [new MeshRef(0x01000048u, Matrix4x4.Identity)],
            isBuildingShell: true);
        var selected = new WalkBuildingSelection(missingGfx, null, 1, 1u);
        var batches = new List<WbDrawDispatcher.WalkClassifiedBatch>();
        var selections = new List<WbDrawDispatcher.WalkClassifiedSelectionPart>();
        FieldInfo requestedField = typeof(WbDrawDispatcher).GetField(
            "_missRequested", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var requested = (HashSet<ulong>)requestedField.GetValue(fx.Dispatcher)!;

        fx.Dispatcher.BeginWalkPartFrame();
        fx.Dispatcher.ClassifyEntityForWalk(
            in record, 0x8C04u, batches, selections,
            buildingSelection: selected,
            buildingPartTransform: Matrix4x4.Identity);
        fx.Dispatcher.ClassifyEntityForWalk(
            in record, 0x8C04u, batches, selections,
            buildingSelection: selected,
            buildingPartTransform: Matrix4x4.Identity);
        Assert.Equal([missingGfx], requested);
        Assert.Throws<InvalidOperationException>(() => fx.Dispatcher.BeginWalkPartFrame());
        Assert.Equal([missingGfx], requested);
        Assert.True(SpinWait.SpinUntil(() => source.ReadCount >= 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, source.ReadCount);

        fx.Manager.CancelStagedUploads([missingGfx]);
        fx.Dispatcher.EndWalkPartFrame();
        fx.Dispatcher.BeginWalkPartFrame();
        Assert.Empty(requested);
        fx.Dispatcher.ClassifyEntityForWalk(
            in record, 0x8C04u, batches, selections,
            buildingSelection: selected,
            buildingPartTransform: Matrix4x4.Identity);
        Assert.True(SpinWait.SpinUntil(() => source.ReadCount >= 2, TimeSpan.FromSeconds(5)));
        Assert.Equal([missingGfx], requested);
        fx.Dispatcher.EndWalkPartFrame();

        for (int frame = 0; frame < 32; frame++)
        {
            fx.Manager.CancelStagedUploads([missingGfx]);
            fx.Dispatcher.BeginWalkPartFrame();
            Assert.Empty(requested);
            fx.Dispatcher.ClassifyEntityForWalk(
                in record, 0x8C04u, batches, selections,
                buildingSelection: selected,
                buildingPartTransform: Matrix4x4.Identity);
            Assert.Single(requested);
            fx.Dispatcher.EndWalkPartFrame();
        }
        Assert.Single(requested);
    }

    [Fact]
    public void OwnedWalkLadderDependency_PreparesPublishesAndClassifiesWithoutInjection()
    {
        const uint selectedGfx = 0x0100004Au;
        var prepared = PreparedTriangle(selectedGfx, 0x0800004Au);
        var source = new RecordingPreparedAssetSource(prepared);
        using var fx = new DispatcherFixture(preparedAssets: source);
        var ownership = new LandblockSpawnAdapter(fx.Adapter);
        var building = new WalkBuilding
        {
            PositionCellId = 0x8C040001u,
            DegradeLevels =
            [
                new WalkBuildingDegradeLevel(selectedGfx, 1u, 0f, 10f, 20f, null),
            ],
        };
        var envCells = new EnvCellLandblockBuild(
            0x8C04FFFFu,
            Array.Empty<LoadedCell>(),
            Array.Empty<EnvCellShellPlacement>(),
            [new WalkBuildingFactory.Entry(building, Matrix4x4.Identity, Matrix4x4.Identity)]);
        var landblock = new LoadedLandblock(
            0x8C04FFFFu, new LandBlock(), Array.Empty<WorldEntity>());

        ownership.OnLandblockLoaded(
            landblock,
            additionalOrdinaryIds: envCells.WalkBuildingMeshDependencies);

        Assert.True(fx.Manager.IsOwned(selectedGfx));
        Assert.True(SpinWait.SpinUntil(() =>
        {
            fx.Adapter.Tick();
            return fx.Adapter.TryGetRenderData(selectedGfx) is not null;
        }, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, source.ReadCount);

        RenderProjectionRecord record = MakeRecord(
            250, 0, Vector3.Zero,
            [new MeshRef(0x0100004Bu, Matrix4x4.Identity)],
            isBuildingShell: true);
        var batches = new List<WbDrawDispatcher.WalkClassifiedBatch>();
        var selections = new List<WbDrawDispatcher.WalkClassifiedSelectionPart>();
        fx.Dispatcher.BeginWalkPartFrame();
        fx.Dispatcher.ClassifyEntityForWalk(
            in record, 0x8C04u, batches, selections,
            buildingSelection: building.Select(0f, 1f, 1f),
            buildingPartTransform: Matrix4x4.Identity);
        fx.Dispatcher.EndWalkPartFrame();

        Assert.Single(batches);
        Assert.Equal(selectedGfx, Assert.Single(selections).GfxObjId);
        ownership.OnLandblockUnloaded(landblock.LandblockId);
        Assert.False(fx.Manager.IsOwned(selectedGfx));
    }

    [Fact]
    public void WarmedBuildingSelectionAndClassificationAllocateZeroAndDoNotMutateRetainedRecord()
    {
        using var fx = new DispatcherFixture();
        const uint baseGfx = 0x0100_0051u;
        const uint selectedGfx = 0x0100_0052u;
        InjectRenderData(fx.Manager, selectedGfx, MakeFlatMesh(
            MakeBatch(0x08000052u, TranslucencyKind.Opaque, 3, 4, 6, 2)));
        RenderProjectionRecord record = MakeRecord(
            251, 0x7000_0251u, new Vector3(8, 9, 10),
            [new MeshRef(baseGfx, Matrix4x4.Identity)],
            isBuildingShell: true,
            parentCellId: 0x8C040112u,
            casterIdentity: RenderCasterIdentityKind.Building) with
        {
            Source = new RenderSourceMetadata(
                LocalEntityId: 251,
                ServerGuid: 0x7000_0251u,
                SourceId: 0x02000051u,
                ParentCellId: 0x8C040112u,
                EffectCellId: 0x8C040113u,
                BuildingShellAnchorCellId: 0x8C040001u,
                TransformFingerprint: new RenderSceneHash128(11, 21),
                GeometryFingerprint: new RenderSceneHash128(12, 22),
                AppearanceFingerprint: new RenderSceneHash128(13, 23)),
        };
        RenderProjectionRecord original = record;
        var building = new WalkBuilding
        {
            DegradeLevels =
            [
                new(selectedGfx, 4u, 0f, 10f, 20f, null),
                new(0u, 5u, 20f, 30f, 40f, null),
            ],
        };
        var batches = new List<WbDrawDispatcher.WalkClassifiedBatch>(1);
        var selections = new List<WbDrawDispatcher.WalkClassifiedSelectionPart>(1);

        void Classify()
        {
            batches.Clear();
            selections.Clear();
            WalkBuildingSelection selected = building.Select(0f, 50f, 0f);
            fx.Dispatcher.ClassifyEntityForWalk(
                in record, 0x8C04u, batches, selections,
                buildingSelection: selected,
                buildingPartTransform: Matrix4x4.Identity);
        }

        Classify();
        long allocated = ZeroAllocationProbe.MeasureWarmed(
            Classify,
            batchSize: 10_000,
            warmupBatches: 2,
            samples: 5);

        Assert.Equal(0, allocated);
        Assert.Equal(original, record);
        Assert.Equal(selectedGfx, Assert.Single(selections).GfxObjId);
        WbDrawDispatcher.WalkClassifiedBatch batch = Assert.Single(batches);
        Assert.Equal(1u, batch.DetailCategory);
        Assert.Equal(Matrix4x4.CreateTranslation(8, 9, 10), batch.Transform);
    }

    [Fact]
    public void ClassifyEntityForWalk_FrameScopeStampsEachAdmittedSetupPartOncePerRetailPass()
    {
        using var fx = new DispatcherFixture();
        const ulong setupGfxObj = 0x1000_0020UL;
        const ulong headGfxObj = 0x0100_0021UL;
        const ulong torsoGfxObj = 0x0100_0022UL;

        InjectRenderData(fx.Manager, headGfxObj, MakeFlatMesh(
            MakeBatch(0x08000021u, TranslucencyKind.Opaque, 0, 0, 3, 1)));
        InjectRenderData(fx.Manager, torsoGfxObj, MakeFlatMesh(
            MakeBatch(0x08000022u, TranslucencyKind.Opaque, 3, 4, 6, 2)));
        InjectRenderData(fx.Manager, setupGfxObj, new ObjectRenderData
        {
            IsSetup = true,
            SetupParts = new List<(ulong GfxObjId, Matrix4x4 Transform)>
            {
                (headGfxObj, Matrix4x4.CreateTranslation(0, 0, 2)),
                (torsoGfxObj, Matrix4x4.CreateTranslation(0, 0, 1)),
            },
        });

        RenderProjectionRecord record = MakeRecord(
            220,
            0,
            Vector3.Zero,
            [new MeshRef((uint)setupGfxObj, Matrix4x4.Identity)]);
        var batches = new List<WbDrawDispatcher.WalkClassifiedBatch>();
        var selectionParts = new List<WbDrawDispatcher.WalkClassifiedSelectionPart>();

        fx.Dispatcher.BeginWalkPartFrame();
        try
        {
            fx.Dispatcher.ClassifyEntityForWalk(
                in record, 0x8C04u, batches, selectionParts);
            Assert.Equal(2, batches.Count);
            Assert.Equal(2, selectionParts.Count);

            batches.Clear();
            selectionParts.Clear();
            fx.Dispatcher.ClassifyEntityForWalk(
                in record, 0x8C04u, batches, selectionParts);
            Assert.Empty(batches);
            Assert.Empty(selectionParts);

            fx.Dispatcher.AdvanceWalkPartPassStamp();
            fx.Dispatcher.ClassifyEntityForWalk(
                in record, 0x8C04u, batches, selectionParts);
            Assert.Equal(2, batches.Count);
            Assert.Equal(2, selectionParts.Count);
        }
        finally
        {
            fx.Dispatcher.EndWalkPartFrame();
        }

        batches.Clear();
        selectionParts.Clear();
        fx.Dispatcher.ClassifyEntityForWalk(
            in record, 0x8C04u, batches, selectionParts);
        Assert.Equal(2, batches.Count);
        Assert.Equal(2, selectionParts.Count);
    }

    [Fact]
    public void ClassifyEntityForWalk_FrameScopeDoesNotStampPortalRejectedPart()
    {
        using var fx = new DispatcherFixture();
        const ulong gfxObj = 0x0100_0023UL;
        InjectRenderData(fx.Manager, gfxObj, MakeFlatMesh(
            MakeBatch(0x08000023u, TranslucencyKind.Opaque, 0, 0, 3, 1)));
        RenderProjectionRecord record = MakeRecord(
            221,
            0,
            Vector3.Zero,
            [new MeshRef((uint)gfxObj, Matrix4x4.Identity)]);
        var batches = new List<WbDrawDispatcher.WalkClassifiedBatch>();
        var selectionParts = new List<WbDrawDispatcher.WalkClassifiedSelectionPart>();

        fx.Dispatcher.BeginWalkPartFrame();
        try
        {
            fx.Dispatcher.ClassifyEntityForWalk(
                in record,
                0x8C04u,
                batches,
                selectionParts,
                liveDynamic: true,
                lookInViews: new FixedWalkViews(),
                lookInRouteIndex: 0);
            Assert.Empty(batches);
            Assert.Empty(selectionParts);

            fx.Dispatcher.ClassifyEntityForWalk(
                in record,
                0x8C04u,
                batches,
                selectionParts,
                liveDynamic: true,
                lookInViews: new FixedWalkViews(7u),
                lookInRouteIndex: 1);
            Assert.Single(batches);
            Assert.Single(selectionParts);
        }
        finally
        {
            fx.Dispatcher.EndWalkPartFrame();
        }
    }

    [Fact]
    public void ClassifyEntityForWalk_LocalPlayerBypassesDrawnPartStampLikeRetail()
    {
        using var fx = new DispatcherFixture();
        const ulong gfxObj = 0x0100_0024UL;
        InjectRenderData(fx.Manager, gfxObj, MakeFlatMesh(
            MakeBatch(0x08000024u, TranslucencyKind.Opaque, 0, 0, 3, 1)));
        RenderProjectionRecord player = MakeRecord(
            222,
            0x5000_0001u,
            Vector3.Zero,
            [new MeshRef((uint)gfxObj, Matrix4x4.Identity)],
            casterIdentity: RenderCasterIdentityKind.LocalPlayer);
        var batches = new List<WbDrawDispatcher.WalkClassifiedBatch>();
        var selectionParts = new List<WbDrawDispatcher.WalkClassifiedSelectionPart>();

        fx.Dispatcher.BeginWalkPartFrame();
        try
        {
            fx.Dispatcher.ClassifyEntityForWalk(
                in player, 0xF418u, batches, selectionParts,
                liveDynamic: true);
            Assert.Single(batches);
            Assert.Single(selectionParts);

            batches.Clear();
            selectionParts.Clear();
            fx.Dispatcher.ClassifyEntityForWalk(
                in player, 0xF418u, batches, selectionParts,
                liveDynamic: true);
            Assert.Single(batches);
            Assert.Single(selectionParts);
        }
        finally
        {
            fx.Dispatcher.EndWalkPartFrame();
        }
    }

    [Fact]
    public void ClassifyEntityForWalk_PortalViewsAdmitOneCompleteUnclippedMesh()
    {
        using var fx = new DispatcherFixture();
        const ulong gfxObj = 0x0100_0013UL;
        InjectRenderData(fx.Manager, gfxObj, MakeFlatMesh(
            MakeBatch(0x08000013u, TranslucencyKind.Opaque, 0, 0, 3, 1)));
        RenderProjectionRecord record = MakeRecord(
            201, 0, Vector3.Zero,
            [new MeshRef((uint)gfxObj, Matrix4x4.Identity)],
            parentCellId: 0x8C040112u);
        var batches = new List<WbDrawDispatcher.WalkClassifiedBatch>();
        var selectionParts = new List<WbDrawDispatcher.WalkClassifiedSelectionPart>();
        var views = new FixedWalkViews(7u, 9u);

        fx.Dispatcher.ClassifyEntityForWalk(
            in record,
            0x8C04u,
            batches,
            selectionParts,
            liveDynamic: true,
            views,
            lookInRouteIndex: 0,
            lookInCellId: 0x8C040112u);

        WbDrawDispatcher.WalkClassifiedBatch batch = Assert.Single(batches);
        Assert.Equal(0u, batch.ClipSlot);
        Assert.Single(selectionParts);
    }

    // ── Deliverable 2: WalkStaticStreamPopulator routing ───────────────────

    [Fact]
    public void PopulateCell_OpaqueBatchAppendsOrderedDrawCommandInRecordOrderWithStageAndCellProvenance()
    {
        using var fx = new DispatcherFixture();
        const ulong gfxObj = 0x0100_0003UL;
        InjectRenderData(fx.Manager, gfxObj, MakeFlatMesh(
            MakeBatch(0x08000003u, TranslucencyKind.Opaque, 10, 2, 12, 5)));

        var record = MakeRecord(300, 0, new Vector3(1, 2, 3), new[] { new MeshRef((uint)gfxObj, Matrix4x4.Identity) });
        var populator = new WalkStaticStreamPopulator(fx.Dispatcher);
        var stream = new OrderedDrawStream();

        populator.PopulateCell(
            stream, WalkDrawStage.CellStatic, cellId: 0x8C040100u,
            new[] { record }, tupleLandblockId: 0x8C04u,
            cameraWorldPosition: Vector3.Zero, viewProjection: Matrix4x4.Identity);

        Assert.Equal(1, stream.Count);
        Assert.Equal(WalkDrawStage.CellStatic, stream.Stages[0]);
        Assert.Equal(0x8C040100u, stream.CellIds[0]);
        Assert.Equal(10u, stream.Keys[0].FirstIndex);
        Assert.Equal(12, stream.Keys[0].IndexCount);
        Assert.Equal(1f, stream.Alphas[0]);
        Assert.Equal(record.Transform.LocalToWorld, stream.Transforms[0]);
    }

    [Fact]
    public void PopulateOutdoorStatics_UsesTheOutdoorStaticStage()
    {
        using var fx = new DispatcherFixture();
        const ulong gfxObj = 0x0100_0004UL;
        InjectRenderData(fx.Manager, gfxObj, MakeFlatMesh(
            MakeBatch(0x08000004u, TranslucencyKind.Opaque, 0, 0, 3, 1)));

        var record = MakeRecord(400, 0, Vector3.Zero, new[] { new MeshRef((uint)gfxObj, Matrix4x4.Identity) });
        var populator = new WalkStaticStreamPopulator(fx.Dispatcher);
        var stream = new OrderedDrawStream();

        populator.PopulateOutdoorStatics(
            stream, cellId: 0x8C040000u, new[] { record }, tupleLandblockId: 0x8C04u,
            cameraWorldPosition: Vector3.Zero, viewProjection: Matrix4x4.Identity);

        Assert.Equal(1, stream.Count);
        Assert.Equal(WalkDrawStage.OutdoorStatic, stream.Stages[0]);
    }

    [Fact]
    public void PopulateCell_TranslucentBatchDoesNotAppendToTheStreamAndReachesTheAlphaQueue()
    {
        using var fx = new DispatcherFixture(withAlphaQueue: true);
        const ulong gfxObj = 0x0100_0005UL;
        InjectRenderData(fx.Manager, gfxObj, MakeFlatMesh(
            MakeBatch(0x08000005u, TranslucencyKind.AlphaBlend, 0, 0, 3, 1)));

        var record = MakeRecord(500, 0, new Vector3(0, 0, 10), new[] { new MeshRef((uint)gfxObj, Matrix4x4.Identity) });
        var populator = new WalkStaticStreamPopulator(fx.Dispatcher);
        var stream = new OrderedDrawStream();

        fx.AlphaQueue!.BeginFrame();
        populator.PopulateCell(
            stream, WalkDrawStage.CellStatic, 0x8C040100u, new[] { record }, 0x8C04u,
            cameraWorldPosition: Vector3.Zero, viewProjection: Matrix4x4.Identity);

        Assert.Equal(0, stream.Count);
        Assert.Equal(1, fx.AlphaQueue.PendingCount);
        fx.AlphaQueue.AbortFrame();
    }

    [Fact]
    public void PopulateCell_PublishesSelectionPartsForEveryClassifiedEntity()
    {
        var sink = new RecordingSelectionSink();
        using var fx = new DispatcherFixture(selectionSink: sink);
        const ulong gfxObj = 0x0100_0006UL;
        InjectRenderData(fx.Manager, gfxObj, MakeFlatMesh(
            MakeBatch(0x08000006u, TranslucencyKind.Opaque, 0, 0, 3, 1)));

        var record = MakeRecord(600, serverGuid: 0x8000_0060u, new Vector3(1, 1, 1),
            new[] { new MeshRef((uint)gfxObj, Matrix4x4.Identity) });
        var populator = new WalkStaticStreamPopulator(fx.Dispatcher);
        var stream = new OrderedDrawStream();

        populator.PopulateCell(
            stream, WalkDrawStage.CellStatic, 0x8C040100u, new[] { record }, 0x8C04u,
            Vector3.Zero, Matrix4x4.Identity);

        var call = Assert.Single(sink.Calls);
        Assert.Equal(0x8000_0060u, call.ServerGuid);
        Assert.Equal(600u, call.LocalEntityId);
        Assert.Equal(0, call.PartIndex);
        Assert.Equal((uint)gfxObj, call.GfxObjId);
        Assert.Equal(record.Transform.LocalToWorld, call.LocalToWorld);
    }


    [Fact]
    public void SubmitWalkAlphaInstance_RoutesAlphaBlendBatchIntoTheAlphaList()
    {
        using var fx = new DispatcherFixture(withAlphaQueue: true);
        fx.AlphaQueue!.BeginFrame();

        var key = new GroupKey(10, 2, 6, new GpuTextureSlot(3), 1, TranslucencyKind.AlphaBlend,
            MaterialState: RetailSetSurfaceMaterialState.Opaque, FoliageFlags: 0);
        Vector3 localSortCenter = new(1, 2, 3);
        Matrix4x4 model = Matrix4x4.CreateTranslation(4, 5, 6);
        var batch = new WbDrawDispatcher.WalkClassifiedBatch(
            key, model, ClipSlot: 7, WbDrawDispatcher.InstanceLightSet.Disabled, IndoorFlag: 1,
            Alpha: 0.5f, SelectionLighting: new Vector2(0.25f, 0.75f), DetailCategory: 1,
            IsOpaque: false, LocalSortCenter: localSortCenter);

        fx.Dispatcher.SubmitWalkAlphaInstance(in batch, Matrix4x4.Identity);

        Assert.Equal(1, fx.AlphaQueue.PendingCount);
        Assert.Equal(1, fx.AlphaQueue.AlphaCount);
        Assert.Equal(0, fx.AlphaQueue.ClipCount);

        FieldInfo alphaListField = typeof(RetailAlphaQueue).GetField(
            "_alpha", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var alphaList = (List<RetailAlphaEntry>)alphaListField.GetValue(fx.AlphaQueue)!;
        RetailAlphaEntry entry = Assert.Single(alphaList);
        Assert.Equal(0, entry.Token);

        fx.AlphaQueue.AbortFrame();
    }

    [Fact]
    public void SubmitWalkAlphaInstance_DetailOnOffAndOrdinaryUseProductionOutcomes()
    {
        using (var on = new DispatcherFixture(
            withAlphaQueue: true,
            detailAvailable: true,
            detailEnabled: true))
        using (DrawScope draw = on.BeginDraw(beginAlpha: true))
        {
            WbDrawDispatcher.WalkClassifiedBatch building = AlphaWalkBatch(detailCategory: 1u);
            on.Dispatcher.SubmitWalkAlphaInstance(
                in building,
                Matrix4x4.Identity);

            Assert.Equal(0, on.AlphaQueue!.PendingCount);
            Assert.Contains(
                on.Device.Calls.OfType<GpuRecordedPipelineBind>(),
                call => call.PipelineName.Contains("alpha", StringComparison.Ordinal)
                    && !call.PipelineName.Contains("detail", StringComparison.Ordinal));
            Assert.Single(on.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
            Assert.DoesNotContain(on.Device.Calls.OfType<GpuRecordedPipelineBind>(),
                call => call.PipelineName.Contains("detail", StringComparison.Ordinal));
            Assert.Contains(on.Device.Calls.OfType<GpuRecordedPushConstants>(),
                call => call.Constants.TextureIndexA == 99u && call.Constants.ParamA == 2f);
        }

        using (var off = new DispatcherFixture(
            withAlphaQueue: true,
            detailAvailable: true,
            detailEnabled: false))
        using (DrawScope draw = off.BeginDraw(beginAlpha: true))
        {
            WbDrawDispatcher.WalkClassifiedBatch building = AlphaWalkBatch(detailCategory: 1u);
            off.Dispatcher.SubmitWalkAlphaInstance(
                in building,
                Matrix4x4.Identity);

            Assert.Equal(1, off.AlphaQueue!.PendingCount);
            Assert.Empty(off.Device.Calls.OfType<GpuRecordedPipelineBind>());
            off.AlphaQueue.EndFrame();
            Assert.DoesNotContain(off.Device.Calls.OfType<GpuRecordedPushConstants>(),
                call => call.Constants.TextureIndexA == 99u || call.Constants.ParamA != 0f);
        }

        using (var ordinary = new DispatcherFixture(
            withAlphaQueue: true,
            detailAvailable: true,
            detailEnabled: true))
        using (DrawScope draw = ordinary.BeginDraw(beginAlpha: true))
        {
            WbDrawDispatcher.WalkClassifiedBatch batch = AlphaWalkBatch(detailCategory: 0u);
            ordinary.Dispatcher.SubmitWalkAlphaInstance(
                in batch,
                Matrix4x4.Identity);

            Assert.Equal(1, ordinary.AlphaQueue!.PendingCount);
            Assert.Empty(ordinary.Device.Calls.OfType<GpuRecordedPipelineBind>());
        }
    }

    [Fact]
    public void DeferTransparentGroups_BuildingDetailOnDrawsImmediateWithDetail()
    {
        using var fx = new DispatcherFixture(
            withAlphaQueue: true,
            detailAvailable: true,
            detailEnabled: true);
        using DrawScope draw = fx.BeginDraw(beginAlpha: true);
        WbDrawDispatcher.InstanceGroup group = AlphaInstanceGroup(detailCategory: 1u);
        FieldInfo field = typeof(WbDrawDispatcher).GetField(
            "_translucentDraws",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var groups = (List<WbDrawDispatcher.InstanceGroup>)field.GetValue(fx.Dispatcher)!;
        groups.Add(group);
        MethodInfo defer = typeof(WbDrawDispatcher).GetMethod(
            "DeferTransparentGroups",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        defer.Invoke(fx.Dispatcher, [Matrix4x4.Identity]);

        Assert.Equal(0, fx.AlphaQueue!.PendingCount);
        Assert.Single(fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
        Assert.DoesNotContain(fx.Device.Calls.OfType<GpuRecordedPipelineBind>(),
            call => call.PipelineName.Contains("detail", StringComparison.Ordinal));
        Assert.Contains(fx.Device.Calls.OfType<GpuRecordedPushConstants>(),
            call => call.Constants.TextureIndexA == 99u && call.Constants.ParamA == 2f);
    }

    [Theory]
    [InlineData(SurfaceType.Additive, true, false)]
    [InlineData(SurfaceType.InvAlpha, true, false)]
    [InlineData(SurfaceType.Additive, false, true)]
    [InlineData(SurfaceType.InvAlpha, false, true)]
    public void ClassicImmediate_NonDetailUsesAlphaBlendWhenDetailIsDisabledOrUnavailable(
        SurfaceType surfaceType,
        bool detailAvailable,
        bool detailEnabled)
    {
        using var fx = new DispatcherFixture(
            detailAvailable: detailAvailable,
            detailEnabled: detailEnabled);
        using DrawScope draw = fx.BeginDraw();

        ExecuteClassicGroups(
            fx,
            AlphaInstanceGroup(
                detailCategory: 1u,
                surfaceType: surfaceType,
                sortDistance: 10f));

        (string Pipeline, GpuPushConstants Constants, GpuRecordedMultiDrawIndirect Draw) transcript =
            Assert.Single(ClassicDrawTranscript(fx.Device));
        Assert.Equal("wb-mesh-alpha-1x", transcript.Pipeline);
        Assert.Equal(1u, transcript.Draw.DrawCount);
        Assert.Equal(0u, transcript.Constants.TextureIndexA);
        Assert.Equal(0f, transcript.Constants.ParamA);
        Assert.Equal(0f, transcript.Constants.ParamB);
    }

    [Fact]
    public void ClassicImmediate_NonDetailCrossBlendRunCoalescesIntoOnePhysicalDraw()
    {
        using var fx = new DispatcherFixture(
            detailAvailable: true,
            detailEnabled: false);
        using DrawScope draw = fx.BeginDraw();

        ExecuteClassicGroups(
            fx,
            AlphaInstanceGroup(1u, SurfaceType.Alpha, sortDistance: 30f),
            AlphaInstanceGroup(1u, SurfaceType.Additive, sortDistance: 20f),
            AlphaInstanceGroup(1u, SurfaceType.InvAlpha, sortDistance: 10f));

        (string Pipeline, GpuPushConstants Constants, GpuRecordedMultiDrawIndirect Draw) transcript =
            Assert.Single(ClassicDrawTranscript(fx.Device));
        Assert.Equal("wb-mesh-alpha-1x", transcript.Pipeline);
        Assert.Equal(3u, transcript.Draw.DrawCount);
        Assert.Equal(0f, transcript.Constants.ParamA);
    }

    [Fact]
    public void ClassicImmediate_DetailActiveUsesExactMaterialPipelineAndArm()
    {
        using var fx = new DispatcherFixture(
            detailAvailable: true,
            detailEnabled: true);
        using DrawScope draw = fx.BeginDraw();

        ExecuteClassicGroups(
            fx,
            AlphaInstanceGroup(
                1u,
                SurfaceType.Additive | SurfaceType.Base1ClipMap,
                sortDistance: 10f));

        (string Pipeline, GpuPushConstants Constants, GpuRecordedMultiDrawIndirect Draw) transcript =
            Assert.Single(ClassicDrawTranscript(fx.Device));
        Assert.Equal("wb-mesh-raw-additive-depth-write-1x", transcript.Pipeline);
        Assert.Equal(1u, transcript.Draw.DrawCount);
        Assert.Equal(99u, transcript.Constants.TextureIndexA);
        Assert.Equal(2f, transcript.Constants.ParamA);
        Assert.Equal(200f / 255f, transcript.Constants.ParamB);
        Assert.NotEqual(
            0,
            transcript.Constants.RenderPass & RetailDetailTextureContract.NoFogRenderPassFlag);
    }

    [Fact]
    public void ClassicImmediate_NonDetailDetailNonDetailKeepsBothBoundariesAndDoesNotLeak()
    {
        using var fx = new DispatcherFixture(
            detailAvailable: true,
            detailEnabled: true);
        using DrawScope draw = fx.BeginDraw();

        ExecuteClassicGroups(
            fx,
            AlphaInstanceGroup(0u, SurfaceType.Alpha, sortDistance: 30f),
            AlphaInstanceGroup(
                1u,
                SurfaceType.Additive | SurfaceType.Base1ClipMap,
                sortDistance: 20f),
            AlphaInstanceGroup(0u, SurfaceType.InvAlpha, sortDistance: 10f));

        var transcript = ClassicDrawTranscript(fx.Device);
        Assert.Equal(3, transcript.Count);
        Assert.Equal(
            ["wb-mesh-alpha-1x", "wb-mesh-raw-additive-depth-write-1x", "wb-mesh-alpha-1x"],
            transcript.Select(entry => entry.Pipeline));
        Assert.All(transcript, entry => Assert.Equal(1u, entry.Draw.DrawCount));
        Assert.Equal(0f, transcript[0].Constants.ParamA);
        Assert.Equal(2f, transcript[1].Constants.ParamA);
        Assert.Equal(99u, transcript[1].Constants.TextureIndexA);
        Assert.Equal(200f / 255f, transcript[1].Constants.ParamB);
        Assert.Equal(0f, transcript[2].Constants.ParamA);
        Assert.Equal(0u, transcript[2].Constants.TextureIndexA);
        Assert.Equal(0f, transcript[2].Constants.ParamB);
    }

    [Theory]
    [InlineData(SurfaceType.Alpha, false, "wb-mesh-alpha-1x", GpuBlendMode.StraightAlpha, 0f, true)]
    [InlineData(SurfaceType.Alpha | SurfaceType.Additive, false, "wb-mesh-additive-1x", GpuBlendMode.Additive, 0f, false)]
    [InlineData(SurfaceType.Additive, false, "wb-mesh-raw-additive-1x", GpuBlendMode.RawAdditive, 0f, false)]
    [InlineData(SurfaceType.InvAlpha, false, "wb-mesh-inverse-1x", GpuBlendMode.InverseAlpha, 0f, true)]
    [InlineData(SurfaceType.InvAlpha | SurfaceType.Additive, false, "wb-mesh-inverse-additive-1x", GpuBlendMode.InverseAdditive, 0f, false)]
    [InlineData(SurfaceType.Translucent, false, "wb-mesh-alpha-1x", GpuBlendMode.StraightAlpha, 0f, true)]
    [InlineData(SurfaceType.Translucent | SurfaceType.Additive, false, "wb-mesh-raw-additive-1x", GpuBlendMode.RawAdditive, 0f, false)]
    [InlineData(SurfaceType.Translucent | SurfaceType.InvAlpha, false, "wb-mesh-inverse-1x", GpuBlendMode.InverseAlpha, 0f, true)]
    [InlineData(SurfaceType.Alpha | SurfaceType.Base1ClipMap, false, "wb-mesh-alpha-depth-write-1x", GpuBlendMode.StraightAlpha, 200f / 255f, true)]
    [InlineData(SurfaceType.Alpha | SurfaceType.Base1ClipMap, true, "wb-mesh-alpha-depth-write-1x", GpuBlendMode.StraightAlpha, 100f / 255f, true)]
    [InlineData(SurfaceType.Alpha | SurfaceType.Additive | SurfaceType.Base1ClipMap, false, "wb-mesh-additive-depth-write-1x", GpuBlendMode.Additive, 200f / 255f, false)]
    [InlineData(SurfaceType.Alpha | SurfaceType.Additive | SurfaceType.Base1ClipMap, true, "wb-mesh-additive-depth-write-1x", GpuBlendMode.Additive, 100f / 255f, false)]
    [InlineData(SurfaceType.Additive | SurfaceType.Base1ClipMap, false, "wb-mesh-raw-additive-depth-write-1x", GpuBlendMode.RawAdditive, 200f / 255f, false)]
    [InlineData(SurfaceType.Additive | SurfaceType.Base1ClipMap, true, "wb-mesh-raw-additive-depth-write-1x", GpuBlendMode.RawAdditive, 100f / 255f, false)]
    [InlineData(SurfaceType.InvAlpha | SurfaceType.Base1ClipMap, false, "wb-mesh-inverse-depth-write-1x", GpuBlendMode.InverseAlpha, 200f / 255f, true)]
    [InlineData(SurfaceType.InvAlpha | SurfaceType.Base1ClipMap, true, "wb-mesh-inverse-depth-write-1x", GpuBlendMode.InverseAlpha, 100f / 255f, true)]
    [InlineData(SurfaceType.InvAlpha | SurfaceType.Additive | SurfaceType.Base1ClipMap, false, "wb-mesh-inverse-additive-depth-write-1x", GpuBlendMode.InverseAdditive, 200f / 255f, false)]
    [InlineData(SurfaceType.InvAlpha | SurfaceType.Additive | SurfaceType.Base1ClipMap, true, "wb-mesh-inverse-additive-depth-write-1x", GpuBlendMode.InverseAdditive, 100f / 255f, false)]
    [InlineData(SurfaceType.Translucent | SurfaceType.Base1ClipMap | SurfaceType.Additive,
        true, "wb-mesh-alpha-1x", GpuBlendMode.StraightAlpha, 0f, false)]
    public void ImmediateBuildingDetail_UsesExactResolvedSetSurfaceState(
        SurfaceType type,
        bool paletted,
        string expectedPipeline,
        object expectedBlendValue,
        float expectedReference,
        bool expectedFog)
    {
        var expectedBlend = (GpuBlendMode)expectedBlendValue;
        using var fx = new DispatcherFixture(
            withAlphaQueue: true,
            detailAvailable: true,
            detailEnabled: true);
        using DrawScope draw = fx.BeginDraw(beginAlpha: true);
        WbDrawDispatcher.WalkClassifiedBatch building = AlphaWalkBatch(
            detailCategory: 1u,
            surfaceType: type,
            paletted: paletted);

        fx.Dispatcher.SubmitWalkAlphaInstance(in building, Matrix4x4.Identity);

        Assert.Equal(0, fx.AlphaQueue!.PendingCount);
        Assert.Single(fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
        Assert.Contains(fx.Device.Calls.OfType<GpuRecordedPipelineBind>(),
            call => call.PipelineName == expectedPipeline);
        GpuPushConstants armed = fx.Device.Calls
            .OfType<GpuRecordedPushConstants>()
            .Last(call => call.Constants.ParamA != 0f)
            .Constants;
        Assert.Equal(99u, armed.TextureIndexA);
        Assert.Equal(2f, armed.ParamA);
        Assert.Equal(expectedReference, armed.ParamB);
        Assert.Equal(!expectedFog,
            (armed.RenderPass & RetailDetailTextureContract.NoFogRenderPassFlag) != 0);
        RetailSetSurfaceMaterialState resolved = RetailSetSurfaceMaterialState.Resolve(
            type, texturePresent: true, textureHasPalette: paletted);
        GpuPipelineDescription selected = fx.Device.CreatedPipelines
            .Single(pipeline => pipeline.Description.Name == expectedPipeline)
            .Description;
        Assert.Equal(expectedBlend, selected.Blend);
        Assert.True(selected.Depth.Test);
        Assert.Equal(
            resolved.Blend == RetailSetSurfaceBlend.Opaque || resolved.AlphaTestEnabled,
            selected.Depth.Write);
        Assert.Equal(WorldDepthContract.WorldCompare, selected.Depth.Compare);
        Vector4 source = RetailDetailTextureContract.Combine(
            new Vector4(0.31f, 0.57f, 0.83f, 0.19f),
            new Vector3(0.73f, 0.41f, 0.67f),
            new Vector4(0.91f, 0.23f, 0.49f, 0.62f),
            authoredOpacity: 0.75f,
            liveOpacity: 0.4f);
        Assert.Equal(0.3534682f, source.X, 6);
        Assert.Equal(0.2330118f, source.Y, 6);
        Assert.Equal(0.5438054f, source.Z, 6);
        Assert.Equal(0.11532f, source.W, 6);
        Vector4 destination = new(0.17f, 0.37f, 0.71f, 0.29f);
        float x = source.W;
        RetailDetailTextureContract.FramebufferFamily family = expectedBlend switch
        {
            GpuBlendMode.StraightAlpha => RetailDetailTextureContract.FramebufferFamily.Alpha,
            GpuBlendMode.Additive => RetailDetailTextureContract.FramebufferFamily.AlphaAdditive,
            GpuBlendMode.RawAdditive => RetailDetailTextureContract.FramebufferFamily.Additive,
            GpuBlendMode.InverseAlpha => RetailDetailTextureContract.FramebufferFamily.InverseAlpha,
            GpuBlendMode.InverseAdditive => RetailDetailTextureContract.FramebufferFamily.InverseAlphaAdditive,
            _ => throw new ArgumentOutOfRangeException(nameof(expectedBlend)),
        };
        Vector4 expectedFramebuffer = family switch
        {
            RetailDetailTextureContract.FramebufferFamily.Alpha => source * x + destination * (1f - x),
            RetailDetailTextureContract.FramebufferFamily.AlphaAdditive => source * x + destination,
            RetailDetailTextureContract.FramebufferFamily.Additive => source + destination,
            RetailDetailTextureContract.FramebufferFamily.InverseAlpha => source * (1f - x) + destination * x,
            RetailDetailTextureContract.FramebufferFamily.InverseAlphaAdditive => source * (1f - x) + destination,
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };
        Vector4 framebuffer = RetailDetailTextureContract.Composite(source, destination, family);
        Assert.Equal(expectedFramebuffer.X, framebuffer.X, 6);
        Assert.Equal(expectedFramebuffer.Y, framebuffer.Y, 6);
        Assert.Equal(expectedFramebuffer.Z, framebuffer.Z, 6);
        Assert.Equal(expectedFramebuffer.W, framebuffer.W, 6);
        if (resolved.AlphaTestEnabled)
        {
            Assert.False(RetailDetailTextureContract.SurvivesClip(
                MathF.BitDecrement(expectedReference), expectedReference));
            Assert.True(RetailDetailTextureContract.SurvivesClip(expectedReference, expectedReference));
            Assert.True(RetailDetailTextureContract.SurvivesClip(
                MathF.BitIncrement(expectedReference), expectedReference));
        }
    }

    [Theory]
    [InlineData(SurfaceType.Alpha, "wb-mesh-alpha-1x")]
    [InlineData(SurfaceType.Alpha | SurfaceType.Additive, "wb-mesh-additive-1x")]
    [InlineData(SurfaceType.Additive, "wb-mesh-additive-1x")]
    [InlineData(SurfaceType.InvAlpha, "wb-mesh-inverse-1x")]
    [InlineData(SurfaceType.InvAlpha | SurfaceType.Additive, "wb-mesh-additive-1x")]
    [InlineData(SurfaceType.Alpha | SurfaceType.Base1ClipMap, "wb-mesh-alpha-1x")]
    [InlineData(SurfaceType.Alpha | SurfaceType.Additive | SurfaceType.Base1ClipMap, "wb-mesh-additive-1x")]
    [InlineData(SurfaceType.Additive | SurfaceType.Base1ClipMap, "wb-mesh-additive-1x")]
    [InlineData(SurfaceType.InvAlpha | SurfaceType.Base1ClipMap, "wb-mesh-inverse-1x")]
    [InlineData(SurfaceType.InvAlpha | SurfaceType.Additive | SurfaceType.Base1ClipMap, "wb-mesh-additive-1x")]
    [InlineData(SurfaceType.Translucent, "wb-mesh-alpha-1x")]
    [InlineData(SurfaceType.Translucent | SurfaceType.Additive, "wb-mesh-additive-1x")]
    [InlineData(SurfaceType.Translucent | SurfaceType.InvAlpha, "wb-mesh-inverse-1x")]
    [InlineData(SurfaceType.Translucent | SurfaceType.Base1ClipMap | SurfaceType.Additive, "wb-mesh-alpha-1x")]
    public void BuildingDetailOff_EveryRawStateRetainsThePreFixLogicalPath(
        SurfaceType type,
        string expectedPipeline)
    {
        using var fx = new DispatcherFixture(
            withAlphaQueue: true,
            detailAvailable: true,
            detailEnabled: false);
        using DrawScope draw = fx.BeginDraw(beginAlpha: true);
        WbDrawDispatcher.WalkClassifiedBatch building = AlphaWalkBatch(
            detailCategory: 1u,
            surfaceType: type);

        fx.Dispatcher.SubmitWalkAlphaInstance(in building, Matrix4x4.Identity);

        Assert.Equal(1, fx.AlphaQueue!.PendingCount);
        Assert.Empty(fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
        fx.AlphaQueue.EndFrame();
        Assert.Single(fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
        Assert.Contains(fx.Device.Calls.OfType<GpuRecordedPipelineBind>(),
            call => call.PipelineName == expectedPipeline);
        GpuPushConstants constants = fx.Device.Calls
            .OfType<GpuRecordedPushConstants>()
            .Last()
            .Constants;
        Assert.Equal(0u, constants.TextureIndexA);
        Assert.Equal(0f, constants.ParamA);
        Assert.Equal(0f, constants.ParamB);
        Assert.Equal(0, constants.RenderPass & RetailDetailTextureContract.NoFogRenderPassFlag);
    }

    private static WbDrawDispatcher.WalkClassifiedBatch AlphaWalkBatch(
        uint detailCategory,
        SurfaceType surfaceType = SurfaceType.Alpha,
        bool paletted = false) =>
        new(
            new GroupKey(
                0,
                0,
                3,
                new GpuTextureSlot(1),
                0,
                TranslucencyKindExtensions.FromSurfaceType(surfaceType),
                RetailSetSurfaceMaterialState.Resolve(
                    surfaceType,
                    texturePresent: true,
                    textureHasPalette: paletted),
                FoliageFlags: 0),
            Matrix4x4.Identity,
            ClipSlot: 0,
            WbDrawDispatcher.InstanceLightSet.Disabled,
            IndoorFlag: 0,
            Alpha: 1f,
            SelectionLighting: Vector2.Zero,
            DetailCategory: detailCategory,
            IsOpaque: false,
            LocalSortCenter: Vector3.Zero);

    private static WbDrawDispatcher.InstanceGroup AlphaInstanceGroup(
        uint detailCategory,
        SurfaceType surfaceType = SurfaceType.Alpha,
        float sortDistance = 0f)
    {
        var group = new WbDrawDispatcher.InstanceGroup
        {
            FirstIndex = 0,
            BaseVertex = 0,
            IndexCount = 3,
            TextureSlot = new GpuTextureSlot(1),
            TextureLayer = 0,
            Translucency = TranslucencyKindExtensions.FromSurfaceType(surfaceType),
            MaterialState = RetailSetSurfaceMaterialState.Resolve(
                surfaceType,
                texturePresent: true,
                textureHasPalette: false),
        };
        group.Matrices.Add(Matrix4x4.CreateTranslation(0f, 0f, sortDistance));
        group.SubmissionOrders.Add(0);
        group.Slots.Add(0);
        group.LightSets.Add(WbDrawDispatcher.InstanceLightSet.Disabled);
        group.IndoorFlags.Add(0);
        group.DetailCategories.Add(detailCategory);
        group.Opacities.Add(1f);
        group.SelectionLighting.Add(Vector2.Zero);
        return group;
    }

    private static void ExecuteClassicGroups(
        DispatcherFixture fixture,
        params WbDrawDispatcher.InstanceGroup[] groups)
    {
        fixture.Dispatcher.BeginFrame(frameSlot: 0);
        MethodInfo execute = typeof(WbDrawDispatcher).GetMethod(
            "ExecuteClassifiedGroups",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        execute.Invoke(
            fixture.Dispatcher,
            [
                Matrix4x4.Identity,
                Vector3.Zero,
                0u,
                groups,
                WbDrawDispatcher.EntitySet.All,
                groups.Length,
                groups.Length,
                false,
                false,
            ]);
    }

    private static List<(
        string Pipeline,
        GpuPushConstants Constants,
        GpuRecordedMultiDrawIndirect Draw)> ClassicDrawTranscript(
            RecordingGpuDevice device)
    {
        var transcript = new List<(
            string Pipeline,
            GpuPushConstants Constants,
            GpuRecordedMultiDrawIndirect Draw)>();
        IReadOnlyList<GpuRecordedCall> calls = device.Calls;
        for (int i = 0; i < calls.Count; i++)
        {
            if (calls[i] is not GpuRecordedMultiDrawIndirect draw)
                continue;

            string? pipeline = null;
            GpuPushConstants? constants = null;
            for (int prior = i - 1; prior >= 0 && (pipeline is null || constants is null); prior--)
            {
                if (pipeline is null && calls[prior] is GpuRecordedPipelineBind bind)
                    pipeline = bind.PipelineName;
                if (constants is null && calls[prior] is GpuRecordedPushConstants push)
                    constants = push.Constants;
            }

            Assert.NotNull(pipeline);
            Assert.NotNull(constants);
            transcript.Add((pipeline!, constants!.Value, draw));
        }

        return transcript;
    }

    private static ObjectMeshData PreparedTriangle(uint objectId, uint surfaceId) => new()
    {
        ObjectId = objectId,
        Vertices =
        [
            new VertexPositionNormalTexture { Position = Vector3.Zero },
            new VertexPositionNormalTexture { Position = Vector3.UnitX },
            new VertexPositionNormalTexture { Position = Vector3.UnitY },
        ],
        TextureBatches =
        {
            [(2, 2, Chorizite.Core.Render.Enums.TextureFormat.RGBA8)] =
            [
                new TextureBatchData
                {
                    Key = new TextureKey { SurfaceId = surfaceId },
                    TextureData = Enumerable.Repeat((byte)0xFF, 16).ToArray(),
                    Indices = [0, 1, 2],
                    Translucency = TranslucencyKind.Opaque,
                    CullMode = CullMode.Clockwise,
                },
            ],
        },
    };

    [Fact]
    public void SubmitWalkAlphaInstance_RejectsAMismatchedViewProjectionInTheSameScope()
    {
        using var fx = new DispatcherFixture(withAlphaQueue: true);
        fx.AlphaQueue!.BeginFrame();

        var key = new GroupKey(0, 0, 3, new GpuTextureSlot(1), 0, TranslucencyKind.AlphaBlend,
            MaterialState: RetailSetSurfaceMaterialState.Opaque, FoliageFlags: 0);
        var batch = new WbDrawDispatcher.WalkClassifiedBatch(
            key, Matrix4x4.Identity, 0, WbDrawDispatcher.InstanceLightSet.Disabled, 0, 1f,
            Vector2.Zero, 0, IsOpaque: false, LocalSortCenter: new Vector3(0, 0, 10));

        fx.Dispatcher.SubmitWalkAlphaInstance(in batch, Matrix4x4.Identity);

        Assert.Throws<InvalidOperationException>(() =>
            fx.Dispatcher.SubmitWalkAlphaInstance(
                in batch, Matrix4x4.CreateTranslation(1, 0, 0)));

        fx.AlphaQueue.AbortFrame();
    }


    [Fact]
    public void PrepareThenDrawOrderedStream_DoesNotReadOrCorruptTheSharedAlphaCullScratch()
    {
        using var fx = new DispatcherFixture();
        using DrawScope draw = fx.BeginDraw();

        FieldInfo field = typeof(WbDrawDispatcher).GetField(
            "_drawCullModes", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var poisonModes = (CullMode[])field.GetValue(fx.Dispatcher)!;
        poisonModes[0] = CullMode.None;

        var stream = new OrderedDrawStream();
        stream.Append(new OrderedDrawCommand(
            new GroupKey(0, 0, 3, new GpuTextureSlot(1), 0, TranslucencyKind.Opaque,
                MaterialState: RetailSetSurfaceMaterialState.Opaque, FoliageFlags: 0, CullMode: CullMode.Clockwise),
            Matrix4x4.Identity, WalkDrawStage.Terrain, 0, 0,
            WbDrawDispatcher.InstanceLightSet.Disabled, 0, 1f, Vector2.Zero, 0));

        fx.Dispatcher.PrepareOrderedStream(draw.Frame, stream, Matrix4x4.Identity);
        fx.Dispatcher.DrawOrderedRange(draw.Pass, 0, stream.Count);

        List<GpuCullMode> cullCalls = [.. fx.Device.Calls.OfType<GpuRecordedCullMode>().Select(c => c.CullMode)];
        Assert.Equal([GpuCullMode.Front], cullCalls);

        // (2) The shared _drawCullModes scratch is UNTOUCHED — the ordered
        // path never wrote through it.
        var afterModes = (CullMode[])field.GetValue(fx.Dispatcher)!;
        Assert.Equal(CullMode.None, afterModes[0]);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    private readonly struct DrawScope : IDisposable
    {
        private readonly IDisposable _publication;
        private readonly IGpuPassEncoder _pass;

        public DrawScope(IGpuFrame frame, IGpuPassEncoder pass, IDisposable publication)
        {
            Frame = frame;
            _pass = pass;
            _publication = publication;
        }

        public IGpuFrame Frame { get; }

        public IGpuPassEncoder Pass => _pass;

        public void Dispose()
        {
            _publication.Dispose();
            _pass.Dispose();
        }
    }

    private sealed class DispatcherFixture : IDisposable
    {
        private readonly WbMeshAdapter _meshAdapter;
        private readonly TextureCache _textures;

        public DispatcherFixture(
            bool withAlphaQueue = false,
            IRetailSelectionRenderSink? selectionSink = null,
            bool detailAvailable = false,
            bool detailEnabled = false,
            IPreparedAssetSource? preparedAssets = null)
        {
            Device = new RecordingGpuDevice();
            FrameLifetime = new GpuDeviceFrameLifetime(Device);
            Scope = new VulkanWorldPassScope(sampleCount: 1);
            _textures = new TextureCache(Device, new NoopDatReaderWriter());
            _meshAdapter = new WbMeshAdapter(
                Device,
                new NoopDatReaderWriter(),
                preparedAssets ?? new NullPreparedAssetSource(),
                NullLogger<WbMeshAdapter>.Instance,
                Device.Retirement);
            var entitySpawnAdapter = new EntitySpawnAdapter(
                _textures,
                _ => throw new NotSupportedException("Not exercised by these tests."));
            AlphaQueue = withAlphaQueue ? new RetailAlphaQueue() : null;

            Dispatcher = new WbDrawDispatcher(
                Device,
                FrameLifetime,
                Scope,
                _textures,
                _meshAdapter,
                entitySpawnAdapter,
                new EntityClassificationCache(),
                new AcDream.Core.Rendering.TranslucencyFadeManager(),
                selectionSink: selectionSink,
                alphaQueue: AlphaQueue,
                buildingDetail: detailAvailable
                    ? new TerrainAtlas.RetailDetailTextureBinding(
                        new GpuTextureSlot(99),
                        Tiling: 2f,
                        SurfaceTextureId: 1,
                        RenderSurfaceId: 2,
                        Width: 4,
                        Height: 4)
                    : default,
                buildingDetailEnabled: detailEnabled ? DetailOn : DetailOff);
        }

        public RecordingGpuDevice Device { get; }

        public GpuDeviceFrameLifetime FrameLifetime { get; }

        public VulkanWorldPassScope Scope { get; }

        public WbDrawDispatcher Dispatcher { get; }

        public RetailAlphaQueue? AlphaQueue { get; }

        public ObjectMeshManager Manager => _meshAdapter.MeshManager!;

        public WbMeshAdapter Adapter => _meshAdapter;

        public DrawScope BeginDraw(bool beginAlpha = false)
        {
            if (beginAlpha)
            {
                Dispatcher.BeginFrame(frameSlot: 0);
                AlphaQueue!.BeginFrame();
            }
            FrameLifetime.BeginFrame();
            IGpuFrame frame = FrameLifetime.CurrentFrame!;
            IGpuPassEncoder pass = frame.BeginPass(
                GpuPassDescription.BackbufferClear(
                    "fw3-2a-walk-populator-test", Vector4.Zero, sampleCount: 1));
            IDisposable publication = Scope.Publish(pass);
            Device.Clear();
            return new DrawScope(frame, pass, publication);
        }

        private static bool DetailOn() => true;

        private static bool DetailOff() => false;

        public void Dispose()
        {
            Dispatcher.Dispose();
            _meshAdapter.Dispose();
            _textures.Dispose();
            Device.Dispose();
        }
    }

    private sealed class NullPreparedAssetSource : IPreparedAssetSource
    {
        public PreparedAssetSourceStats Stats => default;

        public CacheStats DecodedTextureCacheStats => default;

        public PreparedAssetPresence Probe(
            AcDream.Content.Pak.PakAssetType type,
            uint sourceFileId) =>
            PreparedAssetPresence.Missing;

        public PreparedAssetReadResult Read(
            in PreparedAssetRequest request,
            CancellationToken cancellationToken = default) =>
            PreparedAssetReadResult.Missing;

        public void Dispose()
        {
        }
    }

    private sealed class RecordingPreparedAssetSource(ObjectMeshData? data = null)
        : IPreparedAssetSource
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);
        public PreparedAssetSourceStats Stats => default;
        public CacheStats DecodedTextureCacheStats => default;
        public PreparedAssetPresence Probe(
            AcDream.Content.Pak.PakAssetType type,
            uint sourceFileId) => PreparedAssetPresence.Available;
        public PreparedAssetReadResult Read(
            in PreparedAssetRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCount);
            return data is not null && data.ObjectId == request.RuntimeObjectId
                ? PreparedAssetReadResult.Loaded(data)
                : PreparedAssetReadResult.Missing;
        }
        public void Dispose()
        {
        }
    }

    private sealed class NoopDatReaderWriter : IDatReaderWriter
    {
        private readonly StubDatabase _portal = new();
        private readonly StubDatabase _highRes = new();
        private readonly StubDatabase _language = new();
        private readonly StubDatabase _cell = new();

        public string SourceDirectory => string.Empty;

        public IDatDatabase Portal => _portal;

        public IDatDatabase Cell => _cell;

        public ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; } =
            new(new Dictionary<uint, IDatDatabase>());

        public IDatDatabase HighRes => _highRes;

        public IDatDatabase Language => _language;

        public IDatDatabase Local => _language;

        public ReadOnlyDictionary<uint, uint> RegionFileMap { get; } =
            new(new Dictionary<uint, uint>());

        public int PortalIteration => 0;

        public int CellIteration => 0;

        public int HighResIteration => 0;

        public int LanguageIteration => 0;

        public bool TryGetFileBytes(
            uint regionId,
            uint fileId,
            ref byte[] bytes,
            out int bytesRead)
        {
            bytesRead = 0;
            return false;
        }

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
            Array.Empty<uint>();

        public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) =>
            Array.Empty<IDatReaderWriter.IdResolution>();

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        public bool TrySave<T>(
            uint regionId,
            T obj,
            int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : IDBObj => default;

        public bool TryGet<T>(
            uint fileId,
            [MaybeNullWhen(false)] out T value) where T : IDBObj
        {
            value = default;
            return false;
        }

        public void Dispose()
        {
        }

        private sealed class StubDatabase : IDatDatabase
        {
            public DatDatabase Db => throw new NotSupportedException();

            public int Iteration => 0;

            public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
                Array.Empty<uint>();

            public bool TryGet<T>(
                uint fileId,
                [MaybeNullWhen(false)] out T value) where T : IDBObj
            {
                value = default;
                return false;
            }

            public bool TryGetFileBytes(
                uint fileId,
                [MaybeNullWhen(false)] out byte[] value)
            {
                value = null;
                return false;
            }

            public bool TryGetFileBytes(
                uint fileId,
                ref byte[] bytes,
                out int bytesRead)
            {
                bytesRead = 0;
                return false;
            }

            public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
                throw new NotSupportedException();

            public void Dispose()
            {
            }
        }
    }
}
