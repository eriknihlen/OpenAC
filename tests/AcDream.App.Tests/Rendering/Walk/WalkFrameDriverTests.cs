using System.Collections.Concurrent;
using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Walk;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Content;
using AcDream.Core.Meshing;
using AcDream.Core.Vfx;
using AcDream.Core.World;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib;
using DatReaderWriter.Lib.IO;
using DatReaderWriter.Types;
using Microsoft.Extensions.Logging.Abstractions;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed partial class WalkFrameDriverTests
{
    // ── Shared ordered log: BOTH the fake leaf renderer and the fake trace
    // write into ONE list, so a single sequence assertion proves the FULL
    // interleave (stream flushes interleaved with sky/terrain/shell/punch/
    // alpha-barrier), not just each half in isolation. ─────────────────────

    internal sealed class RecordingLeafRenderer(
        List<string> log,
        RetailAlphaQueue? alpha = null) : IWalkFrameLeafRenderer
    {
        public readonly List<WalkPolygon> Punches = new();
        public readonly List<uint> Shells = new();
        public readonly List<int> AlphaPendingAtBarrier = new();
        public readonly List<(uint LandblockId, int SideCellCount, int CellIndex)[]> LandCellBatches = new();

        public readonly HashSet<uint> CellsWithoutEmitters = new();

        public int SealPolygonsSubmitted = 1;

        public void DrawSky() => log.Add("SKY");

        public void DrawLandCellBatch(
            IReadOnlyList<(uint LandblockId, int SideCellCount, int CellIndex)> cells)
        {
            LandCellBatches.Add(cells.ToArray());
            log.Add("LANDCELL:" + string.Join(
                ',', cells.Select(c => $"{c.LandblockId:x8}:{c.SideCellCount}:{c.CellIndex}")));
        }

        public bool HasRenderableEmittersInCell(uint cellId) => !CellsWithoutEmitters.Contains(cellId);

        public void DrawCellShell(uint cellId)
        {
            Shells.Add(cellId);
            log.Add($"SHELL:{cellId:x8}");
        }

        public void FlushLandscape() => log.Add("LFLUSH");

        public void ClearInteriorDepth() => log.Add("CLEAR");

        public int DrawExitSeals()
        {
            log.Add("SEALS");
            return SealPolygonsSubmitted;
        }

        public void DrawPunchFan(WalkPolygon worldPolygon, int activeViewIndex)
        {
            Punches.Add(worldPolygon);
            log.Add($"PUNCH:{worldPolygon.Vertices.Length}@v{activeViewIndex}");
        }

        public void AlphaBarrier()
        {
            if (alpha is null)
            {
                log.Add("ALPHA");
                return;
            }

            AlphaPendingAtBarrier.Add(alpha.PendingCount);
            log.Add($"ALPHA:{alpha.PendingCount}");
            alpha.Flush(RetailAlphaFlushSite.DrawBuilding, 0f);
        }

        public readonly List<int> AlphaPendingAtSortCellExit = new();

        public void FlushSortCellExit()
        {
            if (alpha is null)
            {
                log.Add("SORTCELLEXIT");
                return;
            }

            AlphaPendingAtSortCellExit.Add(alpha.PendingCount);
            log.Add($"SORTCELLEXIT:{alpha.PendingCount}");
            alpha.Flush(RetailAlphaFlushSite.SortCellExit, 0.75f);
        }

        public ReadOnlySpan<PreparedParticleAlphaSubmission> PrepareStaticParticles(uint cellId)
        {
            log.Add($"PARTICLES:{cellId:x8}");
            return ReadOnlySpan<PreparedParticleAlphaSubmission>.Empty;
        }

        public ReadOnlySpan<PreparedParticleAlphaSubmission> PrepareCellParticles(uint cellId)
        {
            log.Add($"CELL-PARTICLES:{cellId:x8}");
            return ReadOnlySpan<PreparedParticleAlphaSubmission>.Empty;
        }
    }

    private sealed class RecordingTrace(List<string> log) : IWalkFrameDriverTrace
    {
        public void OnFlush(int commandCount, IReadOnlyList<WalkDrawStage> stages) =>
            log.Add($"FLUSH:{commandCount}:{string.Join(',', stages.Distinct())}");
    }

    private sealed class ProductionParticleLeaf(
        ParticleSystem particles,
        ParticleRenderer renderer,
        ICamera camera,
        Vector3 cameraWorldPosition) : IWalkFrameLeafRenderer
    {
        public void DrawSky() { }
        public void DrawLandCellBatch(
            IReadOnlyList<(uint LandblockId, int SideCellCount, int CellIndex)> cells) { }
        public bool HasRenderableEmittersInCell(uint cellId) =>
            particles.HasRenderableEmittersInCell(ParticleRenderPass.Scene, cellId);
        public void DrawCellShell(uint cellId) { }
        public ReadOnlySpan<PreparedParticleAlphaSubmission> PrepareStaticParticles(uint cellId) =>
            renderer.PrepareForCellAlpha(
                camera, cameraWorldPosition, ParticleRenderPass.Scene, cellId);
        public ReadOnlySpan<PreparedParticleAlphaSubmission> PrepareCellParticles(uint cellId) =>
            renderer.PrepareForCellAlpha(
                camera, cameraWorldPosition, ParticleRenderPass.Scene, cellId);
        public void ClearInteriorDepth() { }
        public void FlushLandscape() { }
        public int DrawExitSeals() => 0;
        public void DrawPunchFan(WalkPolygon worldPolygon, int activeViewIndex) { }
        public void AlphaBarrier() { }
        public void FlushSortCellExit() { }
    }

    private sealed class IdentityCamera : ICamera
    {
        public Matrix4x4 View => Matrix4x4.Identity;
        public Matrix4x4 Projection => Matrix4x4.Identity;
        public float Aspect { get; set; } = 1f;
    }

    internal sealed class FakeWorldData : IWalkFrameWorldData
    {
        public readonly Dictionary<uint, WalkFrameStaticRecords> CellStaticsByCell = new();
        public readonly Dictionary<uint, WalkFrameStaticRecords> CellDynamicsByCell = new();
        public readonly Dictionary<uint, WalkFrameStaticRecords> OutdoorStaticsByCell = new();
        public readonly Dictionary<uint, WalkFrameStaticRecords> OutdoorDynamicsByCell = new();
        public readonly Dictionary<WalkBuilding, WalkFrameStaticRecords> ShellByBuilding = new();
        public readonly Dictionary<WalkBuilding, Matrix4x4> WorldTransformByBuilding = new();

        public WalkFrameStaticRecords GetCellStatics(uint cellId) =>
            CellStaticsByCell.GetValueOrDefault(cellId, WalkFrameStaticRecords.Empty);

        public WalkFrameStaticRecords GetCellObjects(uint cellId) =>
            Combine(
                CellStaticsByCell.GetValueOrDefault(cellId, WalkFrameStaticRecords.Empty),
                CellDynamicsByCell.GetValueOrDefault(cellId, WalkFrameStaticRecords.Empty));

        public WalkFrameStaticRecords GetCellDynamics(uint cellId) =>
            CellDynamicsByCell.GetValueOrDefault(cellId, WalkFrameStaticRecords.Empty);

        public WalkFrameStaticRecords GetOutdoorStatics(uint cellId) =>
            OutdoorStaticsByCell.GetValueOrDefault(cellId, WalkFrameStaticRecords.Empty);

        public WalkFrameStaticRecords GetOutdoorObjects(uint cellId) =>
            Combine(
                OutdoorStaticsByCell.GetValueOrDefault(cellId, WalkFrameStaticRecords.Empty),
                OutdoorDynamicsByCell.GetValueOrDefault(cellId, WalkFrameStaticRecords.Empty));

        public WalkFrameStaticRecords GetOutdoorDynamics(uint cellId) =>
            OutdoorDynamicsByCell.GetValueOrDefault(cellId, WalkFrameStaticRecords.Empty);

        public WalkFrameStaticRecords GetBuildingShellStatics(WalkBuilding building) =>
            ShellByBuilding.GetValueOrDefault(building, WalkFrameStaticRecords.Empty);

        public Matrix4x4 GetBuildingWorldTransform(WalkBuilding building) =>
            WorldTransformByBuilding.GetValueOrDefault(building, Matrix4x4.Identity);

        private static WalkFrameStaticRecords Combine(
            WalkFrameStaticRecords first,
            WalkFrameStaticRecords second)
        {
            if (second.Records.Count == 0)
                return first;
            RenderProjectionRecord[] dynamicRecords = second.Records
                .Select(record => record with
                {
                    ProjectionClass = RenderProjectionClass.LiveDynamicRoot,
                })
                .ToArray();
            if (first.Records.Count == 0)
                return new WalkFrameStaticRecords(dynamicRecords, second.TupleLandblockId);
            return new WalkFrameStaticRecords(
                first.Records.Concat(dynamicRecords).ToArray(),
                first.TupleLandblockId);
        }
    }


    private sealed class Caster : IWalkRayCaster
    {
        public Vector3 RayThrough(float screenX, float screenY) => new(screenX, screenY, 100f);
    }

    internal sealed class TestContext : IWalkFrameContext, IRetailFrameWalkContext
    {
        public readonly Dictionary<uint, WalkCell> Cells = new();
        public readonly Dictionary<WalkBuilding, float> ViewerDistances = new();
        private readonly Matrix4x4 _viewProj;
        private static readonly Vector2[] RootQuad =
        [
            new(0, 480), new(640, 480), new(640, 0), new(0, 0),
        ];

        public TestContext()
        {
            Matrix4x4 view = Matrix4x4.CreateLookAt(
                Vector3.Zero, new Vector3(0, 0, -1), Vector3.UnitY);
            Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 1f, 0.1f, 1000f);
            _viewProj = view * proj;
        }

        public Vector3 ViewpointIn(WalkCell cell) => Vector3.Zero;
        public Matrix4x4 ObjectToClip(WalkCell cell) => _viewProj;
        public WalkCell? GetVisible(uint cellId) => Cells.GetValueOrDefault(cellId);
        public IWalkRayCaster Rays { get; } = new Caster();
        public Vector3 WorldViewpoint => Vector3.Zero;
        public float ViewportWidth => 640f;
        public float ViewportHeight => 480f;

        public uint ViewerCellId { get; set; }
        public bool WeatherGateOpen { get; set; }

        public Vector3 ViewpointInBuilding(WalkBuilding building) => Vector3.Zero;

        public float ViewerDistanceTo(WalkBuilding building) =>
            ViewerDistances.GetValueOrDefault(building, 0f);

        public IWalkFrameContext CellContext => this;
        public WalkPlane CyPlane => new(new Vector3(0, 0, 1), 0f);
        public void SetActiveView(WalkPortalView views, int index) { }

        public int ClipBuildingPolygon(
            WalkBuilding building, WalkPolygon polygon, int side, Span<WalkScreenPoint> output)
        {
            Span<WalkScreenPoint> projected = stackalloc WalkScreenPoint[polygon.Vertices.Length];
            for (int i = 0; i < polygon.Vertices.Length; i++)
                projected[i] = WalkScreenClip.TransformToScreen(
                    polygon.Vertices[i], _viewProj, ViewportWidth, ViewportHeight);
            if (side != 0)
                projected.Reverse();
            return WalkScreenClip.ClipAgainstView(projected, RootQuad, output);
        }
    }

    internal static WalkPolygon Quad(float z, bool facingViewer = true) => new()
    {
        Vertices =
        [
            new Vector3(-0.5f, -0.5f, z), new Vector3(0.5f, -0.5f, z),
            new Vector3(0.5f, 0.5f, z), new Vector3(-0.5f, 0.5f, z),
        ],
        Plane = new WalkPlane(new Vector3(0, 0, facingViewer ? 1f : -1f), facingViewer ? -z : z),
    };



    [Fact]
    public void RunFrame_InteriorFloodWithExitView_FreshDriverSkipsTheGatedClearThenDrawsSealsAndFloodCells()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        const ulong gfxObjA = 0x0200_0001UL;
        const ulong gfxObjB = 0x0200_0002UL;
        InjectRenderData(fx.Manager, gfxObjA, MakeFlatMesh(
            MakeBatch(0x08100001u, TranslucencyKind.Opaque, 0, 0, 3, 1)));
        InjectRenderData(fx.Manager, gfxObjB, MakeFlatMesh(
            MakeBatch(0x08100002u, TranslucencyKind.Opaque, 3, 4, 3, 2)));

        var ctx = new TestContext();
        var cell1 = new WalkCell
        {
            CellId = 0x100,
            StabList = [0x101u],
            Portals =
            [
                new WalkCellPortal
                {
                    OtherCellId = 0x101, PolygonIndex = 0, PortalSide = 0, OtherPortalId = 0,
                },
                new WalkCellPortal
                {
                    OtherCellId = 0xFFFFFFFF, PolygonIndex = 1, PortalSide = 0, OtherPortalId = -1,
                },
            ],
            PortalPolygons = [Quad(-2f), Quad(-3f)],
        };
        var cell2 = new WalkCell
        {
            CellId = 0x101,
            Portals = [new WalkCellPortal
            {
                OtherCellId = 0x100, PolygonIndex = 0, PortalSide = 1, OtherPortalId = 0,
            }],
            PortalPolygons = [Quad(-2f)],
        };
        ctx.Cells[cell1.CellId] = cell1;
        ctx.Cells[cell2.CellId] = cell2;

        var worldData = new FakeWorldData();
        worldData.CellStaticsByCell[0x100] = new WalkFrameStaticRecords(
            new[] { MakeRecord(101, 0, Vector3.Zero, [new MeshRef((uint)gfxObjA, Matrix4x4.Identity)]) }, 0x8C04u);
        worldData.CellStaticsByCell[0x101] = new WalkFrameStaticRecords(
            new[] { MakeRecord(102, 0, Vector3.Zero, [new MeshRef((uint)gfxObjB, Matrix4x4.Identity)]) }, 0x8C04u);

        var leaf = new RecordingLeafRenderer(log);
        var trace = new RecordingTrace(log);
        using ClipFrame clipFrame = ClipFrame.NoClip();
        var driver = new WalkFrameDriver(
            fx.Dispatcher, leaf, worldData, trace, clipFrame);
        var walk = new RetailFrameWalk();
        var landscape = new WalkLandscape { MidWidth = 1, Blocks = new WalkLandBlock?[1] };

        using DrawScope draw = fx.BeginDraw();
        driver.RunFrame(
            walk, cameraCellId: cell1.CellId, cameraCell: cell1, landscape: landscape,
            ctx, draw.Frame, draw.Pass, Matrix4x4.Identity, cameraWorldPosition: Vector3.Zero);

        Assert.Equal(
            new[]
            {
                "SKY", "LFLUSH", "SEALS",
                "SHELL:00000101", "SHELL:00000100",
                "FLUSH:1:CellStatic", "CELL-PARTICLES:00000101",
                "FLUSH:1:CellStatic", "CELL-PARTICLES:00000100",
            },
            log);
        Assert.DoesNotContain("CLEAR", log);
        Assert.Equal(1, driver.PortalsDrawnCount);

        List<GpuRecordedMultiDrawIndirect> mdiCalls =
            [.. fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>()];
        Assert.Equal(2, mdiCalls.Count);
        Assert.All(mdiCalls, c => Assert.Equal(1u, c.DrawCount));
        // Nothing dropped: every populated record reached exactly one indirect draw.
        Assert.Equal(2, mdiCalls.Sum(c => (int)c.DrawCount));

        Assert.Equal(1, driver.InteriorFloodViewSliceCountAt(0));
        Assert.Equal(1, driver.InteriorFloodViewSliceCountAt(1));
        Assert.Equal(4, driver.InteriorFloodViewClipPlanesAt(0, 0).Length);
        Assert.Equal(4, driver.InteriorFloodViewClipPlanesAt(1, 0).Length);
        Assert.True(clipFrame.SlotCount >= 3);
    }

    [Fact]
    public void InteriorFloodAfterLandscape_RearmsRetailPartStampForPostClearCellRepaint()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        const ulong gfxObj = 0x0200_0021UL;
        const uint outdoorCellId = 0xF4180009u;
        const uint interiorCellId = 0xF4180112u;
        InjectRenderData(fx.Manager, gfxObj, MakeFlatMesh(
            MakeBatch(0x08100021u, TranslucencyKind.Opaque, 0, 0, 3, 1)));

        RenderProjectionRecord sharedPart = MakeRecord(
            0x4F41806Cu,
            0,
            Vector3.Zero,
            [new MeshRef((uint)gfxObj, Matrix4x4.Identity)]);
        var worldData = new FakeWorldData();
        worldData.OutdoorStaticsByCell[outdoorCellId] =
            new WalkFrameStaticRecords(new[] { sharedPart }, 0xF418u);
        worldData.CellStaticsByCell[interiorCellId] =
            new WalkFrameStaticRecords(new[] { sharedPart }, 0xF418u);

        var ctx = new TestContext();
        var interiorCell = new WalkCell { CellId = interiorCellId };
        interiorCell.PushView();
        WalkCopyView.AppendFullViewportQuad(
            interiorCell.TopView,
            ctx.Rays,
            ctx.WorldViewpoint,
            ctx.ViewportWidth,
            ctx.ViewportHeight);
        ctx.Cells[interiorCellId] = interiorCell;

        var driver = new WalkFrameDriver(
            fx.Dispatcher,
            new RecordingLeafRenderer(log),
            worldData,
            new RecordingTrace(log));
        IWalkEventSink sink = driver;

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        sink.Emit(WalkEvent.Landscape(activeViewCount: 1));
        var landscapeViews = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            landscapeViews,
            ctx.Rays,
            ctx.WorldViewpoint,
            ctx.ViewportWidth,
            ctx.ViewportHeight);
        sink.OnLandscapeViews(landscapeViews);
        sink.OnLandscapeCellTurn(outdoorCellId);
        sink.OnInteriorFloodDrawTurn([interiorCellId], outsideViewCount: 1);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        List<GpuRecordedMultiDrawIndirect> mdiCalls =
            [.. fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>()];
        Assert.Equal(2, mdiCalls.Count);
        Assert.All(mdiCalls, call => Assert.Equal(1u, call.DrawCount));
        Assert.Equal(2, mdiCalls.Sum(call => (int)call.DrawCount));
        Assert.Equal(2, log.Count(entry => entry == "FLUSH:1:OutdoorStatic"
            || entry == "FLUSH:1:CellStatic"));
    }


    [Fact]
    public void RunFrame_InteriorFloodWithNoExitView_SkipsLandscapeAndNeverFlushesClearsOrSeals()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        const ulong gfxObjA = 0x0200_0003UL;
        const ulong gfxObjB = 0x0200_0004UL;
        InjectRenderData(fx.Manager, gfxObjA, MakeFlatMesh(
            MakeBatch(0x08100003u, TranslucencyKind.Opaque, 0, 0, 3, 1)));
        InjectRenderData(fx.Manager, gfxObjB, MakeFlatMesh(
            MakeBatch(0x08100004u, TranslucencyKind.Opaque, 3, 4, 3, 2)));

        var ctx = new TestContext();
        var cell1 = new WalkCell
        {
            CellId = 0x100,
            StabList = [0x101u],
            Portals = [new WalkCellPortal
            {
                OtherCellId = 0x101, PolygonIndex = 0, PortalSide = 0, OtherPortalId = 0,
            }],
            PortalPolygons = [Quad(-2f)],
        };
        var cell2 = new WalkCell
        {
            CellId = 0x101,
            Portals = [new WalkCellPortal
            {
                OtherCellId = 0x100, PolygonIndex = 0, PortalSide = 1, OtherPortalId = 0,
            }],
            PortalPolygons = [Quad(-2f)],
        };
        ctx.Cells[cell1.CellId] = cell1;
        ctx.Cells[cell2.CellId] = cell2;

        var worldData = new FakeWorldData();
        worldData.CellStaticsByCell[0x100] = new WalkFrameStaticRecords(
            new[] { MakeRecord(101, 0, Vector3.Zero, [new MeshRef((uint)gfxObjA, Matrix4x4.Identity)]) }, 0x8C04u);
        worldData.CellStaticsByCell[0x101] = new WalkFrameStaticRecords(
            new[] { MakeRecord(102, 0, Vector3.Zero, [new MeshRef((uint)gfxObjB, Matrix4x4.Identity)]) }, 0x8C04u);

        var leaf = new RecordingLeafRenderer(log);
        var trace = new RecordingTrace(log);
        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, worldData, trace);
        var walk = new RetailFrameWalk();

        using DrawScope draw = fx.BeginDraw();
        driver.RunFrame(
            walk, cameraCellId: cell1.CellId, cameraCell: cell1, landscape: new WalkLandscape(),
            ctx, draw.Frame, draw.Pass, Matrix4x4.Identity, cameraWorldPosition: Vector3.Zero);

        Assert.Equal(
            new[]
            {
                "SHELL:00000101", "SHELL:00000100",
                "FLUSH:1:CellStatic", "CELL-PARTICLES:00000101",
                "FLUSH:1:CellStatic", "CELL-PARTICLES:00000100",
            },
            log);
        Assert.Equal(0, driver.PortalsDrawnCount);

        List<GpuRecordedMultiDrawIndirect> mdiCalls =
            [.. fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>()];
        Assert.Equal(2, mdiCalls.Count);
        Assert.All(mdiCalls, c => Assert.Equal(1u, c.DrawCount));
        // Nothing dropped: every populated record reached exactly one indirect draw.
        Assert.Equal(2, mdiCalls.Sum(c => (int)c.DrawCount));
    }


    [Fact]
    public void OnInteriorFloodDrawTurn_FirstOvFrameSkipsClear_SecondFrameArmedByFirstsSealsClears()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var leaf = new RecordingLeafRenderer(log);
        var ctx = new TestContext();
        const uint cellId = 0xF4180200u;
        var cell = new WalkCell { CellId = cellId };
        cell.PushView();
        WalkCopyView.AppendFullViewportQuad(
            cell.TopView, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        ctx.Cells[cellId] = cell;

        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, new FakeWorldData());
        IWalkEventSink sink = driver;

        using DrawScope draw = fx.BeginDraw();

        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        sink.Emit(WalkEvent.Landscape(activeViewCount: 1));
        var views1 = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            views1, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        sink.OnLandscapeViews(views1);
        sink.OnInteriorFloodDrawTurn([cellId], outsideViewCount: 1);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal(
            new[]
            {
                "SKY", "LFLUSH", "SEALS",
                "SHELL:f4180200", "CELL-PARTICLES:f4180200",
            },
            log);
        Assert.DoesNotContain("CLEAR", log);
        Assert.Equal(1, driver.PortalsDrawnCount);
        log.Clear();

        // Frame 1's exit seals reported SealPolygonsSubmitted (default 1) at
        // Replay, arming PortalsDrawnCount for THIS frame's read-then-zero.
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        sink.Emit(WalkEvent.Landscape(activeViewCount: 1));
        var views2 = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            views2, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        sink.OnLandscapeViews(views2);
        sink.OnInteriorFloodDrawTurn([cellId], outsideViewCount: 1);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal(
            new[]
            {
                "SKY", "LFLUSH", "CLEAR", "SEALS",
                "SHELL:f4180200", "CELL-PARTICLES:f4180200",
            },
            log);
    }


    [Fact]
    public void OnInteriorFloodDrawTurn_FloodWithNoExitPortal_NeverClearsAcrossFrames()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var leaf = new RecordingLeafRenderer(log) { SealPolygonsSubmitted = 0 };
        var ctx = new TestContext();
        const uint cellId = 0xF4180201u;
        var cell = new WalkCell { CellId = cellId };
        cell.PushView();
        WalkCopyView.AppendFullViewportQuad(
            cell.TopView, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        ctx.Cells[cellId] = cell;

        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, new FakeWorldData());
        IWalkEventSink sink = driver;

        using DrawScope draw = fx.BeginDraw();
        for (int frame = 0; frame < 3; frame++)
        {
            driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
            sink.Emit(WalkEvent.Landscape(activeViewCount: 1));
            var views = new WalkPortalView();
            WalkCopyView.AppendFullViewportQuad(
                views, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
            sink.OnLandscapeViews(views);
            sink.OnInteriorFloodDrawTurn([cellId], outsideViewCount: 1);
            driver.EndFrame();
            driver.Replay(draw.Frame, draw.Pass);
        }

        Assert.DoesNotContain("CLEAR", log);
        Assert.Equal(3, log.Count(entry => entry == "SEALS"));
        Assert.Equal(0, driver.PortalsDrawnCount);
    }


    [Fact]
    public void WalkFrame_OutdoorRoot_NeverFiresTheInteriorClearSealMachinery()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var leaf = new RecordingLeafRenderer(log);
        var ctx = new TestContext();
        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, new FakeWorldData());
        var walk = new RetailFrameWalk();
        var landscape = new WalkLandscape { MidWidth = 1, Blocks = new WalkLandBlock?[1] };

        using DrawScope draw = fx.BeginDraw();
        driver.RunFrame(
            walk, cameraCellId: 0x00000050u, cameraCell: null, landscape: landscape,
            ctx, draw.Frame, draw.Pass, Matrix4x4.Identity, cameraWorldPosition: Vector3.Zero);

        Assert.Contains("SKY", log);
        Assert.DoesNotContain("LFLUSH", log);
        Assert.DoesNotContain("CLEAR", log);
        Assert.DoesNotContain("SEALS", log);
        Assert.Equal(0, driver.PortalsDrawnCount);
    }

    [Fact]
    public void OnInteriorFloodDrawTurn_OvZeroAfterAPriorArmedCounter_LeavesTheLatchCompletelyUntouched()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var leaf = new RecordingLeafRenderer(log);
        var ctx = new TestContext();
        const uint cellId = 0xF4180310u;
        var cell = new WalkCell { CellId = cellId };
        cell.PushView();
        WalkCopyView.AppendFullViewportQuad(
            cell.TopView, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        ctx.Cells[cellId] = cell;

        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, new FakeWorldData());
        IWalkEventSink sink = driver;

        using DrawScope draw = fx.BeginDraw();

        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        sink.Emit(WalkEvent.Landscape(activeViewCount: 1));
        var views = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            views, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        sink.OnLandscapeViews(views);
        sink.OnInteriorFloodDrawTurn([cellId], outsideViewCount: 1);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);
        Assert.Equal(1, driver.PortalsDrawnCount);
        log.Clear();

        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        sink.OnInteriorFloodDrawTurn([cellId], outsideViewCount: 0);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.DoesNotContain("SKY", log);
        Assert.DoesNotContain("LFLUSH", log);
        Assert.DoesNotContain("CLEAR", log);
        Assert.DoesNotContain("SEALS", log);
        Assert.Equal(1, driver.PortalsDrawnCount);
    }



    [Fact]
    public void LookInDrawCells_NeitherArmsNorConsumesThePortalsDrawnCounter()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var leaf = new RecordingLeafRenderer(log);
        var ctx = new TestContext();
        const uint rootCellId = 0xF4180301u;
        const uint lookInCellId = 0xF4180302u;
        var rootCell = new WalkCell { CellId = rootCellId };
        rootCell.PushView();
        WalkCopyView.AppendFullViewportQuad(
            rootCell.TopView, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        ctx.Cells[rootCellId] = rootCell;
        var lookInCell = new WalkCell { CellId = lookInCellId };
        lookInCell.PushView();
        WalkCopyView.AppendFullViewportQuad(
            lookInCell.TopView, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        ctx.Cells[lookInCellId] = lookInCell;

        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, new FakeWorldData());
        IWalkEventSink sink = driver;

        using DrawScope draw = fx.BeginDraw();

        // Arm PortalsDrawnCount with one throwaway ov>0 interior-root flood.
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        sink.Emit(WalkEvent.Landscape(activeViewCount: 1));
        var rootViews = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            rootViews, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        sink.OnLandscapeViews(rootViews);
        sink.OnInteriorFloodDrawTurn([rootCellId], outsideViewCount: 1);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);
        Assert.Equal(1, driver.PortalsDrawnCount);
        log.Clear();

        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        sink.OnBuildingTurn(new WalkBuilding());
        sink.Emit(WalkEvent.DrawCells(outsideViewCount: 0, [lookInCellId]));
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.DoesNotContain("LFLUSH", log);
        Assert.DoesNotContain("CLEAR", log);
        Assert.DoesNotContain("SEALS", log);
        Assert.Equal(1, driver.PortalsDrawnCount);
    }


    [Fact]
    public void MultipleLookIns_WithinOneFrameAndAcrossFrames_NeverTouchTheRootLatch()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var leaf = new RecordingLeafRenderer(log);
        var ctx = new TestContext();
        const uint rootCellId = 0xF4180330u;
        const uint lookInCellIdA = 0xF4180331u;
        const uint lookInCellIdB = 0xF4180332u;
        var rootCell = new WalkCell { CellId = rootCellId };
        rootCell.PushView();
        WalkCopyView.AppendFullViewportQuad(
            rootCell.TopView, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        ctx.Cells[rootCellId] = rootCell;
        foreach (uint lookInId in new[] { lookInCellIdA, lookInCellIdB })
        {
            var lookInCell = new WalkCell { CellId = lookInId };
            lookInCell.PushView();
            WalkCopyView.AppendFullViewportQuad(
                lookInCell.TopView, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
            ctx.Cells[lookInId] = lookInCell;
        }

        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, new FakeWorldData());
        IWalkEventSink sink = driver;

        using DrawScope draw = fx.BeginDraw();

        // Arm PortalsDrawnCount with one throwaway ov>0 interior-root flood.
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        sink.Emit(WalkEvent.Landscape(activeViewCount: 1));
        var rootViews = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            rootViews, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        sink.OnLandscapeViews(rootViews);
        sink.OnInteriorFloodDrawTurn([rootCellId], outsideViewCount: 1);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);
        Assert.Equal(1, driver.PortalsDrawnCount);
        log.Clear();

        // TWO look-ins in the SAME frame.
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        sink.OnBuildingTurn(new WalkBuilding());
        sink.Emit(WalkEvent.DrawCells(outsideViewCount: 0, [lookInCellIdA]));
        sink.OnBuildingTurn(new WalkBuilding());
        sink.Emit(WalkEvent.DrawCells(outsideViewCount: 0, [lookInCellIdB]));
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.DoesNotContain("LFLUSH", log);
        Assert.DoesNotContain("CLEAR", log);
        Assert.DoesNotContain("SEALS", log);
        Assert.Equal(1, driver.PortalsDrawnCount);
        log.Clear();

        // A THIRD look-in in a LATER, separate frame.
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        sink.OnBuildingTurn(new WalkBuilding());
        sink.Emit(WalkEvent.DrawCells(outsideViewCount: 0, [lookInCellIdA]));
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.DoesNotContain("LFLUSH", log);
        Assert.DoesNotContain("CLEAR", log);
        Assert.DoesNotContain("SEALS", log);
        Assert.Equal(1, driver.PortalsDrawnCount);
    }


    [Fact]
    public void BeginEndFrame_BuildingTurnWithPunchAndLookIn_OrdersAlphaBarrierPortalPassThenShell()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        const ulong shellGfxObj = 0x0200_0010UL;
        const ulong interiorGfxObj = 0x0200_0011UL;
        const ulong dynamicGfxObj = 0x0200_0012UL;
        InjectRenderData(fx.Manager, shellGfxObj, MakeFlatMesh(
            MakeBatch(0x08100010u, TranslucencyKind.Opaque, 0, 0, 3, 1)));
        InjectRenderData(fx.Manager, interiorGfxObj, MakeFlatMesh(
            MakeBatch(0x08100011u, TranslucencyKind.Opaque, 3, 4, 3, 2)));
        InjectRenderData(fx.Manager, dynamicGfxObj, MakeFlatMesh(
            MakeBatch(0x08100012u, TranslucencyKind.Opaque, 6, 8, 3, 3)));

        var ctx = new TestContext();
        var interior = new WalkCell
        {
            CellId = 0x104,
            Portals = [new WalkCellPortal
            {
                OtherCellId = 0xFFFFFFFF, PolygonIndex = 0, PortalSide = 1, OtherPortalId = 0,
            }],
            PortalPolygons = [Quad(-2f)],
        };
        ctx.Cells[interior.CellId] = interior;

        var building = new WalkBuilding
        {
            PositionCellId = 0xA9B4000Fu,
            GfxObjId = (uint)shellGfxObj,
            Portals =
            [
                new WalkBldPortal
                {
                    PortalSide = 0, OtherCellId = 0x104, OtherPortalId = 0,
                    StabList = [0x104u],
                },
            ],
            // Viewpoint (0,0,0) is on the NEGATIVE side of this splitting
            // plane (d=-5): the single PORT node's side==1 arm emits its
            // portal exactly once per pass (WalkBuildingPortals.Walk).
            DrawingBsp = new WalkBspNode
            {
                SplittingPlane = new WalkPlane(new Vector3(1, 0, 0), -5f),
                InPortals = [new WalkPortalRef { PortalIndex = 0, Polygon = Quad(-2f) }],
            },
        };
        ctx.ViewerDistances[building] = 12.5f;

        var worldData = new FakeWorldData();
        worldData.ShellByBuilding[building] = new WalkFrameStaticRecords(
            new[] { MakeRecord(201, 0, Vector3.Zero, [new MeshRef((uint)shellGfxObj, Matrix4x4.Identity)]) }, 0x8C04u);
        worldData.CellStaticsByCell[0x104] = new WalkFrameStaticRecords(
            new[] { MakeRecord(202, 0, Vector3.Zero, [new MeshRef((uint)interiorGfxObj, Matrix4x4.Identity)]) }, 0x8C04u);
        worldData.CellDynamicsByCell[0x104] = new WalkFrameStaticRecords(
            new[] { MakeRecord(203, 0, Vector3.Zero, [new MeshRef((uint)dynamicGfxObj, Matrix4x4.Identity)], parentCellId: 0x104) }, 0x8C04u);
        Matrix4x4 buildingWorld = Matrix4x4.CreateTranslation(10f, 0f, 0f);
        worldData.WorldTransformByBuilding[building] = buildingWorld;

        var leaf = new RecordingLeafRenderer(log);
        var trace = new RecordingTrace(log);
        using ClipFrame clipFrame = ClipFrame.NoClip();
        var driver = new WalkFrameDriver(
            fx.Dispatcher, leaf, worldData, trace, clipFrame);
        var walk = new RetailFrameWalk();

        var activeView = new WalkPortalView();
        activeView.ResetForPush();
        WalkCopyView.AppendFullViewportQuad(
            activeView, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        Assert.Equal(1, activeView.ViewCount);

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        walk.DrawBuilding(building, activeView, ctx, driver);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal(
            new[]
            {
                "ALPHA", "PUNCH:4@v0", "SHELL:00000104",
                "FLUSH:2:LookInStatic,Dynamic",
                "CELL-PARTICLES:00000104",
                "FLUSH:1:BuildingShell",
            },
            log);

        Assert.Equal([0x104u], driver.LookInCellTurns);
        Assert.Equal([0x104u], driver.LookInCells);
        Assert.Collection(
            leaf.Shells,
            shell => Assert.Equal(0x104u, shell));
        WalkPortalView capturedView = ctx.Cells[0x104].PortalViews[0];
        WalkViewPoly capturedPoly = Assert.Single(capturedView.View.Polys);
        Vector2 capturedCenter = Vector2.Zero;
        for (int edge = 0; edge < capturedPoly.VertexCount; edge++)
        {
            capturedCenter += capturedView.View.Vertices[
                capturedPoly.VertexIndex + edge].Point;
        }
        capturedCenter /= capturedPoly.VertexCount;
        Vector3 insideCone = ctx.Rays.RayThrough(capturedCenter.X, capturedCenter.Y);
        Assert.True(driver.SphereVisibleInLookInTurn(
            0, in insideCone, 0.1f));
        Assert.False(driver.SphereVisibleInLookInTurn(
            0, new Vector3(10_000f, 0f, 10f), 0.1f));
        uint clipSlot = driver.LookInSliceClipSlotAt(0);
        Assert.NotEqual(0u, clipSlot);
        Assert.Equal(2, clipFrame.SlotCount);

        WalkPolygon punch = Assert.Single(leaf.Punches);
        Assert.Equal(new Vector3(9.5f, -0.5f, -2f), punch.Vertices[0]);

        List<GpuRecordedMultiDrawIndirect> mdiCalls =
            [.. fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>()];
        Assert.Equal(3, mdiCalls.Count);
        Assert.Equal(3, mdiCalls.Sum(c => (int)c.DrawCount));
    }

    [Fact]
    public void BuildingShellTurnAllowsEmptyRecordButFailsLoudOnDuplicateAnchorRecords()
    {
        using var fx = new DispatcherFixture();
        var building = new WalkBuilding
        {
            PositionCellId = 0xA9B40001u,
            GfxObjId = 0x01000001u,
        };
        var selected = new WalkBuildingSelection(
            building.GfxObjId, null, 0, 1u);
        var world = new FakeWorldData();
        var driver = new WalkFrameDriver(
            fx.Dispatcher, new RecordingLeafRenderer([]), world);
        driver.BeginFrame(new TestContext(), Matrix4x4.Identity, Vector3.Zero);

        ((IWalkEventSink)driver).OnBuildingShellTurn(building, selected);

        RenderProjectionRecord record = MakeRecord(
            301, 0, Vector3.Zero,
            [new MeshRef(building.GfxObjId, Matrix4x4.Identity)],
            isBuildingShell: true);
        world.ShellByBuilding[building] = new WalkFrameStaticRecords(
            new[] { record, record with { Id = RenderProjectionId.FromRaw(302) } },
            0xA9B4u);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ((IWalkEventSink)driver).OnBuildingShellTurn(building, selected));
        Assert.Contains("expected at most one", error.Message, StringComparison.Ordinal);
    }


    [Fact]
    public void OnPunchGeometry_RejectsOnlyWhenEveryVertexSharesOnePlane_ButPunchesAnyOtherShape()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var leaf = new RecordingLeafRenderer(log);
        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, new FakeWorldData());
        IWalkEventSink sink = driver;
        var building = new WalkBuilding { PositionCellId = 0xA9B40040u };

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(new TestContext(), Matrix4x4.Identity, Vector3.Zero);

        sink.OnPunchGeometry(
            building,
            new WalkPolygon
            {
                Vertices = [new(12f, -3f, 3f), new(12f, 0f, 3f), new(12f, 5f, 3f)],
                Plane = new WalkPlane(Vector3.UnitZ, -3f),
            },
            activeViewIndex: 0);

        sink.OnPunchGeometry(
            building,
            new WalkPolygon
            {
                Vertices = [new(0f, 0f, 3f), new(12f, 0f, 3f), new(5f, 5f, 3f)],
                Plane = new WalkPlane(Vector3.UnitZ, -3f),
            },
            activeViewIndex: 0);

        // Admitted: the nearest-boundary vertex is 11.999, not 12 — an
        // ordinary polygon that must punch exactly like any other.
        sink.OnPunchGeometry(
            building,
            new WalkPolygon
            {
                Vertices = [new(0f, 0f, 3f), new(11.999f, 0f, 3f), new(5f, 5f, 3f)],
                Plane = new WalkPlane(Vector3.UnitZ, -3f),
            },
            activeViewIndex: 0);

        sink.OnPunchGeometry(
            building,
            new WalkPolygon
            {
                Vertices = [new(12f, 0f, 3f), new(0f, 12f, 3f), new(12f, 5f, 3f)],
                Plane = new WalkPlane(Vector3.UnitZ, -3f),
            },
            activeViewIndex: 0);

        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        // Exactly THREE punches reached the leaf — the all-on-plane polygon
        // produced no PunchFan event at all (not a punch that draws zero
        // vertices; no event, full stop); the other three (one-vertex-on-
        // plane, just-inside, split-across-two-planes) are ordinary and all
        // punched.
        Assert.Equal(3, leaf.Punches.Count);
        Assert.Equal(new Vector3(12f, 0f, 3f), leaf.Punches[0].Vertices[1]);
        Assert.Equal(new Vector3(11.999f, 0f, 3f), leaf.Punches[1].Vertices[1]);
        Assert.Equal(new Vector3(0f, 12f, 3f), leaf.Punches[2].Vertices[1]);
        Assert.Equal(3, log.Count(entry => entry == "PUNCH:3@v0"));
    }

    [Fact]
    public void RepeatedFloodTurns_DrawEnvCellShellWholeOncePerRetailFrameStamp()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var leaf = new RecordingLeafRenderer(log);
        var ctx = new TestContext();
        const uint cellId = 0xF4180112u;
        var cell = new WalkCell { CellId = cellId };
        cell.PushView();
        WalkCopyView.AppendFullViewportQuad(
            cell.TopView,
            ctx.Rays,
            ctx.WorldViewpoint,
            ctx.ViewportWidth,
            ctx.ViewportHeight);
        ctx.Cells[cellId] = cell;

        var driver = new WalkFrameDriver(
            fx.Dispatcher,
            leaf,
            new FakeWorldData());
        IWalkEventSink sink = driver;

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        sink.OnInteriorFloodDrawTurn([cellId], outsideViewCount: 0);
        sink.OnInteriorFloodDrawTurn([cellId], outsideViewCount: 0);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal([cellId], leaf.Shells);
        Assert.Equal(1, log.Count(entry => entry == "SHELL:f4180112"));
        Assert.Equal(0, log.Count(entry => entry == "LFLUSH"));
        Assert.Equal(0, log.Count(entry => entry == "CLEAR"));
        Assert.Equal(0, log.Count(entry => entry == "SEALS"));
        Assert.Equal(0, driver.PortalsDrawnCount);
    }

    [Fact]
    public void EmitCellContentsTurn_FiresCellParticlesEvenWhenTheCellHasNoStaticOrDynamicRecords()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var leaf = new RecordingLeafRenderer(log);
        var ctx = new TestContext();
        const uint arrivalCellId = 0x8A020141u;
        var arrivalCell = new WalkCell { CellId = arrivalCellId };
        ctx.Cells[arrivalCellId] = arrivalCell;

        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, new FakeWorldData());
        IWalkEventSink sink = driver;

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        // No landscape turn modeled here — ov==0.
        sink.OnInteriorFloodDrawTurn([arrivalCellId], outsideViewCount: 0);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Contains("CELL-PARTICLES:8a020141", log);
    }

    [Fact]
    public void LandscapeStampBoundary_RearmsWholeShellForPostClearRootRepaint()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var ctx = new TestContext();
        const uint cellId = 0xF4180112u;
        var cell = new WalkCell { CellId = cellId };
        cell.PushView();
        WalkCopyView.AppendFullViewportQuad(
            cell.TopView,
            ctx.Rays,
            ctx.WorldViewpoint,
            ctx.ViewportWidth,
            ctx.ViewportHeight);
        ctx.Cells[cellId] = cell;

        var driver = new WalkFrameDriver(
            fx.Dispatcher,
            new RecordingLeafRenderer(new List<string>()),
            new FakeWorldData());
        IWalkEventSink sink = driver;

        using DrawScope draw = fx.BeginDraw();

        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        sink.Emit(WalkEvent.Landscape(activeViewCount: 1));
        var primerViews = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            primerViews, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        sink.OnLandscapeViews(primerViews);
        sink.OnInteriorFloodDrawTurn([cellId], outsideViewCount: 1);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);
        Assert.Equal(1, driver.PortalsDrawnCount);

        var leaf = new RecordingLeafRenderer(log);
        driver.RebindFrame(leaf, clipFrame: null);
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);

        sink.Emit(WalkEvent.Landscape(activeViewCount: 1));
        var landscapeViews = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            landscapeViews,
            ctx.Rays,
            ctx.WorldViewpoint,
            ctx.ViewportWidth,
            ctx.ViewportHeight);
        sink.OnLandscapeViews(landscapeViews);
        sink.OnBuildingTurn(new WalkBuilding());
        sink.Emit(WalkEvent.DrawCells(outsideViewCount: 0, [cellId]));

        sink.OnInteriorFloodDrawTurn([cellId], outsideViewCount: 1);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal([cellId, cellId], leaf.Shells);
        Assert.Equal(2, log.Count(entry => entry == "SHELL:f4180112"));
        Assert.Contains("CLEAR", log);
        Assert.True(
            log.IndexOf("SHELL:f4180112") < log.IndexOf("CLEAR"),
            "The look-in shell must precede the interior clear.");
        Assert.True(
            log.LastIndexOf("SHELL:f4180112") > log.IndexOf("SEALS"),
            "The rearmed root shell must repaint after the clear and seals.");
    }

    // ── Fail-loud: a DrawCells turn with no preceding DrawInside/Building
    // turn is a walk/driver desync, not a silent skip. ─────────────────────

    [Fact]
    public void Emit_DrawCellsBeforeAnyDrawInsideOrBuildingTurn_ThrowsRatherThanSilentlyDropping()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var ctx = new TestContext();
        var driver = new WalkFrameDriver(fx.Dispatcher, new RecordingLeafRenderer(log), new FakeWorldData());

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);

        Assert.Throws<InvalidOperationException>(
            () => ((IWalkEventSink)driver).Emit(WalkEvent.DrawCells(0, [0x100u])));
    }

    // ── Fail-loud: BeginFrame is not re-entrant. ────────────────────────────

    [Fact]
    public void BeginFrame_CalledWhileAFrameIsAlreadyOpen_Throws()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var ctx = new TestContext();
        var driver = new WalkFrameDriver(fx.Dispatcher, new RecordingLeafRenderer(log), new FakeWorldData());

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);

        Assert.Throws<InvalidOperationException>(
            () => driver.BeginFrame(
                ctx, Matrix4x4.Identity, Vector3.Zero));

        driver.EndFrame();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        driver.EndFrame();
    }


    [Fact]
    public void Replay_WithNoPrecedingCollect_Throws()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var driver = new WalkFrameDriver(fx.Dispatcher, new RecordingLeafRenderer(log), new FakeWorldData());

        using DrawScope draw = fx.BeginDraw();
        Assert.Throws<InvalidOperationException>(() => driver.Replay(draw.Frame, draw.Pass));
    }

    [Fact]
    public void Replay_WhileCollectIsStillOpen_Throws()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var ctx = new TestContext();
        var driver = new WalkFrameDriver(fx.Dispatcher, new RecordingLeafRenderer(log), new FakeWorldData());

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);

        Assert.Throws<InvalidOperationException>(() => driver.Replay(draw.Frame, draw.Pass));
    }


    [Fact]
    public void CollectThenReplay_AsSeparateCalls_PerformsNoGpuWorkUntilReplay()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var ctx = new TestContext();
        var driver = new WalkFrameDriver(
            fx.Dispatcher, new RecordingLeafRenderer(log), new FakeWorldData(), new RecordingTrace(log));
        var walk = new RetailFrameWalk();
        var landscape = new WalkLandscape { MidWidth = 1, Blocks = new WalkLandBlock?[1] };

        using DrawScope draw = fx.BeginDraw();
        driver.Collect(
            walk, cameraCellId: 0u, cameraCell: null, landscape, ctx,
            Matrix4x4.Identity, cameraWorldPosition: Vector3.Zero);

        Assert.Empty(log);
        Assert.Empty(fx.Device.Calls);

        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal(new[] { "SKY" }, log);
    }

    [Fact]
    public void RebindFrame_ReusesTheDriverAndRoutesTheNextFrameToTheNewLeaf()
    {
        using var fx = new DispatcherFixture();
        var firstLog = new List<string>();
        var secondLog = new List<string>();
        var ctx = new TestContext();
        var driver = new WalkFrameDriver(
            fx.Dispatcher, new RecordingLeafRenderer(firstLog), new FakeWorldData());
        var walk = new RetailFrameWalk();
        var landscape = new WalkLandscape { MidWidth = 1, Blocks = new WalkLandBlock?[1] };

        using DrawScope draw = fx.BeginDraw();
        driver.Collect(
            walk, 0u, null, landscape, ctx,
            Matrix4x4.Identity, Vector3.Zero);
        driver.Replay(draw.Frame, draw.Pass);

        driver.RebindFrame(new RecordingLeafRenderer(secondLog), clipFrame: null);
        driver.Collect(
            walk, 0u, null, landscape, ctx,
            Matrix4x4.Identity, Vector3.Zero);
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal(new[] { "SKY" }, firstLog);
        Assert.Equal(new[] { "SKY" }, secondLog);
    }

    [Fact]
    public void RebindFrame_DiscardsAnIncompletePriorFrameAndRecovers()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var ctx = new TestContext();
        var driver = new WalkFrameDriver(
            fx.Dispatcher, new RecordingLeafRenderer(new List<string>()), new FakeWorldData());
        var walk = new RetailFrameWalk();
        var landscape = new WalkLandscape { MidWidth = 1, Blocks = new WalkLandBlock?[1] };

        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        driver.RebindFrame(new RecordingLeafRenderer(log), clipFrame: null);

        using DrawScope draw = fx.BeginDraw();
        driver.Collect(
            walk, 0u, null, landscape, ctx,
            Matrix4x4.Identity, Vector3.Zero);
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal(new[] { "SKY" }, log);
    }


    [Fact]
    public void OnLandscapeCellTurn_AppendsOutdoorStaticsWithNoShellCallAndFlushesAtFrameEnd()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        const ulong gfxObj = 0x0200_0020UL;
        InjectRenderData(fx.Manager, gfxObj, MakeFlatMesh(
            MakeBatch(0x08100020u, TranslucencyKind.Opaque, 0, 0, 3, 1)));

        var ctx = new TestContext();
        var worldData = new FakeWorldData();
        worldData.OutdoorStaticsByCell[0x8C040005u] = new WalkFrameStaticRecords(
            new[] { MakeRecord(301, 0, Vector3.Zero, [new MeshRef((uint)gfxObj, Matrix4x4.Identity)]) }, 0x8C04u);
        worldData.OutdoorDynamicsByCell[0x8C040005u] = new WalkFrameStaticRecords(
            new[] { MakeRecord(302, 0x50000001, Vector3.Zero, [new MeshRef((uint)gfxObj, Matrix4x4.Identity)]) }, 0x8C04u);

        var driver = new WalkFrameDriver(
            fx.Dispatcher, new RecordingLeafRenderer(log), worldData, new RecordingTrace(log));

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        var activeViews = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            activeViews,
            ctx.Rays,
            ctx.WorldViewpoint,
            ctx.ViewportWidth,
            ctx.ViewportHeight);
        ((IWalkEventSink)driver).OnLandscapeViews(activeViews);
        ((IWalkEventSink)driver).OnLandscapeCellTurn(0x8C040005u);
        Assert.Empty(log);
        driver.EndFrame();
        Assert.Empty(log);
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal(
            new[] { "FLUSH:2:OutdoorStatic,Dynamic", "PARTICLES:8c040005" },
            log);
        var visibleCells = new HashSet<uint> { 0xDEAD_BEEFu };
        driver.CopyVisibleCellsTo(visibleCells);
        Assert.Equal(new[] { 0x8C040005u }, visibleCells);
        GpuRecordedMultiDrawIndirect[] mdi =
            fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>().ToArray();
        Assert.Equal(2, mdi.Length);
        Assert.Equal(2u, mdi.Aggregate(0u, static (sum, call) => sum + call.DrawCount));
    }

    [Fact]
    public void OnLandscapeCellTurn_MultiCellShadowAlias_DrawsMeshOnceButParticlesPerVisitedCell()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        const ulong gfxObj = 0x0200_0021UL;
        InjectRenderData(fx.Manager, gfxObj, MakeFlatMesh(
            MakeBatch(0x08100021u, TranslucencyKind.Opaque, 0, 0, 3, 1)));

        var ctx = new TestContext();
        var worldData = new FakeWorldData();
        RenderProjectionRecord shadowAlias = MakeRecord(
            303,
            0x50000002,
            Vector3.Zero,
            [new MeshRef((uint)gfxObj, Matrix4x4.Identity)]);
        WalkFrameStaticRecords aliases = new(
            new[] { shadowAlias },
            0xF07Fu);
        worldData.OutdoorDynamicsByCell[0xF07F0040u] = aliases;
        worldData.OutdoorDynamicsByCell[0xF0800001u] = aliases;

        var driver = new WalkFrameDriver(
            fx.Dispatcher,
            new RecordingLeafRenderer(log),
            worldData,
            new RecordingTrace(log));

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        var activeViews = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            activeViews,
            ctx.Rays,
            ctx.WorldViewpoint,
            ctx.ViewportWidth,
            ctx.ViewportHeight);
        ((IWalkEventSink)driver).OnLandscapeViews(activeViews);
        ((IWalkEventSink)driver).OnLandscapeCellTurn(0xF07F0040u);
        ((IWalkEventSink)driver).OnLandscapeCellTurn(0xF0800001u);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal(
            new[] { "FLUSH:1:Dynamic", "PARTICLES:f07f0040", "PARTICLES:f0800001" },
            log);
        GpuRecordedMultiDrawIndirect[] mdi =
            fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>().ToArray();
        Assert.Single(mdi);
        Assert.Equal(1u, mdi[0].DrawCount);
    }

    [Fact]
    public void FarTierSingleCellLandscapeTurn_DrawsNoObjects()
    {
        const int sideCellCount = 1;
        const int cellIndex = 0;
        using var fx = new DispatcherFixture();
        const ulong gfxObj = 0x0200_0022UL;
        InjectRenderData(fx.Manager, gfxObj, MakeFlatMesh(
            MakeBatch(0x08100022u, TranslucencyKind.Opaque, 0, 0, 3, 1)));

        var worldData = new FakeWorldData();
        for (uint cell = 1; cell <= 64; cell++)
        {
            worldData.OutdoorStaticsByCell[0xE43D0000u | cell] = new WalkFrameStaticRecords(
                new[] { MakeRecord(300u + cell, 0, Vector3.Zero, [new MeshRef((uint)gfxObj, Matrix4x4.Identity)]) },
                0xE43Du);
        }
        var driver = new WalkFrameDriver(
            fx.Dispatcher,
            new RecordingLeafRenderer(new List<string>()),
            worldData);
        var ctx = new TestContext();

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        var activeViews = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            activeViews,
            ctx.Rays,
            ctx.WorldViewpoint,
            ctx.ViewportWidth,
            ctx.ViewportHeight);
        ((IWalkEventSink)driver).OnLandscapeViews(activeViews);
        ((IWalkEventSink)driver).OnLandscapeCellTurn(0xE43DFFFFu, sideCellCount, cellIndex);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Empty(driver.VisitedLandscapeCellIds);
        Assert.Empty(fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
    }

    [Theory]
    [InlineData(2, 3, 4, 7)]
    [InlineData(4, 15, 6, 7)]
    public void NearTierCoarseLandscapeTurn_DrawsEveryCoveredOwnerCell(
        int sideCellCount,
        int cellIndex,
        int firstXy,
        int lastXy)
    {
        using var fx = new DispatcherFixture();
        const ulong gfxObj = 0x0200_0025UL;
        InjectRenderData(fx.Manager, gfxObj, MakeFlatMesh(
            MakeBatch(0x08100025u, TranslucencyKind.Opaque, 0, 0, 3, 1)));

        var worldData = new FakeWorldData();
        for (uint cell = 1; cell <= 64; cell++)
        {
            worldData.OutdoorStaticsByCell[0xE43D0000u | cell] = new WalkFrameStaticRecords(
                new[] { MakeRecord(400u + cell, 0, Vector3.Zero, [new MeshRef((uint)gfxObj, Matrix4x4.Identity)]) },
                0xE43Du);
        }
        var driver = new WalkFrameDriver(
            fx.Dispatcher,
            new RecordingLeafRenderer(new List<string>()),
            worldData);
        var ctx = new TestContext();

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        var activeViews = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            activeViews,
            ctx.Rays,
            ctx.WorldViewpoint,
            ctx.ViewportWidth,
            ctx.ViewportHeight);
        ((IWalkEventSink)driver).OnLandscapeViews(activeViews);
        ((IWalkEventSink)driver).OnLandscapeCellTurn(0xE43DFFFFu, sideCellCount, cellIndex);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        var expected = new HashSet<uint>();
        for (int x = firstXy; x <= lastXy; x++)
        for (int y = firstXy; y <= lastXy; y++)
            expected.Add(0xE43D0000u | (uint)(x * 8 + y + 1));
        Assert.Equal(expected, driver.VisitedLandscapeCellIds.ToHashSet());
        uint drawn = 0;
        foreach (GpuRecordedMultiDrawIndirect call in fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>())
            drawn += call.DrawCount;
        Assert.Equal((uint)expected.Count, drawn);
    }

    [Fact]
    public void FullDetailLandscapeCellTurn_DrawsThatCellsObjects()
    {
        using var fx = new DispatcherFixture();
        const ulong gfxObj = 0x0200_0024UL;
        InjectRenderData(fx.Manager, gfxObj, MakeFlatMesh(
            MakeBatch(0x08100024u, TranslucencyKind.Opaque, 0, 0, 3, 1)));

        var worldData = new FakeWorldData();
        worldData.OutdoorStaticsByCell[0xE43D0040u] = new WalkFrameStaticRecords(
            new[] { MakeRecord(304, 0, Vector3.Zero, [new MeshRef((uint)gfxObj, Matrix4x4.Identity)]) },
            0xE43Du);
        var driver = new WalkFrameDriver(
            fx.Dispatcher,
            new RecordingLeafRenderer(new List<string>()),
            worldData);
        var ctx = new TestContext();

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        var activeViews = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            activeViews,
            ctx.Rays,
            ctx.WorldViewpoint,
            ctx.ViewportWidth,
            ctx.ViewportHeight);
        ((IWalkEventSink)driver).OnLandscapeViews(activeViews);
        ((IWalkEventSink)driver).OnLandscapeCellTurn(0xE43DFFFFu, 8, 63);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Contains(0xE43D0040u, driver.VisitedLandscapeCellIds);
        GpuRecordedMultiDrawIndirect call = Assert.Single(
            fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
        Assert.Equal(1u, call.DrawCount);
    }

    [Fact]
    public void Collect_DoesNotExposeNearAlphaToEarlierBuildingBarrier()
    {
        using var fx = new DispatcherFixture();
        const ulong gfxObj = 0x0200_0023UL;
        InjectRenderData(fx.Manager, gfxObj, MakeFlatMesh(
            MakeBatch(0x08100023u, TranslucencyKind.AlphaBlend, 0, 0, 3, 1)));

        var worldData = new FakeWorldData();
        worldData.OutdoorStaticsByCell[0xE43D0001u] = new WalkFrameStaticRecords(
            new[] { MakeRecord(305, 0, new Vector3(0, 0, -20), [new MeshRef((uint)gfxObj, Matrix4x4.Identity)]) },
            0xE43Du);
        worldData.OutdoorStaticsByCell[0xE43D0002u] = new WalkFrameStaticRecords(
            new[] { MakeRecord(306, 0, new Vector3(0, 0, -2), [new MeshRef((uint)gfxObj, Matrix4x4.Identity)]) },
            0xE43Du);
        var log = new List<string>();
        var leaf = new RecordingLeafRenderer(log, fx.AlphaQueue);
        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, worldData);
        var ctx = new TestContext();

        using DrawScope draw = fx.BeginDraw(beginAlpha: true);
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        var activeViews = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            activeViews,
            ctx.Rays,
            ctx.WorldViewpoint,
            ctx.ViewportWidth,
            ctx.ViewportHeight);
        ((IWalkEventSink)driver).OnLandscapeViews(activeViews);
        ((IWalkEventSink)driver).OnLandscapeCellTurn(0xE43D0001u);
        ((IWalkEventSink)driver).OnBuildingTurn(new WalkBuilding());
        ((IWalkEventSink)driver).OnLandscapeCellTurn(0xE43D0002u);
        driver.EndFrame();

        Assert.Equal(0, fx.AlphaQueue.PendingCount);
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal(new[] { 1 }, leaf.AlphaPendingAtBarrier);
        Assert.Equal(1, fx.AlphaQueue.PendingCount);
    }


    internal static IEnumerable<uint> CoarseLandscapeBuckets(uint landblockPrefix)
    {
        for (int x = 0; x < 8; x++)
        for (int y = 0; y < 8; y++)
            yield return landblockPrefix | (uint)(x * 8 + y + 1);
    }

    internal static WalkPortalView OneDegenerateView()
    {
        var view = new WalkPortalView { ViewCount = 1 };
        view.View.Polys.Add(new WalkViewPoly(0, 0, 0, 0, 0, 0));
        return view;
    }

    [Fact]
    public void OutdoorRoot_FullDetailLandCellPrecedesItsOwnCellsObjectTurn_ThenFlushesAtReplaysEnd()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var leaf = new RecordingLeafRenderer(log);
        leaf.CellsWithoutEmitters.UnionWith(CoarseLandscapeBuckets(0xF4180000u));
        leaf.CellsWithoutEmitters.Remove(0xF4180001u);
        var ctx = new TestContext();
        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, new FakeWorldData());
        var walk = new RetailFrameWalk();
        var landscape = new WalkLandscape { MidWidth = 1, Blocks = new WalkLandBlock?[1] };
        var block = new WalkLandBlock
        {
            LandblockId = 0xF4180000u, SideCellCount = 8, MaxZ = 10f, MinZ = 0f,
        };
        block.EnsureCellArrays();
        landscape.Blocks[0] = block;

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        walk.DrawLandscape(landscape, OneDegenerateView(), ctx, driver);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal("SKY", log[0]);
        int particles = log.IndexOf("PARTICLES:f4180001");
        Assert.True(particles > 0, "cell 0's object turn never drew its particles");
        Assert.Equal("LANDCELL:f4180000:8:0", log[particles - 1]);
        Assert.Equal("SORTCELLEXIT", log[particles + 1]);
        Assert.Equal(1, log.Count(line => line.StartsWith("PARTICLES:", StringComparison.Ordinal)));
        Assert.Equal(64, log.Count(line => line == "SORTCELLEXIT"));
    }

    [Theory]
    [InlineData(2, 2, true)]   // ring 2 within a limit of 2: objects draw
    [InlineData(3, 2, false)]  // ring 3 past the limit: terrain and valve only
    [InlineData(4, 4, true)]   // the High preset's near tier
    public void OutdoorRoot_NearTierCoarseBlock_DrawsObjectsOnlyWithinObjectRingLimit(
        int ring, int limit, bool expectObjects)
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var leaf = new RecordingLeafRenderer(log);
        leaf.CellsWithoutEmitters.UnionWith(CoarseLandscapeBuckets(0xF4180000u));
        leaf.CellsWithoutEmitters.Remove(0xF4180001u);
        var ctx = new TestContext();
        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, new FakeWorldData());
        var walk = new RetailFrameWalk { ObjectRingLimit = limit };
        var landscape = new WalkLandscape { MidWidth = 1, Blocks = new WalkLandBlock?[1] };
        var block = new WalkLandBlock
        {
            LandblockId = 0xF4180000u, SideCellCount = 2, Ring = ring, MaxZ = 10f, MinZ = 0f,
        };
        block.EnsureCellArrays();
        landscape.Blocks[0] = block;

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        walk.DrawLandscape(landscape, OneDegenerateView(), ctx, driver);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal(4, log.Count(line => line == "SORTCELLEXIT"));
        Assert.Equal(4, log.Count(line => line.StartsWith("LANDCELL:", StringComparison.Ordinal)));
        Assert.Equal(expectObjects, log.Contains("PARTICLES:f4180001"));
        Assert.Equal(expectObjects ? 64 : 0, driver.VisitedLandscapeCellIds.Count);
    }

    [Fact]
    public void OutdoorRoot_CoarseLandCell_HasNoObjectTurn_ButStillFlushesAtItsExit()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var leaf = new RecordingLeafRenderer(log);
        leaf.CellsWithoutEmitters.UnionWith(CoarseLandscapeBuckets(0xF4180000u));
        leaf.CellsWithoutEmitters.Remove(0xF4180001u);
        var ctx = new TestContext();
        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, new FakeWorldData());
        var walk = new RetailFrameWalk();
        var landscape = new WalkLandscape { MidWidth = 1, Blocks = new WalkLandBlock?[1] };
        var block = new WalkLandBlock
        {
            LandblockId = 0xF4180000u, SideCellCount = 1, MaxZ = 10f, MinZ = 0f,
        };
        block.EnsureCellArrays();
        landscape.Blocks[0] = block;

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        walk.DrawLandscape(landscape, OneDegenerateView(), ctx, driver);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal(new[] { "SKY", "LANDCELL:f4180000:1:0", "SORTCELLEXIT" }, log);
        Assert.Equal(new (uint LandblockId, int SideCellCount, int CellIndex)[] { (0xF4180000u, 1, 0) },
            Assert.Single(leaf.LandCellBatches));
        Assert.Empty(driver.VisitedLandscapeCellIds);
    }

    [Theory]
    [InlineData(2250, 0)]
    [InlineData(2249, 2249)]
    public void SortCellExit_ValveDrainsThroughReplayAtTheExactBoundary(
        int preloadedCount, int expectedPendingAfter)
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var leaf = new RecordingLeafRenderer(log, fx.AlphaQueue);
        leaf.CellsWithoutEmitters.UnionWith(CoarseLandscapeBuckets(0xF4180000u));
        var ctx = new TestContext();
        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, new FakeWorldData());
        var walk = new RetailFrameWalk();
        var landscape = new WalkLandscape { MidWidth = 1, Blocks = new WalkLandBlock?[1] };
        var block = new WalkLandBlock
        {
            LandblockId = 0xF4180000u, SideCellCount = 1, MaxZ = 10f, MinZ = 0f,
        };
        block.EnsureCellArrays();
        landscape.Blocks[0] = block;
        var dummySource = new DummyAlphaSource();

        using DrawScope draw = fx.BeginDraw(beginAlpha: true);
        for (int i = 0; i < preloadedCount; i++)
        {
            Assert.True(fx.AlphaQueue.TryAppend(RetailAlphaList.Alpha, dummySource, i, false));
        }

        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        walk.DrawLandscape(landscape, OneDegenerateView(), ctx, driver);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal(new[] { preloadedCount }, leaf.AlphaPendingAtSortCellExit);
        Assert.Equal(expectedPendingAfter, fx.AlphaQueue.PendingCount);
    }

    private sealed class DummyAlphaSource : IRetailAlphaDrawSource
    {
        public void PrepareAlphaDraws(ReadOnlySpan<int> tokens)
        {
        }

        public void DrawPreparedAlphaBatch(int firstPreparedDraw, int drawCount)
        {
        }

        public void ResetAlphaSubmissions()
        {
        }
    }

    [Fact]
    public void CellTurn_RealParticlePreparationMergesWithObjectAlphaByCypt()
    {
        using var fx = new DispatcherFixture();
        const uint cellId = 0x8C040005u;
        const ulong objectGfx = 0x0200_0C31UL;
        InjectRenderData(fx.Manager, objectGfx, MakeFlatMesh(
            MakeBatch(0x08100C31u, TranslucencyKind.AlphaBlend, 0, 0, 3, 1)));
        var worldData = new FakeWorldData();
        worldData.OutdoorStaticsByCell[cellId] = new WalkFrameStaticRecords(
            new[]
            {
                MakeRecord(1, 0, new Vector3(20, 0, 0),
                    [new MeshRef((uint)objectGfx, Matrix4x4.Identity)]),
                MakeRecord(2, 0, new Vector3(40, 0, 0),
                    [new MeshRef((uint)objectGfx, Matrix4x4.Identity)]),
            },
            0x8C04u);

        var particles = new ParticleSystem(new EmitterDescRegistry(), new Random(42));
        EmitterDesc desc = new()
        {
            DatId = 0x32000C31u,
            Type = AcDream.Core.Vfx.ParticleType.Still,
            MaxParticles = 1,
            InitialParticles = 1,
            LifetimeMin = 100f,
            LifetimeMax = 100f,
            StartAlpha = 1f,
            EndAlpha = 1f,
        };
        int particle30 = particles.SpawnEmitter(desc, new Vector3(30, 0, 0));
        int particle20 = particles.SpawnEmitter(desc, new Vector3(20, 0, 0));
        particles.UpdateEmitterOwnerCell(particle30, cellId);
        particles.UpdateEmitterOwnerCell(particle20, cellId);
        using var renderer = new ParticleRenderer(
            fx.Device,
            fx.FrameLifetime,
            fx.Scope,
            particles,
            meshAdapter: fx.MeshAdapter,
            alphaQueue: fx.AlphaQueue);
        var leaf = new ProductionParticleLeaf(
            particles, renderer, new IdentityCamera(), Vector3.Zero);
        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, worldData);
        var ctx = new TestContext();
        IWalkEventSink sink = driver;

        using DrawScope draw = fx.BeginDraw(beginAlpha: true);
        renderer.BeginFrame(frameSlot: 0);
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        var activeViews = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            activeViews, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        sink.OnLandscapeViews(activeViews);
        sink.OnLandscapeCellTurn(cellId);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        List<RetailAlphaEntry> entries = QueueAlphaEntries(fx.AlphaQueue);
        Assert.Equal(4, entries.Count);
        Assert.Equal(
            [
                typeof(WbDrawDispatcher),
                typeof(ParticleRenderer),
                typeof(WbDrawDispatcher),
                typeof(ParticleRenderer),
            ],
            entries.Select(static entry => entry.Source.GetType().DeclaringType));

        fx.AlphaQueue.AbortFrame();
    }

    [Fact]
    public void CellTurn_GfxObjBillboardUsesScaledOrientedAuthoredSortCenterButKeepsVisualCenter()
    {
        using var fx = new DispatcherFixture();
        const uint cellId = 0x8C0400A1u;
        const uint objectGfx = 0x02000CA1u;
        const uint particleGfx = 0x01000CA1u;
        const uint particleSurface = 0x08000CA1u;
        const uint degradeId = 0x11000CA1u;
        InjectRenderData(fx.Manager, objectGfx, MakeFlatMesh(
            MakeBatch(0x08100CA1u, TranslucencyKind.AlphaBlend, 0, 0, 3, 1)));
        var worldData = new FakeWorldData();
        worldData.OutdoorStaticsByCell[cellId] = new WalkFrameStaticRecords(
            new[] { MakeRecord(1, 0, new Vector3(7, 0, 0),
                [new MeshRef(objectGfx, Matrix4x4.Identity)]) },
            0x8C04u);

        var gfx = new GfxObj
        {
            Id = particleGfx,
            Flags = GfxObjFlags.HasDIDDegrade,
            DIDDegrade = degradeId,
            SortCenter = new Vector3(1, 2, 0),
            Surfaces = { particleSurface },
            VertexArray = new VertexArray
            {
                Vertices =
                {
                    [0] = new SWVertex { Origin = new Vector3(1, 0, -1) },
                    [1] = new SWVertex { Origin = new Vector3(3, 0, 1) },
                },
            },
        };
        var degrade = new GfxObjDegradeInfo
        {
            Id = degradeId,
            Degrades =
            {
                new GfxObjInfo
                {
                    Id = particleGfx,
                    DegradeMode = 2u,
                    MaxDist = float.MaxValue,
                },
            },
        };
        var surface = new Surface
        {
            Id = particleSurface,
            Type = SurfaceType.Base1Solid,
            ColorValue = new DatReaderWriter.Types.ColorARGB
            {
                Alpha = 255,
                Red = 255,
                Green = 255,
                Blue = 255,
            },
        };
        using var dats = new NoopDatReaderWriter();
        dats.Add(particleGfx, gfx);
        dats.Add(degradeId, degrade);
        dats.Add(particleSurface, surface);
        using var textures = new TextureCache(fx.Device, dats);

        var particles = new ParticleSystem(new EmitterDescRegistry(), new Random(42));
        Quaternion orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        int handle = particles.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000CA1u,
                Type = AcDream.Core.Vfx.ParticleType.Still,
                GfxObjId = particleGfx,
                MaxParticles = 1,
                InitialParticles = 1,
                LifetimeMin = 100f,
                LifetimeMax = 100f,
                StartAlpha = 1f,
                EndAlpha = 1f,
                StartSize = 2f,
                EndSize = 2f,
                Gravity = Vector3.Zero,
            },
            new Vector3(10, 0, 0),
            orientation);
        particles.UpdateEmitterOwnerCell(handle, cellId);
        using var renderer = new ParticleRenderer(
            fx.Device,
            fx.FrameLifetime,
            fx.Scope,
            particles,
            textures,
            dats,
            fx.MeshAdapter,
            fx.AlphaQueue);
        var leaf = new ProductionParticleLeaf(
            particles, renderer, new IdentityCamera(), Vector3.Zero);
        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, worldData);
        var ctx = new TestContext();
        IWalkEventSink sink = driver;

        using DrawScope draw = fx.BeginDraw(beginAlpha: true);
        renderer.BeginFrame(frameSlot: 0);
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        var activeViews = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            activeViews, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        sink.OnLandscapeViews(activeViews);
        sink.OnLandscapeCellTurn(cellId);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        IList payload = DeferredParticlePayload(renderer);
        object deferred = Assert.Single(payload.Cast<object>());
        object billboard = deferred.GetType().GetProperty("Billboard")!.GetValue(deferred)!;
        object instance = billboard.GetType().GetProperty("Instance")!.GetValue(billboard)!;
        float authoredDistanceSq = (float)instance.GetType().GetField("DistanceSq")!.GetValue(instance)!;
        Vector3 visualCenter = (Vector3)instance.GetType().GetField("Position")!.GetValue(instance)!;
        Assert.Equal(40f, authoredDistanceSq, precision: 4);
        Assert.True(
            Vector3.Distance(new Vector3(10, 4, 0), visualCenter) < 1e-4f,
            $"Visual center was {visualCenter}, expected <10, 4, 0>.");
        Assert.NotEqual(new Vector3(6, 2, 0), visualCenter);
        Assert.Equal(
            [typeof(WbDrawDispatcher), typeof(ParticleRenderer)],
            QueueAlphaEntries(fx.AlphaQueue)
                .Select(static entry => entry.Source.GetType().DeclaringType));

        fx.AlphaQueue.AbortFrame();
    }

    [Fact]
    public void CellTurn_GfxObjBillboardWithoutSurfaceRetainsAuthoredSortCenterAndUntexturedFallback()
    {
        using var fx = new DispatcherFixture();
        const uint cellId = 0x8C0400A2u;
        const uint objectGfx = 0x02000CA2u;
        const uint particleGfx = 0x01000CA2u;
        const uint degradeId = 0x11000CA2u;
        InjectRenderData(fx.Manager, objectGfx, MakeFlatMesh(
            MakeBatch(0x08100CA2u, TranslucencyKind.AlphaBlend, 0, 0, 3, 1)));
        var worldData = new FakeWorldData();
        worldData.OutdoorStaticsByCell[cellId] = new WalkFrameStaticRecords(
            new[] { MakeRecord(1, 0, new Vector3(7, 0, 0),
                [new MeshRef(objectGfx, Matrix4x4.Identity)]) },
            0x8C04u);

        var gfx = new GfxObj
        {
            Id = particleGfx,
            Flags = GfxObjFlags.HasDIDDegrade,
            DIDDegrade = degradeId,
            SortCenter = new Vector3(1, 2, 0),
            VertexArray = new VertexArray
            {
                Vertices =
                {
                    [0] = new SWVertex { Origin = new Vector3(1, 0, -1) },
                    [1] = new SWVertex { Origin = new Vector3(3, 0, 1) },
                },
            },
        };
        var degrade = new GfxObjDegradeInfo
        {
            Id = degradeId,
            Degrades =
            {
                new GfxObjInfo
                {
                    Id = particleGfx,
                    DegradeMode = 2u,
                    MaxDist = float.MaxValue,
                },
            },
        };
        using var dats = new NoopDatReaderWriter();
        dats.Add(particleGfx, gfx);
        dats.Add(degradeId, degrade);
        using var textures = new TextureCache(fx.Device, dats);

        var particles = new ParticleSystem(new EmitterDescRegistry(), new Random(42));
        Quaternion orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        int handle = particles.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000CA2u,
                Type = AcDream.Core.Vfx.ParticleType.Still,
                GfxObjId = particleGfx,
                MaxParticles = 1,
                InitialParticles = 1,
                LifetimeMin = 100f,
                LifetimeMax = 100f,
                StartAlpha = 1f,
                EndAlpha = 1f,
                StartSize = 2f,
                EndSize = 2f,
                Gravity = Vector3.Zero,
            },
            new Vector3(10, 0, 0),
            orientation);
        particles.UpdateEmitterOwnerCell(handle, cellId);
        using var renderer = new ParticleRenderer(
            fx.Device,
            fx.FrameLifetime,
            fx.Scope,
            particles,
            textures,
            dats,
            fx.MeshAdapter,
            fx.AlphaQueue);
        var leaf = new ProductionParticleLeaf(
            particles, renderer, new IdentityCamera(), Vector3.Zero);
        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, worldData);
        var ctx = new TestContext();
        IWalkEventSink sink = driver;

        using DrawScope draw = fx.BeginDraw(beginAlpha: true);
        renderer.BeginFrame(frameSlot: 0);
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        var activeViews = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            activeViews, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        sink.OnLandscapeViews(activeViews);
        sink.OnLandscapeCellTurn(cellId);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        IList payload = DeferredParticlePayload(renderer);
        object deferred = Assert.Single(payload.Cast<object>());
        object billboard = deferred.GetType().GetProperty("Billboard")!.GetValue(deferred)!;
        object instance = billboard.GetType().GetProperty("Instance")!.GetValue(billboard)!;
        float authoredDistanceSq = (float)instance.GetType().GetField("DistanceSq")!.GetValue(instance)!;
        Assert.Equal(40f, authoredDistanceSq, precision: 4);
        Assert.Equal(
            [typeof(WbDrawDispatcher), typeof(ParticleRenderer)],
            QueueAlphaEntries(fx.AlphaQueue)
                .Select(static entry => entry.Source.GetType().DeclaringType));

        Vector3 visualCenter = (Vector3)instance.GetType().GetField("Position")!.GetValue(instance)!;
        GpuTextureSlot textureSlot = (GpuTextureSlot)instance.GetType().GetField("TextureSlot")!
            .GetValue(instance)!;
        Assert.True(
            Vector3.Distance(new Vector3(10, 4, 0), visualCenter) < 1e-4f,
            $"Visual center was {visualCenter}, expected <10, 4, 0>.");
        Assert.False(textureSlot.IsAssigned);

        fx.AlphaQueue.AbortFrame();
    }

    [Fact]
    public void SeparateCellTurnsRemainCellMajorWhenLaterCellIsFarther()
    {
        using var fx = new DispatcherFixture();
        const uint firstCell = 0x8C040005u;
        const uint secondCell = 0x8C040006u;
        const ulong objectGfx = 0x0200_0C32UL;
        InjectRenderData(fx.Manager, objectGfx, MakeFlatMesh(
            MakeBatch(0x08100C32u, TranslucencyKind.AlphaBlend, 0, 0, 3, 1)));
        var worldData = new FakeWorldData();
        worldData.OutdoorStaticsByCell[firstCell] = new WalkFrameStaticRecords(
            new[] { MakeRecord(1, 0, new Vector3(5, 0, 0),
                [new MeshRef((uint)objectGfx, Matrix4x4.Identity)]) },
            0x8C04u);
        worldData.OutdoorStaticsByCell[secondCell] = new WalkFrameStaticRecords(
            new[] { MakeRecord(2, 0, new Vector3(50, 0, 0),
                [new MeshRef((uint)objectGfx, Matrix4x4.Identity)]) },
            0x8C04u);
        var log = new List<string>();
        var leaf = new RecordingLeafRenderer(log, fx.AlphaQueue);
        leaf.CellsWithoutEmitters.UnionWith([firstCell, secondCell]);
        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, worldData);
        var ctx = new TestContext();
        IWalkEventSink sink = driver;

        using DrawScope draw = fx.BeginDraw(beginAlpha: true);
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        var activeViews = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            activeViews, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        sink.OnLandscapeViews(activeViews);
        sink.OnLandscapeCellTurn(firstCell);
        sink.OnLandscapeCellTurn(secondCell);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal(2, QueueAlphaEntries(fx.AlphaQueue).Count);
        IList payload = (IList)typeof(WbDrawDispatcher).GetField(
            "_deferredAlpha", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fx.Dispatcher)!;
        Matrix4x4 firstModel = (Matrix4x4)payload[0]!.GetType().GetProperty("Model")!
            .GetValue(payload[0])!;
        Matrix4x4 secondModel = (Matrix4x4)payload[1]!.GetType().GetProperty("Model")!
            .GetValue(payload[1])!;
        Assert.Equal(5f, firstModel.M41);
        Assert.Equal(50f, secondModel.M41);

        fx.AlphaQueue.AbortFrame();
    }

    [Fact]
    public void ProductionCellObjectParticleMerge_WarmedPathAllocatesZeroBytes()
    {
        using var fx = new DispatcherFixture();
        const uint cellId = 0x8C040007u;
        const ulong objectGfx = 0x0200_0C33UL;
        InjectRenderData(fx.Manager, objectGfx, MakeFlatMesh(
            MakeBatch(0x08100C33u, TranslucencyKind.AlphaBlend, 0, 0, 3, 1)));
        var worldData = new FakeWorldData();
        worldData.OutdoorStaticsByCell[cellId] = new WalkFrameStaticRecords(
            new[] { MakeRecord(1, 0, new Vector3(40, 0, 0),
                [new MeshRef((uint)objectGfx, Matrix4x4.Identity)]) },
            0x8C04u);

        var particles = new ParticleSystem(new EmitterDescRegistry(), new Random(42));
        int handle = particles.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000C33u,
                Type = AcDream.Core.Vfx.ParticleType.Still,
                MaxParticles = 1,
                InitialParticles = 1,
                LifetimeMin = 100f,
                LifetimeMax = 100f,
                StartAlpha = 1f,
                EndAlpha = 1f,
            },
            new Vector3(20, 0, 0));
        particles.UpdateEmitterOwnerCell(handle, cellId);
        using var renderer = new ParticleRenderer(
            fx.Device,
            fx.FrameLifetime,
            fx.Scope,
            particles,
            meshAdapter: fx.MeshAdapter,
            alphaQueue: fx.AlphaQueue);
        var leaf = new ProductionParticleLeaf(
            particles, renderer, new IdentityCamera(), Vector3.Zero);
        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, worldData);
        var ctx = new TestContext();
        IWalkEventSink sink = driver;
        var activeViews = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            activeViews, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);

        using DrawScope draw = fx.BeginDraw();
        fx.Dispatcher.BeginFrame(frameSlot: 0);
        renderer.BeginFrame(frameSlot: 0);
        fx.Device.RecordingEnabled = false;

        void RunCellTurn()
        {
            fx.AlphaQueue.BeginFrame();
            driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
            sink.OnLandscapeViews(activeViews);
            sink.OnLandscapeCellTurn(cellId);
            driver.EndFrame();
            driver.Replay(draw.Frame, draw.Pass);
            fx.AlphaQueue.AbortFrame();
        }

        long allocated = ZeroAllocationProbe.MeasureWarmed(
            RunCellTurn,
            batchSize: 128,
            warmupBatches: 2,
            samples: 4);

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void PreparedCellAlpha_AcceptsStableTokenAndRollsBackRejectedTailOnAbort()
    {
        using var fx = new DispatcherFixture();
        const uint cellId = 0x8C0400B1u;
        var particles = new ParticleSystem(new EmitterDescRegistry(), new Random(42));
        int handle = particles.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000CB1u,
                Type = AcDream.Core.Vfx.ParticleType.Still,
                MaxParticles = 2,
                InitialParticles = 2,
                LifetimeMin = 100f,
                LifetimeMax = 100f,
                StartAlpha = 1f,
                EndAlpha = 1f,
                Gravity = Vector3.Zero,
            },
            new Vector3(20, 0, 0));
        particles.UpdateEmitterOwnerCell(handle, cellId);
        using var renderer = new ParticleRenderer(
            fx.Device,
            fx.FrameLifetime,
            fx.Scope,
            particles,
            meshAdapter: fx.MeshAdapter,
            alphaQueue: fx.AlphaQueue);
        var dummy = new DummyAlphaSource();
        fx.AlphaQueue.BeginFrame();
        for (int i = 0; i < RetailAlphaQueue.ListCapacity - 1; i++)
        {
            Assert.True(fx.AlphaQueue.TryAppend(
                RetailAlphaList.Alpha, dummy, i, overrideClipmap: false));
        }

        ReadOnlySpan<PreparedParticleAlphaSubmission> prepared =
            renderer.PrepareForCellAlpha(
                new IdentityCamera(),
                Vector3.Zero,
                ParticleRenderPass.Scene,
                cellId);
        Assert.Equal(2, prepared.Length);
        Assert.Empty(DeferredParticlePayload(renderer));

        prepared[0].Append();
        prepared[1].Append();

        Assert.Single(DeferredParticlePayload(renderer));
        RetailAlphaEntry accepted = Assert.Single(
            QueueAlphaEntries(fx.AlphaQueue),
            entry => entry.Source.GetType().DeclaringType == typeof(ParticleRenderer));
        Assert.Equal(0, accepted.Token);
        Assert.Equal(RetailAlphaQueue.ListCapacity, fx.AlphaQueue.AlphaCount);

        fx.AlphaQueue.AbortFrame();
        Assert.Empty(DeferredParticlePayload(renderer));
        Assert.True(renderer.PreparedCellAlphaScratchDiagnostics.Count == 0);
    }

    [Fact]
    public void PreparedCellAlpha_AppendExceptionRollsBackExactTailToken()
    {
        using var fx = new DispatcherFixture();
        const uint cellId = 0x8C0400B5u;
        var particles = new ParticleSystem(new EmitterDescRegistry(), new Random(42));
        int handle = particles.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000CB7u,
                Type = AcDream.Core.Vfx.ParticleType.Still,
                MaxParticles = 1,
                InitialParticles = 1,
                LifetimeMin = 100f,
                LifetimeMax = 100f,
                StartAlpha = 1f,
                EndAlpha = 1f,
                Gravity = Vector3.Zero,
            },
            new Vector3(20, 0, 0));
        particles.UpdateEmitterOwnerCell(handle, cellId);
        using var renderer = new ParticleRenderer(
            fx.Device,
            fx.FrameLifetime,
            fx.Scope,
            particles,
            meshAdapter: fx.MeshAdapter,
            alphaQueue: fx.AlphaQueue);
        fx.AlphaQueue.BeginFrame();
        PreparedParticleAlphaSubmission prepared = renderer.PrepareForCellAlpha(
            new IdentityCamera(), Vector3.Zero, ParticleRenderPass.Scene, cellId)[0];
        fx.AlphaQueue.AbortFrame();

        Assert.Throws<InvalidOperationException>(() => prepared.Append());
        Assert.Empty(DeferredParticlePayload(renderer));

        renderer.PrepareForCellAlpha(
            new IdentityCamera(), Vector3.Zero, ParticleRenderPass.Scene, 0xDEAD0002u);
        Assert.True(renderer.PreparedCellAlphaScratchDiagnostics.Count == 0);
    }

    [Fact]
    public void PreparedCellAlpha_FlushAndEndResetAcceptedPayloadAndPreparedScratch()
    {
        using var fx = new DispatcherFixture();
        const uint cellId = 0x8C0400B2u;
        var particles = new ParticleSystem(new EmitterDescRegistry(), new Random(42));
        int handle = particles.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000CB2u,
                Type = AcDream.Core.Vfx.ParticleType.Still,
                MaxParticles = 1,
                InitialParticles = 1,
                LifetimeMin = 100f,
                LifetimeMax = 100f,
                StartAlpha = 1f,
                EndAlpha = 1f,
                Gravity = Vector3.Zero,
            },
            new Vector3(20, 0, 0));
        particles.UpdateEmitterOwnerCell(handle, cellId);
        using var renderer = new ParticleRenderer(
            fx.Device,
            fx.FrameLifetime,
            fx.Scope,
            particles,
            meshAdapter: fx.MeshAdapter,
            alphaQueue: fx.AlphaQueue);
        using DrawScope draw = fx.BeginDraw();
        renderer.BeginFrame(frameSlot: 0);

        fx.AlphaQueue.BeginFrame();
        renderer.PrepareForCellAlpha(
            new IdentityCamera(), Vector3.Zero, ParticleRenderPass.Scene, cellId)[0].Append();
        Assert.Single(DeferredParticlePayload(renderer));
        fx.AlphaQueue.Flush(RetailAlphaFlushSite.RenderNormalMode, 0f);
        Assert.Empty(DeferredParticlePayload(renderer));
        Assert.True(renderer.PreparedCellAlphaScratchDiagnostics.Count == 0);

        renderer.PrepareForCellAlpha(
            new IdentityCamera(), Vector3.Zero, ParticleRenderPass.Scene, cellId)[0].Append();
        Assert.Single(DeferredParticlePayload(renderer));
        fx.AlphaQueue.EndFrame();
        Assert.Empty(DeferredParticlePayload(renderer));
        Assert.True(renderer.PreparedCellAlphaScratchDiagnostics.Count == 0);
    }

    [Fact]
    public void PreparedCellAlpha_CapsBothListsAndRejectStormRetainsNoPayloadOrUnboundedScratch()
    {
        using var fx = new DispatcherFixture();
        const uint cellId = 0x8C0400B3u;
        const uint clipGfx = 0x01000CB3u;
        InjectRenderData(fx.Manager, clipGfx, MakeFlatMesh(
            MakeBatch(0x08000CB3u, TranslucencyKind.ClipMap, 0, 0, 3, 1)));
        var particles = new ParticleSystem(new EmitterDescRegistry(), new Random(42));
        int alphaHandle = particles.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000CB3u,
                Type = AcDream.Core.Vfx.ParticleType.Still,
                MaxParticles = RetailAlphaQueue.ListCapacity + 1,
                InitialParticles = RetailAlphaQueue.ListCapacity + 1,
                LifetimeMin = 100f,
                LifetimeMax = 100f,
                StartAlpha = 1f,
                EndAlpha = 1f,
                Gravity = Vector3.Zero,
            },
            new Vector3(30, 0, 0));
        int clipHandle = particles.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000CB4u,
                Type = AcDream.Core.Vfx.ParticleType.Still,
                GfxObjId = clipGfx,
                MaxParticles = RetailAlphaQueue.ListCapacity + 1,
                InitialParticles = RetailAlphaQueue.ListCapacity + 1,
                LifetimeMin = 100f,
                LifetimeMax = 100f,
                StartAlpha = 1f,
                EndAlpha = 1f,
                Gravity = Vector3.Zero,
            },
            new Vector3(20, 0, 0));
        particles.UpdateEmitterOwnerCell(alphaHandle, cellId);
        particles.UpdateEmitterOwnerCell(clipHandle, cellId);
        using var renderer = new ParticleRenderer(
            fx.Device,
            fx.FrameLifetime,
            fx.Scope,
            particles,
            meshAdapter: fx.MeshAdapter,
            alphaQueue: fx.AlphaQueue);
        var dummy = new DummyAlphaSource();
        fx.AlphaQueue.BeginFrame();
        for (int i = 0; i < RetailAlphaQueue.ListCapacity; i++)
        {
            Assert.True(fx.AlphaQueue.TryAppend(
                RetailAlphaList.Alpha, dummy, i, overrideClipmap: false));
            Assert.True(fx.AlphaQueue.TryAppend(
                RetailAlphaList.Clip, dummy, i, overrideClipmap: false));
        }

        ReadOnlySpan<PreparedParticleAlphaSubmission> prepared =
            renderer.PrepareForCellAlpha(
                new IdentityCamera(), Vector3.Zero, ParticleRenderPass.Scene, cellId);
        int alphaCount = 0;
        int clipCount = 0;
        for (int i = 0; i < prepared.Length; i++)
        {
            if (prepared[i].List == RetailAlphaList.Clip)
                clipCount++;
            else
                alphaCount++;
        }

        Assert.Equal(RetailAlphaQueue.ListCapacity * 2, prepared.Length);
        Assert.Equal(RetailAlphaQueue.ListCapacity, clipCount);
        Assert.Equal(RetailAlphaQueue.ListCapacity, alphaCount);
        Assert.Empty(DeferredParticlePayload(renderer));
        Assert.InRange(renderer.PreparedCellAlphaScratchDiagnostics.Capacity, 6000, 8192);
        Assert.True(renderer.PreparedCellAlphaScratchDiagnostics.RetainedBytes > 0);
        Assert.True(
            renderer.PreparedCellAlphaScratchDiagnostics.RetainedBytes
                <= renderer.RetainedAlphaScratchBytes);

        for (int i = 0; i < prepared.Length; i++)
            prepared[i].Append();
        Assert.Empty(DeferredParticlePayload(renderer));
        Assert.Equal(RetailAlphaQueue.ListCapacity * 2, fx.AlphaQueue.PendingCount);

        fx.AlphaQueue.AbortFrame();
        Assert.True(renderer.PreparedCellAlphaScratchDiagnostics.Count == 0);
        Assert.InRange(renderer.PreparedCellAlphaScratchDiagnostics.Capacity, 6000, 8192);
        Assert.Empty(DeferredParticlePayload(renderer));
    }

    [Fact]
    public void PreparedCellAlpha_ImmediateExceptionAfterDelayedCandidateDoesNotReservePayload()
    {
        using var fx = new DispatcherFixture();
        const uint cellId = 0x8C0400B4u;
        const uint immediateGfx = 0x01000CB4u;
        InjectRenderData(fx.Manager, immediateGfx, MakeFlatMesh(
            MakeBatch(0x08000CB4u, TranslucencyKind.Opaque, 0, 0, 3, 1)));
        var particles = new ParticleSystem(new EmitterDescRegistry(), new Random(42));
        int delayedHandle = particles.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000CB5u,
                Type = AcDream.Core.Vfx.ParticleType.Still,
                MaxParticles = 1,
                InitialParticles = 1,
                LifetimeMin = 100f,
                LifetimeMax = 100f,
                StartAlpha = 1f,
                EndAlpha = 1f,
                Gravity = Vector3.Zero,
            },
            new Vector3(100, 0, 0));
        int immediateHandle = particles.SpawnEmitter(
            new EmitterDesc
            {
                DatId = 0x32000CB6u,
                Type = AcDream.Core.Vfx.ParticleType.Still,
                GfxObjId = immediateGfx,
                MaxParticles = 1,
                InitialParticles = 1,
                LifetimeMin = 100f,
                LifetimeMax = 100f,
                StartAlpha = 1f,
                EndAlpha = 1f,
                Gravity = Vector3.Zero,
            },
            new Vector3(10, 0, 0));
        particles.UpdateEmitterOwnerCell(delayedHandle, cellId);
        particles.UpdateEmitterOwnerCell(immediateHandle, cellId);
        using var renderer = new ParticleRenderer(
            fx.Device,
            fx.FrameLifetime,
            fx.Scope,
            particles,
            meshAdapter: fx.MeshAdapter,
            alphaQueue: fx.AlphaQueue);
        fx.AlphaQueue.BeginFrame();

        Assert.Throws<InvalidOperationException>(() =>
            renderer.PrepareForCellAlpha(
                new IdentityCamera(), Vector3.Zero, ParticleRenderPass.Scene, cellId));
        Assert.Empty(DeferredParticlePayload(renderer));
        Assert.True(renderer.PreparedCellAlphaScratchDiagnostics.Count == 1);

        renderer.PrepareForCellAlpha(
            new IdentityCamera(), Vector3.Zero, ParticleRenderPass.Scene, 0xDEAD0001u);
        Assert.True(renderer.PreparedCellAlphaScratchDiagnostics.Count == 0);
        fx.AlphaQueue.AbortFrame();
    }

    private static List<RetailAlphaEntry> QueueAlphaEntries(RetailAlphaQueue queue) =>
        (List<RetailAlphaEntry>)typeof(RetailAlphaQueue).GetField(
            "_alpha", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(queue)!;

    private static IList DeferredParticlePayload(ParticleRenderer renderer) =>
        (IList)typeof(ParticleRenderer).GetField(
            "_deferredAlpha", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(renderer)!;


    [Fact]
    public void InteriorRootWithExitView_DrawsLandCellThroughTheExitViewBeforeFlushSealsAndFlood()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        const ulong gfxObjA = 0x0200_0025UL;
        const ulong gfxObjB = 0x0200_0026UL;
        InjectRenderData(fx.Manager, gfxObjA, MakeFlatMesh(
            MakeBatch(0x08100025u, TranslucencyKind.Opaque, 0, 0, 3, 1)));
        InjectRenderData(fx.Manager, gfxObjB, MakeFlatMesh(
            MakeBatch(0x08100026u, TranslucencyKind.Opaque, 3, 4, 3, 2)));

        var ctx = new TestContext();
        var cell1 = new WalkCell
        {
            CellId = 0x100,
            StabList = [0x101u],
            Portals =
            [
                new WalkCellPortal
                {
                    OtherCellId = 0x101, PolygonIndex = 0, PortalSide = 0, OtherPortalId = 0,
                },
                new WalkCellPortal
                {
                    OtherCellId = 0xFFFFFFFF, PolygonIndex = 1, PortalSide = 0, OtherPortalId = -1,
                },
            ],
            PortalPolygons = [Quad(-2f), Quad(-3f)],
        };
        var cell2 = new WalkCell
        {
            CellId = 0x101,
            Portals = [new WalkCellPortal
            {
                OtherCellId = 0x100, PolygonIndex = 0, PortalSide = 1, OtherPortalId = 0,
            }],
            PortalPolygons = [Quad(-2f)],
        };
        ctx.Cells[cell1.CellId] = cell1;
        ctx.Cells[cell2.CellId] = cell2;

        var worldData = new FakeWorldData();
        worldData.CellStaticsByCell[0x100] = new WalkFrameStaticRecords(
            new[] { MakeRecord(101, 0, Vector3.Zero, [new MeshRef((uint)gfxObjA, Matrix4x4.Identity)]) }, 0x8C04u);
        worldData.CellStaticsByCell[0x101] = new WalkFrameStaticRecords(
            new[] { MakeRecord(102, 0, Vector3.Zero, [new MeshRef((uint)gfxObjB, Matrix4x4.Identity)]) }, 0x8C04u);

        var leaf = new RecordingLeafRenderer(log);
        leaf.CellsWithoutEmitters.UnionWith(CoarseLandscapeBuckets(0xF4180000u));
        var trace = new RecordingTrace(log);
        using ClipFrame clipFrame = ClipFrame.NoClip();
        var driver = new WalkFrameDriver(
            fx.Dispatcher, leaf, worldData, trace, clipFrame);
        var walk = new RetailFrameWalk();
        // The same 1x1 landscape RunFrame_InteriorFloodWithExitView... uses,
        // but with a real block published in its one slot so the exit view
        // has something to admit.
        var landscape = new WalkLandscape { MidWidth = 1, Blocks = new WalkLandBlock?[1] };
        var block = new WalkLandBlock
        {
            LandblockId = 0xF4180000u, SideCellCount = 1, MaxZ = 10f, MinZ = 0f,
        };
        block.EnsureCellArrays();
        landscape.Blocks[0] = block;

        using DrawScope draw = fx.BeginDraw();
        driver.RunFrame(
            walk, cameraCellId: cell1.CellId, cameraCell: cell1, landscape: landscape,
            ctx, draw.Frame, draw.Pass, Matrix4x4.Identity, cameraWorldPosition: Vector3.Zero);

        Assert.Equal(
            new[]
            {
                "SKY", "LANDCELL:f4180000:1:0", "SORTCELLEXIT", "LFLUSH", "SEALS",
                "SHELL:00000101", "SHELL:00000100",
                "FLUSH:1:CellStatic", "CELL-PARTICLES:00000101",
                "FLUSH:1:CellStatic", "CELL-PARTICLES:00000100",
            },
            log);
    }


    [Fact]
    public void OnLandCellTurn_MergesAcrossLandblocksOverStreamMarksAndEmptyParticleTurns_RealFlushPointsSplit()
    {
        using var fx = new DispatcherFixture();
        const ulong gfxObj = 0x0200_0024UL;
        InjectRenderData(fx.Manager, gfxObj, MakeFlatMesh(
            MakeBatch(0x08100024u, TranslucencyKind.Opaque, 0, 0, 3, 1)));

        var worldData = new FakeWorldData();
        worldData.OutdoorStaticsByCell[0xAAAA0002u] = new WalkFrameStaticRecords(
            new[] { MakeRecord(311, 0, Vector3.Zero, [new MeshRef((uint)gfxObj, Matrix4x4.Identity)]) },
            0xAAAAu);
        worldData.OutdoorStaticsByCell[0xBBBB0002u] = new WalkFrameStaticRecords(
            new[] { MakeRecord(310, 0, Vector3.Zero, [new MeshRef((uint)gfxObj, Matrix4x4.Identity)]) },
            0xBBBBu);

        var log = new List<string>();
        var leaf = new RecordingLeafRenderer(log);
        leaf.CellsWithoutEmitters.Add(0xAAAA0001u);
        leaf.CellsWithoutEmitters.Add(0xAAAA0002u);
        var ctx = new TestContext();
        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, worldData, new RecordingTrace(log));
        IWalkEventSink sink = driver;

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        var landscapeViews = new WalkPortalView();
        WalkCopyView.AppendFullViewportQuad(
            landscapeViews, ctx.Rays, ctx.WorldViewpoint, ctx.ViewportWidth, ctx.ViewportHeight);
        sink.OnLandscapeViews(landscapeViews);

        sink.OnLandCellTurn(0xF4180000u, 8, 0);   // landblock A
        sink.OnLandscapeCellTurn(0xAAAA0001u);    // empty particle turn: no growth, no emitters -> no submit, no flush (F2)
        sink.OnLandCellTurn(0xF3180000u, 8, 0);   // landblock B, DIFFERENT -> still merges (F2)

        sink.OnLandscapeCellTurn(0xAAAA0002u);    // grows the stream (a real StreamMark fires) but no emitters -> the StreamMark does NOT flush (F10), and the particle turn doesn't either
        sink.OnLandCellTurn(0xF2180001u, 8, 0);   // landblock C -> still merges straight across that StreamMark (F10)

        sink.OnLandscapeCellTurn(0xBBBB0002u);    // grows the stream too, but HAS emitters (default) -> its StaticParticles turn flushes (F10), not the StreamMark ahead of it

        sink.OnLandCellTurn(0xF3180000u, 8, 1);   // new batch, started after that flush

        sink.OnBuildingTurn(new WalkBuilding());   // the building's own alpha barrier flushes (F10)

        sink.OnLandCellTurn(0xF2180000u, 8, 0);   // new batch

        sink.OnPunchGeometry(new WalkBuilding(), Quad(0f), 0); // the portal pass's punch fan flushes (F10)

        sink.OnLandCellTurn(0xF1180000u, 8, 0);   // final batch, flushed at Replay's own end
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal(
            new[]
            {
                "FLUSH:1:OutdoorStatic",
                "FLUSH:1:OutdoorStatic",
                "LANDCELL:f4180000:8:0,f3180000:8:0,f2180001:8:0",
                "PARTICLES:bbbb0002",
                "LANDCELL:f3180000:8:1",
                "ALPHA",
                "LANDCELL:f2180000:8:0",
                "PUNCH:4@v0",
                "LANDCELL:f1180000:8:0",
            },
            log);
        Assert.DoesNotContain("PARTICLES:aaaa0001", log);
        Assert.DoesNotContain("PARTICLES:aaaa0002", log);
        // Two genuine StreamMarks fired (the ordered stream grew twice) — the
        // F10 rule is exercised, not assumed.
        Assert.Equal(2, log.Count(entry => entry.StartsWith("FLUSH:", StringComparison.Ordinal)));
        Assert.Equal(4, leaf.LandCellBatches.Count);
        Assert.Equal(
            new (uint LandblockId, int SideCellCount, int CellIndex)[]
            {
                (0xF4180000u, 8, 0), (0xF3180000u, 8, 0), (0xF2180001u, 8, 0),
            },
            leaf.LandCellBatches[0]);
        Assert.Equal(
            new (uint LandblockId, int SideCellCount, int CellIndex)[] { (0xF3180000u, 8, 1) },
            leaf.LandCellBatches[1]);
        Assert.Equal(
            new (uint LandblockId, int SideCellCount, int CellIndex)[] { (0xF2180000u, 8, 0) },
            leaf.LandCellBatches[2]);
        Assert.Equal(
            new (uint LandblockId, int SideCellCount, int CellIndex)[] { (0xF1180000u, 8, 0) },
            leaf.LandCellBatches[3]);
    }


    [Fact]
    public void OnPunchGeometry_FlushesPendingTerrainBatch_FarTerrainBeforePunchNearTerrainAfter()
    {
        using var fx = new DispatcherFixture();
        var log = new List<string>();
        var leaf = new RecordingLeafRenderer(log);
        var ctx = new TestContext();
        var driver = new WalkFrameDriver(fx.Dispatcher, leaf, new FakeWorldData());
        IWalkEventSink sink = driver;

        using DrawScope draw = fx.BeginDraw();
        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        sink.OnLandscapeViews(new WalkPortalView());

        sink.OnLandCellTurn(0xF4180000u, 8, 0);
        sink.OnPunchGeometry(new WalkBuilding(), Quad(0f), 0); // the building's far-Z punch
        sink.OnLandCellTurn(0xF4180000u, 8, 1);
        driver.EndFrame();
        driver.Replay(draw.Frame, draw.Pass);

        Assert.Equal(
            new[]
            {
                "LANDCELL:f4180000:8:0",
                "PUNCH:4@v0",
                "LANDCELL:f4180000:8:1",
            },
            log);
    }

    // ── Fixture (mirrors WalkStaticStreamPopulatorTests' DispatcherFixture —
    // FW3.2a's own referee) ─────────────────────────────────────────────────

    private static RenderProjectionRecord MakeRecord(
        uint localEntityId,
        uint serverGuid,
        Vector3 position,
        IReadOnlyList<MeshRef> meshRefs,
        bool isBuildingShell = false,
        uint parentCellId = 0u) =>
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
                IsBuildingShell: isBuildingShell));

    private static ObjectRenderBatch MakeBatch(
        uint surfaceId,
        TranslucencyKind translucency,
        uint firstIndex,
        int baseVertex,
        int indexCount,
        uint textureSlotIndex,
        uint textureLayer = 0,
        CullMode cullMode = CullMode.CounterClockwise) =>
        new()
        {
            Key = new TextureKey { SurfaceId = surfaceId, IsSolid = false },
            Translucency = translucency,
            FirstIndex = firstIndex,
            BaseVertex = (uint)baseVertex,
            IndexCount = indexCount,
            TextureSlot = new GpuTextureSlot(textureSlotIndex),
            TextureIndex = (int)textureLayer,
        };

    private static ObjectRenderData MakeFlatMesh(params ObjectRenderBatch[] batches) =>
        new() { Batches = new List<ObjectRenderBatch>(batches) };

    private static void InjectRenderData(ObjectMeshManager manager, ulong id, ObjectRenderData data)
    {
        FieldInfo field = typeof(ObjectMeshManager).GetField(
            "_renderData", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "ObjectMeshManager._renderData field not found — test relies on this exact name.");
        var dict = (ConcurrentDictionary<ulong, ObjectRenderData>)field.GetValue(manager)!;
        dict[id] = data;
    }

    internal readonly struct DrawScope : IDisposable
    {
        private readonly IDisposable _publication;
        private readonly IGpuPassEncoder _pass;
        private readonly RetailAlphaQueue? _alpha;

        public DrawScope(
            IGpuFrame frame,
            IGpuPassEncoder pass,
            IDisposable publication,
            RetailAlphaQueue? alpha = null)
        {
            Frame = frame;
            _pass = pass;
            _publication = publication;
            _alpha = alpha;
        }

        public IGpuFrame Frame { get; }

        public IGpuPassEncoder Pass => _pass;

        public void Dispose()
        {
            if (_alpha?.IsCollecting == true)
                _alpha.EndFrame();
            _publication.Dispose();
            _pass.Dispose();
        }
    }

    internal sealed class DispatcherFixture : IDisposable
    {
        private readonly WbMeshAdapter _meshAdapter;
        private readonly TextureCache _textures;

        public DispatcherFixture()
        {
            Device = new RecordingGpuDevice();
            FrameLifetime = new GpuDeviceFrameLifetime(Device);
            Scope = new VulkanWorldPassScope(sampleCount: 1);
            _textures = new TextureCache(Device, new NoopDatReaderWriter());
            _meshAdapter = new WbMeshAdapter(
                Device,
                new NoopDatReaderWriter(),
                new NullPreparedAssetSource(),
                NullLogger<WbMeshAdapter>.Instance,
                Device.Retirement);
            var entitySpawnAdapter = new EntitySpawnAdapter(
                _textures,
                _ => throw new NotSupportedException("Not exercised by these tests."));

            Dispatcher = new WbDrawDispatcher(
                Device,
                FrameLifetime,
                Scope,
                _textures,
                _meshAdapter,
                entitySpawnAdapter,
                new EntityClassificationCache(),
                new AcDream.Core.Rendering.TranslucencyFadeManager(),
                alphaQueue: AlphaQueue);
        }

        public RecordingGpuDevice Device { get; }

        public GpuDeviceFrameLifetime FrameLifetime { get; }

        public VulkanWorldPassScope Scope { get; }

        public WbDrawDispatcher Dispatcher { get; }

        public RetailAlphaQueue AlphaQueue { get; } = new();

        public ObjectMeshManager Manager => _meshAdapter.MeshManager!;

        public WbMeshAdapter MeshAdapter => _meshAdapter;

        public DrawScope BeginDraw(bool beginAlpha = false)
        {
            if (beginAlpha)
            {
                Dispatcher.BeginFrame(frameSlot: 0);
                AlphaQueue.BeginFrame();
            }
            FrameLifetime.BeginFrame();
            IGpuFrame frame = FrameLifetime.CurrentFrame!;
            IGpuPassEncoder pass = frame.BeginPass(
                GpuPassDescription.BackbufferClear(
                    "fw3-2b-1-walk-frame-driver-test", Vector4.Zero, sampleCount: 1));
            IDisposable publication = Scope.Publish(pass);
            Device.Clear();
            return new DrawScope(
                frame,
                pass,
                publication,
                beginAlpha ? AlphaQueue : null);
        }

        public void Dispose()
        {
            if (AlphaQueue.IsCollecting)
                AlphaQueue.AbortFrame();
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

    private sealed class NoopDatReaderWriter : IDatReaderWriter
    {
        private readonly Dictionary<(Type Type, uint Id), IDBObj> _objects = new();
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

        public void Add<T>(uint id, T value) where T : IDBObj =>
            _objects[(typeof(T), id)] = value;

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
        public T Get<T>(uint fileId) where T : IDBObj =>
            _objects.TryGetValue((typeof(T), fileId), out IDBObj? value)
                ? (T)value
                : default;

        public bool TryGet<T>(
            uint fileId,
            [MaybeNullWhen(false)] out T value) where T : IDBObj
        {
            if (_objects.TryGetValue((typeof(T), fileId), out IDBObj? found))
            {
                value = (T)found;
                return true;
            }

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
