namespace AcDream.App.UI;

internal sealed class RetailUiRuntimeLease : IDisposable
{
    private object? _host;
    private Action? _quiesceInput;
    private Action? _deactivateInput;
    private Action? _disposeHost;
    private Func<bool>? _hostDisposalComplete;

    private object? _runtime;
    private Action? _disposeRuntime;
    private Func<bool>? _runtimeDisposalComplete;

    private bool _disposalFailed;
    private bool _inputQuiesced;
    private bool _inputDeactivated;
    private bool _operationActive;
    private bool _disposed;
    private bool _abandoned;

    public bool IsDisposalComplete => _disposed;

    public bool IsAbandoned => _abandoned;

    internal bool HasDisposalFailure => _disposalFailed;

    internal bool RetainsResources => _host is not null || _runtime is not null;

    public UiHost AcquireHost(Func<UiHost> factory) => AcquireHostCore(
        factory,
        static host => host.QuiesceInput(),
        static host => host.DeactivateInput(),
        static host => host.Dispose(),
        static host => host.IsDisposalComplete);

    internal T AcquireHostCore<T>(
        Func<T> factory,
        Action<T> quiesceInput,
        Action<T> deactivateInput,
        Action<T> dispose,
        Func<T, bool> disposalComplete)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(quiesceInput);
        ArgumentNullException.ThrowIfNull(deactivateInput);
        ArgumentNullException.ThrowIfNull(dispose);
        ArgumentNullException.ThrowIfNull(disposalComplete);
        ThrowIfUnavailable();
        if (_operationActive || _host is not null)
            throw new InvalidOperationException("A retained UI host is already owned.");

        _operationActive = true;
        try
        {
            T host = factory()
                ?? throw new InvalidOperationException("The retained UI host factory returned null.");
            _host = host;
            _quiesceInput = () => quiesceInput(host);
            _deactivateInput = () => deactivateInput(host);
            _disposeHost = () => dispose(host);
            _hostDisposalComplete = () => disposalComplete(host);
            return host;
        }
        finally
        {
            _operationActive = false;
        }
    }

    public RetailUiRuntime Mount(Func<RetailUiRuntime> factory) => MountCore(
        factory,
        static runtime => runtime.InitializeForLease(),
        static runtime => runtime.Dispose(),
        static runtime => runtime.IsDisposalComplete);

    internal T MountCore<T>(
        Func<T> factory,
        Action<T> initialize,
        Action<T> dispose,
        Func<T, bool> disposalComplete)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(initialize);
        ArgumentNullException.ThrowIfNull(dispose);
        ArgumentNullException.ThrowIfNull(disposalComplete);
        ThrowIfUnavailable();
        if (_operationActive)
            throw new InvalidOperationException("The retained UI lease is changing state.");
        if (_host is null)
            throw new InvalidOperationException("The retained UI host must be acquired first.");
        if (_runtime is not null)
            throw new InvalidOperationException("A retained UI runtime is already owned.");

        _operationActive = true;
        T runtime;
        try
        {
            runtime = factory()
                ?? throw new InvalidOperationException("The retained UI runtime factory returned null.");
        }
        finally
        {
            _operationActive = false;
        }

        // Publish the partial runtime before any Initialize side effect.
        _runtime = runtime;
        _disposeRuntime = () => dispose(runtime);
        _runtimeDisposalComplete = () => disposalComplete(runtime);

        _operationActive = true;
        try
        {
            initialize(runtime);
            _operationActive = false;
            return runtime;
        }
        catch (Exception initializationFailure)
        {
            _operationActive = false;
            try
            {
                Dispose();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Retail UI initialization failed and its published partial runtime did not cleanly retire.",
                    initializationFailure,
                    cleanupFailure);
            }

            throw;
        }
    }

    public void QuiesceInput()
    {
        if (_disposed || _abandoned || _inputQuiesced)
            return;
        if (_operationActive)
            throw new InvalidOperationException("The retained UI lease is changing state.");

        _operationActive = true;
        _inputQuiesced = true;
        try
        {
            _quiesceInput?.Invoke();
        }
        catch
        {
            _inputQuiesced = false;
            throw;
        }
        finally
        {
            _operationActive = false;
        }
    }

    public void DeactivateInput()
    {
        if (_disposed || _abandoned || _inputDeactivated)
            return;
        if (_operationActive)
            throw new InvalidOperationException("The retained UI lease is changing state.");

        _operationActive = true;
        _inputDeactivated = true;
        try
        {
            _deactivateInput?.Invoke();
        }
        catch
        {
            _inputDeactivated = false;
            throw;
        }
        finally
        {
            _operationActive = false;
        }
    }

    public void Dispose()
    {
        if (_disposed || _abandoned)
            return;
        if (_operationActive)
            throw new InvalidOperationException("The retained UI lease is changing state.");

        _operationActive = true;
        try
        {
            if (_runtime is not null)
            {
                _disposeRuntime!();
                if (_runtimeDisposalComplete?.Invoke() != true)
                    throw new InvalidOperationException(
                        "The retained UI runtime returned without completing disposal.");
                if (_hostDisposalComplete?.Invoke() != true)
                    throw new InvalidOperationException(
                        "The retained UI runtime completed without retiring its host.");
            }
            else if (_host is not null)
            {
                _disposeHost!();
                if (_hostDisposalComplete?.Invoke() != true)
                    throw new InvalidOperationException(
                        "The retained UI host returned without completing disposal.");
            }

            _runtime = null;
            _disposeRuntime = null;
            _runtimeDisposalComplete = null;
            _host = null;
            _quiesceInput = null;
            _deactivateInput = null;
            _disposeHost = null;
            _hostDisposalComplete = null;
            _disposalFailed = false;
            _disposed = true;
        }
        catch
        {
            _disposalFailed = true;
            throw;
        }
        finally
        {
            _operationActive = false;
        }
    }

    public void AbandonAfterTerminalFailure()
    {
        if (_disposed || _abandoned)
            return;
        if (_operationActive)
            throw new InvalidOperationException("The retained UI lease is changing state.");
        if (!_disposalFailed)
            throw new InvalidOperationException(
                "Retained UI resources may be abandoned only after disposal failed.");

        _abandoned = true;
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_abandoned)
            throw new InvalidOperationException("The retained UI lease was abandoned.");
    }
}
