using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Net;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Update;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Core.World.Cells;

namespace AcDream.App.Streaming;

internal interface IStreamingOriginConvergence
{
    bool Advance();
}

internal interface IStreamingFrameBackend
{
    void Tick(int observerCx, int observerCy, bool insideDungeon = false);
}

internal interface IOfflineStreamingObserverSource
{
    Vector3 Position { get; }
}

internal sealed class FlyCameraStreamingObserverSource
    : IOfflineStreamingObserverSource
{
    private readonly CameraController _camera;

    public FlyCameraStreamingObserverSource(CameraController camera) =>
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));

    public Vector3 Position => _camera.Fly.Position;
}

internal readonly record struct StreamingDungeonCellSnapshot(
    uint CellId,
    bool IsSealedDungeon);

internal interface IStreamingDungeonCellSource
{
    StreamingDungeonCellSnapshot Capture();
}

internal sealed class PhysicsStreamingDungeonCellSource
    : IStreamingDungeonCellSource
{
    private readonly PhysicsEngine _physics;

    public PhysicsStreamingDungeonCellSource(PhysicsEngine physics) =>
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));

    public StreamingDungeonCellSnapshot Capture()
    {
        ObjCell? cell = _physics.DataCache?.CellGraph.CurrCell;
        return new StreamingDungeonCellSnapshot(
            cell?.Id ?? 0u,
            cell is EnvCell envCell && !envCell.SeenOutside);
    }
}

internal interface ILiveProjectionRescueRebucketter
{
    void RebucketAll(int observerCx, int observerCy);
}

internal sealed class LiveProjectionRescueRebucketter
    : ILiveProjectionRescueRebucketter
{
    private readonly GpuWorldState _worldState;
    private readonly LiveEntityRuntime _liveEntities;

    public LiveProjectionRescueRebucketter(
        GpuWorldState worldState,
        LiveEntityRuntime liveEntities)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
    }

    public void RebucketAll(int observerCx, int observerCy)
    {
        List<WorldEntity> rescued = _worldState.DrainRescued();
        if (rescued.Count == 0)
            return;

        uint centerLandblock =
            ((uint)observerCx << 24) | ((uint)observerCy << 16) | 0xFFFFu;
        foreach (WorldEntity entity in rescued)
            _liveEntities.RebucketLiveEntity(entity.ServerGuid, centerLandblock);
    }
}

internal sealed class StreamingFrameController : IStreamingFramePhase
{
    private const float LandblockLength = 192f;

    private readonly bool _liveModeEnabled;
    private readonly ILocalPlayerModeSource _playerMode;
    private readonly IRuntimeLocalPlayerControllerSource _playerController;
    private readonly ILiveInWorldSource _session;
    private readonly LiveWorldOriginState _origin;
    private readonly ILocalPlayerLandblockSource _playerLandblock;
    private readonly IOfflineStreamingObserverSource _offlineObserver;
    private readonly IStreamingDungeonCellSource _dungeonCell;
    private readonly IStreamingOriginConvergence _originConvergence;
    private readonly IStreamingFrameBackend _streaming;
    private readonly ILiveProjectionRescueRebucketter _rescues;

    public StreamingFrameController(
        bool liveModeEnabled,
        ILocalPlayerModeSource playerMode,
        IRuntimeLocalPlayerControllerSource playerController,
        ILiveInWorldSource session,
        LiveWorldOriginState origin,
        ILocalPlayerLandblockSource playerLandblock,
        IOfflineStreamingObserverSource offlineObserver,
        IStreamingDungeonCellSource dungeonCell,
        IStreamingOriginConvergence originConvergence,
        IStreamingFrameBackend streaming,
        ILiveProjectionRescueRebucketter rescues)
    {
        _liveModeEnabled = liveModeEnabled;
        _playerMode = playerMode ?? throw new ArgumentNullException(nameof(playerMode));
        _playerController = playerController
            ?? throw new ArgumentNullException(nameof(playerController));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _playerLandblock = playerLandblock
            ?? throw new ArgumentNullException(nameof(playerLandblock));
        _offlineObserver = offlineObserver
            ?? throw new ArgumentNullException(nameof(offlineObserver));
        _dungeonCell = dungeonCell ?? throw new ArgumentNullException(nameof(dungeonCell));
        _originConvergence = originConvergence
            ?? throw new ArgumentNullException(nameof(originConvergence));
        _streaming = streaming ?? throw new ArgumentNullException(nameof(streaming));
        _rescues = rescues ?? throw new ArgumentNullException(nameof(rescues));
    }

    public void Tick()
    {
        _originConvergence.Advance();

        bool liveInWorld = _session.IsInWorld;
        if (!StreamingReadinessGate.ShouldStream(
                _liveModeEnabled,
                _playerMode.ChaseModeEverEntered,
                liveInWorld,
                _origin.IsKnown))
        {
            return;
        }

        (int observerCx, int observerCy) = SelectObserver(liveInWorld);
        PlayerMovementController? controller = _playerController.Controller;
        bool isTeleportHold = controller is { State: PlayerState.PortalSpace };
        StreamingDungeonCellSnapshot currentCell = _dungeonCell.Capture();
        DungeonGateResult gate = DungeonStreamingGate.Compute(
            isTeleportHold,
            currentCell.IsSealedDungeon,
            currentCell.CellId);
        if (gate.ObserverLandblockKey is { } cellLandblock)
        {
            observerCx = (int)((cellLandblock >> 8) & 0xFFu);
            observerCy = (int)(cellLandblock & 0xFFu);
        }

        _streaming.Tick(observerCx, observerCy, gate.InsideDungeon);
        _rescues.RebucketAll(observerCx, observerCy);
    }

    private (int X, int Y) SelectObserver(bool liveInWorld)
    {
        int observerCx = _origin.CenterX;
        int observerCy = _origin.CenterY;
        PlayerMovementController? controller = _playerController.Controller;

        if (_playerMode.IsPlayerMode
            && controller is { State: PlayerState.PortalSpace })
        {
            return (observerCx, observerCy);
        }

        if (_playerMode.IsPlayerMode && controller is not null)
        {
            Vector3 position = controller.Position;
            return (
                observerCx + (int)Math.Floor(position.X / LandblockLength),
                observerCy + (int)Math.Floor(position.Y / LandblockLength));
        }

        if (liveInWorld)
        {
            if (_playerLandblock.LastKnownLandblockId is { } landblockId)
            {
                return (
                    (int)((landblockId >> 24) & 0xFFu),
                    (int)((landblockId >> 16) & 0xFFu));
            }

            return (observerCx, observerCy);
        }

        Vector3 cameraPosition = _offlineObserver.Position;
        return (
            observerCx + (int)Math.Floor(cameraPosition.X / LandblockLength),
            observerCy + (int)Math.Floor(cameraPosition.Y / LandblockLength));
    }
}
