using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.Rendering.Wb;

internal sealed class BuildingRegistryPublication
{
    internal BuildingRegistryPublication(
        BuildingInfo[] buildings,
        uint landblockId,
        IReadOnlyDictionary<uint, LoadedCell> cellsByCellId)
    {
        Buildings = buildings;
        LandblockId = landblockId;
        CellsByCellId = cellsByCellId;
        PreparationCommitted = buildings.Length == 0;
    }

    internal BuildingInfo[] Buildings { get; }
    internal uint LandblockId { get; }
    internal IReadOnlyDictionary<uint, LoadedCell> CellsByCellId { get; }
    internal BuildingRegistry Registry { get; } = new();
    internal List<(LoadedCell Cell, uint BuildingId)> CellStamps { get; } = new();
    internal int BuildingCursor { get; set; }
    internal uint NextBuildingId { get; set; } = 1;
    internal bool PreparationCommitted { get; set; }
    internal bool PublicationCommitted { get; set; }
}

public static class BuildingLoader
{
    public static BuildingRegistry Build(
        LandBlockInfo info,
        uint landblockId,
        IReadOnlyDictionary<uint, LoadedCell> cellsByCellId)
    {
        BuildingRegistryPublication publication = PreparePublication(
            info,
            landblockId,
            cellsByCellId);
        while (!AdvancePreparationOne(publication))
        {
        }
        CommitPublication(publication);
        return publication.Registry;
    }

    internal static BuildingRegistryPublication PreparePublication(
        LandBlockInfo info,
        uint landblockId,
        IReadOnlyDictionary<uint, LoadedCell> cellsByCellId)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(cellsByCellId);

        int buildingCount = info.Buildings?.Count ?? 0;
        var buildings = new BuildingInfo[buildingCount];
        for (int index = 0; index < buildingCount; index++)
            buildings[index] = info.Buildings![index];
        return new BuildingRegistryPublication(
            buildings,
            landblockId,
            cellsByCellId);
    }

    internal static bool AdvancePreparationOne(
        BuildingRegistryPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (publication.PreparationCommitted)
            return true;
        if (publication.BuildingCursor >= publication.Buildings.Length)
        {
            publication.PreparationCommitted = true;
            return true;
        }

        AddBuilding(
            publication,
            publication.Buildings[publication.BuildingCursor]);
        publication.BuildingCursor++;
        if (publication.BuildingCursor >= publication.Buildings.Length)
            publication.PreparationCommitted = true;
        return publication.PreparationCommitted;
    }

    internal static void CommitPublication(
        BuildingRegistryPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (!publication.PreparationCommitted)
        {
            throw new InvalidOperationException(
                "A building registry cannot publish before preparation completes.");
        }
        if (publication.PublicationCommitted)
            return;

        foreach ((LoadedCell cell, uint buildingId) in publication.CellStamps)
            cell.BuildingId = buildingId;
        publication.PublicationCommitted = true;
    }

    private static void AddBuilding(
        BuildingRegistryPublication publication,
        BuildingInfo bInfo)
    {
        uint lbMask = publication.LandblockId & 0xFFFF0000u;
        var envCellIds = new HashSet<uint>();
        var exitPortalPolys = new List<Vector3[]>();

        if (bInfo.Portals is not null)
        {
            foreach (var portal in bInfo.Portals)
            {
                if (portal.OtherCellId == 0xFFFF) continue;
                envCellIds.Add(lbMask | portal.OtherCellId);
            }
        }

        // Step B: BFS through interior portals.
        var queue = new Queue<uint>(envCellIds);
        while (queue.Count > 0)
        {
            uint current = queue.Dequeue();
            if (!publication.CellsByCellId.TryGetValue(current, out var cell))
                continue;
            foreach (var portal in cell.Portals)
            {
                if (portal.OtherCellId == 0xFFFF) continue;
                uint neighbourId = lbMask | portal.OtherCellId;
                if (envCellIds.Add(neighbourId))
                    queue.Enqueue(neighbourId);
            }
        }

        foreach (uint cellId in envCellIds)
        {
            if (!publication.CellsByCellId.TryGetValue(cellId, out var cell))
                continue;
            for (int portalIndex = 0; portalIndex < cell.Portals.Count; portalIndex++)
            {
                if (cell.Portals[portalIndex].OtherCellId != 0xFFFF) continue;
                if (portalIndex >= cell.PortalPolygons.Count) continue;
                Vector3[] localPolygon = cell.PortalPolygons[portalIndex];
                if (localPolygon.Length < 3) continue;
                var worldPolygon = new Vector3[localPolygon.Length];
                for (int vertexIndex = 0; vertexIndex < localPolygon.Length; vertexIndex++)
                {
                    worldPolygon[vertexIndex] = Vector3.Transform(
                        localPolygon[vertexIndex],
                        cell.WorldTransform);
                }
                exitPortalPolys.Add(worldPolygon);
            }
        }

        bool hasPortalBounds = false;
        var portalMin = new Vector3(float.MaxValue);
        var portalMax = new Vector3(float.MinValue);
        foreach (Vector3[] polygon in exitPortalPolys)
        {
            foreach (Vector3 vertex in polygon)
            {
                hasPortalBounds = true;
                portalMin = Vector3.Min(portalMin, vertex);
                portalMax = Vector3.Max(portalMax, vertex);
            }
        }

        if (envCellIds.Count == 0)
            return;

        uint buildingId = publication.NextBuildingId++;
        publication.Registry.Add(new Building
        {
            BuildingId = buildingId,
            EnvCellIds = envCellIds,
            ExitPortalPolygons = exitPortalPolys,
            HasPortalBounds = hasPortalBounds,
            PortalBounds = hasPortalBounds
                ? new WbBoundingBox(portalMin, portalMax)
                : default,
        });

        foreach (uint cellId in envCellIds)
        {
            if (publication.CellsByCellId.TryGetValue(cellId, out var cell))
                publication.CellStamps.Add((cell, buildingId));
        }
    }
}
