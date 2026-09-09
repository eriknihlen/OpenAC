using System;
using System.Collections.Generic;
using System.IO;
using AcDream.App.UI.Layout;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;

namespace AcDream.App.Tests.UI.Layout;

public class PaperdollSlotBackgroundTests
{
    public static TheoryData<uint, uint> SlotPrototypeCases => new()
    {
        { 0x100001DAu, 0x10000446u }, // neck
        { 0x100001DBu, 0x10000447u }, // left wrist
        { 0x100001DCu, 0x10000448u }, // left ring
        { 0x100001DDu, 0x10000449u }, // right wrist
        { 0x100001DEu, 0x1000044Au }, // right ring
        { 0x100001DFu, 0x1000044Bu }, // weapon
        { 0x100001E0u, 0x1000044Cu }, // ammo
        { 0x100001E1u, 0x1000044Du }, // shield
        { 0x100001E2u, 0x1000044Eu }, // shirt
        { 0x100001E3u, 0x1000044Fu }, // pants
        { 0x1000058Eu, 0x1000058Fu }, // trinket
        { 0x100005E9u, 0x100005EAu },
        { 0x10000595u, 0x10000592u }, // blue Aetheria
        { 0x10000596u, 0x10000593u }, // yellow Aetheria
        { 0x10000597u, 0x10000594u }, // red Aetheria
        { 0x100005ABu, 0x100005B4u }, // head
        { 0x100005ACu, 0x100005B5u },
        { 0x100005ADu, 0x100005B6u }, // abdomen armor
        { 0x100005AEu, 0x100005B7u }, // upper arm armor
        { 0x100005AFu, 0x100005B8u }, // lower arm armor
        { 0x100005B0u, 0x100005B9u }, // hands
        { 0x100005B1u, 0x100005BAu }, // upper legs
        { 0x100005B2u, 0x100005BBu }, // lower legs
        { 0x100005B3u, 0x100005BDu }, // feet
    };

    [Theory]
    [MemberData(nameof(SlotPrototypeCases))]
    public void Slot_uses_authored_UIItem_prototype(uint slotElement, uint prototypeElement)
    {
        Assert.True(PaperdollSlotBackgrounds.TryGetPrototype(slotElement, out uint actual));
        Assert.Equal(prototypeElement, actual);
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void Live_dat_resolves_all_pinned_retail_backgrounds()
    {
        string datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        if (!Directory.Exists(datDir)) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        IReadOnlyDictionary<uint, uint> actual = PaperdollSlotBackgrounds.ResolveEmptySprites(dats);
        var expected = new Dictionary<uint, uint>
        {
            [0x100001DAu] = 0x06000F68u,
            [0x100001DBu] = 0x06000F5Du,
            [0x100001DCu] = 0x06000F5Au,
            [0x100001DDu] = 0x06000F6Au,
            [0x100001DEu] = 0x06000F6Bu,
            [0x100001DFu] = 0x06000F66u,
            [0x100001E0u] = 0x06000F5Eu,
            [0x100001E1u] = 0x06000F6Cu,
            [0x100001E2u] = 0x060032C5u,
            [0x100001E3u] = 0x060032C4u,
            [0x1000058Eu] = 0x06006A6Cu,
            [0x100005E9u] = 0x0600708Fu,
            [0x10000595u] = 0x06006BEFu,
            [0x10000596u] = 0x06006BF0u,
            [0x10000597u] = 0x06006BF1u,
            [0x100005ABu] = 0x06006D7Fu,
            [0x100005ACu] = 0x06006D7Bu,
            [0x100005ADu] = 0x06006D79u,
            [0x100005AEu] = 0x06006D87u,
            [0x100005AFu] = 0x06006D81u,
            [0x100005B0u] = 0x06006D7Du,
            [0x100005B1u] = 0x06006D89u,
            [0x100005B2u] = 0x06006D83u,
            [0x100005B3u] = 0x06006D85u,
        };

        Assert.Equal(expected.Count, actual.Count);
        foreach (var (slot, sprite) in expected)
            Assert.Equal(sprite, actual[slot]);
    }
}
