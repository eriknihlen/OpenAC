using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.Core.Orchestration;

public sealed record LauncherCharacterSnapshot(
    string ServerName,
    string AccountName,
    string Name,
    string? Id,
    LaunchMode LaunchMode,
    IReadOnlyList<string> Plugins,
    IReadOnlyList<string> LoginCommands,
    bool HasRunningSession,
    string SessionStatus);

public sealed record LauncherAccountSnapshot(
    string ServerName,
    string AccountName,
    IReadOnlyList<LauncherCharacterSnapshot> Characters,
    bool HasRunningActivity,
    string ActivityStatus);

public sealed record LauncherServerSnapshot(
    string Name,
    string Host,
    int Port,
    IReadOnlyList<LauncherAccountSnapshot> Accounts);

public enum LauncherActivityKind
{
    Play,
    Probe,
}

public enum LauncherActivityState
{
    Starting,
    Running,
    Connected,
    InWorld,
    Disconnected,
    Stopping,
    Exited,
    Failed,
    Cancelled,
}

public sealed record LauncherSessionSnapshot(
    string SessionId,
    LauncherActivityKind Kind,
    string ServerName,
    string AccountName,
    string? CharacterName,
    LaunchMode? LaunchMode,
    LauncherActivityState State,
    string Status,
    int? ExitCode,
    string? Error,
    DateTimeOffset CreatedAt,
    string? ExitReason = null,
    bool ExitedGracefully = false)
{
    public bool IsActive => State is not (
        LauncherActivityState.Exited
        or LauncherActivityState.Failed
        or LauncherActivityState.Cancelled);
}

public sealed record LauncherStateSnapshot(
    IReadOnlyList<LauncherServerSnapshot> Servers,
    IReadOnlyList<LauncherSessionSnapshot> Sessions,
    LauncherPlatformCapabilities Platform,
    bool IsInstallationReady,
    string InstallationStatus,
    IReadOnlyList<string>? SharedAccountNames = null);

public sealed class LauncherOperationException : Exception
{
    public LauncherOperationException(string message)
        : base(message)
    {
    }
}
