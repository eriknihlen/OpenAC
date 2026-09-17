namespace AcDream.Plugin.Abstractions;

/// <summary>
/// The host's text clipboard. Hosts without a window have none, so callers
/// must handle a false return rather than assume the text was copied.
/// </summary>
public interface IPluginClipboard
{
    /// <summary>
    /// Puts <paramref name="text"/> on the system clipboard. Returns false
    /// when the host has no clipboard or the attempt failed.
    /// </summary>
    bool TrySetText(string text) => false;
}

/// <summary>
/// The clipboard a host without a window offers: it copies nothing and
/// always returns false.
/// </summary>
public sealed class NoOpPluginClipboard : IPluginClipboard
{
    /// <summary>The shared instance; this type holds no state.</summary>
    public static NoOpPluginClipboard Instance { get; } = new();

    private NoOpPluginClipboard()
    {
    }
}
