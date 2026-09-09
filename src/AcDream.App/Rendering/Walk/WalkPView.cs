using System.Numerics;

namespace AcDream.App.Rendering.Walk;

public sealed class WalkPView
{
    private static int _masterTimestamp;

    internal static int MasterTimestampForDiagnostics => _masterTimestamp;

    private readonly struct TodoEntry(WalkCell cell, float dist)
    {
        public readonly WalkCell Cell = cell;
        public readonly float Dist = dist;
    }

    public readonly WalkPortalView OutsideView = new();
    public readonly List<WalkCell> CellDrawList = new();
    private readonly List<TodoEntry> _todo = new();
    private readonly WalkPortalView _otherPortalScratch = new();
    private Vector2[] _activeViewVerts = new Vector2[32];
    private int _activeViewVertCount;
    private readonly WalkScreenPoint[] _projectScratch = new WalkScreenPoint[64];
    private readonly WalkScreenPoint[] _clipScratch = new WalkScreenPoint[64];

    public bool DrawLandscape = true;

    public WalkPortalView? PortalList { get; private set; }

    public void ConstructView(WalkCell seed, int throughPortalIndex, IWalkFrameContext ctx)
    {
        OutsideView.ResetForPush();
        _masterTimestamp++;
        _todo.Clear();
        CellDrawList.Clear();
        InitCell(seed, throughPortalIndex, ctx);
        InsCellTodoList(seed, 0f);
        while (_todo.Count > 0)
        {
            WalkCell cell = _todo[^1].Cell;
            _todo.RemoveAt(_todo.Count - 1);
            CellDrawList.Add(cell);
            cell.TopView.CellViewDone = true;
            if (ClipPortals(cell, 0, ctx))
                AddViewToPortals(cell, ctx);
        }
    }

    public bool InitCell(WalkCell cell, int entryPortalIndex, IWalkFrameContext ctx)
    {
        WalkPortalView top = cell.TopView;
        if (top.ViewCount == 0) return false;

        Vector3 viewpoint = ctx.ViewpointIn(cell);
        top.CellViewDone = false;
        top.ViewTimestamp = _masterTimestamp;
        if (top.PortalFlags.Length < cell.Portals.Length)
            top.PortalFlags = new WalkPortalFlags[cell.Portals.Length];

        float maxDistSquared = 0f;
        bool anyLookThrough = false;

        for (int i = 0; i < cell.Portals.Length; i++)
        {
            ref WalkPortalFlags flags = ref top.PortalFlags[i];
            WalkPolygon poly = cell.PortalPolygons[cell.Portals[i].PolygonIndex];

            if (i == entryPortalIndex && !flags.InView)
            {
                // Entered-through portal: forced facing + armed — never re-traversed.
                flags.InView = true;
                flags.Seen = true;
            }
            else
            {
                flags.Seen = false;
                float d = Vector3.Dot(poly.Plane.Normal, viewpoint) + poly.Plane.D;
                if (d <= WalkVisibilityMath.Epsilon && d >= -WalkVisibilityMath.Epsilon)
                {
                    flags.InView = false;      // IN_PLANE: neither surface nor opening
                    anyLookThrough = true;
                }
                else
                {
                    int side = d > WalkVisibilityMath.Epsilon ? 0 : 1;
                    if (side == cell.Portals[i].PortalSide)
                    {
                        flags.InView = false;  // viewer on the look-through side (opening)
                        anyLookThrough = true;
                    }
                    else
                    {
                        flags.InView = true;   // portal polygon faces the viewer
                    }
                }
            }

            if (flags.InView)
            {
                foreach (Vector3 v in poly.Vertices)
                {
                    float dx = viewpoint.X - v.X;
                    float dy = viewpoint.Y - v.Y;
                    float dz = viewpoint.Z - v.Z;
                    float d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 > maxDistSquared) maxDistSquared = d2;
                }
            }
        }
        top.MaxInDistSquared = maxDistSquared;

        if (anyLookThrough && top.ViewCount > 0)
        {
            for (int j = 0; j < cell.Portals.Length; j++)
            {
                ref WalkPortalFlags flags = ref top.PortalFlags[j];
                if (!flags.InView && !flags.Seen) flags.Seen = true;
            }
        }

