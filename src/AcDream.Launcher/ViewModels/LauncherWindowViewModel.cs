using System.Collections.ObjectModel;
using System.ComponentModel;
using AcDream.Launcher.Core.Installation;
using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.ViewModels;

public sealed class LauncherWindowViewModel : ObservableObject, IDisposable
{
    private readonly ILauncherOrchestrator _orchestrator;
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(30);

    private readonly IUiDispatcher _dispatcher;
    private readonly ILauncherInstaller _installer;
    private LauncherStateSnapshot? _snapshot;
    private LauncherTreeNodeViewModel? _selectedNode;
    private CancellationTokenSource? _operationCancellation;
    private bool _isBusy;
    private bool _isInstallationChecking = true;
    private bool _isClientCompatibilityCheckBlocking;
    private bool _isContentUpdateRequired;
    private bool _disposed;
    private Task? _startupInitialization;
    private bool _openUpdateAfterContentCompletion;
    private bool _isClientCompatibilityPending;
    private LauncherInstallRecord? _pendingInstalledContent;
    private readonly CancellationTokenSource _startupCancellation = new();
    private string? _lastError;
    private string _operationStatus = "Ready";
    private LaunchMode _characterLaunchMode;
    private string _characterPluginsText = string.Empty;
    private string _characterLoginCommandsText = string.Empty;

    public LauncherWindowViewModel(
        ILauncherOrchestrator orchestrator,
        IUiDispatcher dispatcher,
        ILauncherInstaller? installer = null,
        ILauncherUpdater? updater = null,
        Func<CancellationToken, Task<bool>>? applyLauncherUpdateAsync = null,
        Action? requestShutdown = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _orchestrator.StateChanged += OnOrchestratorStateChanged;

        EditorDialog = new ProfileEditorDialogViewModel();
        _installer = installer ?? new UnavailableLauncherInstaller();
        FirstRunWizardShell = new FirstRunInstallerViewModel(
            _installer,
            dispatcher,
            OnInstallCompleted,
            () => CanInteract,
            () => !IsBusy && Sessions.All(session => !session.IsActive));
        UpdatePrompt = new LauncherUpdateViewModel(
            updater ?? new UnavailableLauncherUpdater(),
            dispatcher,
            OnClientVersionChanged,
            () => CanInteract,
            () => !IsBusy && Sessions.All(session => !session.IsActive),
            applyLauncherUpdateAsync,
            requestShutdown);

        EditorDialog.PropertyChanged += OnModalPropertyChanged;
        FirstRunWizardShell.PropertyChanged += OnModalPropertyChanged;
        UpdatePrompt.PropertyChanged += OnModalPropertyChanged;
        UpdatePrompt.StartupCheckCompleted += OnStartupUpdateCheckCompleted;

        AddServerCommand = new RelayCommand(OpenAddServerDialog, () => CanInteract);
        AddAccountCommand = new RelayCommand(OpenAddAccountDialog, CanAddAccount);
        AddCharacterCommand = new RelayCommand(OpenAddCharacterDialog, CanAddCharacter);
        EditSelectedCommand = new RelayCommand(OpenEditSelectedDialog, CanEditSelected);
        RemoveSelectedCommand = new RelayCommand(OpenRemoveSelectedDialog, CanEditSelected);
        SaveCharacterSettingsCommand = new RelayCommand(
            SaveCharacterSettings,
            () => IsCharacterSelected && CanInteract);
        LaunchGuiCommand = new AsyncRelayCommand(
            () => LaunchSelectedAsync(LaunchMode.Gui),
            () => CanLaunchGui);
        LaunchAccountGuiSelectCommand = new AsyncRelayCommand(
            LaunchSelectedAccountGuiSelectAsync,
            () => CanLaunchAccountGuiSelect);
        LaunchHeadlessCommand = new AsyncRelayCommand(
            () => LaunchSelectedAsync(LaunchMode.Headless),
            () => CanLaunchHeadless);
        CancelOperationCommand = new RelayCommand(
            CancelOperation,
            () => !IsModalOpen && IsBusy && _operationCancellation is not null);
        ClearFinishedSessionsCommand = new RelayCommand(
            _orchestrator.ClearFinishedSessions,
            () => Sessions.Any(session => !session.IsActive) && CanInteract);
        VerifyContentCommand = new AsyncRelayCommand(
            VerifyContentAsync,
            () => CanInteract && Sessions.All(session => !session.IsActive));
    }

    public ObservableCollection<LauncherTreeNodeViewModel> Servers { get; } = [];

    public ObservableCollection<LauncherSessionRowViewModel> Sessions { get; } = [];

    public IReadOnlyList<LaunchMode> AvailableLaunchModes { get; } =
        [LaunchMode.Gui, LaunchMode.GuiSelect, LaunchMode.Headless];

    public ProfileEditorDialogViewModel EditorDialog { get; }

    public FirstRunInstallerViewModel FirstRunWizardShell { get; }

    public LauncherUpdateViewModel UpdatePrompt { get; }

