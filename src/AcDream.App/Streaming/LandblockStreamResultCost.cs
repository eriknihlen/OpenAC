using System.Runtime.CompilerServices;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Terrain;
using AcDream.Core.World;

namespace AcDream.App.Streaming;

public readonly record struct LandblockStreamCostEstimate(
    StreamingWorkCost Work,
    long TerrainPayloadBytes,
    int Entities,
    int MeshReferences,
    int VisibilityCells,
    int EnvCellShells,
    int PortalConnections,
    int PortalPolygonVertices,
    int PhysicsEnvCells,
    int PhysicsEnvironments,
    int PhysicsSetups,
    int PhysicsGfxObjects);

public static class LandblockStreamResultCost
{
    private const int ReferenceChargeBytes = 8;
    private const int DictionaryEntryChargeBytes = 16;

    public static LandblockStreamCostEstimate Estimate(
        LandblockStreamResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result switch
        {
            LandblockStreamResult.Loaded loaded =>
                Estimate(loaded.Build, loaded.MeshData),
            LandblockStreamResult.Promoted promoted =>
                Estimate(promoted.Build, promoted.MeshData),
            _ => new LandblockStreamCostEstimate(
                new StreamingWorkCost(CompletionAdmissions: 1),
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0),
        };
    }

    public static LandblockStreamCostEstimate Estimate(
        LandblockBuild build,
        LandblockMeshData meshData)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(meshData);

        long terrainBytes = Add(
            Multiply(meshData.Vertices.LongLength, Unsafe.SizeOf<TerrainVertex>()),
            Multiply(meshData.Indices.LongLength, sizeof(uint)));
        long retainedBytes = terrainBytes;

        IReadOnlyList<WorldEntity> entities = build.Landblock.Entities;
        int meshReferences = 0;
        retainedBytes = Add(
            retainedBytes,
            Multiply(entities.Count, ReferenceChargeBytes));
        for (int i = 0; i < entities.Count; i++)
        {
            int count = entities[i].MeshRefs.Count;
            meshReferences = SaturatingAdd(meshReferences, count);
            retainedBytes = Add(
                retainedBytes,
                Multiply(count, Unsafe.SizeOf<MeshRef>()));
        }

        int visibilityCells = 0;
        int shells = 0;
        int portalConnections = 0;
        int portalPolygonVertices = 0;
        if (build.EnvCells is { } envCells)
        {
            visibilityCells = envCells.VisibilityCells.Length;
            shells = envCells.Shells.Length;
            retainedBytes = Add(
                retainedBytes,
                Multiply(
                    (long)visibilityCells + shells,
                    ReferenceChargeBytes));

            foreach (var cell in envCells.VisibilityCells)
            {
                portalConnections = SaturatingAdd(
                    portalConnections,
                    cell.Portals.Count);
                retainedBytes = Add(
                    retainedBytes,
                    Multiply(
                        cell.Portals.Count,
                        Unsafe.SizeOf<Rendering.CellPortalInfo>()));
                retainedBytes = Add(
                    retainedBytes,
                    Multiply(
                        cell.ClipPlanes.Count,
                        Unsafe.SizeOf<Rendering.PortalClipPlane>()));
                retainedBytes = Add(
                    retainedBytes,
                    Multiply(
                        cell.VisibleCells.Count,
                        sizeof(uint)));

                foreach (System.Numerics.Vector3[] polygon in cell.PortalPolygons)
                {
                    portalPolygonVertices = SaturatingAdd(
                        portalPolygonVertices,
                        polygon.Length);
                    retainedBytes = Add(
                        retainedBytes,
                        Multiply(
                            polygon.LongLength,
                            Unsafe.SizeOf<System.Numerics.Vector3>()));
                }
            }

            foreach (EnvCellShellPlacement shell in envCells.Shells)
            {
                retainedBytes = Add(
                    retainedBytes,
                    Multiply(shell.Surfaces.Length, sizeof(ushort)));
            }
        }

        PhysicsDatBundle physics =
            build.Landblock.PhysicsDats ?? PhysicsDatBundle.Empty;
        int physicsEnvCells = physics.EnvCells.Count;
        int physicsEnvironments = physics.Environments.Count;
        int physicsSetups = physics.Setups.Count;
        int physicsGfxObjects = physics.GfxObjs.Count;
        long physicsEntries = (long)physicsEnvCells
            + physicsEnvironments
            + physicsSetups
            + physicsGfxObjects;
        retainedBytes = Add(
            retainedBytes,
            Multiply(physicsEntries, DictionaryEntryChargeBytes));

        return new LandblockStreamCostEstimate(
            new StreamingWorkCost(
                CompletionAdmissions: 1,
                AdoptedCpuBytes: retainedBytes,
                EntityOperations: entities.Count,
                GpuUploadBytes: terrainBytes),
            terrainBytes,
            entities.Count,
            meshReferences,
            visibilityCells,
            shells,
            portalConnections,
            portalPolygonVertices,
            physicsEnvCells,
            physicsEnvironments,
            physicsSetups,
            physicsGfxObjects);
    }

    private static long Multiply(long count, int elementBytes) =>
        count <= 0
            ? 0
            : count > long.MaxValue / elementBytes
                ? long.MaxValue
                : count * elementBytes;

    private static long Add(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static int SaturatingAdd(int left, int right) =>
        left > int.MaxValue - right ? int.MaxValue : left + right;
}
