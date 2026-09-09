using AcDream.Core.Plugins;
using AcDream.Platform;
using AcDream.Plugin.Abstractions;
using AcDream.Plugin.Abstractions.Rendering;
using AcDream.Runtime.Session;

namespace AcDream.App.Plugins;

internal sealed class GraphicalPluginSession : IDisposable
{
    private readonly PluginSession _plugins;
    private readonly string[] _roots;
    private readonly IReadOnlyList<string>? _allowList;
    private readonly string _sessionId;
    private readonly SessionStatusWriter _statusWriter;
    private bool _started;

    private GraphicalPluginSession(
        PluginSession plugins,
        string[] roots,
        IReadOnlyList<string>? allowList,
        string sessionId,
        SessionStatusWriter statusWriter)
    {
        _plugins = plugins;
        _roots = roots;
        _allowList = allowList;
        _sessionId = sessionId;
        _statusWriter = statusWriter;
    }

    internal int LoadedCount => _plugins.LoadedCount;

    internal IReadOnlyList<WeakReference> CaptureLoadContextWeakReferences() =>
        _plugins.CaptureLoadContextWeakReferences();

    internal static GraphicalPluginSession Create(
        ApplicationPathSet paths,
        IReadOnlyList<string>? allowList,
        string sessionId,
        IPluginHost host,
        SessionStatusWriter statusWriter,
        IRenderPackRegistry? renderPacks = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(statusWriter);

        var plugins = new PluginSession(
            host,
            status => Report(statusWriter, sessionId, status),
            renderPacks,
            renderPacks is null
                ? [PluginKind.Gameplay]
                : [PluginKind.Gameplay, PluginKind.RenderPack]);
        return new GraphicalPluginSession(
            plugins,
            [
                Path.Combine(AppContext.BaseDirectory, "plugins"),
                paths.PluginsDirectory,
            ],
            allowList,
            sessionId,
            statusWriter);
    }

    internal void Start()
    {
        if (_started)
            throw new InvalidOperationException(
                "The graphical plugin session has already started.");
        _started = true;

        _statusWriter.Started(_sessionId);
        _plugins.Start(_roots, _allowList);
    }

    public void Dispose() => _plugins.Dispose();

    private static void Report(
        SessionStatusWriter writer,
        string sessionId,
        PluginSessionStatus status)
    {
        if (status.Kind == PluginSessionStatusKind.Loaded)
        {
            writer.PluginLoaded(sessionId, status.Plugin);
            return;
        }

        writer.PluginFailed(
            sessionId,
            status.Plugin,
            status.Error ?? "plugin failed");
    }
}
