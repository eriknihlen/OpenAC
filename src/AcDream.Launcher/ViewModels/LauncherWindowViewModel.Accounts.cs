using System.Collections.ObjectModel;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.ViewModels;

/// <summary>One chip of the profile filter above the accounts: "All", or one profile tag.</summary>
public sealed class ProfileFilterChipViewModel : ObservableObject
{
    private bool _isSelected;

    public ProfileFilterChipViewModel(string label, string? profile, Action<ProfileFilterChipViewModel> select)
    {
        Label = label;
        Profile = profile;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public string Label { get; }

    /// <summary>The tag this chip shows, or null for "All".</summary>
    public string? Profile { get; }

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public RelayCommand SelectCommand { get; }
}

public sealed partial class LauncherWindowViewModel
{
    private string? _profileFilter;

    public ObservableCollection<LauncherAccountGroupViewModel> Accounts { get; } = [];
    public bool HasAccounts => Accounts.Count != 0;

    /// <summary>"All" and one chip per profile tag, in the order the tags first appear.</summary>
    public ObservableCollection<ProfileFilterChipViewModel> ProfileFilters { get; } = [];

    public bool HasProfileFilters => ProfileFilters.Count > 1;

    /// <summary>The profile tag the accounts are filtered by, or null for all accounts.</summary>
    public string? ProfileFilter
    {
        get => _profileFilter;
        set
        {
            if (SetProperty(ref _profileFilter, value))
            {
                ApplyProfileFilter();
            }
        }
    }

    public AsyncRelayCommand LaunchCheckedCommand { get; private set; } = null!;
    public string CheckedSelectionSummary => $"{CheckedRows.Count()} selected · {CheckedRows.Count(row => row.CanPlay)} ready";

    /// <summary>"Play selected (2)".</summary>
    public string PlayCheckedText => $"Play selected ({CheckedRows.Count()})";

    private IEnumerable<LauncherAccountServerRowViewModel> AllAccountRows => Accounts.SelectMany(account => account.Rows);

    /// <summary>The ticked rows the profile filter shows: what Play selected starts.</summary>
    private IEnumerable<LauncherAccountServerRowViewModel> CheckedRows =>
        Accounts.Where(account => account.IsVisible).SelectMany(account => account.Rows).Where(row => row.IsChecked);

    /// <summary>Whether launching the checked rows would actually do something, so the button can
    /// show gold only when it is ready rather than whenever it is on screen.</summary>
    public bool HasPlayableCheckedRows => CheckedRows.Any(row => row.CanPlay);

    private void InitializeAccountCommands() => LaunchCheckedCommand = new AsyncRelayCommand(
        () => LaunchRowsAsync([.. CheckedRows]),
        () => CanInteract && CheckedRows.Any(row => row.CanPlay));

    public void RefreshProfiles() => RefreshFromCore();

    private void RefreshAccountRows(LauncherStateSnapshot snapshot)
    {
        var retained = new HashSet<LauncherAccountGroupViewModel>();
        int index = 0;
        foreach (LauncherServerSnapshot server in snapshot.Servers)
        foreach (LauncherAccountSnapshot account in server.Accounts)
        {
            LauncherAccountGroupViewModel? group = Accounts.FirstOrDefault(item =>
                item.ServerName == server.Name && item.AccountName == account.AccountName);
            if (group is null)
            {
                group = new LauncherAccountGroupViewModel(server.Name, account.AccountName, OpenAccountPlugins, () => CanInteract);
                var row = new LauncherAccountServerRowViewModel(account.AccountName, server.Name,
                    GetRowDisabledReason, NotifyAccountCommands, item => LaunchRowsAsync([item]),
                    StopSessionAsync,
                    new LauncherRowActions(OpenLogonCommandsFor, OpenCharacterPlugins, OpenLogsFolder, RemoveRowCharacter),
                    () => CanInteract);
                row.UseSelectionStore(SaveRowSelection);
                row.UseConsole(OpenSessionConsole);
                group.Rows.Add(row);
                Accounts.Insert(Math.Min(index, Accounts.Count), group);
            }
            else if (Accounts.IndexOf(group) != index && index < Accounts.Count)
            {
                Accounts.Move(Accounts.IndexOf(group), index);
            }

            index++;
            retained.Add(group);
            group.Update(account, PluginDisplayName);
            group.Rows[0].Update(server, account, snapshot.Sessions.OrderByDescending(session => session.CreatedAt).FirstOrDefault(session =>
                session.ServerName == server.Name && session.AccountName == account.AccountName));
        }

        foreach (LauncherAccountGroupViewModel group in Accounts.Where(group => !retained.Contains(group)).ToArray())
            Accounts.Remove(group);
        RefreshProfileFilters();
    }

