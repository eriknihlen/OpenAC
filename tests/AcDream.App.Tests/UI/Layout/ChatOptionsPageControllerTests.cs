using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Chat;

namespace AcDream.App.Tests.UI.Layout;

public sealed class ChatOptionsPageControllerTests
{

    [Fact]
    public void FilterRows_Has13EntriesInByteVerifiedAuthoredOrder()
    {
        (ulong Mask, string Key)[] expected =
        {
            (0x0000000083912021ul, "ID_ChatOption_TextFilter_Gameplay"),
            (0x0000000000600040ul, "ID_ChatOption_TextFilter_Combat"),
            (0x0000000000020080ul, "ID_ChatOption_TextFilter_Magic"),
            (0x0000000000001004ul, "ID_ChatOption_TextFilter_AreaSpeech"),
            (0x0000000000000018ul, "ID_ChatOption_TextFilter_Tells"),
            (0x0000000000040C00ul, "ID_ChatOption_TextFilter_Allegience"),
            (0x0000000000080000ul, "ID_ChatOption_TextFilter_Fellowship"),
            (0x0000000008000000ul, "ID_ChatOption_TextFilter_General"),
            (0x0000000010000000ul, "ID_ChatOption_TextFilter_Trade"),
            (0x0000000020000000ul, "ID_ChatOption_TextFilter_LFG"),
            (0x0000000040000000ul, "ID_ChatOption_TextFilter_Roleplay"),
            (0x0000000100000000ul, "ID_ChatOption_TextFilter_Society"),
            (0x0000000004000000ul, "ID_ChatOption_TextFilter_Error"),
        };

        Assert.Equal(13, ChatOptionsPageController.FilterRows.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Mask, ChatOptionsPageController.FilterRows[i].Mask);
            Assert.Equal(expected[i].Key, ChatOptionsPageController.FilterRows[i].RetailLabelKey);
        }
    }

    [Fact]
    public void FilterRows_EveryMaskIsPairwiseDistinct_AndNonZero()
    {
        var masks = ChatOptionsPageController.FilterRows.Select(static r => r.Mask).ToList();
        Assert.Equal(masks.Count, masks.Distinct().Count());
        Assert.All(masks, m => Assert.NotEqual(0ul, m));
    }

    [Fact]
    public void FilterBlocks_Has5EntriesInAuthoredOrder_WithRetailWindowIds()
    {
        (int RetailId, int CompactId, string HeaderKey, ulong Default, bool IncludesGameplay)[] expected =
        {
            (8, ChatWindowState.MainWindowId, "ID_ChatOption_MainChatWindow_Section",
                0xFBFFFFFFul, false),
            (2, 1, "ID_ChatOption_FloatyChatWindow1_Section", 0x0000101Cul, true),
            (3, 2, "ID_ChatOption_FloatyChatWindow2_Section", 0x00040C00ul, true),
            (4, 3, "ID_ChatOption_FloatyChatWindow3_Section", 0x00080000ul, true),
            (5, 4, "ID_ChatOption_FloatyChatWindow4_Section", 0x78000000ul, true),
        };

        Assert.Equal(5, ChatOptionsPageController.FilterBlocks.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            ChatOptionsPageController.FilterBlockSpec spec = ChatOptionsPageController.FilterBlocks[i];
            Assert.Equal(expected[i].RetailId, spec.RetailWindowId);
            Assert.Equal(expected[i].CompactId, spec.CompactWindowId);
            Assert.Equal(expected[i].HeaderKey, spec.HeaderKey);
            Assert.Equal(expected[i].Default, spec.DefaultFilter);
            Assert.Equal(expected[i].IncludesGameplay, spec.IncludesGameplayRow);
        }
    }

    [Fact]
    public void FilterBlocks_DefaultsMatchChatWindowStateNamedConstants()
    {
        Assert.Equal(ChatWindowState.MainWindowDefaultFilter, ChatOptionsPageController.FilterBlocks[0].DefaultFilter);
        Assert.Equal(ChatWindowState.Floaty1DefaultFilter, ChatOptionsPageController.FilterBlocks[1].DefaultFilter);
        Assert.Equal(ChatWindowState.Floaty2DefaultFilter, ChatOptionsPageController.FilterBlocks[2].DefaultFilter);
        Assert.Equal(ChatWindowState.Floaty3DefaultFilter, ChatOptionsPageController.FilterBlocks[3].DefaultFilter);
        Assert.Equal(ChatWindowState.Floaty4DefaultFilter, ChatOptionsPageController.FilterBlocks[4].DefaultFilter);
    }


    [Theory]
    [InlineData("ID_ChatOption_TextFilter_Gameplay")]
    [InlineData("ID_ChatOption_TextFilter_Combat")]
    [InlineData("ID_ChatOption_TextFilter_Magic")]
    [InlineData("ID_ChatOption_TextFilter_AreaSpeech")]
    [InlineData("ID_ChatOption_TextFilter_Tells")]
    [InlineData("ID_ChatOption_TextFilter_Allegience")]
    [InlineData("ID_ChatOption_TextFilter_Fellowship")]
    [InlineData("ID_ChatOption_TextFilter_General")]
    [InlineData("ID_ChatOption_TextFilter_Trade")]
    [InlineData("ID_ChatOption_TextFilter_LFG")]
    [InlineData("ID_ChatOption_TextFilter_Roleplay")]
    [InlineData("ID_ChatOption_TextFilter_Society")]
    [InlineData("ID_ChatOption_TextFilter_Error")]
    public void EveryFilterRowKey_IsAuthoredOnTheFilterRowsTable(string key)
    {
        Assert.Contains(ChatOptionsPageController.FilterRows, r => r.RetailLabelKey == key);
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
        public float DefaultOpacity = 0.5f;
        public float ActiveOpacity = 1.0f;
        public float DefaultOpacityDatDefault = 0.5f;
        public float ActiveOpacityDatDefault = 1.0f;
        public ChatOptionsDatCaptions.Caption DefaultOpacityCaption =
            new("Inactive Opacity", "Adjusts the opacity of the chat window when it is inactive");
        public ChatOptionsDatCaptions.Caption ActiveOpacityCaption =
            new("Active Opacity", "Adjusts the opacity of the chat window when it is active");
        public List<float> DefaultOpacitySets { get; } = new();
        public List<float> ActiveOpacitySets { get; } = new();
        public int OpacityFlushes { get; private set; }

        public Dictionary<int, ulong> Filters { get; } = new()
        {
            [ChatWindowState.MainWindowId] = ChatWindowState.MainWindowDefaultFilter,
            [1] = ChatWindowState.Floaty1DefaultFilter,
            [2] = ChatWindowState.Floaty2DefaultFilter,
            [3] = ChatWindowState.Floaty3DefaultFilter,
            [4] = ChatWindowState.Floaty4DefaultFilter,
        };
        public List<(int WindowId, ulong Value)> FilterSets { get; } = new();

        public ChatOptionsPageController.Bindings ToBindings() => new(
            CurrentDefaultOpacity: () => DefaultOpacity,
            CurrentActiveOpacity: () => ActiveOpacity,
            SetDefaultOpacity: value =>
            {
                (DefaultOpacity, ActiveOpacity) = ChatOpacityLinkFor(ActiveOpacity, value, isDefault: true);
                DefaultOpacitySets.Add(value);
            },
            SetActiveOpacity: value =>
            {
                (DefaultOpacity, ActiveOpacity) = ChatOpacityLinkFor(DefaultOpacity, value, isDefault: false);
                ActiveOpacitySets.Add(value);
            },
            FlushOpacity: () => OpacityFlushes++,
            DefaultOpacityDatDefault: DefaultOpacityDatDefault,
            ActiveOpacityDatDefault: ActiveOpacityDatDefault,
            DefaultOpacityCaption: DefaultOpacityCaption,
            ActiveOpacityCaption: ActiveOpacityCaption,
            CurrentFilter: windowId => Filters[windowId],
            SetFilter: (windowId, value) =>
            {
                Filters[windowId] = value;
                FilterSets.Add((windowId, value));
            });

        private static (float Default, float Active) ChatOpacityLinkFor(
            float other, float value, bool isDefault)
        {
            value = Math.Clamp(value, 0f, 1f);
            if (isDefault)
                return (value, other < value ? value : other);
            return (other > value ? value : other, value);
        }
    }

    private static (OptionsPanelController Panel, FakeBindings Bindings, bool Bound) BindReal(
        Func<uint, uint, string?>? resolveString = null)
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
        bool bound = ChatOptionsPageController.Bind(
            layout,
            controller.ChatPage,
            MakeTemplateResolver(),
            resolveString ?? ((_, _) => null),
            fakeBindings.ToBindings());

        return (controller, fakeBindings, bound);
    }

    [Fact]
    public void Bind_Succeeds_AndRegistersExactlySevenRows()
    {
        (OptionsPanelController controller, _, bool bound) = BindReal();

        Assert.True(bound);
        Assert.Equal(7, controller.ChatPage.Rows.Count);
    }

    [Fact]
    public void Bind_MainWindowBlock_Has12Rows_FloatyBlocks_Have13Rows()
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
        bool bound = ChatOptionsPageController.Bind(
            layout, controller.ChatPage, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());
        Assert.True(bound);

        var listBox = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(ChatOptionsPageController.ListBoxElementId));
        List<UiCheckboxBitfield64> blocks = CollectBlocks(listBox);

        Assert.Equal(5, blocks.Count);
        Assert.Equal(12, blocks[0].Rows.Count); // main window — no Gameplay row
        for (int i = 1; i < 5; i++)
            Assert.Equal(13, blocks[i].Rows.Count); // the four floaties
    }

    [Fact]
    public void Bind_FilterBlocks_ResolveTheAuthoredLedSprites()
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
        ChatOptionsPageController.Bind(
            layout, controller.ChatPage, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());

        var listBox = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(ChatOptionsPageController.ListBoxElementId));
        List<UiCheckboxBitfield64> blocks = CollectBlocks(listBox);

        Assert.Equal(5, blocks.Count);
        Assert.All(blocks, b =>
        {
            Assert.Equal(0x06004D17u, b.CheckedLedSprite);
            Assert.Equal(0x06004D19u, b.UncheckedLedSprite);
        });
    }

    [Fact]
    public void Bind_FilterBlocks_SelfSizeToFitTheirRows()
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
        ChatOptionsPageController.Bind(
            layout, controller.ChatPage, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());

        var listBox = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(ChatOptionsPageController.ListBoxElementId));
        List<UiCheckboxBitfield64> blocks = CollectBlocks(listBox);

        Assert.All(blocks, b => Assert.True(b.Height > 100f, $"block Height={b.Height}"));
        Assert.Equal(272f, blocks[0].Width); // width (GetWidth()) untouched
    }

    private static List<UiCheckboxBitfield64> CollectBlocks(UiElement root)
    {
        var found = new List<UiCheckboxBitfield64>();
        Walk(root, found);
        return found;

        static void Walk(UiElement node, List<UiCheckboxBitfield64> acc)
        {
            if (node is UiCheckboxBitfield64 block) acc.Add(block);
            foreach (UiElement child in node.Children) Walk(child, acc);
        }
    }

    private static List<UiScrollbar> CollectScalarSliders(UiElement listBoxRoot)
    {
        var found = new List<UiScrollbar>();
        Walk(listBoxRoot, found);
        return found;

        static void Walk(UiElement node, List<UiScrollbar> acc)
        {
            if (node is UiScrollbar { Horizontal: true } bar) acc.Add(bar);
            foreach (UiElement child in node.Children) Walk(child, acc);
        }
    }

    [Fact]
    public void Bind_SeedsSlidersFromCurrentOpacity_NotTheDatDefault()
    {
        var fakeBindings = new FakeBindings { DefaultOpacity = 0.25f, ActiveOpacity = 0.75f };
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        bool bound = ChatOptionsPageController.Bind(
            layout, controller.ChatPage, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());
        Assert.True(bound);

        var defaultRow = Assert.IsType<FloatOptionRow>(controller.ChatPage.Rows[0]);
        var activeRow = Assert.IsType<FloatOptionRow>(controller.ChatPage.Rows[1]);
        Assert.Equal(0.25f, defaultRow.Current);
        Assert.Equal(0.75f, activeRow.Current);
        // Never wrote back just from seeding.
        Assert.Empty(fakeBindings.DefaultOpacitySets);
        Assert.Empty(fakeBindings.ActiveOpacitySets);
    }

    [Fact]
    public void Bind_WiresEachSlidersOwnRowCaption_FromTheResolvedDatCatalog()
    {
        var fakeBindings = new FakeBindings
        {
            DefaultOpacityCaption = new ChatOptionsDatCaptions.Caption("Inactive Opacity", "inactive tip"),
            ActiveOpacityCaption = new ChatOptionsDatCaptions.Caption("Active Opacity", "active tip"),
        };
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        bool bound = ChatOptionsPageController.Bind(
            layout, controller.ChatPage, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());
        Assert.True(bound);

        var listBox = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(ChatOptionsPageController.ListBoxElementId));
        List<UiScrollbar> sliders = CollectScalarSliders(listBox);
        Assert.Equal(2, sliders.Count); // build order: [0]=Default, [1]=Active

        UiElement defaultRowRoot = FindRowRoot(sliders[0]);
        UiElement activeRowRoot = FindRowRoot(sliders[1]);

        var defaultCaption = Assert.IsType<UiText>(
            UiElement.FindDescendant(defaultRowRoot, 0x1000021Bu));
        var activeCaption = Assert.IsType<UiText>(
            UiElement.FindDescendant(activeRowRoot, 0x1000021Bu));

        Assert.Equal("Inactive Opacity", Assert.Single(defaultCaption.LinesProvider()).Text);
        Assert.Equal("Active Opacity", Assert.Single(activeCaption.LinesProvider()).Text);

        // The tooltip attaches to the interactive slider itself, per-row.
        Assert.Equal("inactive tip", sliders[0].TooltipText);
        Assert.Equal("active tip", sliders[1].TooltipText);
    }

    [Fact]
    public void Bind_MissingCaption_RendersNoText_NeverInventsEnglish()
    {
        var fakeBindings = new FakeBindings
        {
            DefaultOpacityCaption = default, // both Name/Tooltip null
            ActiveOpacityCaption = default,
        };
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        bool bound = ChatOptionsPageController.Bind(
            layout, controller.ChatPage, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());
        Assert.True(bound);

        var listBox = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(ChatOptionsPageController.ListBoxElementId));
        List<UiScrollbar> sliders = CollectScalarSliders(listBox);
        UiElement defaultRowRoot = FindRowRoot(sliders[0]);

        var defaultCaption = Assert.IsType<UiText>(
            UiElement.FindDescendant(defaultRowRoot, 0x1000021Bu));
        Assert.Empty(defaultCaption.LinesProvider());
        Assert.Null(sliders[0].TooltipText);
    }

    private static UiElement FindRowRoot(UiElement leaf)
    {
        UiElement node = leaf;
        while (node.Parent is { } parent && parent is not UiScrollablePanel)
            node = parent;
        return node;
    }

    [Fact]
    public void DraggingDefaultSlider_AppliesLive_AndDragsActiveUp_NeverClamping()
    {
        var fakeBindings = new FakeBindings { DefaultOpacity = 0.3f, ActiveOpacity = 0.3f };
        (OptionsPanelController controller, FakeBindings bindings, bool bound) =
            BindRealWith(fakeBindings);
        Assert.True(bound);
        var defaultRow = Assert.IsType<FloatOptionRow>(controller.ChatPage.Rows[0]);
        var activeRow = Assert.IsType<FloatOptionRow>(controller.ChatPage.Rows[1]);

        defaultRow.SetCurrentValue(0.9f);

        Assert.Equal(0.9f, bindings.DefaultOpacity);
        Assert.Equal(0.9f, bindings.ActiveOpacity); // dragged up
        Assert.Equal(0.9f, defaultRow.Current);
        Assert.Equal(0.9f, activeRow.Current); // the OTHER row's own value moved too
        Assert.Single(bindings.DefaultOpacitySets);
        Assert.True(controller.ChatPage.Changed);
    }

    [Fact]
    public void DraggingActiveSliderBelowDefault_DragsDefaultDown()
    {
        var fakeBindings = new FakeBindings { DefaultOpacity = 0.6f, ActiveOpacity = 0.6f };
        (OptionsPanelController controller, FakeBindings bindings, _) = BindRealWith(fakeBindings);
        var defaultRow = Assert.IsType<FloatOptionRow>(controller.ChatPage.Rows[0]);
        var activeRow = Assert.IsType<FloatOptionRow>(controller.ChatPage.Rows[1]);

        activeRow.SetCurrentValue(0.1f);

        Assert.Equal(0.1f, bindings.ActiveOpacity);
        Assert.Equal(0.1f, bindings.DefaultOpacity); // dragged down
        Assert.Equal(0.1f, defaultRow.Current);
        Assert.Equal(0.1f, activeRow.Current);
    }

    [Fact]
    public void OnShown_ReSeedsBothSliders_FromLiveOpacity()
    {
        var fakeBindings = new FakeBindings { DefaultOpacity = 0.5f, ActiveOpacity = 1.0f };
        (OptionsPanelController controller, FakeBindings bindings, _) = BindRealWith(fakeBindings);

        bindings.DefaultOpacity = 0.2f;
        bindings.ActiveOpacity = 0.9f;

        controller.ChatPage.OnShown();

        var defaultRow = Assert.IsType<FloatOptionRow>(controller.ChatPage.Rows[0]);
        var activeRow = Assert.IsType<FloatOptionRow>(controller.ChatPage.Rows[1]);
        Assert.Equal(0.2f, defaultRow.Current);
        Assert.Equal(0.2f, defaultRow.Saved);
        Assert.Equal(0.9f, activeRow.Current);
        Assert.Equal(0.9f, activeRow.Saved);
        Assert.False(controller.ChatPage.Changed);
    }

    [Fact]
    public void Defaults_RestoresDatExtractedSliderDefaults()
    {
        var fakeBindings = new FakeBindings
        {
            DefaultOpacity = 0.1f,
            ActiveOpacity = 0.1f,
            DefaultOpacityDatDefault = 0.5f,
            ActiveOpacityDatDefault = 1.0f,
        };
        (OptionsPanelController controller, FakeBindings bindings, _) = BindRealWith(fakeBindings);

        controller.ChatPage.Defaults();

        var defaultRow = Assert.IsType<FloatOptionRow>(controller.ChatPage.Rows[0]);
        var activeRow = Assert.IsType<FloatOptionRow>(controller.ChatPage.Rows[1]);
        Assert.Equal(0.5f, defaultRow.Current);
        Assert.Equal(1.0f, activeRow.Current);
        Assert.Equal(0.5f, bindings.DefaultOpacity);
        Assert.Equal(1.0f, bindings.ActiveOpacity);
    }


    [Fact]
    public void Reset_AfterOnlyDefaultSliderChanged_RevertsTheThumbToo()
    {
        var fakeBindings = new FakeBindings { DefaultOpacity = 0.3f, ActiveOpacity = 0.9f };
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        bool bound = ChatOptionsPageController.Bind(
            layout, controller.ChatPage, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());
        Assert.True(bound);

        var listBox = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(ChatOptionsPageController.ListBoxElementId));
        List<UiScrollbar> sliders = CollectScalarSliders(listBox);
        Assert.Equal(2, sliders.Count);
        UiScrollbar slider1 = sliders[0];

        var defaultRow = Assert.IsType<FloatOptionRow>(controller.ChatPage.Rows[0]);
        var activeRow = Assert.IsType<FloatOptionRow>(controller.ChatPage.Rows[1]);

        defaultRow.SetCurrentValue(0.1f);
        Assert.Equal(0.1f, slider1.ScalarPosition, 3);
        Assert.True(defaultRow.Changed);
        Assert.False(activeRow.Changed); // link never touched Active

        controller.ChatPage.Reset();

        Assert.Equal(0.3f, defaultRow.Current);
        Assert.Equal(0.3f, fakeBindings.DefaultOpacity);
        Assert.Equal(0.3f, slider1.ScalarPosition, 3); // the thumb reverted too, not just the value
    }

    [Fact]
    public void Reset_AfterOnlyActiveSliderChanged_RevertsTheThumbToo()
    {
        var fakeBindings = new FakeBindings { DefaultOpacity = 0.3f, ActiveOpacity = 0.6f };
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        bool bound = ChatOptionsPageController.Bind(
            layout, controller.ChatPage, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());
        Assert.True(bound);

        var listBox = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(ChatOptionsPageController.ListBoxElementId));
        List<UiScrollbar> sliders = CollectScalarSliders(listBox);
        Assert.Equal(2, sliders.Count);
        UiScrollbar slider2 = sliders[1];

        var defaultRow = Assert.IsType<FloatOptionRow>(controller.ChatPage.Rows[0]);
        var activeRow = Assert.IsType<FloatOptionRow>(controller.ChatPage.Rows[1]);

        activeRow.SetCurrentValue(0.95f);
        Assert.Equal(0.95f, slider2.ScalarPosition, 3);
        Assert.True(activeRow.Changed);
        Assert.False(defaultRow.Changed); // link never touched Default (0.6 < 0.95)

        controller.ChatPage.Reset();

        Assert.Equal(0.6f, activeRow.Current);
        Assert.Equal(0.6f, fakeBindings.ActiveOpacity);
        Assert.Equal(0.6f, slider2.ScalarPosition, 3); // the thumb reverted too, not just the value
    }

    // ── OP5 review fix S1: settings write batches to drag-end, not per tick ─

    [Fact]
    public void DraggingDefaultSlider_DefersTheSettingsWriteUntilDragEnd()
    {
        var fakeBindings = new FakeBindings { DefaultOpacity = 0.2f, ActiveOpacity = 1.0f };
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        bool bound = ChatOptionsPageController.Bind(
            layout, controller.ChatPage, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());
        Assert.True(bound);

        var listBox = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(ChatOptionsPageController.ListBoxElementId));
        List<UiScrollbar> sliders = CollectScalarSliders(listBox);
        UiScrollbar slider1 = sliders[0];
        Assert.True(slider1.Width > 16f, $"fixture slider1.Width={slider1.Width} too narrow for this test's thumb math");

        float thumbWidth = MathF.Min(16f, slider1.Width);
        float travel = MathF.Max(1f, slider1.Width - thumbWidth);
        float thumbX = travel * slider1.ScalarPosition;
        int clickX = (int)(thumbX + thumbWidth * 0.5f);

        Assert.True(slider1.OnEvent(new UiEvent(0u, slider1, UiEventType.MouseDown, Data1: clickX)));
        Assert.True(slider1.IsDragging);
        Assert.Equal(0, fakeBindings.OpacityFlushes);

        for (int i = 1; i <= 10; i++)
        {
            Assert.True(slider1.OnEvent(new UiEvent(0u, slider1, UiEventType.MouseMove, Data1: clickX + i)));
            Assert.Equal(0, fakeBindings.OpacityFlushes); // N drag ticks = 0 saves
        }

        // Live opacity DID apply on every tick even though nothing flushed.
        Assert.NotEqual(0.2f, fakeBindings.DefaultOpacity);

        Assert.True(slider1.OnEvent(new UiEvent(0u, slider1, UiEventType.MouseUp, Data1: clickX + 10)));
        Assert.False(slider1.IsDragging);
        Assert.Equal(1, fakeBindings.OpacityFlushes); // drag end = 1 save
    }

    [Fact]
    public void ResetClick_FlushesImmediately_NotMidDrag()
    {
        var fakeBindings = new FakeBindings { DefaultOpacity = 0.3f, ActiveOpacity = 0.9f };
        (OptionsPanelController controller, FakeBindings bindings, bool bound) = BindRealWith(fakeBindings);
        Assert.True(bound);
        var defaultRow = Assert.IsType<FloatOptionRow>(controller.ChatPage.Rows[0]);

        defaultRow.SetCurrentValue(0.1f); // not mid-drag either — flushes immediately
        Assert.Equal(1, bindings.OpacityFlushes);

        controller.ChatPage.Reset();
        Assert.Equal(2, bindings.OpacityFlushes);
    }

    [Fact]
    public void CheckingAFilterRow_PublishesSetFilter_ForItsOwnCompactWindowId()
    {
        var fakeBindings = new FakeBindings();
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        bool bound = ChatOptionsPageController.Bind(
            layout, controller.ChatPage, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());
        Assert.True(bound);

        var listBox = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(ChatOptionsPageController.ListBoxElementId));
        List<UiCheckboxBitfield64> blocks = CollectBlocks(listBox);
        UiCheckboxBitfield64 floaty1Block = blocks[1];

        UiCheckboxBitfield64.Row row = floaty1Block.Rows[0]; // Gameplay (floaty has it)
        row.Toggle.OnClick!.Invoke();

        var (windowId, value) = Assert.Single(fakeBindings.FilterSets);
        Assert.Equal(1, windowId);
        Assert.Equal(fakeBindings.Filters[1], value);
    }

    [Fact]
    public void MainWindowBlock_HasNoGameplayRow()
    {
        var fakeBindings = new FakeBindings();
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        ChatOptionsPageController.Bind(
            layout, controller.ChatPage, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());

        var listBox = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(ChatOptionsPageController.ListBoxElementId));
        UiCheckboxBitfield64 mainBlock = CollectBlocks(listBox)[0];

        Assert.All(mainBlock.Rows, r => Assert.NotEqual("Gameplay", r.Label));
        Assert.DoesNotContain(mainBlock.Rows, r => r.LowMask == 0x83912021ul);
    }

    [Fact]
    public void OnShown_ReSeedsFilterBlock_FromLiveChatWindowState()
    {
        var fakeBindings = new FakeBindings();
        (OptionsPanelController controller, FakeBindings bindings, _) = BindRealWith(fakeBindings);

        bindings.Filters[ChatWindowState.MainWindowId] = 0x1ul;

        controller.ChatPage.OnShown();

        var mainRow = Assert.IsType<BitfieldOptionRow>(controller.ChatPage.Rows[2]); // 2 sliders + main block
        Assert.Equal(0x1ul, mainRow.Current);
        Assert.Equal(0x1ul, mainRow.Saved);
        Assert.False(controller.ChatPage.Changed);
    }

    [Fact]
    public void Reset_RevertsAFilterBlock_ToItsSavedBaseline()
    {
        var fakeBindings = new FakeBindings();
        (OptionsPanelController controller, FakeBindings bindings, _) = BindRealWith(fakeBindings);
        var mainRow = Assert.IsType<BitfieldOptionRow>(controller.ChatPage.Rows[2]);
        ulong initial = mainRow.Current;
        bindings.FilterSets.Clear();

        mainRow.SetCurrentValue(0x0ul);
        Assert.True(controller.ChatPage.Changed);

        controller.ChatPage.Reset();

        Assert.Equal(initial, mainRow.Current);
        Assert.Contains(bindings.FilterSets, s => s.WindowId == ChatWindowState.MainWindowId && s.Value == initial);
    }

    [Fact]
    public void LabelResolutionFailure_LeavesRowsBuilt_NeverInventsEnglish()
    {
        (OptionsPanelController controller, _, bool bound) = BindReal(resolveString: (_, _) => null);

        Assert.True(bound);
        Assert.Equal(7, controller.ChatPage.Rows.Count);
    }

    [Fact]
    public void Bind_MissingListBox_ReturnsFalse_AndDoesNotThrow()
    {
        var emptyRoot = new ElementInfo { Id = 0, Type = 3 };
        ImportedLayout emptyLayout = LayoutImporter.Build(emptyRoot, NoTex, null);
        var page = new OptionPage();

        bool bound = ChatOptionsPageController.Bind(
            emptyLayout, page, MakeTemplateResolver(), (_, _) => null,
            new FakeBindings().ToBindings());

        Assert.False(bound);
        Assert.Empty(page.Rows);
    }

    private const uint ChatPageSlotId = 0x1000050Cu;

    [Fact]
    public void ScrollbarLinkage_ModelPointsAtTheChatListBoxScroll()
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
        ChatOptionsPageController.Bind(
            layout, controller.ChatPage, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());

        var chatSlot = UiElement.FindDescendant(controller.TabPanel, ChatPageSlotId)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(chatSlot, ChatOptionsPageController.ListBoxElementId));
        var scrollbar = Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(chatSlot, ChatOptionsPageController.ScrollbarElementId));

        Assert.Same(listBox.Scroll, scrollbar.Model);
    }

    private static (OptionsPanelController Panel, FakeBindings Bindings, bool Bound) BindRealWith(
        FakeBindings fakeBindings)
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
        bool bound = ChatOptionsPageController.Bind(
            layout, controller.ChatPage, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());
        return (controller, fakeBindings, bound);
    }
}
