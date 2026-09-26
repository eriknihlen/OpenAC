using System.Reflection;
using System.Runtime.Loader;

namespace AcDream.Core.Plugins;

/// <summary>
/// One plugin's own set of assemblies, unloadable as a unit. Every assembly
/// is read into memory and loaded from there rather than mapped from its
/// file, so nothing in the plugin's folder stays open while the plugin runs:
/// an update can overwrite the files in place, and a reload loads the new
/// copy.
/// </summary>
internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private const string AbstractionsAssemblyName = "AcDream.Plugin.Abstractions";

    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginDirectory, string pluginEntryPath)
        : base(name: pluginDirectory, isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginEntryPath);
    }

    /// <summary>Loads the plugin's entry assembly.</summary>
    internal Assembly LoadEntry(string path) => LoadFromBytes(path);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Share the abstractions assembly with the host — do NOT reload it in the plugin ALC
        if (assemblyName.Name == AbstractionsAssemblyName)
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromBytes(path);
    }

    /// <summary>
    /// Reads the assembly, and its symbols when they sit beside it so a stack
    /// trace still names files and lines, and loads both from memory.
    /// </summary>
    private Assembly LoadFromBytes(string path)
    {
        byte[] image = File.ReadAllBytes(path);
        string symbolsPath = Path.ChangeExtension(path, ".pdb");
        byte[]? symbols = File.Exists(symbolsPath)
            ? File.ReadAllBytes(symbolsPath)
            : null;
        using var imageStream = new MemoryStream(image, writable: false);
        if (symbols is null)
            return LoadFromStream(imageStream);
        using var symbolStream = new MemoryStream(symbols, writable: false);
        return LoadFromStream(imageStream, symbolStream);
    }
}
