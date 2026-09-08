using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

public sealed class PaperdollClickMapTests
{
    public static TheoryData<byte, byte, byte, EquipMask> RetailColors => new()
    {
        { 0x00, 0x00, 0xFF, EquipMask.HeadWear },
        { 0x00, 0xFF, 0x00, EquipMask.ChestWear | EquipMask.ChestArmor },
        { 0xFF, 0x00, 0x00, EquipMask.AbdomenWear | EquipMask.AbdomenArmor },
        { 0x00, 0xFF, 0xFF, EquipMask.UpperArmWear | EquipMask.UpperArmArmor },
        { 0xFF, 0x00, 0xFF, EquipMask.LowerArmWear | EquipMask.LowerArmArmor },
        { 0xFF, 0xFF, 0x00, EquipMask.UpperLegWear | EquipMask.UpperLegArmor },
        { 0x00, 0x00, 0x80, EquipMask.LowerLegWear | EquipMask.LowerLegArmor },
        { 0x00, 0x80, 0x00, EquipMask.HandWear },
        { 0x80, 0x00, 0x00, EquipMask.FootWear },
    };

    [Theory]
    [MemberData(nameof(RetailColors))]
    public void GetBodyLocation_mapsRetailHitColors(
        byte r,
        byte g,
        byte b,
        EquipMask expected)
    {
        var map = new PaperdollClickMap([r, g, b, 0xFF], 1, 1);

        Assert.Equal(expected, map.GetBodyLocation(0, 0));
    }

    [Fact]
    public void GetBodyLocation_rejectsBackgroundAndOutOfBounds()
    {
        var map = new PaperdollClickMap([0xFF, 0xFF, 0xFF, 0xFF], 1, 1);

        Assert.Equal(EquipMask.None, map.GetBodyLocation(0, 0));
        Assert.Equal(EquipMask.None, map.GetBodyLocation(-1, 0));
        Assert.Equal(EquipMask.None, map.GetBodyLocation(1, 0));
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void LiveDat_resolvesRetailClickMapAndAllNineBodyRegions()
    {
        string datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        if (!Directory.Exists(datDir)) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        PaperdollClickMap map = Assert.IsType<PaperdollClickMap>(PaperdollClickMap.Load(dats));
        ImportedLayout layout = Assert.IsType<ImportedLayout>(LayoutImporter.Import(
            dats,
            0x21000023u,
            static _ => (0u, 0, 0),
            null));
        UiElement dragMask = Assert.IsAssignableFrom<UiElement>(
            layout.FindElement(PaperdollController.DollDragMaskId));
        Assert.Equal((int)dragMask.Width, map.Width);
        Assert.Equal((int)dragMask.Height, map.Height);

        var regions = new HashSet<EquipMask>();
        for (int y = 0; y < map.Height; y++)
        for (int x = 0; x < map.Width; x++)
        {
            EquipMask region = map.GetBodyLocation(x, y);
            if (region != EquipMask.None)
                regions.Add(region);
        }

        Assert.Equal(9, regions.Count);
        Assert.Contains(EquipMask.HeadWear, regions);
        Assert.Contains(EquipMask.ChestWear | EquipMask.ChestArmor, regions);
        Assert.Contains(EquipMask.FootWear, regions);
    }
}
