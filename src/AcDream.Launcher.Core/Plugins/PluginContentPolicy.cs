using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Plugins;

/// <summary>Walks an extracted plugin tree and enforces the release contract's content rules: a
/// managed-code allowlist by extension, <c>plugin.json</c> at the root, the declared entry DLL
/// present, and no <c>runtimes</c> directory.</summary>
public static class PluginContentPolicy
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".pdb", ".json", ".xml", ".txt", ".md", ".png", ".jpg", ".jpeg", ".ttf", ".otf",
    };

    public static void Validate(IReadOnlyList<ExtractedFileRecord> files, string entryDll)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryDll);

        bool manifestAtRoot = false;
        bool entryFound = false;
        foreach (ExtractedFileRecord file in files)
        {
            if (!AllowedExtensions.Contains(Path.GetExtension(file.Path)))
            {
                throw new LauncherUpdateException(
                    $"Plugin file '{file.Path}' has a disallowed extension.");
            }

            if (file.Path.Split('/').Any(segment =>
                    string.Equals(segment, "runtimes", StringComparison.OrdinalIgnoreCase)))
            {
                throw new LauncherUpdateException(
                    $"Plugin file '{file.Path}' is under a disallowed 'runtimes' directory.");
            }

            if (string.Equals(file.Path, "plugin.json", StringComparison.Ordinal))
                manifestAtRoot = true;
            if (string.Equals(file.Path, entryDll, StringComparison.Ordinal))
                entryFound = true;
        }

        if (!manifestAtRoot)
        {
            throw new LauncherUpdateException("The plugin archive has no 'plugin.json' at its root.");
        }

        if (!entryFound)
        {
            throw new LauncherUpdateException(
                $"The plugin archive is missing its declared entry DLL '{entryDll}'.");
        }
    }
}
