using System.Numerics;
using AcDream.App.Input;
using AcDream.Core.Physics;
using AcDream.Core.Rendering;

namespace AcDream.App.Rendering;

internal readonly record struct WorldSceneDiagnosticOutcome(
    int VisibleLandblocks,
    int TotalLandblocks);

internal interface IWorldSceneDiagnostics
{
    CameraCellResolution CameraCellResolution { get; }

    WorldSceneDiagnosticOutcome DrawAndPublish(
        in WorldCameraFrame camera,
        IReadOnlyList<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax)> bounds);
}

internal sealed class WorldSceneDiagnosticsController : IWorldSceneDiagnostics
{
    private readonly IWorldScenePViewDiagnosticSource _pview;
    private readonly IWorldSceneDebugStateSource _state;
    private readonly DebugLineRenderer? _lines;
    private readonly PhysicsEngine _physics;
    private readonly ILocalPlayerModeSource _mode;
    private readonly IRuntimeLocalPlayerControllerSource _player;
    private readonly DebugVmRenderFactsPublisher _debugVm;
    private readonly bool _debugVmConsumerActive;
    private int _debugDrawLogCount;

    public WorldSceneDiagnosticsController(
        IWorldScenePViewDiagnosticSource pview,
        IWorldSceneDebugStateSource state,
        DebugLineRenderer? lines,
        PhysicsEngine physics,
        ILocalPlayerModeSource mode,
        IRuntimeLocalPlayerControllerSource player,
        DebugVmRenderFactsPublisher debugVm,
        bool debugVmConsumerActive)
    {
        _pview = pview ?? throw new ArgumentNullException(nameof(pview));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _lines = lines;
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _debugVm = debugVm ?? throw new ArgumentNullException(nameof(debugVm));
        _debugVmConsumerActive = debugVmConsumerActive;
    }

    public CameraCellResolution CameraCellResolution => _pview.CameraCellResolution;

    public WorldSceneDiagnosticOutcome DrawAndPublish(
        in WorldCameraFrame camera,
        IReadOnlyList<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax)> bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        DrawCollisionWireframes(in camera);

        int visible = 0;
        int total = bounds.Count;
        for (int index = 0; index < bounds.Count; index++)
        {
            var entry = bounds[index];
            if (FrustumCuller.IsAabbVisible(
                    camera.Frustum,
                    entry.AabbMin,
                    entry.AabbMax))
            {
                visible++;
            }
        }

        if (_debugVmConsumerActive)
        {
            Vector3 nearestOrigin =
                _mode.IsPlayerMode && _player.Controller is { } controller
                    ? controller.Position
                    : camera.Position;
            _debugVm.PublishDebugVmFacts(
                consumerActive: true,
                visible,
                total,
                nearestOrigin,
                _physics.ShadowObjects.AllEntriesForDebug());
        }
        return new WorldSceneDiagnosticOutcome(visible, total);
    }

    private void DrawCollisionWireframes(in WorldCameraFrame camera)
    {
        if (!_state.CollisionWireframesVisible || _lines is null)
            return;

        _lines.Begin();
        int drawn = 0;
        foreach (ShadowEntry shadow in _physics.ShadowObjects.AllEntriesForDebug())
        {
            if (shadow.CollisionType == ShadowCollisionType.Cylinder)
            {
                float height = shadow.CylHeight > 0f
                    ? shadow.CylHeight
                    : shadow.Radius * 2f;
                _lines.AddCylinder(
                    shadow.Position,
                    shadow.Radius,
                    height,
                    new Vector3(0f, 1f, 0f));
            }
            else
            {
                _lines.AddCylinder(
                    shadow.Position - new Vector3(0f, 0f, shadow.Radius),
                    shadow.Radius,
                    shadow.Radius * 2f,
                    new Vector3(1f, 0.5f, 0f));
            }
            drawn++;
        }

        if (_mode.IsPlayerMode && _player.Controller is { } localPlayer)
        {
            Vector3 position = localPlayer.Position;
            _lines.AddCylinder(
                new Vector3(position.X, position.Y, position.Z),
                DebugVmRenderFactsPublisher.PlayerCollisionRadius,
                1.8f,
                new Vector3(1f, 0f, 0f));
            LogNearbyCollisionObjects(position, drawn);
        }

        _lines.Flush(camera.Camera.View, camera.Projection);
    }

    private void LogNearbyCollisionObjects(Vector3 playerPosition, int drawn)
    {
        if (_debugDrawLogCount >= 5)
            return;

        Console.WriteLine(
            $"debug frame {_debugDrawLogCount}: player=({playerPosition.X:F1},{playerPosition.Y:F1},{playerPosition.Z:F1}) drew={drawn} "
            + $"totalReg={_physics.ShadowObjects.TotalRegistered}");
        int logged = 0;
        foreach (ShadowEntry shadow in _physics.ShadowObjects.AllEntriesForDebug())
        {
            float dx = shadow.Position.X - playerPosition.X;
            float dy = shadow.Position.Y - playerPosition.Y;
            float horizontalDistance = MathF.Sqrt(dx * dx + dy * dy);
            if (horizontalDistance >= 10f)
                continue;

            Console.WriteLine(
                $"  near id=0x{shadow.EntityId:X8} type={shadow.CollisionType} "
                + $"pos=({shadow.Position.X:F1},{shadow.Position.Y:F1},{shadow.Position.Z:F1}) "
                + $"r={shadow.Radius:F2} h={shadow.CylHeight:F2} dh={horizontalDistance:F2}");
            if (++logged >= 5)
                break;
        }
        _debugDrawLogCount++;
    }
}
