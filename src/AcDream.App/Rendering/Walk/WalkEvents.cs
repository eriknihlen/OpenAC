namespace AcDream.App.Rendering.Walk;

public enum WalkEventKind
{
    Landscape,

    Building,

    DrawInside,

    DrawCells,
}

public readonly record struct WalkEvent(
    WalkEventKind Kind,
    uint CellId,
    int OutsideViewCount,
    IReadOnlyList<uint> Cells)
{
    public static WalkEvent Landscape(int activeViewCount)
        => new(WalkEventKind.Landscape, 0, activeViewCount, Array.Empty<uint>());

    public static WalkEvent Building(uint positionCellId)
        => new(WalkEventKind.Building, positionCellId, 0, Array.Empty<uint>());

    public static WalkEvent DrawInside(uint cellId)
        => new(WalkEventKind.DrawInside, cellId, 0, Array.Empty<uint>());

    public static WalkEvent DrawCells(int outsideViewCount, IReadOnlyList<uint> cells)
        => new(WalkEventKind.DrawCells, 0, outsideViewCount, cells);
}

public interface IWalkEventSink
{
    void Emit(in WalkEvent walkEvent);

    void OnLandscapeViews(WalkPortalView activeViews) { }

    void OnLandCellTurn(uint landblockId, int sideCellCount, int cellIndex) { }

    void OnSortCellTurn(uint landblockId, int sideCellCount, int cellIndex) { }

    void OnLandscapeCellTurn(uint cellId) { }

    void OnLandscapeCellTurn(uint landblockId, int sideCellCount, int cellIndex) =>
        OnLandscapeCellTurn(
            (landblockId & 0xFFFF0000u) | checked((uint)(cellIndex + 1)));

    void OnSortCellExit(uint landblockId, int sideCellCount, int cellIndex) { }

    void OnBuildingTurn(WalkBuilding building) { }

    void OnBuildingShellTurn(
        WalkBuilding building,
        WalkBuildingSelection selection) { }

    void OnPunchGeometry(
        WalkBuilding building, WalkPolygon polygon, int activeViewIndex)
    { }

    void OnInteriorFloodDrawTurn(IReadOnlyList<uint> cells, int outsideViewCount) { }

    void OnWeatherTurn(uint viewerCellId) { }
}
