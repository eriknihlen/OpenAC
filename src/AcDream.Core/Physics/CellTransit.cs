using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.Types;

namespace AcDream.Core.Physics;

public static class CellTransit
{
    private const float EPSILON = 0.02f;

    private const float FEpsilon = 0.000199999995f;

    public static void FindTransitCellsSphere(
        PhysicsDataCache cache,
        CellPhysics currentCell,
        uint currentCellId,
        Vector3 worldSphereCenter,
        float sphereRadius,
        ICollection<uint> candidates,
        out bool exitOutside)
    {
        var spheres = new[]
        {
            new Sphere
            {
                Origin = worldSphereCenter,
                Radius = sphereRadius,
            },
        };

        FindTransitCellsSphere(
            cache, currentCell, currentCellId,
            spheres, spheres.Length, candidates, out exitOutside);
    }

    public static void FindTransitCellsSphere(
        PhysicsDataCache cache,
        CellPhysics currentCell,
        uint currentCellId,
        IReadOnlyList<Sphere> worldSpheres,
        int numSpheres,
        ICollection<uint> candidates,
        out bool exitOutside)
    {
        exitOutside = false;

        uint lbPrefix = currentCellId & 0xFFFF0000u;
        int sphereCount = EffectiveSphereCount(worldSpheres, numSpheres);

        if (sphereCount == 0) return;

        for (int portalIndex = 0;
             portalIndex < currentCell.Portals.Count;
             portalIndex++)
        {
            PortalInfo portal = currentCell.Portals[portalIndex];
            if (!TryGetPortalPlane(
                    currentCell,
                    portalIndex,
                    portal,
                    out Plane portalPlane))
            {
                continue;
            }

            if (portal.OtherCellId == 0xFFFF)
            {
                if (!exitOutside)
                {
                    for (int i = 0; i < sphereCount; i++)
                    {
                        var sphere = worldSpheres[i];
                        float pad = sphere.Radius + FEpsilon;
                        var localCenter = Vector3.Transform(
                            sphere.Origin, currentCell.InverseWorldTransform);
                        float dist =
                            Vector3.Dot(localCenter, portalPlane.Normal) +
                            portalPlane.D;
                        if (dist > -pad && dist < pad)
                        {
                            exitOutside = true;
                            break;
                        }
                    }
                }
                continue;
            }

            uint otherId = lbPrefix | portal.OtherCellId;

            RecordUnionOnlyProbe(candidates, otherId);
            var otherCell = cache.GetCellStruct(otherId);
            if (otherCell is not null &&
                CollisionTraversal.HasCellContainment(cache, otherCell))
            {
                for (int i = 0; i < sphereCount; i++)
                {
                    var sphere = worldSpheres[i];
                    var otherLocalCenter = Vector3.Transform(
                        sphere.Origin, otherCell.InverseWorldTransform);
                    bool hit = CollisionTraversal.SphereIntersectsCell(
                        cache,
                        otherCell,
                        otherLocalCenter,
                        sphere.Radius);
                    if (hit)
                    {
                        candidates.Add(otherId);
                        break;
                    }
                }

                continue;
            }

            for (int i = 0; i < sphereCount; i++)
            {
                var sphere = worldSpheres[i];
                float rad = sphere.Radius + EPSILON;
                var localCenter = Vector3.Transform(
                    sphere.Origin, currentCell.InverseWorldTransform);
                float dist =
                    Vector3.Dot(localCenter, portalPlane.Normal) +
                    portalPlane.D;
                bool hit = portal.PortalSide ? dist > -rad : dist < rad;
                if (hit)
                {
                    candidates.Add(otherId);
                    break;
                }
            }
        }
    }

