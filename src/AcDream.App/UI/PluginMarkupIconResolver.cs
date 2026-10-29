using AcDream.App.Plugins;

namespace AcDream.App.UI;

/// <summary>
/// The icon resolver one plugin window's markup draws through: the client's
/// own icons from the shared retail resolver, and file icons from the
/// plugin's install folder. A file is read in the background the first time
/// the window draws it, and draws nothing until it is ready. The image is
/// then held in the plugin's image table, under the plugin's image budget,
/// for as long as this window keeps drawing it: a file the window has not
/// drawn for <see cref="ReleaseAfterMilliseconds"/> is let go by
/// <see cref="ReleaseUndrawn"/> and read again if it is drawn later, so a
/// bound icon that runs through many files, or a list with many row icons,
/// does not fill the plugin's budget with images nobody sees. Everything is
/// let go when the window is unmounted (closed for good, reloaded, or its
/// plugin unloaded). Two windows showing the same file each hold it, so one
/// window going does not take the image from the other. Used from the
/// interface thread only.
/// </summary>
internal sealed class PluginMarkupIconResolver : IMarkupIconResolver, IDisposable
{
    /// <summary>A bound path can name anything; past this many, every hold is let go and the paths asked again.</summary>
    private const int MaximumRemembered = 4096;

    /// <summary>A file the window has not drawn for this long, in milliseconds, is let go.</summary>
    internal const long ReleaseAfterMilliseconds = 5_000;

    /// <summary>How often, at most, in milliseconds, <see cref="ReleaseUndrawn"/> looks for files to let go.</summary>
    internal const long ReleaseCheckIntervalMilliseconds = 1_000;

    private sealed class FileIcon(Task<PluginFileImageResult> reading, long drawnAt)
    {
        /// <summary>The read, until its result has been taken; then null.</summary>
        internal Task<PluginFileImageResult>? Reading { get; set; } = reading;

        /// <summary>The table holding the image for this window, once it does.</summary>
        internal PluginImageTable? Table { get; set; }

        internal PluginImageHandle Handle { get; set; }

        /// <summary>
        /// Set when the plugin's table had no room for the image (or there was
        /// no table): the table and its <see cref="PluginImageTable.ReleaseGeneration"/>
        /// at that moment. Once either has changed there may be room, and the
        /// file is read again.
        /// </summary>
        internal (PluginImageTable? Table, long Generation)? RefusedForRoom { get; set; }

        /// <summary>The clock reading of the last time the window drew this file.</summary>
        internal long DrawnAt { get; set; } = drawnAt;
    }

    private readonly IMarkupIconResolver _retail;
    private readonly PluginFileImageLoader _files;
    private readonly Func<PluginImageTable?> _imageTable;
    private readonly string _pluginId;
    private readonly string? _pluginDirectory;
    private readonly Func<long> _now;
    private readonly Dictionary<string, FileIcon> _icons = new(StringComparer.Ordinal);
    private long _lastReleaseCheck;
    private bool _disposed;

    /// <param name="retail">Resolves the client's own icons.</param>
    /// <param name="files">Reads the plugin's image files.</param>
    /// <param name="imageTable">The plugin's image table, where file icons are held.</param>
    /// <param name="pluginId">The plugin, for reports.</param>
    /// <param name="pluginDirectory">The plugin's install folder; null when the window has none.</param>
    /// <param name="clock">Milliseconds on a steady clock; <see cref="Environment.TickCount64"/> unless a test says otherwise.</param>
    internal PluginMarkupIconResolver(
        IMarkupIconResolver retail,
        PluginFileImageLoader files,
        Func<PluginImageTable?> imageTable,
        string pluginId,
        string? pluginDirectory,
        Func<long>? clock = null)
    {
        _retail = retail;
        _files = files;
        _imageTable = imageTable;
        _pluginId = pluginId;
        _pluginDirectory = pluginDirectory;
        _now = clock ?? (static () => Environment.TickCount64);
        _lastReleaseCheck = _now();
    }

