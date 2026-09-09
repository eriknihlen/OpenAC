using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using DatReaderWriter.Types;

namespace AcDream.Content.Tests;

[Trait("Lane", "InstalledDat")]
public sealed class NeftetFormationCellMembershipTests
{
    private const uint NeftetLandblock = 0x87640000u;
    private const uint NeftetLandblockInfo = 0x8764FFFEu;
    private const uint FormationGfxObj = 0x010046D8u;

    private const uint MeasuredPresentA = 0x8764000Au;   // lcoord (1081, 801)
    private const uint MeasuredPresentB = 0x87640012u;   // lcoord (1082, 801)
    private const uint MeasuredAbsentA = 0x87640011u;    // lcoord (1082, 800)
    private const uint MeasuredAbsentB = 0x87640019u;    // lcoord (1083, 800)

    [Fact]
    public void NeftetFormation_RegistersInTheCellsTheProbeMeasuredEmpty()
    {
        string? datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        Assert.True(
            dats.Cell.TryGet<LandBlockInfo>(NeftetLandblockInfo, out LandBlockInfo? info)
            && info is not null,
            "Neftet landblock info 0x8764FFFE is absent from the installed cell dat.");

        Stab formation = info!.Objects.First(o => o.Id == FormationGfxObj);

        Assert.True(
            dats.Portal.TryGet<GfxObj>(FormationGfxObj, out GfxObj? gfx) && gfx is not null,
            "GfxObj 0x010046D8 is absent from the installed portal dat.");

        var cache = new PhysicsDataCache();
        cache.CacheGfxObj(FormationGfxObj, gfx!);
        GfxObjPhysics? phys = cache.GetGfxObj(FormationGfxObj);
        Assert.NotNull(phys);
        Assert.NotNull(phys!.VisualBounds);
        FlatGfxObjVisualBounds box = phys.VisualBounds!.Value;
        float extentX = box.Max.X - box.Min.X;
        float extentY = box.Max.Y - box.Min.Y;
        float rootRadius = phys.BoundingSphere!.Radius;
        Assert.True(
            extentX > rootRadius && extentY > rootRadius,
            $"Fixture is degenerate: extent ({extentX:F2}, {extentY:F2}) does not " +
            $"exceed the root sphere radius {rootRadius:F3}.");
        Assert.True(
            extentX > 48f && extentY > 48f,
            $"Fixture cannot reach two cells away: extent ({extentX:F2}, {extentY:F2}).");

        IReadOnlyList<ShadowShape> shapes =
            ShadowShapeBuilder.FromLandblockBspParts(
                new[] { new MeshRef(FormationGfxObj, Matrix4x4.Identity) },
                isBuildingShell: false,
                cache.GetGfxObj);
        ShadowShape only = Assert.Single(shapes);
        Assert.Equal(ShadowCollisionType.BSP, only.CollisionType);

        var registry = new ShadowObjectRegistry { DataCache = cache };
        const uint ownerId = 0xC8764000u;
        registry.RegisterMultiPart(
            ownerId,
            formation.Frame.Origin,
            formation.Frame.Orientation,
            shapes,
            0u,
            EntityCollisionFlags.None,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: NeftetLandblock,
            seedCellId: 0u,
            isStatic: true);

        var held = new List<uint>();
        for (uint index = 1u; index <= 64u; index++)
        {
            uint cellId = NeftetLandblock | index;
            if (registry.GetObjectsInCell(cellId).Any(e => e.EntityId == ownerId))
                held.Add(cellId);
        }

        // The measured-populated pair must stay populated.
        Assert.Contains(MeasuredPresentA, held);
        Assert.Contains(MeasuredPresentB, held);

        Assert.Contains(MeasuredAbsentA, held);
        Assert.Contains(MeasuredAbsentB, held);

        Assert.Equal(25, held.Count);
    }
}
