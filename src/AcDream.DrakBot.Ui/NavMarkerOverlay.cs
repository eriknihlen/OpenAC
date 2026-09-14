using System.Numerics;
using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;
using ImGuiNET;

namespace AcDream.DrakBot.Ui;

/// <summary>
/// Draws the route in the world, the way RynthAi's nav marker renderer
/// does: a ring on the ground at every travel point within range (red for
/// the step being walked, cyan otherwise, amber for an NPC or vendor stop)
/// and a strip from each point to the next. On a host that draws world
/// geometry the markers are real geometry in the scene - behind walls,
/// under the character, hidden by what stands in front - as RynthAi's
/// Nav3D rings are; otherwise they fall back to the host's projection into
/// a full-screen, input-less overlay window, with the step numbers.
/// </summary>
public sealed class NavMarkerOverlay(BotController controller, IAutomationSurface surface, IImmediateUiHost ui)
{
    private const int RingSegments = 24;
    private const double DrawRangeMeters = 150d;
    /// <summary>World-geometry sizes per unit of the profile's line thickness, and the ring's standing wall.</summary>
    private const float BandMetersPerUnit = 0.05f;
    private const float StripMetersPerUnit = 0.04f;
    private const float RingWallMeters = 0.35f;
    private static readonly Vector4 WorldRing = new(0.15f, 0.85f, 0.90f, 0.85f);
    private static readonly Vector4 WorldActive = new(1f, 0.27f, 0.27f, 0.95f);
    private static readonly Vector4 WorldAction = new(1f, 0.85f, 0.2f, 0.85f);
    private static readonly Vector4 WorldLine = new(0.1f, 0.55f, 1f, 0.7f);
    private static readonly uint ColorLine = ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.85f, 0.90f, 0.7f));
    private static readonly uint ColorRing = ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.85f, 0.90f, 0.95f));
    private static readonly uint ColorActive = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.25f, 0.25f, 1f));
    private static readonly uint ColorAction = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.85f, 0.2f, 0.95f));
    private static readonly float[] Cos = Enumerable.Range(0, RingSegments).Select(i => MathF.Cos(i * MathF.PI * 2f / RingSegments)).ToArray();
    private static readonly float[] Sin = Enumerable.Range(0, RingSegments).Select(i => MathF.Sin(i * MathF.PI * 2f / RingSegments)).ToArray();

    private readonly List<(int Index, Vector2 Screen, float Depth)> _centers = [];

    public void Draw()
    {
        NavigationSettings nav = controller.Profile.Navigation;
        if (!nav.ShowMarkers)
            return;
        Route? route = controller.Navigation.Route ?? (controller.DraftRoute.IsEmpty ? null : controller.DraftRoute);
        if (route is null || route.IsEmpty)
            return;
        PluginNavigationSnapshot snapshot = surface.Navigation.Snapshot;
        if (!snapshot.IsAvailable || snapshot.IsPortalSpace)
            return;
        if (ui.WorldGeometryAvailable)
        {
            SubmitWorld(route, nav, snapshot);
            return;
        }

        Vector2 display = ImGui.GetIO().DisplaySize;
        if (display.X <= 0f || display.Y <= 0f)
            return;
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(display);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        bool open = true;
        bool begun = ImGui.Begin("##drakbot-nav-overlay", ref open,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings);
        if (begun)
            DrawRoute(route, nav, snapshot);
        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    /// <summary>The route as geometry in the world: rings at the travel points in range, strips between consecutive ones.</summary>
    private void SubmitWorld(Route route, NavigationSettings nav, in PluginNavigationSnapshot snapshot)
    {
        int active = ReferenceEquals(route, controller.Navigation.Route) ? controller.Navigation.WaypointIndex : -1;
        PluginNavigationPosition player = snapshot.Position;
        float ringRadius = Math.Max(0.25f, nav.MarkerRingMeters);
        float units = Math.Max(0.5f, nav.MarkerLineThickness);
        float band = units * BandMetersPerUnit;
        float strip = units * StripMetersPerUnit;
        double heightOffset = nav.MarkerHeightOffset / 240d;

        int lastIndex = -1;
        PluginNavigationPosition last = default;
        PluginNavigationPosition first = default;
        int firstIndex = -1;
        int loopStart = Math.Clamp(route.LoopStart, 0, Math.Max(0, route.Waypoints.Count - 1));
        for (int index = 0; index < route.Waypoints.Count; index++)
        {
            Waypoint waypoint = route.Waypoints[index];
            if (!waypoint.Kind.HasPosition())
                continue;
            PluginNavigationPosition at = waypoint.ToPosition();
            if (at.HorizontalDistanceMeters(player) > DrawRangeMeters)
            {
                lastIndex = -1;
                continue;
            }
            at = at with { Elevation = at.Elevation + heightOffset };
            bool isActive = index == active;
            Vector4 color = isActive ? WorldActive : waypoint.Kind == WaypointKind.Point ? WorldRing : WorldAction;
            ui.AddWorldRing(at, ringRadius, isActive ? band * 1.3f : band, isActive ? RingWallMeters * 1.3f : RingWallMeters, color);
            if (lastIndex == index - 1)
                ui.AddWorldLine(last, at, strip, WorldLine);
            if (index == loopStart)
            {
                first = at;
                firstIndex = index;
            }
            lastIndex = index;
            last = at;
        }
        // A loop closes back on the step it starts from.
        if (route.Mode == RouteMode.Loop && firstIndex >= 0 && lastIndex == route.Waypoints.Count - 1 && lastIndex != firstIndex)
            ui.AddWorldLine(last, first, strip, WorldLine);
    }

    private void DrawRoute(Route route, NavigationSettings nav, in PluginNavigationSnapshot snapshot)
    {
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        int active = ReferenceEquals(route, controller.Navigation.Route) ? controller.Navigation.WaypointIndex : -1;
        PluginNavigationPosition player = snapshot.Position;
        float ringRadius = Math.Max(0.25f, nav.MarkerRingMeters);
        float lineThickness = Math.Max(1f, nav.MarkerLineThickness);
        double heightOffset = nav.MarkerHeightOffset / 240d;

        // Centres first, so lines can join consecutive travel points.
        _centers.Clear();
        int lastIndex = -1;
        Vector2 lastScreen = default;
        float lastDepth = 0f;
        bool lastVisible = false;
        for (int index = 0; index < route.Waypoints.Count; index++)
        {
            Waypoint waypoint = route.Waypoints[index];
            if (!waypoint.Kind.HasPosition())
            {
                continue;
            }
            PluginNavigationPosition at = waypoint.ToPosition();
            double distance = at.HorizontalDistanceMeters(player);
            if (distance > DrawRangeMeters)
            {
                lastVisible = false;
                continue;
            }
            at = at with { Elevation = at.Elevation + heightOffset };
            bool visible = ui.TryProjectToScreen(at, out float sx, out float sy);
            var screen = new Vector2(sx, sy);
            float depth = (float)distance;
            if (visible)
                _centers.Add((index, screen, depth));
            if (visible && lastVisible && lastIndex == index - 1)
            {
                float average = (depth + lastDepth) * 0.5f;
                float thick = Math.Clamp(lineThickness * 60f / Math.Max(average, 1f), lineThickness * 0.5f, lineThickness * 2f);
                draw.AddLine(lastScreen, screen, ColorLine, thick);
            }
            lastIndex = index;
            lastScreen = screen;
            lastDepth = depth;
            lastVisible = visible;

            // The ring: segments on the ground around the point, each projected.
            uint color = index == active ? ColorActive : waypoint.Kind == WaypointKind.Point ? ColorRing : ColorAction;
            float ringThick = index == active ? lineThickness * 1.3f : lineThickness;
            Vector2 previous = default;
            bool previousVisible = false;
            Vector2 first = default;
            bool firstVisible = false;
            for (int s = 0; s < RingSegments; s++)
            {
                PluginNavigationPosition rim = at with
                {
                    EastWest = at.EastWest + Cos[s] * ringRadius / 240d,
                    NorthSouth = at.NorthSouth + Sin[s] * ringRadius / 240d,
                };
                bool rimVisible = ui.TryProjectToScreen(rim, out float rx, out float ry);
                var rimScreen = new Vector2(rx, ry);
                if (s == 0)
                {
                    first = rimScreen;
                    firstVisible = rimVisible;
                }
                else if (rimVisible && previousVisible)
                {
                    draw.AddLine(previous, rimScreen, color, ringThick);
                }
                previous = rimScreen;
                previousVisible = rimVisible;
            }
            if (previousVisible && firstVisible)
                draw.AddLine(previous, first, color, ringThick);
        }

        foreach ((int index, Vector2 screen, float depth) in _centers)
        {
            if (depth > 60f && index != active)
                continue;
            string label = (index + 1).ToString();
            Vector2 size = ImGui.CalcTextSize(label);
            draw.AddText(screen - new Vector2(size.X * 0.5f, 16f), index == active ? ColorActive : ColorRing, label);
        }
    }
}

internal static class WaypointKindExtensions
{
    /// <summary>Steps recorded somewhere in the world; pauses and chat carry no place.</summary>
    public static bool HasPosition(this WaypointKind kind) =>
        kind is WaypointKind.Point or WaypointKind.Npc or WaypointKind.Vendor;
}