    public LauncherTreeNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set => SetSelectedNode(value, preserveCharacterDraft: false);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanLaunchGui));
                OnPropertyChanged(nameof(CanLaunchHeadless));
                OnPropertyChanged(nameof(CanLaunchAccountGuiSelect));
                OnPropertyChanged(nameof(ShowGuiLaunchDisabledReason));
                OnPropertyChanged(nameof(ShowHeadlessLaunchDisabledReason));
                NotifyCommandStates();
            }
        }
    }

    public string? LastError
    {
        get => _lastError;
        private set
        {
            if (SetProperty(ref _lastError, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(LastError);

    public bool IsModalOpen =>
        EditorDialog.IsOpen
        || FirstRunWizardShell.IsOpen
        || UpdatePrompt.IsOpen;

    private bool CanInteract => !IsBusy && !IsModalOpen;

    public string OperationStatus
    {
        get => _operationStatus;
        private set => SetProperty(ref _operationStatus, value);
    }

    public bool IsServerSelected => SelectedNode?.Kind == LauncherTreeNodeKind.Server;

    public bool IsAccountSelected => SelectedNode?.Kind == LauncherTreeNodeKind.Account;

    public bool IsCharacterSelected => SelectedNode?.Kind == LauncherTreeNodeKind.Character;

    public bool HasSelection => SelectedNode is not null;

    public string SelectionTitle => SelectedNode?.DisplayName ?? "Select a profile";

    public string SelectionSubtitle => SelectedNode?.SecondaryText
        ?? "Add a server to begin.";

    public string SelectedServerName => SelectedNode?.ServerName ?? string.Empty;

    public string SelectedAccountName => SelectedNode?.AccountName ?? string.Empty;

    public string SelectedCharacterName => SelectedNode?.CharacterName ?? string.Empty;

    public LaunchMode CharacterLaunchMode
    {
        get => _characterLaunchMode;
        set => SetProperty(ref _characterLaunchMode, value);
    }

    public string CharacterPluginsText
    {
        get => _characterPluginsText;
        set => SetProperty(ref _characterPluginsText, value);
    }

    public string CharacterLoginCommandsText
    {
        get => _characterLoginCommandsText;
        set => SetProperty(ref _characterLoginCommandsText, value);
    }

    public bool IsInstallationChecking => _isInstallationChecking;

    public bool IsFirstRunRequired =>
        !IsInstallationChecking
        && (_isContentUpdateRequired
            || _snapshot is { IsInstallationReady: false });

    public bool ShowInstallationBanner =>
        IsInstallationChecking || IsFirstRunRequired;

    public string InstallationBannerTitle => IsInstallationChecking
        ? "Checking installation"
        : _isClientCompatibilityPending
            ? "Game update required"
            : _isContentUpdateRequired
                ? "World data update required"
                : "Client setup required";

    public string InstallationStatus => _snapshot?.InstallationStatus
        ?? "Installation state is loading.";

    public bool ShowGraphicalLaunchNotice =>
        _snapshot?.Platform.CanLaunchGraphicalClient == false
        && !string.IsNullOrEmpty(_snapshot?.Platform.GraphicalLaunchDisabledReason);

    public string GraphicalLaunchNotice =>
        _snapshot?.Platform.GraphicalLaunchDisabledReason ?? string.Empty;

    public bool CanLaunchGui => CanLaunch(LaunchMode.Gui);

    public bool CanLaunchHeadless => CanLaunch(LaunchMode.Headless);

    public bool CanLaunchAccountGuiSelect =>
        CanInteract
        && !_isClientCompatibilityCheckBlocking
        && IsAccountSelected
        && TryGetSelectedAccount(out string server, out string account)
        && _orchestrator.GetAccountLaunchCapability(
            server,
            account,
            LaunchMode.GuiSelect).IsAvailable;

    public string AccountGuiSelectDisabledReason
    {
        get
        {
            if (!TryGetSelectedAccount(out string server, out string account))
            {
                return "Select an account.";
            }

            return _orchestrator.GetAccountLaunchCapability(
                    server,
                    account,
                    LaunchMode.GuiSelect).Reason
                ?? "Character-select launch is available.";
        }
    }

    public string GuiLaunchDisabledReason =>
        GetSelectedAccountLaunchCapability(LaunchMode.Gui).Reason
        ?? "Graphical launch is available.";

    public string HeadlessLaunchDisabledReason =>
        GetSelectedAccountLaunchCapability(LaunchMode.Headless).Reason
        ?? "Headless launch is available.";

    public bool ShowGuiLaunchDisabledReason =>
        IsCharacterSelected
        && (!GetSelectedAccountLaunchCapability(LaunchMode.Gui).IsAvailable
            || !GetSelectedAccountLaunchCapability(LaunchMode.GuiSelect).IsAvailable);

    public bool ShowHeadlessLaunchDisabledReason =>
        IsCharacterSelected
        && !GetSelectedAccountLaunchCapability(LaunchMode.Headless).IsAvailable;

    public RelayCommand AddServerCommand { get; }

    public RelayCommand AddAccountCommand { get; }

    public RelayCommand AddCharacterCommand { get; }

    public RelayCommand EditSelectedCommand { get; }

    public RelayCommand RemoveSelectedCommand { get; }

    public RelayCommand SaveCharacterSettingsCommand { get; }

    public AsyncRelayCommand LaunchGuiCommand { get; }

    public AsyncRelayCommand LaunchAccountGuiSelectCommand { get; }

    public AsyncRelayCommand LaunchHeadlessCommand { get; }

    public AsyncRelayCommand VerifyContentCommand { get; }

    public RelayCommand CancelOperationCommand { get; }

    public RelayCommand ClearFinishedSessionsCommand { get; }

    public void Initialize()
    {
        try
        {
            _orchestrator.LoadProfiles();
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = SafeDisplayError(ex, secret: null);
        }

        RefreshFromCore();
    }

    public Task StartBackgroundInitializationAsync()
    {
        if (_startupInitialization is not null)
        {
            return _startupInitialization;
        }

        _isClientCompatibilityCheckBlocking = true;
        OnPropertyChanged(nameof(CanLaunchGui));
        OnPropertyChanged(nameof(CanLaunchHeadless));
        OnPropertyChanged(nameof(CanLaunchAccountGuiSelect));
        NotifyCommandStates();
        _startupInitialization = InitializeInstalledContentAndUpdatesAsync(
            _startupCancellation.Token);
        return _startupInitialization;
    }

    public void PollStatus()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _orchestrator.PollStatus();
        }
        catch (Exception ex)
        {
            LastError = SafeDisplayError(ex, secret: null);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _startupCancellation.Cancel();
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        _orchestrator.StateChanged -= OnOrchestratorStateChanged;
        EditorDialog.PropertyChanged -= OnModalPropertyChanged;
        FirstRunWizardShell.PropertyChanged -= OnModalPropertyChanged;
        UpdatePrompt.PropertyChanged -= OnModalPropertyChanged;
        UpdatePrompt.StartupCheckCompleted -= OnStartupUpdateCheckCompleted;
        FirstRunWizardShell.Dispose();
        UpdatePrompt.Dispose();
        _startupCancellation.Dispose();
    }

    private async Task InitializeInstalledContentAndUpdatesAsync(
        CancellationToken cancellationToken)
    {
        OperationStatus = "Checking installed game content…";
        try
        {
            InstallRecordVerification verification = await _installer
                .LoadExistingWithProgressAsync(
                    cancellationToken,
                    progress: new Progress<string>(status =>
                        OperationStatus = status))
                .ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            _isContentUpdateRequired = verification.RequiresContentUpdate;
            if (verification.IsVerified
                && verification.Record is
                    { RequiresClientCompatibilityConfirmation: true } pendingRecord)
            {
                _pendingInstalledContent = pendingRecord;
                _isClientCompatibilityPending = true;
                _orchestrator.SetInstallationState(null, verification.Status);
            }
            else
            {
                _orchestrator.SetInstallationState(
                    verification.IsVerified || verification.RequiresContentUpdate
                        ? verification.Record
                        : null,
                    verification.Status);
            }

            OperationStatus = verification.Status;
            if (verification.RequiresContentUpdate
                && verification.Record is not null
                && verification.RequiredContentWork is not null)
            {
                FirstRunWizardShell.PrepareContentUpdate(
                    verification.Record,
                    verification.RequiredContentWork);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (_disposed)
            {
                return;
            }

            string status = "Client content verification failed: "
                + SafeDisplayError(ex, secret: null);
            _orchestrator.SetInstallationState(null, status);
            OperationStatus = status;
            LastError = status;
        }
        if (!_disposed && !cancellationToken.IsCancellationRequested)
        {
            OperationStatus = "Checking for game updates…";
            await UpdatePrompt.StartupCheckAsync().ConfigureAwait(true);
            if (!_disposed)
            {
                OperationStatus = DescribeStartupUpdateCheckOutcome();
            }
        }

        if (!_disposed)
        {
            _isClientCompatibilityCheckBlocking = false;
            _isInstallationChecking = false;
            OnPropertyChanged(nameof(IsInstallationChecking));
            OnPropertyChanged(nameof(IsFirstRunRequired));
            OnPropertyChanged(nameof(ShowInstallationBanner));
            OnPropertyChanged(nameof(InstallationBannerTitle));
            OnPropertyChanged(nameof(InstallationStatus));
            RefreshFromCore();
        }
    }

    private string DescribeStartupUpdateCheckOutcome()
    {
        if (!UpdatePrompt.StartupCheckSucceeded)
        {
            return "Update check unavailable; the launcher works offline.";
        }

        return UpdatePrompt.IsClientUpdateAvailable || UpdatePrompt.IsLauncherUpdateAvailable
            ? "Update available."
            : "Up to date.";
    }

    private void OnOrchestratorStateChanged(object? sender, EventArgs e) =>
        _dispatcher.Post(() =>
        {
            if (!_disposed)
            {
                RefreshFromCore();
            }
        });

    private void OnModalPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProfileEditorDialogViewModel.IsOpen)
            && e.PropertyName != nameof(LauncherShellViewModel.IsOpen)
            && e.PropertyName != nameof(FirstRunInstallerViewModel.IsOpen)
            && e.PropertyName != nameof(LauncherUpdateViewModel.IsOpen))
        {
            return;
        }

        OnPropertyChanged(nameof(IsModalOpen));
        OnPropertyChanged(nameof(CanLaunchGui));
        OnPropertyChanged(nameof(CanLaunchHeadless));
        OnPropertyChanged(nameof(CanLaunchAccountGuiSelect));
        OnPropertyChanged(nameof(ShowGuiLaunchDisabledReason));
        OnPropertyChanged(nameof(ShowHeadlessLaunchDisabledReason));
        NotifyCommandStates();
        if (ReferenceEquals(sender, FirstRunWizardShell)
            && e.PropertyName == nameof(FirstRunInstallerViewModel.IsOpen)
            && !FirstRunWizardShell.IsOpen
            && _openUpdateAfterContentCompletion)
        {
            _openUpdateAfterContentCompletion = false;
            UpdatePrompt.TryOpenPendingUpdate();
        }
    }

    public void CloseActiveModal()
    {
        if (EditorDialog.IsOpen)
        {
            EditorDialog.Close();
        }
        else if (FirstRunWizardShell.IsOpen)
        {
            FirstRunWizardShell.Close();
        }
        else if (UpdatePrompt.IsOpen)
        {
            UpdatePrompt.Close();
        }
    }

    private void RefreshFromCore(SelectionKey? preferredSelection = null)
    {
        SelectionKey? previousSelection = preferredSelection ?? SelectionKey.From(SelectedNode);
        LauncherStateSnapshot snapshot = _orchestrator.GetSnapshot();
        _snapshot = snapshot;

        Servers.Clear();
        foreach (LauncherServerSnapshot server in snapshot.Servers)
        {
            Servers.Add(LauncherTreeNodeViewModel.FromServer(server));
        }

        Sessions.Clear();
        foreach (LauncherSessionSnapshot session in snapshot.Sessions)
        {
            Sessions.Add(new LauncherSessionRowViewModel(
                session,
                StopSessionAsync,
                () => CanInteract));
        }

        LauncherTreeNodeViewModel? restored = previousSelection is null
            ? Servers.FirstOrDefault()
            : FindNode(previousSelection.Value) ?? Servers.FirstOrDefault();
        bool preserveDraft = previousSelection is not null
            && SelectionKey.From(restored) == previousSelection;
        SetSelectedNode(restored, preserveDraft);

        OnPropertyChanged(nameof(IsFirstRunRequired));
        OnPropertyChanged(nameof(ShowInstallationBanner));
        OnPropertyChanged(nameof(InstallationStatus));
        OnPropertyChanged(nameof(ShowGraphicalLaunchNotice));
        OnPropertyChanged(nameof(GraphicalLaunchNotice));
        OnPropertyChanged(nameof(CanLaunchGui));
        OnPropertyChanged(nameof(CanLaunchHeadless));
        OnPropertyChanged(nameof(CanLaunchAccountGuiSelect));
        OnPropertyChanged(nameof(AccountGuiSelectDisabledReason));
        OnPropertyChanged(nameof(GuiLaunchDisabledReason));
        OnPropertyChanged(nameof(HeadlessLaunchDisabledReason));
        OnPropertyChanged(nameof(ShowGuiLaunchDisabledReason));
        OnPropertyChanged(nameof(ShowHeadlessLaunchDisabledReason));
        NotifyCommandStates();
    }

    private void SetSelectedNode(
        LauncherTreeNodeViewModel? value,
        bool preserveCharacterDraft)
    {
        if (ReferenceEquals(_selectedNode, value))
        {
            return;
        }

        _selectedNode = value;
        OnPropertyChanged(nameof(SelectedNode));
        OnPropertyChanged(nameof(IsServerSelected));
        OnPropertyChanged(nameof(IsAccountSelected));
        OnPropertyChanged(nameof(IsCharacterSelected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionTitle));
        OnPropertyChanged(nameof(SelectionSubtitle));
        OnPropertyChanged(nameof(SelectedServerName));
        OnPropertyChanged(nameof(SelectedAccountName));
        OnPropertyChanged(nameof(SelectedCharacterName));

        if (!preserveCharacterDraft)
        {
            LoadCharacterDraft();
        }

        OnPropertyChanged(nameof(CanLaunchGui));
        OnPropertyChanged(nameof(CanLaunchHeadless));
        OnPropertyChanged(nameof(CanLaunchAccountGuiSelect));
        OnPropertyChanged(nameof(AccountGuiSelectDisabledReason));
        OnPropertyChanged(nameof(GuiLaunchDisabledReason));
        OnPropertyChanged(nameof(HeadlessLaunchDisabledReason));
        OnPropertyChanged(nameof(ShowGuiLaunchDisabledReason));
        OnPropertyChanged(nameof(ShowHeadlessLaunchDisabledReason));
        NotifyCommandStates();
    }

    private void LoadCharacterDraft()
    {
        LauncherCharacterSnapshot? character = GetSelectedCharacterSnapshot();
        CharacterLaunchMode = character?.LaunchMode ?? LaunchMode.GuiSelect;
        CharacterPluginsText = character is null
            ? string.Empty
            : string.Join(Environment.NewLine, character.Plugins);
        CharacterLoginCommandsText = character is null
            ? string.Empty
            : string.Join(Environment.NewLine, character.LoginCommands);
    }

    private void OpenAddServerDialog() =>
        EditorDialog.Open(
            ProfileEditorKind.AddServer,
            "Add server",
            dialog =>
            {
                if (!dialog.TryGetPort(out int port))
                {
                    throw new LauncherOperationException("Port must be between 1 and 65535.");
                }

                _orchestrator.AddServer(dialog.Name.Trim(), dialog.Host.Trim(), port);
                RefreshFromCore(new SelectionKey(
                    LauncherTreeNodeKind.Server,
                    dialog.Name.Trim(),
                    null,
                    null));
            });

    private bool CanAddAccount() => CanInteract && SelectedNode is not null;

    private void OpenAddAccountDialog()
    {
        string serverName = SelectedNode?.ServerName
            ?? throw new LauncherOperationException("Select a server first.");
        EditorDialog.Open(
            ProfileEditorKind.AddAccount,
            $"Add account to {serverName}",
            dialog =>
            {
                _orchestrator.AddAccount(
                    serverName,
                    dialog.Name.Trim(),
                    dialog.Password);
                RefreshFromCore(new SelectionKey(
                    LauncherTreeNodeKind.Account,
                    serverName,
                    dialog.Name.Trim(),
                    null));
            });
    }

    private bool CanAddCharacter() =>
        CanInteract && TryGetSelectedAccount(out _, out _);

    private void OpenAddCharacterDialog()
    {
        if (!TryGetSelectedAccount(out string serverName, out string accountName))
        {
            throw new LauncherOperationException("Select an account first.");
        }

        EditorDialog.Open(
            ProfileEditorKind.AddCharacter,
            $"Add cached character to {accountName}",
            dialog =>
            {
                string name = dialog.Name.Trim();
                _orchestrator.AddCharacter(
                    serverName,
                    accountName,
                    name,
                    NullIfWhiteSpace(dialog.CharacterId));
                RefreshFromCore(new SelectionKey(
                    LauncherTreeNodeKind.Character,
                    serverName,
                    accountName,
                    name));
            },
            message: "Normally Refresh Characters fills this list. Manual rows "
                + "let you configure a known character while the server is unavailable.");
    }

    private bool CanEditSelected() => CanInteract && SelectedNode is not null;

    private void OpenEditSelectedDialog()
    {
        LauncherTreeNodeViewModel node = SelectedNode
            ?? throw new LauncherOperationException("Select a profile first.");
        switch (node.Kind)
        {
            case LauncherTreeNodeKind.Server:
                OpenEditServerDialog(node);
                break;
            case LauncherTreeNodeKind.Account:
                OpenEditAccountDialog(node);
                break;
            case LauncherTreeNodeKind.Character:
                OpenEditCharacterDialog(node);
                break;
        }
    }

    private void OpenEditServerDialog(LauncherTreeNodeViewModel node)
    {
        LauncherServerSnapshot server = FindServerSnapshot(node.ServerName);
        EditorDialog.Open(
            ProfileEditorKind.EditServer,
            $"Edit {server.Name}",
            dialog =>
            {
                if (!dialog.TryGetPort(out int port))
                {
                    throw new LauncherOperationException("Port must be between 1 and 65535.");
                }

                string newName = dialog.Name.Trim();
                _orchestrator.EditServer(
                    server.Name,
                    newName,
                    dialog.Host.Trim(),
                    port);
                RefreshFromCore(new SelectionKey(
                    LauncherTreeNodeKind.Server,
                    newName,
                    null,
                    null));
            },
            server.Name,
            server.Host,
            server.Port);
    }

    private void OpenEditAccountDialog(LauncherTreeNodeViewModel node)
    {
        LauncherAccountSnapshot account = FindAccountSnapshot(
            node.ServerName,
            node.AccountName!);
        EditorDialog.Open(
            ProfileEditorKind.EditAccount,
            $"Edit {account.AccountName}",
            dialog =>
            {
                string newName = dialog.Name.Trim();
                _orchestrator.EditAccount(
                    account.ServerName,
                    account.AccountName,
                    newName,
                    string.IsNullOrEmpty(dialog.Password) ? null : dialog.Password);
                RefreshFromCore(new SelectionKey(
                    LauncherTreeNodeKind.Account,
                    account.ServerName,
                    newName,
                    null));
            },
            account.AccountName,
            message: "Leave Password blank to keep the stored credential unchanged.");
    }

    private void OpenEditCharacterDialog(LauncherTreeNodeViewModel node)
    {
        LauncherCharacterSnapshot character = FindCharacterSnapshot(
            node.ServerName,
            node.AccountName!,
            node.CharacterName!);
        EditorDialog.Open(
            ProfileEditorKind.EditCharacter,
            $"Edit {character.Name}",
            dialog =>
            {
                string newName = dialog.Name.Trim();
                _orchestrator.EditCharacterIdentity(
                    character.ServerName,
                    character.AccountName,
                    character.Name,
                    newName,
                    dialog.CharacterId.Trim());
                RefreshFromCore(new SelectionKey(
                    LauncherTreeNodeKind.Character,
                    character.ServerName,
                    character.AccountName,
                    newName));
            },
            character.Name,
            characterId: character.Id ?? string.Empty,
            message: "A later character refresh remains authoritative for name and id.");
    }

    private void OpenRemoveSelectedDialog()
    {
        LauncherTreeNodeViewModel node = SelectedNode
            ?? throw new LauncherOperationException("Select a profile first.");
        EditorDialog.Open(
            ProfileEditorKind.Remove,
            $"Remove {node.DisplayName}?",
            _ =>
            {
                switch (node.Kind)
                {
                    case LauncherTreeNodeKind.Server:
                        _orchestrator.RemoveServer(node.ServerName);
                        break;
                    case LauncherTreeNodeKind.Account:
                        _orchestrator.RemoveAccount(node.ServerName, node.AccountName!);
                        break;
                    case LauncherTreeNodeKind.Character:
                        _orchestrator.RemoveCharacter(
                            node.ServerName,
                            node.AccountName!,
                            node.CharacterName!);
                        break;
                }

                RefreshFromCore();
            },
            message: node.Kind switch
            {
                LauncherTreeNodeKind.Server =>
                    "This removes the server and every account/character profile beneath it.",
                LauncherTreeNodeKind.Account =>
                    "This removes the account, its plaintext credential, and cached characters.",
                _ => "This removes the cached character and its launch settings.",
            });
    }

    private void SaveCharacterSettings()
    {
        LauncherCharacterSnapshot character = GetSelectedCharacterSnapshot()
            ?? throw new LauncherOperationException("Select a character first.");
        try
        {
            _orchestrator.UpdateCharacterSettings(
                character.ServerName,
                character.AccountName,
                character.Name,
                CharacterLaunchMode,
                ParseLines(CharacterPluginsText, distinct: true),
                ParseLines(CharacterLoginCommandsText, distinct: false));
            LastError = null;
            OperationStatus = $"Saved launch settings for {character.Name}.";
            RefreshFromCore(new SelectionKey(
                LauncherTreeNodeKind.Character,
                character.ServerName,
                character.AccountName,
                character.Name));
        }
        catch (Exception ex)
        {
            LastError = SafeDisplayError(ex, secret: null);
        }
    }

    private Task LaunchSelectedAsync(LaunchMode mode)
    {
        LauncherCharacterSnapshot? character = GetSelectedCharacterSnapshot();
        if (character is null)
        {
            return Task.CompletedTask;
        }

        return RunOperationAsync(
            token => _orchestrator.LaunchAsync(
                character.ServerName,
                character.AccountName,
                character.Name,
                mode,
                token),
            $"Launching {character.Name} ({mode})…",
            $"{mode} session started for {character.Name}.");
    }

    private Task LaunchSelectedAccountGuiSelectAsync()
    {
        if (!TryGetSelectedAccount(out string serverName, out string accountName))
        {
            return Task.CompletedTask;
        }

        return RunOperationAsync(
            token => _orchestrator.LaunchAsync(
                serverName,
                accountName,
                null,
                LaunchMode.GuiSelect,
                token),
            $"Opening character select for {accountName}…",
            $"Character-select session started for {accountName}.");
    }

    private async Task RunOperationAsync(
        Func<CancellationToken, Task<LauncherSessionSnapshot>> operation,
        string activeStatus,
        string completedStatus)
    {
        if (IsBusy)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        IsBusy = true;
        LastError = null;
        OperationStatus = activeStatus;
        try
        {
            await operation(cancellation.Token).ConfigureAwait(true);
            OperationStatus = completedStatus;
        }
        catch (OperationCanceledException)
        {
            OperationStatus = "Operation cancelled.";
        }
        catch (Exception ex)
        {
            LastError = SafeDisplayError(ex, secret: null);
            OperationStatus = "Operation failed.";
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, cancellation))
            {
                _operationCancellation = null;
            }

            IsBusy = false;
            RefreshFromCore();
        }
    }

    private async Task StopSessionAsync(string sessionId)
    {
        if (IsBusy)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        IsBusy = true;
        LastError = null;
        OperationStatus = "Logging out…";
        try
        {
            await _orchestrator.StopSessionAsync(
                    sessionId,
                    GracefulStopTimeout,
                    cancellation.Token)
                .ConfigureAwait(true);
            OperationStatus = "Stop requested.";
        }
        catch (OperationCanceledException)
        {
            OperationStatus = "Stop cancelled.";
        }
        catch (Exception ex)
        {
            LastError = SafeDisplayError(ex, secret: null);
            OperationStatus = "Stop failed.";
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, cancellation))
            {
                _operationCancellation = null;
            }

            IsBusy = false;
            RefreshFromCore();
        }
    }

    private void CancelOperation() => _operationCancellation?.Cancel();

    private bool CanLaunch(LaunchMode mode) =>
        CanInteract
        && !_isClientCompatibilityCheckBlocking
        && IsCharacterSelected
        && TryGetSelectedAccount(out string server, out string account)
        && _orchestrator.GetAccountLaunchCapability(server, account, mode).IsAvailable;

    private LauncherCapability GetSelectedAccountLaunchCapability(LaunchMode mode) =>
        TryGetSelectedAccount(out string server, out string account)
            ? _orchestrator.GetAccountLaunchCapability(server, account, mode)
            : _orchestrator.GetLaunchCapability(mode);

    private bool TryGetSelectedAccount(out string serverName, out string accountName)
    {
        serverName = SelectedNode?.ServerName ?? string.Empty;
        accountName = SelectedNode?.AccountName ?? string.Empty;
        return !string.IsNullOrWhiteSpace(serverName)
            && !string.IsNullOrWhiteSpace(accountName);
    }

    private LauncherCharacterSnapshot? GetSelectedCharacterSnapshot()
    {
        if (_snapshot is null
            || SelectedNode is not { Kind: LauncherTreeNodeKind.Character } node)
        {
            return null;
        }

        return FindCharacterSnapshotOrDefault(
            node.ServerName,
            node.AccountName!,
            node.CharacterName!);
    }

    private LauncherServerSnapshot FindServerSnapshot(string serverName) =>
        _snapshot?.Servers.FirstOrDefault(server =>
            string.Equals(server.Name, serverName, StringComparison.Ordinal))
        ?? throw new LauncherOperationException($"No server named '{serverName}'.");

    private LauncherAccountSnapshot FindAccountSnapshot(
        string serverName,
        string accountName) =>
        FindServerSnapshot(serverName).Accounts.FirstOrDefault(account =>
            string.Equals(account.AccountName, accountName, StringComparison.Ordinal))
        ?? throw new LauncherOperationException(
            $"No account '{accountName}' on server '{serverName}'.");

    private LauncherCharacterSnapshot FindCharacterSnapshot(
        string serverName,
        string accountName,
        string characterName) =>
        FindCharacterSnapshotOrDefault(serverName, accountName, characterName)
        ?? throw new LauncherOperationException(
            $"No character '{characterName}' on account '{accountName}'.");

    private LauncherCharacterSnapshot? FindCharacterSnapshotOrDefault(
        string serverName,
        string accountName,
        string characterName) =>
        FindAccountSnapshot(serverName, accountName).Characters.FirstOrDefault(character =>
            string.Equals(character.Name, characterName, StringComparison.Ordinal));

    private LauncherTreeNodeViewModel? FindNode(SelectionKey key)
    {
        LauncherTreeNodeViewModel? server = Servers.FirstOrDefault(candidate =>
            string.Equals(candidate.ServerName, key.ServerName, StringComparison.Ordinal));
        if (server is null || key.Kind == LauncherTreeNodeKind.Server)
        {
            return server;
        }

        LauncherTreeNodeViewModel? account = server.Children.FirstOrDefault(candidate =>
            string.Equals(candidate.AccountName, key.AccountName, StringComparison.Ordinal));
        if (account is null || key.Kind == LauncherTreeNodeKind.Account)
        {
            return account;
        }

        return account.Children.FirstOrDefault(candidate =>
            string.Equals(candidate.CharacterName, key.CharacterName, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> ParseLines(string text, bool distinct)
    {
        IEnumerable<string> values = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0);
        if (distinct)
        {
            values = values.Distinct(StringComparer.Ordinal);
        }

        return values.ToArray();
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string SafeDisplayError(Exception exception, string? secret)
    {
        string message = exception.Message;
        if (!string.IsNullOrEmpty(secret))
        {
            message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(message)
            ? "The launcher operation failed."
            : message;
    }

    private async Task VerifyContentAsync()
    {
        if (IsBusy)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        IsBusy = true;
        LastError = null;
        OperationStatus = "Verifying client content… this reads the whole package.";
        try
        {
            InstallRecordVerification verification = await _installer
                .LoadExistingWithProgressAsync(
                    cancellation.Token,
                    forceFullVerification: true,
                    progress: new Progress<string>(status =>
                        OperationStatus = status))
                .ConfigureAwait(true);
            _isContentUpdateRequired = verification.RequiresContentUpdate;
            _orchestrator.SetInstallationState(
                verification.IsVerified || verification.RequiresContentUpdate
                    ? verification.Record
                    : null,
                verification.Status);
            OperationStatus = verification.Status;
            if (!verification.IsVerified
                && !verification.RequiresContentUpdate)
            {
                LastError = verification.Status;
            }
        }
        catch (OperationCanceledException)
        {
            OperationStatus = "Verification cancelled.";
        }
        catch (Exception ex)
        {
            LastError = SafeDisplayError(ex, secret: null);
            OperationStatus = "Verification failed.";
        }
        finally
        {
            _operationCancellation = null;
            IsBusy = false;
            RefreshFromCore();
        }
    }

    private void OnInstallCompleted(LauncherInstallRecord record)
    {
        bool attemptImmediateCompatibilityConfirmation = false;
        _isContentUpdateRequired = false;
        _openUpdateAfterContentCompletion = true;
        if (FirstRunWizardShell.IsContentUpdate
            && (record.RequiresClientCompatibilityConfirmation
                || !UpdatePrompt.IsStartupCheckComplete
                || !UpdatePrompt.StartupCheckSucceeded
                || UpdatePrompt.IsClientUpdateAvailable))
        {
            _pendingInstalledContent = record;
            _isClientCompatibilityPending = true;
            const string requirement = "acdream built and verified the world data. "
                + "Install the matching game update next; Play stays disabled until it finishes.";
            FirstRunWizardShell.SetCompletionRequirement(requirement);
            _orchestrator.SetInstallationState(
                null,
                "World data is ready; install the matching game update before playing.");
            OperationStatus = "World data is ready; matching game update required.";
            attemptImmediateCompatibilityConfirmation = true;
        }
        else
        {
            _orchestrator.SetInstallRecord(record);
            OperationStatus = "Client content installed and verified.";
        }

        LastError = null;
        RefreshFromCore();
        if (attemptImmediateCompatibilityConfirmation)
        {
            PublishPendingContentIfCompatible(clientWasInstalled: false);
        }
    }

    private void OnClientVersionChanged()
    {
        PublishPendingContentIfCompatible(clientWasInstalled: true);
        OperationStatus = "Versioned client activation changed.";
        LastError = null;
        RefreshFromCore();
    }

    private void OnStartupUpdateCheckCompleted(object? sender, EventArgs e) =>
        PublishPendingContentIfCompatible(clientWasInstalled: false);

    private void PublishPendingContentIfCompatible(bool clientWasInstalled)
    {
        if (_pendingInstalledContent is null)
        {
            return;
        }

        bool compatible = clientWasInstalled
            || (UpdatePrompt.StartupCheckSucceeded
                && !UpdatePrompt.IsClientUpdateAvailable);
        if (!compatible)
        {
            return;
        }

        LauncherInstallRecord pendingRecord = _pendingInstalledContent;
        try
        {
            _installer.ConfirmClientCompatibility();
        }
        catch (Exception ex)
        {
            string status = "The matching client is ready, but the content gate "
                + "could not be cleared: " + SafeDisplayError(ex, secret: null);
            _orchestrator.SetInstallationState(null, status);
            OperationStatus = status;
            LastError = status;
            return;
        }

        LauncherInstallRecord record = pendingRecord with
        {
            RequiresClientCompatibilityConfirmation = false,
        };
        _pendingInstalledContent = null;
        _isClientCompatibilityPending = false;
        FirstRunWizardShell.SetCompletionRequirement(null);
        _orchestrator.SetInstallRecord(record);
        OperationStatus = "Client and world data are installed and verified.";
        OnPropertyChanged(nameof(InstallationBannerTitle));
    }

    private void NotifyCommandStates()
    {
        AddServerCommand.NotifyCanExecuteChanged();
        AddAccountCommand.NotifyCanExecuteChanged();
        AddCharacterCommand.NotifyCanExecuteChanged();
        EditSelectedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        SaveCharacterSettingsCommand.NotifyCanExecuteChanged();
        LaunchGuiCommand.NotifyCanExecuteChanged();
        LaunchAccountGuiSelectCommand.NotifyCanExecuteChanged();
        LaunchHeadlessCommand.NotifyCanExecuteChanged();
        CancelOperationCommand.NotifyCanExecuteChanged();
        ClearFinishedSessionsCommand.NotifyCanExecuteChanged();
        VerifyContentCommand.NotifyCanExecuteChanged();
        foreach (LauncherSessionRowViewModel session in Sessions)
        {
            session.NotifyCommandState();
        }
        FirstRunWizardShell.NotifyCommandStates();
        UpdatePrompt.NotifyCommandStates();
    }

    private readonly record struct SelectionKey(
        LauncherTreeNodeKind Kind,
        string ServerName,
        string? AccountName,
        string? CharacterName)
    {
        public static SelectionKey? From(LauncherTreeNodeViewModel? node) =>
            node is null
                ? null
                : new SelectionKey(
                    node.Kind,
                    node.ServerName,
                    node.AccountName,
                    node.CharacterName);
    }
}
