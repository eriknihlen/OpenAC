using AcDream.App.Update;
using AcDream.Runtime.Session;
using AcDream.App.Input;
using AcDream.App.Net;
using AcDream.App.Streaming;

namespace AcDream.App.World;

internal sealed class RetailLiveFrameCoordinator : IRetailLiveFramePhase
{
    private readonly ILiveObjectFramePhase _objects;
    private readonly GpuWorldState _worldState;
    private readonly IRuntimeLiveSessionFramePhase _session;
    private readonly IPostNetworkCommandFramePhase _localPlayer;
    private readonly ILiveSpatialReconcilePhase _spatialReconciler;
    private readonly IWorldGenerationAvailability _availability;
    private readonly IRenderProjectionSyncPhase? _renderProjectionSync;
    private readonly IRuntimePlacementProjectionRetryPhase?
        _placementProjectionRetry;

    public RetailLiveFrameCoordinator(
        ILiveObjectFramePhase objects,
        GpuWorldState worldState,
        IRuntimeLiveSessionFramePhase session,
        IPostNetworkCommandFramePhase localPlayer,
        ILiveSpatialReconcilePhase spatialReconciler,
        IWorldGenerationAvailability? availability = null,
        IRenderProjectionSyncPhase? renderProjectionSync = null,
        IRuntimePlacementProjectionRetryPhase? placementProjectionRetry = null)
    {
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _localPlayer = localPlayer ?? throw new ArgumentNullException(nameof(localPlayer));
        _spatialReconciler = spatialReconciler
            ?? throw new ArgumentNullException(nameof(spatialReconciler));
        _availability = availability ?? AlwaysAvailableWorldGeneration.Instance;
        _renderProjectionSync = renderProjectionSync;
        _placementProjectionRetry = placementProjectionRetry;
    }

    public void Tick(float deltaSeconds)
    {
        float frameDelta = (float)NormalizeDeltaSeconds(deltaSeconds);
        if (_availability.IsWorldAvailable)
            _objects.Tick(frameDelta);
        using (_worldState.BeginMutationBatch())
        {
            _session.Tick();
            _placementProjectionRetry?.RetryPending();
        }
        _localPlayer.RunPostNetworkCommandPhase();
        if (_availability.IsWorldAvailable)
            _spatialReconciler.Reconcile();
        else
            _renderProjectionSync?.SynchronizeActiveSources();
    }

    public static double NormalizeDeltaSeconds(double deltaSeconds) =>
        UpdateFrameClock.NormalizeDeltaSeconds(deltaSeconds);
}
