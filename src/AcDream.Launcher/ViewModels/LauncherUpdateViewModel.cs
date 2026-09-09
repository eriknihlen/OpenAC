using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.ViewModels;

public sealed class LauncherUpdateViewModel : ObservableObject, IDisposable
{
    private readonly ILauncherUpdater _updater;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action _onClientChanged;
    private readonly Func<bool> _canOpen;
    private readonly Func<bool> _canMutate;
    private readonly Func<CancellationToken, Task<bool>>? _applyLauncherUpdateAsync;
    private readonly Action? _requestShutdown;
    private CancellationTokenSource? _cancellation;
    private LauncherUpdateCheckResult? _check;
    private bool _isOpen;
    private bool _isBusy;
    private bool _disposed;
    private string _status = string.Empty;
    private string? _error;
    private double _progressPercent;
    private bool _isProgressIndeterminate;

    public event EventHandler? StartupCheckCompleted;

    public LauncherUpdateViewModel(
        ILauncherUpdater updater,
        IUiDispatcher dispatcher,
        Action onClientChanged,
        Func<bool>? canOpen = null,
        Func<bool>? canMutate = null,
        Func<CancellationToken, Task<bool>>? applyLauncherUpdateAsync = null,
        Action? requestShutdown = null)
    {
        _updater = updater ?? throw new ArgumentNullException(nameof(updater));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _onClientChanged = onClientChanged
            ?? throw new ArgumentNullException(nameof(onClientChanged));
        _canOpen = canOpen ?? (() => true);
        _canMutate = canMutate ?? (() => true);
        _applyLauncherUpdateAsync = applyLauncherUpdateAsync;
        _requestShutdown = requestShutdown;

        UpdateCommand = new AsyncRelayCommand(
            UpdateAsync,
            () => IsOpen && !IsBusy && _canMutate() && HasSomethingToUpdate);
        NotNowCommand = new RelayCommand(Close, () => !IsBusy);
        CancelCommand = new RelayCommand(
            () => _cancellation?.Cancel(),
            () => IsBusy && _cancellation is not null);
    }

    public string Title => "Update available";

