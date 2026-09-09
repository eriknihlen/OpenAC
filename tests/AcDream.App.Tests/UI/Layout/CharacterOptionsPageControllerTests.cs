using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.UI.Layout;

public sealed class CharacterOptionsPageControllerTests
{
    private static readonly CharacterOptionId[] NotOnCharacterTab =
    [
        CharacterOptionId.AppearOffline,
        CharacterOptionId.UseMouseTurning,
        CharacterOptionId.LockUI,
    ];

    private static IEnumerable<CharacterOptionsPageController.RowSpec> AllRows() =>
        CharacterOptionsPageController.Groups.SelectMany(static g => g.Rows);


    [Fact]
    public void Groups_HasSixGroups_InAuthoredHeaderOrder()
    {
        string[] expectedHeaders =
        [
            "ID_CharacterOption_UIBehavior_Section",
            "ID_CharacterOption_UIDisplay_Section",
            "ID_CharacterOption_Grouping_Section",
            "ID_CharacterOption_OtherPlayers_Section",
            "ID_CharacterOption_CharacterBehavior_Section",
            "ID_CharacterOption_Chat_Section",
        ];

        Assert.Equal(6, CharacterOptionsPageController.Groups.Length);
        Assert.Equal(
            expectedHeaders,
            CharacterOptionsPageController.Groups.Select(static g => g.HeaderKey));
    }

    [Fact]
    public void Groups_RowCountsPerGroup_Match3_15_6_11_7_8()
    {
        int[] expected = { 3, 15, 6, 11, 7, 8 };

        Assert.Equal(
            expected,
            CharacterOptionsPageController.Groups.Select(static g => g.Rows.Length));
    }

    [Fact]
    public void TotalRowCount_Is50()
    {
        Assert.Equal(50, CharacterOptionsPageController.TotalRowCount);
        Assert.Equal(50, AllRows().Count());
    }

    [Fact]
    public void EveryRow_ResolvesInCharacterOptionTable()
    {
        // "An invented row fails the build" — direction 1.
        foreach (CharacterOptionsPageController.RowSpec row in AllRows())
        {
            Assert.True(
                CharacterOptionTable.TryGet(row.Id, out _),
                $"{row.RetailName} (0x{(uint)row.Id:X2}) is authored on the Character tab "
                + "but missing from CharacterOptionTable.");
        }
    }

