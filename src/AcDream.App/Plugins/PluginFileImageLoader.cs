using System.Collections.Generic;
using AcDream.Core.Plugins;
using AcDream.Core.Textures;

namespace AcDream.App.Plugins;

/// <summary>
/// An image file a plugin ships, read, decoded and given its smaller levels:
/// what <see cref="PluginImageTable.AcquireFile"/> takes. <see cref="Path"/>
/// is the path the plugin named; <see cref="Version"/> tells one version of
/// the file on disk from the next.
/// </summary>
internal sealed record PluginFileImage(
    string Path,
    string Version,
    IReadOnlyList<DecodedTexture> MipLevels);

/// <summary>What reading one image file came to: the image, or why there is none.</summary>
internal readonly record struct PluginFileImageResult(PluginFileImage? Image, string? Reason);

/// <summary>
/// Reads the image files plugin markup names (<c>&lt;icon file="..."/&gt;</c>)
/// away from the interface thread. Resolving the path, reading the file,
/// decoding it and building its smaller levels all happen on a worker, so a
/// large image does not hold up the frame that first draws it. Windows that
/// ask for the same file while it is being read share the one read. Nothing
/// here touches the GPU and nothing is kept once read: the result is uploaded,
/// counted and given back through the plugin's own <see cref="PluginImageTable"/>.
/// </summary>
internal sealed class PluginFileImageLoader
{
    private readonly Func<Func<PluginFileImageResult>, Task<PluginFileImageResult>> _run;
    private readonly Action<string> _report;
    private readonly object _gate = new();
    private readonly Dictionary<(string Directory, string Path), Task<PluginFileImageResult>> _reading = [];
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    /// <param name="report">Where refusals are logged.</param>
    /// <param name="run">Runs a read; a worker thread unless a test says otherwise.</param>
    internal PluginFileImageLoader(
        Action<string>? report = null,
        Func<Func<PluginFileImageResult>, Task<PluginFileImageResult>>? run = null)
    {
        _report = report ?? Console.WriteLine;
        _run = run ?? Task.Run;
    }

    /// <summary>
    /// Starts reading <paramref name="relativePath"/> in
    /// <paramref name="pluginDirectory"/>, or joins the read already under way.
    /// The task never faults: a file that cannot be used comes back with the reason.
    /// </summary>
    internal Task<PluginFileImageResult> Load(string pluginDirectory, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(pluginDirectory);
        ArgumentNullException.ThrowIfNull(relativePath);
        // One spelling per file, so its read, and the image it makes, are shared by every spelling.
        string path = PluginFilePath.Normalize(relativePath);
        var key = (pluginDirectory, path);
        lock (_gate)
        {
            // Only a read still under way is shared: one that has finished
            // may be of a version of the file that has since changed.
            if (_reading.TryGetValue(key, out Task<PluginFileImageResult>? reading) && !reading.IsCompleted)
                return reading;
            Task<PluginFileImageResult> task = _run(() => Read(pluginDirectory, path));
            _reading[key] = task;
            task.ContinueWith(
                done =>
                {
                    lock (_gate)
                    {
                        if (_reading.TryGetValue(key, out Task<PluginFileImageResult>? current)
                            && ReferenceEquals(current, done))
                        {
                            _reading.Remove(key);
                        }
                    }
                },
                TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>Logs why a plugin's image is not drawn, once per file and reason.</summary>
    internal void Report(string pluginId, string path, string reason)
    {
        string label = $"{pluginId}/{path.Replace('\\', '/')}";
        lock (_gate)
        {
            if (!_reported.Add(label + "\n" + reason))
                return;
        }
        _report($"[UI] plugin image '{label}' ignored: {reason}");
    }

    private static PluginFileImageResult Read(string pluginDirectory, string path)
    {
        try
        {
            if (!PluginImageFile.TryResolvePath(pluginDirectory, path, out string? fullPath, out string? reason))
                return new PluginFileImageResult(null, reason);

            // Read before the content, so a file written while it is read is
            // at worst read again by the next window, never kept as current.
            var info = new FileInfo(fullPath);
            string version = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";

            if (!PluginImageFile.TryLoad(fullPath, out DecodedTexture? decoded, out reason))
                return new PluginFileImageResult(null, reason);
            IReadOnlyList<DecodedTexture> levels = RgbaMipChain.Build(decoded.Rgba8, decoded.Width, decoded.Height);
            return new PluginFileImageResult(new PluginFileImage(path, version, levels), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new PluginFileImageResult(null, "could not be read: " + ex.Message);
        }
    }
}
