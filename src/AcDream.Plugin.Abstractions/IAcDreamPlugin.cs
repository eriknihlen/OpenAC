// src/AcDream.Plugin.Abstractions/IAcDreamPlugin.cs
namespace AcDream.Plugin.Abstractions;

public static class PluginApi
{
    public const int Current = 1;

    public const int MinimumSupported = 1;

    /// <summary>Whether a plugin declaring <paramref name="apiVersion"/> can load here.</summary>
    public static bool IsSupported(int apiVersion)
        => apiVersion >= MinimumSupported && apiVersion <= Current;
}

public interface IAcDreamPlugin
{
    void Initialize(IPluginHost host);

    void Enable();
    void Disable();
}
