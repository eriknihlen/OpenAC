using AcDream.Core.Net;

namespace AcDream.Runtime.Session;

public sealed record LiveSessionLifecycleBindings(
    Func<WorldSession, LiveSessionBinding> Bind,
    Action<RuntimeGenerationToken> Reset,
    Action<string, int, string> Connecting,
    Action Connected,
    Action<LiveSessionRosterReport> Roster,
    Action<LiveSessionCharacterSelection> Selected,
    Action<LiveSessionCharacterSelection> Entered,
    Action<RuntimeCharacterCreationIdentity>? CharacterCreated = null,
    Action<RuntimeCharacterCreationRejection>? CreationFailed = null);

public sealed class LiveSessionLifecycleHost : ILiveSessionLifecycleHost
{
    private readonly LiveSessionLifecycleBindings _bindings;
    private WorldSession? _boundSession;

    public LiveSessionLifecycleHost(LiveSessionLifecycleBindings bindings)
    {
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        ArgumentNullException.ThrowIfNull(bindings.Bind);
        ArgumentNullException.ThrowIfNull(bindings.Reset);
        ArgumentNullException.ThrowIfNull(bindings.Connecting);
        ArgumentNullException.ThrowIfNull(bindings.Connected);
        ArgumentNullException.ThrowIfNull(bindings.Roster);
        ArgumentNullException.ThrowIfNull(bindings.Selected);
        ArgumentNullException.ThrowIfNull(bindings.Entered);
    }

    public LiveSessionBinding BindSession(WorldSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_boundSession is not null)
            throw new InvalidOperationException("A live session is already attached to this host.");

        LiveSessionBinding binding = _bindings.Bind(session);
        _boundSession = session;
        return binding;
    }

    public void ResetSessionState(
        RuntimeGenerationToken retiringGeneration) =>
        _bindings.Reset(retiringGeneration);

    public void ReportConnecting(string host, int port, string user) =>
        _bindings.Connecting(host, port, user);

    public void ReportConnected() => _bindings.Connected();

    public void ReportRoster(LiveSessionRosterReport roster) =>
        _bindings.Roster(roster);

    public void ApplySelectedCharacter(LiveSessionCharacterSelection selection) =>
        _bindings.Selected(selection);

    public void ApplyEnteredWorld(LiveSessionCharacterSelection selection) =>
        _bindings.Entered(selection);

    public void ApplyCharacterCreated(RuntimeCharacterCreationIdentity identity) =>
        _bindings.CharacterCreated?.Invoke(identity);

    public void ApplyCreationFailed(RuntimeCharacterCreationRejection rejection) =>
        _bindings.CreationFailed?.Invoke(rejection);

    public void DetachSession(WorldSession session)
    {
        if (!ReferenceEquals(_boundSession, session))
            throw new InvalidOperationException(
                "The live-session controller attempted to detach a session that is not bound.");
        _boundSession = null;
    }
}
