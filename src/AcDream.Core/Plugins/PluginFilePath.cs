using System.Diagnostics.CodeAnalysis;

namespace AcDream.Core.Plugins;

/// <summary>
/// Files a plugin points the client at by a path relative to its install folder, such as
/// markup images. One check for all of them, so none can be used to read outside the folder.
/// </summary>
public static class PluginFilePath
{
    /// <summary>
    /// Resolves a path a plugin names against its install folder. The path must be relative,
    /// end in one of <paramref name="extensions"/>, and stay inside the folder after links are
    /// followed. Never throws: every refusal is a reason ("is not <paramref name="kind"/>",
    /// "points outside the plugin folder", "does not exist", ...).
    /// </summary>
    public static bool TryResolve(
        string pluginDirectory, string relativePath, IReadOnlyCollection<string> extensions, string kind,
        [NotNullWhen(true)] out string? fullPath, [NotNullWhen(false)] out string? reason)
    {
        fullPath = null;
        string relative = relativePath.Replace('\\', '/');
        if (relative.Length == 0 || relative.StartsWith('/') || Path.IsPathRooted(relative)
            || (relative.Length >= 2 && relative[1] == ':'))
        {
            reason = "points outside the plugin folder";
            return false;
        }
        if (!extensions.Any(extension => relative.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "is not " + kind;
            return false;
        }

        try
        {
            string root = Path.GetFullPath(pluginDirectory);
            string candidate = Path.GetFullPath(Path.Combine(root, relative));
            if (!IsInside(root, candidate))
            {
                reason = "points outside the plugin folder";
                return false;
            }
            if (!File.Exists(candidate))
            {
                reason = "does not exist";
                return false;
            }

            string resolvedRoot = ResolveLinks(root);
            string resolved = ResolveLinks(candidate);
            if (!IsInside(resolvedRoot, resolved))
            {
                reason = "points outside the plugin folder";
                return false;
            }
            fullPath = resolved;
            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            reason = "could not be resolved: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The one spelling of a path a plugin names, so two spellings of the same file
    /// (<c>./icons/a.png</c>, <c>icons//a.png</c>, <c>icons\a.png</c>) are one image:
    /// separators become <c>/</c>, empty and <c>.</c> parts go, and each <c>..</c> folds
    /// into the part before it, as <see cref="TryResolve"/> reads the path. A rooted path,
    /// or one whose <c>..</c> climbs above the folder, comes back with only its separators
    /// changed, for <see cref="TryResolve"/> to refuse. Letter case is kept as written.
    /// </summary>
    public static string Normalize(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string path = relativePath.Replace('\\', '/');
        if (path.StartsWith('/') || (path.Length >= 2 && path[1] == ':'))
            return path;
        var parts = new List<string>();
        foreach (string part in path.Split('/'))
        {
            if (part.Length == 0 || part == ".")
                continue;
            if (part == "..")
            {
                if (parts.Count == 0)
                    return path;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static bool IsInside(string root, string path)
    {
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return path.StartsWith(prefix, comparison);
    }

    /// <summary>
    /// Follows every link along <paramref name="path"/>, directories included, one component at a
    /// time. A link's target is itself walked component by component, so a link that points through
    /// another link cannot hide where the real file is.
    /// </summary>
    private static string ResolveLinks(string path)
    {
        const int MaximumLinkHops = 40;
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full) ?? "";
        var pending = new LinkedList<string>(SplitPath(full[current.Length..]));
        int hops = 0;
        while (pending.First is { } node)
        {
            pending.RemoveFirst();
            string part = node.Value;
            if (part == ".") continue;
            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            string next = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            string? target = info.LinkTarget;
            if (target is null)
            {
                current = next;
                continue;
            }
            if (++hops > MaximumLinkHops) throw new IOException("too many levels of links");
            if (Path.IsPathRooted(target))
            {
                current = Path.GetPathRoot(target) ?? current;
                target = target[current.Length..];
            }
            string[] targetParts = SplitPath(target);
            for (int i = targetParts.Length - 1; i >= 0; i--) pending.AddFirst(targetParts[i]);
        }
        return current;
    }

    private static string[] SplitPath(string path) =>
        path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
}
