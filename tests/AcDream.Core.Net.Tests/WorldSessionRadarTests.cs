using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests;

public sealed class WorldSessionRadarTests
{
    [Fact]
    public void ToEntitySpawn_PreservesRadarBytes()
    {
        var parsed = MinimalParsed() with
        {
            RadarBlipColor = 7,
            RadarBehavior = 3,
        };

        var spawn = WorldSession.ToEntitySpawn(parsed);

        Assert.Equal((byte)7, spawn.RadarBlipColor);
        Assert.Equal((byte)3, spawn.RadarBehavior);
    }

    [Fact]
    public void ToEntitySpawn_PreservesMissingRadarFields()
    {
        var spawn = WorldSession.ToEntitySpawn(MinimalParsed());

        Assert.Null(spawn.RadarBlipColor);
        Assert.Null(spawn.RadarBehavior);
    }

    [Fact]
    public void ToEntitySpawn_PreservesPluralName()
    {
        var spawn = WorldSession.ToEntitySpawn(MinimalParsed() with
        {
            PluralName = "Pyreal Scarabs",
        });

        Assert.Equal("Pyreal Scarabs", spawn.PluralName);
    }

    [Fact]
    public void ToEntitySpawn_PreservesPetOwner()
    {
        var spawn = WorldSession.ToEntitySpawn(MinimalParsed() with
        {
            PetOwnerId = 0x50000002u,
        });

        Assert.Equal(0x50000002u, spawn.PetOwnerId);
    }

    [Fact]
    public void ToEntitySpawn_PreservesMaterialType()
    {
        var spawn = WorldSession.ToEntitySpawn(MinimalParsed() with
        {
            MaterialType = 77u,
        });

        Assert.Equal(77u, spawn.MaterialType);
    }

    private static CreateObject.Parsed MinimalParsed() => new(
        Guid: 0x50000001u,
        Position: null,
        SetupTableId: null,
        AnimPartChanges: Array.Empty<CreateObject.AnimPartChange>(),
        TextureChanges: Array.Empty<CreateObject.TextureChange>(),
        SubPalettes: Array.Empty<CreateObject.SubPaletteSwap>(),
        BasePaletteId: null,
        ObjScale: null,
        Name: "Radar Test",
        ItemType: null,
        MotionState: null,
        MotionTableId: null);
}
