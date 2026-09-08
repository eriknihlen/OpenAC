namespace AcDream.Plugin.Abstractions;

public readonly record struct PluginLoginCharacter(
    uint ObjectId,
    string Name,
    int ActiveIndex,
    bool IsPendingDelete);

public interface ILoginAutomation
{
    bool IsAvailable => false;
    uint NextLoginObjectId => 0u;

    IReadOnlyList<PluginLoginCharacter> CaptureRoster() =>
        Array.Empty<PluginLoginCharacter>();

    bool SetNextLogin(uint characterObjectId) => false;
    bool ClearNextLogin() => false;
}
