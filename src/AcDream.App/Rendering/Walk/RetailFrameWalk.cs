using System.Numerics;

namespace AcDream.App.Rendering.Walk;

public interface IRetailFrameWalkContext : IWalkBuildingFrameContext
{
    WalkPlane CyPlane { get; }

    void SetActiveView(WalkPortalView views, int index);

    float ViewportWidth { get; }
    float ViewportHeight { get; }

    uint ViewerCellId => 0u;

    bool WeatherGateOpen => false;
    bool BuildingDegradesDisabled => false;
}

public sealed class RetailFrameWalk
{
    private readonly BuildingDegradeController? _degradation;
    private readonly float? _fixedDegradeDistance;
    private readonly float? _fixedDegradeMultiplier;
    private readonly WalkPView _interiorPView = new() { DrawLandscape = true };
    private readonly WalkPView _outdoorPView = new() { DrawLandscape = false };
    private readonly WalkPortalView _defaultView = new();

    public WalkPView InteriorPView => _interiorPView;
    public WalkPView OutdoorPView => _outdoorPView;

    public bool AlwaysDrawObjects = true;

    public int ObjectRingLimit = 4;

    public RetailFrameWalk() { }

    internal RetailFrameWalk(BuildingDegradeController degradation)
        => _degradation = degradation ?? throw new ArgumentNullException(nameof(degradation));

    internal RetailFrameWalk(float degradeDistance, float degradeMultiplier)
    {
        _fixedDegradeDistance = degradeDistance;
        _fixedDegradeMultiplier = degradeMultiplier;
    }

    internal WalkPortalView InteriorOutsideView => _interiorPView.OutsideView;

    public void WalkFrame(
        uint cameraCellId, WalkCell? cameraCell, WalkLandscape landscape,
        IRetailFrameWalkContext ctx, IWalkEventSink sink)
    {
        if ((cameraCellId & 0xFFFF) < 0x100)
        {
            _defaultView.ResetForPush();
            WalkCopyView.AppendFullViewportQuad(
                _defaultView, ctx.Rays, ctx.WorldViewpoint,
                ctx.ViewportWidth, ctx.ViewportHeight);
            DrawLandscape(landscape, _defaultView, ctx, sink);
        }
        else
        {
            DrawInside(
                cameraCell ?? throw new ArgumentNullException(nameof(cameraCell)),
                landscape, ctx, sink);
        }
    }

    public void DrawInside(
        WalkCell cell, WalkLandscape landscape,
        IRetailFrameWalkContext ctx, IWalkEventSink sink)
    {
        sink.Emit(WalkEvent.DrawInside(cell.CellId));
        cell.PushView();
        AddViews(cell.StabList, ctx);
        WalkCopyView.AppendFullViewportQuad(
            cell.TopView, ctx.Rays, ctx.WorldViewpoint,
            ctx.ViewportWidth, ctx.ViewportHeight);
        _interiorPView.ConstructView(cell, 0xFFFF, ctx.CellContext);

        uint[] floodCells = EmitDrawCells(_interiorPView, sink);
        int outsideViewCount = _interiorPView.OutsideView.ViewCount;
        if (outsideViewCount > 0)
            DrawLandscape(landscape, _interiorPView.OutsideView, ctx, sink);

        sink.OnInteriorFloodDrawTurn(floodCells, outsideViewCount);

        RemoveViews(cell.StabList, ctx);
        cell.PopView();
    }