    public static void FindTransitCellsBox(
        PhysicsDataCache cache,
        CellPhysics currentCell,
        uint currentCellId,
        IReadOnlyList<ShadowPartBox> worldParts,
        IReadOnlyList<Sphere> worldPartSpheres,
        ICollection<uint> candidates,
        out bool exitOutside)
    {
        exitOutside = false;

        int partCount = Math.Min(worldParts.Count, worldPartSpheres.Count);
        if (partCount == 0) return;

        uint lbPrefix = currentCellId & 0xFFFF0000u;

        for (int portalIndex = 0;
             portalIndex < currentCell.Portals.Count;
             portalIndex++)
        {
            PortalInfo portal = currentCell.Portals[portalIndex];
            if (!TryGetPortalPlane(
                    currentCell,
                    portalIndex,
                    portal,
                    out Plane portalPlane))
            {
                continue;
            }

            for (int i = 0; i < partCount; i++)
            {
                Sphere sphere = worldPartSpheres[i];

                float rad = sphere.Radius + FEpsilon;
                var localCenter = Vector3.Transform(
                    sphere.Origin, currentCell.InverseWorldTransform);
                float dist =
                    Vector3.Dot(localCenter, portalPlane.Normal) +
                    portalPlane.D;
                bool passesCheapReject = portal.PortalSide
                    ? dist > -rad
                    : dist < rad;
                if (!passesCheapReject)
                    continue;

                // --- box admit -----------------------------------------------
                ShadowPartBox partBox = worldParts[i];
                partBox.RefitToLocal(
                    currentCell.InverseWorldTransform,
                    out Vector3 localBoxMin,
                    out Vector3 localBoxMax);
                BSPQuery.PlaneSide sidedness =
                    BSPQuery.ClassifyBox(portalPlane, localBoxMin, localBoxMax);

                BSPQuery.PlaneSide crossingSide = portal.PortalSide
                    ? BSPQuery.PlaneSide.Positive
                    : BSPQuery.PlaneSide.Negative;
                bool crosses =
                    sidedness == BSPQuery.PlaneSide.Straddle ||
                    sidedness == crossingSide;
                if (!crosses)
                    continue;

                if (portal.OtherCellId == 0xFFFF)
                {
                    exitOutside = true;
                    break;   // next portal
                }

                uint otherId = lbPrefix | portal.OtherCellId;
                RecordUnionOnlyProbe(candidates, otherId);
                var otherCell = cache.GetCellStruct(otherId);
                if (otherCell is null ||
                    !CollisionTraversal.HasCellContainment(cache, otherCell))
                {
                    candidates.Add(otherId);
                    break;   // next portal
                }

                partBox.RefitToLocal(
                    otherCell.InverseWorldTransform,
                    out Vector3 destBoxMin,
                    out Vector3 destBoxMax);
                if (CollisionTraversal.BoxIntersectsCell(
                        cache, otherCell, destBoxMin, destBoxMax))
                {
                    candidates.Add(otherId);
                    break;   // next portal
                }

            }
        }
    }

    private static bool TryGetPortalPlane(
        CellPhysics cell,
        int portalIndex,
        PortalInfo portal,
        out Plane plane)
    {
        if (cell.PortalPolygons is not null &&
            cell.PortalPolygons.TryGetValue(
                portal.PolygonId,
                out ResolvedPolygon? polygon))
        {
            plane = polygon.Plane;
            return true;
        }

        FlatEnvCellTopology? topology = cell.FlatTopology;
        FlatPolygonTable? polygonTable = cell.FlatPortalPolygons;
        if (topology is null || polygonTable is null)
        {
            plane = default;
            return false;
        }

        if ((uint)portalIndex >= (uint)topology.Portals.Length)
        {
            throw new InvalidDataException(
                $"Cell 0x{cell.SourceId:X8} portal {portalIndex} is absent " +
                "from its prepared topology.");
        }

        FlatEnvCellPortal flatPortal = topology.Portals[portalIndex];
        if (flatPortal.OtherCellId != portal.OtherCellId ||
            flatPortal.PolygonId != portal.PolygonId ||
            flatPortal.Flags != portal.Flags)
        {
            throw new InvalidDataException(
                $"Cell 0x{cell.SourceId:X8} portal {portalIndex} does not " +
                "match its prepared topology.");
        }

        int polygonIndex = flatPortal.PolygonIndex;
        if ((uint)polygonIndex >= (uint)polygonTable.Polygons.Length)
        {
            throw new InvalidDataException(
                $"Cell 0x{cell.SourceId:X8} portal {portalIndex} references " +
                $"invalid prepared polygon index {polygonIndex}.");
        }

        plane = polygonTable.Polygons[polygonIndex].Plane;
        return true;
    }

