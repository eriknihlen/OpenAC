// src/AcDream.Plugin.Abstractions/IAcDreamPlugin.cs
namespace AcDream.Plugin.Abstractions;

/// <summary>
/// The version of this plugin contract, and the range of versions a host
/// built against it will load.
/// </summary>
public static class PluginApi
{
    /// <summary>The contract version this build of the client provides.</summary>
    public const int Current = 1;

    /// <summary>The oldest contract version this build still accepts.</summary>
    public const int MinimumSupported = 1;

    /// <summary>Whether a plugin declaring <paramref name="apiVersion"/> can load here.</summary>
    public static bool IsSupported(int apiVersion)
        => apiVersion >= MinimumSupported && apiVersion <= Current;
}

/// <summary>
/// The entry point a plugin implements. The host constructs the type, calls
/// <see cref="Initialize"/> once, then <see cref="Enable"/>, and calls
/// <see cref="Disable"/> once when the plugin is unloaded or the client
/// shuts down.
/// </summary>
public interface IAcDreamPlugin
{
    /// <summary>
    /// Hands the plugin its host. Called once, before <see cref="Enable"/>;
    /// keep the reference, because this is the only way to reach the client.
    /// </summary>
    void Initialize(IPluginHost host);

    /// <summary>
    /// Starts the plugin's work: register panels, commands, hotkeys and
    /// event handlers here. Throwing from this call fails the load, and the
    /// host then calls <see cref="Disable"/> and releases everything the
    /// plugin registered.
    /// </summary>
    void Enable();

    /// <summary>
    /// Stops the plugin's work and releases anything it owns itself. The
    /// host releases the registrations it handed out regardless, even if
    /// this call throws.
    /// </summary>
    void Disable();
}
