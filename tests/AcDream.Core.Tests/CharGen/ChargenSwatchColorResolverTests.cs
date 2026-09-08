using AcDream.Core.CharGen;

namespace AcDream.Core.Tests.CharGen;

public sealed class ChargenSwatchColorResolverTests
{
    private sealed class FakePalSetSource : IChargenPalSetSource
    {
        private readonly Dictionary<uint, ChargenPalSet> _sets = new();
        public void Add(uint id, params uint[] paletteIds) => _sets[id] = new ChargenPalSet(paletteIds);
        public ChargenPalSet? TryGetPalSet(uint palSetId) => _sets.TryGetValue(palSetId, out var s) ? s : null;
    }

    private sealed class FakeClothingTableSource : IChargenClothingTableSource
    {
        private readonly Dictionary<uint, ChargenClothingTable> _tables = new();
        public void Add(uint id, ChargenClothingTable table) => _tables[id] = table;
        public ChargenClothingTable? TryGetClothingTable(uint clothingTableId) =>
            _tables.TryGetValue(clothingTableId, out var t) ? t : null;
    }

    private sealed class FakeColorSource : IChargenPaletteColorSource
    {
        private readonly Dictionary<(uint paletteId, int index), ChargenSwatchRgb> _colors = new();
        public void Add(uint paletteId, int index, byte r, byte g, byte b) =>
            _colors[(paletteId, index)] = new ChargenSwatchRgb(r, g, b);

        public bool TryGetColor(uint paletteId, int index, out ChargenSwatchRgb color) =>
            _colors.TryGetValue((paletteId, index), out color);
    }

    // ── TryGetPalSetAverageColor ────────────────────────────────────────

    [Fact]
    public void TryGetPalSetAverageColor_AveragesEveryPaletteInTheSet()
    {
        var palSets = new FakePalSetSource();
        palSets.Add(0x0F00_0001u, 0x0400_0001u, 0x0400_0002u);
        var colors = new FakeColorSource();
        colors.Add(0x0400_0001u, 0xd0, r: 100, g: 0, b: 0);
        colors.Add(0x0400_0002u, 0xd0, r: 200, g: 0, b: 0);

        bool ok = ChargenSwatchColorResolver.TryGetPalSetAverageColor(
            palSets, colors, 0x0F00_0001u, ChargenSwatchColorResolver.HairSampleIndex, out ChargenSwatchRgb color);

        Assert.True(ok);
        Assert.Equal(new ChargenSwatchRgb(150, 0, 0), color);
    }

    [Fact]
    public void TryGetPalSetAverageColor_MissingIndividualPaletteContributesBlackNotSkip()
    {
        var palSets = new FakePalSetSource();
        palSets.Add(0x0F00_0002u, 0x0400_0010u, 0x0400_0011u);
        var colors = new FakeColorSource();
        colors.Add(0x0400_0010u, 0xd0, r: 200, g: 100, b: 50);

        bool ok = ChargenSwatchColorResolver.TryGetPalSetAverageColor(
            palSets, colors, 0x0F00_0002u, ChargenSwatchColorResolver.HairSampleIndex, out ChargenSwatchRgb color);

        Assert.True(ok);
        Assert.Equal(new ChargenSwatchRgb(100, 50, 25), color);
    }

    [Fact]
    public void TryGetPalSetAverageColor_EmptyPalSet_ReturnsTrueBlack()
    {
        var palSets = new FakePalSetSource();
        palSets.Add(0x0F00_0003u); // zero palette ids.
        var colors = new FakeColorSource();

        bool ok = ChargenSwatchColorResolver.TryGetPalSetAverageColor(
            palSets, colors, 0x0F00_0003u, ChargenSwatchColorResolver.HairSampleIndex, out ChargenSwatchRgb color);

        Assert.True(ok);
        Assert.Equal(new ChargenSwatchRgb(0, 0, 0), color);
    }

    [Fact]
    public void TryGetPalSetAverageColor_UnresolvedPalSetId_ReturnsFalse()
    {
        var palSets = new FakePalSetSource();
        var colors = new FakeColorSource();

        bool ok = ChargenSwatchColorResolver.TryGetPalSetAverageColor(
            palSets, colors, 0x0F00_DEADu, ChargenSwatchColorResolver.HairSampleIndex, out _);

        Assert.False(ok);
    }

    // ── TryGetDirectColor (Eyes) ─────────────────────────────────────────

