using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Ui;

namespace AcDream.App.Tests.UI.Layout;

public sealed class CreatureAppraisalRowsTests
{
    [Fact]
    public void FailedAssessmentShowsOnlyHealthPercentAndUnknownOtherValues()
    {
        var profile = Profile(
            health: 25u,
            healthMax: 100u,
            highlights: (ushort)0,
            colors: (ushort)0);

        IReadOnlyList<CreatureAppraisalRow> rows =
            CreatureAppraisalRows.Build(profile, success: false);

        Assert.All(
            rows,
            row => Assert.Equal(
                CreatureAppraisalValueStyle.Incomplete,
                row.Style));
        Assert.Equal("25 %", rows[6].Value);
        Assert.Equal("???", rows[0].Value);
        Assert.Equal("???", rows[7].Value);
        Assert.Equal("???", rows[8].Value);
    }

    [Fact]
    public void EnchantmentBitsSelectRetailPositiveAndNegativeStyles()
    {
        var profile = Profile(
            health: 100u,
            healthMax: 100u,
            highlights: (ushort)((1 << 0) | (1 << 6)),
            colors: (ushort)(1 << 0));

        IReadOnlyList<CreatureAppraisalRow> rows =
            CreatureAppraisalRows.Build(profile, success: true);

        Assert.Equal(CreatureAppraisalValueStyle.Positive, rows[0].Style);
        Assert.Equal(CreatureAppraisalValueStyle.Negative, rows[6].Style);
        Assert.Equal(CreatureAppraisalValueStyle.Normal, rows[1].Style);
    }

    [Fact]
    public void ExtraRatingsFollowRetailGroupingFormattingAndSeparators()
    {
        var properties = new PropertyBundle();
        properties.Ints[0x133u] = 35;
        properties.Ints[0x139u] = 7;
        properties.Ints[0x13Au] = 4;
        properties.Ints[0x134u] = 12;
        properties.Ints[0x13Bu] = 3;
        properties.Ints[0x13Cu] = 2;
        properties.Ints[0x15Eu] = 9;
        properties.Ints[0x15Fu] = 6;

        IReadOnlyList<CreatureAppraisalRow> rows =
            CreatureAppraisalRows.BuildExtra(properties, armorLevels: null, character: false);

        Assert.Equal(5, rows.Count);
        Assert.Equal(("", ""), (rows[0].Label, rows[0].Value));
        Assert.Equal(
            ("Dmg/CritDmg", "Rating: 35/4"),
            (rows[1].Label, rows[1].Value));
        Assert.Equal(
            ("Dmg/CritDmg", "Resist: 12/2"),
            (rows[2].Label, rows[2].Value));
        Assert.Equal(
            ("DoT/Life:", "Resist: 9/6"),
            (rows[3].Label, rows[3].Value));
        Assert.Equal(("", ""), (rows[4].Label, rows[4].Value));
    }

    [Fact]
    public void CritOnlyTriggersRatingRowWhileHealingBoostAloneDoesNot()
    {
        var critOnly = new PropertyBundle();
        critOnly.Ints[0x139u] = 8;

        IReadOnlyList<CreatureAppraisalRow> rows =
            CreatureAppraisalRows.BuildExtra(critOnly, armorLevels: null, character: false);

        Assert.Equal(3, rows.Count);
        Assert.Equal("Rating: 0/0", rows[1].Value);

        var healingOnly = new PropertyBundle();
        healingOnly.Ints[0x143u] = 20;
        Assert.Empty(
            CreatureAppraisalRows.BuildExtra(
                healingOnly, armorLevels: null, character: false));
    }


