using AcDream.Core.Plugins;
using AcDream.Headless.Diagnostics;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Plugins;

internal sealed class HeadlessPluginSession : IDisposable
{
    private readonly HeadlessPluginHost _host;
    private readonly PluginSession _plugins;
    private readonly string[] _roots;
    private readonly IReadOnlyList<string>? _allowList;
    private int _disposeStage;
    private bool _started;
    private bool _disposed;

    private HeadlessPluginSession(
        HeadlessPluginHost host,
        PluginSession plugins,
        string[] roots,
        IReadOnlyList<string>? allowList)
    {
        _host = host;
        _plugins = plugins;
        _roots = roots;
        _allowList = allowList;
    }

    internal int LoadedCount => _plugins.LoadedCount;
    internal HeadlessPluginHost Host => _host;

    internal IReadOnlyList<WeakReference> CaptureLoadContextWeakReferences() =>
        _plugins.CaptureLoadContextWeakReferences();

    internal static HeadlessPluginSession Create(
        GameRuntime runtime,
        HeadlessDiagnosticWriter diagnostics,
        SessionStatusWriter statusWriter,
        string sessionId,
        IEnumerable<string> roots,
        IReadOnlyList<string>? allowList,
        IPluginCommandRegistry? commands = null,
        IPluginStorage? vtankProfiles = null,
        IReadOnlyDictionary<string, Dictionary<string, string>>? sessionSettings = null,
        Func<string, bool>? submitChatText = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(statusWriter);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(roots);

        var host = new HeadlessPluginHost(
            runtime,
            new HeadlessPluginLogger(
                diagnostics,
                sessionId,
                () => runtime.Generation.Value),
            commands,
            vtankProfiles,
            sessionSettings,
            submitChatText);
        var plugins = new PluginSession(
            host,
            status => Report(statusWriter, sessionId, status),
            renderPacks: null,
            supportedKinds: [PluginKind.Gameplay]);
        return new HeadlessPluginSession(
            host,
            plugins,
            roots.ToArray(),
            allowList);
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            throw new InvalidOperationException(
                "The headless plugin session has already started.");
        _started = true;
        _plugins.Start(_roots, _allowList);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        while (!_disposed)
        {
            switch (_disposeStage)
            {
                case 0:
                    _plugins.Dispose();
                    _disposeStage++;
                    break;
                case 1:
                    _host.Dispose();
                    _disposeStage++;
                    _disposed = true;
                    break;
                default:
                    throw new InvalidOperationException(
                        "Unknown headless plugin teardown stage.");
            }
        }
    }

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
