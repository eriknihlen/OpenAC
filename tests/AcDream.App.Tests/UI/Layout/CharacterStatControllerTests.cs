using AcDream.App.UI;
using AcDream.App.UI.Layout;
using System.Globalization;
using System.Numerics;

namespace AcDream.App.Tests.UI.Layout;

public class CharacterStatControllerTests
{
    // ── Header labels bind to the sheet ──────────────────────────────────────

    [Fact]
    public void Bind_SetsNameLabel()
    {
        var name = new UiText();
        var layout = Fake((CharacterStatController.NameId, name));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        Assert.Equal("Studio Player", name.LinesProvider()[0].Text);
    }

    [Fact]
    public void Bind_SetsLevelLabel()
    {
        var level = new UiText();
        var layout = Fake((CharacterStatController.LevelId, level));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        Assert.Equal("126", level.LinesProvider()[0].Text);
    }

    [Fact]
    public void Bind_SetsHeritageAndPkLabels()
    {
        var heritage = new UiText();
        var pk       = new UiText();
        var layout   = Fake((CharacterStatController.HeritageId, heritage),
                            (CharacterStatController.PkStatusId, pk));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        Assert.Equal("Female Aluvian the Adventurer",  heritage.LinesProvider()[0].Text);
        Assert.Equal("Non-Player Killer", pk.LinesProvider()[0].Text);
    }

    [Fact]
    public void Bind_WindowChromeButton_InvokesCloseCallback()
    {
        var close = MakeButton(WindowChromeController.CharacterCloseButtonId);
        var layout = Fake((WindowChromeController.CharacterCloseButtonId, close));
        int closes = 0;

        CharacterStatController.Bind(layout, SampleData.SampleCharacter, onClose: () => closes++);
        close.OnEvent(new UiEvent(0u, close, UiEventType.Click));

        Assert.Equal(1, closes);
    }

    [Fact]
    public void CharacterIdentityText_StatHeaderLine_ComposesRetailGenderHeritageTitle()
    {
        var sheet = new CharacterSheet
        {
            Gender = "Female",
            Heritage = "Aluvian",
            Title = "the Adventurer",
        };

        Assert.Equal("Female Aluvian the Adventurer", CharacterIdentityText.StatHeaderLine(sheet));
    }

    [Fact]
    public void CharacterIdentityText_StatHeaderLine_KeepsCapitalTheTitleUnmangled()
    {
        var sheet = new CharacterSheet
        {
            Gender = "Male",
            Heritage = "Aluvian",
            Title = "The Noob",
        };

        Assert.Equal("Male Aluvian The Noob", CharacterIdentityText.StatHeaderLine(sheet));
    }

    [Theory]
    [InlineData(1, "Male")]
    [InlineData(2, "Female")]
    [InlineData(0, null)]
    public void CharacterIdentityText_GenderDisplayName_UsesRetailEnum(int value, string? expected)
        => Assert.Equal(expected, CharacterIdentityText.GenderDisplayName(value));

    [Theory]
    [InlineData(1, "Aluvian")]
    [InlineData(2, "Gharu'ndim")]
    [InlineData(5, "Umbraen")]
    [InlineData(13, "Olthoi")]
    [InlineData(99, null)]
    public void CharacterIdentityText_HeritageGroupDisplayName_UsesRetailEnum(int value, string? expected)
        => Assert.Equal(expected, CharacterIdentityText.HeritageGroupDisplayName(value));

    [Fact]
    public void Bind_HeaderElements_UseVisibleAttributesPageWhenIdsAreDuplicated()
    {
        var root = new UiPanel { Width = 300, Height = 600 };
        var attrPage = MakeDatElement(CharacterStatController.AttributesPageId, top: 25, width: 300, height: 575);
        var hiddenPage = MakeDatElement(CharacterStatController.SkillsPageId, top: 25, width: 300, height: 575);

        var visibleName = new UiText { ElementId = CharacterStatController.NameId };
        var hiddenName = new UiText { ElementId = CharacterStatController.NameId };
        var visibleHeritage = new UiText { ElementId = CharacterStatController.HeritageId };
        var hiddenHeritage = new UiText { ElementId = CharacterStatController.HeritageId };
        var visiblePk = new UiText { ElementId = CharacterStatController.PkStatusId };
        var hiddenPk = new UiText { ElementId = CharacterStatController.PkStatusId };
        var visibleLevel = new UiText { ElementId = CharacterStatController.LevelId };
        var hiddenLevel = new UiText { ElementId = CharacterStatController.LevelId };
        var visibleTotalXp = new UiText { ElementId = CharacterStatController.TotalXpId };
        var hiddenTotalXp = new UiText { ElementId = CharacterStatController.TotalXpId };
        var visibleTotalXpLabel = new UiText { ElementId = CharacterStatController.TotalXpLabelId };
        var hiddenTotalXpLabel = new UiText { ElementId = CharacterStatController.TotalXpLabelId };
        var visibleMeter = new UiMeter { ElementId = CharacterStatController.XpMeterId };
        var hiddenMeter = new UiMeter { ElementId = CharacterStatController.XpMeterId };
        var visibleXpNext = new UiText { ElementId = CharacterStatController.XpNextValueId };
        var hiddenXpNext = new UiText { ElementId = CharacterStatController.XpNextValueId };
        visibleMeter.AddChild(visibleXpNext);
        hiddenMeter.AddChild(hiddenXpNext);

        attrPage.AddChild(visibleName);
        attrPage.AddChild(visibleHeritage);
        attrPage.AddChild(visiblePk);
        attrPage.AddChild(visibleLevel);
        attrPage.AddChild(visibleTotalXpLabel);
        attrPage.AddChild(visibleTotalXp);
        attrPage.AddChild(visibleMeter);
        hiddenPage.AddChild(hiddenName);
        hiddenPage.AddChild(hiddenHeritage);
        hiddenPage.AddChild(hiddenPk);
        hiddenPage.AddChild(hiddenLevel);
        hiddenPage.AddChild(hiddenTotalXpLabel);
        hiddenPage.AddChild(hiddenTotalXp);
        hiddenPage.AddChild(hiddenMeter);
        root.AddChild(attrPage);
        root.AddChild(hiddenPage);

        var layout = new ImportedLayout(root, new Dictionary<uint, UiElement>
        {
            [CharacterStatController.NameId] = hiddenName,
            [CharacterStatController.HeritageId] = hiddenHeritage,
            [CharacterStatController.PkStatusId] = hiddenPk,
            [CharacterStatController.LevelId] = hiddenLevel,
            [CharacterStatController.TotalXpLabelId] = hiddenTotalXpLabel,
            [CharacterStatController.TotalXpId] = hiddenTotalXp,
            [CharacterStatController.XpMeterId] = hiddenMeter,
            [CharacterStatController.XpNextValueId] = hiddenXpNext,
        });

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        Assert.Equal("Studio Player", visibleName.LinesProvider()[0].Text);
        Assert.Equal("Female Aluvian the Adventurer", visibleHeritage.LinesProvider()[0].Text);
        Assert.Equal("Non-Player Killer", visiblePk.LinesProvider()[0].Text);
        Assert.Equal("126", visibleLevel.LinesProvider()[0].Text);
        Assert.Equal("Total Experience (XP):", visibleTotalXpLabel.LinesProvider()[0].Text);
        Assert.Equal((1_250_000_000L).ToString("N0", CultureInfo.InvariantCulture), visibleTotalXp.LinesProvider()[0].Text);
        Assert.Equal((42_000_000L).ToString("N0", CultureInfo.InvariantCulture), visibleXpNext.LinesProvider()[0].Text);
        Assert.Empty(hiddenName.LinesProvider());
        Assert.Empty(hiddenXpNext.LinesProvider());
        Assert.Empty(hiddenPk.LinesProvider());
    }


    [Fact]
    public void Bind_HeritageLine_ComposesGenderHeritageTitle_AndUpdatesLiveOnDisplayTitleChange()
    {
        var heritage = new UiText();
        var layout = Fake((CharacterStatController.HeritageId, heritage));
        CharacterSheet sheet = new() { Gender = "Female", Heritage = "Aluvian" };

        CharacterStatController.Bind(layout, () => sheet);

        Assert.Equal("Female Aluvian", heritage.LinesProvider()[0].Text);

        sheet = new CharacterSheet { Gender = "Female", Heritage = "Aluvian", Title = "War Mage" };
        Assert.Equal("Female Aluvian War Mage", heritage.LinesProvider()[0].Text);
    }

    [Fact]
    public void Bind_NameLine_ShowsPlainNameOnly_NoRankPrefix()
    {
        var name = new UiText();
        var layout = Fake((CharacterStatController.NameId, name));

        CharacterStatController.Bind(layout, () => new CharacterSheet { Name = "Dww" });

        Assert.Equal("Dww", name.LinesProvider()[0].Text);
    }

    [Theory]
    [InlineData(126, "126")]
    [InlineData(0, "0")]
    [InlineData(null, "???")]
    public void Bind_LevelLine_FormatsIntegerOrShowsQuestionMarks_InAuthoredColor(int? level, string expectedText)
    {
        var authoredColor = new Vector4(0.11f, 0.22f, 0.33f, 1f);
        var levelText = new UiText { DefaultColor = authoredColor };
        var layout = Fake((CharacterStatController.LevelId, levelText));

        CharacterStatController.Bind(layout, () => new CharacterSheet { Level = level });

        UiText.Line line = Assert.Single(levelText.LinesProvider());
        Assert.Equal(expectedText, line.Text);
        Assert.Equal(authoredColor, line.Color);
    }

    [Theory]
    [InlineData("Player Killer")]
    [InlineData("Player Killer Lite")]
    [InlineData("Non-Player Killer")]
    public void Bind_PkStatusLine_ShowsResolvedText_InAuthoredColor(string resolvedText)
    {
        var authoredColor = new Vector4(0.4f, 0.5f, 0.6f, 1f);
        var pk = new UiText { DefaultColor = authoredColor };
        var layout = Fake((CharacterStatController.PkStatusId, pk));

        CharacterStatController.Bind(layout, () => new CharacterSheet { PkStatus = resolvedText });

        UiText.Line line = Assert.Single(pk.LinesProvider());
        Assert.Equal(resolvedText, line.Text);
        Assert.Equal(authoredColor, line.Color);
    }

    [Fact]
    public void Bind_PkStatusLine_NullPkStatus_ShowsEmptyText()
    {
        var pk = new UiText();
        var layout = Fake((CharacterStatController.PkStatusId, pk));

        CharacterStatController.Bind(layout, () => new CharacterSheet { PkStatus = null });

        Assert.Equal(string.Empty, pk.LinesProvider()[0].Text);
    }

    [Theory]
    [InlineData(126, 0L, false)]      // below level 200 — hidden regardless of luminance
    [InlineData(200, 0L, false)]      // level gate met, but MaximumLuminance == 0 — hidden
    [InlineData(200, 1_000_000L, true)]
    [InlineData(275, 500L, true)]
    [InlineData(null, 500L, false)]   // absent level — treated as "not level 200+"
    public void Bind_LuminancePair_TogglesContentPerRetailGate(int? level, long maxLuminance, bool expectedVisible)
    {
        var label = new UiText { ElementId = CharacterStatController.LuminanceLabelId };
        var value = new UiText { ElementId = CharacterStatController.LuminanceValueId };
        var layout = Fake(
            (CharacterStatController.LuminanceLabelId, label),
            (CharacterStatController.LuminanceValueId, value));

        CharacterStatController.Bind(layout, () => new CharacterSheet
        {
            Level = level,
            MaximumLuminance = maxLuminance,
        });

        Assert.Equal(expectedVisible, label.LinesProvider().Count > 0);
        Assert.Equal(expectedVisible, value.LinesProvider().Count > 0);
        Assert.True(label.Visible);
        Assert.True(value.Visible);
    }

