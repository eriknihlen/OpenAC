using System.Reflection;
using System.Runtime.Loader;

namespace AcDream.Core.Plugins;

internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private const string AbstractionsAssemblyName = "AcDream.Plugin.Abstractions";

    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginDirectory, string pluginEntryPath)
        : base(name: pluginDirectory, isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginEntryPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Share the abstractions assembly with the host — do NOT reload it in the plugin ALC
        if (assemblyName.Name == AbstractionsAssemblyName)
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
