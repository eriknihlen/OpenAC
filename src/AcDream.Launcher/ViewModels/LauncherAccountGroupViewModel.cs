using System.Collections.ObjectModel;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.ViewModels;

/// <summary>One account on one server: its profile tags, the plugins its characters share, and its
/// launch row.</summary>
public sealed class LauncherAccountGroupViewModel : ObservableObject
{
    private bool _isExpanded = true;
    private bool _isVisible = true;
    private IReadOnlyList<string> _profiles = [];
    private string _pluginsSummary = "none";
    private string _subtitle = string.Empty;

    public LauncherAccountGroupViewModel(
        string serverName,
        string accountName,
        Action<LauncherAccountGroupViewModel>? editPlugins = null,
        Func<bool>? canInteract = null)
    {
        ServerName = serverName;
        AccountName = accountName;
        Func<bool> canEdit = canInteract ?? (() => true);
        EditPluginsCommand = new RelayCommand(() => editPlugins?.Invoke(this), () => editPlugins is not null && canEdit());
    }

    public string ServerName { get; }

    public string AccountName { get; }

    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

    /// <summary>False while the profile filter hides this account.</summary>
    public bool IsVisible { get => _isVisible; set => SetProperty(ref _isVisible, value); }

    public ObservableCollection<LauncherAccountServerRowViewModel> Rows { get; } = [];

    public IReadOnlyList<string> Profiles
    {
        get => _profiles;
        private set { if (SetProperty(ref _profiles, value)) OnPropertyChanged(nameof(HasProfiles)); }
    }

    public bool HasProfiles => Profiles.Count > 0;

    /// <summary>The account's plugins by name, such as "Combat · Stats", or "none".</summary>
    public string PluginsSummary { get => _pluginsSummary; private set => SetProperty(ref _pluginsSummary, value); }

    /// <summary>"sawato · 7 characters".</summary>
    public string Subtitle { get => _subtitle; private set => SetProperty(ref _subtitle, value); }

    public RelayCommand EditPluginsCommand { get; }

    public bool HasProfile(string profile) =>
        Profiles.Contains(profile, StringComparer.OrdinalIgnoreCase);

