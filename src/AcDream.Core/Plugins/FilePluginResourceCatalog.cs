using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

/// <summary>Filesystem-backed package resources with deterministic data overlays.</summary>
public sealed class FilePluginResourceCatalog : IPluginResourceCatalog
{
    private readonly string _packageRoot;
    private readonly string[] _dataRoots;

    /// <summary>Creates a catalog. Earlier data roots override later roots.</summary>
    public FilePluginResourceCatalog(string packageRoot, IEnumerable<string>? dataRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        _packageRoot = Path.GetFullPath(packageRoot);
        _dataRoots = (dataRoots ?? Array.Empty<string>())
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToArray();
    }

    /// <inheritdoc />
    public Stream? OpenRead(string resourceId)
    {
        string relative = ValidateRelative(resourceId);
        string path = Path.Combine(_packageRoot, relative);
        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> List(string prefix = "")
    {
        string relativePrefix = string.IsNullOrWhiteSpace(prefix) ? string.Empty : ValidateRelative(prefix);
        string directory = Path.Combine(_packageRoot, relativePrefix);
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_packageRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListDataFiles(string relativeDirectory)
    {
        string relative = string.IsNullOrWhiteSpace(relativeDirectory) ? string.Empty : ValidateRelative(relativeDirectory);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in _dataRoots.Append(_packageRoot))
        {
            string directory = Path.Combine(root, relative);
            if (!Directory.Exists(directory)) continue;
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                result.Add(Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'));
        }
        return result.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ValidateRelative(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Path.IsPathRooted(value)) throw new ArgumentException("Resource paths must be relative.", nameof(value));
        string normalized = value.Replace('/', Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar).Any(static part => part == ".."))
            throw new ArgumentException("Resource paths cannot escape the package root.", nameof(value));
        return normalized;
    }
}
