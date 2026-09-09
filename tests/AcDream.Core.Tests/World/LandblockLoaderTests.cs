using System.Numerics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.World;

public class LandblockLoaderTests
{
    private static LandBlock BuildFlatLandBlock()
    {
        var block = new LandBlock
        {
            HasObjects = true,
            Terrain = new TerrainInfo[81],
            Height = new byte[81],
        };
        for (int i = 0; i < 81; i++)
        {
            block.Terrain[i] = (ushort)0;
            block.Height[i] = 0;
        }
        return block;
    }

    [Fact]
    public void BuildEntitiesFromInfo_StabsAndBuildings_AreMappedToEntities()
    {
        var info = new LandBlockInfo
        {
            Objects =
            {
                new Stab
                {
                    Id = 0x01000042u,  // GfxObj id
                    Frame = new Frame
                    {
                        Origin = new Vector3(10, 20, 5),
                        Orientation = Quaternion.Identity,
                    },
                },
                new Stab
                {
                    Id = 0x02000099u,  // Setup id
                    Frame = new Frame
                    {
                        Origin = new Vector3(30, 40, 10),
                        Orientation = Quaternion.Identity,
                    },
                },
            },
            Buildings =
            {
                new BuildingInfo
                {
                    ModelId = 0x020000AAu,  // Setup for a building
                    Frame = new Frame
                    {
                        Origin = new Vector3(50, 60, 0),
                        Orientation = Quaternion.Identity,
                    },
                },
            },
        };

        var entities = LandblockLoader.BuildEntitiesFromInfo(info);

        Assert.Equal(3, entities.Count);
        Assert.Contains(entities, e => e.SourceGfxObjOrSetupId == 0x01000042u && e.Position == new Vector3(10, 20, 5));
        Assert.Contains(entities, e => e.SourceGfxObjOrSetupId == 0x02000099u && e.Position == new Vector3(30, 40, 10));
        Assert.Contains(entities, e => e.SourceGfxObjOrSetupId == 0x020000AAu && e.Position == new Vector3(50, 60, 0));
    }

    [Fact]
    public void BuildEntitiesFromInfo_AssignsMonotonicIds()
    {
        var info = new LandBlockInfo
        {
            Objects =
            {
                new Stab { Id = 0x01000001u, Frame = new Frame() },
                new Stab { Id = 0x01000002u, Frame = new Frame() },
                new Stab { Id = 0x01000003u, Frame = new Frame() },
            },
        };

        var entities = LandblockLoader.BuildEntitiesFromInfo(info);

        var ids = entities.Select(e => e.Id).OrderBy(i => i).ToArray();
        Assert.Equal(3, ids.Distinct().Count());  // all unique
    }

    [Fact]
    public void BuildEntitiesFromInfo_UnsupportedIdType_IsSkipped()
    {
        // 0x03xxxxxx is neither GfxObj (0x01) nor Setup (0x02).
        var info = new LandBlockInfo
        {
            Objects =
            {
                new Stab { Id = 0x01000001u, Frame = new Frame() },
                new Stab { Id = 0x03000002u, Frame = new Frame() },  // skipped
                new Stab { Id = 0x02000003u, Frame = new Frame() },
            },
        };

        var entities = LandblockLoader.BuildEntitiesFromInfo(info);

        Assert.Equal(2, entities.Count);
        Assert.DoesNotContain(entities, e => e.SourceGfxObjOrSetupId == 0x03000002u);
    }

    [Fact]
    public void BuildEntitiesFromInfo_Empty_ReturnsEmpty()
    {
        var entities = LandblockLoader.BuildEntitiesFromInfo(new LandBlockInfo());
        Assert.Empty(entities);
    }

    [Fact]
    public void BuildEntitiesFromInfo_WithLandblockId_NamespacesIdsForGlobalUniqueness()
    {
        var info = new LandBlockInfo
        {
            Objects =
            {
                new Stab { Id = 0x01000001u, Frame = new Frame() },
                new Stab { Id = 0x01000002u, Frame = new Frame() },
            },
        };

        var entitiesLbA = LandblockLoader.BuildEntitiesFromInfo(info, landblockId: 0xA9B40000u);
        var entitiesLbB = LandblockLoader.BuildEntitiesFromInfo(info, landblockId: 0xA9B50000u);

        // No two entities across LB A and LB B share the same Id.
        var idsA = entitiesLbA.Select(e => e.Id).ToArray();
        var idsB = entitiesLbB.Select(e => e.Id).ToArray();
        Assert.Empty(idsA.Intersect(idsB));

        // The namespace top nibble is 0xC for stabs (distinct from 0x8
        // scenery, 0x4 interior, and low-range live entities).
        Assert.All(idsA, id => Assert.True(LandblockStaticEntityIdAllocator.IsInNamespace(id)));
        Assert.All(idsB, id => Assert.True(LandblockStaticEntityIdAllocator.IsInNamespace(id)));
    }

