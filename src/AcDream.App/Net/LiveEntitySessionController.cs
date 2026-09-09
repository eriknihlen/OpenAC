using AcDream.App.Physics;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Session;

namespace AcDream.App.Net;

internal sealed class LiveEntitySessionController
{
    private readonly RetailInboundEventDispatcher _inbound;
    private readonly LiveEntityHydrationController _hydration;
    private readonly LiveEntityNetworkUpdateController _updates;
    private readonly ILocalPlayerTeleportNetworkSink _teleport;
    private readonly EntityEffectController _effects;

    public LiveEntitySessionController(
        RetailInboundEventDispatcher inbound,
        LiveEntityHydrationController hydration,
        LiveEntityNetworkUpdateController updates,
        ILocalPlayerTeleportNetworkSink teleport,
        EntityEffectController effects)
    {
        _inbound = inbound ?? throw new ArgumentNullException(nameof(inbound));
        _hydration = hydration ?? throw new ArgumentNullException(nameof(hydration));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _teleport = teleport ?? throw new ArgumentNullException(nameof(teleport));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
    }

    public LiveEntitySessionSink CreateSink() => new(
        OnSpawned,
        OnDeleted,
        OnPickedUp,
        OnMotionUpdated,
        OnPositionUpdated,
        OnVectorUpdated,
        OnStateUpdated,
        OnParentUpdated,
        OnTeleportStarted,
        OnAppearanceUpdated,
        OnPlayPhysicsScript,
        OnPlayPhysicsScriptType,
        OnSoundEvent);

    private void OnSpawned(WorldSession.EntitySpawn value) =>
        _inbound.Run(_hydration, value,
            static (owner, message) => owner.OnCreate(message));

    private void OnDeleted(DeleteObject.Parsed value) =>
        _inbound.Run(_hydration, value,
            static (owner, message) => owner.OnDelete(message));

    private void OnPickedUp(PickupEvent.Parsed value) =>
        _inbound.Run(_hydration, value,
            static (owner, message) => owner.OnPickup(message));

    private void OnMotionUpdated(WorldSession.EntityMotionUpdate value) =>
        _inbound.Run(_updates, value,
            static (owner, message) => owner.OnMotion(message));

    private void OnPositionUpdated(WorldSession.EntityPositionUpdate value) =>
        _inbound.Run(_updates, value,
            static (owner, message) => owner.OnPosition(message));

    private void OnVectorUpdated(VectorUpdate.Parsed value) =>
        _inbound.Run(_updates, value,
            static (owner, message) => owner.OnVector(message));

    private void OnStateUpdated(SetState.Parsed value) =>
        _inbound.Run(_updates, value,
            static (owner, message) => owner.OnState(message));

    private void OnParentUpdated(ParentEvent.Parsed value) =>
        _inbound.Run(_hydration, value,
            static (owner, message) => owner.OnParent(message));

    private void OnTeleportStarted(uint value) =>
        _inbound.Run(_teleport, value,
            static (owner, message) => owner.OnTeleportStarted(message));

    private void OnAppearanceUpdated(ObjDescEvent.Parsed value) =>
        _inbound.Run(_hydration, value,
            static (owner, message) => owner.OnAppearance(message));

    private void OnPlayPhysicsScript(PlayPhysicsScript value) =>
        _inbound.Run(_effects, value,
            static (owner, message) => owner.HandleDirect(message));

    private void OnPlayPhysicsScriptType(PlayPhysicsScriptType value) =>
        _inbound.Run(_effects, value,
            static (owner, message) => owner.HandleTyped(message));

    private void OnSoundEvent(SoundEvent value) =>
        _inbound.Run(_effects, value,
            static (owner, message) => owner.HandleSound(message));
}