    [Fact]
    public void EveryRow_IsPairwiseDistinct()
    {
        var ids = AllRows().Select(static r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void EveryCharacterOptionTableId_ExceptTheThreeExcluded_HasExactlyOneRow()
    {
        var rowIds = AllRows().Select(static r => r.Id).ToHashSet();

        foreach (CharacterOptionTableEntry entry in CharacterOptionTable.All)
        {
            bool expectedOnTab = !NotOnCharacterTab.Contains(entry.Id);
            Assert.True(
                rowIds.Contains(entry.Id) == expectedOnTab,
                $"{entry.Id} (0x{(uint)entry.Id:X2}): expected authored-on-tab="
                + $"{expectedOnTab} but rowIds.Contains={rowIds.Contains(entry.Id)}.");
        }
    }

    [Theory]
    [InlineData(CharacterOptionId.AppearOffline)]
    [InlineData(CharacterOptionId.UseMouseTurning)]
    [InlineData(CharacterOptionId.LockUI)]
    public void ExcludedIds_HaveNoRow(CharacterOptionId excludedId)
    {
        Assert.DoesNotContain(AllRows(), r => r.Id == excludedId);
    }

    [Fact]
    public void HearPkDeathMessages_D3Row_IsLastInTheChatGroup()
    {
        CharacterOptionsPageController.RowSpec[] chatRows =
            CharacterOptionsPageController.Groups[5].Rows;
        Assert.Equal(
            CharacterOptionId.HearPkDeathMessages, chatRows[^1].Id);
        Assert.Equal("HearPKDeaths", chatRows[^1].RetailName);
    }

    [Fact]
    public void HearPkDeathMessages_RetailNameHash_MatchesByteVerifiedStringId()
    {
        Assert.Equal(
            0x0D16E9A3u,
            DatStringResolver.ComputeHash("ID_PlayerOption_HearPKDeaths"));
    }

    [Theory]
    [InlineData("ID_CharacterOption_UIBehavior_Section", 0x06489B6Eu)]
    [InlineData("ID_CharacterOption_UIDisplay_Section", 0x0A9BC99Eu)]
    [InlineData("ID_CharacterOption_Grouping_Section", 0x0CBAAFAEu)]
    [InlineData("ID_CharacterOption_OtherPlayers_Section", 0x0872DFFEu)]
    [InlineData("ID_CharacterOption_CharacterBehavior_Section", 0x08674D5Eu)]
    [InlineData("ID_CharacterOption_Chat_Section", 0x0987FE8Eu)]
    public void HeaderKey_RetailNameHash_MatchesByteVerifiedStringId(
        string headerKey, uint expectedHash)
    {
        Assert.Equal(expectedHash, DatStringResolver.ComputeHash(headerKey));
        Assert.Contains(headerKey, CharacterOptionsPageController.Groups.Select(g => g.HeaderKey));
    }

    [Theory]
    [MemberData(nameof(RetailEnumNameCases))]
    public void RetailName_MatchesVerbatimAcclientEnumSpelling(
        CharacterOptionId id, string expectedRetailName)
    {
        CharacterOptionsPageController.RowSpec row =
            AllRows().Single(r => r.Id == id);
        Assert.Equal(expectedRetailName, row.RetailName);
    }

    public static IEnumerable<object[]> RetailEnumNameCases()
    {
        yield return [CharacterOptionId.ListenToAllegianceChat, "HearAllegianceChat"];
        yield return [CharacterOptionId.ListenToGeneralChat, "HearGeneralChat"];
        yield return [CharacterOptionId.ListenToTradeChat, "HearTradeChat"];
        yield return [CharacterOptionId.ListenToLFGChat, "HearLFGChat"];
        yield return [CharacterOptionId.ListenToRoleplayChat, "HearRoleplayChat"];
        yield return [CharacterOptionId.ListenToSocietyChat, "HearSocietyChat"];
        yield return [CharacterOptionId.HearPkDeathMessages, "HearPKDeaths"];
        yield return [CharacterOptionId.ViewCombatTarget, "ViewCombatTarget"];
        yield return [CharacterOptionId.MainPackPreferred, "MainPackPreferred"];
        yield return [CharacterOptionId.AutoRepeatAttack, "AutoRepeatAttack"];
    }

    [Fact]
    public void AuthoredOrder_MatchesResearchDocRowByRow()
    {
        CharacterOptionId[][] expectedGroups =
        [
            [
                CharacterOptionId.ViewCombatTarget,
                CharacterOptionId.SalvageMultiple,
                CharacterOptionId.MainPackPreferred,
            ],
            [
                CharacterOptionId.VividTargetingIndicator,
                CharacterOptionId.ShowTooltips,
                CharacterOptionId.CoordinatesOnRadar,
                CharacterOptionId.SideBySideVitals,
                CharacterOptionId.SpellDuration,
                CharacterOptionId.DisableMostWeatherEffects,
                CharacterOptionId.DisableDistanceFog,
                CharacterOptionId.PersistentAtDay,
                CharacterOptionId.DisableHouseRestrictionEffects,
                CharacterOptionId.UseCraftSuccessDialog,
                CharacterOptionId.ConfirmVolatileRareUse,
                CharacterOptionId.DisplayTimeStamps,
                CharacterOptionId.FilterLanguage,
                CharacterOptionId.ShowHelm,
                CharacterOptionId.ShowCloak,
            ],
            [
                CharacterOptionId.IgnoreAllegianceRequests,
                CharacterOptionId.IgnoreFellowshipRequests,
                CharacterOptionId.DisplayAllegianceLogonNotifications,
                CharacterOptionId.FellowshipShareXP,
                CharacterOptionId.FellowshipShareLoot,
                CharacterOptionId.FellowshipAutoAcceptRequests,
            ],
            [
                CharacterOptionId.AcceptLootPermits,
                CharacterOptionId.UseDeception,
                CharacterOptionId.AllowGive,
                CharacterOptionId.IgnoreTradeRequests,
                CharacterOptionId.DragItemOnPlayerOpensSecureTrade,
                CharacterOptionId.DisplayDateOfBirth,
                CharacterOptionId.DisplayAge,
                CharacterOptionId.DisplayChessRank,
                CharacterOptionId.DisplayFishingSkill,
                CharacterOptionId.DisplayNumberDeaths,
                CharacterOptionId.DisplayNumberCharacterTitles,
            ],
            [
                CharacterOptionId.ToggleRun,
                CharacterOptionId.AdvancedCombatUI,
                CharacterOptionId.AutoTarget,
                CharacterOptionId.AutoRepeatAttack,
                CharacterOptionId.UseChargeAttack,
                CharacterOptionId.LeadMissileTargets,
                CharacterOptionId.UseFastMissiles,
            ],
            [
                CharacterOptionId.StayInChatMode,
                CharacterOptionId.ListenToAllegianceChat,
                CharacterOptionId.ListenToGeneralChat,
                CharacterOptionId.ListenToTradeChat,
                CharacterOptionId.ListenToLFGChat,
                CharacterOptionId.ListenToRoleplayChat,
                CharacterOptionId.ListenToSocietyChat,
                CharacterOptionId.HearPkDeathMessages,
            ],
        ];

        for (int g = 0; g < expectedGroups.Length; g++)
        {
            CharacterOptionId[] actual = CharacterOptionsPageController.Groups[g].Rows
                .Select(static r => r.Id).ToArray();
            Assert.Equal(expectedGroups[g], actual);
        }
    }


    private static (uint, int, int) NoTex(uint _) => (0, 0, 0);

    private static ElementInfo? Find(ElementInfo n, uint id)
    {
        if (n.Id == id) return n;
        foreach (ElementInfo c in n.Children)
        {
            ElementInfo? f = Find(c, id);
            if (f is not null) return f;
        }
        return null;
    }

    private static Func<uint, uint, UiElement?> MakeTemplateResolver()
    {
        ElementInfo panelRoot = FixtureLoader.LoadOptionsPanelInfos();
        return (layoutId, elementId) =>
        {
            if (layoutId != 0x2100002Bu) return null;
            ElementInfo? templateInfo = Find(panelRoot, elementId);
            return templateInfo is null ? null : LayoutImporter.Build(templateInfo, NoTex, null).Root;
        };
    }

    private sealed class FakeBindings
    {
        public Dictionary<CharacterOptionId, bool> Values { get; } = new();
        public List<(CharacterOptionId Id, bool Value)> Sets { get; } = new();

        public CharacterOptionsPageController.Bindings ToBindings() => new(
            CurrentValue: id => Values.TryGetValue(id, out bool v) && v,
            SetOption: (id, value) =>
            {
                Values[id] = value;
                Sets.Add((id, value));
            });
    }

    private static (OptionsPanelController Panel, FakeBindings Bindings, bool Bound) BindReal(
        Func<uint, uint, string?>? resolveString = null)
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        var calls = new List<string>();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => calls.Add("toggle"),
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;

        var fakeBindings = new FakeBindings();
        bool bound = CharacterOptionsPageController.Bind(
            layout,
            controller.CharacterPage,
            MakeTemplateResolver(),
            resolveString ?? ((_, _) => null),
            fakeBindings.ToBindings());

        return (controller, fakeBindings, bound);
    }

    [Fact]
    public void Bind_Succeeds_AndRegistersExactly50Rows()
    {
        (OptionsPanelController controller, _, bool bound) = BindReal();

        Assert.True(bound);
        Assert.Equal(50, controller.CharacterPage.Rows.Count);
    }

    [Fact]
    public void Bind_SeedsEveryRowFromCurrentValue()
    {
        var fakeBindings = new FakeBindings();
        // Seed a handful of ids ON; everything else defaults to off in the
        // fake's dictionary lookup.
        fakeBindings.Values[CharacterOptionId.ViewCombatTarget] = true;
        fakeBindings.Values[CharacterOptionId.IgnoreAllegianceRequests] = true;

        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        bool bound = CharacterOptionsPageController.Bind(
            layout,
            controller.CharacterPage,
            MakeTemplateResolver(),
            (_, _) => null,
            fakeBindings.ToBindings());
        Assert.True(bound);

        Assert.Empty(fakeBindings.Sets);
    }

    [Fact]
    public void ClickingARow_PublishesSetOption_WithTheAuthoredId()
    {
        (OptionsPanelController controller, FakeBindings bindings, _) = BindReal();

        IOptionRow row = Assert.Single(
            controller.CharacterPage.Rows.Skip(0).Take(1));
        var boolRow = Assert.IsType<BoolOptionRow>(row);

        boolRow.SetCurrentValue(true);

        Assert.Single(bindings.Sets);
    }

    [Fact]
    public void Apply_CommitsBaseline_AndReset_NoLongerReverts()
    {
        (OptionsPanelController controller, FakeBindings bindings, _) = BindReal();
        var row = Assert.IsType<BoolOptionRow>(controller.CharacterPage.Rows[0]);
        bool initial = row.Current;

        row.SetCurrentValue(!initial);
        Assert.True(controller.CharacterPage.Changed);

        controller.CharacterPage.Apply();
        Assert.False(controller.CharacterPage.Changed);
        Assert.Equal(!initial, row.Saved);

        controller.CharacterPage.Reset();
        Assert.Equal(!initial, row.Current);
    }

    [Fact]
    public void Reset_RevertsToSavedBaseline_AndRePublishesTheRevertedValue()
    {
        (OptionsPanelController controller, FakeBindings bindings, _) = BindReal();
        var row = Assert.IsType<BoolOptionRow>(controller.CharacterPage.Rows[0]);
        bool initial = row.Current;
        row.SetCurrentValue(!initial);
        bindings.Sets.Clear();

        controller.CharacterPage.Reset();

        Assert.Equal(initial, row.Current);
        Assert.Contains(bindings.Sets, s => s.Value == initial);
    }

    [Fact]
    public void Defaults_AppliesClientDefault_ForEveryRow_WithoutCommitting()
    {
        (OptionsPanelController controller, FakeBindings bindings, _) = BindReal();
        // Drive every row to the OPPOSITE of its own default first.
        foreach (IOptionRow r in controller.CharacterPage.Rows)
        {
            var b = (BoolOptionRow)r;
            b.SetCurrentValue(!b.DefaultValue);
        }
        bindings.Sets.Clear();

        controller.CharacterPage.Defaults();

        foreach (IOptionRow r in controller.CharacterPage.Rows)
        {
            var b = (BoolOptionRow)r;
            Assert.Equal(b.DefaultValue, b.Current);
        }
        Assert.True(controller.CharacterPage.Changed);
    }

    [Fact]
    public void EveryRow_DefaultValue_MatchesCharacterOptionTableClientDefault()
    {
        (OptionsPanelController controller, _, _) = BindReal();

        var rowsById = new Dictionary<CharacterOptionId, BoolOptionRow>();
        int i = 0;
        foreach (CharacterOptionsPageController.RowSpec spec in AllRows())
            rowsById[spec.Id] = (BoolOptionRow)controller.CharacterPage.Rows[i++];

        foreach ((CharacterOptionId id, BoolOptionRow row) in rowsById)
        {
            Assert.True(CharacterOptionTable.TryGet(id, out CharacterOptionTableEntry entry));
            Assert.Equal(entry.ClientDefault, row.DefaultValue);
        }
    }

    [Fact]
    public void TabHide_RevertsUncommittedCharacterEdits_ViaOnVisibilityChanged()
    {
        (OptionsPanelController controller, FakeBindings bindings, _) = BindReal();
        controller.ActivateTabs();
        controller.TabPanel.SwitchTo(0x10000211u);
        var row = Assert.IsType<BoolOptionRow>(controller.CharacterPage.Rows[0]);
        bool initial = row.Current;
        row.SetCurrentValue(!initial);
        Assert.True(controller.CharacterPage.Changed);

        controller.TabPanel.SwitchTo(0x10000212u); // Gameplay page slot

        Assert.Equal(initial, row.Current);
        Assert.False(controller.CharacterPage.Changed);
    }


    [Fact]
    public void OnShown_ReSeedsRow_FromLiveBindingValue_ChangedBehindItsBack()
    {
        (OptionsPanelController controller, FakeBindings bindings, _) = BindReal();
        CharacterOptionId id = AllRows().First().Id;
        var row = Assert.IsType<BoolOptionRow>(controller.CharacterPage.Rows[0]);
        bool initial = row.Current;

        bindings.Values[id] = !initial;
        bindings.Sets.Clear();

        controller.CharacterPage.OnShown();

        Assert.Equal(!initial, row.Current);
        Assert.Equal(!initial, row.Saved);
        Assert.False(controller.CharacterPage.Changed);
        Assert.Empty(bindings.Sets);
    }

    [Fact]
    public void OnShown_AllFiftyRows_ConvergeToTheLiveBindingSnapshot()
    {
        var fakeBindings = new FakeBindings();
        var random = new Random(20260811);
        foreach (CharacterOptionsPageController.RowSpec spec in AllRows())
            fakeBindings.Values[spec.Id] = random.Next(2) == 0;

        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        bool bound = CharacterOptionsPageController.Bind(
            layout,
            controller.CharacterPage,
            MakeTemplateResolver(),
            (_, _) => null,
            fakeBindings.ToBindings());
        Assert.True(bound);

        controller.CharacterPage.OnShown();

        int i = 0;
        foreach (CharacterOptionsPageController.RowSpec spec in AllRows())
        {
            var row = (BoolOptionRow)controller.CharacterPage.Rows[i++];
            Assert.Equal(fakeBindings.Values[spec.Id], row.Current);
            Assert.Equal(fakeBindings.Values[spec.Id], row.Saved);
        }
        Assert.False(controller.CharacterPage.Changed);
    }

    [Fact]
    public void Reset_AfterReseed_RestoresTheLiveValue_NotTheStaleConstructionDefault()
    {
        (OptionsPanelController controller, FakeBindings bindings, _) = BindReal();
        CharacterOptionId id = AllRows().First().Id;
        var row = Assert.IsType<BoolOptionRow>(controller.CharacterPage.Rows[0]);

        bindings.Values[id] = true;
        controller.CharacterPage.OnShown();
        bindings.Sets.Clear();

        row.SetCurrentValue(false);
        controller.CharacterPage.Reset();

        Assert.True(row.Current);
        Assert.Contains(bindings.Sets, s => s.Id == id && s.Value);
    }

    [Fact]
    public void ClickingTheRealCheckboxWidget_PublishesSetOption_ViaMouseDownUpClick()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        var fakeBindings = new FakeBindings();
        bool bound = CharacterOptionsPageController.Bind(
            layout, controller.CharacterPage, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());
        Assert.True(bound);

        var listBox = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(CharacterOptionsPageController.ListBoxElementId));
        var checkbox = Assert.IsType<UiButton>(
            UiElement.FindDescendant(listBox, 0x10000219u));
        CharacterOptionId id = AllRows().First().Id;
        Assert.False(checkbox.Selected);

