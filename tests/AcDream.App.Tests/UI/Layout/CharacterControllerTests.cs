using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Properties;

namespace AcDream.App.Tests.UI.Layout;

public sealed class CharacterControllerTests
{
    [Fact]
    public void AuthoredPanel_BindsTextScrollbarAndClose()
    {
        ImportedLayout layout = FixtureLoader.LoadCharacterInformation();
        int closes = 0;

        CharacterController.Bind(
            layout, SampleData.SampleCharacter, close: () => closes++);

        UiText text = Assert.IsType<UiText>(
            layout.FindElement(CharacterController.MainTextId));
        UiScrollbar scrollbar = Assert.IsType<UiScrollbar>(
            layout.FindElement(CharacterController.ScrollbarId));
        Assert.Same(text.Scroll, scrollbar.Model);
        Assert.False(text.PreserveEndOnLayout);
        Assert.Contains("You were born on", VisibleText(text));

        UiButton close = Assert.IsType<UiButton>(
            layout.FindElement(CharacterController.CloseId));
        close.OnEvent(new UiEvent(0, close, UiEventType.Click));
        Assert.Equal(1, closes);
    }

    [Fact]
    public void Report_UsesRetailContentInsteadOfInventedCharacterSheetSummary()
    {
        string report = Report(SampleData.SampleCharacter());

        Assert.DoesNotContain("Studio Player", report);
        Assert.DoesNotContain("Level 126", report);
        Assert.DoesNotContain("Health:", report);
        Assert.DoesNotContain("Augmentations", report);
        Assert.DoesNotContain("Encumbrance", report);

        Assert.Contains("You have died 42 times.", report);
        Assert.Contains("Natural Resistances:", report);
        Assert.Contains("Drain Resistances:", report);
        Assert.Contains("Regeneration Bonus:", report);
        Assert.Contains("Innate Strength: 200", report);
        Assert.Contains("Innate Coordination: 10", report);
        Assert.Contains("Chess Rank: 12", report);
        Assert.Contains("Fishing Skill: 4", report);
        Assert.Contains("Your melee mastery is Swords.", report);
        Assert.Contains("You are not overburdened at this time.", report);
    }

    [Theory]
    [InlineData(200, "None")]
    [InlineData(201, "Poor")]
    [InlineData(260, "Poor")]
    [InlineData(261, "Mediocre")]
    [InlineData(321, "Hardy")]
    [InlineData(381, "Resilient")]
    [InlineData(441, "Indomitable")]
    public void ResistanceGrade_UsesRetailInclusiveBoundaries(
        int value, string expected)
        => Assert.Equal(expected, CharacterController.ResistanceGrade(
            value, 200, 260, 320, 380, 440));

    // The grades read Strength and Endurance without enchantments (innate
    // plus raised); the burden reads Strength with them.
    [Fact]
    public void Report_GradesReadAttributesWithoutEnchantments()
    {
        string report = Report(new CharacterSheet
        {
            // With enchantments 300 + 300 would be Indomitable for all three.
            Strength = 300,
            Endurance = 300,
            AttributeBaseValues = [200, 50, 10, 10, 10, 10],
            BurdenCurrent = 40_000,
        });

        // 200 + 50 = 250 and 200 + 2 * 50 = 300: both Poor.
        Assert.Contains("Natural Resistances: Poor", report);
        Assert.Contains("Drain Resistances: Poor", report);
        Assert.Contains("Regeneration Bonus: Poor", report);
        // Capacity at 300 Strength is 45,000 (at 200 it would be 30,000).
        Assert.Contains("You are not overburdened at this time.", report);
    }