    public void DrawLandscape(
        WalkLandscape landscape, WalkPortalView activeViews,
        IRetailFrameWalkContext ctx, IWalkEventSink sink)
    {
        sink.Emit(WalkEvent.Landscape(activeViews.ViewCount));
        sink.OnLandscapeViews(activeViews);
        landscape.CalcDrawOrder();
        landscape.CheckBlocks(ctx.CyPlane, activeViews);

        for (int i = landscape.BlockDrawCount - 1; i >= 0; i--)
        {
            WalkLandBlock? block = landscape.Blocks[landscape.BlockDrawList[i]];
            if (block is null || block.InView == WalkBoundingType.Outside)
                continue;
            int cellCount = block.SideCellCount * block.SideCellCount;
            for (int k = 0; k < cellCount; k++)
            {
                int cellIndex = block.DrawArray[k];
                bool cellInView =
                    block.CellInView[cellIndex] != WalkBoundingType.Outside;

                if (cellInView)
                {
                    sink.OnLandCellTurn(
                        block.LandblockId, block.SideCellCount, cellIndex);
                }

                if (!AlwaysDrawObjects && !cellInView)
                    continue;

                sink.OnSortCellTurn(
                    block.LandblockId, block.SideCellCount, cellIndex);

                if (block.CellBuildings[cellIndex] is WalkBuilding building)
                    DrawBuilding(building, activeViews, ctx, sink);
                if (block.SideCellCount == 8 || block.Ring <= ObjectRingLimit)
                {
                    if ((uint)cellIndex < (uint)block.CoarseCellBuildings.Length)
                    {
                        foreach (WalkBuilding coarseBuilding in block.CoarseCellBuildings[cellIndex])
                            DrawBuilding(coarseBuilding, activeViews, ctx, sink);
                    }
                    sink.OnLandscapeCellTurn(
                        block.LandblockId,
                        block.SideCellCount,
                        cellIndex);
                }

                sink.OnSortCellExit(
                    block.LandblockId,
                    block.SideCellCount,
                    cellIndex);
            }
        }

        if (ctx.WeatherGateOpen)
            sink.OnWeatherTurn(ctx.ViewerCellId);
    }

    public void DrawBuilding(
        WalkBuilding building, WalkPortalView activeViews,
        IRetailFrameWalkContext ctx, IWalkEventSink sink)
    {
        sink.Emit(WalkEvent.Building(building.PositionCellId));
        WalkBuildingSelection selection = building.Select(
            ctx.ViewerDistanceTo(building),
            _degradation?.DegradeDistance ?? _fixedDegradeDistance ?? 50f,
            _degradation?.ActiveMultiplier ?? _fixedDegradeMultiplier ?? 0f,
            degradesDisabled: ctx.BuildingDegradesDisabled);
        if (selection.GfxObjId == 0)
            return;

        sink.OnBuildingTurn(building);

        if (selection.DrawingBsp is WalkBspNode bsp)
        {
            int viewCount = Math.Max(activeViews.ViewCount, 0);
            var passSink = new PortalPassSink(building, sink);
            Vector3 viewpoint = ctx.ViewpointInBuilding(building);
            for (int v = 0; v < viewCount; v++)
            {
                passSink.ActiveViewIndex = v;
                ctx.SetActiveView(activeViews, v);
                WalkBuildingPortals.BuildDrawPortalsOnly(
                    bsp, 1, viewpoint,
                    (portalRef, pass) => WalkBuildingPortals.DrawPortal(
                        _outdoorPView, building, portalRef, pass, ctx, passSink));
                WalkBuildingPortals.BuildDrawPortalsOnly(
                    bsp, 2, viewpoint,
                    (portalRef, pass) => WalkBuildingPortals.DrawPortal(
                        _outdoorPView, building, portalRef, pass, ctx, passSink));
            }
        }

        sink.OnBuildingShellTurn(building, selection);
    }

    private uint[] EmitDrawCells(WalkPView pview, IWalkEventSink sink)
    {
        uint[] cells = new uint[pview.CellDrawList.Count];
        for (int i = 0; i < cells.Length; i++)
            cells[i] = pview.CellDrawList[i].CellId;
        sink.Emit(WalkEvent.DrawCells(pview.OutsideView.ViewCount, cells));
        return cells;
    }

    private void AddViews(uint[] stabList, IRetailFrameWalkContext ctx)
    {
        foreach (uint id in stabList)
            ctx.GetVisible(id)?.PushView();
    }

    private void RemoveViews(uint[] stabList, IRetailFrameWalkContext ctx)
    {
        foreach (uint id in stabList)
            ctx.GetVisible(id)?.PopView();
    }

    private sealed class PortalPassSink(WalkBuilding building, IWalkEventSink sink)
        : WalkBuildingPortals.IWalkPortalPassSink
    {
        public int ActiveViewIndex;

        public void OnPunch(WalkPolygon polygon)
        {
            sink.OnPunchGeometry(building, polygon, ActiveViewIndex);
        }

        public void OnDrawCells(WalkPView pview)
        {
            uint[] cells = new uint[pview.CellDrawList.Count];
            for (int i = 0; i < cells.Length; i++)
                cells[i] = pview.CellDrawList[i].CellId;
            sink.Emit(WalkEvent.DrawCells(pview.OutsideView.ViewCount, cells));
        }
    }
}
