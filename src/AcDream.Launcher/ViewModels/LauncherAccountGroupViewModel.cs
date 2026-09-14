using System.Collections.ObjectModel;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.ViewModels;

public sealed class LauncherAccountGroupViewModel(string accountName) : ObservableObject
{
    private bool _isExpanded = true;
    public string AccountName { get; } = accountName;
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public ObservableCollection<LauncherAccountServerRowViewModel> Servers { get; } = [];
}

public sealed class LauncherAccountServerRowViewModel : ObservableObject
{
    public const string CharacterSelect = "Character select";
    private readonly Func<LauncherAccountServerRowViewModel, string?> _disabledReason;
    private readonly Action<LauncherAccountServerRowViewModel> _changed;
    private bool _applying;
    private bool _restored;
    private readonly Func<bool> _canInteract;
    private bool _isChecked;
    private string _selectedCharacter = CharacterSelect;
    private string _selectedLaunchMode = "Graphical";
    private string _endpoint = "";
    private string _status = "Ready";
    private string? _activeSessionId;
    private bool? _isServerOnline;
    private string _serverStatusText = "Not checked";
    private string? _launchError;

    public LauncherAccountServerRowViewModel(string accountName, string serverName,
        Func<LauncherAccountServerRowViewModel, string?> disabledReason, Action<LauncherAccountServerRowViewModel> changed,
        Func<LauncherAccountServerRowViewModel, Task> launch,
        Func<string, Task> stop, Action<LauncherAccountServerRowViewModel> options,
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
        OptionsCommand = new RelayCommand(() => options(this), canInteract);
    }

    public string AccountName { get; }
    public string ServerName { get; }
    public string Endpoint { get => _endpoint; private set => SetProperty(ref _endpoint, value); }
    public bool IsChecked { get => _isChecked; set { if (SetProperty(ref _isChecked, value)) Changed(); } }
    public ObservableCollection<string> CharacterChoices { get; } = [CharacterSelect];
    public IReadOnlyList<string> LaunchModes { get; } = ["Graphical", "Headless"];
    public string SelectedCharacter { get => _selectedCharacter; set { if (SetProperty(ref _selectedCharacter, value ?? CharacterSelect)) { NotifyState(); Changed(); } } }
    public string SelectedLaunchMode { get => _selectedLaunchMode; set { if (SetProperty(ref _selectedLaunchMode, value ?? "Graphical")) { NotifyState(); Changed(); } } }
    public bool IsHeadless => SelectedLaunchMode == "Headless";

    /// <summary>A change the user made, not one applied from the saved profile.</summary>
    private void Changed()
    {
        if (!_applying)
            _changed(this);
    }
    public LaunchMode Mode => SelectedLaunchMode == "Headless" ? LaunchMode.Headless : SelectedCharacter == CharacterSelect ? LaunchMode.GuiSelect : LaunchMode.Gui;
    public string? CharacterName => SelectedCharacter == CharacterSelect ? null : SelectedCharacter;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string? LaunchError { get => _launchError; private set { if (SetProperty(ref _launchError, value)) OnPropertyChanged(nameof(HasLaunchError)); } }
    public bool HasLaunchError => !string.IsNullOrEmpty(LaunchError);
    public string ServerStatusText { get => _serverStatusText; set => SetProperty(ref _serverStatusText, value); }
    public string HealthText { get => ServerStatusText; set { ServerStatusText = value; OnPropertyChanged(); } }
    public bool? IsServerOnline { get => _isServerOnline; set { if (SetProperty(ref _isServerOnline, value)) OnPropertyChanged(nameof(ServerDotColor)); } }
    public string ServerDotColor => IsServerOnline switch { true => "#65D99B", false => "#F17474", _ => "#89949C" };
    public bool IsActive => _activeSessionId is not null;
    public bool CanEditSelection => !IsActive && _canInteract();
    public bool CanPlay => DisabledReason.Length == 0;
    public string DisabledReason => _disabledReason(this) ?? "";
    public AsyncRelayCommand PlayCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public RelayCommand OptionsCommand { get; }

    internal void Update(LauncherServerSnapshot server, LauncherAccountSnapshot account, LauncherSessionSnapshot? session)
    {
        string endpoint = $"{server.Host}:{server.Port}";
        if (Endpoint != endpoint)
        {
            IsServerOnline = null;
            ServerStatusText = "Not checked";
        }
        Endpoint = endpoint;
        string[] choices = [CharacterSelect, .. account.Characters.Select(character => character.Name)];
        _applying = true;
        try
        {
            if (!CharacterChoices.SequenceEqual(choices))
            {
                string selected = SelectedCharacter;
                CharacterChoices.Clear();
                foreach (string choice in choices) CharacterChoices.Add(choice);
                SelectedCharacter = choices.Contains(selected, StringComparer.Ordinal) ? selected : CharacterSelect;
            }
            // The row comes back the way it was left, once, when it first
            // appears; from then on what the user sets here is the truth
            // and the profile just remembers it. A saved character no
            // longer on the account falls back to character select.
            if (!_restored)
            {
                _restored = true;
                IsChecked = account.Selected;
                SelectedLaunchMode = account.Headless ? "Headless" : "Graphical";
                SelectedCharacter = account.SelectedCharacter is { } saved && choices.Contains(saved, StringComparer.Ordinal)
                    ? saved
                    : CharacterSelect;
            }
        }
        finally
        {
            _applying = false;
        }
        _activeSessionId = session?.IsActive == true ? session.SessionId : null;
        Status = session?.Error ?? session?.Status ?? account.ActivityStatus;
        LaunchError = session?.Error;
        NotifyState();
    }

    internal void NotifyState()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanEditSelection));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(DisabledReason));
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        OptionsCommand.NotifyCanExecuteChanged();
    }
}
