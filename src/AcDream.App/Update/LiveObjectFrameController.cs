using AcDream.App.Audio;
using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Interaction;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Settings;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.Rendering;
using AcDream.Core.Vfx;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Update;

internal interface ILiveObjectFramePhase
{
    void Tick(float deltaSeconds);
}

internal interface IPostNetworkCommandFramePhase
{
    void RunPostNetworkCommandPhase();
}

internal interface ILiveSpatialReconcilePhase
{
    void Reconcile();
}

/// <summary>
/// Keeps the derived render projection synchronized after inbound mutation
/// while normal world simulation/presentation is blocked by portal reveal.
/// This is deliberately narrower than full spatial reconciliation: hidden
/// frames must not advance particles, effects, attachments, or lights.
/// </summary>
internal interface IRenderProjectionSyncPhase
{
    void SynchronizeActiveSources();
}

internal interface IParticleRangeSource
{
    float RangeMultiplier { get; }
}

internal sealed class SettingsParticleRangeSource : IParticleRangeSource
{
    private readonly IRuntimeSettingsPreviewSource _settings;

    public SettingsParticleRangeSource(IRuntimeSettingsPreviewSource settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public float RangeMultiplier =>
        _settings.DisplayPreview.ParticleRange == ParticleRange.Extended
            ? ParticleVisibilityController.ExtendedRangeMultiplier
            : 1f;
}

internal sealed class LiveEffectFrameController
{
    private readonly TranslucencyFadeManager _translucencyFades;
    private readonly AnimationHookFrameQueue _animationHooks;
    private readonly EntityEffectController _entityEffects;
    private readonly ParticleHookSink _particleSink;
    private readonly LiveEntityLightController _lights;
    private readonly ParticleVisibilityController _particleVisibility;
    private readonly ParticleSystem _particles;
    private readonly PhysicsScriptRunner _scripts;
    private readonly IPhysicsScriptTimeSource _scriptTime;
    private readonly IParticleRangeSource _particleRange;

    public LiveEffectFrameController(
        TranslucencyFadeManager translucencyFades,
        AnimationHookFrameQueue animationHooks,
        EntityEffectController entityEffects,
        ParticleHookSink particleSink,
        LiveEntityLightController lights,
        ParticleVisibilityController particleVisibility,
        ParticleSystem particles,
        PhysicsScriptRunner scripts,
        IPhysicsScriptTimeSource scriptTime,
        IParticleRangeSource particleRange,
        IAmbientFramePhase? ambientFrame = null)
    {
        _translucencyFades = translucencyFades
            ?? throw new ArgumentNullException(nameof(translucencyFades));
        _animationHooks = animationHooks ?? throw new ArgumentNullException(nameof(animationHooks));
        _entityEffects = entityEffects ?? throw new ArgumentNullException(nameof(entityEffects));
        _particleSink = particleSink ?? throw new ArgumentNullException(nameof(particleSink));
        _lights = lights ?? throw new ArgumentNullException(nameof(lights));
        _particleVisibility = particleVisibility
            ?? throw new ArgumentNullException(nameof(particleVisibility));
        _particles = particles ?? throw new ArgumentNullException(nameof(particles));
        _scripts = scripts ?? throw new ArgumentNullException(nameof(scripts));
        _scriptTime = scriptTime ?? throw new ArgumentNullException(nameof(scriptTime));
        _particleRange = particleRange ?? throw new ArgumentNullException(nameof(particleRange));
        _ambientFrame = ambientFrame;
    }

    private readonly IAmbientFramePhase? _ambientFrame;

    public void Tick(float deltaSeconds)
    {
        _translucencyFades.AdvanceAll(deltaSeconds);
        _animationHooks.Drain();
        _entityEffects.RefreshLiveOwnerPoses();
        _particleSink.RefreshAttachedEmitters();
        _lights.Refresh();
        _particleVisibility.Apply(_particles, _particleRange.RangeMultiplier);

        _particles.Tick(deltaSeconds);
        _scripts.Tick(_scriptTime.CurrentScriptTime);
        _ambientFrame?.TickAmbient(deltaSeconds);
    }
}

internal sealed class LiveObjectFrameController : ILiveObjectFramePhase
{
    private readonly RetailInboundEventDispatcher _inboundEvents;
    private readonly RetailLocalPlayerFrameController _localPlayerFrame;
    private readonly SelectionInteractionController? _selectionInteractions;
    private readonly LiveEntityRuntime _liveEntities;
    private readonly ILocalPlayerIdentitySource _localPlayer;
    private readonly LiveWorldOriginState _origin;
    private readonly LiveEntityAnimationScheduler _animations;
    private readonly RetailStaticAnimatingObjectScheduler _staticAnimations;
    private readonly LiveEntityAnimationPresenter _animationPresenter;
    private readonly LiveEntityAnimationRuntimeView<LiveEntityAnimationState>
        _animatedEntities;
    private readonly EquippedChildRenderController _equippedChildren;
    private readonly LiveEffectFrameController _effects;
    private readonly ILiveRenderProjectionSink? _renderProjections;
    private readonly StaticRenderProjectionJournal? _staticRenderProjections;
    private readonly List<WorldEntity> _activeStaticProjectionScratch = [];

