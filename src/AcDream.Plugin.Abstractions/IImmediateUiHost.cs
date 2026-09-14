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
