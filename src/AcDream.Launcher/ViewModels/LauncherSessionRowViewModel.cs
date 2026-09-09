using AcDream.Launcher.Core.Orchestration;

namespace AcDream.Launcher.ViewModels;

public sealed class LauncherSessionRowViewModel
{
    public LauncherSessionRowViewModel(
        LauncherSessionSnapshot snapshot,
        Func<string, Task> stop,
        Func<bool>? canStop = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(stop);

        SessionId = snapshot.SessionId;
        Account = snapshot.AccountName;
        Server = snapshot.ServerName;
        Character = DescribeCharacter(snapshot);
        State = DescribeState(snapshot);
        Status = DescribeStatus(snapshot);
        Error = snapshot.Error;
        IsActive = snapshot.IsActive;
        StopCommand = new AsyncRelayCommand(
            () => stop(SessionId),
            () => IsActive && (canStop?.Invoke() ?? true));
    }

    public string SessionId { get; }

    public string Account { get; }

    public string Server { get; }

    public string Character { get; }

    public string State { get; }

    public string Status { get; }

    public string? Error { get; }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool IsActive { get; }

    public AsyncRelayCommand StopCommand { get; }

    public void NotifyCommandState() => StopCommand.NotifyCanExecuteChanged();

    private static string DescribeStatus(LauncherSessionSnapshot snapshot)
    {
        if (snapshot.IsActive)
        {
            return snapshot.Status;
        }

        if (snapshot.State == LauncherActivityState.Cancelled)
        {
            return "Cancelled before it started.";
        }

        if (snapshot.ExitedGracefully)
        {
            return snapshot.Kind == LauncherActivityKind.Probe
                ? "Finished reading characters."
                : "Exited gracefully — logged out cleanly.";
        }

        // Not graceful: the host never reported its own exit, or reported a
        // failure. The server may still be holding the account, and saying so
        // here is what stops the next login failure being a mystery.
        string detail = snapshot.ExitReason switch
        {
            "connection-error" => "Could not reach the server",
            "credential-error" => "The account or password was rejected",
            "configuration-error" => "The session configuration was rejected",
            "usage-error" => "The client rejected how it was started",
            null or "" => "Crashed",
            _ => "Stopped unexpectedly",
        };

        return $"{detail} — the server may hold this account for a few minutes.";
    }

    private static string DescribeCharacter(LauncherSessionSnapshot snapshot)
    {
        if (snapshot.Kind == LauncherActivityKind.Probe)
        {
            return "Character refresh";
        }

        return string.IsNullOrWhiteSpace(snapshot.CharacterName)
            ? "Character select"
            : snapshot.CharacterName;
    }

    private static string DescribeState(LauncherSessionSnapshot snapshot) =>
        snapshot.State switch
        {
            LauncherActivityState.Starting or LauncherActivityState.Running =>
                "Starting",
            LauncherActivityState.Connected => snapshot.Kind
                == LauncherActivityKind.Probe
                    ? "Reading characters"
                    : "Character select",
            LauncherActivityState.InWorld => "In game",
            LauncherActivityState.Disconnected or LauncherActivityState.Stopping =>
                "Stopping",
            LauncherActivityState.Failed => "Failed",
            _ => "Stopped",
        };
}
