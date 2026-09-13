using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

/// <summary>
/// A gameplay plugin compiled into the client rather than discovered on disk.
/// <see cref="Id"/> scopes its storage, commands and UI exactly as a manifest
/// id would for a discovered plugin.
/// </summary>
public sealed record BuiltInPlugin(
    string Id,
    string DisplayName,
    string Version,
    IAcDreamPlugin Plugin)
{
    public string Id { get; } = Require(Id, nameof(Id));
    public string DisplayName { get; } = Require(DisplayName, nameof(DisplayName));
    public string Version { get; } = Require(Version, nameof(Version));
    public IAcDreamPlugin Plugin { get; } =
        Plugin ?? throw new ArgumentNullException(nameof(Plugin));

    private static string Require(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }
}
