using AcDream.Core.Net;

namespace AcDream.Runtime.Session;

public sealed record LiveSessionCharacterSelector(
    int? ActiveIndex = null,
    uint? CharacterId = null,
    string? CharacterName = null);

public sealed record LiveSessionConnectOptions(
    bool Enabled,
    string Host,
    int Port,
    string User,
    string Password,
    LiveSessionCharacterSelector? Character = null,
    bool Probe = false,
    bool AwaitCharacterSelection = false);

public interface IRuntimeLiveSessionFramePhase
{
    void Tick();
}

public interface ILiveSessionEventRouting : IDisposable
{
    void Attach();
}

public interface ILiveSessionCommandRouting : IDisposable
{
    void Activate();
}
