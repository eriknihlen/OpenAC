using System.Numerics;

namespace AcDream.App.Rendering.Walk;

public sealed class WalkProductionFrameContext : IWalkFrameContext, IRetailFrameWalkContext
{
    public const float ZNear = 0.1f;

    private sealed class InverseViewProjectionRayCaster : IWalkRayCaster
    {
        private Matrix4x4 _inverseViewProjection;
        private float _viewportWidth;
        private float _viewportHeight;

        public InverseViewProjectionRayCaster(
            Matrix4x4 viewProjection, float viewportWidth, float viewportHeight)
            => Reset(viewProjection, viewportWidth, viewportHeight);

        internal void Reset(
            Matrix4x4 viewProjection, float viewportWidth, float viewportHeight)
        {
            if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverseViewProjection))
            {
                throw new ArgumentException(
                    "The walk's view-projection matrix must be invertible.",
                    nameof(viewProjection));
            }
            _inverseViewProjection = inverseViewProjection;
            _viewportWidth = viewportWidth;
            _viewportHeight = viewportHeight;
        }

        public Vector3 RayThrough(float screenX, float screenY)
        {
            float ndcX = screenX / _viewportWidth * 2f - 1f;
            float ndcY = 1f - screenY / _viewportHeight * 2f;
            Vector4 near = Vector4.Transform(
                new Vector4(ndcX, ndcY, 0f, 1f), _inverseViewProjection);
            Vector4 far = Vector4.Transform(
                new Vector4(ndcX, ndcY, 1f, 1f), _inverseViewProjection);
            Vector3 nearWorld = new Vector3(near.X, near.Y, near.Z) / near.W;
            Vector3 farWorld = new Vector3(far.X, far.Y, far.Z) / far.W;
            return farWorld - nearWorld;
        }
    }

    private readonly CellVisibility _cells;
    private readonly WalkBuildingRegistry _buildings;
    private IWalkShellResidency? _shellResidency;
    private Matrix4x4 _viewProjection;
    private readonly InverseViewProjectionRayCaster _rays;

    private Vector2[] _activeViewVerts = new Vector2[32];
    private int _activeViewVertCount;

    public WalkProductionFrameContext(
        CellVisibility cells,
        WalkBuildingRegistry buildings,
        Vector3 worldViewpoint,
        Vector3 forward,
        Matrix4x4 viewProjection,
        float viewportWidth,
        float viewportHeight,
        uint viewerCellId = 0u,
        bool weatherGateOpen = false,
        bool buildingDegradesDisabled = false,
        IWalkShellResidency? shellResidency = null,
        bool keepDistantBuildings = false)
    {
        _cells = cells ?? throw new ArgumentNullException(nameof(cells));
        _buildings = buildings ?? throw new ArgumentNullException(nameof(buildings));
        _rays = new InverseViewProjectionRayCaster(
            viewProjection, viewportWidth, viewportHeight);
        Reset(
            worldViewpoint, forward, viewProjection, viewportWidth, viewportHeight,
            viewerCellId, weatherGateOpen, buildingDegradesDisabled, shellResidency,
            keepDistantBuildings);
    }

    internal void Reset(
        Vector3 worldViewpoint,
        Vector3 forward,
        Matrix4x4 viewProjection,
        float viewportWidth,
        float viewportHeight,
        uint viewerCellId = 0u,
        bool weatherGateOpen = false,
        bool buildingDegradesDisabled = false,
        IWalkShellResidency? shellResidency = null,
        bool keepDistantBuildings = false)
    {
        _shellResidency = shellResidency;
        _rays.Reset(viewProjection, viewportWidth, viewportHeight);
        WorldViewpoint = worldViewpoint;
        _viewProjection = viewProjection;
        ViewportWidth = viewportWidth;
        ViewportHeight = viewportHeight;
        ViewerCellId = viewerCellId;
        WeatherGateOpen = weatherGateOpen;
        BuildingDegradesDisabled = buildingDegradesDisabled;
        KeepDistantBuildings = keepDistantBuildings;
        _activeViewVertCount = 0;
        CyPlane = new WalkPlane(forward, -Vector3.Dot(worldViewpoint, forward) - ZNear);
    }

    public Vector3 WorldViewpoint { get; private set; }
    public float ViewportWidth { get; private set; }
    public float ViewportHeight { get; private set; }
    public uint ViewerCellId { get; private set; }
    public bool WeatherGateOpen { get; private set; }
    public bool BuildingDegradesDisabled { get; private set; }
    public bool KeepDistantBuildings { get; private set; }
    public WalkPlane CyPlane { get; private set; }
    public IWalkRayCaster Rays => _rays;
    public IWalkFrameContext CellContext => this;

    public Vector3 ViewpointIn(WalkCell cell)
        => Vector3.Transform(WorldViewpoint, cell.InverseWorldTransform);

    public Matrix4x4 ObjectToClip(WalkCell cell) => cell.WorldTransform * _viewProjection;

    public WalkCell? GetVisible(uint cellId)
        => _cells.TryGetCell(cellId, out LoadedCell? cell) ? cell?.Walk : null;

    public bool IsBuildingShellDrawable(uint gfxObjId)
        => _shellResidency is null || _shellResidency.IsShellDrawable(gfxObjId);

    public void SetActiveView(WalkPortalView views, int index)
    {
        WalkViewPoly poly = views.View.Polys[index];
        if (_activeViewVerts.Length < poly.VertexCount)
            _activeViewVerts = new Vector2[poly.VertexCount];
        for (int k = 0; k < poly.VertexCount; k++)
            _activeViewVerts[k] = views.View.Vertices[poly.VertexIndex + k].Point;
        _activeViewVertCount = poly.VertexCount;
    }

    public Vector3 ViewpointInBuilding(WalkBuilding building)
        => Vector3.Transform(WorldViewpoint, GetEntry(building).InversePartZeroWorldTransform);

    public float ViewerDistanceTo(WalkBuilding building)
    {
        WalkBuildingFactory.Entry entry = GetEntry(building);
        float distance = Vector3.Distance(
            WorldViewpoint,
            Vector3.Transform(building.SortCenter, entry.PartZeroWorldTransform));
        return distance / building.PartZeroScaleZ;
    }

    public int ClipBuildingPolygon(
        WalkBuilding building, WalkPolygon polygon, int side, Span<WalkScreenPoint> output)
    {
        Matrix4x4 objectToClip =
            GetEntry(building).PartZeroWorldTransform * _viewProjection;
        Span<WalkScreenPoint> projected = stackalloc WalkScreenPoint[polygon.Vertices.Length];
        for (int i = 0; i < polygon.Vertices.Length; i++)
        {
            projected[i] = WalkScreenClip.TransformToScreen(
                polygon.Vertices[i], objectToClip, ViewportWidth, ViewportHeight);
        }
        if (side != 0)
            projected.Reverse();
        return WalkScreenClip.ClipAgainstView(
            projected, _activeViewVerts.AsSpan(0, _activeViewVertCount), output);
    }

    private WalkBuildingFactory.Entry GetEntry(WalkBuilding building)
    {
        if (!_buildings.TryGetEntry(building, out var entry))
        {
            throw new InvalidOperationException(
                "WalkProductionFrameContext was asked to place a WalkBuilding " +
                "that is not committed in its WalkBuildingRegistry.");
        }
        return entry;
    }
}
