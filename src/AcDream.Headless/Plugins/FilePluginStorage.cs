using System.Text;
using AcDream.Plugin.Abstractions;

namespace AcDream.Headless.Plugins;

internal sealed class FilePluginStorage : IPluginStorage
{
    private readonly string _root;

    internal FilePluginStorage(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public bool IsAvailable => true;

    public string? ReadText(string key)
    {
        string path = Resolve(key);
        return File.Exists(path)
            ? File.ReadAllText(path, Encoding.UTF8)
            : null;
    }

    public IReadOnlyList<string> List(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        string directory = prefix.Length == 0 ? _root : Resolve(prefix);
        if (!Directory.Exists(directory))
            return Array.Empty<string>();
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_root, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void WriteText(string key, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string path = Resolve(key);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public bool Delete(string key)
    {
        string path = Resolve(key);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    private string Resolve(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (Path.IsPathRooted(key))
            throw new ArgumentException("Plugin storage keys must be relative.", nameof(key));
        string path = Path.GetFullPath(Path.Combine(_root, key));
        string relative = Path.GetRelativePath(_root, path);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Plugin storage key escapes its root.", nameof(key));
        }
        return path;
    }
}
