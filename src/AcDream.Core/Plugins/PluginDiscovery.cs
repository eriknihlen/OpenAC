namespace AcDream.Core.Plugins;

public sealed record PluginDiscoveryResult(
    string PluginDirectory,
    PluginManifest? Manifest,
    Exception? Error)
{
    public bool Success => Manifest is not null && Error is null;
}

public static class PluginDiscovery
{
    public static IReadOnlyList<PluginDiscoveryResult> Scan(string pluginsRootDirectory)
    {
        if (!Directory.Exists(pluginsRootDirectory))
            return Array.Empty<PluginDiscoveryResult>();

        var results = new List<PluginDiscoveryResult>();
        foreach (var subdir in Directory.EnumerateDirectories(pluginsRootDirectory).OrderBy(p => p, StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(subdir, "plugin.json");
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = PluginManifest.Parse(json);
                results.Add(new PluginDiscoveryResult(subdir, manifest, null));
            }
            catch (Exception ex)
            {
                results.Add(new PluginDiscoveryResult(subdir, null, ex));
            }
        }
        return results;
    }
}
