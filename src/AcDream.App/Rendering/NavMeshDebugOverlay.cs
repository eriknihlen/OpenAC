using System.Numerics;
using AcDream.Runtime.Navigation;
using AcDream.Core.Navigation;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Rendering;

/// <summary>
/// The navigation view. The route of a walk under way, or of a route asked for
/// alone, is drawn as a magenta line from the character along the legs still to
/// walk, a leap drawn as an arc, with a dimmer line on to a goal beyond a stage.
/// While the grid is shown it adds the grid around the player: every tight
/// point in red and every second clear point in green. It also adds the path the
/// route was straightened from, a post at the end of every leg, and a violet
/// cylinder around each portal routes keep out of.
/// Everything it draws is hidden behind walls, floors and ceilings, as the world is.
/// </summary>
internal sealed class NavMeshDebugOverlay
{
    internal const float DrawRadius = 20f;

    /// <summary>Clear points are drawn on every second column and row; tight points, beside a ledge or near a wall, are all drawn.</summary>
    internal const int DrawStride = 2;

    private const float MarkerHalfSize = 0.08f;
    private const float PortalHeight = 2f;
    private const int LeapArcSegments = 8;
    private const float LeapArcHeight = 1f;

    private static readonly Vector3 NodeLift = new(0f, 0f, 0.04f);
    private static readonly Vector3 PathLift = new(0f, 0f, 0.15f);
    private static readonly Vector3 RouteLift = new(0f, 0f, 0.1f);
    private static readonly Vector3 LegPost = new(0f, 0f, 1f);
    private static readonly Vector3 ClearColour = new(0.2f, 0.95f, 0.35f);
    private static readonly Vector3 TightColour = new(0.9f, 0.25f, 0.15f);
    private static readonly Vector3 PathColour = new(0.85f, 0.85f, 0.85f);
    private static readonly Vector3 RouteColour = new(1f, 0f, 1f);
    private static readonly Vector3 BeyondColour = new(0.5f, 0f, 0.5f);

    /// <summary>Portals, which a walk never goes through: a warm violet, apart from every other mark.</summary>
    private static readonly Vector3 PortalColour = new(0.8f, 0.3f, 1f);

    private readonly NavigationWalkController _walk;

    public NavMeshDebugOverlay(NavigationWalkController walk)
    {
        _walk = walk ?? throw new ArgumentNullException(nameof(walk));
    }

    /// <summary>Whether there is a route to draw while the grid is hidden: a request under way, or a route found alone.</summary>
    public bool HasRouteToShow => _walk.IsBusy || _walk.Report.State == NavigationWalkState.Planned;

