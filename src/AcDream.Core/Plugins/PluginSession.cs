using System.Collections.Concurrent;
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
/// its code, markup or <c>plugin.json</c> changes on disk and those files
/// have then been quiet for <see cref="QuietPeriod"/>. A plugin that failed
/// to start is retried the same way. Reloads run on the thread that raises
/// <see cref="IEvents.Tick"/>, the thread every plugin already runs on.
/// </para>
/// </summary>
public sealed class PluginSession : IDisposable
{
    /// <summary>The chat verb a player reloads plugins with.</summary>
    public const string CommandVerb = "plugin";

    /// <summary>
    /// How long a plugin's code, markup and <c>plugin.json</c> must go
    /// without a write, after one of them changed, before the plugin is
    /// reloaded. An update writes several files; the plugin reloads once,
    /// after the last of them.
    /// </summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1);

    private const string AllPlugins = "all";
    private const string ManifestFileName = "plugin.json";

    private readonly IPluginHost _host;
    private readonly Action<PluginSessionStatus>? _report;
    private readonly IRenderPackRegistry? _renderPacks;
    private readonly HashSet<PluginKind> _supportedKinds;
    private readonly PluginHostKind? _hostKind;
    private readonly PluginHostVersion? _hostVersion;
    private readonly TimeProvider _time;
    private readonly PluginUnloadWatch _unloadWatch;
    private readonly List<ActivePlugin> _loaded = [];

    /// <summary>
    /// The status lines this session's plugins share, one board for all of
    /// them, so every host that runs plugins through a session keeps it the
    /// same way.
    /// </summary>
    private readonly PluginStatusBoard _statusBoard = new();
    private readonly List<WeakReference> _releasedContexts = [];

    /// <summary>
    /// Plugins this session was asked to run that are not running: they
    /// failed to start, or a reload stopped after the running copy was gone.
    /// Kept so the next attempt knows where they live. Touched on the tick
    /// thread only, after Start.
    /// </summary>
    private readonly Dictionary<string, OfflinePlugin> _offline =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the process-wide unload watch found, waiting for this session's tick.</summary>
    private readonly ConcurrentQueue<(string Name, bool LeftMemory)> _unloadResults = new();

    // Written by the chat command and the folder watchers, which do not run
    // on the tick thread, and read on the tick thread.
    private readonly object _requestGate = new();
    private readonly List<string> _requestedReloads = [];
    private readonly Dictionary<string, string> _watchedFolders = new(PathComparer);
    private readonly Dictionary<string, long> _lastFolderWrite =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private bool _watching;

    private string[] _roots = [];
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
        : this(
            host,
            report,
            renderPacks,
            supportedKinds,
            hostKind,
            hostVersion,
            timeProvider,
            PluginUnloadWatch.Shared)
    {
    }

    internal PluginSession(
        IPluginHost host,
        Action<PluginSessionStatus>? report,
        IRenderPackRegistry? renderPacks,
        IEnumerable<PluginKind>? supportedKinds,
        PluginHostKind? hostKind,
        PluginHostVersion? hostVersion,
        TimeProvider? timeProvider,
        PluginUnloadWatch unloadWatch)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _report = report;
        _renderPacks = renderPacks;
        _hostKind = hostKind;
        _hostVersion = hostVersion;
        _time = timeProvider ?? TimeProvider.System;
        _unloadWatch = unloadWatch ?? throw new ArgumentNullException(nameof(unloadWatch));
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
        _roots = roots;
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
        // Where a plugin that is refused before it is loaded lives, so a
        // later change to its files can try it again.
        var refusedFolders = new Dictionary<string, OfflinePlugin>(
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
                    refusedFolders.TryAdd(
                        directoryId,
                        new OfflinePlugin(directoryId, result.PluginDirectory, Manifest: null));
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
                        refusedFolders.TryAdd(
                            id,
                            new OfflinePlugin(id, result.PluginDirectory, result.Manifest));
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
                        refusedFolders.TryAdd(
                            id,
                            new OfflinePlugin(id, result.PluginDirectory, result.Manifest));
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
            LoadOne(id, candidates, errors, refusedFolders);

        StartReloading();
    }

    public IReadOnlyList<WeakReference> CaptureLoadContextWeakReferences() =>
        [
            .. _releasedContexts,
            .. _loaded.Select(static active =>
                new WeakReference(active.Loaded.LoadContext!)),
        ];