    public static bool AddAllOutsideCells(
        Vector3 worldSphereCenter,
        float sphereRadius,
        uint currentCellId,
        Vector3 currentBlockOrigin,
        ICollection<uint> candidates)
    {
        var center = worldSphereCenter - currentBlockOrigin;

        uint cellId = currentCellId;
        if (!LandDefs.AdjustToOutside(ref cellId, ref center))
            return false;
        if (!LandDefs.GidToLcoord(cellId, out int lx, out int ly))
            return false;

        AddOutsideCell(candidates, lx, ly);

        float pointX = center.X - MathF.Floor(center.X / LandDefs.CellLength) * LandDefs.CellLength;
        float pointY = center.Y - MathF.Floor(center.Y / LandDefs.CellLength) * LandDefs.CellLength;
        float minRad = sphereRadius;
        float maxRad = LandDefs.CellLength - sphereRadius;

        if (pointX > maxRad)
        {
            AddOutsideCell(candidates, lx + 1, ly);
            if (pointY > maxRad) AddOutsideCell(candidates, lx + 1, ly + 1);
            if (pointY < minRad) AddOutsideCell(candidates, lx + 1, ly - 1);
        }
        if (pointX < minRad)
        {
            AddOutsideCell(candidates, lx - 1, ly);
            if (pointY > maxRad) AddOutsideCell(candidates, lx - 1, ly + 1);
            if (pointY < minRad) AddOutsideCell(candidates, lx - 1, ly - 1);
        }
        if (pointY > maxRad) AddOutsideCell(candidates, lx, ly + 1);
        if (pointY < minRad) AddOutsideCell(candidates, lx, ly - 1);
        return true;
    }

    public static void AddAllOutsideCells(
        IReadOnlyList<Sphere> worldSpheres,
        int numSpheres,
        uint currentCellId,
        Vector3 currentBlockOrigin,
        ICollection<uint> candidates)
    {
        int sphereCount = EffectiveSphereCount(worldSpheres, numSpheres);
        for (int i = 0; i < sphereCount; i++)
        {
            var sphere = worldSpheres[i];
            if (!AddAllOutsideCells(sphere.Origin, sphere.Radius, currentCellId, currentBlockOrigin, candidates))
                break;
        }
    }

    public static bool AddAllOutsideCellsFromParts(
        IReadOnlyList<ShadowPartBox> worldParts,
        uint currentCellId,
        Vector3 currentBlockOrigin,
        ICollection<uint> candidates)
    {
        if (worldParts is null)
            return false;

        Vector3 seedFramePos = worldParts[0].WorldPosition - currentBlockOrigin;
        Vector3 baseFramePos = seedFramePos;
        uint baseCellId = currentCellId;
        if (!LandDefs.AdjustToOutside(ref baseCellId, ref baseFramePos))
            return false;                                   // gid 0 → get_landcell null → return
        if (!LandDefs.GidToLcoord(baseCellId, out int gx, out int gy))
            return false;

        int baseX = (int)(((baseCellId & 0xFFFFu) - 1u) >> 3);
        int baseY = (int)((baseCellId - 1u) & 7u);

        Vector3 frameOrigin =
            currentBlockOrigin - (baseFramePos - seedFramePos);

        int minDX = 0, minDY = 0, maxDX = 0, maxDY = 0;

        for (int i = 0; i < worldParts.Count; i++)
        {
            worldParts[i].RefitTo(frameOrigin, out Vector3 boxMin, out Vector3 boxMax);

            int a = (int)MathF.Floor(boxMin.X / LandDefs.CellLength);
            int b = (int)MathF.Floor(boxMin.Y / LandDefs.CellLength);
            int c = (int)MathF.Floor(boxMax.X / LandDefs.CellLength);
            int d = (int)MathF.Floor(boxMax.Y / LandDefs.CellLength);

            if (a - baseX < minDX) minDX = a - baseX;
            if (b - baseY < minDY) minDY = b - baseY;
            if (c - baseX > maxDX) maxDX = c - baseX;
            if (d - baseY > maxDY) maxDY = d - baseY;
        }

        AddCellBlock(gx + minDX, gy + minDY, gx + maxDX, gy + maxDY, candidates);
        return true;
    }