    /// <summary>How many file paths the window remembers, held, being read or refused.</summary>
    internal int RememberedFileCount => _icons.Count;

    public (uint tex, int w, int h) ResolveDid(uint did) => _retail.ResolveDid(did);

    public (uint tex, int w, int h) ResolveSpell(uint spellId) => _retail.ResolveSpell(spellId);

    public (uint tex, int w, int h) ResolveItem(uint objectId) => _retail.ResolveItem(objectId);

    public (uint tex, int w, int h) ResolveFile(string path)
    {
        if (_disposed)
            return (0u, 0, 0);
        if (_pluginDirectory is null)
        {
            _files.Report(_pluginId, "*", "file icons need a plugin folder, and this window has no plugin folder");
            return (0u, 0, 0);
        }

        long now = _now();
        if (!_icons.TryGetValue(path, out FileIcon? icon))
        {
            if (_icons.Count >= MaximumRemembered)
                ReleaseAll();
            icon = new FileIcon(_files.Load(_pluginDirectory, path), now);
            _icons[path] = icon;
        }
        icon.DrawnAt = now;

        if (icon.RefusedForRoom is { } refused
            && _imageTable() is { } current
            && (!ReferenceEquals(current, refused.Table) || current.ReleaseGeneration != refused.Generation))
        {
            // Something was let go since the table said no: there may be room now.
            icon.RefusedForRoom = null;
            icon.Reading = _files.Load(_pluginDirectory, path);
        }

        if (icon.Reading is { IsCompleted: true } reading)
        {
            icon.Reading = null;
            PluginFileImageResult result = reading.IsCompletedSuccessfully
                ? reading.Result
                : new PluginFileImageResult(null, "could not be read: " + reading.Exception?.GetBaseException().Message);
            if (result.Image is { } image)
                Hold(icon, image);
            else
                _files.Report(_pluginId, path, result.Reason ?? "could not be read");
        }

        return icon.Table is { } held && held.TryResolve(icon.Handle, out uint texture, out int width, out int height)
            ? (texture, width, height)
            : (0u, 0, 0);
    }

    /// <summary>
    /// Lets go of every file the window has not drawn for
    /// <see cref="ReleaseAfterMilliseconds"/>; a later draw reads it again.
    /// Called once a frame; looks at most once every
    /// <see cref="ReleaseCheckIntervalMilliseconds"/>.
    /// </summary>
    internal void ReleaseUndrawn()
    {
        if (_disposed || _icons.Count == 0)
            return;
        long now = _now();
        if (now - _lastReleaseCheck < ReleaseCheckIntervalMilliseconds)
            return;
        _lastReleaseCheck = now;

        List<string>? undrawn = null;
        foreach ((string path, FileIcon icon) in _icons)
        {
            if (now - icon.DrawnAt >= ReleaseAfterMilliseconds)
                (undrawn ??= []).Add(path);
        }
        if (undrawn is null)
            return;
        foreach (string path in undrawn)
        {
            if (_icons.Remove(path, out FileIcon? icon))
                icon.Table?.Release(icon.Handle);
        }
    }

    /// <summary>Lets go of every image this window holds. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseAll();
    }

    private void Hold(FileIcon icon, PluginFileImage image)
    {
        PluginImageTable? table = _imageTable();
        if (table is null)
        {
            icon.RefusedForRoom = (null, 0L);
            return;
        }
        long generation = table.ReleaseGeneration;
        PluginImageHandle handle = table.AcquireFile(image);
        if (handle.IsValid)
        {
            icon.Handle = handle;
            icon.Table = table;
        }
        else
        {
            icon.RefusedForRoom = (table, generation);
        }
    }

    private void ReleaseAll()
    {
        foreach (FileIcon icon in _icons.Values)
            icon.Table?.Release(icon.Handle);
        _icons.Clear();
    }
}
