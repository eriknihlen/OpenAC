using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Platform;
using AcDream.Headless.Policies;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Hosting;

internal sealed class HeadlessProcessHost : IDisposable
{
    private readonly HeadlessSessionHost[] _sessions;
    private readonly HeadlessProcessScheduler _scheduler;
    private readonly HeadlessDiagnosticWriter _diagnostics;
    private readonly HeadlessProcessContentOwner? _content;
    private readonly HeadlessProcessResourceSampler _resources;
    private readonly CancellationTokenSource _consoleQuitRequested = new();
    private readonly HeadlessConsoleController? _console;
    private readonly IDisposable? _consoleRendererSubscription;
    private int _disposeIndex;
    private bool _disposed;

    internal HeadlessProcessHost(
        HeadlessConfiguration configuration,
        HeadlessPathSet paths,
        TextReader standardInput,
        TextWriter diagnostics,
        ILiveSessionOperations? sessionOperations = null,
        TimeProvider? timeProvider = null,
        IHeadlessProcessContentFactory? contentFactory = null,
        HeadlessDirectCredentials? directCredentials = null,
        bool consoleEnabled = false,
        bool standardOutputIsTerminal = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (configuration.Sessions.Count == 0)
        {
            throw new HeadlessConfigurationException(
                "Headless run mode requires at least one session.");
        }
        if (directCredentials is not null
            && configuration.Sessions.Count != 1)
        {
            throw new HeadlessConfigurationException(
                "Direct credentials require exactly one configured session.");
        }
        _diagnostics = new HeadlessDiagnosticWriter(diagnostics);
        HeadlessStaticStateAudit.ValidateProcessIsolation(
            configuration.Sessions.Count, _diagnostics);
        var credentials = new HeadlessCredentialResolver(
            standardInput,
            paths.ConfigDirectory);
        var sessions = new List<HeadlessSessionHost>(
            configuration.Sessions.Count);
        string[] pluginRoots =
        [
            Path.Combine(AppContext.BaseDirectory, "plugins"),
            paths.PluginsDirectory,
        ];
        var vtankProfiles = new AcDream.Headless.Plugins.FilePluginStorage(
            paths.VtankProfilesDirectory);
        HeadlessProcessContentOwner? content = null;
        HeadlessProcessResourceSampler? resources = null;
        HeadlessConsoleController? console = null;
        IDisposable? consoleRendererSubscription = null;
        var gateCoordinator = new FellowshipAllegianceGateCoordinator();
        try
        {
            if (configuration.Process?.Content is { } contentDescriptor)
            {
                content = new HeadlessProcessContentOwner(
                    contentDescriptor,
                    message => _diagnostics.Message(
                        "process-content",
                        message),
                    contentFactory);
            }

            foreach (HeadlessSessionDescriptor? candidate
                in configuration.Sessions)
            {
                HeadlessSessionDescriptor descriptor = candidate
                    ?? throw new HeadlessConfigurationException(
                        "Headless run mode cannot contain a null session.");
                if (directCredentials is not null)
                {
                    descriptor = WithAccount(
                        descriptor,
                        directCredentials.User);
                }
                HeadlessCredentialSecret secret = directCredentials is null
                    ? credentials.Resolve(
                        descriptor.Id,
                        descriptor.Credential)
                    : new HeadlessCredentialSecret(
                        "command-line",
                        directCredentials.Password);
                HeadlessProcessContentOwner.HeadlessProcessContentLease?
                    contentLease = content?.AcquireLease(descriptor.Id);
                try
                {
                    sessions.Add(new HeadlessSessionHost(
                        descriptor,
                        secret,
                        _diagnostics,
                        sessionOperations,
                        timeProvider,
                        contentLease: contentLease,
                        gateCoordinator: gateCoordinator,
                        pluginRoots: pluginRoots,
                        vtankProfiles: vtankProfiles));
                }
                catch
                {
                    contentLease?.Dispose();
                    secret.Dispose();
                    throw;
                }
            }

            _sessions = sessions.ToArray();
            resources = new HeadlessProcessResourceSampler(timeProvider);
            _scheduler = new HeadlessProcessScheduler(
                _sessions,
                timeProvider,
                observation: snapshot =>
                    CaptureResources(
                        "periodic",
                        resources,
                        snapshot,
                        content));
            _resources = resources;
            _content = content;
            _disposeIndex = _sessions.Length - 1;

            if (consoleEnabled && _sessions.Length == 1)
            {
                HeadlessSessionHost session = _sessions[0];
                var renderer = new HeadlessConsoleRenderer(
                    diagnostics,
                    useColor: standardOutputIsTerminal);
                consoleRendererSubscription =
                    session.Runtime.Subscribe(renderer);
                HeadlessConsoleController controller = new(
                    standardInput,
                    diagnostics,
                    session.SubmitConsoleLine,
                    () => BuildStatusText(session),
                    _consoleQuitRequested);
                var spewPump = new HeadlessConsoleSpewBoxPump(
                    session.Runtime.CommunicationOwner.SpewBox,
                    () => session.Runtime.Clock.SimulationTimeSeconds,
                    renderer.WriteInterfaceText);
                session.ConsolePump = () =>
                {
                    controller.DrainDue();
                    spewPump.Pump();
                };
                console = controller;
            }
            else if (consoleEnabled)
            {
                _diagnostics.Message("console", "single-session only");
            }
            _console = console;
            _consoleRendererSubscription = consoleRendererSubscription;
        }
        catch
        {
            console?.Dispose();
            consoleRendererSubscription?.Dispose();
            resources?.Dispose();
            for (int index = sessions.Count - 1; index >= 0; index--)
                sessions[index].Dispose();
            content?.Dispose();
            throw;
        }
    }

