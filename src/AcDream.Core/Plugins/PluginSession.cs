using AcDream.Plugin.Abstractions;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.Core.Plugins;

public enum PluginSessionStatusKind
{
    Loaded,
    Failed,
}

public readonly record struct PluginSessionStatus(
    string Plugin,
    PluginSessionStatusKind Kind,
    string? Error = null);

public sealed class PluginHostKindException : Exception
{
    public PluginHostKindException(string message) : base(message) { }
}

public sealed class PluginHostCompatibilityException : Exception
{
    public PluginHostCompatibilityException(string message) : base(message) { }
}

public sealed class PluginDuplicateIdException : Exception
{
    public PluginDuplicateIdException(string message) : base(message) { }
}

/// <summary>
/// The plugins one client runs: finding them, loading, enabling, reloading
/// and unloading them. Both clients run their plugins through this one
/// class, so a plugin loads, reloads and unloads the same way with a window
/// or without one.
/// <para>
/// A plugin is reloaded when the player asks (<c>/plugin reload</c>) or when
/// its entry assembly or <c>plugin.json</c> changes on disk and the folder
/// has then been quiet for <see cref="QuietPeriod"/>. Reloads run on the
/// thread that raises <see cref="IEvents.Tick"/>, the thread every plugin
/// already runs on.
/// </para>
/// </summary>
public sealed class PluginSession : IDisposable
{
    /// <summary>The chat verb a player reloads plugins with.</summary>
    public const string CommandVerb = "plugin";

    /// <summary>
    /// How long a plugin's folder must go without a write, after its entry
    /// assembly or <c>plugin.json</c> changed, before the plugin is reloaded.
    /// An update writes several files; the plugin reloads once, after the
    /// last of them.
    /// </summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many times, and how many ticks apart, the host looks for the
    /// previous copy of a reloaded plugin to have left memory before it says
    /// it did not: about one second in all.
    /// </summary>
    internal const int UnloadCheckAttempts = 8;
    internal const int TicksBetweenUnloadChecks = 8;

    private const string AllPlugins = "all";
    private const string ManifestFileName = "plugin.json";
    private const string FilesFolderName = "files";

    private readonly IPluginHost _host;
    private readonly Action<PluginSessionStatus>? _report;
    private readonly IRenderPackRegistry? _renderPacks;
    private readonly HashSet<PluginKind> _supportedKinds;
    private readonly PluginHostKind? _hostKind;
    private readonly PluginHostVersion? _hostVersion;
    private readonly TimeProvider _time;
    private readonly List<ActivePlugin> _loaded = [];

    /// <summary>
    /// The status lines this session's plugins share, one board for all of
    /// them, so every host that runs plugins through a session keeps it the
    /// same way.
    /// </summary>
    private readonly PluginStatusBoard _statusBoard = new();
    private readonly List<WeakReference> _releasedContexts = [];

    /// <summary>
    /// Plugins whose reload stopped after the running copy was already gone:
    /// off until their files change again or the player asks. Kept so that
    /// the next attempt knows where they live. Touched on the tick thread only.
    /// </summary>
    private readonly Dictionary<string, OfflinePlugin> _offline =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Previous copies of reloaded plugins not yet seen to leave memory.</summary>
    private readonly List<UnloadCheck> _unloadChecks = [];

    // Written by the chat command and the folder watchers, which do not run
    // on the tick thread, and read on the tick thread.
    private readonly object _requestGate = new();
    private readonly List<string> _requestedReloads = [];
    private readonly Dictionary<string, WatchedFolder> _watchedFolders =
        new(PathComparer);
    private readonly Dictionary<string, long> _lastFolderWrite =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private bool _watching;

    private IDisposable? _command;
    private Action<double>? _tickHandler;
    private bool _started;
    private bool _disposed;

    public PluginSession(
        IPluginHost host,
        Action<PluginSessionStatus>? report = null,
        IRenderPackRegistry? renderPacks = null,
        IEnumerable<PluginKind>? supportedKinds = null,
        PluginHostKind? hostKind = null,
        PluginHostVersion? hostVersion = null,
        TimeProvider? timeProvider = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _report = report;
        _renderPacks = renderPacks;
        _hostKind = hostKind;
        _hostVersion = hostVersion;
        _time = timeProvider ?? TimeProvider.System;
        _supportedKinds = new HashSet<PluginKind>(
            supportedKinds
                ?? (renderPacks is null
                    ? [PluginKind.Gameplay]
                    : [PluginKind.Gameplay, PluginKind.RenderPack]));
        if (_supportedKinds.Count == 0)
            throw new ArgumentException(
                "At least one supported plugin kind is required.",
                nameof(supportedKinds));
        if (_supportedKinds.Contains(PluginKind.RenderPack) && renderPacks is null)
        {
            throw new ArgumentException(
                "A host that supports render-pack plugins must supply a render-pack registry.",
                nameof(renderPacks));
        }
    }

