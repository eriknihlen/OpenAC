using AcDream.App.Rendering.Wb;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class ObjectMeshWorkerLifecycleTests
{
    [Fact]
    public void Shutdown_never_resets_the_shared_worker_wake_signal()
    {
        Assert.Equal(
            ObjectMeshManager.PreparationWorkerWakeAction.Exit,
            ObjectMeshManager.DecidePreparationWorkerWake(
                isDisposed: true,
                hasPendingRequests: false,
                stagingAtHighWater: true,
                arenaBackpressured: true));

        Assert.Equal(
            ObjectMeshManager.PreparationWorkerWakeAction.ResetAndWait,
            ObjectMeshManager.DecidePreparationWorkerWake(
                isDisposed: false,
                hasPendingRequests: false,
                stagingAtHighWater: false,
                arenaBackpressured: false));

        Assert.Equal(
            ObjectMeshManager.PreparationWorkerWakeAction.Process,
            ObjectMeshManager.DecidePreparationWorkerWake(
                isDisposed: false,
                hasPendingRequests: true,
                stagingAtHighWater: false,
                arenaBackpressured: false));
    }
}