        checkbox.OnEvent(new UiEvent(0, checkbox, UiEventType.MouseDown, Data1: 0, Data2: 0));
        checkbox.OnEvent(new UiEvent(0, checkbox, UiEventType.MouseUp, Data1: 0, Data2: 0));
        checkbox.OnEvent(new UiEvent(0, checkbox, UiEventType.Click));

        Assert.True(checkbox.Selected);
        var set = Assert.Single(fakeBindings.Sets);
        Assert.Equal(id, set.Id);
        Assert.True(set.Value);
    }

    [Fact]
    public void ScrollbarLinkage_ModelPointsAtTheListBoxScroll()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        var fakeBindings = new FakeBindings();
        CharacterOptionsPageController.Bind(
            layout, controller.CharacterPage, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());

        var listBox = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(CharacterOptionsPageController.ListBoxElementId));
        var scrollbar = Assert.IsType<UiScrollbar>(
            layout.FindElement(CharacterOptionsPageController.ScrollbarElementId));

        Assert.Same(listBox.Scroll, scrollbar.Model);
    }

    [Fact]
    public void Bind_MissingListBox_ReturnsFalse_AndDoesNotThrow()
    {
        var emptyRoot = new ElementInfo { Id = 0, Type = 3 };
        ImportedLayout emptyLayout = LayoutImporter.Build(emptyRoot, NoTex, null);
        var page = new OptionPage();

        bool bound = CharacterOptionsPageController.Bind(
            emptyLayout, page, MakeTemplateResolver(), (_, _) => null,
            new CharacterOptionsPageController.Bindings(
                CurrentValue: _ => false, SetOption: (_, _) => { }));

        Assert.False(bound);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public void LabelResolutionFailure_LeavesCheckboxLabelNull_NeverInventsEnglish()
    {
        (OptionsPanelController controller, _, bool bound) = BindReal(resolveString: (_, _) => null);

        Assert.True(bound);
        // Every row still registers (structural build succeeds) even
        // though every string lookup returns null — "no invented text"
        // degrades to "no text", never a fabricated label.
        Assert.Equal(50, controller.CharacterPage.Rows.Count);
    }


    private static readonly HashSet<CharacterOptionId> ExpectedStoreOnlyIds =
    [
        // Group 1 (UI Behavior) — SalvageMultiple (D), MainPackPreferred (B, unbound)
        CharacterOptionId.SalvageMultiple,
        CharacterOptionId.MainPackPreferred,
        // Group 2 (UI Display) — 10 of 15 (vitals round: SideBySideVitals is live)
        CharacterOptionId.ShowTooltips,
        CharacterOptionId.SpellDuration,
        CharacterOptionId.DisableMostWeatherEffects,
        CharacterOptionId.PersistentAtDay,
        CharacterOptionId.DisableHouseRestrictionEffects,
        CharacterOptionId.UseCraftSuccessDialog,
        CharacterOptionId.ConfirmVolatileRareUse,
        CharacterOptionId.FilterLanguage,
        CharacterOptionId.ShowHelm,
        CharacterOptionId.ShowCloak,
        CharacterOptionId.IgnoreAllegianceRequests,
        CharacterOptionId.IgnoreFellowshipRequests,
        CharacterOptionId.DisplayAllegianceLogonNotifications,
        CharacterOptionId.FellowshipShareLoot,
        CharacterOptionId.FellowshipAutoAcceptRequests,
        // Group 4 (Other Players) — 10 of 11
        CharacterOptionId.AcceptLootPermits,
        CharacterOptionId.UseDeception,
        CharacterOptionId.AllowGive,
        CharacterOptionId.IgnoreTradeRequests,
        CharacterOptionId.DisplayDateOfBirth,
        CharacterOptionId.DisplayAge,
        CharacterOptionId.DisplayChessRank,
        CharacterOptionId.DisplayFishingSkill,
        CharacterOptionId.DisplayNumberDeaths,
        CharacterOptionId.DisplayNumberCharacterTitles,
        CharacterOptionId.AdvancedCombatUI,
        CharacterOptionId.UseChargeAttack,
        CharacterOptionId.LeadMissileTargets,
        CharacterOptionId.UseFastMissiles,
        CharacterOptionId.StayInChatMode,
        CharacterOptionId.HearPkDeathMessages,
    ];

    [Fact]
    public void StoreOnlyRows_MatchTheDerivationTableExactly()
    {
        HashSet<CharacterOptionId> actualStoreOnly = AllRows()
            .Where(static r => r.StoreOnly)
            .Select(static r => r.Id)
            .ToHashSet();

        Assert.Equal(ExpectedStoreOnlyIds, actualStoreOnly);
        Assert.Equal(33, actualStoreOnly.Count);
        Assert.Equal(17, 50 - actualStoreOnly.Count); // the 17 live rows (vitals round: SideBySideVitals un-dimmed)
    }

    [Fact]
    public void Bind_AppliesDimmedCaptionColor_ForStoreOnlyRows_AndWhiteForLiveRows()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        var fakeBindings = new FakeBindings();
        bool bound = CharacterOptionsPageController.Bind(
            layout, controller.CharacterPage, MakeTemplateResolver(), (_, _) => "x",
            fakeBindings.ToBindings());
        Assert.True(bound);

        var listBox = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(CharacterOptionsPageController.ListBoxElementId));
        UiElement viewport = Assert.Single(listBox.Children);

        const uint ToggleCheckboxElementId = 0x10000219u;
        List<UiButton> checkboxesInOrder = viewport.Children
            .Select(item => UiElement.FindDescendant(item, ToggleCheckboxElementId) as UiButton)
            .Where(static cb => cb is not null)
            .Select(static cb => cb!)
            .ToList();
        Assert.Equal(50, checkboxesInOrder.Count);

        List<CharacterOptionsPageController.RowSpec> specsInOrder = AllRows().ToList();
        Assert.Equal(50, specsInOrder.Count);

        for (int i = 0; i < 50; i++)
        {
            CharacterOptionsPageController.RowSpec spec = specsInOrder[i];
            Vector4 expected = spec.StoreOnly ? UiRenderContext.StoreOnlyCaptionColor : Vector4.One;
            Assert.True(
                expected == checkboxesInOrder[i].LabelColor,
                $"row {i} ({spec.RetailName}, 0x{(uint)spec.Id:X2}): expected "
                + $"{(spec.StoreOnly ? "DIMMED" : "LIVE")} caption color {expected} but the "
                + $"built checkbox rendered {checkboxesInOrder[i].LabelColor}.");
        }
    }
}
