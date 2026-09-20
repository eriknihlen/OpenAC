namespace AcDream.Plugin.Abstractions;

/// <summary>Describes a resource declared by a plugin package.</summary>
public sealed record PluginResourceDeclaration(string Id, string RelativePath, bool Optional = false);

/// <summary>Provides safe access to resources declared by the host for a plugin.</summary>
public interface IPluginResourceCatalog
{
    /// <summary>Opens a declared resource, or returns null when it is absent.</summary>
    Stream? OpenRead(string resourceId);

    /// <summary>Returns declared resource IDs available in this catalog.</summary>
    IReadOnlyList<string> List(string prefix = "");

    /// <summary>Returns the layered route/data files, user overrides taking precedence.</summary>
    IReadOnlyList<string> ListDataFiles(string relativeDirectory);
}

/// <summary>Inert resources for hosts without package data.</summary>
public sealed class NoOpPluginResourceCatalog : IPluginResourceCatalog
{
    /// <summary>The shared inert instance.</summary>
    public static NoOpPluginResourceCatalog Instance { get; } = new();
    private NoOpPluginResourceCatalog() { }
    /// <inheritdoc />
    public Stream? OpenRead(string resourceId) => null;
    /// <inheritdoc />
    public IReadOnlyList<string> List(string prefix = "") => Array.Empty<string>();
    /// <inheritdoc />
    public IReadOnlyList<string> ListDataFiles(string relativeDirectory) => Array.Empty<string>();
}
