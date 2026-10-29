using AcDream.App.Plugins;

namespace AcDream.App.UI;

/// <summary>
/// The icon resolver one plugin window's markup draws through: the client's
/// own icons from the client's shared icon resolver, and file icons from the
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
///
/// <para>A file the table had no room for keeps its decoded image here,
/// outside the budget, for as long as the window keeps drawing it. Once the
/// table has let something go it is offered again, at most once every
/// <see cref="ReleaseCheckIntervalMilliseconds"/>, without being read again
/// unless the file on disk has changed.</para>
///
/// <para>Time here is the interface's own: each <see cref="ReleaseUndrawn"/>
/// call, once a frame, moves it on by the time since the last call, but by no
/// more than <see cref="MaximumFrameStepMilliseconds"/>, so a stall of the
/// whole interface does not count as time a window spent not drawing.</para>
/// </summary>
internal sealed class PluginMarkupIconResolver : IMarkupIconResolver, IDisposable
{
    /// <summary>A bound path can name anything; past this many, every hold is let go and the paths asked again.</summary>
    private const int MaximumRemembered = 4096;

    /// <summary>A file the window has not drawn for this long, in milliseconds, is let go.</summary>
    internal const long ReleaseAfterMilliseconds = 5_000;

    /// <summary>
    /// How often, at most, in milliseconds, <see cref="ReleaseUndrawn"/> looks for
    /// files to let go, and a file refused for want of room is offered again.
    /// </summary>
    internal const long ReleaseCheckIntervalMilliseconds = 1_000;

    /// <summary>The most one frame moves the interface's time on, in milliseconds.</summary>
    internal const long MaximumFrameStepMilliseconds = 1_000;

    private sealed class FileIcon(Task<PluginFileImageResult> reading, long drawnAt)
    {
        /// <summary>The read, until its result has been taken; then null.</summary>
        internal Task<PluginFileImageResult>? Reading { get; set; } = reading;

        /// <summary>The table holding the image for this window, once it does.</summary>
        internal PluginImageTable? Table { get; set; }

        internal PluginImageHandle Handle { get; set; }

        /// <summary>
        /// Set when the plugin's table had no room for the image (or there was
        /// no table): the image, kept so a later offer need not read it again,
        /// and the table and its <see cref="PluginImageTable.ReleaseGeneration"/>
        /// at that moment. Once either has changed there may be room.
        /// </summary>
        internal (PluginFileImage Image, PluginImageTable? Table, long Generation)? RefusedForRoom { get; set; }

        /// <summary>The interface time of the last offer to the table.</summary>
        internal long OfferedAt { get; set; }

        /// <summary>The interface time of the last time the window drew this file.</summary>
        internal long DrawnAt { get; set; } = drawnAt;
    }

    private readonly IMarkupIconResolver _clientIcons;
    private readonly PluginFileImageLoader _files;
    private readonly Func<PluginImageTable?> _imageTable;
    private readonly string _pluginId;
    private readonly string? _pluginDirectory;
    private readonly Func<long> _now;
    private readonly Dictionary<string, FileIcon> _icons = new(StringComparer.Ordinal);
    private long _lastFrame;
    private long _time;
    private long _lastReleaseCheck;
    private bool _reportedNoFolder;
    private bool _disposed;

    /// <param name="clientIcons">Resolves the client's own icons.</param>
    /// <param name="files">Reads the plugin's image files.</param>
    /// <param name="imageTable">The plugin's image table, where file icons are held.</param>
    /// <param name="pluginId">The plugin, for reports.</param>
    /// <param name="pluginDirectory">The plugin's install folder; null when the window has none.</param>
    /// <param name="clock">Milliseconds on a steady clock; <see cref="Environment.TickCount64"/> unless a test says otherwise.</param>
    internal PluginMarkupIconResolver(
        IMarkupIconResolver clientIcons,
        PluginFileImageLoader files,
        Func<PluginImageTable?> imageTable,
        string pluginId,
        string? pluginDirectory,
        Func<long>? clock = null)
    {
        _clientIcons = clientIcons;
        _files = files;
        _imageTable = imageTable;
        _pluginId = pluginId;
        _pluginDirectory = pluginDirectory;
        _now = clock ?? (static () => Environment.TickCount64);
        _lastFrame = _now();
    }