    public LiveObjectFrameController(
        RetailInboundEventDispatcher inboundEvents,
        RetailLocalPlayerFrameController localPlayerFrame,
        SelectionInteractionController? selectionInteractions,
        LiveEntityRuntime liveEntities,
        ILocalPlayerIdentitySource localPlayer,
        LiveWorldOriginState origin,
        LiveEntityAnimationScheduler animations,
        RetailStaticAnimatingObjectScheduler staticAnimations,
        LiveEntityAnimationPresenter animationPresenter,
        LiveEntityAnimationRuntimeView<LiveEntityAnimationState> animatedEntities,
        EquippedChildRenderController equippedChildren,
        LiveEffectFrameController effects,
        ILiveRenderProjectionSink? renderProjections = null,
        StaticRenderProjectionJournal? staticRenderProjections = null)
    {
        _inboundEvents = inboundEvents ?? throw new ArgumentNullException(nameof(inboundEvents));
        _localPlayerFrame = localPlayerFrame
            ?? throw new ArgumentNullException(nameof(localPlayerFrame));
        _selectionInteractions = selectionInteractions;
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _localPlayer = localPlayer ?? throw new ArgumentNullException(nameof(localPlayer));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _animations = animations ?? throw new ArgumentNullException(nameof(animations));
        _staticAnimations = staticAnimations
            ?? throw new ArgumentNullException(nameof(staticAnimations));
        _animationPresenter = animationPresenter
            ?? throw new ArgumentNullException(nameof(animationPresenter));
        _animatedEntities = animatedEntities
            ?? throw new ArgumentNullException(nameof(animatedEntities));
        _equippedChildren = equippedChildren
            ?? throw new ArgumentNullException(nameof(equippedChildren));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _renderProjections = renderProjections;
        _staticRenderProjections = staticRenderProjections;
    }

    public void Tick(float deltaSeconds) =>
        _inboundEvents.Run(
            this,
            deltaSeconds,
            static (controller, elapsed) => controller.TickCore(elapsed));

    private void TickCore(float deltaSeconds)
    {
        _localPlayerFrame.AdvanceBeforeNetwork(deltaSeconds);
        _selectionInteractions?.DrainOutbound();

        Vector3? playerPosition =
            _liveEntities.TryGetWorldEntity(
                _localPlayer.ServerGuid,
                out WorldEntity? player)
                ? player.Position
                : null;
        IReadOnlyDictionary<RuntimeEntityKey, LiveEntityAnimationSchedule> schedules =
            _animations.Tick(
                deltaSeconds,
                playerPosition,
                _localPlayerFrame.HiddenPartPoseDirty,
                _origin.CenterX,
                _origin.CenterY,
                _animationPresenter.PrepareAnimation);

        _staticAnimations.Tick(deltaSeconds);
        if (_animatedEntities.Count > 0)
            _animationPresenter.Present(schedules);
        _equippedChildren.Tick();
        _renderProjections?.SynchronizeActiveSources();
        if (_staticRenderProjections is not null)
        {
            _staticAnimations.CopyActiveDatStaticEntitiesTo(
                _activeStaticProjectionScratch);
            _staticRenderProjections.SynchronizeActiveAnimatedSources(
                _activeStaticProjectionScratch);
        }
        _staticAnimations.ProcessHooks();
        _effects.Tick(deltaSeconds);
    }
}

internal sealed class LiveSpatialPresentationReconciler : ILiveSpatialReconcilePhase
{
    private readonly EntityEffectController _entityEffects;
    private readonly EquippedChildRenderController _equippedChildren;
    private readonly ParticleHookSink _particleSink;
    private readonly LiveEntityLightController _lights;
    private readonly ILiveRenderProjectionSink? _renderProjections;

    public LiveSpatialPresentationReconciler(
        EntityEffectController entityEffects,
        EquippedChildRenderController equippedChildren,
        ParticleHookSink particleSink,
        LiveEntityLightController lights,
        ILiveRenderProjectionSink? renderProjections = null)
    {
        _entityEffects = entityEffects ?? throw new ArgumentNullException(nameof(entityEffects));
        _equippedChildren = equippedChildren
            ?? throw new ArgumentNullException(nameof(equippedChildren));
        _particleSink = particleSink ?? throw new ArgumentNullException(nameof(particleSink));
        _lights = lights ?? throw new ArgumentNullException(nameof(lights));
        _renderProjections = renderProjections;
    }

    public void Reconcile()
    {
        _entityEffects.RefreshLiveOwnerPoses();
        _equippedChildren.ReconcileSpatialMutations();
        _renderProjections?.SynchronizeActiveSources();
        _particleSink.RefreshAttachedEmitters();
        _lights.Refresh();
    }
}
