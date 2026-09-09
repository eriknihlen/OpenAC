using System.ComponentModel;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Status;

namespace AcDream.Launcher.ViewModels;

public sealed partial class LauncherWindowViewModel
{
    private IServerHealthService? _serverHealth;
    private DateTimeOffset _nextHealthCheck;
    private readonly CancellationTokenSource _healthCancellation = new();
    private bool _isCharacterOptionsOpen;
    private bool _isSessionLogOpen;

    public ProfileTextEditorViewModel TextEditor { get; private set; } = null!;
    public RelayCommand EditUsersTextCommand { get; private set; } = null!;
    public RelayCommand EditServersTextCommand { get; private set; } = null!;
    public RelayCommand EditLogonCommandsTextCommand { get; private set; } = null!;
    public RelayCommand ReviewUpdateCommand { get; private set; } = null!;
    public AsyncRelayCommand CheckForUpdatesCommand { get; private set; } = null!;
    public AsyncRelayCommand CheckServersCommand { get; private set; } = null!;
    public RelayCommand OpenSessionLogCommand { get; private set; } = null!;
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

    private void InitializeDesktop()
    {
        TextEditor = new ProfileTextEditorViewModel(_orchestrator);
        TextEditor.PropertyChanged += OnModalPropertyChanged;
        UpdatePrompt.PropertyChanged += OnDesktopUpdateChanged;
        EditUsersTextCommand = new RelayCommand(() => OpenTextEditor(LauncherTextEditorKind.Users), () => CanInteract);
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
        CloseDesktopDialogCommand = new RelayCommand(CloseDesktopDialogs);
        SaveRowOptionsCommand = new RelayCommand(() =>
        {
            SaveCharacterSettings();
            if (!HasError) IsCharacterOptionsOpen = false;
        }, () => IsCharacterOptionsOpen && !IsBusy);
    }

    private void OpenTextEditor(LauncherTextEditorKind kind)
    {
        try { TextEditor.Open(kind); }
        catch (Exception ex) { LastError = SafeDisplayError(ex, secret: null); }
    }

    public void ConfigureServerHealth(IServerHealthService service) => _serverHealth = service;

    public void PollServerHealth()
    {
        if (_disposed || _serverHealth is null || DateTimeOffset.UtcNow < _nextHealthCheck) return;
        _nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(30);
        CheckServersCommand.Execute(null);
    }

    private async Task CheckServerHealthAsync()
    {
        if (_serverHealth is null) return;
        CancellationToken token = _healthCancellation.Token;
        var servers = _orchestrator.GetSnapshot().Servers.ToArray();
        using var limit = new SemaphoreSlim(4);
        try
        {
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
                        string status = result.IsReachable == true ? "Online" : "Offline · no response";
                        string latency = result.LatencyMilliseconds is { } ms ? $" · {ms:0} ms" : "";
                        foreach (var row in AllAccountRows.Where(row => row.ServerName == server.Name && row.Endpoint == $"{server.Host}:{server.Port}"))
                        {
                            row.IsServerOnline = result.IsReachable;
                            row.ServerStatusText = $"{status} · {count}{latency}";
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
        _nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(30);
    }

    private void OpenAccountRowOptions(LauncherAccountServerRowViewModel row)
    {
        if (row.CharacterName is null) { OpenTextEditor(LauncherTextEditorKind.Users); return; }
        SelectedNode = Servers.FirstOrDefault(server => server.ServerName == row.ServerName)?.Children
            .FirstOrDefault(account => account.AccountName == row.AccountName)?.Children
            .FirstOrDefault(character => character.CharacterName == row.CharacterName);
        if (SelectedNode is not null)
        {
            CharacterLaunchMode = row.Mode;
            IsCharacterOptionsOpen = true;
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
    }

    private void OnDesktopUpdateChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ShowUpdateBanner));
        NotifyDesktopCommands();
    }

    private void NotifyDesktopCommands()
    {
        OnPropertyChanged(nameof(HasActiveSessions));
        EditUsersTextCommand?.NotifyCanExecuteChanged();
        EditServersTextCommand?.NotifyCanExecuteChanged();
        EditLogonCommandsTextCommand?.NotifyCanExecuteChanged();
        ReviewUpdateCommand?.NotifyCanExecuteChanged();
        CheckForUpdatesCommand?.NotifyCanExecuteChanged();
        OpenSessionLogCommand?.NotifyCanExecuteChanged();
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
