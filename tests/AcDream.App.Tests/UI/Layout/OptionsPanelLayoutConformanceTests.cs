using System.Linq;
using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public class OptionsPanelLayoutConformanceTests
{
    private static (uint, int, int) NoTex(uint _) => (0, 0, 0);

    private static ElementInfo? Find(ElementInfo n, uint id)
    {
        if (n.Id == id) return n;
        foreach (var c in n.Children)
        {
            var f = Find(c, id);
            if (f is not null) return f;
        }
        return null;
    }

    // ── Tab table (property 0x2E) ───────────────────────────────────────────

    [Fact]
    public void OptionsPanelFixture_TabControl_HasFourEntriesInAuthoredOrder_GameplayDefault()
    {
        var root = FixtureLoader.LoadOptionsPanelInfos();
        var tabControl = Find(root, 0x10000208u);
        Assert.NotNull(tabControl);
        Assert.Equal(8u, tabControl!.Type);

        var tabs = tabControl.TabTable;
        Assert.Equal(4, tabs.Count);

        Assert.Equal(new UiTabTableEntry(0x1000020Du, 0x10000212u, true), tabs[0]);  // Gameplay Options (default)
        Assert.Equal(new UiTabTableEntry(0x1000020Eu, 0x10000211u, false), tabs[1]);
        Assert.Equal(new UiTabTableEntry(0x1000050Bu, 0x1000050Cu, false), tabs[2]);
        Assert.Equal(new UiTabTableEntry(0x1000020Fu, 0x10000213u, false), tabs[3]);

        Assert.Single(tabs, t => t.IsDefault);
    }

    [Fact]
    public void OptionsPanelFixture_TabControl_BuildsAsUiTabPanel_WithSameTabTable()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        var tabControl = Assert.IsType<UiTabPanel>(layout.FindElement(0x10000208u));
        Assert.Equal(4, tabControl.Tabs.Count);
        Assert.True(tabControl.Tabs[0].IsDefault);
        Assert.Equal(0x10000212u, tabControl.Tabs[0].PageElementId);

        Assert.False(tabControl.BehaviorActive);
        Assert.Equal(0u, tabControl.ActivePageElementId);
    }

    // ── Row-template lists (property 0x64) ──────────────────────────────────

    [Fact]
    public void CharacterListBoxFixture_TemplateList_MatchesThreeRowTemplates()
    {
        var root = FixtureLoader.LoadOptionsCharacterInfos();
        var listBox = Find(root, 0x100001FAu);
        Assert.NotNull(listBox);
        Assert.Equal(5u, listBox!.Type);

        var templates = listBox.TemplateList;
        Assert.Equal(3, templates.Count);
        Assert.Equal(new UiTemplateListEntry(0x2100002Bu, 0x10000216u), templates[0]); // header
        Assert.Equal(new UiTemplateListEntry(0x2100002Bu, 0x10000217u), templates[1]); // separator
        Assert.Equal(new UiTemplateListEntry(0x2100002Bu, 0x10000218u), templates[2]); // toggle
    }

    [Fact]
    public void ConfigListBoxFixture_TemplateList_MatchesEightRowTemplates()
    {
        var root = FixtureLoader.LoadOptionsConfigInfos();
        var listBox = Find(root, 0x10000200u);
        Assert.NotNull(listBox);

        var templates = listBox!.TemplateList;
        Assert.Equal(8, templates.Count);
        uint[] expectedElementIds =
        {
            0x10000216u, 0x10000217u, 0x10000218u, 0x1000021Au,
            0x10000222u, 0x10000220u, 0x1000021Du, 0x10000221u,
        };
        for (int i = 0; i < expectedElementIds.Length; i++)
        {
            Assert.Equal(0x2100002Bu, templates[i].TemplateLayoutId);
            Assert.Equal(expectedElementIds[i], templates[i].TemplateElementId);
        }
    }

    [Fact]
    public void ChatListBoxFixture_TemplateList_MatchesNineRowTemplatesInclBitfield64()
    {
        var root = FixtureLoader.LoadOptionsChatInfos();
        var listBox = Find(root, 0x1000050Du);
        Assert.NotNull(listBox);

        var templates = listBox!.TemplateList;
        Assert.Equal(9, templates.Count);
        uint[] expectedElementIds =
        {
            0x10000216u, 0x10000217u, 0x10000218u, 0x1000021Au, 0x10000222u,
            0x10000220u, 0x1000021Du, 0x10000221u, 0x10000520u, // bitfield64
        };
        for (int i = 0; i < expectedElementIds.Length; i++)
        {
            Assert.Equal(0x2100002Bu, templates[i].TemplateLayoutId);
            Assert.Equal(expectedElementIds[i], templates[i].TemplateElementId);
        }
    }

    // ── Scrollbar linkage (property 0x72) ───────────────────────────────────

    [Fact]
    public void CharacterListBoxFixture_ScrollbarLinkage_Names0x100001FB()
    {
        var root = FixtureLoader.LoadOptionsCharacterInfos();
        var listBox = Find(root, 0x100001FAu);
        Assert.Equal(0x100001FBu, listBox!.ScrollbarElementId);
    }

    [Fact]
    public void ConfigListBoxFixture_ScrollbarLinkage_Names0x10000201()
    {
        var root = FixtureLoader.LoadOptionsConfigInfos();
        var listBox = Find(root, 0x10000200u);
        Assert.Equal(0x10000201u, listBox!.ScrollbarElementId);
    }

    [Fact]
    public void ChatListBoxFixture_ScrollbarLinkage_SharesElement0x10000201WithConfig()
    {
        var root = FixtureLoader.LoadOptionsChatInfos();
        var listBox = Find(root, 0x1000050Du);
        Assert.Equal(0x10000201u, listBox!.ScrollbarElementId);
    }

    // ── Built-widget mapping: ListBox → UiTemplateListBox ───────────────────

    [Fact]
    public void OptionsPanelFixture_CharacterListBox_BuildsAsUiTemplateListBoxWithSameData()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        var listBox = Assert.IsType<UiTemplateListBox>(layout.FindElement(0x100001FAu));
        Assert.Equal(3, listBox.Templates.Count);
        Assert.Equal(0x100001FBu, listBox.ScrollbarElementId);

        Assert.Empty(listBox.Children);
        Assert.Equal(0, listBox.ContentHeight);
    }

    [Fact]
    public void OptionsPanelFixture_CharacterScrollbar_StillBuildsAsUiScrollbar()
    {
        // Regression guard: a real scrollbar sibling (Type 11) must keep building
        // through the EXISTING BuildScrollbar mapping — OP2 must not disturb it.
        var layout = FixtureLoader.LoadOptionsPanel();
        Assert.IsType<UiScrollbar>(layout.FindElement(0x100001FBu));
    }

    // ── The four remaining UIOption_* widget mappings ───────────────────────

    [Fact]
    public void OptionsPanelFixture_SliderControl_BuildsAsHorizontalUiScrollbar()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        var slider = Assert.IsType<UiScrollbar>(layout.FindElement(0x1000021Cu));
        Assert.True(slider.Horizontal);
        Assert.NotEqual(0u, slider.ThumbSprite);
    }

    [Fact]
    public void OptionsPanelFixture_MenuControl_BuildsAsUiMenu()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        Assert.IsType<UiMenu>(layout.FindElement(0x10000224u));
    }

    [Fact]
    public void OptionsPanelFixture_ToggleSliderRow_ComposesCheckboxAndSlider()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        var row = Assert.IsType<UiOptionToggleSlider>(layout.FindElement(0x10000220u));
        Assert.NotNull(row.Toggle);
        Assert.NotNull(row.Slider);
        Assert.Contains(row.Toggle, row.Children);
        Assert.Contains(row.Slider, row.Children);
        Assert.Equal(0x10000219u, row.Toggle!.DatElementId);
        Assert.Equal(0x1000021Cu, row.Slider!.DatElementId);
    }

    [Fact]
    public void OptionsPanelFixture_LabelledToggleSliderRow_AlsoComposesCheckboxAndSlider()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        var row = Assert.IsType<UiOptionToggleSlider>(layout.FindElement(0x10000221u));
        Assert.NotNull(row.Toggle);
        Assert.NotNull(row.Slider);
    }

    [Fact]
    public void OptionsPanelFixture_Bitfield64Template_BuildsAsEmptyUiCheckboxBitfield64()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        var bitfield = Assert.IsType<UiCheckboxBitfield64>(layout.FindElement(0x10000520u));
        Assert.Empty(bitfield.Rows);
        UiTemplateListEntry template = Assert.Single(bitfield.Templates);
        Assert.Equal(new UiTemplateListEntry(0x2100002Bu, 0x10000521u), template);
    }

    [Fact]
    public void OptionsPanelFixture_LabelledSliderRow_StillPlainContainer()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        var row = layout.FindElement(0x1000021Du);
        Assert.IsNotType<UiOptionToggleSlider>(row);
        var slider = Assert.IsType<UiScrollbar>(layout.FindElement(0x1000021Cu));
        Assert.NotNull(slider);
    }

    // ── UiCheckboxBitfield64 behavior (rows built from the authored template) ──

    private static UiCheckboxBitfield64 NewBitfieldWithStubTemplate(
        uint checkedLedSprite = 0u, uint uncheckedLedSprite = 0u)
    {
        var templates = new[] { new UiTemplateListEntry(0x2100002Bu, 0x10000521u) };
        return new UiCheckboxBitfield64(templates, checkedLedSprite, uncheckedLedSprite)
        {
            Width = 272f,
            TemplateResolver = (layoutId, elementId) =>
            {
                Assert.Equal(0x2100002Bu, layoutId);
                Assert.Equal(0x10000521u, elementId);
                var checkboxInfo = new ElementInfo
                {
                    Id = UiCheckboxBitfield64.TemplateCheckboxElementId,
                    Type = 1u,
                    Width = 260f,
                    Height = 14f,
                };
                return LayoutImporter.Build(checkboxInfo, NoTex, null).Root;
            },
        };
    }

    [Fact]
    public void UiCheckboxBitfield64_AddChild_ResolvesRowFromAuthoredTemplate()
    {
        var bitfield = NewBitfieldWithStubTemplate();
        bitfield.SetDefaultValue(0x00000000_FBFFFFFFul, 0ul);

        UiButton? row = bitfield.AddChild(0x00000000_04000000ul, 0ul, "Error");
        Assert.NotNull(row);
        Assert.Equal(UiCheckboxBitfield64.TemplateCheckboxElementId, row!.DatElementId);
        Assert.False(row.Selected);

        ulong? changedLow = null;
        bitfield.ValueChanged += (low, _) => changedLow = low;
        row.OnClick!.Invoke();

        Assert.True(row.Selected);
        Assert.Equal(0x00000000_FFFFFFFFul, changedLow); // FBFFFFFF | 04000000 == FFFFFFFF

        row.OnClick!.Invoke();
        Assert.False(row.Selected);
        Assert.Equal(0x00000000_FBFFFFFFul, changedLow); // back to the seeded default
    }

    [Fact]
    public void UiCheckboxBitfield64_MultiBitMask_IsSet_UsesAnyBitNotAllBits()
    {
        var bitfield = NewBitfieldWithStubTemplate();
        const ulong combatMask = 0x00000000_00600040ul; // two bits
        const ulong onlyOneBitOfMask = 0x00000000_00000040ul; // partially overlapping
        bitfield.SetDefaultValue(onlyOneBitOfMask, 0ul);

        UiButton? row = bitfield.AddChild(combatMask, 0ul, "Combat");
        Assert.NotNull(row);
        Assert.True(row!.Selected);

        ulong? changedLow = null;
        bitfield.ValueChanged += (low, _) => changedLow = low;
        row.OnClick!.Invoke();
        Assert.False(row.Selected);
        Assert.Equal(0ul, changedLow);
    }

    [Fact]
    public void UiCheckboxBitfield64_AddChild_NoTemplateResolverWired_ReturnsNullAndLeavesNoRow()
    {
        var templates = new[] { new UiTemplateListEntry(0x2100002Bu, 0x10000521u) };
        var bitfield = new UiCheckboxBitfield64(templates) { Width = 272f };
        Assert.Null(bitfield.AddChild(0x1ul, 0ul, "Unwired"));
        Assert.Empty(bitfield.Rows);
    }


    private const uint CheckedLedDid = 0x06004D17u;
    private const uint UncheckedLedDid = 0x06004D19u;

    [Fact]
    public void AddChild_AllMaskBitsSet_AppliesCheckedLedSprite()
    {
        var bitfield = NewBitfieldWithStubTemplate(CheckedLedDid, UncheckedLedDid);
        const ulong combatMask = 0x00000000_00600040ul; // two bits
        bitfield.SetDefaultValue(combatMask, 0ul); // BOTH bits already set

        UiButton? row = bitfield.AddChild(combatMask, 0ul, "Combat");

        Assert.NotNull(row);
        Assert.True(row!.Selected);
        Assert.Equal(CheckedLedDid, row.FaceFileOverride);
    }

    [Fact]
    public void AddChild_PartialMaskBitsSet_AppliesUncheckedLedSprite()
    {
        var bitfield = NewBitfieldWithStubTemplate(CheckedLedDid, UncheckedLedDid);
        const ulong combatMask = 0x00000000_00600040ul; // two bits
        const ulong onlyOneBitOfMask = 0x00000000_00000040ul;
        bitfield.SetDefaultValue(onlyOneBitOfMask, 0ul);

        UiButton? row = bitfield.AddChild(combatMask, 0ul, "Combat");

        Assert.NotNull(row);
        Assert.True(row!.Selected);
        Assert.Equal(UncheckedLedDid, row.FaceFileOverride);
    }

    [Fact]
    public void AddChild_NoMaskBitsSet_LeavesLedOverrideUnset()
    {
        var bitfield = NewBitfieldWithStubTemplate(CheckedLedDid, UncheckedLedDid);
        bitfield.SetDefaultValue(0ul, 0ul);

        UiButton? row = bitfield.AddChild(0x00000000_00600040ul, 0ul, "Combat");

        Assert.NotNull(row);
        Assert.False(row!.Selected);
        Assert.Null(row.FaceFileOverride);
    }

    [Fact]
    public void ToggleRow_TogglingOff_ClearsLedOverride()
    {
        var bitfield = NewBitfieldWithStubTemplate(CheckedLedDid, UncheckedLedDid);
        const ulong singleBitMask = 0x00000000_00000040ul;
        bitfield.SetDefaultValue(singleBitMask, 0ul);

        UiButton row = bitfield.AddChild(singleBitMask, 0ul, "Combat")!;
        Assert.Equal(CheckedLedDid, row.FaceFileOverride);

        row.OnClick!.Invoke(); // turns OFF (was fully set)

        Assert.False(row.Selected);
        Assert.Null(row.FaceFileOverride);
    }

    [Fact]
    public void AddChild_MissingLedSprites_LeavesOverrideNull_EvenWhileChecked()
    {
        var bitfield = NewBitfieldWithStubTemplate();
        const ulong mask = 0x1ul;
        bitfield.SetDefaultValue(mask, 0ul);

        UiButton? row = bitfield.AddChild(mask, 0ul, "Row");

        Assert.NotNull(row);
        Assert.True(row!.Selected);
        Assert.Null(row.FaceFileOverride);
    }

    [Fact]
    public void AddChild_MultipleRows_SelfSizesHeightToStackedContent()
    {
        var bitfield = NewBitfieldWithStubTemplate();
        bitfield.SetDefaultValue(0ul, 0ul);
        Assert.Equal(0f, bitfield.Height);

        bitfield.AddChild(0x1ul, 0ul, "Row1");
        Assert.Equal(14f, bitfield.Height);

        bitfield.AddChild(0x2ul, 0ul, "Row2");
        bitfield.AddChild(0x4ul, 0ul, "Row3");

        Assert.Equal(42f, bitfield.Height); // 3 * 14
        Assert.Equal(272f, bitfield.Width); // Width (GetWidth()) is untouched
    }

    [Fact]
    public void SetCurrentValue_PushesValueAndVisuals_WithoutFiringValueChanged()
    {
        var bitfield = NewBitfieldWithStubTemplate(CheckedLedDid, UncheckedLedDid);
        const ulong mask = 0x1ul;
        bitfield.SetDefaultValue(0ul, 0ul); // starts unset
        UiButton row = bitfield.AddChild(mask, 0ul, "Row")!;
        Assert.False(row.Selected);

        bool valueChangedFired = false;
        bitfield.ValueChanged += (_, _) => valueChangedFired = true;

        bitfield.SetCurrentValue(mask, 0ul); // the OnShown/Reset/Defaults re-seed path

        Assert.Equal(mask, bitfield.CurrentLow);
        Assert.True(row.Selected);
        Assert.Equal(CheckedLedDid, row.FaceFileOverride);
        Assert.False(valueChangedFired); // a re-seed must never loop back as "user edited this"
    }


    [Fact]
    public void AddPrebuiltRow_StacksBelowPreviousContent_UsingTheRowsFinalHeight()
    {
        ElementInfo panelRoot = FixtureLoader.LoadOptionsPanelInfos();
        ElementInfo characterListBoxInfo = Find(FixtureLoader.LoadOptionsCharacterInfos(), 0x100001FAu)!;
        var listBox = new UiTemplateListBox(
            characterListBoxInfo, NoTex,
            characterListBoxInfo.TemplateList, characterListBoxInfo.ScrollbarElementId)
        {
            TemplateResolver = (layoutId, elementId) =>
            {
                if (layoutId != 0x2100002Bu) return null;
                ElementInfo? templateInfo = Find(panelRoot, elementId);
                return templateInfo is null ? null : LayoutImporter.Build(templateInfo, NoTex, null).Root;
            },
        };

        // A first ordinary row via the normal template path (header, 22px).
        UiElement? header = listBox.AddItemFromTemplateList(0);
        Assert.NotNull(header);
        Assert.Equal(22, listBox.ContentHeight);

        var prebuilt = new UiPanel { Width = 272f, Height = 260f };

        UiElement returned = listBox.AddPrebuiltRow(prebuilt);

        Assert.Same(prebuilt, returned);
        Assert.Equal(22f, prebuilt.Top); // stacked directly below the header row
        Assert.Equal(22 + 260, listBox.ContentHeight); // the FINAL 260px height, not a stale one
        Assert.Contains(prebuilt, listBox.Children.OfType<UiScrollablePanel>().Single().Children);
    }

    [Fact]
    public void AddPrebuiltRow_SubsequentTemplateRow_StacksAfterThePrebuiltRowsFinalHeight()
    {
        ElementInfo characterListBoxInfo = Find(FixtureLoader.LoadOptionsCharacterInfos(), 0x100001FAu)!;
        var listBox = new UiTemplateListBox(
            characterListBoxInfo, NoTex,
            characterListBoxInfo.TemplateList, characterListBoxInfo.ScrollbarElementId)
        {
            TemplateResolver = (layoutId, elementId) =>
            {
                if (layoutId != 0x2100002Bu) return null;
                ElementInfo? templateInfo = Find(FixtureLoader.LoadOptionsPanelInfos(), elementId);
                return templateInfo is null ? null : LayoutImporter.Build(templateInfo, NoTex, null).Root;
            },
        };

        var prebuilt = new UiPanel { Width = 272f, Height = 260f };
        listBox.AddPrebuiltRow(prebuilt);
        Assert.Equal(260, listBox.ContentHeight);

        // A separator row added AFTER the prebuilt block must stack below its FINAL
        // 260px height, not the block's pre-build authored 100px extent — this is
        // exactly the reflow problem AddPrebuiltRow exists to avoid.
        UiElement? separator = listBox.AddItemFromTemplateList(1); // separator template
        Assert.NotNull(separator);
        Assert.Equal(260f, separator!.Top);
    }

    // ── The template mechanism, exercised end-to-end ────────────────────────

    [Fact]
    public void CharacterListBox_TemplateMechanism_ReachesSixHeadersAndFortyNineToggles()
    {
        ElementInfo panelRoot = FixtureLoader.LoadOptionsPanelInfos();
        UiElement? Resolve(uint layoutId, uint elementId)
        {
            if (layoutId != 0x2100002Bu) return null;
            ElementInfo? templateInfo = Find(panelRoot, elementId);
            return templateInfo is null ? null : LayoutImporter.Build(templateInfo, NoTex, null).Root;
        }

        ElementInfo characterListBoxInfo = Find(FixtureLoader.LoadOptionsCharacterInfos(), 0x100001FAu)!;
        var listBox = new UiTemplateListBox(
            characterListBoxInfo, NoTex,
            characterListBoxInfo.TemplateList, characterListBoxInfo.ScrollbarElementId)
        {
            TemplateResolver = Resolve,
        };

        int[] sectionToggleCounts = { 3, 15, 6, 11, 7, 7 };
        int headerCalls = 0, toggleCalls = 0;
        foreach (int toggleCount in sectionToggleCounts)
        {
            var header = listBox.AddItemFromTemplateList(0); // header template
            Assert.IsType<UiText>(header);
            headerCalls++;

            for (int i = 0; i < toggleCount; i++)
            {
                var toggleRow = listBox.AddItemFromTemplateList(2); // toggle template
                Assert.NotNull(toggleRow);
                Assert.IsType<UiButton>(Assert.Single(toggleRow!.Children));
                toggleCalls++;
            }
        }

        Assert.Equal(6, headerCalls);
        Assert.Equal(49, toggleCalls);

        Assert.Equal(6 * 22 + 49 * 20, listBox.ContentHeight);
    }

    [Fact]
    public void TemplateListBox_AddItemFromTemplateList_WithoutResolver_ReturnsNull()
    {
        ElementInfo characterListBoxInfo = Find(FixtureLoader.LoadOptionsCharacterInfos(), 0x100001FAu)!;
        var listBox = new UiTemplateListBox(
            characterListBoxInfo, NoTex,
            characterListBoxInfo.TemplateList, characterListBoxInfo.ScrollbarElementId);
        Assert.Null(listBox.AddItemFromTemplateList(0));
        // OP2 rework: a failed/never-attempted add must not have allocated a viewport.
        Assert.Empty(listBox.Children);
    }

    [Fact]
    public void TemplateListBox_AddItemFromTemplateList_OutOfRangeIndex_ReturnsNull()
    {
        var listBox = new UiTemplateListBox(
            new ElementInfo(), NoTex, new[] { new UiTemplateListEntry(1u, 2u) }, 0u)
        {
            TemplateResolver = (_, _) => new UiDatElement(new ElementInfo(), NoTex),
        };
        Assert.Null(listBox.AddItemFromTemplateList(-1));
        Assert.Null(listBox.AddItemFromTemplateList(1));
        Assert.NotNull(listBox.AddItemFromTemplateList(0));
        Assert.Single(listBox.Children);
    }


    [Fact]
    public void UiTabPanel_ActivateTabBehavior_ShowsOnlyTheGameplayDefaultPage()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        var tabControl = Assert.IsType<UiTabPanel>(layout.FindElement(0x10000208u));

        tabControl.ActivateTabBehavior();

        Assert.True(tabControl.BehaviorActive);
        Assert.Empty(tabControl.UnresolvedEntries);
        Assert.Equal(0x10000212u, tabControl.ActivePageElementId); // Gameplay page slot

        UiElement gameplayPage = layout.FindElement(0x10000212u)!;
        UiElement characterPage = layout.FindElement(0x10000211u)!;
        UiElement chatPage = layout.FindElement(0x1000050Cu)!;
        UiElement configPage = layout.FindElement(0x10000213u)!;

        Assert.True(gameplayPage.Visible);
        Assert.False(characterPage.Visible);
        Assert.False(chatPage.Visible);
        Assert.False(configPage.Visible);
    }

    [Fact]
    public void UiTabPanel_ActivateTabBehavior_IsIdempotent()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        var tabControl = Assert.IsType<UiTabPanel>(layout.FindElement(0x10000208u));

        tabControl.ActivateTabBehavior();
        tabControl.SwitchTo(0x10000211u);
        tabControl.ActivateTabBehavior();

        Assert.Equal(0x10000211u, tabControl.ActivePageElementId);
    }

    [Fact]
    public void UiTabPanel_SwitchTo_ShowsExactlyOnePage()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        var tabControl = Assert.IsType<UiTabPanel>(layout.FindElement(0x10000208u));
        tabControl.ActivateTabBehavior();

        tabControl.SwitchTo(0x1000050Cu);

        UiElement gameplayPage = layout.FindElement(0x10000212u)!;
        UiElement characterPage = layout.FindElement(0x10000211u)!;
        UiElement chatPage = layout.FindElement(0x1000050Cu)!;
        UiElement configPage = layout.FindElement(0x10000213u)!;

        var visiblePages = new[] { gameplayPage, characterPage, chatPage, configPage }
            .Where(p => p.Visible)
            .ToList();
        Assert.Single(visiblePages);
        Assert.Same(chatPage, visiblePages[0]);
        Assert.Equal(0x1000050Cu, tabControl.ActivePageElementId);
    }

    [Fact]
    public void UiTabPanel_SwitchTo_UpdatesTabButtonOpenClosedState()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        var tabControl = Assert.IsType<UiTabPanel>(layout.FindElement(0x10000208u));
        tabControl.ActivateTabBehavior();

        var gameplayButton = Assert.IsType<UiText>(layout.FindElement(0x1000020Du));
        var characterButton = Assert.IsType<UiText>(layout.FindElement(0x1000020Eu));

        Assert.Equal(RetailUiStateIds.Open, gameplayButton.ActiveRetailStateId);
        Assert.Equal(RetailUiStateIds.Closed, characterButton.ActiveRetailStateId);

        tabControl.SwitchTo(0x10000211u);

        Assert.Equal(RetailUiStateIds.Closed, gameplayButton.ActiveRetailStateId);
        Assert.Equal(RetailUiStateIds.Open, characterButton.ActiveRetailStateId);
    }

    [Fact]
    public void UiTabPanel_ClickingTabButton_SwitchesActivePage()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        var tabControl = Assert.IsType<UiTabPanel>(layout.FindElement(0x10000208u));
        tabControl.ActivateTabBehavior();
        var characterButton = layout.FindElement(0x1000020Eu)!;

        characterButton.OnEvent(new UiEvent(0, characterButton, UiEventType.Click));

        Assert.Equal(0x10000211u, tabControl.ActivePageElementId);
        Assert.True(layout.FindElement(0x10000211u)!.Visible);
        Assert.False(layout.FindElement(0x10000212u)!.Visible);
    }

    [Fact]
    public void UiTabPanel_ClickingTabButton_BeforeActivation_DoesNothing()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        var tabControl = Assert.IsType<UiTabPanel>(layout.FindElement(0x10000208u));
        var characterButton = layout.FindElement(0x1000020Eu)!;

        characterButton.OnEvent(new UiEvent(0, characterButton, UiEventType.Click));

        Assert.Equal(0u, tabControl.ActivePageElementId);
        Assert.False(tabControl.BehaviorActive);
    }

    [Fact]
    public void UiTabPanel_SwitchToSameActivePage_IsANoOp()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        var tabControl = Assert.IsType<UiTabPanel>(layout.FindElement(0x10000208u));
        tabControl.ActivateTabBehavior();
        uint before = tabControl.ActivePageElementId;

        tabControl.SwitchTo(before);

        Assert.Equal(before, tabControl.ActivePageElementId);
        Assert.True(layout.FindElement(before)!.Visible);
    }


    [Fact]
    public void ActivePageChanged_FiresOnInitialActivation_WithOldEqualsZero()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        var tabControl = Assert.IsType<UiTabPanel>(layout.FindElement(0x10000208u));
        var transitions = new List<(uint Old, uint New)>();
        tabControl.ActivePageChanged += (oldId, newId) => transitions.Add((oldId, newId));

        tabControl.ActivateTabBehavior();

        Assert.Equal([(0u, 0x10000212u)], transitions); // Gameplay default, no prior page.
    }

    [Fact]
    public void ActivePageChanged_FiresOnEverySwitch_WithBothOldAndNew()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        var tabControl = Assert.IsType<UiTabPanel>(layout.FindElement(0x10000208u));
        tabControl.ActivateTabBehavior();
        var transitions = new List<(uint Old, uint New)>();
        tabControl.ActivePageChanged += (oldId, newId) => transitions.Add((oldId, newId));

        tabControl.SwitchTo(0x1000050Cu);

        Assert.Equal([(0x10000212u, 0x1000050Cu)], transitions);
    }

    [Fact]
    public void ActivePageChanged_DoesNotFireForANoOpSwitch()
    {
        var layout = FixtureLoader.LoadOptionsPanel();
        var tabControl = Assert.IsType<UiTabPanel>(layout.FindElement(0x10000208u));
        tabControl.ActivateTabBehavior();
        uint before = tabControl.ActivePageElementId;
        var transitions = new List<(uint Old, uint New)>();
        tabControl.ActivePageChanged += (oldId, newId) => transitions.Add((oldId, newId));

        tabControl.SwitchTo(before);

        Assert.Empty(transitions);
    }
}