    [Fact]
    public void TryGetDirectColor_ReadsThePaletteDirectly_NoPalSetIndirection()
    {
        var colors = new FakeColorSource();
        colors.Add(0x0400_0099u, ChargenSwatchColorResolver.EyeSampleIndex, r: 15, g: 63, b: 93);

        bool ok = ChargenSwatchColorResolver.TryGetDirectColor(
            colors, 0x0400_0099u, ChargenSwatchColorResolver.EyeSampleIndex, out ChargenSwatchRgb color);

        Assert.True(ok);
        Assert.Equal(new ChargenSwatchRgb(15, 63, 93), color);
    }

    [Fact]
    public void TryGetDirectColor_UnresolvedPaletteId_ReturnsFalse()
    {
        var colors = new FakeColorSource();

        bool ok = ChargenSwatchColorResolver.TryGetDirectColor(
            colors, 0x0400_DEADu, ChargenSwatchColorResolver.EyeSampleIndex, out _);

        Assert.False(ok);
    }

    // ── TryGetClothingSwatchPalSetId ─────────────────────────────────────

    private static ChargenClothingTable MakeClothingTable(uint templateId, uint firstChoicePalSetId, uint secondChoicePalSetId)
    {
        var template = new ChargenClothingPaletteTemplate(
        [
            new ChargenClothingSubPaletteChoice(firstChoicePalSetId, [new ChargenClothingSubPaletteRange(0, 8)]),
            new ChargenClothingSubPaletteChoice(secondChoicePalSetId, [new ChargenClothingSubPaletteRange(8, 8)]),
        ]);
        return new ChargenClothingTable(
            new Dictionary<uint, ChargenClothingBaseEffect>(),
            new Dictionary<uint, ChargenClothingPaletteTemplate> { [templateId] = template });
    }

    [Fact]
    public void TryGetClothingSwatchPalSetId_ReturnsTheFIRSTChoicesPalSetId()
    {
        var clothingTables = new FakeClothingTableSource();
        clothingTables.Add(0x1900_0001u, MakeClothingTable(9u, 0x0F00_0010u, 0x0F00_0011u));

        bool ok = ChargenSwatchColorResolver.TryGetClothingSwatchPalSetId(
            clothingTables, 0x1900_0001u, paletteTemplateId: 9u, out uint palSetId);

        Assert.True(ok);
        Assert.Equal(0x0F00_0010u, palSetId);
    }

    [Fact]
    public void TryGetClothingSwatchPalSetId_UnresolvedClothingTable_ReturnsFalse()
    {
        var clothingTables = new FakeClothingTableSource();

        bool ok = ChargenSwatchColorResolver.TryGetClothingSwatchPalSetId(
            clothingTables, 0x1900_DEADu, paletteTemplateId: 9u, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryGetClothingSwatchPalSetId_TemplateIdNotInThisGarmentsTable_ReturnsFalse()
    {
        var clothingTables = new FakeClothingTableSource();
        clothingTables.Add(0x1900_0002u, MakeClothingTable(9u, 0x0F00_0010u, 0x0F00_0011u));

        bool ok = ChargenSwatchColorResolver.TryGetClothingSwatchPalSetId(
            clothingTables, 0x1900_0002u, paletteTemplateId: 999u, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryGetClothingSwatchPalSetId_TemplateWithNoChoices_ReturnsFalse()
    {
        var clothingTables = new FakeClothingTableSource();
        var emptyTemplate = new ChargenClothingPaletteTemplate([]);
        clothingTables.Add(
            0x1900_0003u,
            new ChargenClothingTable(
                new Dictionary<uint, ChargenClothingBaseEffect>(),
                new Dictionary<uint, ChargenClothingPaletteTemplate> { [5u] = emptyTemplate }));

        bool ok = ChargenSwatchColorResolver.TryGetClothingSwatchPalSetId(
            clothingTables, 0x1900_0003u, paletteTemplateId: 5u, out _);

        Assert.False(ok);
    }


    [Fact]
    public void SampleIndexConstants_MatchTheReferenceLiterals()
    {
        Assert.Equal(0xd0, ChargenSwatchColorResolver.HairSampleIndex);
        Assert.Equal(0xb0, ChargenSwatchColorResolver.SkinFamilySampleIndex);
        Assert.Equal(0x103, ChargenSwatchColorResolver.EyeSampleIndex);
        Assert.Equal(0x520, ChargenSwatchColorResolver.ClothingSampleIndex);
    }
}
