using AcDream.App.World;

namespace AcDream.App.Rendering;

internal sealed class LiveEntityPartArrayLifecycle
{
    private readonly LiveEntityAnimationRuntimeView<LiveEntityAnimationState> _animations;

    public LiveEntityPartArrayLifecycle(
        LiveEntityAnimationRuntimeView<LiveEntityAnimationState> animations) =>
        _animations = animations ?? throw new ArgumentNullException(nameof(animations));

    public void HandleEnterWorld(uint localEntityId)
    {
        if (_animations.TryGetValue(localEntityId, out LiveEntityAnimationState? animation))
            animation.Sequencer?.Manager.HandleEnterWorld();
    }
}