    public int LoadedCount => _loaded.Count;

    public IReadOnlyList<string> LoadedPluginIds =>
        _loaded.Select(static active => active.Loaded.Manifest.Id).ToArray();

    public void Start(
        IEnumerable<string> pluginRoots,
        IReadOnlyList<string>? allowList)
    {
        ArgumentNullException.ThrowIfNull(pluginRoots);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            throw new InvalidOperationException("The plugin session has already started.");
        _started = true;

        string[] roots = DistinctRoots(pluginRoots);
        string[]? requested = allowList is null
            ? null
            : allowList
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        if (requested is { Length: 0 })
            return;

        // The client's own verb goes in before any plugin can take the name.
        RegisterCommand();

        var candidates = new Dictionary<string, List<PluginDiscoveryResult>>(
            StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, List<Exception>>(
            StringComparer.OrdinalIgnoreCase);
        var discoveredOrder = new List<string>();
        HashSet<string>? requestedSet = requested is null
            ? null
            : new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots)
        {
            IReadOnlyList<PluginDiscoveryResult> results;
            try
            {
                results = PluginDiscovery.Scan(root);
            }
            catch (Exception error) when (IsDiscoveryFailure(error))
            {
                SafeLog(
                    static (log, message, exception) =>
                        log.Error(message, exception),
                    $"plugin discovery failed for root '{root}'",
                    error);
                continue;
            }

            foreach (PluginDiscoveryResult result in results)
            {
                if (!result.Success)
                {
                    string directoryId = Path.GetFileName(
                        Path.TrimEndingDirectorySeparator(result.PluginDirectory));
                    if (string.IsNullOrWhiteSpace(directoryId)
                        || (requestedSet is not null
                            && !requestedSet.Contains(directoryId)))
                    {
                        continue;
                    }

                    AddOrdered(discoveredOrder, directoryId);
                    AddError(
                        errors,
                        directoryId,
                        result.Error ?? new InvalidOperationException(
                            "plugin discovery failed"));
                    continue;
                }

                string id = result.Manifest!.Id;
                if (requestedSet is not null && !requestedSet.Contains(id))
                    continue;
                if (!result.Manifest.Kinds.Any(_supportedKinds.Contains))
                {
                    if (requestedSet is not null)
                    {
                        AddOrdered(discoveredOrder, id);
                        AddError(
                            errors,
                            id,
                            new PluginHostKindException(
                                $"plugin '{id}' declares only "
                                + $"{string.Join(", ", result.Manifest.Kinds)} entry points, "
                                + "which this host does not support."));
                    }
                    continue;
                }
                string? incompatibility = _hostKind is { } hostKind
                    ? PluginHostCompatibility.Evaluate(result.Manifest, hostKind, _hostVersion)
                    : null;
                if (incompatibility is not null)
                {
                    if (requestedSet is not null)
                    {
                        AddOrdered(discoveredOrder, id);
                        AddError(
                            errors,
                            id,
                            new PluginHostCompatibilityException(
                                $"plugin '{id}' {incompatibility}."));
                    }
                    continue;
                }
                AddOrdered(discoveredOrder, id);
                if (!candidates.TryGetValue(id, out List<PluginDiscoveryResult>? list))
                {
                    list = [];
                    candidates.Add(id, list);
                }
                list.Add(result);
            }
        }

        IEnumerable<string> loadOrder = requested is null
            ? discoveredOrder
            : requested;
        foreach (string id in loadOrder)
            LoadOne(id, candidates, errors);

        StartReloading();
    }

    public IReadOnlyList<WeakReference> CaptureLoadContextWeakReferences() =>
        [
            .. _releasedContexts,
            .. _loaded.Select(static active =>
                new WeakReference(active.Loaded.LoadContext!)),
        ];