    private static void AddCellBlock(
        int x0, int y0, int x1, int y1,
        ICollection<uint> candidates)
    {
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                AddOutsideCell(candidates, x, y);
    }

    private static void AddOutsideCell(ICollection<uint> candidates, int lx, int ly)
    {
        uint gid = LandDefs.LcoordToGid(lx, ly);
        if (gid != 0u) candidates.Add(gid);
    }

    public static void CheckBuildingTransit(
        PhysicsDataCache cache,
        BuildingPhysics building,
        Vector3 worldSphereCenter,
        float sphereRadius,
        ICollection<uint> candidates)
        => CheckBuildingTransit(
            cache, building,
            new[] { new Sphere { Origin = worldSphereCenter, Radius = sphereRadius } },
            1, candidates, out _);

    public static void CheckBuildingTransit(
        PhysicsDataCache cache,
        BuildingPhysics building,
        IReadOnlyList<Sphere> worldSpheres,
        int numSpheres,
        ICollection<uint> candidates,
        out bool hitsInteriorCell)
    {
        hitsInteriorCell = false;
        int sphereCount = EffectiveSphereCount(worldSpheres, numSpheres);
        if (sphereCount == 0) return;

        foreach (var portal in building.Portals)
        {
            if (portal.OtherPortalId < 0)
                continue;

            RecordUnionOnlyProbe(candidates, portal.OtherCellId);
            var otherCell = cache.GetCellStruct(portal.OtherCellId);
            if (otherCell is null ||
                !CollisionTraversal.HasCellContainment(cache, otherCell))
            {
                if (PhysicsDiagnostics.ProbeIndoorBspEnabled)
                {
                    string reason = otherCell is null ? "cell not cached" : "CellBSP null";
                    Console.WriteLine(System.FormattableString.Invariant(
                        $"[check-bldg] portal->0x{portal.OtherCellId:X8} skipped: {reason}"));
                }
                continue;
            }

            bool inside = false;
            for (int i = 0; i < sphereCount && !inside; i++)
            {
                var sphere = worldSpheres[i];
                var localCenter = Vector3.Transform(sphere.Origin, otherCell.InverseWorldTransform);
                inside = CollisionTraversal.SphereIntersectsCell(
                    cache,
                    otherCell,
                    localCenter,
                    sphere.Radius);

                if (PhysicsDiagnostics.ProbeIndoorBspEnabled)
                {
                    Console.WriteLine(System.FormattableString.Invariant(
                        $"[check-bldg] portal->0x{portal.OtherCellId:X8} sphere#{i} wpos=({sphere.Origin.X:F3},{sphere.Origin.Y:F3},{sphere.Origin.Z:F3}) lpos=({localCenter.X:F3},{localCenter.Y:F3},{localCenter.Z:F3}) r={sphere.Radius:F3} inside={inside}"));
                }
            }

            if (inside)
            {
                hitsInteriorCell = true;
                candidates.Add(portal.OtherCellId);
            }
        }
    }