    /// <summary>
    /// Asks for a plugin to be reloaded, or started again after it failed,
    /// on the next tick: by its id, or every plugin this session was asked to
    /// run when <paramref name="pluginIdOrAll"/> is <c>all</c>. Safe from any
    /// thread; what came of it is written to chat.
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
    /// for them to go quiet. For tests, which cannot otherwise tell when the
    /// operating system has delivered a change.
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
        _unloadWatch.Forget(this);

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
        Dictionary<string, List<Exception>> errors,
        IReadOnlyDictionary<string, OfflinePlugin> refusedFolders)
    {
        OfflinePlugin? failedAt = refusedFolders.GetValueOrDefault(id);
        if (candidates.TryGetValue(id, out List<PluginDiscoveryResult>? available))
        {
            if (available.Count > 1)
            {
                // Which folder is meant is the player's to settle; nothing
                // here retries either copy.
                AddError(
                    errors,
                    id,
                    new PluginDuplicateIdException(DescribeDuplicate(id, available)));
                available = [];
                failedAt = null;
            }
            foreach (PluginDiscoveryResult candidate in available)
            {
                PluginManifest manifest = candidate.Manifest!;
                failedAt = new OfflinePlugin(manifest.Id, candidate.PluginDirectory, manifest);
                ScopedRenderPackRegistry? renderPackScope =
                    manifest.Declares(PluginKind.RenderPack) && _renderPacks is not null
                        ? new ScopedRenderPackRegistry(_renderPacks)
                        : null;
                LoadedPlugin? unreadable = PluginLoader.Prepare(
                    candidate.PluginDirectory,
                    manifest,
                    registerRenderPack: renderPackScope is not null,
                    out PreparedPlugin? prepared);
                if (unreadable is not null)
                {
                    ReleaseRenderScope(renderPackScope, manifest.Id);
                    ReleaseFailedLoad(unreadable);
                    AddError(
                        errors,
                        id,
                        unreadable.Error ?? new InvalidOperationException(
                            "plugin load failed"));
                    continue;
                }

                var scope = new ScopedPluginHost(
                    _host,
                    manifest.Id,
                    manifest.DisplayName,
                    candidate.PluginDirectory,
                    _statusBoard,
                    package: prepared!.Package);
                LoadedPlugin loaded = PluginLoader.Activate(prepared, scope, renderPackScope);
                if (!loaded.Success)
                {
                    scope.Dispose();
                    ReleaseRenderScope(renderPackScope, manifest.Id);
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

        if (failedAt is not null)
            _offline[id] = failedAt with { Id = id };

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
    /// and a watcher on every plugins folder this session was configured
    /// with.
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

        lock (_requestGate)
        {
            _watching = true;
            foreach (ActivePlugin active in _loaded)
                Watch(active.Directory, active.Loaded.Manifest.Id);
            foreach (OfflinePlugin offline in _offline.Values)
                Watch(offline.Directory, offline.Id);
        }

        foreach (string root in _roots)
        {
            if (Directory.Exists(root))
                StartWatcher(root);
        }
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

    /// <summary>Records which plugin a folder's changes belong to. Under the request gate.</summary>
    private void Watch(string directory, string pluginId) =>
        _watchedFolders[Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))] = pluginId;

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
    /// A write inside a plugins folder. Only what makes up the plugin counts
    /// -- its assemblies, symbols, markup, <c>plugin.json</c> and dependency
    /// list, or the plugin's folder itself appearing -- so a plugin that
    /// keeps writing its own logs or data beside its code does not hold its
    /// reload back forever. The player's <c>files</c> folder never counts.
    /// </summary>
    private void NoteFolderWrite(string root, string fullPath, WatcherChangeTypes kind)
    {
        string relative = Path.GetRelativePath(root, fullPath);
        string[] parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] == "..")
            return;

        string folder = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(root, parts[0])));
        bool counts = parts.Length == 1
            ? kind is WatcherChangeTypes.Created or WatcherChangeTypes.Renamed
            : !PluginPackageSnapshot.IsPlayerFile(folder, fullPath)
                && PluginPackageSnapshot.Kind(fullPath) is not PackageFileKind.Other;
        if (!counts)
            return;

        long now = _time.GetTimestamp();
        lock (_requestGate)
        {
            if (_watching && _watchedFolders.TryGetValue(folder, out string? id))
                _lastFolderWrite[id] = now;
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
        if (!_unloadResults.IsEmpty)
            ReportUnloads();
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
    /// Replaces a running plugin with the copy now on disk, or starts one
    /// that is not running. The new copy is read and checked first, so an
    /// update this client cannot run leaves the running copy alone. Then the
    /// old copy is switched off, everything it registered is released and
    /// its assemblies are let go; the new copy is created, handed a fresh
    /// host and enabled; and the old copy is watched leaving memory.
    /// </summary>
    private void Reload(string id)
    {
        int index = _loaded.FindIndex(active => string.Equals(
            active.Loaded.Manifest.Id,
            id,
            StringComparison.OrdinalIgnoreCase));
        string directory;
        string expectedId;
        string name;
        PluginManifest? current;
        if (index >= 0)
        {
            directory = _loaded[index].Directory;
            current = _loaded[index].Loaded.Manifest;
            expectedId = current.Id;
            name = current.DisplayName;
        }
        else if (_offline.TryGetValue(id, out OfflinePlugin? offline))
        {
            directory = offline.Directory;
            current = offline.Manifest;
            expectedId = offline.Id;
            name = offline.Manifest?.DisplayName ?? offline.Id;
        }
        else
        {
            Say($"No plugin '{id}' is running in this client.");
            return;
        }

        string keeps = index >= 0 ? " The running copy stays." : string.Empty;
        if (index >= 0 && current!.Declares(PluginKind.RenderPack))
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

        if (RefuseReplacement(expectedId, next) is { } refusal)
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
            WeakReference context = Release(previous);
            string previousName = previous.Loaded.Manifest.DisplayName;
            _unloadWatch.Watch(
                this,
                context,
                leftMemory => _unloadResults.Enqueue((previousName, leftMemory)));
        }

        Activate(prepared!, directory, index >= 0 ? index : _loaded.Count, expectedId);
    }

    /// <summary>
    /// Why a new copy of a plugin cannot replace the running one in this
    /// client, or null when it can.
    /// </summary>
    private string? RefuseReplacement(string expectedId, PluginManifest next)
    {
        if (!string.Equals(expectedId, next.Id, StringComparison.OrdinalIgnoreCase))
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
            return "it draws with the renderer, which takes a new copy only when "
                + "the client restarts";
        }
        if (!next.Kinds.Any(_supportedKinds.Contains))
            return "it declares nothing this client runs";
        if (_hostKind is { } hostKind
            && PluginHostCompatibility.Evaluate(next, hostKind, _hostVersion)
                is { } incompatibility)
        {
            return $"it {incompatibility}; it needs a newer client or a restart";
        }
        return null;
    }

    private void Activate(
        PreparedPlugin prepared,
        string directory,
        int position,
        string requestedId)
    {
        PluginManifest manifest = prepared.Manifest;
        var scope = new ScopedPluginHost(
            _host,
            manifest.Id,
            manifest.DisplayName,
            directory,
            _statusBoard,
            isHotReload: true,
            package: prepared.Package);
        LoadedPlugin loaded = PluginLoader.Activate(prepared, scope, renderPacks: null);
        if (!loaded.Success)
        {
            scope.Dispose();
            ReleaseFailedLoad(loaded);
            GoOffline(requestedId, manifest, directory, loaded.Error!);
            return;
        }

        try
        {
            loaded.Plugin?.Enable();
        }
        catch (Exception error)
        {
            ReleaseFailedEnable(loaded, scope, null);
            GoOffline(requestedId, manifest, directory, error);
            return;
        }

        _loaded.Insert(Math.Min(position, _loaded.Count), new ActivePlugin(
            loaded,
            scope,
            null,
            directory));
        _offline.Remove(requestedId);
        lock (_requestGate)
        {
            if (_watching)
                Watch(directory, manifest.Id);
        }

        Say($"Reloaded {manifest.DisplayName} {manifest.Version}.");
        Report(new PluginSessionStatus(manifest.Id, PluginSessionStatusKind.Loaded));
        RaiseLoginIfInWorld(scope, manifest.Id);
    }

    private void GoOffline(
        string requestedId,
        PluginManifest manifest,
        string directory,
        Exception error)
    {
        _offline[requestedId] = new OfflinePlugin(requestedId, directory, manifest);
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
    /// Says what the unload watch found. A copy that left memory is only
    /// logged; one still there when the watch ends is named once in chat.
    /// </summary>
    private void ReportUnloads()
    {
        while (_unloadResults.TryDequeue(out (string Name, bool LeftMemory) result))
        {
            if (result.LeftMemory)
            {
                SafeLog(
                    static (log, message, _) => log.Info(message),
                    $"the previous copy of {result.Name} has left memory",
                    null);
                continue;
            }
            Say($"The previous copy of {result.Name} is still in memory; "
                + "it is freed when the client restarts.");
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

    /// <summary>A plugin that is not running, where it lives, and its manifest when one could be read.</summary>
    private sealed record OfflinePlugin(string Id, string Directory, PluginManifest? Manifest);
}
