using AcDream.App.Update;
using AcDream.Runtime.Session;

namespace AcDream.App.Tests;

internal sealed class TestLiveObjectFramePhase(Action<float> tick)
    : ILiveObjectFramePhase
{
    public void Tick(float deltaSeconds) => tick(deltaSeconds);
}

internal sealed class TestLiveSessionFramePhase(Action tick)
    : IRuntimeLiveSessionFramePhase
{
    public void Tick() => tick();
}

internal sealed class TestPostNetworkCommandFramePhase(Action run)
    : IPostNetworkCommandFramePhase
{
    public void RunPostNetworkCommandPhase() => run();
}

internal sealed class TestLiveSpatialReconcilePhase(Action reconcile)
    : ILiveSpatialReconcilePhase
{
    public void Reconcile() => reconcile();
}

internal sealed class TestRenderProjectionSyncPhase(Action synchronize)
    : IRenderProjectionSyncPhase
{
    public void SynchronizeActiveSources() => synchronize();
}
