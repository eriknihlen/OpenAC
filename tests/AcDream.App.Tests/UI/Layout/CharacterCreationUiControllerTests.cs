using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.CharGen;
using AcDream.Core.Net.Messages;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.App.Tests.UI.Layout;

public sealed class CharacterCreationUiControllerTests
{
    private const uint AluvianId = 1u;
    private const uint OlthoiId = (uint)ChargenHeritageGroup.Olthoi;
    private const uint GenderKey = 1u;
    private const uint SkillTrainOnly = 1u;
    private const uint SkillSpecializable = 2u;

    private const uint SkillFreeTrained = 3u;

    [Fact]
    public void ActiveScreen_KeepsAuthoredRootExtent_AndDefaultsToTheHeritagePage()
    {
        using var environment = new EnvironmentHarness();
        Assert.False(environment.Controller.Root.Visible);

        environment.Controller.Open();

        Assert.True(environment.Controller.Root.Visible);
        Assert.Equal(800f, environment.Controller.Root.Width);
        Assert.Equal(600f, environment.Controller.Root.Height);
        Assert.True(environment.Page(
            CharacterCreationUiController.HeritagePageElementId).Visible);
        Assert.False(environment.Page(
            CharacterCreationUiController.ProfessionPageElementId).Visible);
        Assert.True(environment.TabButton(
            CharacterCreationUiController.HeritageTabElementId).Selected);
    }

    [Fact]
    public void TabClick_SwitchesToTheClickedPage_FreeOfValidation()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();

        environment.TabButton(CharacterCreationUiController.TownTabElementId)
            .OnClick!();

