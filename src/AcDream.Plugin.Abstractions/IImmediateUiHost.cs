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
