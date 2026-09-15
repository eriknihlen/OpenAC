namespace AcDream.Launcher.Core.Plugins;

/// <summary>Launcher-side copy of the API range <c>AcDream.Plugin.Abstractions.PluginApi</c> supports.
/// Launcher.Core cannot reference that assembly (L-303), so a parity test pins these to it.</summary>
public static class LauncherPluginApiRange
{
    public const int Minimum = 1;

    public const int Current = 1;
}
