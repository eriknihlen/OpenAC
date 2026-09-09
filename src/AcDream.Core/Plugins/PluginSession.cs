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

public sealed class PluginSession : IDisposable
{
    private readonly IPluginHost _host;
    private readonly Action<PluginSessionStatus>? _report;
    private readonly IRenderPackRegistry? _renderPacks;
    private readonly HashSet<PluginKind> _supportedKinds;
    private readonly List<ActivePlugin> _loaded = [];
    private readonly List<WeakReference> _releasedContexts = [];
    private bool _started;
    private bool _disposed;

    public PluginSession(
        IPluginHost host,
        Action<PluginSessionStatus>? report = null,
        IRenderPackRegistry? renderPacks = null,
        IEnumerable<PluginKind>? supportedKinds = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _report = report;
        _renderPacks = renderPacks;
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
    }

    public IReadOnlyList<WeakReference> CaptureLoadContextWeakReferences() =>
        [
            .. _releasedContexts,
            .. _loaded.Select(static active =>
                new WeakReference(active.Loaded.LoadContext!)),
        ];

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

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
            foreach (PluginDiscoveryResult candidate in available)
            {
                var scope = new ScopedPluginHost(
                    _host,
                    candidate.Manifest!.Id,
                    candidate.Manifest.DisplayName);
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
                    _loaded.Add(new ActivePlugin(loaded, scope, renderPackScope));
                    SafeLog(
                        static (log, message, _) => log.Info(message),
                        $"plugin loaded: {loaded.Manifest.Id} "
                            + $"({loaded.Manifest.DisplayName})",
                        null);
                    Report(new PluginSessionStatus(
                        loaded.Manifest.Id,
                        PluginSessionStatusKind.Loaded));
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

    private static string[] DistinctRoots(IEnumerable<string> roots)
    {
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return roots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(comparer)
            .ToArray();
    }

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
        ScopedRenderPackRegistry? RenderPackScope);
}
