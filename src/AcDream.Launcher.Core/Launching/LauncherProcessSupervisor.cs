using System.Runtime.ExceptionServices;

namespace AcDream.Launcher.Core.Launching;

public interface ILauncherProcessSupervisor : IDisposable
{
    LauncherSessionState State { get; }

    int? ExitCode { get; }

    event EventHandler<LauncherSessionState>? StateChanged;

    void Start(LauncherProcessSpec spec, string? password);

    void Stop(TimeSpan timeout);
}

public interface ILauncherProcessSupervisorFactory
{
    ILauncherProcessSupervisor Create();
}

public sealed class LauncherProcessSupervisorFactory(
    ILauncherChildProcessFactory? childProcessFactory = null)
    : ILauncherProcessSupervisorFactory
{
    private readonly ILauncherChildProcessFactory _childProcessFactory =
        childProcessFactory ?? new SystemChildProcessFactory();

    public ILauncherProcessSupervisor Create() =>
        new LauncherProcessSupervisor(_childProcessFactory);
}

public sealed class LauncherProcessSupervisor : ILauncherProcessSupervisor
{
    private static readonly TimeSpan DisposeStopTimeout = TimeSpan.FromSeconds(5);
    private readonly ILauncherChildProcessFactory _factory;
    private readonly object _gate = new();
    private readonly Queue<LauncherSessionState> _pendingStateChanges = [];
    private ILauncherChildProcess? _process;
    private LauncherSessionState _state = LauncherSessionState.Starting;
    private int? _exitCode;
    private bool _publishingStateChanges;
    private bool _disposed;

    public LauncherProcessSupervisor(ILauncherChildProcessFactory? factory = null)
    {
        _factory = factory ?? new SystemChildProcessFactory();
    }

    public LauncherSessionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            lock (_gate)
            {
                return _exitCode;
            }
        }
    }

    public event EventHandler<LauncherSessionState>? StateChanged;

    public void Start(LauncherProcessSpec spec, string? password)
    {
        ArgumentNullException.ThrowIfNull(spec);

        ILauncherChildProcess process;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is not null)
            {
                throw new InvalidOperationException(
                    "This supervisor already owns a process; start a new "
                    + "supervisor per launched session.");
            }

            process = _factory.Create(spec);
            process.Exited += OnProcessExited;
            _process = process;
        }

        SetState(LauncherSessionState.Starting);

        bool started = false;
        try
        {
            process.Start();
            started = true;

            if (password is not null)
            {
                process.StandardInput.Write(password);
                process.StandardInput.Write('\n');
                process.StandardInput.Flush();
            }

            process.StandardInput.Close();
        }
        catch
        {
            lock (_gate)
            {
                process.Exited -= OnProcessExited;
                _process = null;
            }

            if (started)
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                }
            }

            process.Dispose();

            throw;
        }

        SetState(LauncherSessionState.Running);
    }

    public void Stop(TimeSpan timeout)
    {
        ILauncherChildProcess? process;
        lock (_gate)
        {
            process = _process;
        }

        if (process is null || process.HasExited)
        {
            return;
        }

        StopProcess(process, timeout);
    }

    private static void StopProcess(
        ILauncherChildProcess process,
        TimeSpan timeout)
    {
        process.TryRequestGracefulStop();
        process.CloseMainWindow();
        if (!process.WaitForExit(timeout) && !process.HasExited)
        {
            process.Kill();
            if (!process.WaitForExit(Timeout.InfiniteTimeSpan) && !process.HasExited)
            {
                throw new InvalidOperationException(
                    "The launcher child could not be observed terminal after it was killed.");
            }
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int? exitCode = null;
        if (sender is ILauncherChildProcess process)
        {
            try
            {
                exitCode = process.HasExited ? process.ExitCode : null;
            }
            catch (Exception error)
                when (error is InvalidOperationException
                    or ObjectDisposedException)
            {
            }
        }

        SetState(LauncherSessionState.Exited, exitCode);
    }

    private void SetState(LauncherSessionState state, int? exitCode = null)
    {
        bool publish;
        lock (_gate)
        {
            if (_state == LauncherSessionState.Exited)
            {
                return;
            }

            _state = state;
            if (state == LauncherSessionState.Exited)
            {
                _exitCode = exitCode;
            }

            _pendingStateChanges.Enqueue(state);
            publish = !_publishingStateChanges;
            if (publish)
            {
                _publishingStateChanges = true;
            }
        }

        if (publish)
        {
            PublishPendingStateChanges();
        }
    }

    private void PublishPendingStateChanges()
    {
        Exception? firstException = null;
        while (true)
        {
            LauncherSessionState state;
            lock (_gate)
            {
                if (_pendingStateChanges.Count == 0)
                {
                    _publishingStateChanges = false;
                    break;
                }

                state = _pendingStateChanges.Dequeue();
            }

            try
            {
                StateChanged?.Invoke(this, state);
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }

        if (firstException is not null)
        {
            ExceptionDispatchInfo.Capture(firstException).Throw();
        }
    }

    public void Dispose()
    {
        ILauncherChildProcess? process;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            return;
        }

        if (process is { HasExited: false })
        {
            StopProcess(process, DisposeStopTimeout);
        }

        process.Exited -= OnProcessExited;
        process.Dispose();
    }
}