    public static IReadOnlyList<uint> BuildShadowCellSet(
        PhysicsDataCache cache,
        uint seedCellId,
        IReadOnlyList<Sphere> worldSpheres,
        int numSpheres,
        bool isStatic)
    {
        var candidates = new CellArray();
        int sphereCount = EffectiveSphereCount(worldSpheres, numSpheres);
        if (seedCellId == 0 || sphereCount == 0)
            return candidates.OrderedIds;

        uint seedLow = seedCellId & 0xFFFFu;
        cache.CellGraph.TryGetTerrainOrigin(seedCellId, out var blockOrigin);

        bool seedLoaded;
        if (seedLow >= 0x0100u)
        {
            candidates.Add(seedCellId);
            seedLoaded = cache.GetCellStruct(seedCellId) is not null;
        }
        else
        {
            AddAllOutsideCells(worldSpheres, sphereCount, seedCellId, blockOrigin, candidates);
            seedLoaded = cache.CellGraph.GetVisible(seedCellId) is not null;
        }

        if (seedLoaded)
        {
            bool outdoorAdded = seedLow < 0x0100u;
            for (int i = 0; i < candidates.Count; i++)
            {
                uint cellId = candidates.OrderedIds[i];
                if ((cellId & 0xFFFFu) >= 0x0100u)
                {
                    var cell = cache.GetCellStruct(cellId);
                    if (cell is null) continue;

                    FindTransitCellsSphere(
                        cache, cell, cellId, worldSpheres, sphereCount,
                        candidates, out bool exitStraddle);

                    if (exitStraddle && !outdoorAdded)
                    {
                        AddAllOutsideCells(worldSpheres, sphereCount, seedCellId, blockOrigin, candidates);
                        outdoorAdded = true;
                    }
                }
                else
                {
                    if (cache.CellGraph.GetVisible(cellId) is null)
                        continue;

                    if (!outdoorAdded)
                    {
                        AddAllOutsideCells(worldSpheres, sphereCount, seedCellId, blockOrigin, candidates);
                        outdoorAdded = true;
                    }

                    var building = cache.GetBuilding(cellId);
                    if (building is not null)
                        CheckBuildingTransit(cache, building, worldSpheres, sphereCount, candidates, out _);
                }
            }

            // Static prune (do_not_load_cells, 0052b66e): indoor-seeded
            // statics keep only {seed} ∪ seed.stab_list.
            if (isStatic && seedLow >= 0x0100u)
            {
                var seedCell = cache.GetCellStruct(seedCellId);
                if (seedCell is not null)
                {
                    var keep = new List<uint>(candidates.Count);
                    foreach (uint id in candidates.OrderedIds)
                    {
                        if (id == seedCellId || seedCell.VisibleCellIds.Contains(id))
                            keep.Add(id);
                    }
                    if (keep.Count != candidates.Count)
                    {
                        candidates.Clear();
                        foreach (uint id in keep) candidates.Add(id);
                    }
                }
            }
        }

        return candidates.OrderedIds;
    }

    private static void CheckBuildingTransitFromParts(
        PhysicsDataCache cache,
        BuildingPhysics building,
        IReadOnlyList<ShadowPartBox> worldParts,
        IReadOnlyList<Sphere> worldPartSpheres,
        ICollection<uint> candidates,
        uint seedCellId,
        Vector3 blockOrigin,
        ref bool outdoorAdded)
    {
        int partCount = Math.Min(worldParts.Count, worldPartSpheres.Count);
        if (partCount == 0) return;

        foreach (BldPortalInfo buildingPortal in building.Portals)
        {
            int reciprocalIndex = buildingPortal.OtherPortalId;
            if (reciprocalIndex < 0) continue;

            RecordUnionOnlyProbe(candidates, buildingPortal.OtherCellId);
            CellPhysics? destination = cache.GetCellStruct(buildingPortal.OtherCellId);
            if (destination is null || !CollisionTraversal.HasCellContainment(cache, destination))
                continue;

            if (reciprocalIndex >= destination.Portals.Count)
                throw new InvalidDataException(
                    $"Building portal to cell 0x{buildingPortal.OtherCellId:X8} references reciprocal portal {reciprocalIndex}, but the destination has {destination.Portals.Count} portals.");

            PortalInfo reciprocal = destination.Portals[reciprocalIndex];
            if (!TryGetPortalPlane(destination, reciprocalIndex, reciprocal, out Plane plane))
                continue;

            for (int i = 0; i < partCount; i++)
            {
                Sphere sphere = worldPartSpheres[i];
                float paddedRadius = sphere.Radius + FEpsilon;
                Vector3 center = Vector3.Transform(sphere.Origin, destination.InverseWorldTransform);
                float distance = Vector3.Dot(center, plane.Normal) + plane.D;
                // Inclusive BUILDING gate: opposite direction to the indoor exit gate.
                bool passesCheapReject = reciprocal.PortalSide
                    ? distance <= paddedRadius
                    : distance >= -paddedRadius;
                if (!passesCheapReject) continue;

                worldParts[i].RefitToLocal(destination.InverseWorldTransform, out Vector3 min, out Vector3 max);
                BSPQuery.PlaneSide side = BSPQuery.ClassifyBox(plane, min, max);
                BSPQuery.PlaneSide sameSide = reciprocal.PortalSide
                    ? BSPQuery.PlaneSide.Negative : BSPQuery.PlaneSide.Positive;
                if (side != BSPQuery.PlaneSide.Straddle && side != sameSide)
                    continue;
                if (!CollisionTraversal.BoxIntersectsCell(cache, destination, min, max))
                    continue;

                candidates.Add(buildingPortal.OtherCellId);
                FindTransitCellsBox(cache, destination, buildingPortal.OtherCellId,
                    worldParts, worldPartSpheres, candidates, out bool exitOutside);
                if (exitOutside && !outdoorAdded)
                    outdoorAdded = AddAllOutsideCellsFromParts(worldParts, seedCellId, blockOrigin, candidates);
                break;
            }
        }
    }

