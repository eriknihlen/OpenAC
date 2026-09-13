using System.Reflection;
using System.Runtime.Loader;

namespace AcDream.Core.Plugins;

internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    // Assemblies a plugin must share with the host rather than load privately:
    // the contract, so interface identity matches, and ImGui.NET, so a plugin's
    // ImGui calls land in the host's native context.
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.Ordinal)
    {
        "AcDream.Plugin.Abstractions",
        "ImGui.NET",
    };

    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginDirectory, string pluginEntryPath)
        : base(name: pluginDirectory, isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginEntryPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name && SharedAssemblies.Contains(name))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
