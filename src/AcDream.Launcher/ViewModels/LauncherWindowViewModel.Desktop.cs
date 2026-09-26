using System.ComponentModel;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Status;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.ViewModels;

public sealed partial class LauncherWindowViewModel
{
    private IServerHealthService? _serverHealth;
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(10);
    private DateTimeOffset _nextHealthCheck;
    private bool _isCheckingServerHealth;
    private readonly CancellationTokenSource _healthCancellation = new();
    private bool _isCharacterOptionsOpen;
    private bool _isSessionLogOpen;
    private bool _isSettingsOpen;
    private string? _profileMigrationNotice;

    public ProfileTextEditorViewModel TextEditor { get; private set; } = null!;
    public RelayCommand EditAccountsTextCommand { get; private set; } = null!;
    public RelayCommand EditServersTextCommand { get; private set; } = null!;
    public RelayCommand EditLogonCommandsTextCommand { get; private set; } = null!;
    public RelayCommand ReviewUpdateCommand { get; private set; } = null!;
    public AsyncRelayCommand CheckForUpdatesCommand { get; private set; } = null!;
    public AsyncRelayCommand CheckServersCommand { get; private set; } = null!;
    public RelayCommand OpenSessionLogCommand { get; private set; } = null!;
    public RelayCommand OpenSettingsCommand { get; private set; } = null!;
    public RelayCommand CloseDesktopDialogCommand { get; private set; } = null!;
    public RelayCommand SaveRowOptionsCommand { get; private set; } = null!;
    public bool ShowUpdateBanner => UpdatePrompt.IsClientUpdateAvailable || UpdatePrompt.IsLauncherUpdateAvailable;
    public bool HasActiveSessions => Sessions.Any(session => session.IsActive);
    public bool IsCharacterOptionsOpen
    {
        get => _isCharacterOptionsOpen;
        private set { if (SetProperty(ref _isCharacterOptionsOpen, value)) NotifyDesktopModal(); }
    }
    public bool IsSessionLogOpen
    {
        get => _isSessionLogOpen;
        private set { if (SetProperty(ref _isSessionLogOpen, value)) NotifyDesktopModal(); }
    }
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        private set { if (SetProperty(ref _isSettingsOpen, value)) NotifyDesktopModal(); }
    }

    /// <summary>What loading an older profile file could not carry over, shown once; null otherwise.</summary>
    public string? ProfileMigrationNotice
    {
        get => _profileMigrationNotice;
        private set
        {
            if (SetProperty(ref _profileMigrationNotice, value))
            {
                OnPropertyChanged(nameof(HasProfileMigrationNotice));
                NotifyDesktopModal();
            }
        }
    }

    public bool HasProfileMigrationNotice => ProfileMigrationNotice is not null;

    public RelayCommand DismissMigrationNoticeCommand { get; private set; } = null!;

    private void InitializeDesktop()
    {
        TextEditor = new ProfileTextEditorViewModel(_orchestrator);
        TextEditor.PropertyChanged += OnModalPropertyChanged;
        UpdatePrompt.PropertyChanged += OnDesktopUpdateChanged;
        EditAccountsTextCommand = new RelayCommand(() => OpenTextEditor(LauncherTextEditorKind.Accounts), () => CanInteract);
        EditServersTextCommand = new RelayCommand(() => OpenTextEditor(LauncherTextEditorKind.Servers), () => CanInteract);
        EditLogonCommandsTextCommand = new RelayCommand(() => OpenTextEditor(LauncherTextEditorKind.LogonCommands), () => CanInteract);
        ReviewUpdateCommand = new RelayCommand(UpdatePrompt.OpenAvailableUpdate, () => CanInteract && ShowUpdateBanner);
        CheckForUpdatesCommand = new AsyncRelayCommand(async () =>
        {
            await UpdatePrompt.StartupCheckAsync();
            if (ShowUpdateBanner) UpdatePrompt.OpenAvailableUpdate();
        }, () => CanInteract && !UpdatePrompt.IsBusy);
        CheckServersCommand = new AsyncRelayCommand(CheckServerHealthAsync, () => _serverHealth is not null && !_disposed);
        OpenSessionLogCommand = new RelayCommand(() => IsSessionLogOpen = true, () => CanInteract);
        OpenSettingsCommand = new RelayCommand(() => IsSettingsOpen = true, () => CanInteract);
        CloseDesktopDialogCommand = new RelayCommand(CloseDesktopDialogs);
        DismissMigrationNoticeCommand = new RelayCommand(() => ProfileMigrationNotice = null);
        SaveRowOptionsCommand = new RelayCommand(() =>
        {
            if (_rowOptionsAccount is { } account) SaveAccountPluginChoices(account);
            else SaveCharacterSettings();
            if (!HasError) IsCharacterOptionsOpen = false;
        }, () => IsCharacterOptionsOpen && !IsBusy);
    }

    private void OpenTextEditor(LauncherTextEditorKind kind)
    {
        try { TextEditor.Open(kind); }
        catch (Exception ex) { LastError = SafeDisplayError(ex, secret: null); }
    }

    private string? _launcherVersion;
    private Func<ClientVersionResolution?> _clientVersion = () => null;

    /// <summary>The small versions shown at the top right of the window, launcher above client, without
    /// build metadata. Plugin compatibility is judged against the client, which can differ from the
    /// launcher while an update is pending.</summary>
    public string VersionText => _launcherVersion is null
        ? string.Empty
        : $"launcher {ShortVersion(_launcherVersion)}\n"
          + (_clientVersion()?.Version is { } client ? $"client {ShortVersion(client.Value)}" : "client not installed");

    public void ConfigureVersions(string launcherVersion, Func<ClientVersionResolution?> clientVersion)
    {
        _launcherVersion = launcherVersion;
        _clientVersion = clientVersion ?? throw new ArgumentNullException(nameof(clientVersion));
        OnPropertyChanged(nameof(VersionText));
    }

    private static string ShortVersion(string version)
    {
        int plus = version.IndexOf('+');
        return "v" + (plus < 0 ? version : version[..plus]);
    }

    private InstallFolderViewModel? _installFolder;

    /// <summary>The install folder rows in the settings and the first-run form; null until configured.</summary>
    public InstallFolderViewModel? InstallFolder => _installFolder;

    /// <summary>Whether the install folder rows are available.</summary>
    public bool HasInstallFolder => _installFolder is not null;

    /// <summary>
    /// Whether the first-run form tells the player this is a new installation:
    /// an earlier version's folders exist, and the form is setting the game up
    /// rather than updating content an install already has.
    /// </summary>
    public bool ShowNewInstallationNotice =>
        _installFolder?.HasEarlierFolders == true && !FirstRunWizardShell.IsContentUpdate;

    /// <summary>Gives the settings their install folder section.</summary>
    public void ConfigureInstallFolder(InstallFolderViewModel installFolder)
    {
        _installFolder = installFolder ?? throw new ArgumentNullException(nameof(installFolder));
        OnPropertyChanged(nameof(InstallFolder));
        OnPropertyChanged(nameof(HasInstallFolder));
        OnPropertyChanged(nameof(ShowNewInstallationNotice));
    }

    /// <summary>Whether the install may move now: nothing running and nothing busy.</summary>
    internal bool CanMoveInstallFolder => !IsBusy && Sessions.All(session => !session.IsActive);

    public void ConfigureServerHealth(IServerHealthService service)
    {
        _serverHealth = service;
        CheckServersCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Checks servers on <see cref="HealthCheckInterval"/> while the window is active. An
    /// inactive window sends nothing, and one that comes back past the interval checks on the next
    /// poll rather than showing a stale ping.</summary>
    public void PollServerHealth(bool windowIsActive) => PollServerHealth(windowIsActive, DateTimeOffset.UtcNow);

    internal void PollServerHealth(bool windowIsActive, DateTimeOffset now)
    {
        if (!windowIsActive || _disposed || _serverHealth is null || _isCheckingServerHealth || now < _nextHealthCheck) return;
        _nextHealthCheck = now + HealthCheckInterval;
        // Not through CheckServersCommand: a background refresh must not disable the button.
        _ = CheckServerHealthAsync();
    }

    private async Task CheckServerHealthAsync()
    {
        if (_serverHealth is null || _isCheckingServerHealth) return;
        _isCheckingServerHealth = true;
        CancellationToken token = _healthCancellation.Token;
        try
        {
            var servers = _orchestrator.GetSnapshot().Servers.ToArray();
            using var limit = new SemaphoreSlim(4);
            await Task.WhenAll(servers.Select(async server =>
            {
                await limit.WaitAsync(token);
                try
                {
                    ServerHealthSnapshot result = await _serverHealth.CheckAsync(server.Host, server.Port, server.Name, token);
                    _dispatcher.Post(() =>
                    {
                        if (_disposed) return;
                        string count = result.PlayerCount is { } value ? $"{value:N0} players" : "— players";
                        if (result.IsPlayerCountStale) count += " (stale)";
                        // The dot beside the server name already says whether it answered.
                        string status = result.IsReachable == true ? "" : "No response · ";
                        foreach (var row in AllAccountRows.Where(row => row.ServerName == server.Name && row.Endpoint == $"{server.Host}:{server.Port}"))
                        {
                            row.IsServerOnline = result.IsReachable;
                            row.ServerStatusText = $"{status}{count}";
                            row.LatencyMilliseconds = result.LatencyMilliseconds;
                        }
                    });
                }
                finally { limit.Release(); }
            }));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed) OperationStatus = "Server status could not be checked. You can still launch.";
        }
        finally
        {
            _isCheckingServerHealth = false;
            _nextHealthCheck = DateTimeOffset.UtcNow + HealthCheckInterval;
        }
    }

    private void NotifyDesktopModal()
    {
        OnPropertyChanged(nameof(IsModalOpen));
        NotifyCommandStates();
    }

    public void CloseDesktopDialogs()
    {
        IsCharacterOptionsOpen = false;
        IsSessionLogOpen = false;
        IsSettingsOpen = false;
    }

    private void OnDesktopUpdateChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ShowUpdateBanner));
        NotifyDesktopCommands();
    }

    private void NotifyDesktopCommands()
    {
        _installFolder?.NotifyCanMoveChanged();
        OnPropertyChanged(nameof(HasActiveSessions));
        EditAccountsTextCommand?.NotifyCanExecuteChanged();
        EditServersTextCommand?.NotifyCanExecuteChanged();
        EditLogonCommandsTextCommand?.NotifyCanExecuteChanged();
        ReviewUpdateCommand?.NotifyCanExecuteChanged();
        CheckForUpdatesCommand?.NotifyCanExecuteChanged();
        OpenSessionLogCommand?.NotifyCanExecuteChanged();
        OpenSettingsCommand?.NotifyCanExecuteChanged();
        SaveRowOptionsCommand?.NotifyCanExecuteChanged();
    }

    private void DisposeDesktop()
    {
        _healthCancellation.Cancel();
        _healthCancellation.Dispose();
        TextEditor.PropertyChanged -= OnModalPropertyChanged;
        TextEditor.Close();
        UpdatePrompt.PropertyChanged -= OnDesktopUpdateChanged;
    }
}