    [Fact]
    public void BuildEntitiesFromInfo_AssignsOutdoorEffectCellWithoutChangingRenderParent()
    {
        var info = new LandBlockInfo
        {
            Objects =
            {
                new Stab
                {
                    Id = 0x01000001u,
                    Frame = new Frame { Origin = new Vector3(25f, 49f, 0f) },
                },
            },
        };

        WorldEntity entity = Assert.Single(
            LandblockLoader.BuildEntitiesFromInfo(info, 0xA9B40000u));

        Assert.Equal(0xA9B4000Bu, entity.EffectCellId);
        Assert.Null(entity.ParentCellId);
    }

    [Fact]
    public void BuildEntitiesFromInfo_LegacyZeroLandblockId_StartsAtOne()
    {
        var info = new LandBlockInfo
        {
            Objects = { new Stab { Id = 0x01000001u, Frame = new Frame() } },
        };

        var entities = LandblockLoader.BuildEntitiesFromInfo(info);

        Assert.Single(entities);
        Assert.Equal(1u, entities[0].Id);
    }

    [Fact]
    public void BuildEntitiesFromInfo_MoreThan255EntriesStayInTheOwningLandblockRange()
    {
        var info = new LandBlockInfo();
        for (uint i = 0; i < 300u; i++)
            info.Objects.Add(new Stab { Id = 0x01000001u, Frame = new Frame() });

        IReadOnlyList<WorldEntity> entities =
            LandblockLoader.BuildEntitiesFromInfo(info, landblockId: 0xA9B40000u);

        Assert.Equal(300, entities.Count);
        Assert.Equal(300, entities.Select(entity => entity.Id).Distinct().Count());
        Assert.All(entities, entity =>
            Assert.True(LandblockStaticEntityIdAllocator.IsInNamespace(entity.Id)));
        Assert.True(entities[^1].Id < LandblockStaticEntityIdAllocator.Base(0xA9u, 0xB5u));
    }

    [Fact]
    public void BuildEntitiesFromInfo_NamespacedIdOverflowFailsBeforeCrossLandblockAlias()
    {
        var info = new LandBlockInfo();
        for (uint i = 0; i <= LandblockStaticEntityIdAllocator.MaxCounter + 1u; i++)
            info.Objects.Add(new Stab { Id = 0x01000001u, Frame = new Frame() });

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => LandblockLoader.BuildEntitiesFromInfo(info, landblockId: 0xA9B40000u));

        Assert.Contains("4096-entry", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildEntitiesFromInfo_TagsBuildingsWithIsBuildingShellTrue()
    {
        var info = new LandBlockInfo
        {
            Buildings =
            {
                new BuildingInfo
                {
                    ModelId = 0x02000123u,  // Setup id
                    Frame = new Frame
                    {
                        Origin = new Vector3(10f, 20f, 30f),
                        Orientation = Quaternion.Identity,
                    },
                    Portals =
                    {
                        new BuildingPortal
                        {
                            OtherCellId = 0x013F,
                            OtherPortalId = 0,
                            Flags = 0,
                        },
                    },
                },
            },
        };

        var entities = LandblockLoader.BuildEntitiesFromInfo(info, landblockId: 0xA9B40000u);

        Assert.Single(entities);
        Assert.True(entities[0].IsBuildingShell);
        Assert.Equal(0xA9B4013Fu, entities[0].BuildingShellAnchorCellId);
    }

    [Fact]
    public void BuildEntitiesFromInfo_TagsObjectsWithIsBuildingShellFalse()
    {
        var info = new LandBlockInfo
        {
            Objects =
            {
                new Stab
                {
                    Id = 0x01000123u,  // GfxObj id
                    Frame = new Frame
                    {
                        Origin = new Vector3(10f, 20f, 30f),
                        Orientation = Quaternion.Identity,
                    },
                },
            },
        };

        var entities = LandblockLoader.BuildEntitiesFromInfo(info);

        Assert.Single(entities);
        Assert.False(entities[0].IsBuildingShell);
    }
}
