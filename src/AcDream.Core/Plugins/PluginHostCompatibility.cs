namespace AcDream.Core.Plugins;

/// <summary>Checks a manifest's declared client versions and hosts against the running host.</summary>
public static class PluginHostCompatibility
{
    public static string? Evaluate(
        PluginManifest manifest,
        PluginHostKind host,
        PluginHostVersion? hostVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!manifest.Hosts.Contains(host))
        {
            return $"runs only on the "
                + $"{string.Join(" or ", manifest.Hosts.Select(DescribeHost))} host";
        }

        if (hostVersion is not { } version)
            return null;

        if (manifest.MinHostVersion is { } min && version.CompareTo(min) < 0)
            return $"requires OpenAC {min} or newer (this is {version})";

        if (manifest.MaxHostVersion is { } max && version.CompareTo(max) > 0)
            return $"supports OpenAC up to {max} (this is {version})";

        if (manifest.SkipHostVersions.Contains(version))
            return $"is marked broken on OpenAC {version}";

        return null;
    }

    private static string DescribeHost(PluginHostKind host) => host switch
    {
        PluginHostKind.Graphical => "graphical",
        PluginHostKind.Headless => "headless",
        _ => host.ToString(),
    };
}
