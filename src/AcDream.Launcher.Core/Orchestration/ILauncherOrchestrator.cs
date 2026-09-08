using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.Core.Orchestration;

public interface ILauncherOrchestrator : IDisposable
{
    event EventHandler? StateChanged;

    void LoadProfiles();

    LauncherStateSnapshot GetSnapshot();

    LauncherCapability GetLaunchCapability(LaunchMode mode);

    LauncherCapability GetAccountLaunchCapability(
        string serverName,
        string accountName,
        LaunchMode mode);

    LauncherCapability GetProbeCapability(string serverName, string accountName);

    void SetInstallRecord(LauncherInstallRecord? installRecord);

    void SetInstallationState(
        LauncherInstallRecord? installRecord,
        string installationStatus) =>
        SetInstallRecord(installRecord);

    void AddServer(string name, string host, int port);

    void EditServer(string name, string newName, string newHost, int newPort);

    void RemoveServer(string name);

    void AddAccount(string serverName, string accountName, string password);

    void EditAccount(
        string serverName,
        string accountName,
        string newAccountName,
        string? newPassword);

    void RemoveAccount(string serverName, string accountName);

    void AddCharacter(
        string serverName,
        string accountName,
        string characterName,
        string? characterId);

    void EditCharacterIdentity(
        string serverName,
        string accountName,
        string characterName,
        string newCharacterName,
        string? newCharacterId);

    void UpdateCharacterSettings(
        string serverName,
        string accountName,
        string characterName,
        LaunchMode launchMode,
        IReadOnlyList<string> plugins,
        IReadOnlyList<string> loginCommands);

    void RemoveCharacter(
        string serverName,
        string accountName,
        string characterName);

    Task<LauncherSessionSnapshot> LaunchAsync(
        string serverName,
        string accountName,
        string? characterName,
        LaunchMode mode,
        CancellationToken cancellationToken = default);

    Task<LauncherSessionSnapshot> ProbeAsync(
        string serverName,
        string accountName,
        CancellationToken cancellationToken = default);

    Task StopSessionAsync(
        string sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    void PollStatus();

    void ClearFinishedSessions();
}
