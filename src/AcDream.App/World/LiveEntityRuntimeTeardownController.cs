using AcDream.App.Interaction;
using AcDream.App.Input;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Physics;
using AcDream.Core.Rendering;
using AcDream.Core.Selection;

namespace AcDream.App.World;

internal interface ILiveEntityTeardownCoordinator
    : ILiveEntityRuntimeComponentLifecycle
{
    void ForgetUnknownOwner(uint serverGuid);
}

internal sealed class LiveEntityRuntimeTeardownController
    : ILiveEntityTeardownCoordinator
{
    private readonly LiveEntityRuntime? _runtime;
    private readonly LiveEntityPresentationController? _presentation;
    private readonly EntityEffectController? _effects;
    private readonly SelectionInteractionController? _interactions;
    private readonly SelectionState? _selection;
    private readonly LiveEntityAnimationRuntimeView<LiveEntityAnimationState>? _animations;
    private readonly RemoteMovementObservationTracker? _remoteMovementObservations;
    private readonly TranslucencyFadeManager? _translucencyFades;
    private readonly LiveEntityProjectionWithdrawalController? _projectionWithdrawal;
    private readonly EquippedChildRenderController? _children;
    private readonly ShadowObjectRegistry? _shadows;
    private readonly LiveEntityLightController? _lights;
    private readonly EntityClassificationCache? _classification;
    private readonly ILocalPlayerIdentitySource? _identity;
    private readonly Func<LiveEntityRecord, LiveEntityTeardownPlan> _createPlan;
    private readonly Action<uint> _forgetUnknownOwner;

    public LiveEntityRuntimeTeardownController(
        LiveEntityRuntime runtime,
        LiveEntityPresentationController presentation,
        EntityEffectController effects,
        SelectionInteractionController? interactions,
        SelectionState selection,
        LiveEntityAnimationRuntimeView<LiveEntityAnimationState> animations,
        RemoteMovementObservationTracker remoteMovementObservations,
        TranslucencyFadeManager translucencyFades,
        LiveEntityProjectionWithdrawalController projectionWithdrawal,
        EquippedChildRenderController children,
        ShadowObjectRegistry shadows,
        LiveEntityLightController lights,
        EntityClassificationCache classification,
        ILocalPlayerIdentitySource identity)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _interactions = interactions;
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _animations = animations ?? throw new ArgumentNullException(nameof(animations));
        _remoteMovementObservations = remoteMovementObservations
            ?? throw new ArgumentNullException(nameof(remoteMovementObservations));
        _translucencyFades = translucencyFades
            ?? throw new ArgumentNullException(nameof(translucencyFades));
        _projectionWithdrawal = projectionWithdrawal
            ?? throw new ArgumentNullException(nameof(projectionWithdrawal));
        _children = children ?? throw new ArgumentNullException(nameof(children));
        _shadows = shadows ?? throw new ArgumentNullException(nameof(shadows));
        _lights = lights ?? throw new ArgumentNullException(nameof(lights));
        _classification = classification
            ?? throw new ArgumentNullException(nameof(classification));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _createPlan = CreatePlan;
        _forgetUnknownOwner = effects.ForgetUnknownOwner;
    }

    internal LiveEntityRuntimeTeardownController(
        Func<LiveEntityRecord, LiveEntityTeardownPlan> createPlan,
        Action<uint> forgetUnknownOwner)
    {
        _createPlan = createPlan ?? throw new ArgumentNullException(nameof(createPlan));
        _forgetUnknownOwner = forgetUnknownOwner
            ?? throw new ArgumentNullException(nameof(forgetUnknownOwner));
    }

    public void TearDown(LiveEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.RuntimeComponentTeardownPlan ??= _createPlan(record);
        record.RuntimeComponentTeardownPlan.Advance();
    }

    public void ForgetUnknownOwner(uint serverGuid) =>
        _forgetUnknownOwner(serverGuid);

    private LiveEntityTeardownPlan CreatePlan(LiveEntityRecord record)
    {
        LiveEntityRuntime runtime = _runtime!;
        uint serverGuid = record.ServerGuid;
        var cleanups = new List<Action>
        {
            () => _presentation!.Forget(record),
            () => _effects!.OnLiveEntityUnregistered(record),
            () =>
            {
                bool replacementExists =
                    runtime.TryGetRecord(serverGuid, out LiveEntityRecord current)
                    && !ReferenceEquals(current, record);
                if (_interactions is { } interactions)
                {
                    interactions.OnEntityRemoved(record, replacementExists);
                }
                else if (!replacementExists
                    && _selection!.SelectedObjectId == serverGuid)
                {
                    _selection.Clear(
                        SelectionChangeSource.System,
                        SelectionChangeReason.SelectedObjectRemoved);
                }
            },
        };

        if (record.WorldEntity is not { } existingEntity)
            return new LiveEntityTeardownPlan(cleanups);

        if (_animations!.TryGetValue(existingEntity.Id, out LiveEntityAnimationState animation))
            cleanups.Add(() => animation.Sequencer?.Manager.HandleExitWorld());
        if (record.RemoteMotionRuntime is RemoteMotion remoteMotion)
            cleanups.Add(remoteMotion.Movement.HandleExitWorld);
        if (record.PhysicsHost is EntityPhysicsHost physicsHost)
        {
            cleanups.Add(physicsHost.PositionManager.UnStick);
            cleanups.Add(physicsHost.ClearTarget);
            cleanups.Add(physicsHost.NotifyExitWorld);
        }

        cleanups.Add(() => _animations.Remove(record));
        cleanups.Add(() => _classification!.InvalidateEntity(existingEntity.Id));
        if (record.ProjectionKey is { } projectionKey)
            cleanups.Add(() => _remoteMovementObservations!.Remove(projectionKey));
        cleanups.Add(() => _translucencyFades!.ClearEntity(existingEntity.Id));
        cleanups.Add(() => _projectionWithdrawal!.LeaveWorld(
            record,
            _identity!.ServerGuid));
        cleanups.Add(() => _children!.OnLogicalTeardown(record));
        cleanups.Add(() => _shadows!.Deregister(existingEntity.Id));
        cleanups.Add(() => _lights!.Forget(record));
        return new LiveEntityTeardownPlan(cleanups);
    }
}