    private static string BuildStatusText(HeadlessSessionHost session)
    {
        RuntimeMovementSnapshot movement =
            session.Runtime.MovementOwner.Snapshot;
        string position = movement.HasController
            ? $"cell=0x{movement.Position.ObjCellId:X8} "
                + $"local=({movement.Position.Frame.Origin.X:F2},"
                + $"{movement.Position.Frame.Origin.Y:F2},"
                + $"{movement.Position.Frame.Origin.Z:F2})"
            : "unknown";
        return $"generation={session.Runtime.Generation.Value} "
            + $"position={position} "
            + $"plugins={session.Plugins.LoadedCount} loaded";
    }

    internal HeadlessSessionHost Session => _sessions.Length == 1
        ? _sessions[0]
        : throw new InvalidOperationException(
            "The process owns more than one headless session.");
    internal IReadOnlyList<HeadlessSessionHost> Sessions => _sessions;
    internal HeadlessSchedulerSnapshot Scheduler =>
        _scheduler.CaptureSnapshot();
    internal HeadlessProcessContentSnapshot? Content =>
        _content?.CaptureSnapshot();

    private static HeadlessSessionDescriptor WithAccount(
        HeadlessSessionDescriptor source,
        string account) =>
        source with { Account = account };

    internal Task<HeadlessExitCode> RunAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<HeadlessExitCode>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(
                    RunOnUpdateThread(cancellationToken));
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        })
        {
            IsBackground = true,
            Name = "acdream-headless-update",
        };
        thread.Start();
        return completion.Task;
    }

    private HeadlessExitCode RunOnUpdateThread(
        CancellationToken cancellationToken)
    {
        foreach (HeadlessSessionHost session in _sessions)
        {
            RuntimeSessionStartResult started = session.Start();
            if (started.Status == RuntimeSessionStartStatus.ProbeComplete)
            {
                _diagnostics.Lifecycle(
                    session.SessionId,
                    "probed",
                    session.Runtime);
                continue;
            }
            if (started.Status != RuntimeSessionStartStatus.Connected)
            {
                if (started.Error is { } error)
                {
                    _diagnostics.Failure(
                        session.SessionId,
                        "start",
                        error);
                }
                return HeadlessExitCode.ConnectionError;
            }

            _diagnostics.Lifecycle(
                session.SessionId,
                "running",
                session.Runtime);
        }
        _scheduler.RebaseDeadlinesAfterSessionStart();
        CaptureResources(
            "running-start",
            _resources,
            _scheduler.CaptureSnapshot(),
            _content);

        using CancellationTokenSource linkedQuit =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _consoleQuitRequested.Token);
        try
        {
            _scheduler.Run(linkedQuit.Token);
        }
        catch (OperationCanceledException)
            when (linkedQuit.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _diagnostics.Failure("process", "tick", error);
            return HeadlessExitCode.RuntimeError;
        }

        CaptureResources(
            "running-stop",
            _resources,
            _scheduler.CaptureSnapshot(),
            _content);
        return _scheduler.CaptureSnapshot().FaultedSessionCount > 0
            ? HeadlessExitCode.RuntimeError
            : HeadlessExitCode.Success;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _console?.Dispose();
        _consoleRendererSubscription?.Dispose();
        _consoleQuitRequested.Dispose();
        while (_disposeIndex >= 0)
        {
            _sessions[_disposeIndex].Dispose();
            _disposeIndex--;
        }
        _content?.Dispose();
        CaptureResources(
            "disposed",
            _resources,
            _scheduler.CaptureSnapshot(),
            _content);
        _resources.Dispose();
        _disposed = true;
    }

    private void CaptureResources(
        string state,
        HeadlessProcessResourceSampler resources,
        HeadlessSchedulerSnapshot scheduler,
        HeadlessProcessContentOwner? content)
    {
        HeadlessProcessResourceSnapshot snapshot = resources.Capture(
            state,
            _sessions,
            scheduler,
            content?.CaptureSnapshot());
        _diagnostics.Resources(state, snapshot);
    }
}