    [Fact]
    public void ArmorLevelTrioUsesRetailGroupingLabelsAndFormatPrecedingRatings()
    {
        var levels = new AppraiseInfoParser.ArmorLevel(
            Head: 100, Chest: 110, Abdomen: 120,
            UpperArm: 130, LowerArm: 140, Hand: 150,
            UpperLeg: 160, LowerLeg: 170, Foot: 180);
        var properties = new PropertyBundle();
        properties.Ints[0x133u] = 35; // DamageRating -> also triggers ratings block

        IReadOnlyList<CreatureAppraisalRow> rows =
            CreatureAppraisalRows.BuildExtra(properties, levels, character: true);

        // [0] spacer, [1..3] AL trio, [4] spacer, [5] rating, [6] spacer,
        // [7] legend.
        Assert.Equal(8, rows.Count);
        Assert.Equal(("", ""), (rows[0].Label, rows[0].Value));
        Assert.Equal(
            ("Head/Chest/Groin", "AL: 100/110/120"),
            (rows[1].Label, rows[1].Value));
        Assert.Equal(
            ("Bicep/Wrist/Hand", "AL: 130/140/150"),
            (rows[2].Label, rows[2].Value));
        Assert.Equal(
            ("Thigh/Shin/Foot", "AL: 160/170/180"),
            (rows[3].Label, rows[3].Value));
        Assert.Equal(("", ""), (rows[4].Label, rows[4].Value));
        Assert.Equal(
            ("Dmg/CritDmg", "Rating: 35/0"),
            (rows[5].Label, rows[5].Value));
        Assert.Equal(("", ""), (rows[6].Label, rows[6].Value));
        Assert.Equal(
            ("* = Unenchantable", string.Empty),
            (rows[7].Label, rows[7].Value));
    }

    [Theory]
    [InlineData(9998, "9998")]
    [InlineData(9999, "*0")]
    [InlineData(10123, "*124")]
    public void ArmorLevelPartRendersUnenchantableSentinelAtOrAbove9999(
        int value,
        string expected)
    {
        var levels = new AppraiseInfoParser.ArmorLevel(
            Head: value, Chest: 0, Abdomen: 0,
            UpperArm: 0, LowerArm: 0, Hand: 0,
            UpperLeg: 0, LowerLeg: 0, Foot: 0);

        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            new PropertyBundle(), levels, character: true);