    internal void Update(LauncherAccountSnapshot account, Func<string, string> pluginDisplayName)
    {
        if (!Profiles.SequenceEqual(account.Profiles, StringComparer.Ordinal))
        {
            Profiles = [.. account.Profiles];
        }

        PluginsSummary = account.Plugins.Count == 0
            ? "none"
            : string.Join(" · ", account.Plugins.Select(pluginDisplayName));
        int count = account.Characters.Count;
        Subtitle = $"{ServerName} · {count} {(count == 1 ? "character" : "characters")}";
        EditPluginsCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>What a row's Options menu does, supplied by the window.</summary>
public sealed record LauncherRowActions(
    Action<LauncherAccountServerRowViewModel> LogonCommands,
    Action<LauncherAccountServerRowViewModel> CharacterPlugins,
    Action<LauncherAccountServerRowViewModel> OpenLogs,
    Action<LauncherAccountServerRowViewModel> RemoveCharacter);

public sealed class LauncherAccountServerRowViewModel : ObservableObject
{
    public const string CharacterSelect = "Character select";
    private readonly Func<LauncherAccountServerRowViewModel, string?> _disabledReason;
    private readonly Action _changed;
    private Action<LauncherAccountServerRowViewModel>? _selectionChanged;
    private bool _applyingSavedSelection;
    private bool _selectionLoaded;
    private readonly Func<bool> _canInteract;
    private bool _isChecked;
    private string _selectedCharacter = CharacterSelect;
    private string? _activeCharacterName;
    private string _selectedLaunchMode = "Graphical";
    private string _endpoint = "";
    private string _status = "Ready";
    private string? _activeSessionId;
    private bool _activeIsHeadless;
    private Action<string, string>? _openConsole;
    private bool? _isServerOnline;
    private string _serverStatusText = "Not checked";
    private double? _latencyMilliseconds;
    private string? _launchError;

    // A ping meter fills one bar above this, two above the fair mark, three below it.
    private const double FairLatencyMilliseconds = 200;
    private const double GoodLatencyMilliseconds = 80;
    private const string EmptyBarColor = "#36444C";

    public LauncherAccountServerRowViewModel(string accountName, string serverName,
        Func<LauncherAccountServerRowViewModel, string?> disabledReason, Action changed,
        Func<LauncherAccountServerRowViewModel, Task> launch,
        Func<string, Task> stop, LauncherRowActions actions,
        Func<bool> canInteract)
    {
        AccountName = accountName;
        ServerName = serverName;
        _disabledReason = disabledReason;
        _changed = changed;
        _canInteract = canInteract;
        PlayCommand = new AsyncRelayCommand(() => launch(this), () => CanPlay);
        StopCommand = new AsyncRelayCommand(() => _activeSessionId is { } id ? stop(id) : Task.CompletedTask,
            () => IsActive && canInteract());
        LogonCommandsCommand = new RelayCommand(() => actions.LogonCommands(this), canInteract);
        CharacterPluginsCommand = new RelayCommand(
            () => actions.CharacterPlugins(this),
            () => canInteract() && CharacterName is not null);
        OpenLogsCommand = new RelayCommand(() => actions.OpenLogs(this));
        RemoveCharacterCommand = new RelayCommand(
            () => actions.RemoveCharacter(this),
            () => canInteract() && CharacterName is not null && !IsActive);
        ConsoleCommand = new RelayCommand(
            () =>
            {
                if (_activeSessionId is { } id)
                    _openConsole?.Invoke(id, $"{ActiveCharacterName ?? AccountName} · {ServerName}");
            },
            () => CanOpenConsole);
    }

    public string AccountName { get; }
    public string ServerName { get; }
    public string Endpoint { get => _endpoint; private set => SetProperty(ref _endpoint, value); }
    public bool IsChecked { get => _isChecked; set { if (SetProperty(ref _isChecked, value)) _changed(); } }
    public ObservableCollection<string> CharacterChoices { get; } = [CharacterSelect];
    public IReadOnlyList<string> LaunchModes { get; } = ["Graphical", "Headless"];
    public string SelectedCharacter { get => _selectedCharacter; set { if (SetProperty(ref _selectedCharacter, value ?? CharacterSelect)) { OnPropertyChanged(nameof(DisplayedCharacter)); NotifyState(); _changed(); SaveSelection(); } } }

    /// <summary>The character the running session is playing, once the client reports it.</summary>
    public string? ActiveCharacterName
    {
        get => _activeCharacterName;
        private set { if (SetProperty(ref _activeCharacterName, value)) OnPropertyChanged(nameof(DisplayedCharacter)); }
    }

    /// <summary>
    /// What the character box shows: whoever is in world during a session, and the saved launch
    /// choice otherwise. Picking a character still writes to the saved choice.
    /// </summary>
    public string DisplayedCharacter
    {
        get => IsActive && ActiveCharacterName is { Length: > 0 } name && CharacterChoices.Contains(name, StringComparer.Ordinal)
            ? name
            : SelectedCharacter;
        set { if (value is not null) SelectedCharacter = value; }
    }
    public string SelectedLaunchMode { get => _selectedLaunchMode; set { if (SetProperty(ref _selectedLaunchMode, value ?? "Graphical")) { NotifyState(); _changed(); SaveSelection(); } } }

    /// <summary>Writes the row's character and launch mode to the profile, unless they just came from it.</summary>
    private void SaveSelection()
    {
        if (!_applyingSavedSelection) _selectionChanged?.Invoke(this);
    }

    /// <summary>How the row opens a session's console: the session's id and a title for the window.</summary>
    internal void UseConsole(Action<string, string> open) => _openConsole = open;

    internal void UseSelectionStore(Action<LauncherAccountServerRowViewModel> save) => _selectionChanged = save;
    public LaunchMode Mode => SelectedLaunchMode == "Headless" ? LaunchMode.Headless : SelectedCharacter == CharacterSelect ? LaunchMode.GuiSelect : LaunchMode.Gui;
    public string? CharacterName => SelectedCharacter == CharacterSelect ? null : SelectedCharacter;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string? LaunchError { get => _launchError; private set { if (SetProperty(ref _launchError, value)) OnPropertyChanged(nameof(HasLaunchError)); } }
    public bool HasLaunchError => !string.IsNullOrEmpty(LaunchError);
    public string ServerStatusText { get => _serverStatusText; set => SetProperty(ref _serverStatusText, value); }
    public string HealthText { get => ServerStatusText; set { ServerStatusText = value; OnPropertyChanged(); } }
    public bool? IsServerOnline { get => _isServerOnline; set { if (SetProperty(ref _isServerOnline, value)) { OnPropertyChanged(nameof(ServerDotColor)); NotifyPing(); } } }
    public string ServerDotColor => IsServerOnline switch { true => "#65D99B", false => "#F17474", _ => "#89949C" };

    public double? LatencyMilliseconds
    {
        get => _latencyMilliseconds;
        set { if (SetProperty(ref _latencyMilliseconds, value)) NotifyPing(); }
    }

    public string LatencyText => LatencyMilliseconds is { } ms ? $"{ms:0} ms" : "";
    public bool HasLatency => LatencyMilliseconds is not null;

    /// <summary>Filled bars in the ping meter: three for a fast reply, one for a slow one.</summary>
    public int PingBars => IsServerOnline == true && LatencyMilliseconds is { } ms
        ? ms <= GoodLatencyMilliseconds ? 3 : ms <= FairLatencyMilliseconds ? 2 : 1
        : 0;

    public string PingColor => PingBars switch { 3 => "#65D99B", 2 => "#DBB573", 1 => "#F17474", _ => EmptyBarColor };
    public string PingBar1Color => PingBars >= 1 ? PingColor : EmptyBarColor;
    public string PingBar2Color => PingBars >= 2 ? PingColor : EmptyBarColor;
    public string PingBar3Color => PingBars >= 3 ? PingColor : EmptyBarColor;

    public string PingTooltip => PingBars switch
    {
        3 => $"{LatencyText} · fast",
        2 => $"{LatencyText} · fair",
        1 => $"{LatencyText} · slow",
        _ => "No reply timed",
    };

    private void NotifyPing()
    {
        OnPropertyChanged(nameof(LatencyText));
        OnPropertyChanged(nameof(HasLatency));
        OnPropertyChanged(nameof(PingBars));
        OnPropertyChanged(nameof(PingColor));
        OnPropertyChanged(nameof(PingBar1Color));
        OnPropertyChanged(nameof(PingBar2Color));
        OnPropertyChanged(nameof(PingBar3Color));
        OnPropertyChanged(nameof(PingTooltip));
    }
    public bool IsActive => _activeSessionId is not null;

    /// <summary>A session with no window is talked to through its console; one with a window has the game itself.</summary>
    public bool CanOpenConsole => IsActive && _activeIsHeadless && _openConsole is not null;
    public bool CanEditSelection => !IsActive && _canInteract();
    public bool CanPlay => DisabledReason.Length == 0;
    public string DisabledReason => _disabledReason(this) ?? "";
    public AsyncRelayCommand PlayCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public RelayCommand LogonCommandsCommand { get; }
    public RelayCommand CharacterPluginsCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand RemoveCharacterCommand { get; }
    public RelayCommand ConsoleCommand { get; }

    internal void Update(LauncherServerSnapshot server, LauncherAccountSnapshot account, LauncherSessionSnapshot? session)
    {
        string endpoint = $"{server.Host}:{server.Port}";
        if (Endpoint != endpoint)
        {
            IsServerOnline = null;
            ServerStatusText = "Not checked";
            LatencyMilliseconds = null;
        }
        Endpoint = endpoint;
        string[] choices = [CharacterSelect, .. account.Characters.Select(character => character.Name)];
        _applyingSavedSelection = true;
        try
        {
            if (!CharacterChoices.SequenceEqual(choices))
            {
                string selected = SelectedCharacter;
                CharacterChoices.Clear();
                foreach (string choice in choices) CharacterChoices.Add(choice);
                SelectedCharacter = choices.Contains(selected, StringComparer.Ordinal) ? selected : CharacterSelect;
            }
            if (!_selectionLoaded)
            {
                _selectionLoaded = true;
                string saved = account.SelectedCharacter ?? CharacterSelect;
                SelectedCharacter = choices.Contains(saved, StringComparer.Ordinal) ? saved : CharacterSelect;
                SelectedLaunchMode = account.SelectedLaunchMode == LaunchMode.Headless ? "Headless" : "Graphical";
            }
        }
        finally { _applyingSavedSelection = false; }
        _activeSessionId = session?.IsActive == true ? session.SessionId : null;
        _activeIsHeadless = session?.IsActive == true && session.LaunchMode == LaunchMode.Headless;
        ActiveCharacterName = session?.IsActive == true ? session.CharacterName : null;
        Status = session?.Error ?? session?.Status ?? account.ActivityStatus;
        LaunchError = session?.Error;
        NotifyState();
    }

    internal void NotifyState()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanOpenConsole));
        OnPropertyChanged(nameof(DisplayedCharacter));
        OnPropertyChanged(nameof(CanEditSelection));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(DisabledReason));
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        LogonCommandsCommand.NotifyCanExecuteChanged();
        CharacterPluginsCommand.NotifyCanExecuteChanged();
        RemoveCharacterCommand.NotifyCanExecuteChanged();
        ConsoleCommand.NotifyCanExecuteChanged();
    }
}