    public static IReadOnlyList<uint> BuildShadowCellSetFromParts(
        PhysicsDataCache cache,
        uint seedCellId,
        IReadOnlyList<ShadowPartBox> worldParts,
        IReadOnlyList<Sphere> worldPartSpheres,
        bool isStatic)
    {
        var candidates = new CellArray();
        if (seedCellId == 0u || worldParts is null || worldParts.Count == 0)
            return candidates.OrderedIds;

        int sphereCount =
            EffectiveSphereCount(worldPartSpheres, worldPartSpheres?.Count ?? 0);

        uint seedLow = seedCellId & 0xFFFFu;
        cache.CellGraph.TryGetTerrainOrigin(seedCellId, out var blockOrigin);

        bool outdoorAdded = false;
        bool seedLoaded;
        if (seedLow >= 0x0100u)
        {
            candidates.Add(seedCellId);
            seedLoaded = cache.GetCellStruct(seedCellId) is not null;
        }
        else
        {
            candidates.Add(seedCellId);
            outdoorAdded = AddAllOutsideCellsFromParts(
                worldParts, seedCellId, blockOrigin, candidates);
            seedLoaded = cache.CellGraph.GetVisible(seedCellId) is not null;
        }

        if (!seedLoaded)
            return candidates.OrderedIds;

        for (int i = 0; i < candidates.Count; i++)
        {
            uint cellId = candidates.OrderedIds[i];
            if ((cellId & 0xFFFFu) >= 0x0100u)
            {
                var cell = cache.GetCellStruct(cellId);
                if (cell is null) continue;

                if (sphereCount == 0 || worldParts.Count == 0) continue;
                FindTransitCellsBox(
                    cache, cell, cellId, worldParts, worldPartSpheres!,
                    candidates, out bool exitStraddle);

                if (exitStraddle && !outdoorAdded)
                {
                    outdoorAdded = AddAllOutsideCellsFromParts(
                        worldParts, seedCellId, blockOrigin, candidates);
                }
            }
            else
            {
                if (cache.CellGraph.GetVisible(cellId) is null)
                    continue;

                if (!outdoorAdded)
                {
                    outdoorAdded = AddAllOutsideCellsFromParts(
                        worldParts, seedCellId, blockOrigin, candidates);
                }

                var building = cache.GetBuilding(cellId);
                if (building is not null && sphereCount > 0)
                {
                    CheckBuildingTransitFromParts(
                        cache, building, worldParts, worldPartSpheres!,
                        candidates, seedCellId, blockOrigin, ref outdoorAdded);
                }
            }
        }


        return candidates.OrderedIds;
    }

