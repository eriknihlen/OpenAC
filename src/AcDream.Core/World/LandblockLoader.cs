using DatReaderWriter;
using DatReaderWriter.DBObjs;
using AcDream.Core.Content;
using DatReaderWriter.Types;
using AcDream.Core.Physics;

namespace AcDream.Core.World;

public static class LandblockLoader
{
    private const uint GfxObjMask = 0x01000000u;
    private const uint SetupMask  = 0x02000000u;
    private const uint TypeMask   = 0xFF000000u;

    public static LoadedLandblock? Load(IDatObjectSource dats, uint landblockId)
    {
        var block = dats.Get<LandBlock>(landblockId);
        if (block is null)
            return null;

        var info = dats.Get<LandBlockInfo>((landblockId & 0xFFFF0000u) | 0xFFFEu);
        var entities = info is null
            ? Array.Empty<WorldEntity>()
            : BuildEntitiesFromInfo(info, landblockId);

        return new LoadedLandblock(landblockId, block, entities);
    }

    /// <summary>
    /// Pure mapping from a parsed LandBlockInfo to a list of WorldEntity.
    /// Each Stab and BuildingInfo becomes one entity. Unsupported id types
    /// (neither GfxObj 0x01xxxxxx nor Setup 0x02xxxxxx) are silently skipped.
    /// MeshRefs is left empty at this stage — Task 5 populates it.
    /// </summary>
    public static IReadOnlyList<WorldEntity> BuildEntitiesFromInfo(LandBlockInfo info, uint landblockId = 0)
    {
        var result = new List<WorldEntity>(info.Objects.Count + info.Buildings.Count);

        uint landblockX = (landblockId >> 24) & 0xFFu;
        uint landblockY = (landblockId >> 16) & 0xFFu;
        uint nextId = landblockId == 0 ? 1u : 0u;

        uint AllocateId()
        {
            if (landblockId == 0)
                return nextId++;
            return LandblockStaticEntityIdAllocator.Allocate(
                landblockX,
                landblockY,
                ref nextId);
        }

        foreach (var stab in info.Objects)
        {
            if (!IsSupported(stab.Id))
                continue;
            var stabEntity = new WorldEntity
            {
                Id = AllocateId(),
                SourceGfxObjOrSetupId = stab.Id,
                Position = stab.Frame.Origin,
                Rotation = stab.Frame.Orientation,
                MeshRefs = Array.Empty<MeshRef>(),
                EffectCellId = OutdoorCellId(landblockId, stab.Frame.Origin),
            };
            stabEntity.RefreshAabb();
            result.Add(stabEntity);
        }

        foreach (var building in info.Buildings)
        {
            if (!IsSupported(building.ModelId))
                continue;
            var buildingEntity = new WorldEntity
            {
                Id = AllocateId(),
                SourceGfxObjOrSetupId = building.ModelId,
                Position = building.Frame.Origin,
                Rotation = building.Frame.Orientation,
                MeshRefs = Array.Empty<MeshRef>(),
                EffectCellId = OutdoorCellId(landblockId, building.Frame.Origin),
                IsBuildingShell = true,
                BuildingShellAnchorCellId = FirstBuildingAnchorCellId(building, landblockId),
            };
            buildingEntity.RefreshAabb();
            result.Add(buildingEntity);
        }

        return result;
    }

    private static bool IsSupported(uint id)
    {
        var type = id & TypeMask;
        return type == GfxObjMask || type == SetupMask;
    }

    private static uint? OutdoorCellId(uint landblockId, System.Numerics.Vector3 localPosition)
    {
        if (landblockId == 0)
            return null;

        return TerrainSurface.ComputeOutdoorCellId(
            landblockId,
            localPosition.X,
            localPosition.Y);
    }

    private static uint? FirstBuildingAnchorCellId(BuildingInfo building, uint landblockId)
    {
        if (landblockId == 0)
            return null;

        uint lbPrefix = landblockId & 0xFFFF0000u;
        foreach (var portal in building.Portals)
        {
            if (portal.OtherCellId == 0xFFFF)
                continue;
            return lbPrefix | (uint)portal.OtherCellId;
        }

        return null;
    }
}