    /// <summary>What is out of date, in one sentence, in a player's words.</summary>
    public string Body
    {
        get
        {
            if (_check is null)
            {
                return string.Empty;
            }

            string version = _check.Manifest.Version.Value;
            if (_check.IsLauncherUpdateAvailable && _check.IsClientUpdateAvailable)
            {
                return $"A new version is available ({version}). The launcher "
                    + "updates first and restarts itself, then the game updates.";
            }

            return _check.IsLauncherUpdateAvailable
                ? $"A new launcher is available ({version}). "
                    + "It will restart itself once installed."
                : $"A new version of the game is available ({version}).";
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (SetProperty(ref _isOpen, value))
            {
                OnPropertyChanged(nameof(Body));
                NotifyCommandStates();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanClose));
                NotifyCommandStates();
            }
        }
    }

    public bool CanClose => !IsBusy;

    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public bool IsClientUpdateAvailable => _check?.IsClientUpdateAvailable == true;

    public bool IsLauncherUpdateAvailable => _check?.IsLauncherUpdateAvailable == true;

    public bool IsStartupCheckComplete { get; private set; }

    public bool StartupCheckSucceeded { get; private set; }

    public AsyncRelayCommand UpdateCommand { get; }

    public RelayCommand NotNowCommand { get; }

    public RelayCommand CancelCommand { get; }

    public bool AutoOpenDiscoveredUpdates { get; set; } = true;

    public void OpenAvailableUpdate()
    {
        if (!_disposed && HasSomethingToUpdate && _canOpen()) IsOpen = true;
    }

    public async Task StartupCheckAsync()
    {
        if (IsBusy || _disposed)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsStartupCheckComplete = false;
        StartupCheckSucceeded = false;
        Error = null;
        Status = "Checking for updates…";
        IsBusy = true;
        try
        {
            _ = await _updater.InitializeAsync(cancellation.Token)
                .ConfigureAwait(true);
            _check = await _updater.CheckAsync(cancellation.Token).ConfigureAwait(true);
            StartupCheckSucceeded = true;
            Status = HasSomethingToUpdate ? "An update is available." : "OpenAC is up to date.";
            OnPropertyChanged(nameof(Body));
            OnPropertyChanged(nameof(IsClientUpdateAvailable));
            OnPropertyChanged(nameof(IsLauncherUpdateAvailable));
            if (HasSomethingToUpdate)
            {
                TryOpenPendingUpdate();
            }
        }
        catch
        {
            _check = null;
            Status = "Updates could not be checked. Try again from Installation & updates.";
            OnPropertyChanged(nameof(IsClientUpdateAvailable));
            OnPropertyChanged(nameof(IsLauncherUpdateAvailable));
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
            }

            IsBusy = false;
            IsStartupCheckComplete = true;
            OnPropertyChanged(nameof(IsStartupCheckComplete));
            OnPropertyChanged(nameof(StartupCheckSucceeded));
            StartupCheckCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Close()
    {
        if (!IsBusy)
        {
            IsOpen = false;
        }
    }

    /// <summary>Opens a previously discovered update once another startup
    /// question (notably required world-data work) has finished.</summary>
    public void TryOpenPendingUpdate()
    {
        if (AutoOpenDiscoveredUpdates && !_disposed && HasSomethingToUpdate && _canOpen())
        {
            IsOpen = true;
        }
    }

    public void NotifyCommandStates()
    {
        UpdateCommand.NotifyCanExecuteChanged();
        NotNowCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private bool HasSomethingToUpdate =>
        _check is { IsClientUpdateAvailable: true } or { IsLauncherUpdateAvailable: true };

    private async Task UpdateAsync()
    {
        if (IsBusy || _disposed || _check is null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        Error = null;
        ProgressPercent = 0;
        IsProgressIndeterminate = true;
        try
        {
            var progress = new UiProgress<LauncherUpdateProgress>(
                _dispatcher,
                ApplyProgress);

            if (_check.IsLauncherUpdateAvailable)
            {
                Status = "Downloading the new launcher…";
                _ = await _updater
                    .StageLauncherAsync(_check, progress, cancellation.Token)
                    .ConfigureAwait(true);

                Status = "Restarting the launcher…";
                bool restarting = _applyLauncherUpdateAsync is not null
                    && await _applyLauncherUpdateAsync(cancellation.Token)
                        .ConfigureAwait(true);
                if (restarting)
                {
                    _requestShutdown?.Invoke();
                    return;
                }

                Status = "The launcher update is ready. Close and reopen the "
                    + "launcher to finish it.";
                IsProgressIndeterminate = false;
                return;
            }

            Status = "Downloading the game update…";
            _ = await _updater
                .InstallClientAsync(_check, progress, cancellation.Token)
                .ConfigureAwait(true);
            _onClientChanged();
            Status = "Update installed.";
            _check = null;
            OnPropertyChanged(nameof(IsClientUpdateAvailable));
            OnPropertyChanged(nameof(IsLauncherUpdateAvailable));
            IsProgressIndeterminate = false;
            IsOpen = false;
        }
        catch (OperationCanceledException)
        {
            Status = "Update cancelled.";
            IsProgressIndeterminate = false;
        }
        catch (Exception ex)
        {
            Error = SafeDisplayError(ex);
            Status = "The update could not be installed.";
            IsProgressIndeterminate = false;
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
            }

            IsBusy = false;
        }
    }

    private void ApplyProgress(LauncherUpdateProgress progress)
    {
        Status = progress.Status;
        if (progress.Total > 0)
        {
            IsProgressIndeterminate = false;
            ProgressPercent = progress.Percent;
        }
        else
        {
            IsProgressIndeterminate = true;
        }
    }

    private static string SafeDisplayError(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message)
            ? "The update operation failed."
            : exception.Message;

    private sealed class UiProgress<T>(IUiDispatcher dispatcher, Action<T> callback)
        : IProgress<T>
    {
        public void Report(T value) => dispatcher.Post(() => callback(value));
    }
}

internal sealed class UnavailableLauncherUpdater : ILauncherUpdater
{
    private readonly ClientVersionResolution _resolution;
    private readonly string _status;

    public UnavailableLauncherUpdater(
        string status = "Versioned client updater is unavailable.",
        ClientVersionResolution? resolution = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        _status = status;
        _resolution = resolution ?? new ClientVersionResolution(
            ClientVersionState.Invalid,
            status,
            null,
            null,
            null,
            null);
    }

    public ClientVersionResolution CurrentClient => _resolution;

    public Task<ClientVersionResolution> InitializeAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_resolution);

    public Task<LauncherUpdateCheckResult> CheckAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromException<LauncherUpdateCheckResult>(
            new LauncherUpdateException(_status));

    public Task<ClientVersionResolution> InstallClientAsync(
        LauncherUpdateCheckResult check,
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ClientVersionResolution>(
            new LauncherUpdateException(_status));

    public Task<SelfUpdateStageResult> StageLauncherAsync(
        LauncherUpdateCheckResult check,
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<SelfUpdateStageResult>(
            new LauncherUpdateException(_status));

    public Task<ClientVersionResolution> RollbackClientAsync(
        IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ClientVersionResolution>(
            new LauncherUpdateException(_status));
}
