using System.Numerics;

namespace AcDream.App.Rendering.Walk;

public sealed class WalkPolygon
{
    public Vector3[] Vertices = [];
    public WalkPlane Plane;
}

public struct WalkCellPortal
{
    public uint OtherCellId;
    public int PolygonIndex;
    public int PortalSide;
    public int OtherPortalId;
    public bool ExactMatch;
}

public sealed class WalkCell
{
    public uint CellId;
    public WalkCellPortal[] Portals = [];
    public WalkPolygon[] PortalPolygons = [];
    public uint[] StabList = [];

    public Matrix4x4 WorldTransform = Matrix4x4.Identity;
    public Matrix4x4 InverseWorldTransform = Matrix4x4.Identity;

    public int NumView;
    public readonly List<WalkPortalView> PortalViews = new();
    public WalkCell?[] CachedNeighbors = [];

    public WalkPortalView TopView => PortalViews[NumView - 1];

    public void PushView()
    {
        while (PortalViews.Count <= NumView)
            PortalViews.Add(new WalkPortalView());
        PortalViews[NumView].ResetForPush();
        NumView++;
        if (CachedNeighbors.Length < Portals.Length)
            CachedNeighbors = new WalkCell?[Portals.Length];
    }

    public void PopView() => NumView--;
}

public interface IWalkFrameContext
{
    Vector3 ViewpointIn(WalkCell cell);

    Matrix4x4 ObjectToClip(WalkCell cell);

    WalkCell? GetVisible(uint cellId);

    IWalkRayCaster Rays { get; }
    Vector3 WorldViewpoint { get; }
    float ViewportWidth { get; }
    float ViewportHeight { get; }

    bool ClipLandscape => true;
}
