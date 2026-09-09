using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Streaming;
using AcDream.Core.Physics;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal interface IWorldSceneSkyStateSource
{
    DayGroupData? ActiveDayGroup { get; }

    int ActiveDayGroupIndex => 0;

    float DayFraction { get; }
}

internal interface IWorldSceneEntitySource
{
    IReadOnlyList<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
        IReadOnlyList<WorldEntity> Entities,
        IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> LandblockEntries
    { get; }

    IReadOnlyList<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax)> LandblockBounds { get; }

    ResidentStreamingWindowFact CaptureResidentStreamingWindow(
        int centerX,
        int centerY) => ResidentStreamingWindowFact.Unavailable(revision: 0);
}

internal sealed class RuntimeWorldSceneEntitySource : IWorldSceneEntitySource
{
    private readonly GpuWorldState _world;

    public RuntimeWorldSceneEntitySource(GpuWorldState world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public IReadOnlyList<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
        IReadOnlyList<WorldEntity> Entities,
        IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> LandblockEntries =>
        _world.LandblockEntries;

    public IReadOnlyList<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax)> LandblockBounds =>
        _world.LandblockBounds;

    public ResidentStreamingWindowFact CaptureResidentStreamingWindow(
        int centerX,
        int centerY) =>
        _world.CaptureResidentStreamingWindow(centerX, centerY);
}

internal interface IWorldScenePViewDiagnosticSource
{
    Vector3 RawPlayerPositionOr(Vector3 fallback);

    float PlayerYaw { get; }

    float? SampleTerrainZ(float worldX, float worldY);

    CameraCellResolution CameraCellResolution { get; }
}

internal sealed class RuntimeWorldScenePViewDiagnosticSource :
    IWorldScenePViewDiagnosticSource
{
    private readonly IRuntimeLocalPlayerControllerSource _player;
    private readonly PhysicsEngine _physics;
    private readonly CellVisibility _visibility;

    public RuntimeWorldScenePViewDiagnosticSource(
        IRuntimeLocalPlayerControllerSource player,
        PhysicsEngine physics,
        CellVisibility visibility)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _visibility = visibility ?? throw new ArgumentNullException(nameof(visibility));
    }

    public Vector3 RawPlayerPositionOr(Vector3 fallback) =>
        _player.Controller?.Position ?? fallback;

    public float PlayerYaw => _player.Controller?.Yaw ?? 0f;

    public float? SampleTerrainZ(float worldX, float worldY) =>
        _physics.SampleTerrainZ(worldX, worldY);

    public CameraCellResolution CameraCellResolution =>
        _visibility.LastCameraCellResolution;
}

internal interface IWorldSceneDebugStateSource
{
    bool CollisionWireframesVisible { get; }
}

internal sealed class WorldSceneDebugState : IWorldSceneDebugStateSource
{
    public bool CollisionWireframesVisible { get; private set; }

    public bool ToggleCollisionWireframes()
    {
        CollisionWireframesVisible = !CollisionWireframesVisible;
        return CollisionWireframesVisible;
    }
}
