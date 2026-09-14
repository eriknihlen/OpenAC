using System.Numerics;

namespace AcDream.Plugin.Abstractions;

/// <summary>
/// Immediate-mode drawing on top of the game frame. The host runs every
/// registered callback once per rendered frame, on the render thread, inside
/// an open Dear ImGui frame; the callback issues <c>ImGui.*</c> calls through
/// the host's shared ImGui.NET assembly and nothing else. Intended for
/// editor-shaped tooling (route builders, rule editors, state-machine
/// editors); player-facing windows belong in markup panels so they wear the
/// game's own look.
/// </summary>
public interface IImmediateUiHost
{
    /// <summary>False on hosts without a renderer or with the overlay disabled.</summary>
    bool IsAvailable => false;

    /// <summary>
    /// Adds a per-frame draw callback. Disposing the lease removes it; the
    /// host also removes it when the plugin is disabled. A callback that
    /// throws is logged and dropped for the rest of the session.
    /// </summary>
    IDisposable Register(string name, Action draw) => NoOpImmediateUiHost.Lease;

    /// <summary>
    /// Where a map position lands on the overlay this frame, in the pixels
    /// <c>ImGui.GetIO().DisplaySize</c> spans, so a drawer can mark the
    /// world (route rings, paths). False when the point is behind the
    /// camera or the host has no camera to project with. Only meaningful
    /// from inside a registered draw callback.
    /// </summary>
    bool TryProjectToScreen(in PluginNavigationPosition position, out float screenX, out float screenY)
    {
        screenX = 0f;
        screenY = 0f;
        return false;
    }

    /// <summary>
    /// Whether the host draws geometry in the world itself: rings and
    /// strips placed on the ground and depth-tested against walls, floors
    /// and creatures, where the overlay sits over everything. False on
    /// hosts without a world renderer.
    /// </summary>
    bool WorldGeometryAvailable => false;

    /// <summary>
    /// A ring on the ground at a map position: a flat band
    /// <paramref name="thicknessMeters"/> wide at <paramref name="radiusMeters"/>,
    /// with a wall <paramref name="heightMeters"/> tall standing on it so the
    /// ring reads at a grazing angle. Drawn once, in the next frame's world
    /// pass: submit again every frame it should show. Colour RGBA, 0..1.
    /// </summary>
    void AddWorldRing(in PluginNavigationPosition center, float radiusMeters, float thicknessMeters, float heightMeters, Vector4 color)
    {
    }

    /// <summary>A flat strip along the ground from one map position to another, drawn like <see cref="AddWorldRing"/>.</summary>
    void AddWorldLine(in PluginNavigationPosition from, in PluginNavigationPosition to, float thicknessMeters, Vector4 color)
    {
    }
}

public sealed class NoOpImmediateUiHost : IImmediateUiHost
{
    public static NoOpImmediateUiHost Instance { get; } = new();

    internal static IDisposable Lease { get; } = new NoOpLease();

    private NoOpImmediateUiHost()
    {
    }

    private sealed class NoOpLease : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
