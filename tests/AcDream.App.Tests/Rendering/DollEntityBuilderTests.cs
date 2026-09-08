using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.World;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class DollEntityBuilderTests
{
    [Fact]
    public void Builds_doll_entity_with_synthetic_guid_and_player_setup()
    {
        // SubPaletteRange uses: SubPaletteId, Offset (byte), Length (byte)
        var doll = DollEntityBuilder.Build(
            setupId: 0x0200_0001u,
            meshRefs: new List<MeshRef>(),
            basePaletteId: 0x04000ABCu,
            subPalettes: new (uint SubPaletteId, byte Offset, byte Length)[] { (0x0F00_0001u, 0, 8) },
            partOverrides: new (byte PartIndex, uint GfxObjId)[] { (2, 0x0100_0042u) });

        Assert.Equal(0x0200_0001u, doll.SourceGfxObjOrSetupId);
        Assert.Equal(DollEntityBuilder.DollServerGuid, doll.ServerGuid);
        Assert.NotEqual(0u, doll.ServerGuid);
        Assert.Single(doll.PartOverrides);
        Assert.Equal((byte)2, doll.PartOverrides[0].PartIndex);
        Assert.Equal(0x0100_0042u, doll.PartOverrides[0].GfxObjId);
        Assert.NotNull(doll.PaletteOverride);
        Assert.Equal(0x04000ABCu, doll.PaletteOverride!.BasePaletteId);
        Assert.Single(doll.PaletteOverride.SubPalettes);
        Assert.Equal(0x0F00_0001u, doll.PaletteOverride.SubPalettes[0].SubPaletteId);
    }

    [Fact]
    public void Null_overrides_give_empty_collections_not_null()
    {
        var doll = DollEntityBuilder.Build(0x0200_0001u, new List<MeshRef>(), null, null, null);
        Assert.Null(doll.PaletteOverride);
        Assert.Empty(doll.PartOverrides);
    }

    [Fact]
    public void Empty_subpalettes_give_null_palette_override()
    {
        var doll = DollEntityBuilder.Build(
            0x0200_0001u,
            new List<MeshRef>(),
            basePaletteId: 0x04000001u,
            subPalettes: System.Array.Empty<(uint, byte, byte)>(),
            partOverrides: null);
        Assert.Null(doll.PaletteOverride);
    }

    [Fact]
    public void Heading_is_normalized_quaternion_facing_viewer()
    {
        var doll = DollEntityBuilder.Build(0x0200_0001u, new List<MeshRef>(), null, null, null);
        Assert.True(System.MathF.Abs(doll.Rotation.LengthSquared() - 1f) < 1e-3f);
    }

    [Fact]
    public void Position_is_world_origin()
    {
        var doll = DollEntityBuilder.Build(0x0200_0001u, new List<MeshRef>(), null, null, null);
        Assert.Equal(Vector3.Zero, doll.Position);
    }

    [Fact]
    public void ParentCellId_is_null_for_doll_scene()
    {
        var doll = DollEntityBuilder.Build(0x0200_0001u, new List<MeshRef>(), null, null, null);
        Assert.Null(doll.ParentCellId);
    }

    [Fact]
    public void MeshRefs_are_passed_through()
    {
        var refs = new List<MeshRef> { new MeshRef(0x01000001u, Matrix4x4.Identity) };
        var doll = DollEntityBuilder.Build(0x0200_0001u, refs, null, null, null);
        Assert.Same(refs, doll.MeshRefs);
    }
}
