namespace AcDream.Plugin.Abstractions.Rendering;

/// <summary>
/// The version of the render-pack contract this client speaks. A pack declares
/// the version it was written against and the client refuses packs outside the
/// range it supports.
/// </summary>
public static class RenderPackApi
{
    /// <summary>The newest render-pack contract version this client understands.</summary>
    public const int Current = 1;

    /// <summary>The oldest render-pack contract version this client still accepts.</summary>
    public const int MinimumSupported = 1;

    /// <summary>Whether a pack written against this contract version can run here.</summary>
    /// <param name="apiVersion">The version a pack declares.</param>
    /// <returns>
    /// True when the version falls between <see cref="MinimumSupported"/> and
    /// <see cref="Current"/> inclusive; false otherwise, and the client then
    /// refuses the pack.
    /// </returns>
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
    /// <param name="registry">The host registry to register into.</param>
    void Register(IRenderPackRegistry registry);
}

/// <summary>
/// The host's list of available render packs. A registered pack becomes
/// selectable by the player; it is not activated by registering it.
/// </summary>
public interface IRenderPackRegistry
{
    /// <summary>
    /// Register one immutable descriptor and its lazy asset source. Disposing
    /// the returned handle withdraws the pack and every host reference to the
    /// asset source.
    /// </summary>
    /// <param name="descriptor">What the pack declares it needs and draws.</param>
    /// <param name="assets">
    /// Where the pack's compiled shader files are read from, opened only when
    /// the pack is actually selected.
    /// </param>
    /// <returns>The handle that withdraws this registration when disposed.</returns>
    IDisposable Register(RenderPackDescriptor descriptor, IRenderPackAssets assets);

    /// <summary>
    /// The folder the registering plugin was installed in: the one holding
    /// its <c>plugin.json</c> and whatever else its package ships. The host
    /// loads a plugin's assemblies from memory, which leaves
    /// <see cref="System.Reflection.Assembly.Location"/> empty; a pack that
    /// reads files from its own package reads them relative to this. Null on
    /// a host that does not say.
    /// </summary>
    string? PluginDirectory => null;
}

/// <summary>Supplies a pack's shader files to the client on demand.</summary>
public interface IRenderPackAssets
{
    /// <summary>Open a new readable stream for a descriptor-declared key.</summary>
    /// <param name="assetKey">
    /// The asset name exactly as a pass or pipeline-variant declaration spells
    /// it, such as a compiled SPIR-V file name.
    /// </param>
    /// <returns>
    /// A fresh stream the caller disposes. Implementations throw when the key
    /// is unknown or points outside the pack's own files.
    /// </returns>
    Stream OpenRead(string assetKey);
}