        CreatureAppraisalRow row = Assert.Single(
            rows, r => r.Label == "Head/Chest/Groin");
        Assert.Equal($"AL: {expected}/0/0", row.Value);
    }

    [Fact]
    public void ArmorLevelRowMixesStarredAndPlainPartsIndependently()
    {
        var levels = new AppraiseInfoParser.ArmorLevel(
            Head: 50, Chest: 9999, Abdomen: 20000,
            UpperArm: 0, LowerArm: 0, Hand: 0,
            UpperLeg: 0, LowerLeg: 0, Foot: 0);

        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            new PropertyBundle(), levels, character: true);

        CreatureAppraisalRow row = Assert.Single(
            rows, r => r.Label == "Head/Chest/Groin");
        Assert.Equal("AL: 50/*0/*10001", row.Value);
    }

    [Fact]
    public void AllNineArmorLevelsZeroOrNegativeEmitsNoTrioAndNoSpacer()
    {
        var levels = new AppraiseInfoParser.ArmorLevel(
            Head: 0, Chest: 0, Abdomen: -5,
            UpperArm: 0, LowerArm: 0, Hand: 0,
            UpperLeg: 0, LowerLeg: 0, Foot: 0);

        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            new PropertyBundle(), levels, character: true);

        Assert.DoesNotContain(rows, r => r.Label.Contains("Groin"));
        Assert.DoesNotContain(rows, r => r.Label.Contains("Hand"));
        Assert.DoesNotContain(rows, r => r.Label.Contains("Foot"));
        Assert.Equal("* = Unenchantable", rows[^1].Label);
    }

    [Fact]
    public void ArmorLevelTrioAbsentWhenArmorLevelsIsNull()
    {
        var properties = new PropertyBundle();
        properties.Ints[0x133u] = 35;

        IReadOnlyList<CreatureAppraisalRow> rows =
            CreatureAppraisalRows.BuildExtra(properties, armorLevels: null, character: true);

        Assert.DoesNotContain(rows, r => r.Value.StartsWith("AL:", StringComparison.Ordinal));
    }

    [Fact]
    public void EachRatingRowGatesIndependently()
    {
        var critOnly = new PropertyBundle();
        critOnly.Ints[0x139u] = 1;
        IReadOnlyList<CreatureAppraisalRow> critRows =
            CreatureAppraisalRows.BuildExtra(critOnly, armorLevels: null, character: false);
        Assert.Equal(3, critRows.Count);
        Assert.Equal(("Dmg/CritDmg", "Rating: 0/0"), (critRows[1].Label, critRows[1].Value));

        var critResistOnly = new PropertyBundle();
        critResistOnly.Ints[0x13Bu] = 1;
        IReadOnlyList<CreatureAppraisalRow> critResistRows =
            CreatureAppraisalRows.BuildExtra(critResistOnly, armorLevels: null, character: false);
        Assert.Equal(3, critResistRows.Count);
        Assert.Equal(
            ("Dmg/CritDmg", "Resist: 0/0"),
            (critResistRows[1].Label, critResistRows[1].Value));

        // 350/351 (DoT/Life) alone, independent of the other two families.
        var dotLifeOnly = new PropertyBundle();
        dotLifeOnly.Ints[0x15Eu] = 4;
        IReadOnlyList<CreatureAppraisalRow> dotLifeRows =
            CreatureAppraisalRows.BuildExtra(dotLifeOnly, armorLevels: null, character: false);
        Assert.Equal(3, dotLifeRows.Count);
        Assert.Equal(
            ("DoT/Life:", "Resist: 4/0"),
            (dotLifeRows[1].Label, dotLifeRows[1].Value));
    }

    [Fact]
    public void LegendIsAbsentOnMonsterPathEvenWithRatingsShown()
    {
        var properties = new PropertyBundle();
        properties.Ints[0x133u] = 35;

        IReadOnlyList<CreatureAppraisalRow> rows =
            CreatureAppraisalRows.BuildExtra(properties, armorLevels: null, character: false);

        Assert.DoesNotContain(rows, r => r.Label == "* = Unenchantable");
    }

    [Fact]
    public void LegendIsAlwaysLastOnCharacterPathEvenWithNoOtherExtras()
    {
        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            new PropertyBundle(), armorLevels: null, character: true);

        CreatureAppraisalRow only = Assert.Single(rows);
        Assert.Equal(("* = Unenchantable", string.Empty), (only.Label, only.Value));
    }


    [Fact]
    public void SocietyRowAbsentWhenFaction1BitsPropertyNotPresent()
    {
        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            new PropertyBundle(), armorLevels: null, character: true);

        Assert.DoesNotContain(rows, r => r.Label == "Society:");
    }

    [Theory]
    [InlineData(0x1, 287u, "Celestial Hand")]
    [InlineData(0x2, 288u, "Eldrytch Web")]
    [InlineData(0x4, 289u, "Radiant Blood")]
    public void SocietyRowSelectsNameAndRankPropertyPerTargetBit(
        int targetBit, uint rankPropertyId, string expectedName)
    {
        var properties = new PropertyBundle();
        properties.Ints[281u] = targetBit;
        properties.Ints[rankPropertyId] = 50; // Initiate band

        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            properties, armorLevels: null, character: true);

        CreatureAppraisalRow row = Assert.Single(rows, r => r.Label == "Society:");
        Assert.Equal($"{expectedName} ~ Initiate", row.Value);
    }

    [Theory]
    [InlineData(0x8)]
    [InlineData(0x10)]
    public void SocietyRowFallsBackToUnrecognizedForUnknownBitCombinations(int bits)
    {
        var properties = new PropertyBundle();
        properties.Ints[281u] = bits;

        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            properties, armorLevels: null, character: true, localFactionBits: 0x1);

        CreatureAppraisalRow row = Assert.Single(rows, r => r.Label == "Society:");
        Assert.Equal("???", row.Value);
        Assert.Equal(CreatureAppraisalValueStyle.Normal, row.Style);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, " ~ Initiate")]
    [InlineData(100, " ~ Initiate")]
    [InlineData(101, " ~ Adept")]
    [InlineData(300, " ~ Adept")]
    [InlineData(301, " ~ Knight")]
    [InlineData(600, " ~ Knight")]
    [InlineData(601, " ~ Lord")]
    [InlineData(1000, " ~ Lord")]
    [InlineData(1001, " ~ Master")]
    [InlineData(1500, " ~ Master")]
    [InlineData(1501, "")]
    public void SocietyRankBandSuffixMatchesRetailInclusiveBoundaries(
        int rank, string expectedSuffix)
    {
        var properties = new PropertyBundle();
        properties.Ints[281u] = 0x1;
        properties.Ints[287u] = rank;

        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            properties, armorLevels: null, character: true);

        CreatureAppraisalRow row = Assert.Single(rows, r => r.Label == "Society:");
        Assert.Equal("Celestial Hand" + expectedSuffix, row.Value);
    }

    [Theory]
    [InlineData(0x1, 0x1, CreatureAppraisalValueStyle.Positive)]
    [InlineData(0x1, 0x2, CreatureAppraisalValueStyle.Negative)]
    [InlineData(0x1, 0x4, CreatureAppraisalValueStyle.Negative)]
    [InlineData(0x1, 0x0, CreatureAppraisalValueStyle.Normal)]
    [InlineData(0x2, 0x2, CreatureAppraisalValueStyle.Positive)]
    [InlineData(0x2, 0x1, CreatureAppraisalValueStyle.Negative)]
    [InlineData(0x4, 0x4, CreatureAppraisalValueStyle.Positive)]
    [InlineData(0x4, 0x2, CreatureAppraisalValueStyle.Negative)]
    public void SocietyColorReflectsLocalPlayerFactionBitsAgainstTarget(
        int targetBit, int localFactionBits, CreatureAppraisalValueStyle expected)
    {
        var properties = new PropertyBundle();
        properties.Ints[281u] = targetBit;

        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            properties, armorLevels: null, character: true, localFactionBits);

        CreatureAppraisalRow row = Assert.Single(rows, r => r.Label == "Society:");
        Assert.Equal(expected, row.Style);
    }

    [Fact]
    public void SocietyColorPrioritizesSameBitMatchEvenWhenLocalHasOtherBitsToo()
    {
        var properties = new PropertyBundle();
        properties.Ints[281u] = 0x1;

        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            properties, armorLevels: null, character: true, localFactionBits: 0x1 | 0x2);

        CreatureAppraisalRow row = Assert.Single(rows, r => r.Label == "Society:");
        Assert.Equal(CreatureAppraisalValueStyle.Positive, row.Style);
    }

    [Fact]
    public void AllegianceCascadeAbsentWhenAllegianceRankBelowOne()
    {
        var properties = new PropertyBundle();
        properties.Ints[30u] = 0;
        properties.Strings[21u] = "Should Not Appear";

        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            properties, armorLevels: null, character: true);

        Assert.DoesNotContain(rows, r => r.Label is "Monarch:" or "Patron:"
            or "Monarch/Patron:" or "Alleg. Monarch:");
    }

    [Theory]
    [InlineData(0, "0 Followers")]
    [InlineData(1, "1 Follower")]
    [InlineData(2, "2 Followers")]
    [InlineData(-5, "0 Followers")]
    public void AllegianceCascadeShowsClampedFollowerCountWhenMonarchsTitleAbsent(
        int followers, string expected)
    {
        var properties = new PropertyBundle();
        properties.Ints[30u] = 1;
        properties.Ints[35u] = followers;

        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            properties, armorLevels: null, character: true);

        CreatureAppraisalRow row = Assert.Single(rows, r => r.Label == "Alleg. Monarch:");
        Assert.Equal(expected, row.Value);
    }

    [Fact]
    public void AllegianceCascadeShowsMonarchOnlyWhenPatronsTitleAbsent()
    {
        var properties = new PropertyBundle();
        properties.Ints[30u] = 1;
        properties.Strings[21u] = "Baroness Aluvia";

        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            properties, armorLevels: null, character: true);

        Assert.Equal(
            "Baroness Aluvia", Assert.Single(rows, r => r.Label == "Monarch:").Value);
        Assert.DoesNotContain(rows, r => r.Label is "Patron:" or "Monarch/Patron:");
    }

    [Fact]
    public void AllegianceCascadeCombinesMonarchAndPatronWhenTitlesAreOrdinallyEqual()
    {
        var properties = new PropertyBundle();
        properties.Ints[30u] = 1;
        properties.Strings[21u] = "Same Title";
        properties.Strings[35u] = "Same Title";

        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            properties, armorLevels: null, character: true);

        Assert.Equal(
            "Same Title", Assert.Single(rows, r => r.Label == "Monarch/Patron:").Value);
        Assert.DoesNotContain(rows, r => r.Label is "Monarch:" or "Patron:");
    }

    [Fact]
    public void AllegianceCascadeSplitsMonarchAndPatronWhenTitlesDiffer()
    {
        var properties = new PropertyBundle();
        properties.Ints[30u] = 1;
        properties.Strings[21u] = "Monarch Title";
        properties.Strings[35u] = "Patron Title";

        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            properties, armorLevels: null, character: true);

        Assert.Equal(
            "Monarch Title", Assert.Single(rows, r => r.Label == "Monarch:").Value);
        Assert.Equal(
            "Patron Title", Assert.Single(rows, r => r.Label == "Patron:").Value);
        Assert.DoesNotContain(rows, r => r.Label == "Monarch/Patron:");
    }

    [Fact]
    public void ConfigurableExtrasEachAppearOnlyWhenTheirOwnPropertyIsPresent()
    {
        var properties = new PropertyBundle();
        properties.Strings[10u] = "The Fellows";
        properties.Strings[43u] = "1/1/2023"; // DateOfBirth (String table)
        properties.Ints[125u] = 90; // Age, seconds
        properties.Ints[181u] = 7;
        properties.Ints[192u] = 42;
        properties.Ints[262u] = 3;
        // Int 43 (NumDeaths) deliberately absent — separate Int-table id
        // from the String-table DateOfBirth id above; must not leak a row.

        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            properties, armorLevels: null, character: true);

        Assert.Equal(
            "The Fellows", Assert.Single(rows, r => r.Label == "Fellowship:").Value);
        Assert.Equal(
            "1/1/2023",
            Assert.Single(rows, r => r.Label == "Arrived in Dereth:").Value);
        Assert.Equal(
            RetailDurationText.Format(90),
            Assert.Single(rows, r => r.Label == "Time in Dereth:").Value);
        Assert.Equal("7", Assert.Single(rows, r => r.Label == "Chess Rank:").Value);
        Assert.Equal("42", Assert.Single(rows, r => r.Label == "Fishing Skill:").Value);
        Assert.Equal("3", Assert.Single(rows, r => r.Label == "Titles Earned:").Value);
        Assert.DoesNotContain(rows, r => r.Label == "Deaths:");
    }

    [Fact]
    public void ConfigurableExtrasAllAbsentLeavesOnlyTheLegend()
    {
        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            new PropertyBundle(), armorLevels: null, character: true);

        CreatureAppraisalRow only = Assert.Single(rows);
        Assert.Equal("* = Unenchantable", only.Label);
    }

    [Theory]
    [InlineData(0, "Has never died")]
    [InlineData(-3, "Has never died")]
    [InlineData(1, "1")]
    [InlineData(5, "5")]
    public void DeathsRowShowsHasNeverDiedAtOrBelowZeroButKeepsTheSameLabel(
        int deaths, string expected)
    {
        var properties = new PropertyBundle();
        properties.Ints[43u] = deaths; // NumDeaths (Int table)

        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            properties, armorLevels: null, character: true);

        CreatureAppraisalRow row = Assert.Single(rows, r => r.Label == "Deaths:");
        Assert.Equal(expected, row.Value);
    }

    [Fact]
    public void MonsterPathNeverGainsSocietyAllegianceOrConfigurableExtraRows()
    {
        var properties = new PropertyBundle();
        properties.Ints[281u] = 0x1;
        properties.Ints[30u] = 5;
        properties.Strings[21u] = "Monarch Title";
        properties.Strings[10u] = "Fellows";
        properties.Strings[43u] = "1/1/2023";
        properties.Ints[125u] = 90;
        properties.Ints[181u] = 7;
        properties.Ints[192u] = 42;
        properties.Ints[43u] = 2;
        properties.Ints[262u] = 3;

        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            properties, armorLevels: null, character: false, localFactionBits: 0x1);

        Assert.DoesNotContain(rows, r => r.Label == "Society:");
        Assert.DoesNotContain(rows, r => r.Label is "Monarch:" or "Patron:"
            or "Monarch/Patron:" or "Alleg. Monarch:");
        Assert.DoesNotContain(rows, r => r.Label == "Fellowship:");
        Assert.DoesNotContain(rows, r => r.Label == "Arrived in Dereth:");
        Assert.DoesNotContain(rows, r => r.Label == "Time in Dereth:");
        Assert.DoesNotContain(rows, r => r.Label == "Chess Rank:");
        Assert.DoesNotContain(rows, r => r.Label == "Fishing Skill:");
        Assert.DoesNotContain(rows, r => r.Label == "Deaths:");
        Assert.DoesNotContain(rows, r => r.Label == "Titles Earned:");
        Assert.DoesNotContain(rows, r => r.Label == "* = Unenchantable");
    }

    [Fact]
    public void CompleteCharacterExtrasOrderingMatchesRetailRowSequence()
    {
        var properties = new PropertyBundle();
        properties.Ints[281u] = 0x1;
        properties.Ints[287u] = 50;
        properties.Ints[30u] = 5;
        properties.Strings[21u] = "Monarch Title";
        properties.Strings[35u] = "Patron Title";
        properties.Ints[0x133u] = 10;
        properties.Strings[10u] = "Fellows";
        properties.Ints[43u] = 0;
        var levels = new AppraiseInfoParser.ArmorLevel(
            Head: 1, Chest: 0, Abdomen: 0,
            UpperArm: 0, LowerArm: 0, Hand: 0,
            UpperLeg: 0, LowerLeg: 0, Foot: 0);

        IReadOnlyList<CreatureAppraisalRow> rows = CreatureAppraisalRows.BuildExtra(
            properties, levels, character: true, localFactionBits: 0x1);

        Assert.Equal(
            [
                "Society:",
                "Monarch:",
                "Patron:",
                "", // AL trio leading spacer
                "Head/Chest/Groin",
                "Bicep/Wrist/Hand",
                "Thigh/Shin/Foot",
                "", // ratings leading spacer
                "Dmg/CritDmg",
                "", // ratings trailing spacer
                "Fellowship:",
                "Deaths:",
                "* = Unenchantable",
            ],
            rows.Select(r => r.Label));
        Assert.Equal(CreatureAppraisalValueStyle.Positive, rows[0].Style);
    }

    [Fact]
    public void AuthoredRowTemplateCarriesRetailOverlappingLabelValueGeometry()
    {
        ElementInfo template =
            FixtureLoader.LoadExaminationRowTemplateInfos();

        Assert.Equal(CreatureAppraisalRowTemplateFactory.TemplateId, template.Id);
        Assert.Equal((292f, 20f), (template.Width, template.Height));
        ElementInfo label = Assert.Single(
            template.Children,
            child => child.Id == CreatureAppraisalRowTemplateFactory.LabelId);
        ElementInfo value = Assert.Single(
            template.Children,
            child => child.Id == CreatureAppraisalRowTemplateFactory.ValueId);
        Assert.Equal((0f, 128f), (label.X, label.Width));
        Assert.Equal((27f, 256f), (value.X, value.Width));
        Assert.Equal(0x40000001u, label.FontDid);
        Assert.Equal(0x40000001u, value.FontDid);
    }

    [Fact]
    public void CreatureNameResolverUsesLoadedRetailMappingAndSafeFallback()
    {
        var resolver = new CreatureDisplayNameResolver(
            new Dictionary<uint, string> { [77u] = "Ghost" });

        Assert.Equal("Ghost", resolver.Resolve(77));
        Assert.Equal(string.Empty, resolver.Resolve(0));
        Assert.Equal(string.Empty, resolver.Resolve(999));
    }

    [Fact]
    public void LayeredTemplateSeparatesChromeFromForegroundText()
    {
        var templates = new CreatureAppraisalRowTemplateFactory(
            FixtureLoader.LoadExaminationRowTemplateInfos(),
            _ => (0u, 0, 0),
            defaultFont: null);
        var row = new CreatureAppraisalRow(
            "Strength",
            "120",
            CreatureAppraisalValueStyle.Normal);

        UiTemplateListSlot background = templates.Create(
            row,
            CreatureAppraisalRowLayer.Background);
        UiTemplateListSlot foreground = templates.Create(
            row,
            CreatureAppraisalRowLayer.Foreground);

        Assert.Empty(((UiText)background.Content.FindElement(
            CreatureAppraisalRowTemplateFactory.LabelId)!).LinesProvider());
        Assert.Empty(((UiText)background.Content.FindElement(
            CreatureAppraisalRowTemplateFactory.ValueId)!).LinesProvider());
        Assert.False(
            Assert.IsType<UiDatElement>(foreground.Content.Root).MediaVisible);
        Assert.Equal(
            "Strength",
            Assert.Single(((UiText)foreground.Content.FindElement(
                CreatureAppraisalRowTemplateFactory.LabelId)!)
                .LinesProvider()).Text);
        Assert.Equal(
            "120",
            Assert.Single(((UiText)foreground.Content.FindElement(
                CreatureAppraisalRowTemplateFactory.ValueId)!)
                .LinesProvider()).Text);
    }

    private static AppraiseInfoParser.CreatureProfile Profile(
        uint health,
        uint healthMax,
        ushort highlights,
        ushort colors)
        => new(
            Flags: 0x09u,
            Health: health,
            HealthMax: healthMax,
            Strength: null,
            Endurance: null,
            Quickness: null,
            Coordination: null,
            Focus: null,
            Self: null,
            Stamina: 50u,
            Mana: 75u,
            StaminaMax: 100u,
            ManaMax: 100u,
            AttributeHighlights: highlights,
            AttributeColors: colors);
}
