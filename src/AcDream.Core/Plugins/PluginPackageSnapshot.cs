namespace AcDream.Core.Plugins;

/// <summary>
/// One copy of a plugin's code and markup, read from its folder into memory
/// when the plugin is prepared. Everything the plugin loads afterwards comes
/// from here and never from the folder, so the copy that runs stays whole
/// while an update writes the next version into the same folder: a running
/// copy cannot pick up half of a newer version's assemblies or panels.
/// </summary>
internal sealed class PluginPackageSnapshot
{
    /// <summary>The folder inside a plugin's folder that holds the player's files.</summary>
    internal const string FilesFolderName = "files";

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly Dictionary<string, byte[]> _assemblies;
    private readonly Dictionary<string, byte[]> _symbols;
    private readonly Dictionary<string, string> _markup;

    private PluginPackageSnapshot(
        string directory,
        Dictionary<string, byte[]> assemblies,
        Dictionary<string, byte[]> symbols,
        Dictionary<string, string> markup)
    {
        Directory = directory;
        _assemblies = assemblies;
        _symbols = symbols;
        _markup = markup;
    }

    /// <summary>The plugin's folder, as a full path.</summary>
    internal string Directory { get; }

    /// <summary>
    /// Reads every assembly, symbol file and markup file in the plugin's
    /// folder and below it, except the player's own <c>files</c> folder.
    /// </summary>
    internal static PluginPackageSnapshot Read(string pluginDirectory)
    {
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginDirectory));
        var assemblies = new Dictionary<string, byte[]>(PathComparer);
        var symbols = new Dictionary<string, byte[]>(PathComparer);
        var markup = new Dictionary<string, string>(PathComparer);
        foreach (string file in System.IO.Directory.EnumerateFiles(
            directory,
            "*",
            SearchOption.AllDirectories))
        {
            if (IsPlayerFile(directory, file))
                continue;
            switch (Kind(file))
            {
                case PackageFileKind.Assembly:
                    assemblies[file] = File.ReadAllBytes(file);
                    break;
                case PackageFileKind.Symbols:
                    symbols[file] = File.ReadAllBytes(file);
                    break;
                case PackageFileKind.Markup:
                    markup[file] = File.ReadAllText(file);
                    break;
            }
        }
        return new PluginPackageSnapshot(directory, assemblies, symbols, markup);
    }

    /// <summary>The assembly read from <paramref name="path"/>, or false when there was none.</summary>
    internal bool TryGetAssembly(string path, out byte[] image, out byte[]? symbols)
    {
        string full = Path.GetFullPath(path);
        symbols = _symbols.GetValueOrDefault(Path.ChangeExtension(full, ".pdb"));
        return _assemblies.TryGetValue(full, out image!);
    }

    /// <summary>The markup read from <paramref name="path"/>, or null when it is not part of the package.</summary>
    internal string? MarkupAt(string path) =>
        _markup.GetValueOrDefault(Path.GetFullPath(path));

    /// <summary>
    /// What a file in a plugin's folder is to the plugin's code: an assembly,
    /// its symbols, a markup panel, its manifest or dependency list, or
    /// nothing (data the plugin keeps, logs, anything else).
    /// </summary>
    internal static PackageFileKind Kind(string path)
    {
        string name = Path.GetFileName(path);
        if (name.Equals("plugin.json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
        {
            return PackageFileKind.Manifest;
        }
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".dll" => PackageFileKind.Assembly,
            ".pdb" => PackageFileKind.Symbols,
            ".xml" => PackageFileKind.Markup,
            _ => PackageFileKind.Other,
        };
    }

    /// <summary>Whether a path in a plugin's folder is inside the player's own <c>files</c> folder.</summary>
    internal static bool IsPlayerFile(string pluginDirectory, string path)
    {
        string relative = Path.GetRelativePath(pluginDirectory, path);
        int separator = relative.IndexOfAny(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return separator > 0
            && relative.AsSpan(0, separator).Equals(
                FilesFolderName,
                StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>What a file in a plugin's folder is to the plugin's code.</summary>
internal enum PackageFileKind
{
    Other,
    Manifest,
    Assembly,
    Symbols,
    Markup,
}
