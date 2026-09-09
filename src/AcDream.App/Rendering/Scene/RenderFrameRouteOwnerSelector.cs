namespace AcDream.App.Rendering.Scene;

/// <summary>
/// Projects the retained PView route stream into particle-owner identities.
/// This keeps attached-effect routing on the same visibility decision as mesh
/// submission without walking spatial/gameplay entities a second time.
/// </summary>
internal static class RenderFrameRouteOwnerSelector
{
    public static void Replace(
        HashSet<uint> destination,
        in RenderFrameView view,
        RenderFrameCandidateRoute route,
        int routeIndex,
        uint cellId)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        Union(
            destination,
            in view,
            route,
            routeIndex,
            cellId);
    }

    public static void Union(
        HashSet<uint> destination,
        in RenderFrameView view,
        RenderFrameCandidateRoute route,
        int routeIndex,
        uint cellId)
    {
        ArgumentNullException.ThrowIfNull(destination);
        VisitExact(
            destination,
            in view,
            route,
            routeIndex,
            cellId,
            remove: false);
    }

    public static void ExceptRoute(
        HashSet<uint> destination,
        in RenderFrameView view,
        RenderFrameCandidateRoute route)
    {
        ArgumentNullException.ThrowIfNull(destination);

        ReadOnlySpan<RenderFrameCandidateRange> ranges =
            view.RouteRanges;
        ReadOnlySpan<RenderProjectionRecord> candidates =
            view.RouteCandidates;
        for (int index = 0; index < ranges.Length; index++)
        {
            RenderFrameCandidateRange range = ranges[index];
            if (range.Route != route)
                continue;

            ApplyRange(
                destination,
                candidates,
                in range,
                remove: true);
        }
    }

    private static void VisitExact(
        HashSet<uint> destination,
        in RenderFrameView view,
        RenderFrameCandidateRoute route,
        int routeIndex,
        uint cellId,
        bool remove)
    {
        ReadOnlySpan<RenderFrameCandidateRange> ranges =
            view.RouteRanges;
        ReadOnlySpan<RenderProjectionRecord> candidates =
            view.RouteCandidates;
        for (int index = 0; index < ranges.Length; index++)
        {
            RenderFrameCandidateRange range = ranges[index];
            if (range.Route != route
                || range.RouteIndex != routeIndex
                || range.CellId != cellId)
            {
                continue;
            }

            ApplyRange(
                destination,
                candidates,
                in range,
                remove);
            return;
        }
    }

    private static void ApplyRange(
        HashSet<uint> destination,
        ReadOnlySpan<RenderProjectionRecord> candidates,
        in RenderFrameCandidateRange range,
        bool remove)
    {
        ReadOnlySpan<RenderProjectionRecord> owners =
            candidates.Slice(range.Offset, range.Count);
        for (int index = 0; index < owners.Length; index++)
        {
            uint localEntityId = owners[index].Source.LocalEntityId;
            if (localEntityId == 0)
                continue;

            if (remove)
                destination.Remove(localEntityId);
            else
                destination.Add(localEntityId);
        }
    }
}