    /// <summary>
    /// Asks for a plugin to be reloaded on the next tick: by its id, or every
    /// plugin this session runs when <paramref name="pluginIdOrAll"/> is
    /// <c>all</c>. Safe from any thread; what came of it is written to chat.
    /// </summary>
    public void RequestReload(string pluginIdOrAll)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginIdOrAll);
        lock (_requestGate)
        {
            if (!_disposed)
                _requestedReloads.Add(pluginIdOrAll.Trim());
        }
    }

    /// <summary>
    /// Whether a change to the plugin's files has been seen and is waiting
    /// for its folder to go quiet. For tests, which cannot otherwise tell
    /// when the operating system has delivered a change.
    /// </summary>
    internal bool HasPendingFileChange(string pluginId)
    {
        lock (_requestGate)
            return _lastFolderWrite.ContainsKey(pluginId);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopReloading();

        for (int index = _loaded.Count - 1; index >= 0; index--)
        {
            ActivePlugin active = _loaded[index];
            LoadedPlugin loaded = active.Loaded;
            if (loaded.Plugin is not null)
            {
                try
                {
                    loaded.Plugin.Disable();
                }
                catch (Exception error)
                {
                    SafeLog(
                        static (log, message, exception) =>
                            log.Error(message, exception),
                        $"plugin disable failed: {loaded.Manifest.Id}",
                        error);
                }
            }

            // Host-owned registrations are released even when Disable throws.
            // This must precede ALC unload so no UI binding or event delegate
            // can keep the plugin assembly reachable.
            active.Scope.Dispose();
            ReleaseRenderScope(active.RenderPackScope, loaded.Manifest.Id);

            try
            {
                loaded.LoadContext!.Unload();
            }
            catch (Exception error)
            {
                SafeLog(
                    static (log, message, exception) =>
                        log.Error(message, exception),
                    $"plugin unload failed: {loaded.Manifest.Id}",
                    error);
            }
        }

        _loaded.Clear();
    }

    private void LoadOne(
        string id,
        IReadOnlyDictionary<string, List<PluginDiscoveryResult>> candidates,
        Dictionary<string, List<Exception>> errors)
    {
        if (candidates.TryGetValue(id, out List<PluginDiscoveryResult>? available))
        {
            if (available.Count > 1)
            {
                AddError(
                    errors,
                    id,
                    new PluginDuplicateIdException(DescribeDuplicate(id, available)));
                available = [];
            }
            foreach (PluginDiscoveryResult candidate in available)
            {
                var scope = new ScopedPluginHost(
                    _host,
                    candidate.Manifest!.Id,
                    candidate.Manifest.DisplayName,
                    candidate.PluginDirectory,
                    _statusBoard);
                ScopedRenderPackRegistry? renderPackScope =
                    candidate.Manifest!.Declares(PluginKind.RenderPack)
                    && _renderPacks is not null
                        ? new ScopedRenderPackRegistry(_renderPacks)
                        : null;
                LoadedPlugin loaded = PluginLoader.Load(
                    candidate.PluginDirectory,
                    candidate.Manifest,
                    scope,
                    renderPackScope);
                if (!loaded.Success)
                {
                    scope.Dispose();
                    ReleaseRenderScope(renderPackScope, candidate.Manifest.Id);
                    ReleaseFailedLoad(loaded);
                    AddError(
                        errors,
                        id,
                        loaded.Error ?? new InvalidOperationException(
                            "plugin load failed"));
                    continue;
                }

                try
                {
                    loaded.Plugin?.Enable();
                    _loaded.Add(new ActivePlugin(
                        loaded,
                        scope,
                        renderPackScope,
                        candidate.PluginDirectory));
                    SafeLog(
                        static (log, message, _) => log.Info(message),
                        $"plugin loaded: {loaded.Manifest.Id} "
                            + $"({loaded.Manifest.DisplayName})",
                        null);
                    Report(new PluginSessionStatus(
                        loaded.Manifest.Id,
                        PluginSessionStatusKind.Loaded));
                    RaiseLoginIfInWorld(scope, loaded.Manifest.Id);
                    return;
                }
                catch (Exception error)
                {
                    AddError(errors, id, error);
                    ReleaseFailedEnable(loaded, scope, renderPackScope);
                }
            }
        }

        if (!errors.TryGetValue(id, out List<Exception>? failures)
            || failures.Count == 0)
        {
            failures =
            [
                new FileNotFoundException(
                    $"plugin '{id}' was not found in the configured plugin roots."),
            ];
        }

        string errorText = string.Join(
            " | ",
            failures.Select(Describe));
        Report(new PluginSessionStatus(
            id,
            PluginSessionStatusKind.Failed,
            errorText));
        SafeLog(
            static (log, message, _) => log.Warn(message),
            $"plugin failed: {id}: {errorText}",
            null);
    }

    // ---- reloading --------------------------------------------------------

    private void RegisterCommand()
    {
        try
        {
            _command = _host.Commands.Register(CommandVerb, OnCommand);
        }
        catch (InvalidOperationException error)
        {
            // Another registration already owns the verb; reloading by
            // folder change still works, and the log says why the verb
            // does not.
            SafeLog(
                static (log, message, exception) => log.Error(message, exception),
                $"the /{CommandVerb} command could not be registered",
                error);
        }
    }

    private void OnCommand(PluginCommand command)
    {
        string[] words = command.Arguments.Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 2
            && string.Equals(words[0], "reload", StringComparison.OrdinalIgnoreCase))
        {
            RequestReload(words[1]);
            return;
        }

        Say($"Usage: /{CommandVerb} reload <plugin id>, or /{CommandVerb} reload {AllPlugins}");
    }

    /// <summary>
    /// Starts listening for reload requests: the tick that carries them out,
    /// and a watcher on each plugin folder that holds a running plugin.
    /// </summary>
    private void StartReloading()
    {
        _tickHandler = OnTick;
        try
        {
            _host.Events.Tick += _tickHandler;
        }
        catch (Exception error) when (error is InvalidOperationException
            or ObjectDisposedException
            or NotSupportedException)
        {
            _tickHandler = null;
            SafeLog(
                static (log, message, exception) => log.Error(message, exception),
                "plugin reloading is unavailable: the host did not accept a tick handler",
                error);
            return;
        }

        var roots = new HashSet<string>(PathComparer);
        lock (_requestGate)
        {
            _watching = true;
            foreach (ActivePlugin active in _loaded)
            {
                Watch(active.Directory, active.Loaded.Manifest);
                if (Path.GetDirectoryName(
                        Path.TrimEndingDirectorySeparator(
                            Path.GetFullPath(active.Directory))) is { } root)
                {
                    roots.Add(root);
                }
            }
        }

        foreach (string root in roots.Order(PathComparer))
            StartWatcher(root);
    }

    private void StopReloading()
    {
        FileSystemWatcher[] watchers;
        lock (_requestGate)
        {
            _watching = false;
            watchers = [.. _watchers];
            _watchers.Clear();
            _requestedReloads.Clear();
            _lastFolderWrite.Clear();
        }
        foreach (FileSystemWatcher watcher in watchers)
            watcher.Dispose();

        if (_tickHandler is not null)
        {
            try { _host.Events.Tick -= _tickHandler; }
            catch (Exception error)
            {
                SafeLog(
                    static (log, message, exception) => log.Error(message, exception),
                    "plugin reload tick handler could not be removed",
                    error);
            }
            _tickHandler = null;
        }

        _command?.Dispose();
        _command = null;
    }

    /// <summary>
    /// One watcher per plugins folder rather than per plugin: it follows a
    /// plugin folder that is replaced as a whole, and a client running many
    /// plugins holds a handful of watches rather than one each.
    /// </summary>
    private void StartWatcher(string root)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime,
            };
            watcher.Changed += OnFolderEvent;
            watcher.Created += OnFolderEvent;
            watcher.Deleted += OnFolderEvent;
            watcher.Renamed += OnFolderEvent;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException
            or ArgumentException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            SafeLog(
                static (log, message, exception) => log.Warn(
                    exception is null ? message : $"{message}: {exception.Message}"),
                $"plugin folder '{root}' is not watched, so its plugins reload only "
                    + $"with /{CommandVerb} reload",
                error);
            return;
        }

        lock (_requestGate)
        {
            if (_watching)
            {
                _watchers.Add(watcher);
                return;
            }
        }
        watcher.Dispose();
    }

    /// <summary>Records what a plugin folder's changes are matched against. Under the request gate.</summary>
    private void Watch(string directory, PluginManifest manifest) =>
        _watchedFolders[Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))] =
            new WatchedFolder(manifest.Id, manifest.EntryDll.Replace('\\', '/'));

    private void OnFolderEvent(object sender, FileSystemEventArgs change)
    {
        string root = ((FileSystemWatcher)sender).Path;
        NoteFolderWrite(root, change.FullPath, change.ChangeType);
        if (change is RenamedEventArgs renamed)
            NoteFolderWrite(root, renamed.OldFullPath, change.ChangeType);
    }

    private void OnWatcherError(object sender, ErrorEventArgs error) =>
        SafeLog(
            static (log, message, exception) => log.Warn(
                exception is null ? message : $"{message}: {exception.Message}"),
            $"plugin folder watch on '{((FileSystemWatcher)sender).Path}' missed changes; "
                + $"use /{CommandVerb} reload for a plugin that did not reload",
            error.GetException());

    /// <summary>
    /// A write inside a plugin folder. A change to the entry assembly or
    /// <c>plugin.json</c>, or the folder itself appearing, marks the plugin
    /// for reload; every later write in the folder restarts the quiet period.
    /// The player's own <c>files</c> folder is not the plugin's code and is
    /// ignored.
    /// </summary>
    private void NoteFolderWrite(string root, string fullPath, WatcherChangeTypes kind)
    {
        string relative = Path.GetRelativePath(root, fullPath);
        string[] parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] == "..")
            return;
        if (parts.Length > 1
            && string.Equals(parts[1], FilesFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string folder = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(root, parts[0])));
        string inside = string.Join('/', parts.Skip(1));
        long now = _time.GetTimestamp();
        lock (_requestGate)
        {
            if (!_watching || !_watchedFolders.TryGetValue(folder, out WatchedFolder? watched))
                return;
            bool marks = inside.Length == 0
                ? kind is WatcherChangeTypes.Created or WatcherChangeTypes.Renamed
                : string.Equals(inside, ManifestFileName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(inside, watched.EntryDll, StringComparison.OrdinalIgnoreCase);
            if (marks || _lastFolderWrite.ContainsKey(watched.Id))
                _lastFolderWrite[watched.Id] = now;
        }
    }

    private void OnTick(double elapsedSeconds)
    {
        if (_disposed)
            return;

        // Every client ticks about 67 times a second with nothing to reload,
        // and that tick allocates nothing.
        bool anyRequest;
        lock (_requestGate)
            anyRequest = _lastFolderWrite.Count != 0 || _requestedReloads.Count != 0;
        if (anyRequest)
            ReloadRequested();
        if (_unloadChecks.Count != 0)
            CheckUnloads();
    }

    private void ReloadRequested()
    {
        List<string> due = [];
        lock (_requestGate)
        {
            foreach ((string id, long lastWrite) in _lastFolderWrite.ToArray())
            {
                if (_time.GetElapsedTime(lastWrite) < QuietPeriod)
                    continue;
                _lastFolderWrite.Remove(id);
                due.Add(id);
            }
            due.AddRange(_requestedReloads);
            _requestedReloads.Clear();
        }

        foreach (string id in ExpandRequests(due))
        {
            if (_disposed)
                return;
            Reload(id);
        }
    }

    private List<string> ExpandRequests(List<string> requests)
    {
        var ids = new List<string>();
        foreach (string request in requests)
        {
            IEnumerable<string> named = string.Equals(
                    request,
                    AllPlugins,
                    StringComparison.OrdinalIgnoreCase)
                ? [.. _loaded.Select(static active => active.Loaded.Manifest.Id),
                   .. _offline.Keys]
                : [request];
            foreach (string id in named)
                AddOrdered(ids, id);
        }
        return ids;
    }

    /// <summary>
    /// Replaces a running plugin with the copy now on disk. The new copy is
    /// read and checked first, so an update this client cannot run leaves
    /// the running copy alone. Then the old copy is switched off, everything
    /// it registered is released and its assemblies are let go; the new copy
    /// is created, handed a fresh host and enabled; and over the next second
    /// the old copy is checked to have really left memory.
    /// </summary>
    private void Reload(string id)
    {
        int index = _loaded.FindIndex(active => string.Equals(
            active.Loaded.Manifest.Id,
            id,
            StringComparison.OrdinalIgnoreCase));
        string directory;
        PluginManifest current;
        if (index >= 0)
        {
            directory = _loaded[index].Directory;
            current = _loaded[index].Loaded.Manifest;
        }
        else if (_offline.TryGetValue(id, out OfflinePlugin? offline))
        {
            directory = offline.Directory;
            current = offline.Manifest;
        }
        else
        {
            Say($"No plugin '{id}' is running in this client.");
            return;
        }

        string name = current.DisplayName;
        string keeps = index >= 0 ? " The running copy stays." : string.Empty;
        if (current.Declares(PluginKind.RenderPack))
        {
            Say($"{name} was not reloaded: it draws with the renderer, which takes a "
                + "new copy only when the client restarts.");
            return;
        }

        PluginManifest next;
        try
        {
            next = PluginManifest.Parse(
                File.ReadAllText(Path.Combine(directory, ManifestFileName)));
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or PluginManifestException)
        {
            Say($"{name} was not reloaded: its {ManifestFileName} could not be read "
                + $"({Describe(error)}).{keeps}");
            return;
        }

        if (RefuseReplacement(current, next) is { } refusal)
        {
            Say($"{name} {next.Version} was not loaded: {refusal}.{keeps}");
            return;
        }

        LoadedPlugin? unreadable = PluginLoader.Prepare(
            directory,
            next,
            registerRenderPack: false,
            out PreparedPlugin? prepared);
        if (unreadable is not null)
        {
            ReleaseFailedLoad(unreadable);
            Say($"{name} was not reloaded: the new copy could not be loaded "
                + $"({Describe(unreadable.Error!)}).{keeps}");
            return;
        }

        if (index >= 0)
        {
            ActivePlugin previous = _loaded[index];
            _loaded.RemoveAt(index);
            _unloadChecks.Add(new UnloadCheck(
                name,
                Release(previous),
                UnloadCheckAttempts,
                TicksBetweenUnloadChecks));
        }

        Activate(prepared!, directory, index >= 0 ? index : _loaded.Count);
    }

    /// <summary>
    /// Why a new copy of a plugin cannot replace the running one in this
    /// client, or null when it can.
    /// </summary>
    private string? RefuseReplacement(PluginManifest current, PluginManifest next)
    {
        if (!string.Equals(current.Id, next.Id, StringComparison.OrdinalIgnoreCase))
        {
            return $"its new {ManifestFileName} names a different plugin ('{next.Id}'); "
                + "restart the client to load it";
        }
        if (!PluginApi.IsSupported(next.ApiVersion))
        {
            return $"it needs plugin API version {next.ApiVersion}, and this client "
                + $"supports {PluginApi.MinimumSupported} to {PluginApi.Current}; "
                + "it needs a newer client";
        }
        if (next.Declares(PluginKind.RenderPack))
        {
            return "it now draws with the renderer, which takes a new copy only when "
                + "the client restarts";
        }
        if (!next.Kinds.Any(_supportedKinds.Contains))
            return "it no longer declares anything this client runs";
        if (_hostKind is { } hostKind
            && PluginHostCompatibility.Evaluate(next, hostKind, _hostVersion)
                is { } incompatibility)
        {
            return $"it {incompatibility}; it needs a newer client or a restart";
        }
        return null;
    }

    private void Activate(PreparedPlugin prepared, string directory, int position)
    {
        PluginManifest manifest = prepared.Manifest;
        var scope = new ScopedPluginHost(
            _host,
            manifest.Id,
            manifest.DisplayName,
            directory,
            _statusBoard,
            isHotReload: true);
        LoadedPlugin loaded = PluginLoader.Activate(prepared, scope, renderPacks: null);
        if (!loaded.Success)
        {
            scope.Dispose();
            ReleaseFailedLoad(loaded);
            GoOffline(manifest, directory, loaded.Error!);
            return;
        }

        try
        {
            loaded.Plugin?.Enable();
        }
        catch (Exception error)
        {
            ReleaseFailedEnable(loaded, scope, null);
            GoOffline(manifest, directory, error);
            return;
        }

        _loaded.Insert(Math.Min(position, _loaded.Count), new ActivePlugin(
            loaded,
            scope,
            null,
            directory));
        _offline.Remove(manifest.Id);
        lock (_requestGate)
        {
            if (_watching)
                Watch(directory, manifest);
        }

        Say($"Reloaded {manifest.DisplayName} {manifest.Version}.");
        Report(new PluginSessionStatus(manifest.Id, PluginSessionStatusKind.Loaded));
        RaiseLoginIfInWorld(scope, manifest.Id);
    }

    private void GoOffline(PluginManifest manifest, string directory, Exception error)
    {
        _offline[manifest.Id] = new OfflinePlugin(directory, manifest);
        string reason = Describe(error);
        Say($"{manifest.DisplayName} {manifest.Version} failed to start ({reason}). "
            + $"It is off until its files change again or /{CommandVerb} reload {manifest.Id}.");
        Report(new PluginSessionStatus(
            manifest.Id,
            PluginSessionStatusKind.Failed,
            reason));
    }

    /// <summary>
    /// Switches a running plugin off and lets go of it: Disable, then every
    /// registration the host handed it, then its assemblies. Returns what
    /// tells whether the assemblies have really left memory.
    /// </summary>
    private WeakReference Release(ActivePlugin active)
    {
        LoadedPlugin loaded = active.Loaded;
        if (loaded.Plugin is not null)
        {
            try
            {
                loaded.Plugin.Disable();
            }
            catch (Exception error)
            {
                SafeLog(
                    static (log, message, exception) =>
                        log.Error(message, exception),
                    $"plugin disable failed: {loaded.Manifest.Id}",
                    error);
            }
        }

        active.Scope.Dispose();
        ReleaseRenderScope(active.RenderPackScope, loaded.Manifest.Id);
        var context = new WeakReference(loaded.LoadContext!);
        _releasedContexts.Add(context);
        try
        {
            loaded.LoadContext!.Unload();
        }
        catch (Exception error)
        {
            SafeLog(
                static (log, message, exception) =>
                    log.Error(message, exception),
                $"plugin unload failed: {loaded.Manifest.Id}",
                error);
        }
        return context;
    }

    /// <summary>
    /// Looks, a few ticks apart, for the previous copies of reloaded plugins
    /// to have left memory, and says so when one has not. Never in the tick
    /// that released it: that tick is still holding the old copy's handlers.
    /// </summary>
    private void CheckUnloads()
    {
        if (_unloadChecks.Count == 0)
            return;

        bool collected = false;
        for (int index = _unloadChecks.Count - 1; index >= 0; index--)
        {
            UnloadCheck check = _unloadChecks[index];
            if (--check.TicksUntilNext > 0)
                continue;
            if (!collected)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                collected = true;
            }
            if (!check.Context.IsAlive)
            {
                _unloadChecks.RemoveAt(index);
                SafeLog(
                    static (log, message, _) => log.Info(message),
                    $"the previous copy of {check.Name} has left memory",
                    null);
                continue;
            }
            if (--check.AttemptsLeft > 0)
            {
                check.TicksUntilNext = TicksBetweenUnloadChecks;
                continue;
            }

            _unloadChecks.RemoveAt(index);
            Say($"The previous copy of {check.Name} did not unload. It is switched off, "
                + "but it stays in memory until the client restarts. A plugin that keeps "
                + "static state, runs its own threads or timers, or subscribes to events "
                + "outside its host cannot be unloaded; see the plugin guide.");
        }
    }

    /// <summary>
    /// A plugin started while the character is already in the world hears
    /// the login it missed, so a plugin that sets up on login needs nothing
    /// special for a reload.
    /// </summary>
    private void RaiseLoginIfInWorld(ScopedPluginHost scope, string pluginId)
    {
        bool inWorld;
        try
        {
            inWorld = _host.Automation.Character.IsInWorld;
        }
        catch (Exception error) when (error is InvalidOperationException
            or ObjectDisposedException
            or NotSupportedException)
        {
            SafeLog(
                static (log, message, exception) => log.Error(message, exception),
                $"could not tell whether the character is in the world for {pluginId}",
                error);
            return;
        }
        if (!inWorld)
            return;
        scope.RaiseLoginComplete((kind, error) => SafeLog(
            static (log, message, exception) => log.Error(message, exception),
            $"plugin {kind} handler threw: {pluginId}",
            error));
    }

    /// <summary>
    /// Writes to the player's chat and to the log: a reload is something the
    /// player asked for or caused, so what came of it is said where they are
    /// looking.
    /// </summary>
    private void Say(string text)
    {
        SafeLog(static (log, message, _) => log.Info(message), text, null);
        try
        {
            _host.Automation.Chat.PostSystemMessage(text);
        }
        catch (Exception error)
        {
            SafeLog(
                static (log, message, exception) => log.Error(message, exception),
                "a plugin reload message could not be written to chat",
                error);
        }
    }

    private void ReleaseFailedEnable(
        LoadedPlugin loaded,
        ScopedPluginHost scope,
        ScopedRenderPackRegistry? renderPackScope)
    {
        if (loaded.Plugin is not null)
        {
            try
            {
                loaded.Plugin.Disable();
            }
            catch (Exception error)
            {
                SafeLog(
                    static (log, message, exception) =>
                        log.Error(message, exception),
                    $"plugin cleanup after enable failure failed: {loaded.Manifest.Id}",
                    error);
            }
        }

        scope.Dispose();
        ReleaseRenderScope(renderPackScope, loaded.Manifest.Id);

        _releasedContexts.Add(new WeakReference(loaded.LoadContext!));
        try
        {
            loaded.LoadContext!.Unload();
        }
        catch (Exception error)
        {
            SafeLog(
                static (log, message, exception) =>
                    log.Error(message, exception),
                $"plugin unload after enable failure failed: {loaded.Manifest.Id}",
                error);
        }
    }

    private void ReleaseFailedLoad(LoadedPlugin loaded)
    {
        if (loaded.Plugin is not null)
        {
            try
            {
                loaded.Plugin.Disable();
            }
            catch (Exception error)
            {
                SafeLog(
                    static (log, message, exception) =>
                        log.Error(message, exception),
                    $"plugin cleanup after initialize failure failed: {loaded.Manifest.Id}",
                    error);
            }
        }

        if (loaded.LoadContext is null)
            return;

        _releasedContexts.Add(new WeakReference(loaded.LoadContext));
        try
        {
            loaded.LoadContext.Unload();
        }
        catch (Exception error)
        {
            SafeLog(
                static (log, message, exception) =>
                    log.Error(message, exception),
                $"plugin unload after load failure failed: {loaded.Manifest.Id}",
                error);
        }
    }

    private void ReleaseRenderScope(
        ScopedRenderPackRegistry? scope,
        string pluginId)
    {
        if (scope is null)
            return;
        try
        {
            scope.Dispose();
        }
        catch (Exception error)
        {
            SafeLog(
                static (log, message, exception) =>
                    log.Error(message, exception),
                $"render-pack registration cleanup failed: {pluginId}",
                error);
        }
    }

    private void Report(PluginSessionStatus status)
    {
        if (_report is null)
            return;
        try
        {
            _report(status);
        }
        catch (Exception error)
        {
            SafeLog(
                static (log, message, exception) =>
                    log.Error(message, exception),
                $"plugin status observer failed for {status.Plugin}",
                error);
        }
    }

    private void SafeLog(
        Action<IPluginLogger, string, Exception?> write,
        string message,
        Exception? error)
    {
        try { write(_host.Log, message, error); }
        catch { }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static string[] DistinctRoots(IEnumerable<string> roots) =>
        roots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();

    private static string DescribeDuplicate(string id, List<PluginDiscoveryResult> available) =>
        $"plugin '{id}' is declared in more than one plugin folder: "
        + string.Join(", ", available.Select(static candidate => candidate.PluginDirectory))
        + ".";

    private static void AddOrdered(List<string> ordered, string id)
    {
        if (!ordered.Contains(id, StringComparer.OrdinalIgnoreCase))
            ordered.Add(id);
    }

    private static void AddError(
        Dictionary<string, List<Exception>> errors,
        string id,
        Exception error)
    {
        if (!errors.TryGetValue(id, out List<Exception>? list))
        {
            list = [];
            errors.Add(id, list);
        }
        list.Add(error);
    }

    private static string Describe(Exception error)
    {
        Exception root = error.GetBaseException();
        return string.IsNullOrWhiteSpace(root.Message)
            ? root.GetType().Name
            : root.Message;
    }

    private static bool IsDiscoveryFailure(Exception error) =>
        error is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException;

    private sealed record ActivePlugin(
        LoadedPlugin Loaded,
        ScopedPluginHost Scope,
        ScopedRenderPackRegistry? RenderPackScope,
        string Directory);

    private sealed record OfflinePlugin(string Directory, PluginManifest Manifest);

    private sealed record WatchedFolder(string Id, string EntryDll);

    private sealed class UnloadCheck(
        string name,
        WeakReference context,
        int attempts,
        int ticksUntilNext)
    {
        internal string Name { get; } = name;
        internal WeakReference Context { get; } = context;
        internal int AttemptsLeft { get; set; } = attempts;
        internal int TicksUntilNext { get; set; } = ticksUntilNext;
    }
}