        Assert.True(environment.Page(
            CharacterCreationUiController.TownPageElementId).Visible);
        Assert.False(environment.Page(
            CharacterCreationUiController.HeritagePageElementId).Visible);
        Assert.True(environment.TabButton(
            CharacterCreationUiController.TownTabElementId).Selected);
    }

    [Fact]
    public void Next_AdvancesOnePageAtATime_AndBackReturns()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();

        environment.Button(CharacterCreationUiController.NextElementId).OnClick!();
        Assert.True(environment.Page(
            CharacterCreationUiController.ProfessionPageElementId).Visible);

        environment.Button(CharacterCreationUiController.BackElementId).OnClick!();
        Assert.True(environment.Page(
            CharacterCreationUiController.HeritagePageElementId).Visible);
    }

    [Fact]
    public void Next_AtSummary_IsANoOp()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.TabButton(CharacterCreationUiController.SummaryTabElementId)
            .OnClick!();
        Assert.True(environment.Page(
            CharacterCreationUiController.SummaryPageElementId).Visible);

        environment.Button(CharacterCreationUiController.NextElementId).OnClick!();

        Assert.True(environment.Page(
            CharacterCreationUiController.SummaryPageElementId).Visible);
    }

    [Fact]
    public void Finish_GhostedExceptOnSummary()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        UiButton finish = environment.Button(CharacterCreationUiController.FinishElementId);
        Assert.NotNull(finish.OnClick);
        Assert.False(finish.Enabled);

        environment.TabButton(CharacterCreationUiController.SummaryTabElementId).OnClick!();
        Assert.True(finish.Enabled);
    }

    [Fact]
    public void Random_IsDisabledOnSkillsPageOnly()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        UiButton random = environment.Button(CharacterCreationUiController.RandomElementId);
        Assert.True(random.Enabled);

        environment.TabButton(CharacterCreationUiController.SkillsTabElementId).OnClick!();
        Assert.False(random.Enabled);

        environment.TabButton(CharacterCreationUiController.AppearanceTabElementId).OnClick!();
        Assert.True(random.Enabled);

        environment.TabButton(CharacterCreationUiController.SummaryTabElementId).OnClick!();
        Assert.True(random.Enabled);

        environment.TabButton(CharacterCreationUiController.TownTabElementId).OnClick!();
        Assert.True(random.Enabled);
    }

    [Fact]
    public void Back_AtHeritage_OpensExitConfirmation()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();

        environment.Button(CharacterCreationUiController.BackElementId).OnClick!();

        Assert.True(environment.Dialogs.IsOpen);
    }

    [Fact]
    public void Exit_Confirm_ClosesTheScreenAndCallsRequestExit()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();

        environment.Button(CharacterCreationUiController.ExitElementId).OnClick!();
        Assert.True(environment.Dialogs.IsOpen);

        environment.ConfirmActiveDialog(confirmed: true);

        Assert.False(environment.Controller.Root.Visible);
        Assert.Equal(1, environment.Runtime.RequestExitCalls);
    }

    [Fact]
    public void Exit_Cancel_LeavesTheScreenOpen()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();

        environment.Button(CharacterCreationUiController.ExitElementId).OnClick!();
        environment.ConfirmActiveDialog(confirmed: false);

        Assert.True(environment.Controller.Root.Visible);
        Assert.Equal(0, environment.Runtime.RequestExitCalls);
    }

    [Fact]
    public void HeritageButton_SelectsHeritage_WithNoGenderSideEffect()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();

        environment.Button(0x100003BFu).OnClick!(); // Aluvian

        Assert.Equal(AluvianId, environment.Runtime.LastSelectedHeritage);
        Assert.Equal(0u, environment.Runtime.LastSelectedGender);
    }

    [Fact]
    public void OlthoiHeritage_HidesProfessionSkillsAndTownTabs()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(OlthoiId);

        environment.TabButton(CharacterCreationUiController.HeritageTabElementId)
            .OnClick!();

        Assert.False(environment.TabButton(
            CharacterCreationUiController.ProfessionTabElementId).Visible);
        Assert.False(environment.TabButton(
            CharacterCreationUiController.SkillsTabElementId).Visible);
        Assert.False(environment.TabButton(
            CharacterCreationUiController.TownTabElementId).Visible);

        // Next from Heritage would normally land on Profession; for an
        // Olthoi heritage it must redirect straight to Appearance.
        environment.Button(CharacterCreationUiController.NextElementId).OnClick!();
        Assert.True(environment.Page(
            CharacterCreationUiController.AppearancePageElementId).Visible);
    }

    [Fact]
    public void ProfessionTemplateButton_SelectsTemplate()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.ProfessionTabElementId)
            .OnClick!();

        environment.Button(0x100003DAu).OnClick!(); // Bow Hunter = template index 1

        Assert.Equal(1u, environment.Runtime.LastSelectedTemplate);
    }

    [Fact]
    public void ProfessionSlider_ScalarChange_SetsTheAttribute()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.ProfessionTabElementId)
            .OnClick!();

        UiElement strengthContainer = Assert.IsAssignableFrom<UiElement>(
            environment.Screen.FindElement(0x100003E6u));
        var slider = Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(strengthContainer, 0x100002EEu));

        slider.ScalarChanged!(1f); // top of the [10,100] range

        Assert.Equal(ChargenAttributeId.Strength, environment.Runtime.LastAttributeSet);
        Assert.Equal(100, environment.Runtime.LastAttributeValue);
    }

    [Fact]
    public void ProfessionValueField_DirectEntry_SetsTheAttribute()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.ProfessionTabElementId)
            .OnClick!();

        UiElement strengthContainer = Assert.IsAssignableFrom<UiElement>(
            environment.Screen.FindElement(0x100003E6u));
        var field = Assert.IsType<UiField>(
            UiElement.FindDescendant(strengthContainer, 0x100002EFu));

        field.OnSubmit!("42");

        Assert.Equal(ChargenAttributeId.Strength, environment.Runtime.LastAttributeSet);
        Assert.Equal(42, environment.Runtime.LastAttributeValue);
    }

    [Fact]
    public void SkillsRow_ArrowClick_TrainsThenSpecializes()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.SkillsTabElementId)
            .OnClick!();

        (UiButton up, UiButton down) = environment.SkillRowArrows(SkillSpecializable);

        up.OnClick!();
        Assert.Equal(ChargenSkillAdvancementClass.Trained,
            environment.Runtime.GetSkillLevel(SkillSpecializable));

        up.OnClick!();
        Assert.Equal(ChargenSkillAdvancementClass.Specialized,
            environment.Runtime.GetSkillLevel(SkillSpecializable));

        down.OnClick!();
        Assert.Equal(ChargenSkillAdvancementClass.Trained,
            environment.Runtime.GetSkillLevel(SkillSpecializable));
    }

    [Fact]
    public void SkillsPage_Rows_RenderNameAndLevelCostValues_ThroughTheRealTemplate()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.SkillsTabElementId)
            .OnClick!();

        IReadOnlyList<UiElement> children = environment.SkillsList().ViewportForTest!.Children;
        List<UiElement> skillRows = [.. children.Where(
            candidate => UiElement.FindDescendant(candidate, 0x10000301u) is not null)];
        Assert.Equal(4, children.Count - skillRows.Count); // four bucket headers, always built.
        Assert.Equal(3, skillRows.Count);

        UiElement row = Assert.Single(skillRows, candidate =>
            UiElement.FindDescendant(candidate, 0x10000301u) is UiText name
            && JoinedText(name) == ItemAppraisalTextFormatter.SkillName((int)SkillTrainOnly));

        // FakeRuntime.GetSkillScore's deterministic stand-in: skillId * 10.
        UiText level = Assert.IsType<UiText>(UiElement.FindDescendant(row, 0x10000302u));
        Assert.Equal((SkillTrainOnly * 10u).ToString(), JoinedText(level));

        UiText upCost = Assert.IsType<UiText>(UiElement.FindDescendant(row, 0x10000303u));
        UiText downCost = Assert.IsType<UiText>(UiElement.FindDescendant(row, 0x10000306u));
        Assert.Equal("2", JoinedText(upCost));
        Assert.Equal("0", JoinedText(downCost));

        environment.SkillRowArrows(SkillTrainOnly).Up.OnClick!();
        RuntimeCharacterCreationSnapshot snapshot = environment.Runtime.View.Snapshot;
        environment.Runtime.View.Snapshot = snapshot with { Revision = snapshot.Revision + 1 };
        environment.Controller.Tick();
        row = Assert.Single(environment.SkillsList().ViewportForTest!.Children, candidate =>
            UiElement.FindDescendant(candidate, 0x10000301u) is UiText name
            && JoinedText(name) == ItemAppraisalTextFormatter.SkillName((int)SkillTrainOnly));
        upCost = Assert.IsType<UiText>(UiElement.FindDescendant(row, 0x10000303u));
        downCost = Assert.IsType<UiText>(UiElement.FindDescendant(row, 0x10000306u));
        Assert.Equal("4", JoinedText(upCost));
        Assert.Equal("2", JoinedText(downCost));
    }


    [Fact]
    public void SkillsPage_RowClick_SelectsRow_HighlightsNameAndPopulatesInfoBoxTitle()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.SkillsTabElementId).OnClick!();

        (UiDatElement row, UiText nameText) = environment.SkillRow(SkillTrainOnly);
        Vector4 unselectedColor = nameText.DefaultColor;

        // Nothing selected yet.
        Assert.Equal(string.Empty, JoinedText(environment.SkillInfoTitle()));

        row.OnClick!();

        Assert.Equal(Vector4.One, nameText.DefaultColor);
        Assert.NotEqual(unselectedColor, nameText.DefaultColor);

        // FakeRuntime.GetSkillScore's deterministic stand-in: skillId * 10.
        string expectedTitle =
            $"{ItemAppraisalTextFormatter.SkillName((int)SkillTrainOnly)} ({SkillTrainOnly * 10u})";
        Assert.Equal(expectedTitle, JoinedText(environment.SkillInfoTitle()));
        Assert.Equal(
            "A test skill description. Formula : (2 x Strength) / 4 +2",
            JoinedText(environment.SkillInfoText()));
    }

    [Fact]
    public void SkillsPage_InfoBoxPanes_InheritRetailTopDefault_ToAvoidTitleDescriptionOverlap()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.SkillsTabElementId).OnClick!();

        Assert.Equal(VJustify.Top, environment.SkillInfoTitle().VerticalJustify);
        Assert.Equal(VJustify.Top, environment.SkillInfoText().VerticalJustify);
    }

    [Fact]
    public void SkillsPage_InfoBoxDescriptionPane_HeightClampedToFrameBottom()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.SkillsTabElementId).OnClick!();

        Assert.Equal(50f, environment.SkillInfoText().Height, 3f);
        Assert.Equal(60f, environment.SkillInfoTitle().Height, 3f);
    }

    [Fact]
    public void SkillsPage_ArrowClick_AlsoSelectsRow_InfoBoxShowsLevelBonusLine()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.SkillsTabElementId).OnClick!();

        (UiButton up, _) = environment.SkillRowArrows(SkillTrainOnly);

        up.OnClick!(); // Untrained/Inactive -> Trained.
        BumpRevisionAndTick(environment);
        Assert.Equal(
            "A test skill description. Training Bonus  +5 Formula : (2 x Strength) / 4 +2",
            JoinedText(environment.SkillInfoText()));

        (up, _) = environment.SkillRowArrows(SkillTrainOnly);
        up.OnClick!(); // Trained -> Specialized.
        BumpRevisionAndTick(environment);
        Assert.Equal(
            "A test skill description. Specialization Bonus  +10 Formula : (2 x Strength) / 4 +2",
            JoinedText(environment.SkillInfoText()));
    }

    [Fact]
    public void SkillsPage_RowClick_DeselectsPreviousRow_RestoresItsOwnColor()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.SkillsTabElementId).OnClick!();

        (UiDatElement firstRow, UiText firstName) = environment.SkillRow(SkillTrainOnly);
        Vector4 firstUnselected = firstName.DefaultColor;
        (UiDatElement secondRow, UiText secondName) = environment.SkillRow(SkillSpecializable);

        firstRow.OnClick!();
        Assert.Equal(Vector4.One, firstName.DefaultColor);

        secondRow.OnClick!();
        Assert.Equal(Vector4.One, secondName.DefaultColor);
        Assert.Equal(firstUnselected, firstName.DefaultColor);
    }

    [Fact]
    public void SkillsPage_SpecializedCostText_UpCostIsLiteralZero_DownCostUnconditional()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.SkillsTabElementId).OnClick!();

        environment.Runtime.View.SetSkillLevel(SkillSpecializable, ChargenSkillAdvancementClass.Specialized);
        BumpRevisionAndTick(environment);

        (UiElement row, _) = environment.SkillRow(SkillSpecializable);
        UiText upCost = Assert.IsType<UiText>(UiElement.FindDescendant(row, 0x10000303u));
        UiText downCost = Assert.IsType<UiText>(UiElement.FindDescendant(row, 0x10000306u));

        Assert.Equal("0", JoinedText(upCost));
        Assert.Equal("4", JoinedText(downCost)); // specCost(6) - trainCost(2).
    }

    [Fact]
    public void SkillsPage_ArrowStates_GatedOnCreditsAndFreeSkillLocksDownArrow()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.SkillsTabElementId).OnClick!();

        (UiButton up, UiButton down) = environment.SkillRowArrows(SkillTrainOnly);
        Assert.Equal(0x1000001Bu, up.ActiveRetailStateId);
        Assert.Equal(0x1000001Au, down.ActiveRetailStateId);

        up.OnClick!(); // -> Trained. trainedCost(2) != 0 -> Down enabled.
        BumpRevisionAndTick(environment);
        (up, down) = environment.SkillRowArrows(SkillTrainOnly);
        Assert.Equal(0x1000001Bu, down.ActiveRetailStateId);

        (UiButton freeUp, UiButton freeDown) = environment.SkillRowArrows(SkillFreeTrained);
        freeUp.OnClick!(); // -> Trained. trainedCost(0) == 0 -> Down locked.
        BumpRevisionAndTick(environment);
        (freeUp, freeDown) = environment.SkillRowArrows(SkillFreeTrained);
        Assert.Equal(0x1000001Au, freeDown.ActiveRetailStateId);

        freeUp.OnClick!(); // -> Specialized. specCost(6) != 0 -> Down unlocks;
        BumpRevisionAndTick(environment); // Up is now ALWAYS ghosted.
        (freeUp, freeDown) = environment.SkillRowArrows(SkillFreeTrained);
        Assert.Equal(0x1000001Au, freeUp.ActiveRetailStateId);
        Assert.Equal(0x1000001Bu, freeDown.ActiveRetailStateId);
    }

    [Fact]
    public void SkillsPage_ListboxScrollbar_IsLinkedToTheListsOwnScroll()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.TabButton(CharacterCreationUiController.SkillsTabElementId).OnClick!();

        UiScrollbar scrollbar = environment.SkillsScrollbar();
        Assert.Same(environment.SkillsList().Scroll, scrollbar.Model);
    }


    /// <summary><c>DoSkillRecords</c>'s own unconditional 4-header build —
    /// every bucket header is present, in Specialized/Trained/
    /// UseableUntrained/UnuseableUntrained order, even though this
    /// fixture's three skills leave the Specialized bucket empty.</summary>
    [Fact]
    public void SkillsPage_BucketHeaders_AlwaysBuildAllFour_InRetailOrder()
    {
        using var environment = new EnvironmentHarness();
        environment.Runtime.ResolvedStrings["ID_CharGen_Specialized"] = "Specialized";
        environment.Runtime.ResolvedStrings["ID_CharGen_Trained"] = "Trained";
        environment.Runtime.ResolvedStrings["ID_CharGen_UseableUntrained"] = "Useable Untrained";
        environment.Runtime.ResolvedStrings["ID_CharGen_UnuseableUntrained"] = "Unuseable Untrained";
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.SkillsTabElementId).OnClick!();

        List<string> headerCaptions = [.. environment.SkillsList().ViewportForTest!.Children
            .Where(candidate => UiElement.FindDescendant(candidate, 0x10000301u) is null)
            .Select(candidate => Assert.IsType<UiButton>(
                UiElement.FindDescendant(candidate, 0x100002F6u)).Label!)];

        Assert.Equal(
            ["Specialized", "Trained", "Useable Untrained", "Unuseable Untrained"],
            headerCaptions);
    }

    /// <summary><c>UpdateSkillEntry</c>'s own <c>iMinlevel &lt;= 1</c> split:
    /// while Untrained, SkillTrainOnly (fixture MinLevel 1) is useable and
    /// SkillSpecializable (fixture MinLevel 2) is not — they land in
    /// DIFFERENT buckets even though both start Untrained (the default,
    /// unset, <c>FakeView.GetSkillLevel</c> state).</summary>
    [Fact]
    public void SkillsPage_UntrainedSkill_BucketsByMinLevel()
    {
        using var environment = new EnvironmentHarness();
        environment.Runtime.ResolvedStrings["ID_CharGen_UseableUntrained"] = "Useable Untrained";
        environment.Runtime.ResolvedStrings["ID_CharGen_UnuseableUntrained"] = "Unuseable Untrained";
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.SkillsTabElementId).OnClick!();

        List<UiElement> children = [.. environment.SkillsList().ViewportForTest!.Children];
        int useableHeaderIndex = children.FindIndex(c =>
            UiElement.FindDescendant(c, 0x100002F6u) is UiButton b && b.Label == "Useable Untrained");
        int unuseableHeaderIndex = children.FindIndex(c =>
            UiElement.FindDescendant(c, 0x100002F6u) is UiButton b && b.Label == "Unuseable Untrained");
        int trainOnlyRowIndex = children.FindIndex(c =>
            UiElement.FindDescendant(c, 0x10000301u) is UiText n
            && JoinedText(n) == ItemAppraisalTextFormatter.SkillName((int)SkillTrainOnly));
        int specializableRowIndex = children.FindIndex(c =>
            UiElement.FindDescendant(c, 0x10000301u) is UiText n
            && JoinedText(n) == ItemAppraisalTextFormatter.SkillName((int)SkillSpecializable));

        Assert.InRange(trainOnlyRowIndex, useableHeaderIndex + 1, unuseableHeaderIndex - 1);
        Assert.True(specializableRowIndex > unuseableHeaderIndex);
    }

    [Fact]
    public void SkillsPage_AdvancingASkill_MovesItsRowIntoTheNewBucket()
    {
        using var environment = new EnvironmentHarness();
        environment.Runtime.ResolvedStrings["ID_CharGen_Trained"] = "Trained";
        environment.Runtime.ResolvedStrings["ID_CharGen_UseableUntrained"] = "Useable Untrained";
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.SkillsTabElementId).OnClick!();

        environment.SkillRowArrows(SkillTrainOnly).Up.OnClick!(); // Untrained -> Trained.
        BumpRevisionAndTick(environment);

        List<UiElement> children = [.. environment.SkillsList().ViewportForTest!.Children];
        int trainedHeaderIndex = children.FindIndex(c =>
            UiElement.FindDescendant(c, 0x100002F6u) is UiButton b && b.Label == "Trained");
        int useableHeaderIndex = children.FindIndex(c =>
            UiElement.FindDescendant(c, 0x100002F6u) is UiButton b && b.Label == "Useable Untrained");
        int rowIndex = children.FindIndex(c =>
            UiElement.FindDescendant(c, 0x10000301u) is UiText n
            && JoinedText(n) == ItemAppraisalTextFormatter.SkillName((int)SkillTrainOnly));

        Assert.InRange(rowIndex, trainedHeaderIndex + 1, useableHeaderIndex - 1);
    }

    [Fact]
    public void TownButton_SelectsTheLiteralStartAreaIndex()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.TabButton(CharacterCreationUiController.TownTabElementId)
            .OnClick!();

        environment.Button(0x1000040Du).OnClick!();
        Assert.Equal(0, environment.Runtime.LastSelectedStartArea);

        environment.Button(0x1000040Eu).OnClick!();
        Assert.Equal(2, environment.Runtime.LastSelectedStartArea);
    }

    [Fact]
    public void TownButton_Refresh_SetsThePagesOwnRetailStateLiteral()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.TabButton(CharacterCreationUiController.TownTabElementId)
            .OnClick!();

        var pageRoot = Assert.IsType<UiDatElement>(
            environment.Page(CharacterCreationUiController.TownPageElementId));

        environment.Button(0x1000040Du).OnClick!(); // Holtburg -> startArea 0
        BumpRevisionAndTick(environment);
        Assert.Equal("Holtburg", pageRoot.ActiveState);

        environment.Button(0x1000040Eu).OnClick!(); // Yaraq -> startArea 2
        BumpRevisionAndTick(environment);
        Assert.Equal("Yaraq", pageRoot.ActiveState);
    }

    [Fact]
    public void ProfessionSlider_Refresh_DisplaysScalarAsValueOverOneHundred()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.ProfessionTabElementId)
            .OnClick!();

        UiElement strengthContainer = Assert.IsAssignableFrom<UiElement>(
            environment.Screen.FindElement(0x100003E6u));
        var slider = Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(strengthContainer, 0x100002EEu));

        RuntimeCharacterCreationSnapshot snapshot = environment.Runtime.View.Snapshot;
        environment.Runtime.View.Snapshot = snapshot with
        {
            Revision = snapshot.Revision + 1,
            Attributes = snapshot.Attributes with { Strength = 55 },
        };
        environment.Controller.Tick();

        Assert.Equal(0.55f, slider.ScalarPosition);
    }

    [Theory]
    [InlineData(0.5f, 50)]
    [InlineData(0f, 10)]
    public void ProfessionSlider_ScalarChange_TruncatesAndClampsLowOnly(
        float scalar,
        int expectedValue)
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.ProfessionTabElementId)
            .OnClick!();

        UiElement strengthContainer = Assert.IsAssignableFrom<UiElement>(
            environment.Screen.FindElement(0x100003E6u));
        var slider = Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(strengthContainer, 0x100002EEu));

        slider.ScalarChanged!(scalar);

        Assert.Equal(ChargenAttributeId.Strength, environment.Runtime.LastAttributeSet);
        Assert.Equal(expectedValue, environment.Runtime.LastAttributeValue);
    }

    [Fact]
    public void HeritageButtonClick_RestoresHiddenTabsAtClickTime_ExceptLugian()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();

        environment.Button(0x100005C7u).OnClick!(); // Olthoi -> HIDE
        Assert.False(environment.TabButton(
            CharacterCreationUiController.ProfessionTabElementId).Visible);
        Assert.False(environment.TabButton(
            CharacterCreationUiController.SkillsTabElementId).Visible);
        Assert.False(environment.TabButton(
            CharacterCreationUiController.TownTabElementId).Visible);

        environment.Button(0x100005F1u).OnClick!(); // Lugian -> no-op quirk
        Assert.False(environment.TabButton(
            CharacterCreationUiController.ProfessionTabElementId).Visible);
        Assert.False(environment.TabButton(
            CharacterCreationUiController.SkillsTabElementId).Visible);
        Assert.False(environment.TabButton(
            CharacterCreationUiController.TownTabElementId).Visible);

        environment.Button(0x100003BFu).OnClick!(); // Aluvian -> SHOW
        Assert.True(environment.TabButton(
            CharacterCreationUiController.ProfessionTabElementId).Visible);
        Assert.True(environment.TabButton(
            CharacterCreationUiController.SkillsTabElementId).Visible);
        Assert.True(environment.TabButton(
            CharacterCreationUiController.TownTabElementId).Visible);
    }

    [Fact]
    public void Open_SetsFixedCanvas_ExitConfirmClosesAndNullsIt()
    {
        using var environment = new EnvironmentHarness();
        Assert.Null(environment.Host.FixedCanvasSize);

        environment.Controller.Open();
        Assert.Equal(new Vector2(800f, 600f), environment.Host.FixedCanvasSize);

        environment.Button(CharacterCreationUiController.ExitElementId).OnClick!();
        environment.ConfirmActiveDialog(confirmed: true);

        Assert.Null(environment.Host.FixedCanvasSize);
    }

    [Fact]
    public void Deactivate_NullsFixedCanvas_AndClosesTheScreen()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        Assert.NotNull(environment.Host.FixedCanvasSize);

        environment.Runtime.ProvideView = false;
        environment.Controller.Tick();

        Assert.False(environment.Controller.Root.Visible);
        Assert.Null(environment.Host.FixedCanvasSize);
    }

    [Fact]
    public void Dispose_NullsFixedCanvas()
    {
        var environment = new EnvironmentHarness();
        environment.Controller.Open();
        Assert.NotNull(environment.Host.FixedCanvasSize);

        environment.Dispose();

        Assert.Null(environment.Host.FixedCanvasSize);
    }


    [Fact]
    public void AppearanceGenderButton_SelectsGender()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.AppearanceTabElementId).OnClick!();

        environment.Button(CharacterCreationAppearancePage.MaleButtonId).OnClick!();
        Assert.Equal(1u, environment.Runtime.LastSelectedGender);

        environment.Button(CharacterCreationAppearancePage.FemaleButtonId).OnClick!();
        Assert.Equal(2u, environment.Runtime.LastSelectedGender);
    }

    [Fact]
    public void AppearanceSpin_IncrementZoneClick_CyclesStyleForwardFromUnset()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        SelectAluvianMale(environment);

        environment.Button(CharacterCreationAppearancePage.HairSpinId).OnClickAt!(150, 10);

        Assert.Equal(ChargenAppearanceSlot.HairStyle, environment.Runtime.LastAppearanceSlot);
        Assert.Equal(0u, environment.Runtime.LastAppearanceIndex);
    }

    [Fact]
    public void AppearanceSpin_DecrementZoneClick_FromUnset_WrapsToLastStyle()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        SelectAluvianMale(environment);

        environment.Button(CharacterCreationAppearancePage.HairSpinId).OnClickAt!(100, 10);

        Assert.Equal(ChargenAppearanceSlot.HairStyle, environment.Runtime.LastAppearanceSlot);
        Assert.Equal(2u, environment.Runtime.LastAppearanceIndex);
    }

    [Fact]
    public void AppearanceSpin_SelectZoneClick_SelectsPartWithoutChangingIndex()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        SelectAluvianMale(environment);

        // Establish a real Hair index first (increment zone, x=150).
        environment.Button(CharacterCreationAppearancePage.HairSpinId).OnClickAt!(150, 10);
        Assert.Equal(0u, environment.Runtime.LastAppearanceIndex);
        int callsAfterCycle = environment.Runtime.AppearanceIndexCallCount;

        // x=180 is inside [174,200) — past the increment arrow's own zone,
        // still inside the 200px-wide spin — the spin's own BODY, not
        // either arrow.
        environment.Button(CharacterCreationAppearancePage.HairSpinId).OnClickAt!(180, 10);

        Assert.Equal(callsAfterCycle, environment.Runtime.AppearanceIndexCallCount);
        Assert.Equal(ChargenAppearanceSlot.HairStyle, environment.Runtime.LastAppearanceSlot);
        Assert.Equal(0u, environment.Runtime.LastAppearanceIndex);
    }

    [Fact]
    public void AppearanceSpin_SelectZoneClick_FromUnset_NormalizesToLastStyle()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        SelectAluvianMale(environment);

        environment.Button(CharacterCreationAppearancePage.HairSpinId).OnClickAt!(180, 10);

        Assert.Equal(ChargenAppearanceSlot.HairStyle, environment.Runtime.LastAppearanceSlot);
        Assert.Equal(2u, environment.Runtime.LastAppearanceIndex);
    }

    [Fact]
    public void AppearanceHeadgearSpin_RingIncludesTheUnsetPosition()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        SelectAluvianMale(environment);
        environment.TabButton(CharacterCreationUiController.AppearanceTabElementId).OnClick!();
        environment.Button(CharacterCreationAppearancePage.ClothesButtonId).OnClick!();

        environment.Button(CharacterCreationAppearancePage.HeadgearSpinId).OnClickAt!(150, 10); // increment
        Assert.Equal(ChargenAppearanceSlot.HeadgearStyle, environment.Runtime.LastAppearanceSlot);
        Assert.Equal(0u, environment.Runtime.LastAppearanceIndex);

        environment.Button(CharacterCreationAppearancePage.HeadgearSpinId).OnClickAt!(100, 10); // decrement
        Assert.Equal(RuntimeCharacterCreationAppearance.Unset, environment.Runtime.LastAppearanceIndex);
    }

    [Theory]
    [InlineData(0u, +1, 3, false, 1u)]
    [InlineData(2u, +1, 3, false, 0u)] // plain wrap forward past the end.
    [InlineData(1u, -1, 3, false, 0u)]
    [InlineData(0u, -1, 3, false, 2u)] // plain wrap backward past the start.
    [InlineData(RuntimeCharacterCreationAppearance.Unset, +1, 3, false, 0u)]
    [InlineData(RuntimeCharacterCreationAppearance.Unset, -1, 3, false, 2u)]
    [InlineData(0u, -1, 3, true, RuntimeCharacterCreationAppearance.Unset)] // headgear ring: 0 -> Unset.
    [InlineData(RuntimeCharacterCreationAppearance.Unset, +1, 3, true, 0u)] // headgear ring: Unset -> 0.
    [InlineData(2u, +1, 3, true, RuntimeCharacterCreationAppearance.Unset)] // headgear ring: last -> Unset.
    [InlineData(RuntimeCharacterCreationAppearance.Unset, -1, 3, true, 2u)] // headgear ring: Unset -> last.
    public void CycleIndex_MatchesTheReferenceWrap(
        uint current, int delta, int count, bool allowUnset, uint expected)
    {
        Assert.Equal(
            expected,
            CharacterCreationAppearancePage.CycleIndex(current, delta, count, allowUnset));
    }

    [Fact]
    public void AppearanceSkinSpin_Click_NeverCallsSetAppearanceIndex()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        SelectAluvianMale(environment);

        environment.Button(CharacterCreationAppearancePage.SkinSpinId).OnClickAt!(100, 10);
        environment.Button(CharacterCreationAppearancePage.SkinSpinId).OnClickAt!(150, 10);

        Assert.Equal(0, environment.Runtime.AppearanceIndexCallCount);
    }

    [Fact]
    public void OlthoiHeritage_HidesClothesButtonAndNoseMouthSpins()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(OlthoiId);
        environment.TabButton(CharacterCreationUiController.AppearanceTabElementId).OnClick!();

        Assert.False(environment.Button(CharacterCreationAppearancePage.ClothesButtonId).Visible);
        Assert.False(environment.Button(CharacterCreationAppearancePage.NoseSpinId).Visible);
        Assert.False(environment.Button(CharacterCreationAppearancePage.MouthSpinId).Visible);
    }

    [Fact]
    public void AppearanceSwatch_WithinColorCount_SetsColorForTheCurrentPart()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        SelectAluvianMale(environment);

        environment.Button(CharacterCreationAppearancePage.SwatchIds[1]).OnClick!();

        Assert.Equal(ChargenAppearanceSlot.HairColor, environment.Runtime.LastAppearanceSlot);
        Assert.Equal(1u, environment.Runtime.LastAppearanceIndex);
    }

    [Fact]
    public void AppearanceSwatch_BeyondColorCount_IsANoOp()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        SelectAluvianMale(environment);

        environment.Button(CharacterCreationAppearancePage.SwatchIds[^1]).OnClick!();

        Assert.Equal(0, environment.Runtime.AppearanceIndexCallCount);
    }

    [Fact]
    public void AppearanceSwatchOverlays_ExactlyOneVisible_TrackingTheCurrentPartsColorIndex()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        SelectAluvianMale(environment);

        UiElement[] overlays = [.. CharacterCreationAppearancePage.SwatchOverlayIds
            .Select(environment.Page)];

        Assert.All(overlays, overlay => Assert.False(overlay.Visible));

        RuntimeCharacterCreationSnapshot snapshot = environment.Runtime.View.Snapshot;
        environment.Runtime.View.Snapshot = snapshot with
        {
            Revision = snapshot.Revision + 1,
            Appearance = snapshot.Appearance with { HairColor = 1u },
        };
        environment.Controller.Tick();

        for (int i = 0; i < overlays.Length; i++)
            Assert.Equal(i == 1, overlays[i].Visible);

        snapshot = environment.Runtime.View.Snapshot;
        environment.Runtime.View.Snapshot = snapshot with
        {
            Revision = snapshot.Revision + 1,
            Appearance = snapshot.Appearance with { HairColor = 2u },
        };
        environment.Controller.Tick();

        for (int i = 0; i < overlays.Length; i++)
            Assert.Equal(i == 2, overlays[i].Visible);
    }

    [Fact]
    public void AppearanceShadeScroll_ScalarChanged_SetsShadeForTheCurrentPart()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        SelectAluvianMale(environment);

        environment.ShadeScroll().ScalarChanged!(0.75f);

        Assert.Equal(ChargenShadeSlot.Hair, environment.Runtime.LastShadeSlot);
        Assert.Equal(0.75, environment.Runtime.LastShadeValue, 3);
    }

    [Fact]
    public void AppearanceShadeScroll_ForNoseOrMouth_RoutesToSkinShade()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        SelectAluvianMale(environment);
        environment.Button(CharacterCreationAppearancePage.NoseSpinId).OnClickAt!(10, 10);

        environment.ShadeScroll().ScalarChanged!(0.5f);

        Assert.Equal(ChargenShadeSlot.Skin, environment.Runtime.LastShadeSlot);
    }

    [Fact]
    public void AppearanceZoomAndRotateButtons_DelegateToThePreviewControl()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        var preview = new FakeChargenPreviewControl();
        environment.Controller.AppearancePreviewControl = preview;

        environment.Button(CharacterCreationAppearancePage.ZoomInId).OnClick!();
        environment.Button(CharacterCreationAppearancePage.ZoomOutId).OnClick!();
        environment.Button(CharacterCreationAppearancePage.RotateClockwiseId).OnClick!();
        environment.Button(CharacterCreationAppearancePage.RotateCounterClockwiseId).OnClick!();

        Assert.Equal(1, preview.ZoomInCalls);
        Assert.Equal(1, preview.ZoomOutCalls);
        Assert.Equal(1, preview.RotateClockwiseCalls);
        Assert.Equal(1, preview.RotateCounterClockwiseCalls);
    }

    [Fact]
    public void AppearanceZoomButtons_WithNoPreviewControlAssignedYet_AreHarmlessNoOps()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();

        environment.Button(CharacterCreationAppearancePage.ZoomInId).OnClick!();
        environment.Button(CharacterCreationAppearancePage.RotateClockwiseId).OnClick!();
    }

    [Fact]
    public void AppearancePage_HelpText_TopOriented_AndOwnScrollbarIsWired()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();

        UiText helpText = Assert.IsType<UiText>(
            environment.Screen.FindElement(CharacterCreationAppearancePage.HelpTextId));
        Assert.False(helpText.PreserveEndOnLayout);

        UiScrollbar helpScroll = Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(helpText, 0x100002E7u));
        Assert.Same(helpText.Scroll, helpScroll.Model);
    }

    [Fact]
    public void AppearanceZoomButtons_ClickPath_TogglesMutualExclusiveHighlightPair()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        var preview = new FakeChargenPreviewControl();
        environment.Controller.AppearancePreviewControl = preview;

        UiButton zoomIn = environment.Button(CharacterCreationAppearancePage.ZoomInId);
        UiButton zoomOut = environment.Button(CharacterCreationAppearancePage.ZoomOutId);

        Assert.Equal("Normal", zoomIn.ActiveState);
        Assert.Equal("Normal", zoomOut.ActiveState);

        zoomIn.OnClick!();
        Assert.Equal("Highlight", zoomIn.ActiveState);
        Assert.Equal("Normal", zoomOut.ActiveState);

        zoomOut.OnClick!();
        Assert.Equal("Normal", zoomIn.ActiveState);
        Assert.Equal("Highlight", zoomOut.ActiveState);

        zoomOut.OnClick!();
        Assert.Equal("Normal", zoomIn.ActiveState);
        Assert.Equal("Highlight", zoomOut.ActiveState);
    }

    private static void SelectAluvianMale(EnvironmentHarness environment)
    {
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.AppearanceTabElementId).OnClick!();
        environment.Button(CharacterCreationAppearancePage.MaleButtonId).OnClick!();
    }

    // ── CC5: RandomizeCharacter open-roll + gender flip ─────────────────

    [Fact]
    public void Open_RollsACharacterThenFlipsTheGenderToTheOpposite()
    {
        using var environment = new EnvironmentHarness();
        environment.Runtime.RandomizedHeritageId = AluvianId;
        environment.Runtime.RandomizedGenderKey = GenderKey; // Male = 1

        environment.Controller.Open();

        Assert.Equal(1, environment.Runtime.RandomizeCharacterCalls);
        Assert.Equal(AluvianId, environment.Runtime.View.Snapshot.HeritageId);
        // RandomizeCharacter rolled Male (1); InitializePage's own flip
        // immediately inverts it to Female (2).
        Assert.Equal(2u, environment.Runtime.LastSelectedGender);
        Assert.Equal(2u, environment.Runtime.View.Snapshot.GenderKey);
    }

    [Fact]
    public void Open_RandomizeCharacterRejected_DoesNotAttemptTheGenderFlip()
    {
        using var environment = new EnvironmentHarness();
        environment.Runtime.RandomizeCharacterAccepts = false;

        environment.Controller.Open();

        Assert.Equal(0u, environment.Runtime.LastSelectedGender);
    }


    private static void GoToSummary(EnvironmentHarness environment) =>
        environment.TabButton(CharacterCreationUiController.SummaryTabElementId).OnClick!();

    [Fact]
    public void Finish_EmptyName_ShowsNoNameWarningDialog_AndDoesNotSend()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        GoToSummary(environment);

        environment.Button(CharacterCreationUiController.FinishElementId).OnClick!();

        Assert.Equal(1, environment.Runtime.FinishCallCount);
        Assert.True(environment.Dialogs.IsOpen);
        Assert.Equal("No name entered.", environment.LastDialogMessage());
    }

    [Fact]
    public void Finish_EmptyName_RealEventPath_DialogSurvivesTheNextFrameTick_AndFieldRefocusableAfterDismiss()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        SelectAluvianMale(environment);
        GoToSummary(environment);

        UiField nameField = environment.SummaryNameField();
        UiButton finishButton = environment.Button(CharacterCreationUiController.FinishElementId);

        // (1) A real mouse-down at the field's own screen rect sets
        // KeyboardFocus to it.
        Vector2 fieldPos = nameField.ScreenPosition;
        environment.Host.OnMouseDown(UiMouseButton.Left, (int)fieldPos.X + 2, (int)fieldPos.Y + 2);
        Assert.Same(nameField, environment.Host.KeyboardFocus);

        environment.Host.OnChar('Z');
        Assert.Equal("Z", nameField.Text);
        nameField.Backspace();
        Assert.Equal(string.Empty, nameField.Text);

        Vector2 finishPos = finishButton.ScreenPosition;
        environment.Host.OnMouseDown(UiMouseButton.Left, (int)finishPos.X + 2, (int)finishPos.Y + 2);
        environment.Host.OnMouseUp(UiMouseButton.Left, (int)finishPos.X + 2, (int)finishPos.Y + 2);

        Assert.Equal(1, environment.Runtime.FinishCallCount);
        Assert.True(environment.Dialogs.IsOpen);
        UiPanel dialogModal = Assert.IsAssignableFrom<UiPanel>(environment.Host.Modal);

        environment.Controller.Tick();
        environment.Dialogs.Tick();
        Assert.True(dialogModal.ZOrder >= environment.Controller.Root.ZOrder);

        UiButton okButton = Assert.IsType<UiButton>(
            UiElement.FindDescendant(dialogModal, RetailMessageDialogView.OkButtonId));
        Vector2 okPos = okButton.ScreenPosition;
        environment.Host.OnMouseDown(UiMouseButton.Left, (int)okPos.X + 2, (int)okPos.Y + 2);
        environment.Host.OnMouseUp(UiMouseButton.Left, (int)okPos.X + 2, (int)okPos.Y + 2);
        Assert.False(environment.Dialogs.IsOpen);
        Assert.Null(environment.Host.Modal);

        environment.Host.OnMouseDown(UiMouseButton.Left, (int)fieldPos.X + 2, (int)fieldPos.Y + 2);
        Assert.Same(nameField, environment.Host.KeyboardFocus);
        environment.Host.OnChar('Q');
        Assert.Contains('Q', nameField.Text);
    }

    [Fact]
    public void Finish_UnspentCredits_ShowsCreditWarning_ConfirmResendsWithConfirmedFlag()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        SelectAluvianMale(environment);
        environment.Runtime.View.Snapshot = environment.Runtime.View.Snapshot with
        {
            Name = "Adventurer",
            RemainingAttributeCredits = 6,
        };
        GoToSummary(environment);

        environment.Button(CharacterCreationUiController.FinishElementId).OnClick!();

        Assert.Equal(1, environment.Runtime.FinishCallCount);
        Assert.False(environment.Runtime.LastConfirmedUnspentCredits);
        Assert.True(environment.Dialogs.IsOpen);
        Assert.Equal("You have unspent attribute credits.", environment.LastDialogMessage());

        environment.ConfirmActiveDialog(confirmed: true);

        Assert.Equal(2, environment.Runtime.FinishCallCount);
        Assert.True(environment.Runtime.LastConfirmedUnspentCredits);
    }

    [Fact]
    public void Finish_UnspentCredits_CancelDialog_DoesNotResend()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        SelectAluvianMale(environment);
        environment.Runtime.View.Snapshot = environment.Runtime.View.Snapshot with
        {
            Name = "Adventurer",
            RemainingAttributeCredits = 6,
        };
        GoToSummary(environment);
        environment.Button(CharacterCreationUiController.FinishElementId).OnClick!();

        environment.ConfirmActiveDialog(confirmed: false);

        Assert.Equal(1, environment.Runtime.FinishCallCount);
    }

    [Fact]
    public void Finish_Accepted_ShowsNoDialog()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        SelectAluvianMale(environment);
        environment.Runtime.View.Snapshot = environment.Runtime.View.Snapshot with
        {
            Name = "Adventurer",
            RemainingAttributeCredits = 0,
        };
        GoToSummary(environment);

        environment.Button(CharacterCreationUiController.FinishElementId).OnClick!();

        Assert.Equal(1, environment.Runtime.FinishCallCount);
        Assert.False(environment.Dialogs.IsOpen);
    }

    [Fact]
    public void Finish_OffSummaryPage_IsANoOp()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open(); // defaults to Heritage

        environment.Button(CharacterCreationUiController.FinishElementId).OnClick!();

        Assert.Equal(0, environment.Runtime.FinishCallCount);
    }


    [Fact]
    public void RandomOnSummary_ShowsWarningDialog_ConfirmCallsRandomizeCharacter()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        GoToSummary(environment);

        environment.Button(CharacterCreationUiController.RandomElementId).OnClick!();

        Assert.True(environment.Dialogs.IsOpen);
        Assert.Equal("This will randomize your character.", environment.LastDialogMessage());
        int callsBeforeConfirm = environment.Runtime.RandomizeCharacterCalls;

        environment.ConfirmActiveDialog(confirmed: true);

        Assert.Equal(callsBeforeConfirm + 1, environment.Runtime.RandomizeCharacterCalls);
    }

    [Fact]
    public void RandomOnSummary_CancelDialog_DoesNotRandomize()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        GoToSummary(environment);
        int callsBeforeClick = environment.Runtime.RandomizeCharacterCalls;

        environment.Button(CharacterCreationUiController.RandomElementId).OnClick!();
        environment.ConfirmActiveDialog(confirmed: false);

        Assert.Equal(callsBeforeClick, environment.Runtime.RandomizeCharacterCalls);
    }


    [Fact]
    public void RandomOnAppearance_FaceSubTab_CallsRandomizeAppearance()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.TabButton(CharacterCreationUiController.AppearanceTabElementId).OnClick!();

        environment.Button(CharacterCreationUiController.RandomElementId).OnClick!();

        Assert.Equal(1, environment.Runtime.RandomizeAppearanceCalls);
        Assert.Equal(0, environment.Runtime.RandomizeClothingCalls);
    }

    [Fact]
    public void RandomOnAppearance_ClothesSubTab_CallsRandomizeClothing()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.TabButton(CharacterCreationUiController.AppearanceTabElementId).OnClick!();
        environment.Button(CharacterCreationAppearancePage.ClothesButtonId).OnClick!();

        environment.Button(CharacterCreationUiController.RandomElementId).OnClick!();

        Assert.Equal(1, environment.Runtime.RandomizeClothingCalls);
        Assert.Equal(0, environment.Runtime.RandomizeAppearanceCalls);
    }


    [Fact]
    public void SummaryNameField_Submit_CommitsTheName()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        GoToSummary(environment);
        UiField field = environment.SummaryNameField();

        field.SetText("Adventurer");
        field.Submit();

        Assert.Equal("Adventurer", environment.Runtime.LastSetName);
    }

    [Fact]
    public void SummaryNameField_TooLong_ShowsDialogAndRevertsToLastCommitted()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        GoToSummary(environment);
        UiField field = environment.SummaryNameField();
        field.SetText("Adventurer");
        field.Submit();
        Assert.Equal("Adventurer", environment.Runtime.LastSetName);

        field.SetText(new string('a', 40));
        field.Submit();

        Assert.Equal("Adventurer", environment.Runtime.LastSetName);
        Assert.True(environment.Dialogs.IsOpen);
        Assert.Equal("That name is too long.", environment.LastDialogMessage());
        Assert.Equal("Adventurer", field.Text);
    }

    [Fact]
    public void SummaryNameField_NameInputFilter_RejectsDigitsAndSymbols()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        GoToSummary(environment);
        UiField field = environment.SummaryNameField();

        Assert.NotNull(field.CharacterFilter);
        Assert.True(field.CharacterFilter!('A'));
        Assert.True(field.CharacterFilter!(' '));
        Assert.True(field.CharacterFilter!('\''));
        Assert.True(field.CharacterFilter!('-'));
        Assert.False(field.CharacterFilter!('7'));
        Assert.False(field.CharacterFilter!('$'));
    }

    [Fact]
    public void SummaryNameField_RealCommitAfterExternalRefreshWhileUnfocused_StillReachesSetName()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        GoToSummary(environment);
        UiField field = environment.SummaryNameField();

        RuntimeCharacterCreationSnapshot snapshot = environment.Runtime.View.Snapshot;
        environment.Runtime.View.Snapshot = snapshot with
        {
            Revision = snapshot.Revision + 1,
            Name = "Zorak",
        };
        environment.Controller.Tick();
        Assert.Equal("Zorak", field.Text);

        field.SetText("Adventurer");
        field.Submit();

        Assert.Equal("Adventurer", environment.Runtime.LastSetName);
    }


    [Fact]
    public void Summary_RebuildListbox_SkillRows_UseKeyValueTemplate_WithUnconditionalHeaders()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        SelectAluvianMale(environment);
        // SkillTrainOnly (1, "Axe") is Trained; SkillSpecializable (2,
        // "Bow") is left Inactive entirely — no skill occupies the
        // Specialized bucket.
        environment.Runtime.View.SetSkillLevel(SkillTrainOnly, ChargenSkillAdvancementClass.Trained);
        GoToSummary(environment);

        UiTemplateListBox listBox = environment.SummaryListBox();
        IReadOnlyList<UiElement> rows = Assert.IsType<UiScrollablePanel>(
            listBox.ViewportForTest).Children;

        const uint headerTextId = 0x100000FEu;
        const uint keyTextId = 0x100002FCu;
        const uint valueTextId = 0x100002FDu;

        var headers = new List<string>();
        var pairs = new List<(string Key, string Value)>();
        foreach (UiElement row in rows)
        {
            if (UiElement.FindDescendant(row, headerTextId) is UiText header)
                headers.Add(JoinedText(header));
            else if (UiElement.FindDescendant(row, keyTextId) is UiText key
                && UiElement.FindDescendant(row, valueTextId) is UiText value)
            {
                pairs.Add((JoinedText(key), JoinedText(value)));
            }
        }

        Assert.Contains("Specialized Skills", headers);
        Assert.Contains("Trained Skills", headers);
        Assert.Single(pairs, p => p.Key == "Axe" && p.Value == "10");
        Assert.DoesNotContain(pairs, p => p.Key == "Bow");
    }

    private static string JoinedText(UiText text) =>
        string.Join(" ", text.LinesProvider().Select(static line => line.Text));

    // ── CC5: 0xF643 rejection dialogs ─────────────────────────────────────

    [Fact]
    public void CreationFailed_NameInUse_ShowsTheRetailErrorDialog_AndAcknowledgesOnClose()
    {
        using var environment = new EnvironmentHarness();
        environment.Runtime.ResolvedStrings["ID_Character_Err_NameReserved"] = "That name is in use.";
        environment.Controller.Open();

        environment.Runtime.View.Snapshot = environment.Runtime.View.Snapshot with
        {
            LastRejection = new RuntimeCharacterCreationRejection(
                3u,
                CharGenVerificationResponse.Code.NameInUse,
                "NameInUse",
                "Adventurer"),
        };
        BumpRevisionAndTick(environment);

        Assert.True(environment.Dialogs.IsOpen);
        Assert.Equal("That name is in use.", environment.LastDialogMessage());

        environment.DismissActiveMessageDialog();

        Assert.Equal(1, environment.Runtime.AcknowledgeRejectionCalls);
    }

    [Fact]
    public void CreationFailed_IdenticalRejectionAfterAcknowledge_ReshowsTheDialog()
    {
        using var environment = new EnvironmentHarness();
        environment.Runtime.ResolvedStrings["ID_Character_Err_NameReserved"] = "That name is in use.";
        environment.Controller.Open();

        var rejection = new RuntimeCharacterCreationRejection(
            3u, CharGenVerificationResponse.Code.NameInUse, "NameInUse", "Adventurer");

        environment.Runtime.View.Snapshot = environment.Runtime.View.Snapshot with { LastRejection = rejection };
        BumpRevisionAndTick(environment);
        Assert.True(environment.Dialogs.IsOpen);

        environment.DismissActiveMessageDialog();
        Assert.Equal(1, environment.Runtime.AcknowledgeRejectionCalls);
        Assert.False(environment.Dialogs.IsOpen);
        Assert.Null(environment.Runtime.View.Snapshot.LastRejection);

        environment.Controller.Tick();

        // The identical rejection value arrives again.
        environment.Runtime.View.Snapshot = environment.Runtime.View.Snapshot with { LastRejection = rejection };
        BumpRevisionAndTick(environment);

        Assert.True(environment.Dialogs.IsOpen);
        Assert.Equal("That name is in use.", environment.LastDialogMessage());

        environment.DismissActiveMessageDialog();
        Assert.Equal(2, environment.Runtime.AcknowledgeRejectionCalls);
    }

    [Fact]
    public void CreationFailed_SameRejectionAcrossTicks_ShowsOnlyOneDialog()
    {
        using var environment = new EnvironmentHarness();
        environment.Runtime.ResolvedStrings["ID_Character_Err_NameBanned"] = "That name is banned.";
        environment.Controller.Open();

        environment.Runtime.View.Snapshot = environment.Runtime.View.Snapshot with
        {
            LastRejection = new RuntimeCharacterCreationRejection(
                4u, CharGenVerificationResponse.Code.NameBanned, "NameBanned", "Adventurer"),
        };
        BumpRevisionAndTick(environment);
        Assert.Equal(1, environment.Dialogs.ActiveCount);

        environment.Controller.Tick();
        Assert.Equal(1, environment.Dialogs.ActiveCount);
    }

    private sealed class FakeChargenPreviewControl : AcDream.App.Rendering.IChargenPreviewControl
    {
        public int ZoomInCalls { get; private set; }
        public int ZoomOutCalls { get; private set; }
        public int RotateClockwiseCalls { get; private set; }
        public int RotateCounterClockwiseCalls { get; private set; }

        public bool Rebuild(
            ChargenOptions options,
            uint heritageId,
            int genderKey,
            ChargenAppearanceSelection selection) => true;

        public void ZoomIn() => ZoomInCalls++;
        public void ZoomOut() => ZoomOutCalls++;
        public void RotateClockwise() => RotateClockwiseCalls++;
        public void RotateCounterClockwise() => RotateCounterClockwiseCalls++;
    }


    [Fact]
    public void HeritageDescription_ComposesGreenHeaderAndWhiteBodySegments()
    {
        using var environment = new EnvironmentHarness();
        environment.Runtime.ResolvedStrings["ID_CharGen_Heritage_StartingSkills_Header"] = "Trained Starting Skills:";
        environment.Runtime.ResolvedStrings["ID_CharGen_Heritage_StartingSkills"] = "Line one\nLine two";
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        BumpRevisionAndTick(environment);

        UiText description = Assert.IsType<UiText>(environment.Screen.FindElement(0x100003C4u));
        var lines = description.LinesProvider().ToList();

        Assert.Contains(lines, l => l.Text == "Trained Starting Skills:" && l.Color == new Vector4(0f, 1f, 0f, 1f));
        // The source-decoded line break in the body segment must become TWO
        // separate lines, not render as a literal backslash-n.
        Assert.Contains(lines, l => l.Text == "Line one" && l.Color == Vector4.One);
        Assert.Contains(lines, l => l.Text == "Line two" && l.Color == Vector4.One);
        Assert.DoesNotContain(lines, l => l.Text.Contains("\\n"));
    }

    [Fact]
    public void TownDescription_ChangesRenderedLinesWhenSwitchingTowns()
    {
        using var environment = new EnvironmentHarness();
        environment.Runtime.ResolvedStrings["ID_CharGen_TownHowTo"] = "How to pick a town.";
        environment.Runtime.ResolvedStrings["ID_CharGen_HoltText"] = "Holtburg is snowy.";
        environment.Runtime.ResolvedStrings["ID_CharGen_ShoushiText"] = "Shoushi is sunny.";
        environment.Controller.Open();
        environment.TabButton(CharacterCreationUiController.TownTabElementId).OnClick!();

        environment.Button(0x1000040Du).OnClick!(); // Holtburg
        BumpRevisionAndTick(environment);
        UiText description = Assert.IsType<UiText>(environment.Screen.FindElement(0x10000409u));
        string holtburgText = JoinedText(description);
        Assert.Contains("Holtburg is snowy.", holtburgText);

        environment.Button(0x1000040Fu).OnClick!(); // Shoushi
        BumpRevisionAndTick(environment);
        string shoushiText = JoinedText(description);
        Assert.Contains("Shoushi is sunny.", shoushiText);
        Assert.DoesNotContain("Holtburg is snowy.", shoushiText);
    }

    [Fact]
    public void ProfessionDescription_BindsAndSwitchesPerTemplate()
    {
        using var environment = new EnvironmentHarness();
        environment.Runtime.ResolvedStrings["ID_CharGen_CustomText"] = "Custom flexible build.";
        environment.Runtime.ResolvedStrings["ID_CharGen_BowText"] = "Bow hunters use ranged attacks.";
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.ProfessionTabElementId).OnClick!();

        environment.Button(0x100003DAu).OnClick!(); // Bow Hunter = template 1
        BumpRevisionAndTick(environment);

        UiText description = Assert.IsType<UiText>(environment.Screen.FindElement(0x100003E0u));
        Assert.Contains("Bow hunters use ranged attacks.", JoinedText(description));
    }

    [Fact]
    public void ProfessionAndSkillsDisplayButtons_ValueWriteDoesNotClobberLabel()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);

        UiButton available = environment.Button(0x100003E2u);
        available.Label = "Attribute Credits"; // fixture authors no P0x17; simulate it
        UiButton credits = environment.Button(0x100003F9u);
        credits.Label = "Available Skill Credits";

        environment.TabButton(CharacterCreationUiController.ProfessionTabElementId).OnClick!();
        BumpRevisionAndTick(environment);
        Assert.Equal("Attribute Credits", available.Label);
        Assert.Equal("66", available.ValueLabel);

        environment.TabButton(CharacterCreationUiController.SkillsTabElementId).OnClick!();
        BumpRevisionAndTick(environment);
        Assert.Equal("Available Skill Credits", credits.Label);
        Assert.Equal("50", credits.ValueLabel); // RemainingSkillCredits=50
    }

    [Fact]
    public void AppearanceSpinCaptions_ArePartNames_NotOrdinals_AndVaryByHeritage()
    {
        using var environment = new EnvironmentHarness();
        environment.Runtime.ResolvedStrings["ID_CharGen_HairStyle"] = "Hair Style";
        environment.Runtime.ResolvedStrings["ID_CharGen_Eyes"] = "Eyes";
        environment.Runtime.ResolvedStrings["ID_CharGen_GearText_HairButton"] = "Gear Hair";
        environment.Controller.Open();
        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.TabButton(CharacterCreationUiController.AppearanceTabElementId).OnClick!();
        BumpRevisionAndTick(environment);

        UiButton hairSpin = environment.Button(CharacterCreationAppearancePage.HairSpinId);
        Assert.Equal("Hair Style", hairSpin.Label);
        Assert.DoesNotContain(hairSpin.Label, new[] { "1", "2", "-" });

        environment.Runtime.SelectHeritageDirect((uint)ChargenHeritageGroup.Gearknight);
        BumpRevisionAndTick(environment);
        Assert.Equal("Gear Hair", hairSpin.Label);
    }

    /// <summary>Root 1d: the Heritage and Profession backdrops switch
    /// state per selection.</summary>
    [Fact]
    public void HeritageAndProfessionBackdrops_SwitchStatePerSelection()
    {
        using var environment = new EnvironmentHarness();
        environment.Controller.Open();

        var heritageBackdrop = Assert.IsAssignableFrom<IUiDatStateful>(
            environment.Screen.FindElement(0x100003BEu));
        environment.Runtime.SelectHeritageDirect(AluvianId);
        BumpRevisionAndTick(environment);
        Assert.Equal(0x10000021u, heritageBackdrop.ActiveRetailStateId);

        environment.Runtime.SelectHeritageDirect((uint)ChargenHeritageGroup.Gharundim);
        BumpRevisionAndTick(environment);
        Assert.Equal(0x10000022u, heritageBackdrop.ActiveRetailStateId);

        environment.TabButton(CharacterCreationUiController.ProfessionTabElementId).OnClick!();
        var professionBackdrop = Assert.IsAssignableFrom<IUiDatStateful>(
            environment.Screen.FindElement(0x100003D8u));
        environment.Button(0x100003DAu).OnClick!(); // Bow Hunter = template 1
        BumpRevisionAndTick(environment);
        Assert.Equal(0x1000002Cu, professionBackdrop.ActiveRetailStateId);
    }

    [Fact]
    public void SummaryHowToText_ComposesHowToPlusNameSuggestionsPlusHowToEnd_ForNamedHeritages()
    {
        using var environment = new EnvironmentHarness();
        environment.Runtime.ResolvedStrings["ID_CharGen_SummaryHowTo"] = "HOWTO.";
        environment.Runtime.ResolvedStrings["ID_CharGen_SummaryHowToEnd"] = "HOWTOEND.";
        environment.Runtime.ResolvedStrings["ID_CharGen_AluMaleNames"] = "Alucard, Aldric";
        environment.Runtime.ResolvedStrings["ID_CharGen_AluFemaleNames"] = "Alura, Aldyth";
        environment.Controller.Open();

        environment.Runtime.SelectHeritageDirect(AluvianId);
        environment.Runtime.SelectGenderDirect(1u); // male
        environment.TabButton(CharacterCreationUiController.SummaryTabElementId).OnClick!();
        BumpRevisionAndTick(environment);

        UiText howTo = Assert.IsType<UiText>(
            environment.Screen.FindElement(CharacterCreationSummaryPage.HowToTextId));
        string composed = JoinedText(howTo);
        Assert.Contains("HOWTO.", composed);
        Assert.Contains("Alucard, Aldric", composed);
        Assert.DoesNotContain("Alura, Aldyth", composed);
        Assert.Contains("HOWTOEND.", composed);
        Assert.Contains("HOWTO.Alucard, Aldric", composed.Replace("\n", string.Empty));

        environment.Runtime.SelectGenderDirect(2u); // female
        BumpRevisionAndTick(environment);
        string femaleComposed = JoinedText(howTo);
        Assert.Contains("Alura, Aldyth", femaleComposed);
        Assert.DoesNotContain("Alucard, Aldric", femaleComposed);
    }

    [Fact]
    public void SummaryHowToText_SkipsNameSuggestions_ForHeritagesWithNoRealString()
    {
        using var environment = new EnvironmentHarness();
        environment.Runtime.ResolvedStrings["ID_CharGen_SummaryHowTo"] = "HOWTO.";
        environment.Runtime.ResolvedStrings["ID_CharGen_SummaryHowToEnd"] = "HOWTOEND.";
        environment.Controller.Open();

        environment.Runtime.SelectHeritageDirect((uint)ChargenHeritageGroup.Undead);
        environment.TabButton(CharacterCreationUiController.SummaryTabElementId).OnClick!();
        BumpRevisionAndTick(environment);

        UiText howTo = Assert.IsType<UiText>(
            environment.Screen.FindElement(CharacterCreationSummaryPage.HowToTextId));
        string composed = JoinedText(howTo);
        Assert.Contains("HOWTO.", composed);
        Assert.Contains("HOWTOEND.", composed);
    }

    private static void BumpRevisionAndTick(EnvironmentHarness environment)
    {
        RuntimeCharacterCreationSnapshot snapshot = environment.Runtime.View.Snapshot;
        environment.Runtime.View.Snapshot = snapshot with { Revision = snapshot.Revision + 1 };
        environment.Controller.Tick();
    }

    private static IEnumerable<UiElement> Descendants(UiElement root)
    {
        yield return root;
        foreach (UiElement child in root.Children)
            foreach (UiElement descendant in Descendants(child))
                yield return descendant;
    }

    // ── Fixture ──────────────────────────────────────────────────────────

    private sealed class EnvironmentHarness : IDisposable
    {
        private readonly List<ImportedLayout> _dialogLayouts = [];

        public EnvironmentHarness()
        {
            Host = new UiRoot { Width = 800f, Height = 600f };
            Screen = BuildScreen();
            Runtime = new FakeRuntime();
            Dialogs = new RetailDialogFactory(Host, type =>
            {
                ImportedLayout layout = RetailDialogFactoryTests.BuildDialogLayout(type);
                _dialogLayouts.Add(layout);
                return layout;
            });
            Controller = Assert.IsType<CharacterCreationUiController>(
                CharacterCreationUiController.CreateDetached(
                    Host,
                    Screen,
                    ResolveSkillRowTemplate,
                    Dialogs,
                    Runtime.Bindings,
                    new CharacterCreationUiController.DialogStrings(
                        "Are you sure you want to leave?",
                        "No name entered.",
                        "You have unspent attribute credits.",
                        "This will randomize your character.",
                        "That name is too long.")));
            Controller.AttachAndTick();
        }

        public UiRoot Host { get; }
        public ImportedLayout Screen { get; }
        public FakeRuntime Runtime { get; }
        public RetailDialogFactory Dialogs { get; }
        public CharacterCreationUiController Controller { get; }

        public UiButton Button(uint id) =>
            Assert.IsType<UiButton>(Screen.FindElement(id));

        public UiButton TabButton(uint id) => Button(id);

        public UiElement Page(uint id) =>
            Assert.IsAssignableFrom<UiElement>(Screen.FindElement(id));

        public UiTemplateListBox SkillsList() =>
            Assert.IsType<UiTemplateListBox>(Screen.FindElement(0x100003F7u));

        public (UiButton Up, UiButton Down) SkillRowArrows(uint skillId)
        {
            string skillName = ItemAppraisalTextFormatter.SkillName((int)skillId);
            UiElement row = Assert.Single(
                SkillsList().ViewportForTest!.Children,
                candidate => UiElement.FindDescendant(candidate, 0x10000301u) is UiText name
                    && JoinedText(name) == skillName);
            UiButton up = Assert.IsType<UiButton>(UiElement.FindDescendant(row, 0x10000304u));
            UiButton down = Assert.IsType<UiButton>(UiElement.FindDescendant(row, 0x10000305u));
            return (up, down);
        }

        public (UiDatElement Row, UiText NameText) SkillRow(uint skillId)
        {
            string skillName = ItemAppraisalTextFormatter.SkillName((int)skillId);
            UiElement row = Assert.Single(
                SkillsList().ViewportForTest!.Children,
                candidate => UiElement.FindDescendant(candidate, 0x10000301u) is UiText name
                    && JoinedText(name) == skillName);
            UiDatElement datRow = Assert.IsType<UiDatElement>(row);
            UiText nameText = Assert.IsType<UiText>(UiElement.FindDescendant(row, 0x10000301u));
            return (datRow, nameText);
        }

        public UiText SkillInfoTitle() =>
            Assert.IsType<UiText>(Screen.FindElement(0x100003FBu));

        public UiText SkillInfoText() =>
            Assert.IsType<UiText>(Screen.FindElement(0x100003FCu));

        public UiScrollbar SkillsScrollbar() =>
            Assert.IsType<UiScrollbar>(Screen.FindElement(0x100003F8u));

        public UiTemplateListBox SummaryListBox() =>
            Assert.IsType<UiTemplateListBox>(Screen.FindElement(CharacterCreationSummaryPage.ListBoxId));

        public UiScrollbar ShadeScroll() =>
            Assert.IsType<UiScrollbar>(Screen.FindElement(CharacterCreationAppearancePage.ShadeScrollId));

        public UiField SummaryNameField() =>
            Assert.IsType<UiField>(Screen.FindElement(CharacterCreationSummaryPage.NameTextId));

        public void ConfirmActiveDialog(bool confirmed)
        {
            ImportedLayout dialog = _dialogLayouts[^1];
            uint buttonId = confirmed
                ? RetailConfirmationDialogView.AcceptButtonId
                : RetailConfirmationDialogView.RejectButtonId;
            UiButton button = Assert.IsType<UiButton>(dialog.FindElement(buttonId));
            button.OnClick!();
        }

        public void DismissActiveMessageDialog()
        {
            ImportedLayout dialog = _dialogLayouts[^1];
            UiButton button = Assert.IsType<UiButton>(
                dialog.FindElement(RetailMessageDialogView.OkButtonId));
            button.OnClick!();
        }

        public string LastDialogMessage() => string.Join(
            " ",
            Assert.IsType<UiText>(_dialogLayouts[^1].FindElement(0x3Eu))
                .LinesProvider()
                .Select(static line => line.Text));

        private static UiElement? ResolveSkillRowTemplate(
            uint templateLayoutId,
            uint templateElementId) =>
            templateElementId switch
            {
                SummaryLineTemplateId => BuildSummaryLineTemplate(),
                SummaryHeaderTemplateId => BuildSummaryHeaderTemplate(),
                SummaryPairTemplateId => BuildSummaryPairTemplate(),
                _ => BuildSkillRowTemplate(templateElementId),
            };

        public void Dispose()
        {
            Controller.Dispose();
            Dialogs.Dispose();
        }
    }

    private sealed class FakeRuntime
    {
        private static readonly RuntimeGenerationToken Generation = new(3u);

        public FakeRuntime()
        {
            View = new FakeView(BuildOptions());
            Bindings = new CharacterCreationRuntimeBindings(
                () => ProvideView ? View : null,
                SelectHeritage,
                SelectGender,
                SelectTemplate,
                SetAttribute,
                (_, _) => Result(RuntimeCommandStatus.Accepted),
                skillId => SetSkillLevel(skillId, ChargenSkillAdvancementClass.Trained),
                skillId => SetSkillLevel(skillId, ChargenSkillAdvancementClass.Specialized),
                skillId => SetSkillLevel(skillId, ChargenSkillAdvancementClass.Untrained),
                SelectStartArea,
                Finish,
                () => RequestExitCalls++,
                SetAppearanceIndex: SetAppearanceIndex,
                SetShade: SetShade,
                ResolveText: key => ResolvedStrings.TryGetValue(key, out string? value) ? value : null,
                SetName: SetName,
                AcknowledgeRejection: AcknowledgeRejection,
                RandomizeCharacter: RandomizeCharacter,
                RandomizeAppearance: () => { RandomizeAppearanceCalls++; return Result(RuntimeCommandStatus.Accepted); },
                RandomizeClothing: () => { RandomizeClothingCalls++; return Result(RuntimeCommandStatus.Accepted); },
                GetSkillScore: GetSkillScore,
                OpenOnStart: false);
        }

        private static uint GetSkillScore(
            uint skillId,
            ChargenAttributeValues attributes,
            ChargenSkillAdvancementClass level) => skillId * 10u;

        public FakeView View { get; }
        public CharacterCreationRuntimeBindings Bindings { get; }
        public bool ProvideView { get; set; } = true;
        public int RequestExitCalls { get; private set; }
        public uint LastSelectedHeritage { get; private set; }
        public uint LastSelectedGender { get; private set; }
        public uint LastSelectedTemplate { get; private set; }
        public ChargenAttributeId LastAttributeSet { get; private set; }
        public int LastAttributeValue { get; private set; }
        public int LastSelectedStartArea { get; private set; } = -1;
        public ChargenAppearanceSlot? LastAppearanceSlot { get; private set; }
        public uint LastAppearanceIndex { get; private set; }
        public int AppearanceIndexCallCount { get; private set; }
        public ChargenShadeSlot? LastShadeSlot { get; private set; }
        public double LastShadeValue { get; private set; }
        public string? LastSetName { get; private set; }
        public int FinishCallCount { get; private set; }
        public bool LastConfirmedUnspentCredits { get; private set; }
        public int AcknowledgeRejectionCalls { get; private set; }
        public int RandomizeCharacterCalls { get; private set; }
        public int RandomizeAppearanceCalls { get; private set; }
        public int RandomizeClothingCalls { get; private set; }

        public uint RandomizedHeritageId { get; set; }
        public uint RandomizedGenderKey { get; set; }

        public bool RandomizeCharacterAccepts { get; set; } = true;

        /// <summary>Populated by tests exercising the <c>ID_Character_Err_*</c>
        /// rejection-dialog path — <c>ResolveText</c> above reads from it.</summary>
        public Dictionary<string, string> ResolvedStrings { get; } = [];

        public void SelectHeritageDirect(uint heritageId) => SelectHeritage(heritageId);
        public void SelectGenderDirect(uint genderKey) => SelectGender(genderKey);

        public ChargenSkillAdvancementClass GetSkillLevel(uint skillId) =>
            View.GetSkillLevel(skillId);

        private RuntimeCommandResult SelectHeritage(uint heritageId)
        {
            LastSelectedHeritage = heritageId;
            View.Snapshot = View.Snapshot with { HeritageId = heritageId };
            return Result(RuntimeCommandStatus.Accepted);
        }

        private RuntimeCommandResult SelectGender(uint genderKey)
        {
            LastSelectedGender = genderKey;
            View.Snapshot = View.Snapshot with { GenderKey = genderKey };
            return Result(RuntimeCommandStatus.Accepted);
        }

        private RuntimeCommandResult SelectTemplate(uint templateIndex)
        {
            LastSelectedTemplate = templateIndex;
            View.Snapshot = View.Snapshot with { Template = templateIndex };
            return Result(RuntimeCommandStatus.Accepted);
        }

        private RuntimeCommandResult SetAttribute(ChargenAttributeId attribute, int value)
        {
            LastAttributeSet = attribute;
            LastAttributeValue = value;
            return Result(RuntimeCommandStatus.Accepted);
        }

        private RuntimeCommandResult SetSkillLevel(
            uint skillId,
            ChargenSkillAdvancementClass targetClass)
        {
            View.SetSkillLevel(skillId, targetClass);
            return Result(RuntimeCommandStatus.Accepted);
        }

        private RuntimeCommandResult SelectStartArea(int startAreaIndex)
        {
            LastSelectedStartArea = startAreaIndex;
            View.Snapshot = View.Snapshot with { StartArea = startAreaIndex };
            return Result(RuntimeCommandStatus.Accepted);
        }

        private RuntimeCommandResult SetAppearanceIndex(ChargenAppearanceSlot slot, uint index)
        {
            LastAppearanceSlot = slot;
            LastAppearanceIndex = index;
            AppearanceIndexCallCount++;
            View.Snapshot = View.Snapshot with { Appearance = WithAppearanceIndex(View.Snapshot.Appearance, slot, index) };
            return Result(RuntimeCommandStatus.Accepted);
        }

        private RuntimeCommandResult SetShade(ChargenShadeSlot slot, double value)
        {
            LastShadeSlot = slot;
            LastShadeValue = value;
            return Result(RuntimeCommandStatus.Accepted);
        }

        private RuntimeCommandResult SetName(string name)
        {
            LastSetName = name;
            View.Snapshot = View.Snapshot with { Name = name };
            return Result(RuntimeCommandStatus.Accepted);
        }

        private RuntimeCommandResult Finish(bool confirmUnspentCredits)
        {
            FinishCallCount++;
            LastConfirmedUnspentCredits = confirmUnspentCredits;
            RuntimeCharacterCreationSnapshot snapshot = View.Snapshot;
            string trimmed = snapshot.Name.Trim();

            RuntimeCharacterCreationLocalRefusal refusal = trimmed.Length == 0
                ? new RuntimeCharacterCreationLocalRefusal(NoName: true, false, false, false)
                : snapshot.HeritageId == 0u || snapshot.GenderKey == 0u
                    ? new RuntimeCharacterCreationLocalRefusal(
                        false, false, false, false, HeritageOrGenderUnset: true)
                    : !confirmUnspentCredits && snapshot.RemainingAttributeCredits > 0
                        ? new RuntimeCharacterCreationLocalRefusal(
                            false, AttributeCreditsUnspent: true, false, false)
                        : RuntimeCharacterCreationLocalRefusal.None;

            View.Snapshot = snapshot with { Name = trimmed, LastLocalRefusal = refusal };
            return Result(refusal.Any ? RuntimeCommandStatus.Rejected : RuntimeCommandStatus.Accepted);
        }

        private RuntimeCommandResult AcknowledgeRejection()
        {
            AcknowledgeRejectionCalls++;
            View.Snapshot = View.Snapshot with { LastRejection = null };
            return Result(RuntimeCommandStatus.Accepted);
        }

        private RuntimeCommandResult RandomizeCharacter()
        {
            RandomizeCharacterCalls++;
            if (!RandomizeCharacterAccepts)
                return Result(RuntimeCommandStatus.Rejected);
            View.Snapshot = View.Snapshot with
            {
                HeritageId = RandomizedHeritageId,
                GenderKey = RandomizedGenderKey,
            };
            return Result(RuntimeCommandStatus.Accepted);
        }

        private static RuntimeCharacterCreationAppearance WithAppearanceIndex(
            RuntimeCharacterCreationAppearance a,
            ChargenAppearanceSlot slot,
            uint index) => slot switch
        {
            ChargenAppearanceSlot.EyesStrip => a with { EyesStrip = index },
            ChargenAppearanceSlot.NoseStrip => a with { NoseStrip = index },
            ChargenAppearanceSlot.MouthStrip => a with { MouthStrip = index },
            ChargenAppearanceSlot.HairStyle => a with { HairStyle = index },
            ChargenAppearanceSlot.HairColor => a with { HairColor = index },
            ChargenAppearanceSlot.EyeColor => a with { EyeColor = index },
            ChargenAppearanceSlot.HeadgearStyle => a with { HeadgearStyle = index },
            ChargenAppearanceSlot.HeadgearColor => a with { HeadgearColor = index },
            ChargenAppearanceSlot.ShirtStyle => a with { ShirtStyle = index },
            ChargenAppearanceSlot.ShirtColor => a with { ShirtColor = index },
            ChargenAppearanceSlot.TrousersStyle => a with { TrousersStyle = index },
            ChargenAppearanceSlot.TrousersColor => a with { TrousersColor = index },
            ChargenAppearanceSlot.FootwearStyle => a with { FootwearStyle = index },
            ChargenAppearanceSlot.FootwearColor => a with { FootwearColor = index },
            _ => a,
        };

        private static RuntimeCommandResult Result(RuntimeCommandStatus status) =>
            new(status, Generation);

        private static ChargenOptions BuildOptions()
        {
            var gender = new ChargenGenderOptions(
                GenderKey: (int)GenderKey,
                Name: "Male",
                Scale: 1u,
                SetupId: 0x2000054u,
                SoundTableId: 0u,
                IconId: 0u,
                BasePaletteId: 0u,
                SkinPalSetId: 0u,
                PhysicsTableId: 0u,
                MotionTableId: 0u,
                CombatTableId: 0u,
                BaseObjDesc: ChargenObjDesc.Empty,
                HairColors: [0x1000u, 0x1001u, 0x1002u],
                HairStyles:
                [
                    new ChargenHairStyle(IconId: 1u, Bald: false, AlternateSetup: 0u, ObjDesc: ChargenObjDesc.Empty),
                    new ChargenHairStyle(IconId: 2u, Bald: false, AlternateSetup: 0u, ObjDesc: ChargenObjDesc.Empty),
                    new ChargenHairStyle(IconId: 3u, Bald: true, AlternateSetup: 0u, ObjDesc: ChargenObjDesc.Empty),
                ],
                EyeColors: [0x2000u, 0x2001u],
                EyeStrips:
                [
                    new ChargenEyeStrip(IconId: 1u, BaldIconId: 1u, ObjDesc: ChargenObjDesc.Empty, BaldObjDesc: ChargenObjDesc.Empty),
                    new ChargenEyeStrip(IconId: 2u, BaldIconId: 2u, ObjDesc: ChargenObjDesc.Empty, BaldObjDesc: ChargenObjDesc.Empty),
                ],
                NoseStrips: [new ChargenFaceStrip(IconId: 1u, ObjDesc: ChargenObjDesc.Empty)],
                MouthStrips: [new ChargenFaceStrip(IconId: 1u, ObjDesc: ChargenObjDesc.Empty)],
                Headgears:
                [
                    new ChargenGearOption("Cloth Cap", ClothingTableId: 1u, WeenieDefaultId: 1u),
                    new ChargenGearOption("Leather Cap", ClothingTableId: 2u, WeenieDefaultId: 2u),
                ],
                Shirts: [new ChargenGearOption("Tunic", ClothingTableId: 3u, WeenieDefaultId: 3u)],
                Pants: [new ChargenGearOption("Trousers", ClothingTableId: 4u, WeenieDefaultId: 4u)],
                Footwear: [new ChargenGearOption("Boots", ClothingTableId: 5u, WeenieDefaultId: 5u)],
                ClothingColors: [0x3000u, 0x3001u, 0x3002u]);

            var templates = new List<ChargenTemplate>
            {
                new(
                    "Custom",
                    IconId: 0u,
                    TitleStringId: 0u,
                    Attributes: new ChargenAttributeValues(10, 10, 10, 10, 10, 10),
                    NormalSkills: [],
                    PrimarySkills: []),
                new(
                    "Bow Hunter",
                    IconId: 0u,
                    TitleStringId: 0u,
                    Attributes: new ChargenAttributeValues(16, 10, 10, 10, 10, 10),
                    NormalSkills: [SkillTrainOnly],
                    PrimarySkills: []),
            };

            var skillCosts = new Dictionary<uint, ChargenSkillCost>
            {
                [SkillTrainOnly] = new(SkillTrainOnly, NormalCost: 2, PrimaryCost: 6),
                [SkillSpecializable] = new(SkillSpecializable, NormalCost: 2, PrimaryCost: 6),
                [SkillFreeTrained] = new(SkillFreeTrained, NormalCost: 0, PrimaryCost: 6),
            };

            var skillDetails = new Dictionary<uint, ChargenSkillDetail>
            {
                [SkillTrainOnly] = new ChargenSkillDetail(
                    SkillTrainOnly,
                    MinLevel: 1u,
                    Description: "A test skill description.",
                    Formula: new ChargenSkillFormula(
                        AdditiveBonus: 2,
                        Attribute1Multiplier: 2,
                        Attribute2Multiplier: 0,
                        Divisor: 4,
                        Attribute1: (uint)ChargenAttributeId.Strength,
                        Attribute2: 0u)),
                [SkillSpecializable] = new ChargenSkillDetail(
                    SkillSpecializable, MinLevel: 2u, Description: string.Empty, Formula: default),
                [SkillFreeTrained] = new ChargenSkillDetail(
                    SkillFreeTrained, MinLevel: 1u, Description: string.Empty, Formula: default),
            };

            var aluvian = new ChargenHeritageOptions(
                AluvianId,
                "Aluvian",
                IconId: 0u,
                SetupId: 0x2000054u,
                EnvironmentSetupId: 0u,
                AttributeCredits: 66u,
                SkillCredits: 50u,
                PrimaryStartAreaIndices: [0, 1],
                SecondaryStartAreaIndices: [],
                SkillCostsBySkillId: skillCosts,
                Templates: templates,
                GendersByKey: new Dictionary<int, ChargenGenderOptions> { [(int)GenderKey] = gender });

            var olthoi = new ChargenHeritageOptions(
                OlthoiId,
                "Olthoi",
                IconId: 0u,
                SetupId: 0x2000054u,
                EnvironmentSetupId: 0u,
                AttributeCredits: 60u,
                SkillCredits: 0u,
                PrimaryStartAreaIndices: [0],
                SecondaryStartAreaIndices: [],
                SkillCostsBySkillId: new Dictionary<uint, ChargenSkillCost>(),
                Templates:
                [
                    new ChargenTemplate(
                        "Custom",
                        IconId: 0u,
                        TitleStringId: 0u,
                        Attributes: new ChargenAttributeValues(10, 10, 10, 10, 10, 10),
                        NormalSkills: [],
                        PrimarySkills: []),
                ],
                GendersByKey: new Dictionary<int, ChargenGenderOptions> { [(int)GenderKey] = gender });

            var starterAreas = new List<ChargenStarterArea>
            {
                new(0, "Holtburg", [new ChargenPosition(1u, Vector3.Zero, Quaternion.Identity)]),
                new(1, "Shoushi", [new ChargenPosition(2u, Vector3.Zero, Quaternion.Identity)]),
                new(2, "Yaraq", [new ChargenPosition(3u, Vector3.Zero, Quaternion.Identity)]),
                new(3, "Sanamar", [new ChargenPosition(4u, Vector3.Zero, Quaternion.Identity)]),
            };

            return new ChargenOptions(
                starterAreas,
                new Dictionary<uint, ChargenHeritageOptions>
                {
                    [AluvianId] = aluvian,
                    [OlthoiId] = olthoi,
                },
                new Dictionary<uint, ChargenSkillCost>(),
                skillDetails);
        }
    }

    private sealed class FakeView(ChargenOptions options) : IRuntimeCharacterCreationView
    {
        private readonly Dictionary<uint, ChargenSkillAdvancementClass> _skillLevels = [];

        public RuntimeCharacterCreationSnapshot Snapshot { get; set; } =
            new(
                new RuntimeGenerationToken(3u),
                IsActive: true,
                Revision: 1,
                HeritageId: 0u,
                GenderKey: 0u,
                Appearance: RuntimeCharacterCreationAppearance.Default,
                Template: RuntimeCharacterCreationSnapshot.TemplateUnset,
                Attributes: default,
                AttributeLockMask: 0u,
                TotalAttributeCredits: 66u,
                RemainingAttributeCredits: 66,
                TotalSkillCredits: 50u,
                RemainingSkillCredits: 50,
                Name: string.Empty,
                StartArea: -1,
                Slot: 0u,
                VerificationPending: false,
                LastLocalRefusal: default,
                LastRejection: null,
                LastCreated: null);

        public ChargenOptions Options { get; } = options;

        public ChargenSkillAdvancementClass GetSkillLevel(uint skillId) =>
            _skillLevels.TryGetValue(skillId, out ChargenSkillAdvancementClass level)
                ? level
                : ChargenSkillAdvancementClass.Inactive;

        public void SetSkillLevel(uint skillId, ChargenSkillAdvancementClass level) =>
            _skillLevels[skillId] = level;

        public IDisposable Subscribe(IRuntimeCharacterCreationObserver observer) =>
            NullSubscription.Instance;

        private sealed class NullSubscription : IDisposable
        {
            public static readonly NullSubscription Instance = new();
            public void Dispose() { }
        }
    }

    private static ImportedLayout BuildScreen()
    {
        var root = new ElementInfo
        {
            Id = CharacterCreationUiController.RootElementId,
            Type = 3u,
            Width = 800f,
            Height = 600f,
        };

        root.Children.Add(ContainerInfo(CharacterCreationUiController.ProgressBarElementId));
        root.Children.Add(ButtonInfo(CharacterCreationUiController.BackElementId));
        root.Children.Add(ButtonInfo(CharacterCreationUiController.NextElementId));
        ElementInfo finishInfo = ButtonInfo(CharacterCreationUiController.FinishElementId);
        finishInfo.X = 600f;
        finishInfo.Y = 500f;
        root.Children.Add(finishInfo);
        root.Children.Add(ButtonInfo(CharacterCreationUiController.HelpElementId));
        root.Children.Add(ButtonInfo(CharacterCreationUiController.ExitElementId));
        root.Children.Add(ButtonInfo(CharacterCreationUiController.RandomElementId));
        root.Children.Add(ContainerInfo(CharacterCreationUiController.MasterPageElementId));

        root.Children.Add(BuildHeritagePage());
        root.Children.Add(BuildProfessionPage());
        root.Children.Add(BuildSkillsPage());
        root.Children.Add(BuildAppearancePage());
        root.Children.Add(BuildTownPage());
        root.Children.Add(BuildSummaryPage());

        root.Children.Add(ButtonInfo(CharacterCreationUiController.HeritageTabElementId));
        root.Children.Add(ButtonInfo(CharacterCreationUiController.ProfessionTabElementId));
        root.Children.Add(ButtonInfo(CharacterCreationUiController.SkillsTabElementId));
        root.Children.Add(ButtonInfo(CharacterCreationUiController.AppearanceTabElementId));
        root.Children.Add(ButtonInfo(CharacterCreationUiController.TownTabElementId));
        root.Children.Add(ButtonInfo(CharacterCreationUiController.SummaryTabElementId));

        return LayoutImporter.Build(root, _ => (0u, 0, 0), null);
    }

    private static ElementInfo BuildHeritagePage()
    {
        var page = new ElementInfo
        {
            Id = CharacterCreationUiController.HeritagePageElementId,
            Type = 3u,
            Width = 800f,
            Height = 500f,
        };
        page.Children.Add(ButtonInfo(0x100003BFu)); // Aluvian
        page.Children.Add(ButtonInfo(0x100005C7u)); // Olthoi
        page.Children.Add(ButtonInfo(0x100005F1u)); // Lugian (F3 quirk: no tab-restore/hide)
        page.Children.Add(TextInfo(0x100003C4u));

        var backdrop = ContainerInfo(0x100003BEu);
        foreach (uint stateId in new[]
                 {
                     0x10000021u, 0x10000022u, 0x10000023u, 0x10000024u, 0x10000058u,
                     0x10000059u, 0x1000005Au, 0x1000005Bu, 0x1000005Cu, 0x1000005Du,
                     0x1000005Eu, 0x1000005Fu, 0x10000060u,
                 })
        {
            backdrop.States[stateId] = new UiStateInfo { Id = stateId, Name = $"State_{stateId:X8}" };
        }
        page.Children.Add(backdrop);
        return page;
    }

    private static ElementInfo BuildProfessionPage()
    {
        var page = new ElementInfo
        {
            Id = CharacterCreationUiController.ProfessionPageElementId,
            Type = 3u,
            Width = 800f,
            Height = 500f,
        };
        page.Children.Add(ButtonInfo(0x100003D9u));
        page.Children.Add(ButtonInfo(0x100003DAu)); // Bow Hunter

        var strengthSlider = ContainerInfo(0x100003E6u);
        strengthSlider.Children.Add(ButtonInfo(0x100002ECu));
        strengthSlider.Children.Add(ScrollbarInfo(0x100002EEu));
        strengthSlider.Children.Add(EditableFieldInfo(0x100002EFu));
        page.Children.Add(strengthSlider);

        page.Children.Add(ButtonInfo(0x100003E2u));
        page.Children.Add(ButtonInfo(0x100003E3u)); // Health
        page.Children.Add(ButtonInfo(0x100003E4u)); // Stamina
        page.Children.Add(ButtonInfo(0x100003E5u)); // Mana

        page.Children.Add(TextInfo(0x100003E0u)); // GF-3: description textbox

        var backdrop = ContainerInfo(0x100003D8u);
        foreach (uint stateId in new[]
                 {
                     0x1000002Bu, 0x1000002Cu, 0x1000002Du,
                     0x1000002Eu, 0x1000002Fu, 0x10000030u, 0x10000031u,
                 })
        {
            backdrop.States[stateId] = new UiStateInfo { Id = stateId, Name = $"State_{stateId:X8}" };
        }
        page.Children.Add(backdrop);
        return page;
    }

    private static ElementInfo BuildSkillsPage()
    {
        var page = new ElementInfo
        {
            Id = CharacterCreationUiController.SkillsPageElementId,
            Type = 3u,
            Width = 800f,
            Height = 500f,
        };
        var list = new ElementInfo
        {
            Id = 0x100003F7u,
            Type = 5u,
            X = 20f,
            Y = 40f,
            Width = 300f,
            Height = 320f,
            ScrollbarElementId = 0x100003F8u,
        };
        list.TemplateList.Add(new UiTemplateListEntry(0x21000038u, 0x100002F4u));
        list.TemplateList.Add(new UiTemplateListEntry(0x21000038u, 0x100002FFu));
        page.Children.Add(list);
        page.Children.Add(ScrollbarInfo(0x100003F8u));
        page.Children.Add(ButtonInfo(0x100003F9u));
        page.Children.Add(TextInfo(0x100003FBu));
        page.Children.Add(new ElementInfo
        {
            Id = 0x100003FAu,
            Type = 12u,
            Y = 0f,
            Width = 200f,
            Height = 50f,
        });
        page.Children.Add(TextInfo(0x100003FCu));
        return page;
    }

    private static ElementInfo BuildTownPage()
    {
        var page = new ElementInfo
        {
            Id = CharacterCreationUiController.TownPageElementId,
            Type = 3u,
            Width = 800f,
            Height = 500f,
        };
        page.Children.Add(ButtonInfo(0x1000040Bu)); // Sanamar
        page.Children.Add(ButtonInfo(0x1000040Du)); // Holtburg
        page.Children.Add(ButtonInfo(0x1000040Eu)); // Yaraq
        page.Children.Add(ButtonInfo(0x1000040Fu)); // Shoushi
        page.Children.Add(TextInfo(0x10000409u));

        page.States[0x10000034u] = new UiStateInfo { Id = 0x10000034u, Name = "Holtburg" };
        page.States[0x10000035u] = new UiStateInfo { Id = 0x10000035u, Name = "Sanamar" };
        page.States[0x10000036u] = new UiStateInfo { Id = 0x10000036u, Name = "Yaraq" };
        page.States[0x10000037u] = new UiStateInfo { Id = 0x10000037u, Name = "Shoushi" };
        return page;
    }

    private static ElementInfo BuildAppearancePage()
    {
        var page = new ElementInfo
        {
            Id = CharacterCreationUiController.AppearancePageElementId,
            Type = 3u,
            Width = 800f,
            Height = 500f,
        };

        page.Children.Add(ButtonInfo(CharacterCreationAppearancePage.FemaleButtonId));
        page.Children.Add(ButtonInfo(CharacterCreationAppearancePage.MaleButtonId));
        page.Children.Add(ButtonInfo(CharacterCreationAppearancePage.FaceButtonId));
        page.Children.Add(ButtonInfo(CharacterCreationAppearancePage.ClothesButtonId));
        page.Children.Add(ContainerInfo(CharacterCreationAppearancePage.FaceChoicesId));
        page.Children.Add(ContainerInfo(CharacterCreationAppearancePage.ClothesChoicesId));

        foreach (uint spinId in new[]
                 {
                     CharacterCreationAppearancePage.HairSpinId,
                     CharacterCreationAppearancePage.EyesSpinId,
                     CharacterCreationAppearancePage.NoseSpinId,
                     CharacterCreationAppearancePage.MouthSpinId,
                     CharacterCreationAppearancePage.SkinSpinId,
                     CharacterCreationAppearancePage.HeadgearSpinId,
                     CharacterCreationAppearancePage.ShirtSpinId,
                     CharacterCreationAppearancePage.TrousersSpinId,
                     CharacterCreationAppearancePage.FootwearSpinId,
                 })
        {
            page.Children.Add(SpinInfo(spinId));
        }

        foreach (uint swatchId in CharacterCreationAppearancePage.SwatchIds)
            page.Children.Add(ButtonInfo(swatchId));
        foreach (uint overlayId in CharacterCreationAppearancePage.SwatchOverlayIds)
            page.Children.Add(ContainerInfo(overlayId));

        page.Children.Add(ScrollbarInfo(CharacterCreationAppearancePage.ShadeScrollId));
        page.Children.Add(ContainerInfo(CharacterCreationAppearancePage.GradCircleId));

        var viewport = new ElementInfo
        {
            Id = CharacterCreationAppearancePage.ViewportId,
            Type = 0xDu,
            Width = 300f,
            Height = 300f,
        };
        page.Children.Add(viewport);

        page.Children.Add(ButtonInfo(CharacterCreationAppearancePage.RotateClockwiseId));
        page.Children.Add(ButtonInfo(CharacterCreationAppearancePage.RotateCounterClockwiseId));
        page.Children.Add(ZoomButtonInfo(CharacterCreationAppearancePage.ZoomInId));
        page.Children.Add(ZoomButtonInfo(CharacterCreationAppearancePage.ZoomOutId));

        var helpText = TextInfo(CharacterCreationAppearancePage.HelpTextId);
        var helpScrollInfo = new ElementInfo { Id = 0x100002E7u, Type = 11u, Width = 12f, Height = 40f };
        helpScrollInfo.StateMedia[""] = (0x06001919u, 1);
        helpText.Children.Add(helpScrollInfo);
        page.Children.Add(helpText);

        return page;
    }

    private static ElementInfo ZoomButtonInfo(uint id)
    {
        var info = new ElementInfo { Id = id, Type = 1u, Width = 81f, Height = 38f };
        info.StateMedia["Normal"] = (0x06004D55u, 1);
        info.StateMedia["Highlight"] = (0x06004D56u, 1);
        info.DefaultStateName = "Normal";
        return info;
    }

    private static ElementInfo SpinInfo(uint id)
    {
        var spin = new ElementInfo
        {
            Id = id,
            Type = 1u,
            Width = 200f,
            Height = 24f,
        };
        spin.Children.Add(new ElementInfo { Id = 0x1000030Au, Type = 1u, X = 80f, Width = 47f, Height = 24f });
        spin.Children.Add(new ElementInfo { Id = 0x1000030Bu, Type = 1u, X = 127f, Width = 47f, Height = 24f });
        return spin;
    }

    private static ElementInfo ArrowButtonInfo(uint id)
    {
        ElementInfo info = ButtonInfo(id);
        info.States[0x1000001Au] = new UiStateInfo { Id = 0x1000001Au, Name = "ArrowGhosted" };
        info.States[0x1000001Bu] = new UiStateInfo { Id = 0x1000001Bu, Name = "ArrowEnabled" };
        return info;
    }

    private static UiElement BuildSkillRowTemplate(uint templateElementId)
    {
        if (templateElementId == 0x100002FFu)
        {
            var row = new ElementInfo
            {
                Id = templateElementId,
                Type = 3u,
                Width = 280f,
                Height = 16f,
            };
            ElementInfo nameInfo = TextInfo(0x10000301u);
            nameInfo.FontColor = new System.Numerics.Vector4(218f / 255f, 167f / 255f, 85f / 255f, 1f);
            row.Children.Add(ContainerInfo(0x10000300u));   // unreferenced icon/backdrop
            row.Children.Add(nameInfo);                       // name
            row.Children.Add(TextInfo(0x10000302u));         // pSkillLevelText
            row.Children.Add(TextInfo(0x10000303u));         // pUpCostText
            row.Children.Add(ArrowButtonInfo(0x10000304u));  // pSkillUpButton
            row.Children.Add(ArrowButtonInfo(0x10000305u));  // pSkillDownButton
            row.Children.Add(TextInfo(0x10000306u));         // pDownCostText
            return LayoutImporter.Build(row, _ => (0u, 0, 0), null).Root;
        }

        var header = new ElementInfo
        {
            Id = templateElementId,
            Type = 3u,
            Width = 280f,
            Height = 16f,
        };
        header.Children.Add(ButtonInfo(0x100002F6u));
        return LayoutImporter.Build(header, _ => (0u, 0, 0), null).Root;
    }


    private const uint SummaryLineTemplateId = 0x100002F8u;
    private const uint SummaryHeaderTemplateId = 0x100002FAu;
    private const uint SummaryPairTemplateId = 0x100002FBu;

    private static ElementInfo BuildSummaryPage()
    {
        var page = new ElementInfo
        {
            Id = CharacterCreationUiController.SummaryPageElementId,
            Type = 3u,
            Width = 800f,
            Height = 500f,
        };

        var list = new ElementInfo
        {
            Id = CharacterCreationSummaryPage.ListBoxId,
            Type = 5u,
            X = 20f,
            Y = 40f,
            Width = 400f,
            Height = 300f,
        };
        list.TemplateList.Add(new UiTemplateListEntry(0x21000038u, SummaryLineTemplateId));
        list.TemplateList.Add(new UiTemplateListEntry(0x21000038u, SummaryHeaderTemplateId));
        list.TemplateList.Add(new UiTemplateListEntry(0x21000038u, SummaryPairTemplateId));
        page.Children.Add(list);

        page.Children.Add(ScrollbarInfo(CharacterCreationSummaryPage.ScrollId));

        ElementInfo nameFieldInfo = EditableFieldInfo(CharacterCreationSummaryPage.NameTextId);
        nameFieldInfo.X = 450f;
        nameFieldInfo.Y = 100f;
        page.Children.Add(nameFieldInfo);

        page.Children.Add(TextInfo(CharacterCreationSummaryPage.HowToTextId));

        var viewport = new ElementInfo
        {
            Id = CharacterCreationSummaryPage.ViewportId,
            Type = 0xDu,
            Width = 300f,
            Height = 300f,
        };
        page.Children.Add(viewport);

        return page;
    }

    private static UiElement BuildSummaryLineTemplate()
    {
        var root = new ElementInfo { Id = 0x90001u, Type = 3u, Width = 380f, Height = 16f };
        root.Children.Add(TextInfo(0x100002F9u));
        return LayoutImporter.Build(root, _ => (0u, 0, 0), null).Root;
    }

    private static UiElement BuildSummaryHeaderTemplate()
    {
        var root = new ElementInfo { Id = 0x90002u, Type = 3u, Width = 380f, Height = 18f };
        root.Children.Add(TextInfo(0x100000FEu));
        return LayoutImporter.Build(root, _ => (0u, 0, 0), null).Root;
    }

    private static UiElement BuildSummaryPairTemplate()
    {
        var root = new ElementInfo { Id = 0x90003u, Type = 3u, Width = 380f, Height = 16f };
        root.Children.Add(TextInfo(0x100002FCu));
        root.Children.Add(TextInfo(0x100002FDu));
        return LayoutImporter.Build(root, _ => (0u, 0, 0), null).Root;
    }

    private static ElementInfo ContainerInfo(uint id) => new()
    {
        Id = id,
        Type = 3u,
        Width = 200f,
        Height = 60f,
    };

    private static ElementInfo ButtonInfo(uint id) => new()
    {
        Id = id,
        Type = 1u,
        Width = 100f,
        Height = 30f,
    };

    private static ElementInfo TextInfo(uint id) => new()
    {
        Id = id,
        Type = 12u,
        Width = 200f,
        Height = 60f,
    };

    private static ElementInfo ScrollbarInfo(uint id) => new()
    {
        Id = id,
        Type = 11u,
        Width = 120f,
        Height = 12f,
    };

    private static ElementInfo EditableFieldInfo(uint id)
    {
        var info = new ElementInfo
        {
            Id = id,
            Type = 12u,
            Width = 40f,
            Height = 16f,
        };
        var state = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        state.Properties.Values[0x16u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Bool,
            BoolValue = true,
        };
        info.States[UiStateInfo.DirectStateId] = state;
        return info;
    }
}
