using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.Core.Orchestration;

public interface ILauncherOrchestrator : IDisposable
{
    event EventHandler? StateChanged;

    void LoadProfiles();

    string ReadProfileText(LauncherTextEditorKind kind) =>
        throw new NotSupportedException("Text editing is not available.");

    void SaveProfileText(LauncherTextEditorKind kind, string text, string originalText) =>
        throw new NotSupportedException("Text editing is not available.");

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

    /// <summary>The catalog the next launched session filters blocked ids against. A
    /// no-op default, since most fakes never exercise a launched session's plugin allow-list.</summary>
    void SetPluginCatalog(PluginCatalog? catalog)
    {
    }

    /// <summary>Launcher-wide: offers a beta-only plugin in Discover and Add from URL when true. A no-op default, since most fakes never exercise it.</summary>
    void SetShowBetaPlugins(bool value)
    {
    }

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

    /// <summary>Saves a character's launch mode and its own plugin list; null plugins puts it back on
    /// its account's list.</summary>
    void UpdateCharacterSettings(
        string serverName,
        string accountName,
        string characterName,
        LaunchMode launchMode,
        IReadOnlyList<string>? plugins);

    /// <summary>The plugins every character on the account launches with, unless it has its own list.</summary>
    void UpdateAccountPlugins(
        string serverName,
        string accountName,
        IReadOnlyList<string> plugins) =>
        throw new NotSupportedException("Account plugin lists are not available.");

    /// <summary>What loading the profiles could not carry over from an older file, once; null
    /// otherwise.</summary>
    string? TakeProfileMigrationNotice() => null;

    /// <summary>Remembers the character and launch mode an account's row is set to.</summary>
    void UpdateAccountSelection(
        string serverName,
        string accountName,
        string? selectedCharacter,
        LaunchMode selectedLaunchMode);

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

    /// <summary>
    /// Sends one console line to a running windowless session. False when the
    /// session is not running or takes no console input.
    /// </summary>
    bool TrySendConsoleLine(string sessionId, string line) => false;

    /// <summary>
    /// The file a session's console output is written to, or null when the
    /// session is unknown or writes none.
    /// </summary>
    string? GetSessionLogPath(string sessionId) => null;

    Task StopSessionAsync(
        string sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    void PollStatus();

    void ClearFinishedSessions();
}
