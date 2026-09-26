using System.Reflection;
using System.Runtime.Loader;

namespace AcDream.Core.Plugins;

/// <summary>
/// One plugin's own set of assemblies, unloadable as a unit. Every assembly
/// comes from the plugin's <see cref="PluginPackageSnapshot"/>, read into
/// memory when the plugin was prepared, never from its file: nothing in the
/// plugin's folder stays open while the plugin runs, and an update written
/// into the folder cannot mix its assemblies into the copy already running.
/// </summary>
internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private const string AbstractionsAssemblyName = "AcDream.Plugin.Abstractions";

    private readonly AssemblyDependencyResolver _resolver;
    private readonly PluginPackageSnapshot _package;

    public PluginAssemblyLoadContext(PluginPackageSnapshot package, string pluginEntryPath)
        : base(name: package.Directory, isCollectible: true)
    {
        _package = package;
        _resolver = new AssemblyDependencyResolver(pluginEntryPath);
    }

    /// <summary>Loads the plugin's entry assembly.</summary>
    internal Assembly LoadEntry(string path) =>
        FromPackage(path)
            ?? throw new FileNotFoundException($"entry dll not found: {path}", path);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Share the abstractions assembly with the host — do NOT reload it in the plugin ALC
        if (assemblyName.Name == AbstractionsAssemblyName)
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : FromPackage(path);
    }

    /// <summary>
    /// Loads an assembly, with its symbols when the package had them so a
    /// stack trace still names files and lines. An assembly that was not in
    /// the package when it was read is not loaded, whatever is on disk now.
    /// </summary>
    private Assembly? FromPackage(string path)
    {
        if (!_package.TryGetAssembly(path, out byte[] image, out byte[]? symbols))
            return null;
        using var imageStream = new MemoryStream(image, writable: false);
        if (symbols is null)
            return LoadFromStream(imageStream);
        using var symbolStream = new MemoryStream(symbols, writable: false);
        return LoadFromStream(imageStream, symbolStream);
    }
}
