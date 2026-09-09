using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Chat;

namespace AcDream.App.Tests.UI.Layout;

public sealed class OptionsPanelControllerTests
{
    private static UiButton Click(ImportedLayout layout, uint elementId)
    {
        var button = Assert.IsType<UiButton>(layout.FindElement(elementId));
        button.OnEvent(new UiEvent(0, button, UiEventType.Click));
        return button;
    }

    private static OptionsPanelController.Callbacks MakeCallbacks(
        List<string> calls,
        Action<string>? displaySystemMessage = null)
        => new(
            Toggle: () => calls.Add("toggle"),
            RequestExitToCharacterSelection: () => calls.Add("exit-to-char-select"),
            ExitGame: () => calls.Add("exit-game"),
            UseMouseTurningSettings: () => calls.Add("mouse-turning"),
            DisplaySystemMessage: displaySystemMessage ?? (text => calls.Add($"message:{text}")));


    [Fact]
    public void Bind_RootBuildsAsUiTabPanel()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();

        Assert.IsType<UiTabPanel>(layout.Root);
    }

    [Fact]
    public void Bind_Succeeds_AndExposesFourEmptyPages()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();

        OptionsPanelController? controller =
            OptionsPanelController.Bind(layout, MakeCallbacks(calls));

        Assert.NotNull(controller);
        Assert.Equal(4, controller!.Pages.Count);
        Assert.Empty(controller.GameplayPage.Rows);
        Assert.Empty(controller.CharacterPage.Rows);
        Assert.Empty(controller.ChatPage.Rows);
        Assert.Empty(controller.ConfigPage.Rows);
    }

    private static List<UiSolidSpriteFill> CollectFooterBackings(UiElement root)
    {
        var found = new List<UiSolidSpriteFill>();
        Walk(root, found);
        return found;

        static void Walk(UiElement node, List<UiSolidSpriteFill> acc)
        {
            if (node is UiSolidSpriteFill fill) acc.Add(fill);
            foreach (UiElement child in node.Children) Walk(child, acc);
        }
    }

    [Fact]
    public void Bind_SynthesizesOneOpaqueFooterBacking_PerPageWithApplyResetDefaults()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();

        OptionsPanelController? controller = OptionsPanelController.Bind(
            layout, MakeCallbacks(calls), resolveSprite: _ => (1u, 8, 8));

        Assert.NotNull(controller);
        foreach (uint pageSlotId in new[] { 0x10000211u, 0x1000050Cu, 0x10000213u })
        {
            UiElement pageRoot = UiElement.FindDescendant(controller!.TabPanel, pageSlotId)!;
            Assert.NotNull(pageRoot);
            List<UiSolidSpriteFill> backings = CollectFooterBackings(pageRoot);
            UiSolidSpriteFill backing = Assert.Single(backings);

            Assert.Equal(RetailChromeSprites.CenterFill, backing.SpriteId);
            Assert.NotNull(backing.SpriteResolve);
            Assert.True(backing.ClickThrough);
            Assert.Equal(pageRoot.Width, backing.Width);

            Assert.All(pageRoot.Children.Where(c => !ReferenceEquals(c, backing)),
                sibling => Assert.True(backing.ZOrder < sibling.ZOrder));
        }
    }

    [Fact]
    public void ActivateTabs_SelectsGameplayAsDefault_ButNeverFlushesIt()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        int gameplayFlushCount = 0;
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            MakeCallbacks(calls) with { AfterApply = () => gameplayFlushCount++ })!;

        controller.ActivateTabs();

        Assert.True(controller.TabPanel.BehaviorActive);
        Assert.Equal(0x10000212u, controller.TabPanel.ActivePageElementId); // Gameplay slot
        Assert.Equal(0, gameplayFlushCount);
    }

    [Fact]
    public void TabSwitch_RevertsLeavingGameplayPage_AndAppliesEnteringCharacterPage()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        var flushes = new List<string>();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout, MakeCallbacks(calls) with { AfterApply = () => flushes.Add("flush") })!;
        controller.ActivateTabs();
        flushes.Clear(); // drop the initial-activation flush (Gameplay never flushes anyway)

        controller.TabPanel.SwitchTo(0x10000211u);

        Assert.Equal(0x10000211u, controller.TabPanel.ActivePageElementId);
        Assert.Equal(["flush"], flushes);
    }

    [Fact]
    public void ShowGameplay_UsesTheSameAuthoredTabStateAsAClick()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout, MakeCallbacks(calls))!;
        controller.ActivateTabs();
        controller.TabPanel.SwitchTo(0x10000211u);

        controller.ShowGameplay();

        Assert.True(controller.IsShowingGameplay);
        Assert.Equal(0x10000212u, controller.TabPanel.ActivePageElementId);
    }

    [Fact]
    public void WholeWindowHide_RevertsCurrentlyActivePage()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        OptionsPanelController controller =
            OptionsPanelController.Bind(layout, MakeCallbacks(calls))!;
        controller.ActivateTabs();
        var row = new BoolOptionRow(initial: false, defaultValue: false);
        controller.GameplayPage.Register(row);
        row.SetCurrentValue(true);
        Assert.True(controller.GameplayPage.Changed);

        controller.OnHidden();

        Assert.False(controller.GameplayPage.Changed);
    }

    [Fact]
    public void WholeWindowShow_AppliesCurrentlyActivePage()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        int flushCount = 0;
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout, MakeCallbacks(calls) with { AfterApply = () => flushCount++ })!;
        controller.ActivateTabs();
        controller.TabPanel.SwitchTo(0x10000211u);
        flushCount = 0;

        controller.OnShown();

        Assert.Equal(1, flushCount);
    }

    [Fact]
    public void GameplayPage_OnShownAndOnHidden_NeverFlush_EvenWhenControllerAfterApplyIsWired()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        int flushCount = 0;
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout, MakeCallbacks(calls) with { AfterApply = () => flushCount++ })!;

        controller.GameplayPage.OnShown();
        controller.GameplayPage.OnHidden();

        Assert.Equal(0, flushCount);
    }


    private static (UiButton Apply, UiButton Reset, UiButton Defaults) GetCharacterPageButtons(
        OptionsPanelController controller)
    {
        UiElement pageRoot = UiElement.FindDescendant(controller.Root, 0x10000211u)!;
        var apply = Assert.IsType<UiButton>(UiElement.FindDescendant(pageRoot, 0x100001FCu));
        var reset = Assert.IsType<UiButton>(UiElement.FindDescendant(pageRoot, 0x100001FDu));
        var defaults = Assert.IsType<UiButton>(UiElement.FindDescendant(pageRoot, 0x100001FEu));
        return (apply, reset, defaults);
    }

    [Fact]
    public void CharacterPage_ApplyAndReset_StartDisabled_OnFreshBind()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        OptionsPanelController controller =
            OptionsPanelController.Bind(layout, MakeCallbacks(calls))!;

        (UiButton apply, UiButton reset, _) = GetCharacterPageButtons(controller);

        Assert.False(apply.Enabled);
        Assert.False(reset.Enabled);
    }

    [Fact]
    public void CharacterPage_OneLedClick_EnablesApplyAndReset()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        OptionsPanelController controller =
            OptionsPanelController.Bind(layout, MakeCallbacks(calls))!;
        var row = new BoolOptionRow(initial: false, defaultValue: false);
        controller.CharacterPage.Register(row);
        (UiButton apply, UiButton reset, _) = GetCharacterPageButtons(controller);

        row.SetCurrentValue(true);

        Assert.True(apply.Enabled);
        Assert.True(reset.Enabled);
    }

    [Fact]
    public void CharacterPage_Apply_DisablesApplyAndResetAgain()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        OptionsPanelController controller =
            OptionsPanelController.Bind(layout, MakeCallbacks(calls))!;
        var row = new BoolOptionRow(initial: false, defaultValue: false);
        controller.CharacterPage.Register(row);
        (UiButton apply, UiButton reset, _) = GetCharacterPageButtons(controller);
        row.SetCurrentValue(true);

        controller.CharacterPage.Apply();

        Assert.False(apply.Enabled);
        Assert.False(reset.Enabled);
    }

    [Fact]
    public void CharacterPage_Defaults_LeavesApplyAndResetEnabled_WhenSomethingActuallyChanged()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        OptionsPanelController controller =
            OptionsPanelController.Bind(layout, MakeCallbacks(calls))!;
        var row = new BoolOptionRow(initial: false, defaultValue: true);
        controller.CharacterPage.Register(row);
        (UiButton apply, UiButton reset, _) = GetCharacterPageButtons(controller);

        controller.CharacterPage.Defaults();

        Assert.True(row.Current);
        Assert.True(controller.CharacterPage.Changed);
        Assert.True(apply.Enabled);
        Assert.True(reset.Enabled);
    }

    [Fact]
    public void CharacterPage_Defaults_IsNeverGated()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        OptionsPanelController controller =
            OptionsPanelController.Bind(layout, MakeCallbacks(calls))!;
        var row = new BoolOptionRow(initial: false, defaultValue: false);
        controller.CharacterPage.Register(row);
        (_, _, UiButton defaults) = GetCharacterPageButtons(controller);

        Assert.True(defaults.Enabled);

        row.SetCurrentValue(true);
        Assert.True(defaults.Enabled);

        controller.CharacterPage.Apply();
        Assert.True(defaults.Enabled);

        controller.CharacterPage.Defaults();
        Assert.True(defaults.Enabled);
    }


    [Fact]
    public void CloseButton_FiresToggleCallback()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        OptionsPanelController.Bind(layout, MakeCallbacks(calls));

        Click(layout, 0x10000210u);

        Assert.Equal(["toggle"], calls);
    }

    // ── The seven Gameplay-tab buttons ───────────────────────────────────────

    [Fact]
    public void ExitToCharacterSelectionButton_FiresRequestCallback()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        OptionsPanelController.Bind(layout, MakeCallbacks(calls));

        Click(layout, 0x10000203u);

        Assert.Equal(["exit-to-char-select"], calls);
    }

    [Fact]
    public void ExitGameButton_FiresExitGameCallback()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        OptionsPanelController.Bind(layout, MakeCallbacks(calls));

        Click(layout, 0x10000617u);

        Assert.Equal(["exit-game"], calls);
    }

    [Fact]
    public void UseMouseTurningSettingsButton_FiresMacroCallback()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        OptionsPanelController.Bind(layout, MakeCallbacks(calls));

        Click(layout, 0x100005CCu);

        Assert.Equal(["mouse-turning"], calls);
    }

    [Fact]
    public void UrgentAssistanceButton_DisplaysItsOwnByteVerifiedFailureText()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        OptionsPanelController.Bind(layout, MakeCallbacks(calls));

        Click(layout, 0x10000206u);

        Assert.Equal([$"message:{OptionsPanelText.UrgentAssistanceUnavailable}"], calls);
    }

    [Fact]
    public void ReportAbuseButton_DisplaysItsOwnByteVerifiedFailureText()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        OptionsPanelController.Bind(layout, MakeCallbacks(calls));

        Click(layout, 0x10000207u);

        Assert.Equal([$"message:{OptionsPanelText.ReportAbuseUnavailable}"], calls);
    }

    [Fact]
    public void UrgentAssistanceAndReportAbuse_UseDifferentText()
    {
        Assert.NotEqual(
            OptionsPanelText.UrgentAssistanceUnavailable,
            OptionsPanelText.ReportAbuseUnavailable);
        Assert.Contains(OptionsPanelText.SupportUrl, OptionsPanelText.UrgentAssistanceUnavailable);
        Assert.Contains(OptionsPanelText.SupportUrl, OptionsPanelText.ReportAbuseUnavailable);
    }

    [Fact]
    public void ConfigureKeyboardButton_IsInert_ClickDoesNothing()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        OptionsPanelController.Bind(layout, MakeCallbacks(calls));

        Click(layout, 0x10000204u);

        Assert.Empty(calls);
    }

    [Fact]
    public void InGameHelpFilesButton_IsInert_ClickDoesNothing()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        OptionsPanelController.Bind(layout, MakeCallbacks(calls));

        Click(layout, 0x10000205u);

        Assert.Empty(calls);
    }

    [Fact]
    public void AllSevenGameplayButtons_ResolveInTheBuiltLayout()
    {
        // Guards against a future fixture regeneration silently dropping an
        // id (a missing button degrades to a logged no-op, not a test
        // failure, unless asserted here).
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();

        uint[] buttonIds =
        {
            0x10000203u,
            0x10000204u,
            0x10000205u, // In-Game Help Files
            0x10000206u, // Urgent Assistance
            0x10000207u, // Report Abuse
            0x100005CCu, // Use Mouse Turning Settings
            0x10000617u, // Exit Game
        };

        foreach (uint id in buttonIds)
            Assert.IsType<UiButton>(layout.FindElement(id));
    }
}