    // Every whole number is grouped by thousands; a character who never
    // played chess is ranked 1400.
    [Fact]
    public void Report_GroupsIntegersAndRanksAnUnrankedChessPlayer1400()
    {
        Assert.Contains("Chess Rank: 1,400", Report(new CharacterSheet()));

        string report = Report(new CharacterSheet
        {
            ChessRank = 1_850,
            Deaths = 1_234,
            FishingSkill = 2_000,
        });
        Assert.Contains("Chess Rank: 1,850", report);
        Assert.Contains("died 1,234 times", report);
        Assert.Contains("Fishing Skill: 2,000", report);

        string burdened = Report(new CharacterSheet { Strength = 100, BurdenCurrent = 20_000 });
        Assert.Contains("overburdened by 5,000 burden units", burdened);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "1 second")]
    [InlineData(61, "1 minute 1 second")]
    [InlineData(3_196_800, "1 month 1 week")]
    [InlineData(31_536_000, "1 year")]
    public void Duration_UsesRetailUnitDecomposition(int seconds, string expected)
        => Assert.Equal(expected, CharacterController.FormatRetailDuration(seconds));

    [Fact]
    public void DeathCount_UsesRetailSpecialCases()
    {
        Assert.Contains("never died", Report(new CharacterSheet { Deaths = 0 }));
        Assert.Contains("only once", Report(new CharacterSheet { Deaths = 1 }));
        Assert.Contains("died twice", Report(new CharacterSheet { Deaths = 2 }));
        Assert.Contains("died 3 times", Report(new CharacterSheet { Deaths = 3 }));
    }

    [Fact]
    public void EmptyLayout_GetsFallbackMainText()
    {
        ImportedLayout layout = FakeLayout();
        CharacterController.Bind(layout, SampleData.SampleCharacter);
        Assert.Contains(layout.Root.Children.OfType<UiText>(),
            text => text.EventId == CharacterController.MainTextId);
    }

    [Fact]
    public void Stable_draws_reuse_report_until_source_or_layout_changes()
    {
        ImportedLayout layout = FixtureLoader.LoadCharacterInformation();
        int builds = 0;
        int deaths = 1;
        Action? changed = null;
        using CharacterInformationUiController controller =
            CharacterController.Bind(
                layout,
                () =>
                {
                    builds++;
                    return new CharacterSheet { Deaths = deaths };
                },
                subscribeChanged: handler =>
                {
                    changed = handler;
                    return new TestSubscription();
                });
        UiText text = Assert.IsType<UiText>(
            layout.FindElement(CharacterController.MainTextId));

        IReadOnlyList<UiText.Line> first = text.LinesProvider();
        IReadOnlyList<UiText.Line> second = text.LinesProvider();
        Assert.Same(first, second);
        Assert.Equal(1, builds);

        deaths = 2;
        changed!();
        IReadOnlyList<UiText.Line> changedLines = text.LinesProvider();
        Assert.NotSame(first, changedLines);
        Assert.Equal(2, builds);

        text.Width -= 24f;
        IReadOnlyList<UiText.Line> resized = text.LinesProvider();
        Assert.NotSame(changedLines, resized);
        Assert.Equal(2, builds);
    }

    [Fact]
    public void Report_InnateLineShowsStartingValuesNotCurrentOnes()
    {
        string report = Report(new CharacterSheet
        {
            Strength = 290,
            Endurance = 250,
            Coordination = 240,
            Quickness = 230,
            Focus = 220,
            Self = 210,
            AttributeInnateValues = [100, 60, 50, 40, 30, 10],
        });

        Assert.Contains("Innate Strength: 100", report);
        Assert.Contains("Innate Endurance: 60", report);
        Assert.Contains("Innate Coordination: 50", report);
        Assert.Contains("Innate Quickness: 40", report);
        Assert.Contains("Innate Focus: 30", report);
        Assert.Contains("Innate Self: 10", report);
    }

    [Theory]
    [InlineData(PropertyInt.AugmentationSpecializeArmorTinkering, "Armor Tinkering", "Magic Item Tinkering")]
    [InlineData(PropertyInt.AugmentationSpecializeMagicItemTinkering, "Magic Item Tinkering", "Armor Tinkering")]
    public void Report_TinkeringAugmentationUsesMatchingProperty(
        PropertyInt property, string expected, string other)
    {
        var sheet = new CharacterSheet
        {
            CharacterInfoProperties = new Dictionary<uint, int> { [(uint)property] = 1 },
        };
        CharacterInfoStrings strings = CharacterInfoStrings.English with
        {
            AugmentationText = new Dictionary<string, string[]>
            {
                ["ID_CharacterInfo_Augmentation_Spec_ArmorTinkering"] = ["Armor Tinkering\n"],
                ["ID_CharacterInfo_Augmentation_Spec_MagicItemTinkering"] = ["Magic Item Tinkering\n"],
            },
        };

        string report = CharacterController.BuildReport(sheet, strings);

        Assert.Contains(expected, report);
        Assert.DoesNotContain(other, report);
    }

    // Issue #255: each specialization gem property shows its own skill's line.
    [Theory]
    [InlineData(PropertyInt.AugmentationSpecializeSalvaging, "Salvaging")]
    [InlineData(PropertyInt.AugmentationSpecializeItemTinkering, "ItemTinkering")]
    [InlineData(PropertyInt.AugmentationSpecializeArmorTinkering, "ArmorTinkering")]
    [InlineData(PropertyInt.AugmentationSpecializeMagicItemTinkering, "MagicItemTinkering")]
    [InlineData(PropertyInt.AugmentationSpecializeWeaponTinkering, "WeaponTinkering")]
    [InlineData(PropertyInt.AugmentationSpecializeGearcraft, "Gearcraft")]
    public void Report_SpecializationAugmentationUsesMatchingSkillLine(
        PropertyInt property, string skill)
    {
        string[] skills =
        [
            "Salvaging", "ItemTinkering", "ArmorTinkering",
            "MagicItemTinkering", "WeaponTinkering", "Gearcraft",
        ];
        var sheet = new CharacterSheet
        {
            CharacterInfoProperties = new Dictionary<uint, int> { [(uint)property] = 1 },
        };
        CharacterInfoStrings strings = CharacterInfoStrings.English with
        {
            AugmentationText = skills.ToDictionary(
                name => "ID_CharacterInfo_Augmentation_Spec_" + name,
                name => new[] { "<" + name + ">\n" }),
        };

        string report = CharacterController.BuildReport(sheet, strings);

        Assert.Contains("<" + skill + ">", report);
        foreach (string other in skills.Where(name => name != skill))
            Assert.DoesNotContain("<" + other + ">", report);
    }

    [Fact]
    public void Report_DoesNotAppendUnusedValueAfterSingleFragmentAugmentation()
    {
        var sheet = new CharacterSheet
        {
            CharacterInfoProperties = new Dictionary<uint, int>
            {
                [(uint)PropertyInt.AugmentationDamageBonus] = 1,
                [(uint)PropertyInt.AugmentationJackOfAllTrades] = 1,
            },
        };
        CharacterInfoStrings strings = CharacterInfoStrings.English with
        {
            AugmentationText = new Dictionary<string, string[]>
            {
                ["ID_CharacterInfo_Augmentation_DamageBonus"] = ["Damage bonus.\n"],
                ["ID_CharacterInfo_Augmentation_JackOfAllTrades"] = ["Jack of All Trades.\n"],
            },
        };

        string report = CharacterController.BuildReport(sheet, strings);

        Assert.Contains("Damage bonus.\nJack of All Trades.", report);
        Assert.DoesNotContain("Damage bonus.\n1Jack of All Trades.", report);
    }

    [Fact]
    public void Report_ListsManaFlowAuraUnderLuminanceHeader()
    {
        string report = CharacterController.BuildReport(
            Sheet((PropertyInt.LumAugItemManaUsage, 4)), LuminanceStrings());

        Assert.Contains(
            "Luminance Augmentations:\n\n"
            + "Aura of Mana Flow. The mana consumption of your items is decreased by a rating of 20.",
            report);
    }

    [Fact]
    public void Report_WritesLuminanceHeaderWithoutAnyAura()
    {
        string report = CharacterController.BuildReport(
            new CharacterSheet(), LuminanceStrings());

        Assert.Contains("Luminance Augmentations:", report);
        Assert.DoesNotContain("Aura of", report);
    }

    [Theory]
    [InlineData(3, "Aura of Valor. Damage 3.", null)]
    [InlineData(5, "Aura of Valor. Damage 5.", null)]
    [InlineData(8, "Aura of Valor. Damage 5.", "Aura of Destruction. Seer damage 3.")]
    public void Report_SplitsTieredAuraRanksPastFiveIntoSeerLine(
        int ranks, string baseLine, string? seerLine)
    {
        string report = CharacterController.BuildReport(
            Sheet((PropertyInt.LumAugDamageRating, ranks)), LuminanceStrings());

        Assert.Contains(baseLine, report);
        if (seerLine is null)
            Assert.DoesNotContain("Aura of Destruction", report);
        else
            Assert.Contains(baseLine + "\n\n" + seerLine, report);
    }

    [Fact]
    public void Report_ScalesAndOrdersLuminanceAurasBeforeOtherAugmentations()
    {
        string report = CharacterController.BuildReport(
            Sheet(
                (PropertyInt.AugmentationInnateStrength, 1),
                (PropertyInt.LumAugAllSkills, 2),
                (PropertyInt.LumAugSkilledSpec, 3),
                (PropertyInt.LumAugItemManaGain, 2),
                (PropertyInt.SummoningMastery, 1)),
            LuminanceStrings());

        int summoning = report.IndexOf("Your summoning mastery is Primalist.", StringComparison.Ordinal);
        int header = report.IndexOf("Luminance Augmentations:", StringComparison.Ordinal);
        int manaGain = report.IndexOf("Aura of Mana Infusion. Mana gain 10.", StringComparison.Ordinal);
        int specialist = report.IndexOf("Aura of the Specialist. Spec 6.", StringComparison.Ordinal);
        int world = report.IndexOf("Aura of the World. All skills 2.", StringComparison.Ordinal);
        int strength = report.IndexOf("Innate Strength augmented", StringComparison.Ordinal);
        Assert.True(summoning >= 0 && summoning < header, report);
        Assert.True(header < manaGain, report);
        Assert.True(manaGain < specialist, report);
        Assert.True(specialist < world, report);
        Assert.True(world < strength, report);
    }

    [Fact]
    public void FromDat_ResolvesLuminanceStrings()
    {
        CharacterInfoStrings strings = CharacterInfoStrings.FromDat(
            key => key.Contains("Luminance", StringComparison.Ordinal)
                ? [key + ": ", "\n"]
                : null);

        Assert.True(strings.AugmentationText.ContainsKey(
            "ID_CharacterInfo_Luminance_Header"));
        Assert.True(strings.AugmentationText.ContainsKey(
            "ID_CharacterInfo_Luminance_Base_Mana_Use"));
        Assert.True(strings.AugmentationText.ContainsKey(
            "ID_CharacterInfo_Luminance_Spec_Crit_Reduction"));
        Assert.Contains("ID_CharacterInfo_Luminance_Base_Mana_Use: 15",
            CharacterController.BuildReport(
                Sheet((PropertyInt.LumAugItemManaUsage, 3)), strings));
    }

    private static CharacterSheet Sheet(params (PropertyInt Property, int Value)[] values)
        => new()
        {
            CharacterInfoProperties = values.ToDictionary(
                value => (uint)value.Property, value => value.Value),
        };

    private static CharacterInfoStrings LuminanceStrings()
        => CharacterInfoStrings.English with
        {
            AugmentationText = new Dictionary<string, string[]>
            {
                ["ID_CharacterInfo_Luminance_Header"] = ["Luminance Augmentations:\n\n"],
                ["ID_CharacterInfo_Luminance_Base_Damage"] = ["Aura of Valor. Damage ", ".\n\n"],
                ["ID_CharacterInfo_Luminance_Spec_Damage"] = ["Aura of Destruction. Seer damage ", ".\n\n"],
                ["ID_CharacterInfo_Luminance_Base_Mana_Use"] =
                    ["Aura of Mana Flow. The mana consumption of your items is decreased by a rating of ", ".\n\n"],
                ["ID_CharacterInfo_Luminance_Base_Mana_Gain"] = ["Aura of Mana Infusion. Mana gain ", ".\n\n"],
                ["ID_CharacterInfo_Luminance_Spec_Skilled_Spec"] = ["Aura of the Specialist. Spec ", ".\n\n"],
                ["ID_CharacterInfo_Luminance_Base_All_Skills"] = ["Aura of the World. All skills ", ".\n\n"],
                ["ID_CharacterInfo_Augmentation_Attribute_Strength"] =
                    ["Innate Strength augmented ", " {time[1]|times}.\n\n"],
            },
        };

    private static string Report(CharacterSheet sheet)
        => CharacterController.BuildReport(sheet, CharacterInfoStrings.English);

    private static string VisibleText(UiText text)
        => string.Join('\n', text.LinesProvider().Select(line => line.Text));

    private static ImportedLayout FakeLayout()
        => new(new UiPanel { Width = 300f, Height = 362f },
            new Dictionary<uint, UiElement>());

    private sealed class TestSubscription : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
