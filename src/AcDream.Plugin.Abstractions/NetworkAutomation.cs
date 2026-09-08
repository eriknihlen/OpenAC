namespace AcDream.Plugin.Abstractions;

public readonly record struct PluginNetworkClient(
    uint ClientId,
    uint PlayerId,
    string Name,
    string WorldName,
    PluginNavigationPosition Position,
    IReadOnlyList<string> Tags,
    uint CurrentHealth,
    uint CurrentMana,
    uint CurrentStamina,
    uint MaxHealth,
    uint MaxMana,
    uint MaxStamina,
    float Heading);

public interface INetworkAutomation
{
    bool IsAvailable => false;
    IReadOnlyList<PluginNetworkClient> CaptureClients() =>
        Array.Empty<PluginNetworkClient>();
}