    public static uint FindVisibleChildCell(
        PhysicsDataCache cache,
        uint startCellId,
        Vector3 worldPoint,
        bool useStabList,
        ICollection<uint>? probedCells = null)
    {
        probedCells?.Add(startCellId);
        var start = cache.GetCellStruct(startCellId);
        if (start is null) return 0u;

        // this->point_in_cell(point) → return this  (:311402-311405)
        if (PointInCell(cache, start, worldPoint)) return startCellId;

        if (useStabList)
        {
            // arg3 != 0 → iterate stab_list, GetVisible + point_in_cell (:311444-311465)
            foreach (uint id in start.VisibleCellIds)
            {
                probedCells?.Add(id);
                if (PointInCell(cache, cache.GetCellStruct(id), worldPoint)) return id;
            }
        }
        else
        {
            // arg3 == 0 → iterate direct portals, GetOtherCell + point_in_cell (:311411-311434)
            foreach (var portal in start.Portals)
            {
                probedCells?.Add(portal.OtherCellId);
                if (PointInCell(
                        cache,
                        cache.GetCellStruct(portal.OtherCellId),
                        worldPoint))
                {
                    return portal.OtherCellId;
                }
            }
        }

        return 0u;
    }

    private static bool PointInCell(
        PhysicsDataCache cache,
        CellPhysics? cell,
        Vector3 worldPoint)
    {
        if (cell is null ||
            cell.Portals.Count == 0 ||
            !CollisionTraversal.HasCellContainment(cache, cell))
        {
            return false;
        }

        var local = Vector3.Transform(worldPoint, cell.InverseWorldTransform);
        return CollisionTraversal.PointInsideCell(cache, cell, local);
    }

    public static uint FindCellList(
        PhysicsDataCache cache,
        Vector3 worldSphereCenter,
        float sphereRadius,
        uint currentCellId)
    {
        return FindCellSet(cache, worldSphereCenter, sphereRadius, currentCellId, out _);
    }

    public static uint FindCellSet(
        PhysicsDataCache cache,
        Vector3 worldSphereCenter,
        float sphereRadius,
        uint currentCellId,
        out IReadOnlyCollection<uint> cellSet,
        Vector3? carriedBlockOrigin = null)
    {
        var spheres = new[]
        {
            new Sphere
            {
                Origin = worldSphereCenter,
                Radius = sphereRadius,
            },
        };

        return FindCellSet(cache, spheres, spheres.Length, currentCellId, out cellSet, carriedBlockOrigin);
    }

    public static uint FindCellSet(
        PhysicsDataCache cache,
        IReadOnlyList<Sphere> worldSpheres,
        int numSpheres,
        uint currentCellId,
        out IReadOnlyCollection<uint> cellSet,
        Vector3? carriedBlockOrigin = null)
    {
        var candidates = new CellArray();
        var containing = BuildCellSetAndPickContaining(
            cache, worldSpheres, numSpheres, currentCellId,
            carriedBlockOrigin, candidates);
        cellSet = candidates;
        return containing;
    }

    internal static uint FindCellSet(
        PhysicsDataCache cache,
        IReadOnlyList<Sphere> worldSpheres,
        int numSpheres,
        uint currentCellId,
        CellArray candidates,
        Vector3? carriedBlockOrigin = null)
        => BuildCellSetAndPickContaining(
            cache, worldSpheres, numSpheres, currentCellId,
            carriedBlockOrigin, candidates);

