using System.Numerics;

namespace AcDream.App.Rendering.Walk;

public struct WalkBldPortal
{
    public int PortalSide;
    public uint OtherCellId;
    public int OtherPortalId;
    public bool ExactMatch;
    public uint[] StabList;
}

public struct WalkPortalRef
{
    public int PortalIndex;
    public WalkPolygon Polygon;
}

public sealed class WalkBspNode
{
    public WalkPlane SplittingPlane;
    public WalkBspNode? PosNode;
    public WalkBspNode? NegNode;
    public bool IsFail;
    public WalkPortalRef[]? InPortals;   // non-null = PORT node

    public bool IsPortal => InPortals is not null;
}

/// <summary>One degrade-ladder level: the drawing BSP of that level's
/// GfxObj (portal-only view) and the level's authored distance bands.</summary>
public readonly record struct WalkBuildingDegradeLevel(
    uint GfxObjId,
    uint Mode,
    float MinDist,
    float IdealDist,
    float MaxDist,
    WalkBspNode? DrawingBsp);

public readonly record struct WalkBuildingSelection(
    uint GfxObjId,
    WalkBspNode? DrawingBsp,
    int Level,
    uint Mode);

public sealed class WalkBuilding
{
    public uint PositionCellId;

    public WalkBldPortal[] Portals = [];

    /// <summary>The drawing BSP of part 0's BASE GfxObj (portal-only view).
    /// Used directly when the model has no degrade ladder.</summary>
    public WalkBspNode? DrawingBsp;

    public uint GfxObjId;

    public WalkBuildingDegradeLevel[] DegradeLevels = [];

    public Vector3 SortCenter;

    /// <summary>Part-zero Setup resting/default-scale transform. Direct Gfx
    /// buildings use identity.</summary>
    public Matrix4x4 PartZeroTransform = Matrix4x4.Identity;

    public float PartZeroScaleZ = 1f;

    /// <param name="keepDistantBuildings">
    /// When a building's detail ladder ends in an entry that names no mesh at
    /// all — "at this distance, draw nothing" — take the nearest entry below it
    /// that does name one instead of drawing nothing. Our object range reaches
    /// much further than the ladder's authors assumed, and the small objects
    /// around a building carry no ladder, so honouring that entry leaves fences
    /// and stairs standing around a building that is no longer there. Off, the
    /// ladder is honoured exactly as authored.
    /// </param>
    public WalkBuildingSelection Select(
        float viewerDistance,
        float degradeDistance,
        float degradeMultiplier,
        bool degradesDisabled = false,
        int forcedLevel = -1,
        bool keepDistantBuildings = false)
    {
        if (DegradeLevels.Length == 0)
            return new WalkBuildingSelection(GfxObjId, DrawingBsp, 0, 1u);
        if (degradesDisabled)
            return SelectionAt(0);
        if (forcedLevel >= 0)
            return SelectionAt(Math.Min(forcedLevel, DegradeLevels.Length - 1));

        float effective = MathF.Max(0f, MathF.Abs(viewerDistance) - degradeDistance);
        for (int i = 0; i < DegradeLevels.Length; i++)
        {
            WalkBuildingDegradeLevel level = DegradeLevels[i];
            double threshold = degradeMultiplier >= 0f
                ? (double)level.IdealDist
                    - ((double)level.IdealDist - level.MaxDist) * degradeMultiplier
                : (double)level.IdealDist
                    + ((double)level.IdealDist - level.MinDist) * degradeMultiplier;
            if (effective < threshold)
                return SelectionAt(i);
        }
        return SelectionAt(DegradeLevels.Length - 1);

        WalkBuildingSelection SelectionAt(int index)
        {
            // The ladder's entries come straight from the authored data, so
            // even the nearest one can name no mesh; when none of them does,
            // the building draws nothing, exactly as authored.
            if (keepDistantBuildings && DegradeLevels[index].GfxObjId == 0)
            {
                for (int i = index - 1; i >= 0; i--)
                {
                    if (DegradeLevels[i].GfxObjId != 0)
                    {
                        index = i;
                        break;
                    }
                }
            }

            WalkBuildingDegradeLevel level = DegradeLevels[index];
            return new WalkBuildingSelection(
                level.GfxObjId, level.DrawingBsp, index, level.Mode);
        }
    }
}

public static class WalkBuildingPortals
{
    public interface IWalkPortalPassSink
    {
        /// <summary>Pass 1 drew the portal polygon as a far-Z punch
        /// (<c>DrawPortalPolyInternal(poly, 1)</c>).</summary>
        void OnPunch(WalkPolygon polygon);

        void OnDrawCells(WalkPView pview);
    }

    public static void BuildDrawPortalsOnly(
        WalkBspNode? root, int pass, Vector3 viewpointInBuilding,
        Action<WalkPortalRef, int> emitPortal)
    {
        if (root is null || root.IsFail) return;
        Walk(root, pass, viewpointInBuilding, emitPortal);
    }