    /// <summary>Rebuilds the chips from the accounts' tags, keeping the chosen one while it exists.</summary>
    private void RefreshProfileFilters()
    {
        string[] tags = [.. Accounts.SelectMany(account => account.Profiles).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (_profileFilter is not null && !tags.Contains(_profileFilter, StringComparer.OrdinalIgnoreCase))
        {
            _profileFilter = null;
            OnPropertyChanged(nameof(ProfileFilter));
        }

        string?[] wanted = [null, .. tags];
        if (!ProfileFilters.Select(chip => chip.Profile).SequenceEqual(wanted, StringComparer.Ordinal))
        {
            ProfileFilters.Clear();
            foreach (string? tag in wanted)
                ProfileFilters.Add(new ProfileFilterChipViewModel(tag ?? "All", tag, chip => ProfileFilter = chip.Profile));
            OnPropertyChanged(nameof(HasProfileFilters));
        }

        ApplyProfileFilter();
    }

    private void ApplyProfileFilter()
    {
        foreach (ProfileFilterChipViewModel chip in ProfileFilters)
            chip.IsSelected = string.Equals(chip.Profile, _profileFilter, StringComparison.OrdinalIgnoreCase);
        foreach (LauncherAccountGroupViewModel group in Accounts)
            group.IsVisible = _profileFilter is null || group.HasProfile(_profileFilter);
        NotifyAccountCommands();
    }

    private void SaveRowSelection(LauncherAccountServerRowViewModel row)
    {
        try
        {
            _orchestrator.UpdateAccountSelection(row.ServerName, row.AccountName, row.CharacterName, row.Mode);
        }
        catch (Exception ex)
        {
            LastError = SafeDisplayError(ex, secret: null);
        }
    }

    private string? GetRowDisabledReason(LauncherAccountServerRowViewModel row) =>
        !CanInteract ? "Finish the current operation or close the dialog first."
        : GetRowLaunchBlock(row);

    private string? GetRowLaunchBlock(LauncherAccountServerRowViewModel row) => GetRowLaunchBlock(row, row.CharacterName, row.Mode);

    private string? GetRowLaunchBlock(LauncherAccountServerRowViewModel row, string? characterName, LaunchMode mode)
    {
        if (_isInstallationChecking || _isClientCompatibilityCheckBlocking) return "Checking installation and client compatibility…";
        if (row.IsActive) return "This account already has an active session on this server.";
        if (mode == LaunchMode.Headless && characterName is null) return "Choose a character for Headless mode.";
        if (row.SelectedLaunchMode is not ("Graphical" or "Headless")) return "Choose Graphical or Headless.";
        LauncherStateSnapshot current = _orchestrator.GetSnapshot();
        LauncherAccountSnapshot? account = current.Servers.FirstOrDefault(server => server.Name == row.ServerName)
            ?.Accounts.FirstOrDefault(account => account.AccountName == row.AccountName);
        if (account is null) return "This account is no longer configured on this server.";
        if (account.HasRunningActivity || current.Sessions.Any(session => session.IsActive
            && session.ServerName == row.ServerName && session.AccountName == row.AccountName)) return "This account already has an active session on this server.";
        if (characterName is { } name && !account.Characters.Any(character => character.Name == name)) return "Choose a current character.";
        LauncherCapability capability = _orchestrator.GetAccountLaunchCapability(row.ServerName, row.AccountName, mode);
        return capability.IsAvailable ? null : capability.Reason ?? "Launch unavailable.";
    }

    private async Task LaunchRowsAsync(LauncherAccountServerRowViewModel[] rows)
    {
        if (!CanInteract) return;
        var pending = rows.Select(row => (Row: row, Character: row.CharacterName, Mode: row.Mode)).ToArray();
        if (pending.Length == 0) return;
        using var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        _operationCancellation = cancellation;
        IsBusy = true;
        LastError = null;
        int started = 0;
        var errors = new List<string>();
        try
        {
            foreach (var item in pending)
            {
                token.ThrowIfCancellationRequested();
                string? block = GetRowLaunchBlock(item.Row, item.Character, item.Mode);
                if (block is not null) { errors.Add($"{item.Row.AccountName} / {item.Row.ServerName}: {block}"); continue; }
                OperationStatus = $"Launching {started + 1} of {pending.Length}…";
                try
                {
                    var result = await _orchestrator.LaunchAsync(item.Row.ServerName, item.Row.AccountName,
                        item.Character, item.Mode, token).ConfigureAwait(true);
                    if (result.State is not (LauncherActivityState.Failed or LauncherActivityState.Cancelled) && result.ExitCode is not (> 0 or < 0)) started++;
                    else errors.Add($"{item.Row.AccountName} / {item.Row.ServerName}: {result.Error ?? result.Status}");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception ex) { errors.Add($"{item.Row.AccountName} / {item.Row.ServerName}: {SafeDisplayError(ex, secret: null)}"); }
            }
            OperationStatus = $"Started {started} of {pending.Length} selected sessions.";
        }
        catch (OperationCanceledException) { OperationStatus = $"Launch cancelled. Started {started} sessions."; }
        finally
        {
            LastError = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors);
            _operationCancellation = null;
            if (!_disposed)
            {
                IsBusy = false;
                RefreshFromCore();
            }
        }
    }