        top.UpdateCount = top.ViewCount;
        return true;
    }

    public void InsCellTodoList(WalkCell cell, float dist)
    {
        int pos = _todo.Count;
        while (pos > 0 && !(dist < _todo[pos - 1].Dist))
            pos--;
        _todo.Insert(pos, new TodoEntry(cell, dist));
    }

    public bool ClipPortals(WalkCell cell, int startView, IWalkFrameContext ctx)
    {
        WalkPortalView top = cell.TopView;
        PortalList = top;
        if (cell.Portals.Length <= 0) return false;

        bool anyLive = false;
        for (int j = 0; j < cell.Portals.Length; j++)
        {
            ref WalkPortalFlags flags = ref top.PortalFlags[j];
            ref WalkCellPortal portal = ref cell.Portals[j];
            if (!flags.Seen || flags.InView)
            {
                continue;
            }
            if (cell.CachedNeighbors[j] is null && portal.OtherCellId != 0xFFFFFFFFu)
            {
                cell.CachedNeighbors[j] = ctx.GetVisible(portal.OtherCellId);
                if (cell.CachedNeighbors[j] is null)
                {
                    continue;   // not loaded: silently dead
                }
            }
            anyLive = true;
        }
        if (!anyLive) return false;

        for (int i = startView; i < top.ViewCount; i++)
        {
            SetView(top, i);
            for (int j = 0; j < cell.Portals.Length; j++)
            {
                ref WalkPortalFlags flags = ref top.PortalFlags[j];
                if (!flags.Seen || flags.InView) continue;
                ref WalkCellPortal portal = ref cell.Portals[j];
                int n = GetClip(
                    cell, portal.PortalSide,
                    cell.PortalPolygons[portal.PolygonIndex],
                    doClip: true, ctx, _clipScratch);
                if (n == 0)
                {
                    continue;
                }

                if (portal.OtherCellId == 0xFFFFFFFFu)
                {
                    if (DrawLandscape)
                    {
                        if (ctx.ClipLandscape)
                            WalkCopyView.Append(
                                OutsideView, _clipScratch.AsSpan(0, n),
                                ctx.Rays, ctx.WorldViewpoint);
                        else
                            WalkCopyView.AppendFullViewportQuad(
                                OutsideView, ctx.Rays, ctx.WorldViewpoint,
                                ctx.ViewportWidth, ctx.ViewportHeight);
                    }
                }
                else if (cell.CachedNeighbors[j] is WalkCell neighbor)
                {
                    if (!portal.ExactMatch && portal.OtherPortalId >= 0)
                    {
                        n = OtherPortalClip(cell, j, n, ctx);
                        SetView(top, i);   // restore after the far-frame excursion
                        if (n == 0)
                        {
                            continue;
                        }
                    }
                    if (neighbor.NumView != 0)
                        WalkCopyView.Append(
                            neighbor.TopView, _clipScratch.AsSpan(0, n),
                            ctx.Rays, ctx.WorldViewpoint);
                }
            }
        }
        return true;
    }

    private int OtherPortalClip(
        WalkCell cell, int portalIndex, int count, IWalkFrameContext ctx)
    {
        _otherPortalScratch.ResetForPush();
        if (!WalkCopyView.Append(
                _otherPortalScratch, _clipScratch.AsSpan(0, count),
                ctx.Rays, ctx.WorldViewpoint))
            return 0;
        ref WalkCellPortal portal = ref cell.Portals[portalIndex];
        WalkCell far = cell.CachedNeighbors[portalIndex]
            ?? throw new InvalidOperationException(
                "OtherPortalClip requires a resolved neighbor (ClipPortals pass 1 contract).");
        ref WalkCellPortal farPortal = ref far.Portals[portal.OtherPortalId];
        SetView(_otherPortalScratch, 0);
        return GetClip(
            far, farPortal.PortalSide == 0 ? 1 : 0,
            far.PortalPolygons[farPortal.PolygonIndex],
            doClip: true, ctx, _clipScratch);
    }

    public void AddViewToPortals(WalkCell cell, IWalkFrameContext ctx)
    {
        for (int j = 0; j < cell.Portals.Length; j++)
        {
            ref WalkCellPortal portal = ref cell.Portals[j];
            WalkCell? neighbor = cell.CachedNeighbors[j];
            ref WalkPortalFlags flags = ref cell.TopView.PortalFlags[j];
            if (neighbor is null || flags.InView || !flags.Seen || neighbor.NumView == 0)
                continue;
            WalkPortalView neighborTop = neighbor.TopView;
            if (neighborTop.ViewCount == 0) continue;

            if (neighborTop.UpdateCount == 0)
            {
                // First touch this flood: schedule the neighbor.
                if (InitCell(neighbor, ToEntryIndex(portal.OtherPortalId), ctx))
                    InsCellTodoList(neighbor, neighborTop.MaxInDistSquared);
            }
            else if (neighborTop.UpdateCount != neighborTop.ViewCount)
            {
                // Duplicate reach with NEW views since last processed.
                AddToCell(neighbor, ToEntryIndex(portal.OtherPortalId));
                if (neighborTop.CellViewDone)
                    FixCellList(neighbor, cell, ctx);
                neighborTop.UpdateCount = neighborTop.ViewCount;   // fresh re-read after recursion
            }
            else
            {
                continue;   // nothing new; NO SetOtherSeen either
            }

            if (portal.OtherPortalId >= 0)     // full-width signed test (−1 sentinel skips)
                SetOtherSeen(cell, j);
        }
    }

    private static int ToEntryIndex(int otherPortalId)
        => otherPortalId < 0 ? 0xFFFF : otherPortalId;

    public void AddToCell(WalkCell cell, int entryPortalIndex)
    {
        WalkPortalView top = cell.TopView;
        for (int i = top.UpdateCount; i < top.ViewCount; i++)
        {
            for (int j = 0; j < cell.Portals.Length; j++)
            {
                ref WalkPortalFlags flags = ref top.PortalFlags[j];
                if (j == entryPortalIndex && !flags.InView) flags.InView = true;
                if (!flags.InView && !flags.Seen) flags.Seen = true;
            }
        }
    }

    public void SetOtherSeen(WalkCell cell, int portalIndex)
    {
        WalkCell? neighbor = cell.CachedNeighbors[portalIndex];
        if (neighbor is null) return;
        int backIndex = cell.Portals[portalIndex].OtherPortalId;
        ref WalkCellPortal backPortal = ref neighbor.Portals[backIndex];
        if (neighbor.CachedNeighbors[backIndex] is null)
            neighbor.CachedNeighbors[backIndex] = cell;
        ref WalkPortalFlags backFlags = ref neighbor.TopView.PortalFlags[backIndex];
        if (backFlags.InView) backFlags.Seen = true;
    }

    public void FixCellList(WalkCell moved, WalkCell reachedThrough, IWalkFrameContext ctx)
    {
        AdjustCellPlace(moved, reachedThrough);
        AdjustCellView(moved, ctx);
    }

    private void AdjustCellPlace(WalkCell moved, WalkCell reachedThrough)
    {
        WalkPortalView top = reachedThrough.TopView;
        if (!AdjustDrawList(moved, reachedThrough)) return;
        for (int i = 0; i < reachedThrough.Portals.Length; i++)
        {
            ref WalkPortalFlags flags = ref top.PortalFlags[i];
            if (flags.Seen && flags.InView && reachedThrough.CachedNeighbors[i] is WalkCell next)
                AdjustCellPlace(reachedThrough, next);
        }
    }

    private bool AdjustDrawList(WalkCell moved, WalkCell reachedThrough)
    {
        for (int i = 0; i < CellDrawList.Count; i++)
        {
            uint id = CellDrawList[i].CellId;
            if (id == reachedThrough.CellId) break;     // already earlier: nothing to do
            if (id != moved.CellId) continue;

            int at = i;
            while (at < CellDrawList.Count && CellDrawList[at].CellId != reachedThrough.CellId)
                at++;
            if (at == CellDrawList.Count)
                CellDrawList.Add(reachedThrough);
            for (int k = at; k > i; k--)
                CellDrawList[k] = CellDrawList[k - 1];
            CellDrawList[i] = reachedThrough;
            return true;
        }
        return false;
    }

    private void AdjustCellView(WalkCell cell, IWalkFrameContext ctx)
    {
        if (ClipPortals(cell, cell.TopView.UpdateCount, ctx))
            AddViewToPortals(cell, ctx);
    }

    public void SetView(WalkPortalView portalView, int polyIndex)
    {
        WalkViewPoly poly = portalView.View.Polys[polyIndex];
        if (_activeViewVerts.Length < poly.VertexCount)
            _activeViewVerts = new Vector2[poly.VertexCount];
        for (int k = 0; k < poly.VertexCount; k++)
            _activeViewVerts[k] = portalView.View.Vertices[poly.VertexIndex + k].Point;
        _activeViewVertCount = poly.VertexCount;
    }

    public int GetClip(
        WalkCell cell, int side, WalkPolygon polygon, bool doClip,
        IWalkFrameContext ctx, Span<WalkScreenPoint> output)
    {
        int n = polygon.Vertices.Length;
        Matrix4x4 objectToClip = ctx.ObjectToClip(cell);
        for (int i = 0; i < n; i++)
        {
            _projectScratch[i] = WalkScreenClip.TransformToScreen(
                polygon.Vertices[i], objectToClip, ctx.ViewportWidth, ctx.ViewportHeight);
        }
        if (side != 0)
        {
            for (int i = 0; i < n / 2; i++)
                (_projectScratch[i], _projectScratch[n - 1 - i])
                    = (_projectScratch[n - 1 - i], _projectScratch[i]);
        }
        if (!doClip)
        {
            _projectScratch.AsSpan(0, n).CopyTo(output);
            return n;
        }
        return WalkScreenClip.ClipAgainstView(
            _projectScratch.AsSpan(0, n),
            _activeViewVerts.AsSpan(0, _activeViewVertCount),
            output);
    }
}
