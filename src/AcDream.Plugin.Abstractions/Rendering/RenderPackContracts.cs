namespace AcDream.Plugin.Abstractions.Rendering;

public static class RenderPackApi
{
    public const int Current = 1;

    public const int MinimumSupported = 1;

    public static bool IsSupported(int apiVersion) =>
        apiVersion >= MinimumSupported && apiVersion <= Current;
}

/// <summary>
/// Optional plugin entry point for declarative graphics enhancements. The host
/// invokes this only in a graphical process; no-window hosts never expose a
/// registry or ask a render-pack entry point to register.
/// </summary>
public interface IRenderPackPlugin
{
    /// <summary>Register every pack supplied by this plugin.</summary>
    void Register(IRenderPackRegistry registry);
}

public interface IRenderPackRegistry
{
    /// <summary>
    /// Register one immutable descriptor and its lazy asset source. Disposing
    /// the returned handle withdraws the pack and every host reference to the
    /// asset source.
    /// </summary>
    IDisposable Register(RenderPackDescriptor descriptor, IRenderPackAssets assets);
}

public interface IRenderPackAssets
{
    /// <summary>Open a new readable stream for a descriptor-declared key.</summary>
    Stream OpenRead(string assetKey);
}