    private void OpenLogonCommandsFor(LauncherAccountServerRowViewModel row) =>
        OpenTextEditor(LauncherTextEditorKind.LogonCommands);

    private void OpenLogsFolder(LauncherAccountServerRowViewModel row) =>
        _installFolder?.Folders.FirstOrDefault(folder => folder.Label == "Logs")?.OpenCommand.Execute(null);

    private void RemoveRowCharacter(LauncherAccountServerRowViewModel row)
    {
        if (row.CharacterName is not { } name) return;
        (string server, string account) = (row.ServerName, row.AccountName);
        EditorDialog.Open(
            ProfileEditorKind.Remove,
            $"Remove {name}?",
            _ =>
            {
                _orchestrator.RemoveCharacter(server, account, name);
                OperationStatus = $"Removed {name}. Refreshing the account's characters brings it back.";
            },
            message: $"This removes {name} from {account} on {server}, with its own plugin list if it has one. "
                + "The next login to the account brings the character back, using the account's plugins.");
    }

    /// <summary>
    /// Raised when a session's console should be shown. Showing a window is
    /// the view's business; the view model only says which console.
    /// </summary>
    public event Action<SessionConsoleViewModel>? ConsoleRequested;

    private void OpenSessionConsole(string sessionId, string title) =>
        ConsoleRequested?.Invoke(
            new SessionConsoleViewModel(_orchestrator, sessionId, "Console · " + title));

    private void NotifyAccountCommands()
    {
        LaunchCheckedCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasPlayableCheckedRows));
        OnPropertyChanged(nameof(CheckedSelectionSummary));
        OnPropertyChanged(nameof(PlayCheckedText));
        OnPropertyChanged(nameof(HasAccounts));
        foreach (LauncherAccountGroupViewModel group in Accounts) group.EditPluginsCommand.NotifyCanExecuteChanged();
        foreach (LauncherAccountServerRowViewModel row in AllAccountRows) row.NotifyState();
    }
}