    /// <summary>How many file paths the window remembers, held, being read or refused.</summary>
    internal int RememberedFileCount => _icons.Count;

    /// <summary>How many times a file refused for want of room has been offered to the table again.</summary>
    internal int RoomRetries { get; private set; }

    public (uint tex, int w, int h) ResolveDid(uint did) => _clientIcons.ResolveDid(did);

    public (uint tex, int w, int h) ResolveSpell(uint spellId) => _clientIcons.ResolveSpell(spellId);

    public (uint tex, int w, int h) ResolveItem(uint objectId) => _clientIcons.ResolveItem(objectId);

    public (uint tex, int w, int h) ResolveFile(string path)
    {
        if (_disposed)
            return (0u, 0, 0);
        if (_pluginDirectory is null)
        {
            // Said once per window, so drawing takes no lock and builds no text each frame.
            if (!_reportedNoFolder)
            {
                _reportedNoFolder = true;
                _files.Report(_pluginId, "*", "file icons need a plugin folder, and this window has no plugin folder");
            }
            return (0u, 0, 0);
        }

        if (!_icons.TryGetValue(path, out FileIcon? icon))
        {
            if (_icons.Count >= MaximumRemembered)
                ReleaseAll();
            icon = new FileIcon(_files.Load(_pluginDirectory, path), _time);
            _icons[path] = icon;
        }
        icon.DrawnAt = _time;

        if (icon.RefusedForRoom is { } refused
            && _time - icon.OfferedAt >= ReleaseCheckIntervalMilliseconds
            && _imageTable() is { } current
            && (!ReferenceEquals(current, refused.Table) || current.ReleaseGeneration != refused.Generation))
        {
            // Something was let go since the table said no: there may be room now.
            icon.RefusedForRoom = null;
            if (IsCurrent(refused.Image))
            {
                RoomRetries++;
                Hold(icon, refused.Image);
            }
            else
            {
                icon.Reading = _files.Load(_pluginDirectory, path);
            }
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
    /// Moves the interface's time on (called once a frame) and lets go of every
    /// file the window has not drawn for <see cref="ReleaseAfterMilliseconds"/>
    /// of it; a later draw reads it again. Looks at most once every
    /// <see cref="ReleaseCheckIntervalMilliseconds"/>.
    /// </summary>
    internal void ReleaseUndrawn()
    {
        if (_disposed)
            return;
        long now = _now();
        _time += Math.Clamp(now - _lastFrame, 0L, MaximumFrameStepMilliseconds);
        _lastFrame = now;
        if (_icons.Count == 0 || _time - _lastReleaseCheck < ReleaseCheckIntervalMilliseconds)
            return;
        _lastReleaseCheck = _time;

        List<string>? undrawn = null;
        foreach ((string path, FileIcon icon) in _icons)
        {
            if (_time - icon.DrawnAt >= ReleaseAfterMilliseconds)
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

    /// <summary>Lets go of every image this window holds, and drops any it kept. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseAll();
    }

    private void Hold(FileIcon icon, PluginFileImage image)
    {
        icon.OfferedAt = _time;
        PluginImageTable? table = _imageTable();
        if (table is null)
        {
            icon.RefusedForRoom = (image, null, 0L);
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
            icon.RefusedForRoom = (image, table, generation);
        }
    }

    /// <summary>
    /// Whether a kept image is still what is on disk. One whose file cannot be
    /// looked at is not, and is read again, which says why.
    /// </summary>
    private static bool IsCurrent(PluginFileImage image)
    {
        if (image.FullPath is null)
            return true;
        try
        {
            return PluginFileImageLoader.VersionOf(image.FullPath) == image.Version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ReleaseAll()
    {
        foreach (FileIcon icon in _icons.Values)
            icon.Table?.Release(icon.Handle);
        _icons.Clear();
    }
}
