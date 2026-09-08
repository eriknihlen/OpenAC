using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.World;
using AcDream.Core.Physics.Motion;
using DatReaderWriter.Types;

namespace AcDream.App.Input;

internal sealed class LocalPlayerAnimationController
{
    private readonly LiveEntityRuntime _liveEntities;
    private readonly ILocalPlayerIdentitySource _identity;
    private readonly LiveEntityAnimationRuntimeView<LiveEntityAnimationState> _animations;
    private readonly LiveEntityAnimationPresenter _presenter;
    private readonly AnimationHookFrameQueue _hooks;

    public LocalPlayerAnimationController(
        LiveEntityRuntime liveEntities,
        ILocalPlayerIdentitySource identity,
        LiveEntityAnimationRuntimeView<LiveEntityAnimationState> animations,
        LiveEntityAnimationPresenter presenter,
        AnimationHookFrameQueue hooks)
    {
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _animations = animations ?? throw new ArgumentNullException(nameof(animations));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
    }

    public void AdvanceRoot(float deltaSeconds, MotionDeltaFrame output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Reset();

        uint playerGuid = _identity.ServerGuid;
        if (!_liveEntities.TryGetWorldEntity(playerGuid, out var entity)
            || !_animations.TryGetValue(entity.Id, out var animation)
            || animation.Sequencer is not { } sequencer
            || !_liveEntities.ShouldAdvanceRootRuntime(playerGuid)
            || _liveEntities.IsHidden(playerGuid))
        {
            return;
        }

        if (_liveEntities.TryGetRecord(playerGuid, out LiveEntityRecord record))
            _presenter.PrepareAnimation(record, animation);

        Frame rootFrame = animation.RootMotionScratch;
        rootFrame.Origin = System.Numerics.Vector3.Zero;
        rootFrame.Orientation = System.Numerics.Quaternion.Identity;
        animation.PreparedSequenceFrames = animation.CaptureSequenceFrames(
            sequencer.Advance(deltaSeconds, rootFrame));
        animation.SequenceAdvancedBeforeAnimationPass = true;

        output.Origin = rootFrame.Origin;
        output.Orientation = rootFrame.Orientation;
    }

    public void CaptureHooks()
    {
        uint playerGuid = _identity.ServerGuid;
        if (_liveEntities.TryGetWorldEntity(playerGuid, out var entity)
            && _animations.TryGetValue(entity.Id, out var animation)
            && animation.Sequencer is { } sequencer)
        {
            _hooks.Capture(animation.Entity.Id, sequencer);
        }
    }
}
