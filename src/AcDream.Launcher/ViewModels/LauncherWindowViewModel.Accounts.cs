using System.Collections.ObjectModel;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.ViewModels;

public sealed partial class LauncherWindowViewModel
{
    public ObservableCollection<LauncherAccountGroupViewModel> Accounts { get; } = [];
    public bool HasAccounts => Accounts.Count != 0;
    public AsyncRelayCommand LaunchCheckedCommand { get; private set; } = null!;
    public string CheckedSelectionSummary => $"{AllAccountRows.Count(row => row.IsChecked)} selected · {AllAccountRows.Count(row => row.IsChecked && row.CanPlay)} ready";
    private IEnumerable<LauncherAccountServerRowViewModel> AllAccountRows => Accounts.SelectMany(account => account.Servers);

    private void InitializeAccountCommands() => LaunchCheckedCommand = new AsyncRelayCommand(
        () => LaunchRowsAsync(AllAccountRows.Where(row => row.IsChecked).ToArray()),
        () => CanInteract && AllAccountRows.Any(row => row.IsChecked && row.CanPlay));

    public void RefreshProfiles() => RefreshFromCore();

    private void RefreshAccountRows(LauncherStateSnapshot snapshot)
    {
        var retained = new HashSet<LauncherAccountServerRowViewModel>();
        foreach (string name in snapshot.SharedAccountNames ?? [])
            if (!Accounts.Any(account => account.AccountName == name)) Accounts.Add(new LauncherAccountGroupViewModel(name));
        foreach (LauncherServerSnapshot server in snapshot.Servers)
        foreach (LauncherAccountSnapshot account in server.Accounts)
        {
            LauncherAccountGroupViewModel? group = Accounts.FirstOrDefault(item => item.AccountName == account.AccountName);
            if (group is null)
            {
                group = new LauncherAccountGroupViewModel(account.AccountName);
                Accounts.Add(group);
            }
            LauncherAccountServerRowViewModel? row = group.Servers.FirstOrDefault(item => item.ServerName == server.Name);
            if (row is null)
            {
                row = new LauncherAccountServerRowViewModel(account.AccountName, server.Name,
                    GetRowDisabledReason, NotifyAccountCommands, item => LaunchRowsAsync([item]),
                    StopSessionAsync, OpenRowOptions, () => CanInteract);
                group.Servers.Add(row);
            }
            retained.Add(row);
            row.Update(server, account, snapshot.Sessions.OrderByDescending(session => session.CreatedAt).FirstOrDefault(session =>
                session.ServerName == server.Name && session.AccountName == account.AccountName));
        }
        foreach (LauncherAccountGroupViewModel group in Accounts.ToArray())
        {
            foreach (LauncherAccountServerRowViewModel row in group.Servers.Where(row => !retained.Contains(row)).ToArray())
                group.Servers.Remove(row);
            if (group.Servers.Count == 0 && !(snapshot.SharedAccountNames?.Contains(group.AccountName) ?? false)) Accounts.Remove(group);
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

    private void OpenRowOptions(LauncherAccountServerRowViewModel row) => OpenAccountRowOptions(row);

    private void NotifyAccountCommands()
    {
        LaunchCheckedCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CheckedSelectionSummary));
        OnPropertyChanged(nameof(HasAccounts));
        foreach (LauncherAccountServerRowViewModel row in AllAccountRows) row.NotifyState();
    }
}