    private static uint BuildCellSetAndPickContaining(
        PhysicsDataCache cache,
        IReadOnlyList<Sphere> worldSpheres,
        int numSpheres,
        uint currentCellId,
        Vector3? carriedBlockOrigin,
        CellArray candidates)
    {
        candidates.Clear();
        int sphereCount = EffectiveSphereCount(worldSpheres, numSpheres);
        if (sphereCount == 0) return currentCellId;

        Vector3 worldSphereCenter = worldSpheres[0].Origin;
        float sphereRadius = worldSpheres[0].Radius;
        uint currentLow = currentCellId & 0xFFFFu;

        Vector3 blockOrigin;
        if (carriedBlockOrigin is { } carriedAnchor)
        {
            blockOrigin = carriedAnchor;
        }
        else
        {
            bool terrainResident = cache.CellGraph.TryGetTerrainOrigin(currentCellId, out blockOrigin);
            if (!terrainResident && currentLow < 0x0100u)
                return currentCellId;
        }

        bool outdoorPickAllowed = currentLow < 0x0100u;

        bool outdoorAdded;
        if (currentLow >= 0x0100u)
        {
            var currentCell = cache.GetCellStruct(currentCellId);
            if (currentCell is null) return currentCellId;
            candidates.Add(currentCellId);
            outdoorAdded = false;
        }
        else
        {
            AddAllOutsideCells(worldSpheres, sphereCount, currentCellId, blockOrigin, candidates);
            outdoorAdded = true;
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            uint cellId = candidates.OrderedIds[i];

            if ((cellId & 0xFFFFu) < 0x0100u)
            {
                if (cache.CellGraph.GetVisible(cellId) is null)
                    continue;
                var building = cache.GetBuilding(cellId);
                if (building is null) continue;
                CheckBuildingTransit(cache, building, worldSpheres, sphereCount, candidates, out _);
                continue;
            }

            var cell = cache.GetCellStruct(cellId);
            if (cell is null) continue;

            FindTransitCellsSphere(
                cache, cell, cellId, worldSpheres, sphereCount,
                candidates, out bool exitOutsideStraddle);

            outdoorPickAllowed |= exitOutsideStraddle;

            if (exitOutsideStraddle && !outdoorAdded)
            {
                AddAllOutsideCells(worldSpheres, sphereCount, currentCellId, blockOrigin, candidates);
                outdoorAdded = true;
            }
        }

        if (PhysicsDiagnostics.ProbeCellSetEnabled)
            PhysicsDiagnostics.LogCellSetBuild(currentCellId, worldSphereCenter, candidates);

        uint containingOutdoorId = 0u;
        {
            var pickPos = worldSphereCenter - blockOrigin;
            uint pickCell = currentCellId;
            if (LandDefs.AdjustToOutside(ref pickCell, ref pickPos))
                containingOutdoorId = pickCell;
        }

        uint outdoorResult = 0u;
        foreach (uint candId in candidates.OrderedIds)
        {
            if ((candId & 0xFFFFu) >= 0x0100u)
            {
                var cand = cache.GetCellStruct(candId);
                if (PointInCell(cache, cand, worldSphereCenter))
                    return candId;
            }
            else if (outdoorResult == 0u &&
                     containingOutdoorId != 0u &&
                     outdoorPickAllowed &&
                     cache.CellGraph.GetVisible(candId) is not null)
            {
                if (candId == containingOutdoorId)
                    outdoorResult = candId;
            }
        }

        if (outdoorResult != 0u) return outdoorResult;

        if (currentLow >= 0x0100u)
        {
            var cur = cache.GetCellStruct(currentCellId);
            if (cur is not null &&
                CollisionTraversal.HasCellContainment(cache, cur))
            {
                var curLocal = Vector3.Transform(worldSphereCenter, cur.InverseWorldTransform);
                if (!CollisionTraversal.SphereIntersectsCell(
                        cache,
                        cur,
                        curLocal,
                        sphereRadius))
                {
                    uint recovered = FindVisibleChildCell(
                        cache,
                        currentCellId,
                        worldSphereCenter,
                        useStabList: true,
                        (candidates as CellArray)?.UnionTarget);
                    if (recovered != 0u && recovered != currentCellId)
                        return recovered;
                }
            }
        }

        return currentCellId;
    }

    private static void RecordUnionOnlyProbe(
        ICollection<uint> candidates,
        uint cellId)
    {
        if (candidates is CellArray { UnionTarget: { } queryFootprint })
            queryFootprint.Add(cellId);
    }

    private static int EffectiveSphereCount(IReadOnlyList<Sphere> worldSpheres, int numSpheres)
    {
        if (numSpheres <= 0 || worldSpheres.Count == 0) return 0;
        return numSpheres < worldSpheres.Count ? numSpheres : worldSpheres.Count;
    }
}