    private static void Walk(
        WalkBspNode node, int pass, Vector3 viewpoint,
        Action<WalkPortalRef, int> emitPortal)
    {
        while (true)
        {
            float d = Vector3.Dot(node.SplittingPlane.Normal, viewpoint) + node.SplittingPlane.D;
            int side = d > WalkVisibilityMath.Epsilon ? 0
                : d < -WalkVisibilityMath.Epsilon ? 1 : 2;

            WalkBspNode? next;
            if (node.IsPortal)
            {
                if (side == 0)
                {
                    Visit(node.NegNode, pass, viewpoint, emitPortal);
                    foreach (WalkPortalRef portal in node.InPortals!)
                        emitPortal(portal, pass);
                    next = node.PosNode;
                }
                else if (side == 1)
                {
                    Visit(node.PosNode, pass, viewpoint, emitPortal);
                    foreach (WalkPortalRef portal in node.InPortals!)
                        emitPortal(portal, pass);
                    next = node.NegNode;
                }
                else
                {
                    Visit(node.PosNode, pass, viewpoint, emitPortal);
                    next = node.NegNode;
                }
            }
            else
            {
                if (side == 0)
                {
                    Visit(node.NegNode, pass, viewpoint, emitPortal);
                    next = node.PosNode;
                }
                else
                {
                    Visit(node.PosNode, pass, viewpoint, emitPortal);
                    next = node.NegNode;
                }
            }

            if (next is null || next.IsFail) return;
            node = next;
        }
    }

    private static void Visit(
        WalkBspNode? child, int pass, Vector3 viewpoint,
        Action<WalkPortalRef, int> emitPortal)
    {
        if (child is null || child.IsFail) return;
        Walk(child, pass, viewpoint, emitPortal);
    }

    public static bool DrawPortal(
        WalkPView pview, WalkBuilding building, in WalkPortalRef portalRef,
        int pass, IWalkBuildingFrameContext ctx, IWalkPortalPassSink sink)
    {
        ref readonly WalkBldPortal bldPortal = ref building.Portals[portalRef.PortalIndex];
        AddViews(bldPortal.StabList, ctx);
        bool ok = ConstructBuildingView(
            pview, building, in bldPortal, portalRef.Polygon, pass, ctx, sink);
        if (ok && pass != 1)
            sink.OnDrawCells(pview);
        RemoveViews(bldPortal.StabList, ctx);
        return ok;
    }

    public static bool ConstructBuildingView(
        WalkPView pview, WalkBuilding building, in WalkBldPortal bldPortal,
        WalkPolygon polygon, int pass, IWalkBuildingFrameContext ctx,
        IWalkPortalPassSink sink)
    {
        Vector3 viewpoint = ctx.ViewpointInBuilding(building);
        float d = Vector3.Dot(polygon.Plane.Normal, viewpoint) + polygon.Plane.D;
        int side = d > WalkVisibilityMath.Epsilon ? 0
            : d < -WalkVisibilityMath.Epsilon ? 1 : 2;
        if (bldPortal.PortalSide != 0)
        {
            if (side != 1) return false;
        }
        else if (side != 0)
        {
            return false;
        }

        Span<WalkScreenPoint> clipped = stackalloc WalkScreenPoint[64];
        int n = ctx.ClipBuildingPolygon(building, polygon, side, clipped);
        if (n == 0) return false;

        WalkCell? cell = ctx.GetVisible(bldPortal.OtherCellId);
        if (cell is null) return false;
        if (!WalkCopyView.Append(
                cell.TopView, clipped[..n], ctx.Rays, ctx.WorldViewpoint))
            return false;

        if (pass != 2)
            sink.OnPunch(polygon);   // DrawPortalPolyInternal(poly, pass == 1)
        if (pass != 1)
            pview.ConstructView(cell, ToEntryIndex(bldPortal.OtherPortalId), ctx.CellContext);
        return true;
    }

    private static int ToEntryIndex(int otherPortalId)
        => otherPortalId < 0 ? 0xFFFF : otherPortalId;

    private static void AddViews(uint[] stabList, IWalkBuildingFrameContext ctx)
    {
        foreach (uint id in stabList)
            ctx.GetVisible(id)?.PushView();
    }

    private static void RemoveViews(uint[] stabList, IWalkBuildingFrameContext ctx)
    {
        foreach (uint id in stabList)
            ctx.GetVisible(id)?.PopView();
    }
}

/// <summary>Answers whether one building shell's selected mesh is present and
/// drawable right now. A building's meshes arrive asynchronously here, so the
/// id a degrade level names is not by itself proof that anything can be drawn
/// for it; this is the question the walk asks before it commits to drawing a
/// building at all.</summary>
public interface IWalkShellResidency
{
    /// <summary>True when <paramref name="gfxObjId"/>'s geometry can be drawn
    /// in this frame. False covers both "not here yet" and "never draws at
    /// all" — either way the caller must behave as though the whole building
    /// were absent, rather than draw part of it.</summary>
    bool IsShellDrawable(uint gfxObjId);
}

public interface IWalkBuildingFrameContext
{
    Vector3 ViewpointInBuilding(WalkBuilding building);

    float ViewerDistanceTo(WalkBuilding building);

    int ClipBuildingPolygon(
        WalkBuilding building, WalkPolygon polygon, int side, Span<WalkScreenPoint> output);

    WalkCell? GetVisible(uint cellId);
    IWalkRayCaster Rays { get; }
    Vector3 WorldViewpoint { get; }
    IWalkFrameContext CellContext { get; }
}
