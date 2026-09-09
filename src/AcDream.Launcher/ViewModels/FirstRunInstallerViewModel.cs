using System.Globalization;
using System.ComponentModel;
using AcDream.Launcher.Core.Installation;
using AcDream.Launcher.Core.Launching;

namespace AcDream.Launcher.ViewModels;

public sealed class FirstRunInstallerViewModel : ObservableObject, IDisposable
{
    private readonly ILauncherInstaller _installer;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<LauncherInstallRecord> _onInstalled;
    private readonly Func<bool> _canOpen;
    private readonly Func<bool> _canStart;
    private CancellationTokenSource? _cancellation;
    private string _datDirectory = string.Empty;
    private string _threadsText = Math.Max(1, Environment.ProcessorCount).ToString();
    private string _status = "Choose the folder containing the retail DAT files.";
    private string _validationStatus = "No DAT directory selected.";
    private string _missingFiles = string.Empty;
    private string? _error;
    private LauncherInstallPhase _phase = LauncherInstallPhase.Idle;
    private double _progressPercent;
    private bool _isCompleted;
    private bool _isOpen;
    private bool _isRunning;
    private bool _isDatDirectoryValid;
    private LauncherInstallRecord? _contentUpdateBase;
    private ContentMigrationPlan? _contentMigration;
    private string? _completionRequirement;
    private bool _disposed;

    public FirstRunInstallerViewModel(
        ILauncherInstaller installer,
        IUiDispatcher dispatcher,
        Action<LauncherInstallRecord> onInstalled,
        Func<bool>? canOpen = null,
        Func<bool>? canStart = null)
    {
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _onInstalled = onInstalled ?? throw new ArgumentNullException(nameof(onInstalled));
        _canOpen = canOpen ?? (() => true);
        _canStart = canStart ?? (() => true);

        OpenCommand = new RelayCommand(Open, () => !_disposed && _canOpen());
        AcknowledgeCompletionCommand = new RelayCommand(
            AcknowledgeCompletion,
            () => IsCompleted && !IsRunning);
        CloseCommand = new RelayCommand(Close, () => !IsRunning);
        ValidateCommand = new RelayCommand(Validate, () => !IsRunning);
        StartCommand = new AsyncRelayCommand(StartAsync, CanBeginInstall);
        CancelCommand = new RelayCommand(
            () => _cancellation?.Cancel(),
            () => IsRunning && _cancellation is not null);
    }

    public bool IsContentUpdate => _contentMigration is not null;

    public string Title => IsContentUpdate
        ? "World data update required"
        : "First-run setup";

    public string Body => IsContentUpdate
        ? BuildContentUpdateBody()
        : "Choose your Asheron's Call data folder. OpenAC will check the required "
            + "files and prepare the game content. First setup can take a while. "
            + "Choose Build and install when you are ready.";

    public string StartActionText => IsContentUpdate
        ? _contentMigration?.Kind == ContentWorkKind.Overlay
            ? "Build small update"
            : "Rebuild world data"
        : "Build and install";

    public string DatDirectory
    {
        get => _datDirectory;
        set
        {
            if (SetProperty(ref _datDirectory, value ?? string.Empty))
            {
                Validate();
            }
        }
    }