    [Fact]
    public void Bind_LuminancePair_ShowsBoundTextWhenGateIsOpen()
    {
        var authoredColor = new Vector4(0.9f, 0.9f, 0.9f, 1f);
        var label = new UiText { ElementId = CharacterStatController.LuminanceLabelId, DefaultColor = authoredColor };
        var value = new UiText { ElementId = CharacterStatController.LuminanceValueId, DefaultColor = authoredColor };
        var layout = Fake(
            (CharacterStatController.LuminanceLabelId, label),
            (CharacterStatController.LuminanceValueId, value));

        CharacterStatController.Bind(layout, () => new CharacterSheet
        {
            Level = 200,
            AvailableLuminance = 1_500_000L,
            MaximumLuminance = 25_000_000L,
        });

        UiText.Line labelLine = Assert.Single(label.LinesProvider());
        Assert.Equal("Luminance:", labelLine.Text);
        Assert.Equal(authoredColor, labelLine.Color);

        UiText.Line valueLine = Assert.Single(value.LinesProvider());
        Assert.Equal("1,500,000 / 25,000,000", valueLine.Text);
        Assert.Equal(authoredColor, valueLine.Color);
    }

    // ── XP meter fill ────────────────────────────────────────────────────────

    [Fact]
    public void Bind_SetsXpMeterFill()
    {
        var meter = new UiMeter();
        var layout = Fake((CharacterStatController.XpMeterId, meter));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var fill = meter.Fill();
        Assert.NotNull(fill);
        Assert.True(System.MathF.Abs(fill!.Value - 0.63f) < 0.001f, $"expected ~0.63, got {fill}");
    }


