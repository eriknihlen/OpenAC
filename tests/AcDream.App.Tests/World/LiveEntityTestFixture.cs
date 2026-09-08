using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.World;
using AcDream.Runtime.Entities;

namespace AcDream.App.World;

internal static class LiveEntityTestFixture
{
    public static LiveEntityRecord CreateExactProjectionRecord(
        WorldSession.EntitySpawn spawn)
    {
        var directory = new RuntimeEntityDirectory();
        RuntimeEntityRecord canonical = directory.AddActive(spawn);
        directory.ClaimLocalId(canonical);
        var projections = new LiveEntityProjectionStore(directory);
        return projections.AddMaterializing(canonical);
    }

    public static LiveEntityRecord RegisterAndMaterializeProjection(
        this LiveEntityRuntime runtime,
        WorldSession.EntitySpawn spawn,
        Func<uint, WorldEntity>? factory = null,
        LiveEntityProjectionKind projectionKind = LiveEntityProjectionKind.World,
        Action<LiveEntityRecord>? initializeProjection = null,
        bool isLocalPlayer = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        LiveEntityRegistrationResult registration =
            runtime.RegisterLiveEntity(spawn, isLocalPlayer);
        RuntimeEntityRecord canonical = registration.Canonical
            ?? throw new InvalidOperationException(
                "The fixture cannot materialize a stale CreateObject.");
        if (registration.Projection is { } existing)
            return existing;

        WorldEntity? entity = runtime.MaterializeLiveEntity(
            canonical,
            spawn.Position?.LandblockId ?? canonical.FullCellId,
            factory ?? (id => CreateWorldEntity(id, spawn)),
            projectionKind,
            initializeProjection,
            out LiveEntityRecord? record);
        if (entity is null || record is null)
        {
            throw new InvalidOperationException(
                "The fixture failed to materialize the exact Runtime incarnation.");
        }

        return record;
    }

    private static WorldEntity CreateWorldEntity(
        uint localEntityId,
        WorldSession.EntitySpawn spawn)
    {
        var position = spawn.Position;
        return new WorldEntity
        {
            Id = localEntityId,
            ServerGuid = spawn.Guid,
            SourceGfxObjOrSetupId = spawn.SetupTableId ?? 0u,
            Position = position is { } placed
                ? new Vector3(
                    placed.PositionX,
                    placed.PositionY,
                    placed.PositionZ)
                : Vector3.Zero,
            Rotation = position is { } oriented
                ? new Quaternion(
                    oriented.RotationX,
                    oriented.RotationY,
                    oriented.RotationZ,
                    oriented.RotationW)
                : Quaternion.Identity,
            MeshRefs = Array.Empty<MeshRef>(),
            ParentCellId = position?.LandblockId,
            EffectCellId = position?.LandblockId,
        };
    }
}
