using AcDream.App.Rendering.Scene;

namespace AcDream.App.Update;

internal sealed class RenderSceneUpdateCommitPhase : IUpdateFrameCommitPhase
{
    private readonly RenderSceneShadowRuntime? _shadow;

    public RenderSceneUpdateCommitPhase(RenderSceneShadowRuntime? shadow) =>
        _shadow = shadow;

    public void Commit() => _shadow?.DrainUpdateBoundary();
}