    [Fact]
    public void Bind_AttributeList_Has9Rows()
    {
        var list   = new UiPanel();
        var layout = Fake((CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var rows = Descendants(list).OfType<UiClickablePanel>().ToList();
        Assert.Equal(9, rows.Count);
    }

    [Fact]
    public void Bind_AttributeList_RowsAreClickablePanels()
    {
        var list   = new UiPanel();
        var layout = Fake((CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var rows = Descendants(list).OfType<UiClickablePanel>().ToList();
        Assert.Equal(9, rows.Count);
        foreach (var row in rows)
        {
            Assert.NotNull(row.OnClick);
            Assert.False(row.ClickThrough, "clickable row must accept pointer hits");
        }
    }

    [Fact]
    public void Bind_AttributeList_EachRowHasRightAlignedValueLabel()
    {
        var list   = new UiPanel();
        var layout = Fake((CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var rows = Descendants(list).OfType<UiClickablePanel>().ToList();
        Assert.Equal(9, rows.Count);
        foreach (var row in rows)
        {
            var texts = row.Children.OfType<UiText>().ToList();
            Assert.True(texts.Count >= 2, "each row must have at least name + value UiText");
            var valueEl = texts[^1];
            Assert.True(valueEl.RightAligned, "value label must be RightAligned");
        }
    }

    [Fact]
    public void Bind_AttributeList_RowNamesInRetailOrder()
    {
        var list   = new UiPanel();
        var layout = Fake((CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var rows = Descendants(list).OfType<UiClickablePanel>().ToList();
        Assert.Equal(9, rows.Count);

        string[] expectedNames =
        {
            "Strength", "Endurance", "Coordination", "Quickness", "Focus", "Self",
            "Health", "Stamina", "Mana",
        };

        for (int i = 0; i < 9; i++)
        {
            var texts = rows[i].Children.OfType<UiText>().ToList();
            Assert.True(texts.Count >= 2, $"row {i} must have name + value");
            string rowName = texts[1].LinesProvider()[0].Text;
            Assert.Equal(expectedNames[i], rowName);
        }
    }

    [Fact]
    public void Bind_AttributeList_RowValues_AttributeIntegersAndVitalsCurMax()
    {
        var list   = new UiPanel();
        var layout = Fake((CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var rows = Descendants(list).OfType<UiClickablePanel>().ToList();

        string ValueOf(UiPanel row) => row.Children.OfType<UiText>().ToList()[^1].LinesProvider()[0].Text;

        Assert.Equal("200", ValueOf(rows[0]));   // Strength
        Assert.Equal("10",  ValueOf(rows[1]));   // Endurance
        Assert.Equal("10",  ValueOf(rows[2]));
        Assert.Equal("200", ValueOf(rows[3]));   // Quickness
        Assert.Equal("10",  ValueOf(rows[4]));   // Focus
        Assert.Equal("10",  ValueOf(rows[5]));   // Self
        Assert.Equal("5/5",   ValueOf(rows[6]));   // Health
        Assert.Equal("10/10", ValueOf(rows[7]));   // Stamina
        Assert.Equal("10/10", ValueOf(rows[8]));   // Mana
    }

    [Fact]
    public void Bind_AttributeList_IconHasBackgroundSpriteWhenResolverProvided()
    {
        var list   = new UiPanel();
        var layout = Fake((CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16));

        var rows = Descendants(list).OfType<UiClickablePanel>().ToList();
        Assert.Equal(9, rows.Count);

        var iconEl = rows[0].Children.OfType<UiText>().First();
        Assert.Equal(0x060002C8u, iconEl.BackgroundSprite);

        var healthIcon = rows[6].Children.OfType<UiText>().First();
        Assert.Equal(0x06004C3Bu, healthIcon.BackgroundSprite);
    }

    [Fact]
    public void Bind_AttributeList_IconBackgroundSprite_ZeroWhenNoResolver()
    {
        var list   = new UiPanel();
        var layout = Fake((CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter, spriteResolve: null);

        var rows = Descendants(list).OfType<UiClickablePanel>().ToList();
        Assert.Equal(9, rows.Count);
        foreach (var row in rows)
        {
            var iconEl = row.Children.OfType<UiText>().First();
            Assert.Equal(0u, iconEl.BackgroundSprite);
        }
    }


    [Fact]
    public void Bind_AttributeRow_MatchesAuthoredTemplateGeometry()
    {
        var list = new UiPanel();
        var layout = Fake((CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var row = Descendants(list).OfType<UiClickablePanel>().First();
        AssertRowGeometry(row, expectedWidth: 282f);
    }

    /// <summary>Vital rows (Health/Stamina/Mana) are appended after the 6
    /// attribute rows by <c>BuildAttributeRows</c> — index 6 is the first
    /// vital row.</summary>
    [Fact]
    public void Bind_VitalRow_MatchesAuthoredTemplateGeometry()
    {
        var list = new UiPanel();
        var layout = Fake((CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var row = Descendants(list).OfType<UiClickablePanel>().ToList()[6];
        AssertRowGeometry(row, expectedWidth: 282f);
    }

    [Fact]
    public void Bind_SkillRow_ClampsToAuthoredScrollbarGutterWidth()
    {
        var root = new UiPanel { Width = 300, Height = 600 };
        var page = new UiPanel { Width = 300, Height = 600 };
        var name = new UiText();
        var list = MakeDatElement(CharacterStatController.ListBoxId, top: 112, width: 300, height: 398);
        var scrollbarShell = MakeDatElement(CharacterStatController.ListScrollbarId, top: 112, width: 16, height: 398);
        scrollbarShell.Left = 281;

        page.AddChild(name);
        page.AddChild(list);
        page.AddChild(scrollbarShell);
        root.AddChild(page);
        var skillsTab = MakeTab(CharacterStatController.TabSkillsId, left: 92f);
        root.AddChild(skillsTab);

        var layout = new ImportedLayout(root, new Dictionary<uint, UiElement>
        {
            [CharacterStatController.NameId] = name,
            [CharacterStatController.ListBoxId] = list,
            [CharacterStatController.ListScrollbarId] = scrollbarShell,
            [CharacterStatController.TabSkillsId] = skillsTab,
        });

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16));

        ClickTab(layout, left: 92f);

        var row = SkillRows(list).First();
        AssertRowGeometry(row, expectedWidth: 281f);
    }

    private static void AssertRowGeometry(UiPanel row, float expectedWidth)
    {
        Assert.Equal(expectedWidth, row.Width);
        Assert.Equal(20f, row.Height);

        var texts = row.Children.OfType<UiText>().ToList();
        Assert.True(texts.Count >= 3, "row must have icon + name + value children");
        UiText icon = texts[0];
        UiText name = texts[1];
        UiText value = texts[2];

        Assert.Equal(0f, icon.Left);
        Assert.Equal(20f, icon.Width);

        Assert.Equal(25f, name.Left);
        Assert.Equal(150f, name.Width);
        Assert.Equal(0f, name.Padding);

        Assert.Equal(175f, value.Left);
        Assert.Equal(100f, value.Width);
        Assert.True(value.RightAligned);
    }

    // ── Footer State A ────────────────────────────────────────────────────────

    [Fact]
    public void Bind_FooterStateA_TitleIsSelectPrompt()
    {
        var title = new UiText();
        var layout = Fake((CharacterStatController.FooterTitleId, title));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        // Initial state (nothing selected): State-A title.
        Assert.Equal("Select an Attribute to Improve", title.LinesProvider()[0].Text);
    }

    [Fact]
    public void Bind_FooterStateA_Line1LabelIsSkillCreditsAvailable()
    {
        var lbl = new UiText();
        var layout = Fake((CharacterStatController.FooterLine1Label, lbl));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        Assert.Equal("Skill Credits Available:", lbl.LinesProvider()[0].Text);
    }

    [Fact]
    public void Bind_FooterStateA_Line1ValueIsSkillCredits()
    {
        var val = new UiText();
        var layout = Fake((CharacterStatController.FooterLine1Value, val));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        Assert.Equal("96", val.LinesProvider()[0].Text);
    }

    [Fact]
    public void Bind_FooterStateA_Line2LabelIsUnassignedExperience()
    {
        var lbl = new UiText();
        var layout = Fake((CharacterStatController.FooterLine2Label, lbl));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        Assert.Equal("Unassigned Experience:", lbl.LinesProvider()[0].Text);
    }

    [Fact]
    public void Bind_FooterStateA_Line2ValueIsUnassignedXp()
    {
        var val = new UiText();
        var layout = Fake((CharacterStatController.FooterLine2Value, val));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var expected = (87_757_321_741L).ToString("N0", CultureInfo.InvariantCulture);
        Assert.Equal(expected, val.LinesProvider()[0].Text);
    }

    // ── Pass 2: Row selection → Footer State B ───────────────────────────────

    [Fact]
    public void RowClick_SelectRow4Focus_FooterStateBShowsFocusTitle()
    {
        var title = new UiText();
        var list  = new UiPanel();
        var layout = Fake(
            (CharacterStatController.FooterTitleId, title),
            (CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var rows = Descendants(list).OfType<UiClickablePanel>().ToList();
        Assert.Equal(9, rows.Count);
        rows[4].OnClick!();

        // Footer title should now be "Focus: 10".
        Assert.Equal("Focus: 10", title.LinesProvider()[0].Text);
    }

    [Fact]
    public void RowClick_SelectRow4Focus_FooterLine1LabelIsExperienceToRaise()
    {
        var lbl  = new UiText();
        var list = new UiPanel();
        var layout = Fake(
            (CharacterStatController.FooterLine1Label, lbl),
            (CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);
        Descendants(list).OfType<UiClickablePanel>().ToList()[4].OnClick!();

        Assert.Equal("Experience To Raise:", lbl.LinesProvider()[0].Text);
    }

    [Fact]
    public void RowClick_SelectRow4Focus_FooterLine1ValueIsRaiseCost()
    {
        var val  = new UiText();
        var list = new UiPanel();
        var layout = Fake(
            (CharacterStatController.FooterLine1Value, val),
            (CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);
        Descendants(list).OfType<UiClickablePanel>().ToList()[4].OnClick!();

        Assert.Equal((110L).ToString("N0", CultureInfo.InvariantCulture), val.LinesProvider()[0].Text);
    }

    [Fact]
    public void RowClick_SelectRow4Focus_FooterLine2LabelIsUnassignedExperience()
    {
        var lbl  = new UiText();
        var list = new UiPanel();
        var layout = Fake(
            (CharacterStatController.FooterLine2Label, lbl),
            (CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);
        Descendants(list).OfType<UiClickablePanel>().ToList()[4].OnClick!();

        Assert.Equal("Unassigned Experience:", lbl.LinesProvider()[0].Text);
    }

    [Fact]
    public void RowClick_SelectRow4Focus_FooterLine2ValueIsUnassignedXp()
    {
        var val  = new UiText();
        var list = new UiPanel();
        var layout = Fake(
            (CharacterStatController.FooterLine2Value, val),
            (CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);
        Descendants(list).OfType<UiClickablePanel>().ToList()[4].OnClick!();

        // UnassignedXp = 87_757_321_741L
        var expected = (87_757_321_741L).ToString("N0", CultureInfo.InvariantCulture);
        Assert.Equal(expected, val.LinesProvider()[0].Text);
    }

    // ── Pass 2: Toggle deselects ──────────────────────────────────────────────

    [Fact]
    public void RowClick_ToggleSameRow_ReturnsToFooterStateA()
    {
        var title = new UiText();
        var list  = new UiPanel();
        var layout = Fake(
            (CharacterStatController.FooterTitleId, title),
            (CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var rows = Descendants(list).OfType<UiClickablePanel>().ToList();

        // Select Focus (row 4).
        rows[4].OnClick!();
        Assert.Equal("Focus: 10", title.LinesProvider()[0].Text);

        rows[4].OnClick!();
        Assert.Equal("Select an Attribute to Improve", title.LinesProvider()[0].Text);
    }

    [Fact]
    public void RowClick_SwitchRow_UpdatesToNewRow()
    {
        var title = new UiText();
        var list  = new UiPanel();
        var layout = Fake(
            (CharacterStatController.FooterTitleId, title),
            (CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var rows = Descendants(list).OfType<UiClickablePanel>().ToList();

        // Select Endurance (row 1, value=10).
        rows[1].OnClick!();
        Assert.Equal("Endurance: 10", title.LinesProvider()[0].Text);

        // Select Self (row 5, value=10).
        rows[5].OnClick!();
        Assert.Equal("Self: 10", title.LinesProvider()[0].Text);
    }

    // ── Pass 2: Row highlight ─────────────────────────────────────────────────

    [Fact]
    public void RowClick_SelectRow_HighlightsSelectedAndClearsOthers()
    {
        var list  = new UiPanel();
        var layout = Fake((CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var rows = Descendants(list).OfType<UiClickablePanel>().ToList();

        // All rows start transparent.
        Assert.All(rows, r => Assert.Equal(0f, r.BackgroundColor.W));

        rows[2].OnClick!();
        Assert.NotEqual(0f, rows[2].BackgroundColor.W);    // highlighted
        Assert.Equal(0f,    rows[0].BackgroundColor.W);
        Assert.Equal(0f,    rows[1].BackgroundColor.W);
    }

    [Fact]
    public void RowClick_Deselect_ClearsHighlight()
    {
        var list  = new UiPanel();
        var layout = Fake((CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var rows = Descendants(list).OfType<UiClickablePanel>().ToList();
        rows[2].OnClick!();   // select
        rows[2].OnClick!();   // deselect

        Assert.Equal(0f, rows[2].BackgroundColor.W);
    }

    [Fact]
    public void RowClick_WithSpriteResolve_SelectedRowHasHighlightSprite()
    {
        var list   = new UiPanel();
        var layout = Fake((CharacterStatController.ListBoxId, list));

        // Minimal sprite resolver — returns a fake non-zero handle so UiPanel draws it.
        static (uint, int, int) FakeResolve(uint id) => (1u, 32, 8);

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            spriteResolve: FakeResolve);

        var rows = Descendants(list).OfType<UiClickablePanel>().ToList();
        rows[2].OnClick!();

        Assert.Equal(0x06000F93u, rows[2].BackgroundSprite);    // selected → sprite
        Assert.Equal(0f,          rows[2].BackgroundColor.W);   // no tint
        Assert.Equal(0x06004CC2u, rows[0].BackgroundSprite);    // others: Normal-state media
        Assert.Equal(0x06004CC2u, rows[1].BackgroundSprite);
    }

    [Fact]
    public void RowClick_WithSpriteResolve_Deselect_ClearsSprite()
    {
        var list   = new UiPanel();
        var layout = Fake((CharacterStatController.ListBoxId, list));
        static (uint, int, int) FakeResolve(uint id) => (1u, 32, 8);
        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            spriteResolve: FakeResolve);

        var rows = Descendants(list).OfType<UiClickablePanel>().ToList();
        rows[2].OnClick!();   // select
        rows[2].OnClick!();   // deselect

        Assert.Equal(0x06004CC2u, rows[2].BackgroundSprite);
    }

    // ── Pass 2: Raise button affordability ───────────────────────────────────

    [Fact]
    public void RaiseButtons_InitiallyHidden()
    {
        var btn1   = MakeButton();
        var btn10  = MakeButton();
        var list   = new UiPanel();
        var layout = Fake(
            (CharacterStatController.RaiseOneId, btn1),
            (CharacterStatController.RaiseTenId, btn10),
            (CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        Assert.False(btn1.Visible,  "raise×1 must start hidden");
        Assert.False(btn10.Visible, "raise×10 must start hidden");
    }

    [Fact]
    public void RaiseButtons_AffordableRow_ShowsNormalState()
    {
        var btn1   = MakeButton();
        var btn10  = MakeButton();
        var list   = new UiPanel();
        var layout = Fake(
            (CharacterStatController.RaiseOneId, btn1),
            (CharacterStatController.RaiseTenId, btn10),
            (CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        Descendants(list).OfType<UiClickablePanel>().ToList()[4].OnClick!();  // select Focus

        Assert.True(btn1.Visible,  "raise×1 visible on selection");
        Assert.True(btn10.Visible, "raise×10 visible on selection");
        Assert.Equal("Normal", btn1.ActiveState);
        Assert.Equal("Normal", btn10.ActiveState);
    }

    [Fact]
    public void RaiseButtons_OnlyOneAffordable_SplitsOneAndTenStates()
    {
        var btn1 = MakeButton();
        var btn10 = MakeButton();
        var list = new UiPanel();
        var layout = Fake(
            (CharacterStatController.RaiseOneId, btn1),
            (CharacterStatController.RaiseTenId, btn10),
            (CharacterStatController.ListBoxId, list));
        var sheet = new CharacterSheet
        {
            UnassignedXp = 500L,
            AttributeRaiseCosts = new long[] { 0L, 0L, 0L, 0L, 110L, 0L, 0L, 0L, 0L },
            AttributeRaise10Costs = new long[] { 0L, 0L, 0L, 0L, 1_100L, 0L, 0L, 0L, 0L },
        };

        CharacterStatController.Bind(layout, () => sheet);

        Descendants(list).OfType<UiClickablePanel>().ToList()[4].OnClick!();

        Assert.Equal("Normal", btn1.ActiveState);
        Assert.Equal("Ghosted", btn10.ActiveState);
    }

    [Fact]
    public void RaiseButtons_MaxedRow_ShowsGhostedState()
    {
        var btn1   = MakeButton();
        var btn10  = MakeButton();
        var list   = new UiPanel();
        var layout = Fake(
            (CharacterStatController.RaiseOneId, btn1),
            (CharacterStatController.RaiseTenId, btn10),
            (CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        Descendants(list).OfType<UiClickablePanel>().ToList()[0].OnClick!();

        Assert.True(btn1.Visible,   "raise button visible even when disabled");
        Assert.Equal("Ghosted", btn1.ActiveState);
        Assert.Equal("Ghosted", btn10.ActiveState);
    }

    [Fact]
    public void RaiseButtons_ClickAffordableAttribute_EmitsRaiseRequest()
    {
        var btn1 = MakeButton();
        var list = new UiPanel();
        var layout = Fake(
            (CharacterStatController.RaiseOneId, btn1),
            (CharacterStatController.ListBoxId, list));
        var requests = new List<CharacterStatController.RaiseRequest>();

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            onRaiseRequest: (request, completed) => { requests.Add(request); completed(); });

        Descendants(list).OfType<UiClickablePanel>().ToList()[4].OnClick!();
        btn1.OnClick!();

        var request = Assert.Single(requests);
        Assert.Equal(CharacterStatController.RaiseTargetKind.Attribute, request.Kind);
        Assert.Equal(5u, request.StatId);
        Assert.Equal(110L, request.Cost);
        Assert.Equal(1, request.Amount);
    }

    [Fact]
    public void RaiseButtons_ClickAffordableAttribute_KeepsNormalStateUntilCostsRefresh()
    {
        var btn1 = MakeButton();
        var list = new UiPanel();
        var layout = Fake(
            (CharacterStatController.RaiseOneId, btn1),
            (CharacterStatController.ListBoxId, list));
        var requests = new List<CharacterStatController.RaiseRequest>();

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            onRaiseRequest: (request, completed) => { requests.Add(request); completed(); });

        Descendants(list).OfType<UiClickablePanel>().ToList()[4].OnClick!();
        Assert.Equal("Normal", btn1.ActiveState);

        btn1.OnClick!();

        Assert.Single(requests);
        Assert.Equal("Normal", btn1.ActiveState);
    }

    [Fact]
    public void RaiseButtons_ClickAffordableVital_EmitsMaxVitalId()
    {
        var btn1 = MakeButton();
        var list = new UiPanel();
        var layout = Fake(
            (CharacterStatController.RaiseOneId, btn1),
            (CharacterStatController.ListBoxId, list));
        var requests = new List<CharacterStatController.RaiseRequest>();

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            onRaiseRequest: (request, completed) => { requests.Add(request); completed(); });

        Descendants(list).OfType<UiClickablePanel>().ToList()[6].OnClick!();
        btn1.OnClick!();

        var request = Assert.Single(requests);
        Assert.Equal(CharacterStatController.RaiseTargetKind.Vital, request.Kind);
        Assert.Equal(1u, request.StatId);
        Assert.Equal(90L, request.Cost);
        Assert.Equal(1, request.Amount);
    }

    [Fact]
    public void RaiseButtons_ClickUnaffordableTen_DoesNotEmitRequest()
    {
        var btn10 = MakeButton();
        var list = new UiPanel();
        var layout = Fake(
            (CharacterStatController.RaiseTenId, btn10),
            (CharacterStatController.ListBoxId, list));
        var requests = new List<CharacterStatController.RaiseRequest>();
        var sheet = new CharacterSheet
        {
            UnassignedXp = 500L,
            AttributeRaiseCosts = new long[] { 0L, 0L, 0L, 0L, 110L, 0L, 0L, 0L, 0L },
            AttributeRaise10Costs = new long[] { 0L, 0L, 0L, 0L, 1_100L, 0L, 0L, 0L, 0L },
        };

        CharacterStatController.Bind(layout, () => sheet,
            onRaiseRequest: (request, completed) => { requests.Add(request); completed(); });

        Descendants(list).OfType<UiClickablePanel>().ToList()[4].OnClick!();
        btn10.OnClick!();

        Assert.Empty(requests);
    }

    [Fact]
    public void RaiseButtons_Deselect_HidesButtons()
    {
        var btn1   = MakeButton();
        var list   = new UiPanel();
        var layout = Fake(
            (CharacterStatController.RaiseOneId, btn1),
            (CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var rows = Descendants(list).OfType<UiClickablePanel>().ToList();
        rows[4].OnClick!();  // select
        Assert.True(btn1.Visible);
        rows[4].OnClick!();  // deselect
        Assert.False(btn1.Visible, "raise button hidden after deselect");
    }

    // ── Pass 2: DAT-authored tab states ───────────────────────────────────────

    [Fact]
    public void CharacterTabs_UseImportedChromeWithoutSyntheticRootChildren()
    {
        var layout = FixtureLoader.LoadCharacter();
        int rootChildCount = layout.Root.Children.Count;
        var attributes = Assert.IsType<UiText>(layout.FindElement(CharacterStatController.TabAttribId));
        var skills = Assert.IsType<UiText>(layout.FindElement(CharacterStatController.TabSkillsId));
        var titles = Assert.IsType<UiText>(layout.FindElement(CharacterStatController.TabTitlesId));

        Assert.Equal(3, attributes.Children.Count);
        Assert.Equal(3, skills.Children.Count);
        Assert.Equal(3, titles.Children.Count);

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16));

        Assert.Equal(rootChildCount, layout.Root.Children.Count);
        Assert.Equal(RetailUiStateIds.Open, attributes.ActiveRetailStateId);
        Assert.Equal(RetailUiStateIds.Closed, skills.ActiveRetailStateId);
        Assert.Equal(RetailUiStateIds.Closed, titles.ActiveRetailStateId);
        Assert.Equal(
            new uint[] { 0x06005D92u, 0x06005D94u, 0x06005D96u },
            attributes.Children.Cast<UiDatElement>().Select(child => child.ActiveMedia().File));
        Assert.Equal(
            new uint[] { 0x06005D93u, 0x06005D95u, 0x06005D97u },
            skills.Children.Cast<UiDatElement>().Select(child => child.ActiveMedia().File));
        Assert.False(titles.ClickThrough);
        Assert.NotNull(titles.OnClick);
    }

    [Fact]
    public void CharacterTabs_ClickUsesRetailStateColorAndPropagatesToChrome()
    {
        var layout = FixtureLoader.LoadCharacter();
        var attributes = Assert.IsType<UiText>(layout.FindElement(CharacterStatController.TabAttribId));
        var skills = Assert.IsType<UiText>(layout.FindElement(CharacterStatController.TabSkillsId));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16));
        skills.OnClick!();

        Assert.Equal(RetailUiStateIds.Closed, attributes.ActiveRetailStateId);
        Assert.Equal(RetailUiStateIds.Open, skills.ActiveRetailStateId);
        Assert.Equal(127f / 255f, attributes.DefaultColor.X, 5);
        Assert.Equal(204f / 255f, skills.DefaultColor.X, 5);
        Assert.All(attributes.Children, child =>
            Assert.Equal(RetailUiStateIds.Closed, Assert.IsAssignableFrom<IUiDatStateful>(child).ActiveRetailStateId));
        Assert.All(skills.Children, child =>
            Assert.Equal(RetailUiStateIds.Open, Assert.IsAssignableFrom<IUiDatStateful>(child).ActiveRetailStateId));
    }

    [Fact]
    public void ProgrammaticShowTab_UsesTheSameAuthoredStateAsAKeyboardAction()
    {
        ImportedLayout layout = FixtureLoader.LoadCharacter();
        var attributes = Assert.IsType<UiText>(
            layout.FindElement(CharacterStatController.TabAttribId));
        var skills = Assert.IsType<UiText>(
            layout.FindElement(CharacterStatController.TabSkillsId));
        CharacterStatController.Binding binding = CharacterStatController.Bind(
            layout,
            SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16));

        binding.ShowTab(CharacterStatController.CharacterStatTab.Skills);

        Assert.Equal(
            CharacterStatController.CharacterStatTab.Skills,
            binding.CurrentTab());
        Assert.Equal(RetailUiStateIds.Closed, attributes.ActiveRetailStateId);
        Assert.Equal(RetailUiStateIds.Open, skills.ActiveRetailStateId);
    }

    [Fact]
    public void TitlesTab_Click_ShowsTitlesPageAndHidesAttributesSkillsContent()
    {
        var layout = FixtureLoader.LoadCharacter();
        var titlesTab = Assert.IsType<UiText>(layout.FindElement(CharacterStatController.TabTitlesId));
        var attributesTab = Assert.IsType<UiText>(layout.FindElement(CharacterStatController.TabAttribId));
        var titlesPage = layout.FindElement(CharacterStatController.TitlesPageId);
        var attributesPage = layout.FindElement(CharacterStatController.AttributesPageId);
        Assert.NotNull(titlesPage);
        Assert.NotNull(attributesPage);

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16));

        Assert.True(attributesPage!.Visible);
        Assert.False(titlesPage!.Visible);

        titlesTab.OnClick!();

        Assert.True(titlesPage.Visible);
        Assert.False(attributesPage.Visible);
        Assert.Equal(RetailUiStateIds.Open, titlesTab.ActiveRetailStateId);
        Assert.Equal(RetailUiStateIds.Closed, attributesTab.ActiveRetailStateId);

        attributesTab.OnClick!();

        Assert.True(attributesPage.Visible);
        Assert.False(titlesPage.Visible);
        Assert.Equal(RetailUiStateIds.Closed, titlesTab.ActiveRetailStateId);
    }

    // ── Affordability helpers (GetRaiseCost) ──────────────────────────────────

    [Fact]
    public void SkillsTab_Click_RebuildsListWithRetailBucketsAndRows()
    {
        var list = new UiPanel { Width = 300 };
        var layout = Fake((CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16));

        ClickTab(layout, left: 92f);

        var headerPanels = SkillHeaders(list);
        var headers = headerPanels
            .Select(c => c.Children.OfType<UiText>().First().LinesProvider()[0].Text)
            .ToList();
        Assert.Equal(new[]
        {
            "Specialized Skills",
            "Trained Skills",
            "Untrained Skills",
            "Unusable Skills",
        }, headers);
        Assert.Equal(new[] { 0x06000F90u, 0x06000F86u, 0x06000F98u, 0x06000F89u },
            headerPanels.Select(h => h.BackgroundSprite).ToArray());
        Assert.All(headerPanels, h =>
            Assert.Equal(Vector4.One, h.Children.OfType<UiText>().First().LinesProvider()[0].Color));

        var rows = SkillRows(list);
        Assert.Equal(12, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(Vector4.Zero, row.BackgroundColor);
            Assert.Equal(0x06004CC2u, row.BackgroundSprite);
        });
        var rowNames = rows
            .Select(r => r.Children.OfType<UiText>().ToList()[1].LinesProvider()[0].Text)
            .ToList();
        Assert.Equal(new[]
        {
            "Melee Defense", "War Magic",
            "Arcane Lore", "Life Magic", "Missile Weapons",
            "Healing", "Jump", "Loyalty", "Run",
            "Alchemy", "Cooking", "Fletching",
        }, rowNames);

        var meleeTexts = rows[0].Children.OfType<UiText>().ToList();
        Assert.Equal(Vector4.One, meleeTexts[1].LinesProvider()[0].Color);
        Assert.Equal(new Vector4(0f, 1f, 0f, 1f), meleeTexts[2].LinesProvider()[0].Color);

        var healingTexts = rows[5].Children.OfType<UiText>().ToList();
        Assert.Equal(Vector4.One, healingTexts[1].LinesProvider()[0].Color);
        Assert.Equal(Vector4.One, healingTexts[2].LinesProvider()[0].Color);
    }

    [Fact]
    public void DataChangedRefresh_MovesATrainedSkillToItsSection_WithoutAClick()
    {
        var list = new UiPanel { Width = 300 };
        var layout = Fake((CharacterStatController.ListBoxId, list));

        CharacterSheet sheet = SampleData.SampleCharacter();
        Action refresh = CharacterStatController.Bind(layout, () => sheet,
            spriteResolve: id => (id, 16, 16)).Refresh;

        ClickTab(layout, left: 92f);
        var untrained = sheet.Skills.First(
            s => s.AdvancementClass == CharacterSkillAdvancementClass.Untrained);
        int trainedBefore = sheet.Skills.Count(
            s => s.AdvancementClass == CharacterSkillAdvancementClass.Trained);
        Assert.Equal(trainedBefore, RowsUnderHeader(list, "Trained Skills"));

        CharacterSheet updated = SampleData.SampleCharacter();
        updated.GetType();
        sheet = new CharacterSheet
        {
            Name = sheet.Name,
            UnassignedXp = sheet.UnassignedXp,
            SkillCredits = sheet.SkillCredits,
            Skills = sheet.Skills
                .Select(s => s.Id == untrained.Id
                    ? s with { AdvancementClass = CharacterSkillAdvancementClass.Trained }
                    : s)
                .ToList(),
        };

        refresh();

        Assert.Equal(trainedBefore + 1, RowsUnderHeader(list, "Trained Skills"));
    }

    [Fact]
    public void Rows_CarryTheSharedTooltipPopupLocatorAndDescriptionText()
    {
        var list = new UiPanel { Width = 300 };
        var layout = Fake((CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16));

        var attributeRows = Descendants(list).OfType<UiClickablePanel>().ToList();
        Assert.NotEmpty(attributeRows);
        Assert.All(attributeRows, row =>
        {
            Assert.Equal(
                RetailTooltipPresenter.SharedPopupSkinRootElementId,
                row.AuthoredTooltipRootElementId);
            Assert.Equal(
                RetailTooltipPresenter.SharedPopupSkinLayoutDid,
                row.AuthoredTooltipLayoutDid);
        });
        Assert.All(
            attributeRows.Take(6),
            row => Assert.False(string.IsNullOrEmpty(row.GetTooltipText())));
    }

    private static int RowsUnderHeader(UiElement list, string header)
    {
        static bool IsHeader(UiElement c, string text) =>
            c is UiPanel and not UiClickablePanel
            && c.Children.OfType<UiText>().Any(
                x => x.LinesProvider()[0].Text == text);
        UiElement? container = new[] { list }.Concat(Descendants(list))
            .FirstOrDefault(e => e.Children.Any(c => IsHeader(c, header)));
        Assert.NotNull(container);
        var children = container!.Children.ToList();
        int start = children.FindIndex(c => IsHeader(c, header));
        Assert.True(start >= 0, $"header '{header}' not found");
        int count = 0;
        for (int i = start + 1; i < children.Count; i++)
        {
            if (children[i] is UiClickablePanel) count++;
            else if (children[i] is UiPanel p
                     && p.Children.OfType<UiText>().Any()) break;
        }
        return count;
    }

    [Fact]
    public void SkillsTab_ClickThenAttributesTab_RestoresAttributeRows()
    {
        var list = new UiPanel { Width = 300 };
        var layout = Fake((CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16));

        ClickTab(layout, left: 92f);
        Assert.Equal(12, SkillRows(list).Count);

        ClickTab(layout, left: 0f);
        var rows = Descendants(list).OfType<UiClickablePanel>().ToList();
        Assert.Equal(9, rows.Count);
        Assert.Equal("Strength", rows[0].Children.OfType<UiText>().ToList()[1].LinesProvider()[0].Text);
    }

    [Fact]
    public void SkillsTab_MouseClick_RebuildsVisibleListWhenListIdIsDuplicated()
    {
        var root = new UiPanel { Width = 300, Height = 600 };
        var attrPage = MakeDatElement(CharacterStatController.AttributesPageId, top: 25, width: 300, height: 575);
        var hiddenPage = MakeDatElement(CharacterStatController.SkillsPageId, top: 25, width: 300, height: 575);
        var name = new UiText();
        var visibleList = MakeDatElement(CharacterStatController.ListBoxId, top: 112, width: 300, height: 398);
        var hiddenDuplicateList = MakeDatElement(CharacterStatController.ListBoxId, top: 112, width: 300, height: 398);

        attrPage.AddChild(name);
        attrPage.AddChild(visibleList);
        hiddenPage.AddChild(hiddenDuplicateList);
        root.AddChild(attrPage);
        root.AddChild(hiddenPage);
        var skillsTab = MakeTab(CharacterStatController.TabSkillsId, left: 92f);
        root.AddChild(skillsTab);

        var layout = new ImportedLayout(root, new Dictionary<uint, UiElement>
        {
            [CharacterStatController.NameId] = name,
            // Mirrors the real import: the id dictionary can point at a hidden duplicate.
            [CharacterStatController.ListBoxId] = hiddenDuplicateList,
            [CharacterStatController.TabSkillsId] = skillsTab,
        });

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16));

        Assert.Equal("Strength", FirstRowName(visibleList));
        Assert.Equal("<no row>", FirstRowName(hiddenDuplicateList));

        var ui = new UiRoot { Width = 300, Height = 600 };
        ui.AddChild(root);
        Assert.Same(skillsTab, ui.Pick(132, 12));

        ui.OnMouseDown(UiMouseButton.Left, 132, 12);
        ui.OnMouseUp(UiMouseButton.Left, 132, 12);

        Assert.Equal("Melee Defense", FirstRowName(visibleList));
        Assert.Equal("<no row>", FirstRowName(hiddenDuplicateList));
    }

    [Fact]
    public void SkillsTab_SelectWarMagic_ShowsTrainedSkillFooter()
    {
        var list = new UiPanel { Width = 300 };
        var title = new UiText();
        var l1Label = new UiText();
        var l1Value = new UiText();
        var l2Label = new UiText();
        var l2Value = new UiText();
        var layout = Fake(
            (CharacterStatController.ListBoxId, list),
            (CharacterStatController.FooterTitleId, title),
            (CharacterStatController.FooterLine1Label, l1Label),
            (CharacterStatController.FooterLine1Value, l1Value),
            (CharacterStatController.FooterLine2Label, l2Label),
            (CharacterStatController.FooterLine2Value, l2Value));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16));

        ClickTab(layout, left: 92f);
        var rows = SkillRows(list);
        rows[1].OnClick!();

        Assert.Equal("War Magic: 285 (+5)", title.LinesProvider()[0].Text);
        Assert.Equal("Experience To Raise:", l1Label.LinesProvider()[0].Text);
        Assert.Equal((11_100_000L).ToString("N0", CultureInfo.InvariantCulture), l1Value.LinesProvider()[0].Text);
        Assert.Equal("Unassigned Experience:", l2Label.LinesProvider()[0].Text);
        Assert.Equal((87_757_321_741L).ToString("N0", CultureInfo.InvariantCulture), l2Value.LinesProvider()[0].Text);
        Assert.Equal(0x06000F93u, rows[1].BackgroundSprite);
        Assert.Equal(Vector4.Zero, rows[1].BackgroundColor);
        Assert.Equal(0x06004CC2u, rows[0].BackgroundSprite);
        Assert.Equal(Vector4.Zero, rows[0].BackgroundColor);
    }

    [Fact]
    public void SkillsTab_ClickRaiseTen_EmitsSkillRaiseRequest()
    {
        var list = new UiPanel { Width = 300 };
        var btn10 = MakeButton();
        var layout = Fake(
            (CharacterStatController.ListBoxId, list),
            (CharacterStatController.RaiseTenId, btn10));
        var requests = new List<CharacterStatController.RaiseRequest>();

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16),
            onRaiseRequest: (request, completed) => { requests.Add(request); completed(); });

        ClickTab(layout, left: 92f);
        SkillRows(list)[1].OnClick!();
        btn10.OnClick!();

        var request = Assert.Single(requests);
        Assert.Equal(CharacterStatController.RaiseTargetKind.Skill, request.Kind);
        Assert.Equal(34u, request.StatId);
        Assert.Equal(111_000_000L, request.Cost);
        Assert.Equal(10, request.Amount);
    }

    [Fact]
    public void SkillsTab_SelectHealing_ShowsUntrainedSkillFooter()
    {
        var list = new UiPanel { Width = 300 };
        var title = new UiText();
        var l1Label = new UiText();
        var l1Value = new UiText();
        var l2Label = new UiText();
        var l2Value = new UiText();
        var btn1 = MakeButton();
        var layout = Fake(
            (CharacterStatController.ListBoxId, list),
            (CharacterStatController.FooterTitleId, title),
            (CharacterStatController.FooterLine1Label, l1Label),
            (CharacterStatController.FooterLine1Value, l1Value),
            (CharacterStatController.FooterLine2Label, l2Label),
            (CharacterStatController.FooterLine2Value, l2Value),
            (CharacterStatController.RaiseOneId, btn1));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16));

        ClickTab(layout, left: 92f);
        SkillRows(list)[5].OnClick!();

        Assert.Equal("Healing", title.LinesProvider()[0].Text);
        Assert.Equal("Skill Credits To Raise:", l1Label.LinesProvider()[0].Text);
        Assert.Equal("6", l1Value.LinesProvider()[0].Text);
        Assert.Equal("Skill Credits Available:", l2Label.LinesProvider()[0].Text);
        Assert.Equal("96", l2Value.LinesProvider()[0].Text);
        Assert.True(btn1.Visible);
        Assert.Equal("Normal", btn1.ActiveState);
    }

    [Fact]
    public void SkillsTab_ClickUntrainedSkill_EmitsTrainSkillRequest()
    {
        var list = new UiPanel { Width = 300 };
        var btn1 = MakeButton();
        var layout = Fake(
            (CharacterStatController.ListBoxId, list),
            (CharacterStatController.RaiseOneId, btn1));
        var requests = new List<CharacterStatController.RaiseRequest>();

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16),
            onRaiseRequest: (request, completed) => { requests.Add(request); completed(); });

        ClickTab(layout, left: 92f);
        SkillRows(list)[5].OnClick!();
        btn1.OnClick!();

        var request = Assert.Single(requests);
        Assert.Equal(CharacterStatController.RaiseTargetKind.TrainSkill, request.Kind);
        Assert.Equal(21u, request.StatId);
        Assert.Equal(6L, request.Cost);
        Assert.Equal(1, request.Amount);
    }

    [Fact]
    public void SkillsTab_ClickTrain_RebuildsSelectedSkillAsTrained()
    {
        var list = new UiPanel { Width = 300 };
        var btn1 = MakeButton();
        var btn10 = MakeButton();
        CharacterSheet sheet = new()
        {
            SkillCredits = 10,
            UnassignedXp = 1_000,
            Skills = new[]
            {
                new CharacterSkill(100u, "Train Me", 0x06000001u,
                    CharacterSkillAdvancementClass.Untrained,
                    BaseLevel: 5,
                    CurrentLevel: 5,
                    UsableUntrained: true,
                    TrainedCost: 4,
                    SpecializedCost: 0,
                    RaiseCost: 0,
                    Raise10Cost: 0),
            },
        };
        var layout = Fake(
            (CharacterStatController.ListBoxId, list),
            (CharacterStatController.RaiseOneId, btn1),
            (CharacterStatController.RaiseTenId, btn10));
        var requests = new List<CharacterStatController.RaiseRequest>();

        CharacterStatController.Bind(layout, () => sheet,
            spriteResolve: id => (id, 16, 16),
            onRaiseRequest: (request, completed) =>
            {
                requests.Add(request);
                sheet = new CharacterSheet
                {
                    SkillCredits = 6,
                    UnassignedXp = 1_000,
                    Skills = new[]
                    {
                        new CharacterSkill(100u, "Train Me", 0x06000001u,
                            CharacterSkillAdvancementClass.Trained,
                            BaseLevel: 5,
                            CurrentLevel: 5,
                            UsableUntrained: true,
                            TrainedCost: 4,
                            SpecializedCost: 0,
                            RaiseCost: 10,
                        Raise10Cost: 100),
                    },
                };
                completed();
            });

        ClickTab(layout, left: 92f);
        SkillRows(list).Single().OnClick!();
        Assert.False(btn10.Visible);

        btn1.OnClick!();

        var request = Assert.Single(requests);
        Assert.Equal(CharacterStatController.RaiseTargetKind.TrainSkill, request.Kind);
        Assert.Equal("Train Me", SkillRows(list).Single().Children.OfType<UiText>().ToList()[1].LinesProvider()[0].Text);
        Assert.True(btn1.Visible);
        Assert.True(btn10.Visible);
        Assert.Equal("Normal", btn1.ActiveState);
        Assert.Equal("Normal", btn10.ActiveState);
    }

    [Fact]
    public void SkillsTab_DeferredTrainRefreshesOnlyWhenRequestCompletes()
    {
        var list = new UiPanel { Width = 300 };
        var btn1 = MakeButton();
        var btn10 = MakeButton();
        CharacterSheet sheet = TrainingSheet(CharacterSkillAdvancementClass.Untrained);
        var layout = Fake(
            (CharacterStatController.ListBoxId, list),
            (CharacterStatController.RaiseOneId, btn1),
            (CharacterStatController.RaiseTenId, btn10));
        Action? completeRaise = null;

        CharacterStatController.Bind(layout, () => sheet,
            spriteResolve: id => (id, 16, 16),
            onRaiseRequest: (_, completed) => completeRaise = completed);

        ClickTab(layout, left: 92f);
        SkillRows(list).Single().OnClick!();
        btn1.OnClick!();
        sheet = TrainingSheet(CharacterSkillAdvancementClass.Trained);

        Assert.NotNull(completeRaise);
        Assert.False(btn10.Visible);

        completeRaise!();

        Assert.True(btn10.Visible);

        static CharacterSheet TrainingSheet(CharacterSkillAdvancementClass advancement)
            => new()
            {
                SkillCredits = advancement == CharacterSkillAdvancementClass.Untrained ? 10 : 6,
                UnassignedXp = 1_000,
                Skills =
                [
                    new CharacterSkill(
                        100u,
                        "Train Me",
                        0x06000001u,
                        advancement,
                        BaseLevel: 5,
                        CurrentLevel: 5,
                        UsableUntrained: true,
                        TrainedCost: 4,
                        SpecializedCost: 0,
                        RaiseCost: advancement == CharacterSkillAdvancementClass.Untrained ? 0 : 10,
                        Raise10Cost: advancement == CharacterSkillAdvancementClass.Untrained ? 0 : 100),
                ],
            };
    }

    [Fact]
    public void SkillsTab_BindsCharacterScrollbarToScrollableViewport()
    {
        var root = new UiPanel { Width = 300, Height = 600 };
        var page = new UiPanel { Width = 300, Height = 600 };
        var name = new UiText();
        var list = MakeDatElement(CharacterStatController.ListBoxId, top: 137, width: 300, height: 80);
        var scrollbarShell = MakeDatElement(CharacterStatController.ListScrollbarId, top: 137, width: 16, height: 80);
        scrollbarShell.Left = 281;

        page.AddChild(name);
        page.AddChild(list);
        page.AddChild(scrollbarShell);
        root.AddChild(page);
        var skillsTab = MakeTab(CharacterStatController.TabSkillsId, left: 92f);
        root.AddChild(skillsTab);

        var layout = new ImportedLayout(root, new Dictionary<uint, UiElement>
        {
            [CharacterStatController.NameId] = name,
            [CharacterStatController.ListBoxId] = list,
            [CharacterStatController.ListScrollbarId] = scrollbarShell,
            [CharacterStatController.TabSkillsId] = skillsTab,
        });

        CharacterStatController.Bind(layout, SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16));

        ClickTab(layout, left: 92f);

        var bar = page.Children.OfType<UiScrollbar>().Single();
        Assert.True(bar.Visible);
        Assert.NotNull(bar.Model);
        Assert.Contains(list.Children, c => c is UiScrollablePanel);
        Assert.False(scrollbarShell.Visible);
    }

    [Fact]
    public void GetRaiseCost_Index4Focus_Returns110()
    {
        var sheet = SampleData.SampleCharacter();
        Assert.Equal(110L, CharacterStatController.GetRaiseCost(sheet, 4));
    }

    [Fact]
    public void GetRaiseCost_Amount10Index4Focus_Returns1100()
    {
        var sheet = SampleData.SampleCharacter();
        Assert.Equal(1_100L, CharacterStatController.GetRaiseCost(sheet, 4, amount: 10));
    }

    [Fact]
    public void GetRaiseCost_Index0Strength_Returns0()
    {
        var sheet = SampleData.SampleCharacter();
        Assert.Equal(0L, CharacterStatController.GetRaiseCost(sheet, 0));
    }

    [Fact]
    public void GetRaiseCost_OutOfRange_Returns0()
    {
        var sheet = SampleData.SampleCharacter();
        Assert.Equal(0L, CharacterStatController.GetRaiseCost(sheet, 99));
    }

    // ── GetRowName helper ─────────────────────────────────────────────────────

    [Fact]
    public void GetRowName_Index0_ReturnsStrength()
        => Assert.Equal("Strength", CharacterStatController.GetRowName(0));

    [Fact]
    public void GetRowName_Index4_ReturnsFocus()
        => Assert.Equal("Focus", CharacterStatController.GetRowName(4));

    [Fact]
    public void GetRowName_Index6_ReturnsHealth()
        => Assert.Equal("Health", CharacterStatController.GetRowName(6));

    [Fact]
    public void GetRowName_NegativeIndex_ReturnsEmpty()
        => Assert.Equal(string.Empty, CharacterStatController.GetRowName(-1));


    [Fact]
    public void GetAttributeDelta_ComputesEffectiveMinusBase()
    {
        var sheet = new CharacterSheet { Strength = 220, AttributeBaseValues = [200, 0, 0, 0, 0, 0] };
        Assert.Equal(20, CharacterStatController.GetAttributeDelta(sheet, 0));
    }

    [Fact]
    public void GetAttributeDelta_VitalRowIndex_ReturnsZero()
    {
        var sheet = new CharacterSheet { AttributeBaseValues = [200, 0, 0, 0, 0, 0] };
        Assert.Equal(0, CharacterStatController.GetAttributeDelta(sheet, 6));
    }

    [Fact]
    public void GetAttributeDelta_EmptyBaseValues_ReturnsZero()
    {
        // SampleData / older sheets that don't populate AttributeBaseValues
        // must not throw or fabricate a delta.
        var sheet = new CharacterSheet { Strength = 220 };
        Assert.Equal(0, CharacterStatController.GetAttributeDelta(sheet, 0));
    }

    [Fact]
    public void GetSkillBuffOnlyDelta_IsolatesBuffFromVitae()
    {
        var skill = new CharacterSkill(1u, "S", 0u, CharacterSkillAdvancementClass.Trained,
            BaseLevel: 300, CurrentLevel: 251, UsableUntrained: true,
            TrainedCost: 0, SpecializedCost: 0, RaiseCost: 0, VitaeModifier: -99);
        Assert.Equal(50, CharacterStatController.GetSkillBuffOnlyDelta(skill));
    }

    [Fact]
    public void SkillValueColor_SubtractsVitaeBeforeChoosingFontState()
    {
        var vitaeOnly = new CharacterSkill(
            1u, "S", 0u, CharacterSkillAdvancementClass.Trained,
            BaseLevel: 300, CurrentLevel: 201, UsableUntrained: true,
            TrainedCost: 0, SpecializedCost: 0, RaiseCost: 0,
            VitaeModifier: -99);
        var vitaeAndBuff = vitaeOnly with { CurrentLevel = 211 };

        Assert.Equal(Vector4.One, CharacterStatController.SkillValueColor(vitaeOnly));
        Assert.Equal(
            new Vector4(0f, 1f, 0f, 1f),
            CharacterStatController.SkillValueColor(vitaeAndBuff));
    }

    [Fact]
    public void AttributeAndVitalValueColors_FollowRetailResidualComparison()
    {
        var sheet = new CharacterSheet
        {
            Strength = 220,
            AttributeBaseValues = [200, 0, 0, 0, 0, 0],
            HealthMax = 170,
            VitalBaseMaxValues = [200, 0, 0],
            VitalVitaeModifiers = [-40, 0, 0],
        };

        Assert.Equal(
            new Vector4(0f, 1f, 0f, 1f),
            CharacterStatController.AttributeValueColor(sheet, 0));
        Assert.Equal(
            new Vector4(0f, 1f, 0f, 1f),
            CharacterStatController.VitalValueColor(sheet, 0));
    }

    [Fact]
    public void RowClick_AttributeWithBuff_FooterTitleShowsPositiveDelta()
    {
        var title = new UiText();
        var list  = new UiPanel();
        var layout = Fake(
            (CharacterStatController.FooterTitleId, title),
            (CharacterStatController.ListBoxId, list));

        CharacterSheet Sheet() => new() { Strength = 220, AttributeBaseValues = [200, 0, 0, 0, 0, 0] };
        CharacterStatController.Bind(layout, Sheet);

        Descendants(list).OfType<UiClickablePanel>().ToList()[0].OnClick!();   // Strength = index 0

        Assert.Equal("Strength: 220 (+20)", title.LinesProvider()[0].Text);
    }

    [Fact]
    public void RowClick_AttributeWithDebuff_FooterTitleShowsNegativeDelta()
    {
        var title = new UiText();
        var list  = new UiPanel();
        var layout = Fake(
            (CharacterStatController.FooterTitleId, title),
            (CharacterStatController.ListBoxId, list));

        CharacterSheet Sheet() => new() { Endurance = 180, AttributeBaseValues = [0, 200, 0, 0, 0, 0] };
        CharacterStatController.Bind(layout, Sheet);

        Descendants(list).OfType<UiClickablePanel>().ToList()[1].OnClick!();   // Endurance = index 1

        Assert.Equal("Endurance: 180 (-20)", title.LinesProvider()[0].Text);
    }

    [Fact]
    public void RowClick_AttributeZeroDelta_NoParenthetical()
    {
        var title = new UiText();
        var list  = new UiPanel();
        var layout = Fake(
            (CharacterStatController.FooterTitleId, title),
            (CharacterStatController.ListBoxId, list));

        CharacterSheet Sheet() => new() { Strength = 200, AttributeBaseValues = [200, 0, 0, 0, 0, 0] };
        CharacterStatController.Bind(layout, Sheet);

        Descendants(list).OfType<UiClickablePanel>().ToList()[0].OnClick!();

        Assert.Equal("Strength: 200", title.LinesProvider()[0].Text);
    }

    [Fact]
    public void SkillClick_VitaeOnly_FooterTitleShowsVitaeParenthetical()
    {
        var list  = new UiPanel { Width = 300 };
        var title = new UiText();
        var layout = Fake(
            (CharacterStatController.ListBoxId, list),
            (CharacterStatController.FooterTitleId, title));

        CharacterSheet Sheet() => VitaeSkillSheet(currentLevel: 203, baseLevel: 303, vitaeModifier: -100);
        CharacterStatController.Bind(layout, Sheet, spriteResolve: id => (id, 16, 16));

        ClickTab(layout, left: 92f);
        SkillRows(list)[0].OnClick!();

        Assert.Equal("Test Skill: 203 (-100)", title.LinesProvider()[0].Text);
    }

    [Fact]
    public void SkillClick_VitaePlusBuff_FooterTitleShowsBothParentheticals()
    {
        var list  = new UiPanel { Width = 300 };
        var title = new UiText();
        var layout = Fake(
            (CharacterStatController.ListBoxId, list),
            (CharacterStatController.FooterTitleId, title));

        CharacterSheet Sheet() => VitaeSkillSheet(currentLevel: 251, baseLevel: 300, vitaeModifier: -99);
        CharacterStatController.Bind(layout, Sheet, spriteResolve: id => (id, 16, 16));

        ClickTab(layout, left: 92f);
        SkillRows(list)[0].OnClick!();

        Assert.Equal("Test Skill: 251 (-99) (+50)", title.LinesProvider()[0].Text);
    }

    [Fact]
    public void SkillFooter_UsesAuthoredPaletteForVitaeAndBuffRuns()
    {
        Vector4 normal = new(0.1f, 0.1f, 0.1f, 1f);
        Vector4 buff = new(0.2f, 0.3f, 0.4f, 1f);
        Vector4 debuff = new(0.5f, 0.6f, 0.7f, 1f);
        Vector4 vitae = new(127f / 255f, 1f, 1f, 1f);
        var list = new UiPanel { Width = 300 };
        var title = new UiText
        {
            FontColorPalette = [normal, buff, debuff, vitae],
        };
        var layout = Fake(
            (CharacterStatController.ListBoxId, list),
            (CharacterStatController.FooterTitleId, title));

        CharacterSheet Sheet() => VitaeSkillSheet(
            currentLevel: 251,
            baseLevel: 300,
            vitaeModifier: -99);
        CharacterStatController.Bind(
            layout,
            Sheet,
            spriteResolve: id => (id, 16, 16));

        ClickTab(layout, left: 92f);
        SkillRows(list)[0].OnClick!();

        IReadOnlyList<UiText.TextRun> runs = title.RunsProvider!();
        Assert.Equal(3, runs.Count);
        Assert.Equal(("Test Skill: 251", normal), (runs[0].Text, runs[0].Color));
        Assert.Equal((" (-99)", vitae), (runs[1].Text, runs[1].Color));
        Assert.Equal((" (+50)", buff), (runs[2].Text, runs[2].Color));
    }

    [Fact]
    public void SkillRow_ValueAndColorFollowLiveSheetWithoutTabRebuild()
    {
        var list = new UiPanel { Width = 300 };
        var layout = Fake((CharacterStatController.ListBoxId, list));
        CharacterSheet sheet = VitaeSkillSheet(
            currentLevel: 300,
            baseLevel: 300,
            vitaeModifier: 0);

        CharacterStatController.Bind(
            layout,
            () => sheet,
            spriteResolve: id => (id, 16, 16));
        ClickTab(layout, left: 92f);

        UiText value = SkillRows(list)[0].Children.OfType<UiText>().ToList()[2];
        Assert.Equal("300", value.LinesProvider()[0].Text);
        Assert.Equal(Vector4.One, value.LinesProvider()[0].Color);

        sheet = VitaeSkillSheet(
            currentLevel: 350,
            baseLevel: 300,
            vitaeModifier: 0);

        Assert.Equal("350", value.LinesProvider()[0].Text);
        Assert.Equal(new Vector4(0f, 1f, 0f, 1f), value.LinesProvider()[0].Color);
    }

    [Fact]
    public void SkillClick_ZeroDelta_NoParentheticals()
    {
        var list  = new UiPanel { Width = 300 };
        var title = new UiText();
        var layout = Fake(
            (CharacterStatController.ListBoxId, list),
            (CharacterStatController.FooterTitleId, title));

        CharacterSheet Sheet() => VitaeSkillSheet(currentLevel: 300, baseLevel: 300, vitaeModifier: 0);
        CharacterStatController.Bind(layout, Sheet, spriteResolve: id => (id, 16, 16));

        ClickTab(layout, left: 92f);
        SkillRows(list)[0].OnClick!();

        Assert.Equal("Test Skill: 300", title.LinesProvider()[0].Text);
    }

    private static CharacterSheet VitaeSkillSheet(int currentLevel, int baseLevel, int vitaeModifier) => new()
    {
        SkillCredits = 0,
        UnassignedXp = 1_000_000,
        Skills =
        [
            new CharacterSkill(
                200u,
                "Test Skill",
                0x06000001u,
                CharacterSkillAdvancementClass.Trained,
                BaseLevel: baseLevel,
                CurrentLevel: currentLevel,
                UsableUntrained: true,
                TrainedCost: 0,
                SpecializedCost: 0,
                RaiseCost: 100,
                Raise10Cost: 1000,
                VitaeModifier: vitaeModifier),
        ],
    };

    // ── SampleData sanity ─────────────────────────────────────────────────────

    [Fact]
    public void SampleCharacter_SkillCredits_Is96()
        => Assert.Equal(96, SampleData.SampleCharacter().SkillCredits);

    [Fact]
    public void SampleCharacter_UnassignedXp_IsSet()
        => Assert.Equal(87_757_321_741L, SampleData.SampleCharacter().UnassignedXp);

    [Fact]
    public void SampleCharacter_AttributeRaiseCosts_HasNineEntries()
    {
        var costs = SampleData.SampleCharacter().AttributeRaiseCosts;
        Assert.NotNull(costs);
        Assert.Equal(9, costs.Length);
    }

    [Fact]
    public void SampleCharacter_AttributeRaise10Costs_HasNineEntries()
    {
        var costs = SampleData.SampleCharacter().AttributeRaise10Costs;
        Assert.NotNull(costs);
        Assert.Equal(9, costs.Length);
    }

    [Fact]
    public void SampleCharacter_AttributeRaiseCosts_FocusAt110()
        => Assert.Equal(110L, SampleData.SampleCharacter().AttributeRaiseCosts[4]);

    [Fact]
    public void SampleCharacter_AttributeRaise10Costs_FocusAt1100()
        => Assert.Equal(1_100L, SampleData.SampleCharacter().AttributeRaise10Costs[4]);

    [Fact]
    public void SampleCharacter_AttributeRaiseCosts_StrengthAt0()
        => Assert.Equal(0L, SampleData.SampleCharacter().AttributeRaiseCosts[0]);

    // ── UiText flag sanity ────────────────────────────────────────────────────

    [Fact]
    public void UiText_RightAligned_DefaultFalse()
    {
        var t = new UiText();
        Assert.False(t.RightAligned);
    }

    [Fact]
    public void UiText_RightAligned_CanBeSetTrue()
    {
        var t = new UiText { RightAligned = true };
        Assert.True(t.RightAligned);
    }


    [Fact]
    public void Bind_LevelCaptionId_SetsCharacterLevelText()
    {
        var caption = new UiText();
        var layout  = Fake((CharacterStatController.LevelCaptionId, caption));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var lines = caption.LinesProvider();
        Assert.True(lines.Count >= 1, "LevelCaption must provide at least one line");
        Assert.Equal("Character", lines[0].Text);
        Assert.False(caption.Centered,      "LevelCaption must be left-justified (Centered=false)");
        Assert.False(caption.RightAligned,  "LevelCaption must be left-justified (RightAligned=false)");
    }

    [Fact]
    public void Bind_TotalXpLabelId_SetsTotalExperienceXpText()
    {
        var lbl    = new UiText();
        var layout = Fake((CharacterStatController.TotalXpLabelId, lbl));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        Assert.Equal("Total Experience (XP):", lbl.LinesProvider()[0].Text);
        Assert.False(lbl.Centered,     "TotalXpLabel must be left-justified (Centered=false)");
        Assert.False(lbl.RightAligned, "TotalXpLabel must be left-justified (RightAligned=false)");
    }

    [Fact]
    public void Bind_TotalXpValue_IsRightAligned()
    {
        var value = new UiText { Centered = true };
        var layout = Fake((CharacterStatController.TotalXpId, value));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        Assert.Equal((1_250_000_000L).ToString("N0", CultureInfo.InvariantCulture), value.LinesProvider()[0].Text);
        Assert.False(value.Centered);
        Assert.True(value.RightAligned);
    }


    [Fact]
    public void Bind_NameColor_IsWhite()
    {
        var name   = new UiText();
        var layout = Fake((CharacterStatController.NameId, name));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var color = name.LinesProvider()[0].Color;
        Assert.Equal(1f, color.X, precision: 3);
        Assert.Equal(1f, color.Y, precision: 3);
        Assert.Equal(1f, color.Z, precision: 3);
        Assert.Equal(1f, color.W, precision: 3);
    }

    [Fact]
    public void RowClick_MaxedRow_FooterLine1ValueIsInfinity()
    {
        var val  = new UiText();
        var list = new UiPanel();
        var layout = Fake(
            (CharacterStatController.FooterLine1Value, val),
            (CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);
        Descendants(list).OfType<UiClickablePanel>().ToList()[0].OnClick!();   // Strength

        Assert.Equal("Infinity!", val.LinesProvider()[0].Text);
    }

    [Fact]
    public void RowClick_SelectedFooterTitle_IsWhite()
    {
        var title = new UiText();
        var list  = new UiPanel();
        var layout = Fake(
            (CharacterStatController.FooterTitleId, title),
            (CharacterStatController.ListBoxId, list));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);
        Descendants(list).OfType<UiClickablePanel>().ToList()[4].OnClick!();   // Focus

        var color = title.LinesProvider()[0].Color;
        Assert.Equal(1f, color.X, precision: 3);
        Assert.Equal(1f, color.Y, precision: 3);
        Assert.Equal(1f, color.Z, precision: 3);
        Assert.Equal(1f, color.W, precision: 3);
    }

    [Fact]
    public void Bind_FooterStateA_TitleColor_IsBodyNotWhite()
    {
        // State A title is body (parchment), not white.
        var title  = new UiText();
        var layout = Fake((CharacterStatController.FooterTitleId, title));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var color = title.LinesProvider()[0].Color;
        Assert.True(color.X < 1f || color.Y < 1f || color.Z < 1f,
            "State-A title should be body/parchment color, not pure white");
    }

    [Fact]
    public void Bind_LevelCaptionId_SetsTwoLines()
    {
        var caption = new UiText();
        var layout  = Fake((CharacterStatController.LevelCaptionId, caption));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        var lines = caption.LinesProvider();
        Assert.Equal(2, lines.Count);
        Assert.Equal("Character", lines[0].Text);
        Assert.Equal("Level",     lines[1].Text);
    }

    // ── Robustness ────────────────────────────────────────────────────────────

    [Fact]
    public void Bind_MissingElements_DoesNotThrow()
        => CharacterStatController.Bind(Fake(), SampleData.SampleCharacter);


    [Fact]
    public void Bind_XpMeter_XpNextLabel_IsBoundViaFindElement()
    {
        var meter    = new UiMeter();
        var xpLabel  = new UiText();
        meter.AddChild(xpLabel);
        var layout = Fake(
            (CharacterStatController.XpMeterId,    meter),
            (CharacterStatController.XpNextLabelId, xpLabel));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        Assert.NotNull(xpLabel.LinesProvider);
        var lines = xpLabel.LinesProvider();
        Assert.Single(lines);
        Assert.Equal("XP for next level:", lines[0].Text);
    }

    [Fact]
    public void Bind_XpMeter_XpNextValue_IsBoundViaFindElement()
    {
        var meter    = new UiMeter();
        var xpValue  = new UiText();
        meter.AddChild(xpValue);
        var layout = Fake(
            (CharacterStatController.XpMeterId,    meter),
            (CharacterStatController.XpNextValueId, xpValue));

        CharacterStatController.Bind(layout, SampleData.SampleCharacter);

        Assert.NotNull(xpValue.LinesProvider);
        Assert.True(xpValue.ClickThrough, "XP value overlay must be ClickThrough");
        Assert.True(xpValue.RightAligned, "XP value overlay must be RightAligned");

        var lines = xpValue.LinesProvider();
        Assert.Single(lines);
        // XpToNextLevel from SampleData = 42_000_000L formatted as "42,000,000"
        Assert.Equal((42_000_000L).ToString("N0", CultureInfo.InvariantCulture), lines[0].Text);
    }

    [Fact]
    public void Bind_XpMeter_MissingTextChildren_DoesNotThrow()
    {
        var meter  = new UiMeter();
        var layout = Fake((CharacterStatController.XpMeterId, meter));

        // No XpNextLabelId or XpNextValueId in the layout.
        var ex = Record.Exception(() =>
            CharacterStatController.Bind(layout, SampleData.SampleCharacter));

        Assert.Null(ex);
        // Fill must still be bound.
        Assert.NotNull(meter.Fill());
    }

    [Fact]
    public void ProductionFixture_MountedPagesUseRetailListFooterAndScrollbarReflow()
    {
        var layout = FixtureLoader.LoadCharacter();
        CharacterStatController.Bind(
            layout,
            SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16));

        ApplyLayoutPass(layout.Root);

        var page = layout.Root.Children.Single(
            e => e.DatElementId == CharacterStatController.AttributesPageId);
        var list = Descendants(page).Single(
            e => e.DatElementId == CharacterStatController.ListBoxId);
        var statLayout = list.Parent!;
        var scrollbar = statLayout.Children.OfType<UiScrollbar>().Single(
            e => e.DatElementId == CharacterStatController.ListScrollbarId);
        var divider = statLayout.Children.Single(
            e => e.DatElementId == CharacterStatController.ListDividerId);
        var footers = statLayout.Children.Where(
            e => e.DatElementId is CharacterStatController.FooterStateAId
                or CharacterStatController.FooterStateBId
                or CharacterStatController.FooterStateCId).ToList();

        Assert.Equal((112f, 398f), (list.Top, list.Height));
        Assert.Equal((112f, 398f), (scrollbar.Top, scrollbar.Height));
        Assert.Equal((510f, 7f), (divider.Top, divider.Height));
        Assert.Equal(3, footers.Count);
        Assert.All(footers, footer => Assert.Equal((520f, 55f), (footer.Top, footer.Height)));
        Assert.True(
            scrollbar.Visible,
            $"attributes-tab scrollbar: model={scrollbar.Model is not null}, " +
            $"resolve={scrollbar.SpriteResolve is not null}, track=0x{scrollbar.TrackSprite:X8}");
        Assert.NotNull(scrollbar.Model);

        ClickTab(layout, left: 92f);
        Assert.True(scrollbar.Visible);
        Assert.NotNull(scrollbar.Model);
        Assert.NotNull(scrollbar.SpriteResolve);
        Assert.Equal(0x06004C5Fu, scrollbar.TrackSprite);
        Assert.Equal(RetailScrollbarChrome.UpNormal, scrollbar.UpSprite);
        Assert.Equal(RetailScrollbarChrome.DownNormal, scrollbar.DownSprite);
        Assert.Equal(RetailScrollbarChrome.UpRollover, scrollbar.UpRolloverSprite);
        Assert.Equal(RetailScrollbarChrome.ThumbMidRollover, scrollbar.ThumbRolloverSprite);
    }

    [Fact]
    public void CharacterWindow_ResizesYWithinAuthoredHostClamp_AndReflowsListAndScrollbar()
    {
        var layout = FixtureLoader.LoadCharacter();
        CharacterStatController.Bind(
            layout,
            SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16));

        var root = new UiRoot { Width = 1280, Height = 1400 };
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            root,
            layout.Root,
            id => (id, 16, 16),
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Character,
                Chrome = RetailWindowChrome.NineSlice,
                Left = 540f,
                Top = 18f,
                ResizeX = false,
                ResizeY = true,
                ResizableEdges = ResizeEdges.Bottom,
                ConstrainResizeToParent = true,
                DatConstraintSource = HostConstraints(),
                DatConstraintSourceIsOuterFrame = true,
                ContentAnchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Bottom,
            });

        ApplyLayoutPass(handle.OuterFrame);

        var page = layout.Root.Children.Single(
            e => e.DatElementId == CharacterStatController.AttributesPageId);
        var list = Descendants(page).Single(
            e => e.DatElementId == CharacterStatController.ListBoxId);
        var statLayout = list.Parent!;
        var scrollbar = statLayout.Children.OfType<UiScrollbar>().Single(
            e => e.DatElementId == CharacterStatController.ListScrollbarId);
        var footer = statLayout.Children.First(
            e => e.DatElementId == CharacterStatController.FooterStateAId);
        var viewport = Assert.IsType<UiScrollablePanel>(list.Children.Single());

        Assert.Equal(310f, handle.OuterFrame.MinWidth);
        Assert.Equal(310f, handle.OuterFrame.MaxWidth);
        Assert.Equal(372f, handle.OuterFrame.MinHeight);
        Assert.Equal(1000f, handle.OuterFrame.MaxHeight);

        float originalOuterHeight = handle.Height;
        float originalListHeight = list.Height;
        float originalFooterBottomGap = statLayout.Height - (footer.Top + footer.Height);
        Assert.NotNull(scrollbar.Model);
        viewport.LayoutScrollableChildren();
        Assert.False(
            scrollbar.Model!.HasOverflow,
            "the fixture's authored default height fits all 9 rows without scrolling");

        handle.ResizeTo(handle.Width, 50f);
        Assert.Equal(372f, handle.Height);

        ApplyLayoutPass(handle.OuterFrame);
        viewport.LayoutScrollableChildren();

        Assert.True(list.Height < originalListHeight, "the stat list must shrink with the window");
        Assert.Equal((int)MathF.Floor(viewport.Height), scrollbar.Model!.ViewHeight);
        Assert.True(
            scrollbar.Model!.HasOverflow,
            "9 rows (180px content) must overflow the shrunk view");
        Assert.True(
            scrollbar.IsPresentationVisible,
            "an overflowing list's scrollbar must be presentation-visible (interactive), not hidden");

        // The footer stays bottom-docked: same distance from the stat
        // layout's own bottom edge before and after the shrink.
        float shrunkFooterBottomGap = statLayout.Height - (footer.Top + footer.Height);
        Assert.Equal(originalFooterBottomGap, shrunkFooterBottomGap, precision: 2);

        handle.ResizeTo(handle.Width, 5000f);
        Assert.Equal(1000f, handle.Height);

        ApplyLayoutPass(handle.OuterFrame);
        viewport.LayoutScrollableChildren();
        Assert.False(
            scrollbar.Model!.HasOverflow,
            "growing well past the content height restores no-overflow");
        Assert.False(
            scrollbar.IsPresentationVisible,
            "content that fits after growing large must HIDE the scrollbar (0x79 HideWhenDisabled), not show a disabled thumb");

        // Growing back to the ORIGINAL authored size restores the original
        // list height and the no-overflow state.
        handle.ResizeTo(handle.Width, originalOuterHeight);
        ApplyLayoutPass(handle.OuterFrame);
        viewport.LayoutScrollableChildren();

        Assert.Equal(originalOuterHeight, handle.Height);
        Assert.Equal(originalListHeight, list.Height, precision: 2);
        Assert.False(scrollbar.Model!.HasOverflow);
    }

    private static ElementInfo HostConstraints()
    {
        var info = new ElementInfo();
        var direct = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        direct.Properties.Values[0x3Fu] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Integer,
            IntegerValue = 310,
        };
        direct.Properties.Values[0x3Eu] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Integer,
            IntegerValue = 372,
        };
        direct.Properties.Values[0x3Du] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Integer,
            IntegerValue = 310,
        };
        direct.Properties.Values[0x3Cu] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Integer,
            IntegerValue = 1000,
        };
        info.States[UiStateInfo.DirectStateId] = direct;
        return info;
    }

    [Fact]
    public void ProductionFixture_HoveringTheSkillScrollbar_SelectsRolloverMedia()
    {
        var layout = FixtureLoader.LoadCharacter();
        CharacterStatController.Bind(
            layout,
            SampleData.SampleCharacter,
            spriteResolve: id => (id, 16, 16));
        var root = new UiRoot { Width = 800f, Height = 600f };
        root.AddChild(layout.Root);
        ApplyLayoutPass(layout.Root);

        ClickTab(layout, left: 92f);
        var page = layout.Root.Children.Single(
            e => e.DatElementId == CharacterStatController.AttributesPageId);
        var list = Descendants(page).Single(
            e => e.DatElementId == CharacterStatController.ListBoxId);
        var scrollbar = list.Parent!.Children.OfType<UiScrollbar>().Single(
            e => e.DatElementId == CharacterStatController.ListScrollbarId);
        Assert.True(scrollbar.Visible);
        Assert.NotNull(scrollbar.Model);
        scrollbar.Model!.ContentHeight = 800;
        scrollbar.Model.ViewHeight = 398;
        Assert.False(scrollbar.IsModelDisabled);

        var screen = scrollbar.ScreenPosition;
        // Over the up arrow (5px into the 16px top button).
        root.OnMouseMove((int)(screen.X + 8f), (int)(screen.Y + 5f));
        Assert.Equal(
            RetailScrollbarChrome.UpRollover, scrollbar.ActiveStartSpriteForTest);

        // Over the thumb (just below the up button; thumb starts at track top).
        root.OnMouseMove((int)(screen.X + 8f), (int)(screen.Y + 24f));
        Assert.Equal(
            RetailScrollbarChrome.ThumbMidRollover, scrollbar.ActiveThumbSpriteForTest);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void ClickTab(ImportedLayout layout, float left)
    {
        uint id = left switch
        {
            0f => CharacterStatController.TabAttribId,
            92f => CharacterStatController.TabSkillsId,
            _ => CharacterStatController.TabTitlesId,
        };
        var tab = Assert.IsType<UiText>(layout.FindElement(id));
        tab.OnClick!();
    }

    private static void ApplyLayoutPass(UiElement parent)
    {
        foreach (var child in parent.Children)
        {
            child.ApplyAnchor(parent.Width, parent.Height);
            ApplyLayoutPass(child);
        }
    }

    private static string FirstRowName(UiElement list)
    {
        var row = Descendants(list).OfType<UiClickablePanel>().FirstOrDefault();
        if (row is null) return "<no row>";
        var texts = row.Children.OfType<UiText>().ToList();
        return texts.Count > 1 ? texts[1].LinesProvider()[0].Text : "<no text>";
    }

    private static List<UiClickablePanel> SkillRows(UiElement list)
        => Descendants(list).OfType<UiClickablePanel>().ToList();

    private static List<UiPanel> SkillHeaders(UiElement list)
        => Descendants(list)
            .Where(c => c is UiPanel and not UiClickablePanel
                        && c.Children.OfType<UiText>().Any())
            .Cast<UiPanel>()
            .ToList();

    private static IEnumerable<UiElement> Descendants(UiElement root)
    {
        foreach (var child in root.Children)
        {
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }

    private static UiDatElement MakeDatElement(uint id, float top, float width, float height)
    {
        var info = new ElementInfo
        {
            Id = id,
            Type = 3,
            Y = top,
            Width = width,
            Height = height,
        };
        return new UiDatElement(info, static _ => (0u, 0, 0))
        {
            Top = top,
            Width = width,
            Height = height,
        };
    }

    private static UiButton MakeButton(uint id = 0u)
    {
        var info = new ElementInfo { Id = id, Type = 1 };
        info.StateMedia["Normal"] = (1u, 1);
        info.StateMedia["Ghosted"] = (2u, 1);
        return new UiButton(info, static _ => (0u, 0, 0));
    }

    private static UiText MakeTab(uint id, float left)
    {
        var info = new ElementInfo
        {
            Id = id,
            Type = 12,
            X = left,
            Width = 92f,
            Height = 25f,
            DefaultStateId = RetailUiStateIds.Closed,
            DefaultStateName = "Closed",
        };
        info.States[RetailUiStateIds.Closed] = new UiStateInfo
        {
            Id = RetailUiStateIds.Closed,
            Name = "Closed",
            PassToChildren = true,
        };
        info.States[RetailUiStateIds.Open] = new UiStateInfo
        {
            Id = RetailUiStateIds.Open,
            Name = "Open",
            PassToChildren = true,
        };
        return Assert.IsType<UiText>(DatWidgetFactory.Create(info, static _ => (0u, 0, 0), null));
    }

    private static ImportedLayout Fake(params (uint id, UiElement e)[] items)
    {
        var dict = new Dictionary<uint, UiElement>();
        var root = new UiPanel();
        foreach (var (id, e) in items)
        {
            root.AddChild(e);
            dict[id] = e;
        }
        foreach ((uint id, float left) in new[]
                 {
                     (CharacterStatController.TabAttribId, 0f),
                     (CharacterStatController.TabSkillsId, 92f),
                     (CharacterStatController.TabTitlesId, 184f),
                 })
        {
            if (dict.ContainsKey(id)) continue;
            UiText tab = MakeTab(id, left);
            root.AddChild(tab);
            dict[id] = tab;
        }
        return new ImportedLayout(root, dict);
    }
}
