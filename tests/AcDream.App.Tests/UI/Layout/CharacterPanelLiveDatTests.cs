using System.Numerics;
using AcDream.App.UI.Layout;
using AcDream.Content;
using DatReaderWriter;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class CharacterPanelLiveDatTests
{
    private static string DatDirectory =>
        Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");

    private static IEnumerable<ElementInfo> Flatten(ElementInfo e)
    {
        yield return e;
        foreach (var c in e.Children)
            foreach (var d in Flatten(c))
                yield return d;
    }

    [InstalledDatFact]
    public void HeaderElements_AuthorExpectedFontsAndColors()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        ElementInfo? tree = LayoutImporter.ImportInfos(dats, 0x2100002Eu);
        Assert.NotNull(tree);

        var nameOccurrences = Flatten(tree!).Where(e => e.Id == CharacterStatController.NameId).ToList();
        Assert.Equal(2, nameOccurrences.Count);
        foreach (var name in nameOccurrences)
        {
            Assert.Equal(0x40000001u, name.FontDid);
            Assert.Equal(Vector4.One, name.FontColor);
        }

        var heritageOccurrences = Flatten(tree!).Where(e => e.Id == CharacterStatController.HeritageId).ToList();
        Assert.Equal(2, heritageOccurrences.Count);
        foreach (var heritage in heritageOccurrences)
        {
            Assert.Equal(0x40000002u, heritage.FontDid);
            Assert.Equal(Vector4.One, heritage.FontColor);
            Assert.Equal(5, heritage.MarginLeft);
            Assert.Equal(5, heritage.MarginRight);
        }

        // Item 4: PK status line is authored PURE WHITE.
        var pkOccurrences = Flatten(tree!).Where(e => e.Id == CharacterStatController.PkStatusId).ToList();
        Assert.Equal(2, pkOccurrences.Count);
        foreach (var pk in pkOccurrences)
        {
            Assert.Equal(0x40000002u, pk.FontDid);
            Assert.Equal(Vector4.One, pk.FontColor);
        }

        var levelOccurrences = Flatten(tree!).Where(e => e.Id == CharacterStatController.LevelId).ToList();
        Assert.Equal(2, levelOccurrences.Count);
        foreach (var level in levelOccurrences)
        {
            Assert.Equal(0x40000010u, level.FontDid);
            Assert.True(level.Outline);
            Assert.NotNull(level.FontColor);
            Assert.Equal(1f, level.FontColor!.Value.X, precision: 3);
            Assert.Equal(0.949f, level.FontColor!.Value.Y, precision: 2);
            Assert.Equal(0.498f, level.FontColor!.Value.Z, precision: 2);
        }

        var xpLabelOccurrences = Flatten(tree!).Where(e => e.Id == CharacterStatController.XpNextLabelId).ToList();
        Assert.Equal(2, xpLabelOccurrences.Count);
        foreach (var xpLabel in xpLabelOccurrences)
            Assert.Equal(0x40000000u, xpLabel.FontDid);

        var xpValueOccurrences = Flatten(tree!).Where(e => e.Id == CharacterStatController.XpNextValueId).ToList();
        Assert.Equal(2, xpValueOccurrences.Count);
        foreach (var xpValue in xpValueOccurrences)
            Assert.Equal(0x40000000u, xpValue.FontDid);

        var luminanceLabelOccurrences = Flatten(tree!).Where(e => e.Id == CharacterStatController.LuminanceLabelId).ToList();
        Assert.Equal(2, luminanceLabelOccurrences.Count);
        foreach (var luminanceLabel in luminanceLabelOccurrences)
        {
            Assert.Equal(0x40000000u, luminanceLabel.FontDid);
            Assert.Equal(Vector4.One, luminanceLabel.FontColor);
        }

        var luminanceValueOccurrences = Flatten(tree!).Where(e => e.Id == CharacterStatController.LuminanceValueId).ToList();
        Assert.Equal(2, luminanceValueOccurrences.Count);
        foreach (var luminanceValue in luminanceValueOccurrences)
        {
            Assert.Equal(0x40000000u, luminanceValue.FontDid);
            Assert.Equal(Vector4.One, luminanceValue.FontColor);
        }
    }

    [InstalledDatFact]
    public void StatListBox_AuthorsFiveRowTemplatesInSharedLayout()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        ElementInfo? tree = LayoutImporter.ImportInfos(dats, 0x2100002Eu);
        Assert.NotNull(tree);

        var listBoxes = Flatten(tree!).Where(e => e.Id == CharacterStatController.ListBoxId).ToList();
        Assert.Equal(2, listBoxes.Count);
        uint[] expected =
        {
            0x10000248u, 0x10000249u, 0x1000024Au, 0x1000024Bu, 0x1000024Cu,
        };
        foreach (ElementInfo listBox in listBoxes)
        {
            Assert.Equal(0f, listBox.X);
            Assert.Equal(112f, listBox.Y);
            Assert.Equal(300f, listBox.Width);
            Assert.Equal(160f, listBox.Height);

            Assert.Equal(CharacterStatController.ListScrollbarId, listBox.ScrollbarElementId);
            Assert.Equal(5, listBox.TemplateList.Count);
            foreach (uint id in expected)
            {
                Assert.Contains(
                    listBox.TemplateList,
                    t => t.TemplateLayoutId == 0x21000045u && t.TemplateElementId == id);
            }
        }

        // CT1 fix round: the ListBox's own authored scrollbar rect — the
        // "always reserved" gutter CT5/CT6 both depend on.
        var scrollbars = Flatten(tree!).Where(e => e.Id == CharacterStatController.ListScrollbarId).ToList();
        Assert.Equal(2, scrollbars.Count);
        foreach (ElementInfo scrollbar in scrollbars)
        {
            Assert.Equal(281f, scrollbar.X);
            Assert.Equal(16f, scrollbar.Width);
            Assert.Equal(160f, scrollbar.Height);
        }
    }

    [InstalledDatFact]
    public void AttributeRowTemplate_IconIsFlushLeftTwentyPixels_NameAndValueAreFixedColumns()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        ElementInfo? row = LayoutImporter.ImportInfos(dats, 0x21000045u, 0x10000248u);
        Assert.NotNull(row);

        Assert.Equal(282f, row!.Width);
        Assert.Equal(20f, row.Height);
        Assert.Equal(0x06004CC2u, row.StateMedia["Normal"].File);
        Assert.Equal(0x06000F93u, row.StateMedia["Highlight"].File);

        ElementInfo icon = Assert.Single(row.Children, c => c.Id == 0x10000129u);
        Assert.Equal(0f, icon.X);
        Assert.Equal(0f, icon.Y);
        Assert.Equal(20f, icon.Width);
        Assert.Equal(20f, icon.Height);

        ElementInfo name = Assert.Single(row.Children, c => c.Id == 0x1000012Au);
        Assert.Equal(25f, name.X);
        Assert.Equal(150f, name.Width);
        Assert.Equal(HJustify.Left, name.HJustify);
        Assert.Equal(0x40000001u, name.FontDid);

        ElementInfo value = Assert.Single(row.Children, c => c.Id == 0x1000012Bu);
        Assert.Equal(175f, value.X);
        Assert.Equal(100f, value.Width);
        Assert.Equal(HJustify.Right, value.HJustify);
        Assert.Equal(0x40000001u, value.FontDid);

        // Item 2: 7px gap between the value's right edge (275) and the row's
        // own right edge (282) — the authored margin the owner reported.
        Assert.Equal(7f, row.Width - (value.X + value.Width));
    }

    [InstalledDatFact]
    public void SkillSectionHeaderTemplates_MatchExistingSpriteConstants()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);

        (uint elementId, uint expectedSprite)[] headers =
        {
            (0x10000249u, 0x06000F90u), // SkillHeaderSpecializedSprite
            (0x1000024Au, 0x06000F86u), // SkillHeaderTrainedSprite
            (0x1000024Bu, 0x06000F98u), // SkillHeaderUntrainedSprite
            (0x1000024Cu, 0x06000F89u), // SkillHeaderUnusableSprite
        };
        foreach (var (elementId, expectedSprite) in headers)
        {
            ElementInfo? header = LayoutImporter.ImportInfos(dats, 0x21000045u, elementId);
            Assert.NotNull(header);
            Assert.Equal(280f, header!.Width);
            Assert.Equal(20f, header.Height);
            Assert.Equal(expectedSprite, header.StateMedia[""].File);
        }
    }

    [InstalledDatFact]
    public void TitlesPage_ElementRosterExists()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        ElementInfo? tree = LayoutImporter.ImportInfos(dats, 0x2100002Eu);
        Assert.NotNull(tree);

        ElementInfo page = Assert.Single(Flatten(tree!), e => e.Id == 0x10000539u);
        Assert.Equal(300f, page.Width);
        Assert.Equal(575f, page.Height);

        ElementInfo displayTitle = Assert.Single(page.Children, c => c.Id == 0x1000052Fu);
        Assert.Equal(0x40000001u, displayTitle.FontDid);
        Assert.Equal(HJustify.Center, displayTitle.HJustify);

        ElementInfo listBox = Assert.Single(page.Children, c => c.Id == 0x10000532u);
        Assert.Equal(5u, listBox.Type); // Type-5 ListBox
        Assert.Equal(0x10000533u, listBox.ScrollbarElementId);
        UiTemplateListEntry template = Assert.Single(listBox.TemplateList);
        Assert.Equal(0x2100005Eu, template.TemplateLayoutId);
        Assert.Equal(0x10000536u, template.TemplateElementId);

        ElementInfo setDisplayButton = Assert.Single(page.Children, c => c.Id == 0x10000535u);
        Assert.Equal(1u, setDisplayButton.Type); // Type-1 button
        Assert.Equal(65, setDisplayButton.MinWidth);
        Assert.Equal("Ghosted", setDisplayButton.DefaultStateName);
    }

    [InstalledDatFact]
    public void TitlesListAndPage_AuthorHasOriginalParentSize()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        ElementInfo? tree = LayoutImporter.ImportInfos(dats, 0x2100002Eu);
        Assert.NotNull(tree);

        ElementInfo page = Assert.Single(Flatten(tree!), e => e.Id == 0x10000539u);
        Assert.True(page.HasOriginalParentSize);

        ElementInfo listBox = Assert.Single(page.Children, c => c.Id == 0x10000532u);
        Assert.True(listBox.HasOriginalParentSize);
    }

    [InstalledDatFact]
    public void TitleRowTemplate_IsSingleLineTextRowWithNoIconColumn()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        ElementInfo? row = LayoutImporter.ImportInfos(dats, 0x2100005Eu, 0x10000536u);
        Assert.NotNull(row);

        Assert.Equal(270f, row!.Width);
        Assert.Equal(24f, row.Height);
        // DirectState (the "" key), not a named "Normal" state.
        Assert.Equal(0x06004CCAu, row.StateMedia[""].File);
        Assert.Equal(0x06001AAFu, row.StateMedia["Highlight"].File);

        ElementInfo text = Assert.Single(row.Children);
        Assert.Equal(0x10000537u, text.Id);
        Assert.Equal(270f, text.Width);
        Assert.Equal(24f, text.Height);
        Assert.Equal(HJustify.Left, text.HJustify);
        Assert.Equal(6, text.MarginLeft);
        Assert.Equal(6, text.MarginRight);
        Assert.Equal(0x40000001u, text.FontDid);
    }

    [InstalledDatFact]
    public void CharacterWindowRoot_AuthorsNoSizeConstraints()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        ElementInfo? tree = LayoutImporter.ImportInfos(dats, 0x2100002Eu);
        Assert.NotNull(tree);
        Assert.Equal(0x10000227u, tree!.Id);
        Assert.Equal(8u, tree.Type); // Type-8 TabControl

        Assert.Null(tree.MinWidth);
        Assert.Null(tree.MinHeight);
        Assert.Null(tree.MaxWidth);
        Assert.Null(tree.MaxHeight);
    }

    [InstalledDatFact]
    public void ChatWindowRoot_AuthorsExplicitSizeConstraints()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        ElementInfo? tree = LayoutImporter.ImportInfos(dats, 0x2100006Fu);
        Assert.NotNull(tree);
        Assert.Equal(0x10000600u, tree!.Id);

        Assert.Equal(300, tree.MinWidth);
        Assert.Equal(100, tree.MinHeight);
        Assert.Equal(2000, tree.MaxWidth);
        Assert.Equal(2000, tree.MaxHeight);
    }

    [InstalledDatFact]
    public void PanelHost_AuthorsFixedWidthAndBottomOnlyResizeContract()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        ElementInfo? host = LayoutImporter.ImportInfos(dats, 0x2100006Eu, 0x100005FEu);
        Assert.NotNull(host);
        Assert.Equal(310f, host!.Width);
        Assert.Equal(372f, host.Height);

        Assert.Equal(310, host.MinWidth);
        Assert.Equal(310, host.MaxWidth);
        Assert.Equal(372, host.MinHeight);
        Assert.Equal(1000, host.MaxHeight);

        Assert.Contains(host.Children, c => c.Id == 0x10000660u && c.Type == 9u); // Resizebar
        Assert.Contains(host.Children, c => c.Id == 0x1000065Cu && c.Type == 2u); // Dragbar
        Assert.Contains(host.Children, c => c.Id == 0x10000180u);

        ElementInfo? slot = LayoutImporter.ImportInfos(dats, 0x2100006Eu, 0x1000018Eu);
        Assert.NotNull(slot);
        Assert.Equal(300f, slot!.Width);
        Assert.Equal(362f, slot.Height);
        Assert.Null(slot.MinWidth);
        Assert.Null(slot.MinHeight);
        Assert.Null(slot.MaxWidth);
        Assert.Null(slot.MaxHeight);
    }

    [InstalledDatFact]
    public void TitleStringTable_ResolvesWarMageEndToEnd()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);

        bool gotMapper = dats.Portal.TryGet<DatReaderWriter.DBObjs.EnumMapper>(
            0x22000041u, out var titleEnumMapper);
        Assert.True(gotMapper);
        Assert.NotNull(titleEnumMapper);
        string raw = titleEnumMapper!.IdToStringMap[13u].ToString();
        Assert.Equal("ID_CharacterTitle_War_Mage", raw);

        uint hash = DatStringResolver.ComputeHash(raw);
        Assert.Equal(0x0543AF05u, hash);

        var resolver = new DatStringResolver(dats);
        string? resolved = resolver.Resolve(0x2300000Eu, hash);
        Assert.Equal("War Mage", resolved);
    }

    [InstalledDatFact]
    public void GenderHeritageDisplayNameTables_MatchTheRetailEnumMapperChain()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);

        bool gotMaster = dats.Portal.TryGet<DatReaderWriter.DBObjs.EnumIDMap>(
            (uint)dats.Portal.Header.MasterMapId, out var master);
        Assert.True(gotMaster);
        Assert.NotNull(master);
        Assert.True(master!.ClientEnumToID.TryGetValue(1u, out uint categoryDid));

        bool gotCategoryMap = dats.Portal.TryGet<DatReaderWriter.DBObjs.EnumIDMap>(categoryDid, out var categoryMap);
        Assert.True(gotCategoryMap);
        Assert.NotNull(categoryMap);

        Assert.True(categoryMap!.ClientEnumToID.TryGetValue(0x10000001u, out uint genderDid));
        Assert.Equal(0x2200000Au, genderDid);
        Assert.True(dats.Portal.TryGet<DatReaderWriter.DBObjs.EnumMapper>(genderDid, out var genderMapper));
        Assert.NotNull(genderMapper);

        foreach (var (id, raw) in genderMapper!.IdToStringMap)
        {
            string? expected = raw.Value == "Invalid" ? null : raw.Value;
            Assert.Equal(expected, CharacterIdentityText.GenderDisplayName((int)id));
        }

        Assert.True(categoryMap.ClientEnumToID.TryGetValue(0x10000002u, out uint heritageDid));
        Assert.Equal(0x2200000Bu, heritageDid);
        Assert.True(dats.Portal.TryGet<DatReaderWriter.DBObjs.EnumMapper>(heritageDid, out var heritageMapper));
        Assert.NotNull(heritageMapper);

        var overrides = new Dictionary<uint, string>
        {
            [2u] = "Gharu'ndim",
            [5u] = "Umbraen",
            [0xDu] = "Olthoi",
        };

        foreach (var (id, raw) in heritageMapper!.IdToStringMap)
        {
            string? expected = overrides.TryGetValue(id, out string? overridden)
                ? overridden
                : raw.Value == "Invalid" ? null : raw.Value;
            Assert.Equal(expected, CharacterIdentityText.HeritageGroupDisplayName((int)id));
        }
    }

    [InstalledDatFact]
    public void PkStatusKeys_ResolveExpectedAuthoredStrings()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        var resolver = new DatStringResolver(dats);

        string? pk = resolver.Resolve(0x23000001u,
            DatStringResolver.ComputeHash("ID_StatManagement_Header_PKStatus_PK"));
        string? pkLite = resolver.Resolve(0x23000001u,
            DatStringResolver.ComputeHash("ID_StatManagement_Header_PKStatus_PKL"));
        string? npk = resolver.Resolve(0x23000001u,
            DatStringResolver.ComputeHash("ID_StatManagement_Header_PKStatus_NPK"));

        Assert.Equal("Player Killer", pk);
        Assert.Equal("Player Killer Lite", pkLite);
        Assert.Equal("Non-Player Killer", npk);
    }

    [InstalledDatFact]
    public void AttributeAndVitalIconDids_MatchTheRetailEnumMapperChain()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);

        const uint attributeIconCategory = 0x10000002u;
        // CT5 fix round (NOTE d): AttrRows/VitalRows are now internal, so
        // this pin iterates THEM directly instead of a re-typed duplicate
        // literal array — a divergence between the two would previously
        // have gone undetected by this test.
        foreach (var (_, iconDid, statId) in CharacterStatController.AttrRows)
        {
            uint resolved = RetailDataIdResolver.Resolve(dats, statId, attributeIconCategory);
            Assert.Equal(iconDid, resolved);
        }

        const uint vitalIconCategory = 0x10000003u;
        foreach (var (_, iconDid, maxStatId) in CharacterStatController.VitalRows)
        {
            uint resolved = RetailDataIdResolver.Resolve(dats, maxStatId, vitalIconCategory);
            Assert.Equal(iconDid, resolved);
        }

        foreach (var (_, iconDid, maxStatId) in CharacterStatController.VitalRows)
        {
            uint currentStatId = maxStatId + 1u;
            uint resolved = RetailDataIdResolver.Resolve(dats, currentStatId, vitalIconCategory);
            Assert.Equal(iconDid, resolved);
        }
    }
}
