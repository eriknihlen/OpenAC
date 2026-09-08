namespace AcDream.App.Net;

using AcDream.App.World;
using AcDream.Runtime;
using AcDream.Runtime.Entities;

internal sealed class LiveSessionResetBindings
{
    public required Action MouseCapture { get; init; }
    public required Action PlayerPresentation { get; init; }
    public required Action TeleportPresentation { get; init; }
    public required Action WorldAudio { get; init; }
    public required Action SessionDialogs { get; init; }
    public required Action SettingsCharacterContext { get; init; }
    public required Action EquippedChildren { get; init; }
    public required Action InteractionPresentation { get; init; }
    public required Action SelectionPresentation { get; init; }
    public required Action ParticleVisibility { get; init; }
    public required Action InboundEventFifo { get; init; }
    public required Action LiveLiveness { get; init; }
    public required Action<RuntimeGenerationToken> RuntimeGeneration { get; init; }
    public required Action<RuntimeGenerationToken> SessionIdentityPresentation
        { get; init; }
    public required Action NetworkEffects { get; init; }
    public required Action AnimationHookFrames { get; init; }
    public required Action LivePresentation { get; init; }
    public required Action RemoteMovementDiagnostics { get; init; }
}

internal static class LiveSessionResetManifest
{
    public static LiveSessionResetPlan Create(LiveSessionResetBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        return new LiveSessionResetPlan(
        [
            new("mouse capture", bindings.MouseCapture),
            new("player presentation", bindings.PlayerPresentation),
            new("teleport presentation", bindings.TeleportPresentation),
            new("world audio", bindings.WorldAudio),
            new("session dialogs", bindings.SessionDialogs),
            new("settings character context", bindings.SettingsCharacterContext),
            new("equipped children", bindings.EquippedChildren),
            new("interaction presentation", bindings.InteractionPresentation),
            new("selection presentation", bindings.SelectionPresentation),
            new("particle visibility", bindings.ParticleVisibility),
            new("inbound event fifo", bindings.InboundEventFifo),
            new("live liveness", bindings.LiveLiveness),
            new("runtime generation", bindings.RuntimeGeneration),
            new(
                "session identity presentation",
                bindings.SessionIdentityPresentation),
            new("network effects", bindings.NetworkEffects),
            new("animation hook frames", bindings.AnimationHookFrames),
            new("live presentation", bindings.LivePresentation),
            new("remote movement diagnostics", bindings.RemoteMovementDiagnostics),
        ]);
    }
}

internal sealed class GraphicalRuntimeGenerationResetHost
    : IRuntimeGenerationResetHost
{
    private readonly LiveEntityRuntime _entities;
    private readonly Action _drainRenderProjection;

    public GraphicalRuntimeGenerationResetHost(
        LiveEntityRuntime entities,
        Action drainRenderProjection)
    {
        _entities = entities
            ?? throw new ArgumentNullException(nameof(entities));
        _drainRenderProjection = drainRenderProjection
            ?? throw new ArgumentNullException(nameof(drainRenderProjection));
    }

    public void RetireEntityProjection(
        RuntimeEntityRecord entity) =>
        _entities.RetireGenerationProjection(entity);

    public void DrainEntityProjectionBoundary() =>
        _drainRenderProjection();

    public void CompleteEntityProjectionRetirement() =>
        _entities.CompleteGenerationProjectionRetirement();
}