    public void Draw(DebugLineRenderer lines, PlayerMovementController? player, bool showGrid)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (showGrid && player is not null && _walk.Grid is { } grid)
            DrawGrid(lines, grid, player.Position);
        if (showGrid && player is not null)
        {
            // The portals a walk keeps out of, so a walk that runs into one shows at once
            // whether the keep-out saw it.
            foreach (NavAvoidance portal in _walk.PortalsNear(player.Position, DrawRadius))
                lines.AddCylinder(portal.Centre, portal.Radius, PortalHeight, PortalColour, hiddenByScene: false);
        }
        DrawRoute(lines, player?.Position, showGrid);
    }

    private static void DrawGrid(DebugLineRenderer lines, NavGrid grid, Vector3 centre)
    {
        var alongX = new Vector3(MarkerHalfSize, 0f, 0f);
        var alongY = new Vector3(0f, MarkerHalfSize, 0f);
        foreach ((Vector3 at, bool clear) in GridMarks(grid, centre))
        {
            Vector3 colour = clear ? ClearColour : TightColour;
            lines.AddLine(at - alongX, at + alongX, colour, hiddenByScene: true);
            lines.AddLine(at - alongY, at + alongY, colour, hiddenByScene: true);
        }
    }

    /// <summary>
    /// Where the grid's points around a centre are drawn, and whether each is clear: every
    /// tight point within <see cref="DrawRadius"/> across, and the clear ones on every
    /// <see cref="DrawStride"/>th column and row.
    /// </summary>
    internal static IEnumerable<(Vector3 At, bool Clear)> GridMarks(NavGrid grid, Vector3 centre)
    {
        int reach = (int)(DrawRadius / grid.CellSize);
        int centreX = (int)MathF.Floor((centre.X - grid.OriginX) / grid.CellSize);
        int centreY = (int)MathF.Floor((centre.Y - grid.OriginY) / grid.CellSize);
        int startX = Math.Max(0, centreX - reach);
        int startY = Math.Max(0, centreY - reach);
        int endX = Math.Min(grid.Side - 1, centreX + reach);
        int endY = Math.Min(grid.Side - 1, centreY + reach);
        for (int y = startY; y <= endY; y++)
        {
            for (int x = startX; x <= endX; x++)
            {
                bool onStride = x % DrawStride == 0 && y % DrawStride == 0;
                (int first, int count) = grid.NodesInColumn(x, y);
                for (int node = first; node < first + count; node++)
                {
                    bool clear = grid.IsClear(node);
                    if (clear && !onStride)
                        continue;
                    Vector3 position = grid.Position(node);
                    float dx = position.X - centre.X;
                    float dy = position.Y - centre.Y;
                    if ((dx * dx) + (dy * dy) > DrawRadius * DrawRadius)
                        continue;
                    yield return (position + NodeLift, clear);
                }
            }
        }
    }

    private void DrawRoute(DebugLineRenderer lines, Vector3? character, bool showGrid)
    {
        if (_walk.Goal is { } goal)
            lines.AddCylinder(goal.Position, goal.ArrivalMeters, 0.1f, RouteColour, hiddenByScene: true);
        if (_walk.Route is not { Outcome: NavRouteOutcome.Routed } route || route.Legs.Count == 0)
            return;

        if (showGrid)
        {
            for (int index = 1; index < route.Path.Count; index++)
                lines.AddLine(route.Path[index - 1] + PathLift, route.Path[index] + PathLift, PathColour, hiddenByScene: true);
            foreach (Vector3 end in route.Legs)
                lines.AddLine(end, end + LegPost, RouteColour, hiddenByScene: true);
        }

        int next = 1;
        if (_walk.LegIndex is { } walking && character is { } at && walking < route.Legs.Count)
        {
            lines.AddLine(at + RouteLift, route.Legs[walking] + RouteLift, RouteColour, hiddenByScene: true);
            next = walking + 1;
        }
        for (int index = Math.Max(next, 1); index < route.Legs.Count; index++)
            DrawLeg(lines, route, index);
        if (_walk.IsStaged && _walk.Goal is { } beyond)
            lines.AddLine(route.Legs[^1] + RouteLift, beyond.Position + RouteLift, BeyondColour, hiddenByScene: true);
    }

    /// <summary>Draws one leg of a route: a straight line, or an arc for a leap.</summary>
    private static void DrawLeg(DebugLineRenderer lines, NavRoute route, int index)
    {
        Vector3 from = route.Legs[index - 1] + RouteLift;
        Vector3 to = route.Legs[index] + RouteLift;
        if (!route.Leaps.Any(leap => leap.LegIndex == index))
        {
            lines.AddLine(from, to, RouteColour, hiddenByScene: true);
            return;
        }
        Vector3 previous = from;
        for (int segment = 1; segment <= LeapArcSegments; segment++)
        {
            float along = segment / (float)LeapArcSegments;
            Vector3 point = Vector3.Lerp(from, to, along) + new Vector3(0f, 0f, LeapArcHeight * 4f * along * (1f - along));
            lines.AddLine(previous, point, RouteColour, hiddenByScene: true);
            previous = point;
        }
    }
}