    public string ThreadsText
    {
        get => _threadsText;
        set
        {
            if (SetProperty(ref _threadsText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsThreadCountValid));
                OnPropertyChanged(nameof(ThreadCountValidation));
                NotifyCommandStates();
            }
        }
    }

    public bool IsThreadCountValid =>
        int.TryParse(
            ThreadsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int threads)
        && threads > 0;

    public string ThreadCountValidation => IsThreadCountValid
        ? "Worker count is valid."
        : "Threads must be a positive whole number.";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string ValidationStatus
    {
        get => _validationStatus;
        private set => SetProperty(ref _validationStatus, value);
    }

    public string MissingFiles
    {
        get => _missingFiles;
        private set
        {
            if (SetProperty(ref _missingFiles, value))
            {
                OnPropertyChanged(nameof(HasMissingFiles));
            }
        }
    }

    public bool HasMissingFiles => MissingFiles.Length > 0;

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

    public LauncherInstallPhase Phase
    {
        get => _phase;
        private set => SetProperty(ref _phase, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public bool IsProgressIndeterminate =>
        IsRunning && ProgressPercent <= 0;

    public bool CanEditInputs => !IsRunning;

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsProgressIndeterminate));
                OnPropertyChanged(nameof(CanEditInputs));
                NotifyCommandStates();
            }
        }
    }

    public bool IsDatDirectoryValid
    {
        get => _isDatDirectoryValid;
        private set
        {
            if (SetProperty(ref _isDatDirectoryValid, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public RelayCommand OpenCommand { get; }

    public RelayCommand AcknowledgeCompletionCommand { get; }

    public RelayCommand CloseCommand { get; }

    public RelayCommand ValidateCommand { get; }

    public AsyncRelayCommand StartCommand { get; }

    public RelayCommand CancelCommand { get; }

    public void SelectDatDirectory(string directory) => DatDirectory = directory;

    public void ReportPickerError(string message)
    {
        Error = string.IsNullOrWhiteSpace(message)
            ? "The DAT directory picker failed."
            : message;
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        private set
        {
            if (SetProperty(ref _isCompleted, value))
            {
                OnPropertyChanged(nameof(ShowSetupForm));
                NotifyCommandStates();
            }
        }
    }

    public bool ShowSetupForm => !IsCompleted;

    public string CompletedTitle => IsContentUpdate
        ? "World data updated"
        : "Setup complete";

    public string CompletedBody => IsContentUpdate
        ? _completionRequirement
            ?? "acdream built and verified the required world data. You can play now."
        : "acdream built and verified your game content. You can play now.";

    public void SetCompletionRequirement(string? requirement)
    {
        _completionRequirement = string.IsNullOrWhiteSpace(requirement)
            ? null
            : requirement;
        OnPropertyChanged(nameof(CompletedBody));
    }

    public void PrepareContentUpdate(
        LauncherInstallRecord baseRecord,
        ContentMigrationPlan migration)
    {
        ArgumentNullException.ThrowIfNull(baseRecord);
        ArgumentNullException.ThrowIfNull(migration);
        _contentUpdateBase = baseRecord;
        _contentMigration = migration;
        SetCompletionRequirement(null);
        _datDirectory = baseRecord.DatDirectory;
        Status = "Review the required work. Nothing has started.";
        OnPropertyChanged(nameof(DatDirectory));
        OnPropertyChanged(nameof(IsContentUpdate));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(StartActionText));
        OnPropertyChanged(nameof(CompletedTitle));
        OnPropertyChanged(nameof(CompletedBody));
        Open();
    }

    public void NotifyCommandStates()
    {
        OpenCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
        ValidateCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        AcknowledgeCompletionCommand.NotifyCanExecuteChanged();
    }

    public void Close()
    {
        if (!IsRunning)
        {
            IsOpen = false;
        }
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
        NotifyCommandStates();
    }

    private void AcknowledgeCompletion()
    {
        IsCompleted = false;
        IsOpen = false;
        ClearContentUpdateMode();
    }

    private void Open()
    {
        if (string.IsNullOrWhiteSpace(DatDirectory))
        {
            IReadOnlyList<DatDirectoryValidation> candidates =
                _installer.DetectDatDirectories();
            DatDirectoryValidation? preferred =
                candidates.FirstOrDefault(candidate => candidate.IsValid)
                ?? candidates.FirstOrDefault();
            if (preferred is not null)
            {
                _datDirectory = preferred.Directory;
                OnPropertyChanged(nameof(DatDirectory));
            }
        }

        Validate();
        IsOpen = true;
    }

    private void Validate()
    {
        if (IsRunning)
        {
            return;
        }

        DatDirectoryValidation validation =
            _installer.ValidateDatDirectory(DatDirectory);
        IsDatDirectoryValid = validation.IsValid;
        ValidationStatus = validation.Message;
        MissingFiles = validation.MissingFileNames.Count == 0
            ? string.Empty
            : "Missing: " + string.Join(", ", validation.MissingFileNames);
        Error = null;
        NotifyCommandStates();
    }

    private bool CanBeginInstall() =>
        !_disposed
        && IsOpen
        && !IsRunning
        && IsDatDirectoryValid
        && IsThreadCountValid
        && _canStart();

    private async Task StartAsync()
    {
        if (!int.TryParse(
                ThreadsText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int threads)
            || threads <= 0)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsRunning = true;
        Error = null;
        ProgressPercent = 0;
        Phase = LauncherInstallPhase.ValidatingDatFiles;
        Status = IsContentUpdate
            ? "Starting the approved world-data update..."
            : "Starting installation...";

        var progress = new CallbackProgress<LauncherInstallProgress>(value =>
            _dispatcher.Post(() => ApplyProgress(value)));
        try
        {
            LauncherInstallResult result = await (IsContentUpdate
                    ? _installer.ApplyContentUpdateAsync(
                        DatDirectory,
                        threads,
                        _contentMigration!,
                        progress,
                        cancellation.Token)
                    : _installer.InstallAsync(
                        DatDirectory,
                        threads,
                        progress,
                        cancellation.Token))
                .ConfigureAwait(true);
            _onInstalled(result.Record);
            Phase = LauncherInstallPhase.Completed;
            ProgressPercent = 100;
            Status = _completionRequirement is null
                ? "Client content installed and verified. Launch is enabled."
                : "World data is ready. The matching game update is still required.";
            IsCompleted = true;
        }
        catch (OperationCanceledException)
        {
            Phase = LauncherInstallPhase.Cancelled;
            Status = "Installation cancelled. The previous verified install was preserved.";
        }
        catch (Exception ex)
        {
            Phase = LauncherInstallPhase.Failed;
            Error = string.IsNullOrWhiteSpace(ex.Message)
                ? "Installation failed."
                : ex.Message;
            Status = "Installation failed; no new install record was published.";
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
            }

            IsRunning = false;
        }
    }

    private string BuildContentUpdateBody()
    {
        ContentMigrationPlan migration = _contentMigration!;
        string work = migration.Kind == ContentWorkKind.Overlay
            ? "a small filtered overlay"
            : "a complete replacement pak";
        string estimate = migration.Kind == ContentWorkKind.Overlay
            ? $"Affected filters: {migration.EffectiveDatIds.Count:N0} DAT id(s), "
                + $"{migration.EffectiveLandblocks.Count:N0} landblock(s)."
            : $"Free-space guidance: allow at least "
                + $"{LauncherInstaller.FullRebuildRequiredFreeBytes / (1024d * 1024d * 1024d):N0} GiB "
                + "while the optimized package is built beside the active one.";
        return $"This client needs recipe {migration.TargetRecipeVersion}: "
            + $"{migration.Reason}. acdream will build {work} from your installed "
            + "Asheron's Call DAT files. The existing package stays in place "
            + $"until the new one has finished and verified. {estimate} "
            + "No work begins until you confirm below.";
    }

    private void ClearContentUpdateMode()
    {
        if (_contentMigration is null)
        {
            return;
        }

        _contentMigration = null;
        _contentUpdateBase = null;
        SetCompletionRequirement(null);
        OnPropertyChanged(nameof(IsContentUpdate));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(StartActionText));
        OnPropertyChanged(nameof(CompletedTitle));
        OnPropertyChanged(nameof(CompletedBody));
    }

    private void ApplyProgress(LauncherInstallProgress progress)
    {
        if (_disposed)
        {
            return;
        }

        Phase = progress.Phase;
        Status = progress.Status;
        ProgressPercent = progress.Total > 0
            ? progress.Fraction * 100
            : 0;
        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        private readonly Action<T> _callback = callback
            ?? throw new ArgumentNullException(nameof(callback));

        public void Report(T value) => _callback(value);
    }
}

internal sealed class UnavailableLauncherInstaller : ILauncherInstaller
{
    public IReadOnlyList<DatDirectoryValidation> DetectDatDirectories() => [];

    public DatDirectoryValidation ValidateDatDirectory(string? directory) =>
        new(
            directory ?? string.Empty,
            false,
            "The installer service is unavailable in this host.",
            DatDirectoryLocator.RequiredFileNames);

    public Task<InstallRecordVerification> LoadExistingAsync(
        CancellationToken cancellationToken = default,
        bool forceFullVerification = false) =>
        Task.FromResult(new InstallRecordVerification(
            InstallRecordVerificationState.Missing,
            null,
            "The installer service is unavailable in this host."));

    public Task<LauncherInstallResult> InstallAsync(
        string datDirectory,
        int threads,
        IProgress<LauncherInstallProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<LauncherInstallResult>(
            new LauncherInstallException(
                "The installer service is unavailable in this host."));
}
