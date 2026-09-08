using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public class LayoutImporterTests
{
    private static (uint, int, int) NoTex(uint _) => (0, 0, 0);


    [Fact]
    public void BuildFromInfos_HealthMeter_IsUiMeterAtRect()
    {
        var root   = new ElementInfo { Id = 0x100005F9, Type = 3, Width = 160, Height = 58 };
        var health = new ElementInfo { Id = 0x100000E6, Type = 7, X = 5, Y = 5, Width = 150, Height = 16 };

        var tree = LayoutImporter.BuildFromInfos(root, new[] { health }, NoTex, null);

        var found = tree.FindElement(0x100000E6);
        Assert.IsType<UiMeter>(found);
        Assert.Equal(5f,   found!.Left);
        Assert.Equal(150f, found.Width);
    }


    [Fact]
    public void BuildFromInfos_StampsDatElementId_OnRootAndItemList()
    {
        const uint RootId = 0x100005F9u;
        const uint ListId = 0x100001C6u;

        var root = new ElementInfo { Id = RootId, Type = 3, Width = 160, Height = 58 };
        var itemList = new ElementInfo { Id = ListId, Type = 0x10000031u, X = 5, Y = 5, Width = 72, Height = 72 };

        var tree = LayoutImporter.BuildFromInfos(root, new[] { itemList }, NoTex, null);

        Assert.Equal(RootId, tree.Root.DatElementId);
        var found = Assert.IsType<UiItemList>(tree.FindElement(ListId));
        Assert.Equal(ListId, found.DatElementId);
    }

    [Fact]
    public void BuildFromInfos_Type12Child_IsSkipped_Type3Present()
    {
        var root      = new ElementInfo { Id = 0x10000001, Type = 3, Width = 160, Height = 58 };
        var prototype = new ElementInfo { Id = 0x20000001, Type = 12, Width = 0, Height = 0 };
        var container = new ElementInfo { Id = 0x20000002, Type = 3,  Width = 100, Height = 20 };

        var tree = LayoutImporter.BuildFromInfos(root, new[] { prototype, container }, NoTex, null);

        // Type-12 is now a UiText (transparent, no lines) — present in the tree.
        Assert.IsType<UiText>(tree.FindElement(0x20000001));
        // Type-3 must also be present.
        Assert.NotNull(tree.FindElement(0x20000002));
    }

    [Fact]
    public void BuildFromInfos_TextPassToChildren_PropagatesDefaultAndRuntimeState()
    {
        var tab = new ElementInfo
        {
            Id = 0x100002A9u,
            Type = 12,
            Width = 100,
            Height = 25,
            DefaultStateId = RetailUiStateIds.Closed,
            DefaultStateName = "Closed",
        };
        tab.States[RetailUiStateIds.Closed] = new UiStateInfo
            { Id = RetailUiStateIds.Closed, Name = "Closed", PassToChildren = true };
        tab.States[RetailUiStateIds.Open] = new UiStateInfo
            { Id = RetailUiStateIds.Open, Name = "Open", PassToChildren = true };

        var cap = new ElementInfo { Id = 0x10000439u, Type = 3, Width = 17, Height = 25 };
        cap.States[RetailUiStateIds.Closed] = new UiStateInfo
        {
            Id = RetailUiStateIds.Closed,
            Name = "Closed",
            Image = new UiImageMedia(0x06005D93u, 3),
        };
        cap.States[RetailUiStateIds.Open] = new UiStateInfo
        {
            Id = RetailUiStateIds.Open,
            Name = "Open",
            Image = new UiImageMedia(0x06005D92u, 3),
        };
        cap.StateMedia["Closed"] = (0x06005D93u, 3);
        cap.StateMedia["Open"] = (0x06005D92u, 3);
        tab.Children.Add(cap);

        var root = new ElementInfo { Id = 1u, Type = 3, Width = 100, Height = 25 };
        ImportedLayout layout = LayoutImporter.BuildFromInfos(root, [tab], NoTex, null);
        UiText text = Assert.IsType<UiText>(layout.FindElement(tab.Id));
        UiDatElement child = Assert.IsType<UiDatElement>(layout.FindElement(cap.Id));

        Assert.Single(text.Children);
        Assert.Equal(RetailUiStateIds.Closed, child.ActiveRetailStateId);
        Assert.Equal(0x06005D93u, child.ActiveMedia().File);

        Assert.True(text.TrySetRetailState(RetailUiStateIds.Open));
        Assert.Equal(RetailUiStateIds.Open, child.ActiveRetailStateId);
        Assert.Equal(0x06005D92u, child.ActiveMedia().File);
    }


    [Fact]
    public void BuildFromInfos_MeterWithSliceChildren_MeterPresent_SliceChildrenNotInTree()
    {
        const uint MeterId      = 0x100000E6u;
        const uint BackLayerId  = 0x100000E7u;
        const uint FrontLayerId = 0x00000002u;

        var backContainer  = BuildSliceContainer(BackLayerId,  ReadOrder: 0,
            l: 0x0600747Eu, t: 0x0600747Fu, r: 0x06007480u);
        var frontContainer = BuildSliceContainer(FrontLayerId, ReadOrder: 1,
            l: 0x06007481u, t: 0x06007482u, r: 0x06007483u);

        var meter = new ElementInfo { Id = MeterId, Type = 7, Width = 150, Height = 16 };
        meter.Children.Add(backContainer);
        meter.Children.Add(frontContainer);

        var root = new ElementInfo { Id = 0x100005F9, Type = 3, Width = 160, Height = 58 };

        var tree = LayoutImporter.BuildFromInfos(root, new[] { meter }, NoTex, null);

        // The meter widget is present.
        Assert.IsType<UiMeter>(tree.FindElement(MeterId));
        Assert.Null(tree.FindElement(BackLayerId));
        Assert.Null(tree.FindElement(FrontLayerId));
        var uiMeter = (UiMeter)tree.FindElement(MeterId)!;
        Assert.Empty(uiMeter.Children);
    }


    [Fact]
    public void BuildFromInfos_MeterWithTextChild_TextChildInTreeAsUiTextChildOfMeter()
    {
        const uint MeterId     = 0x10000236u;
        const uint BackId      = 0x10000237u;  // Type-3 slice
        const uint FrontId     = 0x10000238u;  // Type-3 slice
        const uint LabelId     = 0x10000239u;
        const uint ValueId     = 0x1000023Au;

        var backContainer  = BuildSliceContainer(BackId,  ReadOrder: 0,
            l: 0x0600747Eu, t: 0x0600747Fu, r: 0x06007480u);
        var frontContainer = BuildSliceContainer(FrontId, ReadOrder: 1,
            l: 0x06007481u, t: 0x06007482u, r: 0x06007483u);

        var labelInfo = new ElementInfo { Id = LabelId, Type = 12, X = 0, Y = 0, Width = 120, Height = 13 };
        var valueInfo = new ElementInfo { Id = ValueId, Type = 12, X = 0, Y = 0, Width = 200, Height = 13,
            HJustify = HJustify.Right };

        var meter = new ElementInfo { Id = MeterId, Type = 7, Width = 200, Height = 13 };
        meter.Children.Add(backContainer);
        meter.Children.Add(frontContainer);
        meter.Children.Add(labelInfo);
        meter.Children.Add(valueInfo);

        var root = new ElementInfo { Id = 0x100005F9, Type = 3, Width = 210, Height = 20 };

        var tree = LayoutImporter.BuildFromInfos(root, new[] { meter }, NoTex, null);

        // Meter is present.
        Assert.IsType<UiMeter>(tree.FindElement(MeterId));

        Assert.Null(tree.FindElement(BackId));
        Assert.Null(tree.FindElement(FrontId));

        Assert.IsType<UiText>(tree.FindElement(LabelId));
        Assert.IsType<UiText>(tree.FindElement(ValueId));

        var uiMeter = (UiMeter)tree.FindElement(MeterId)!;
        Assert.Equal(2, uiMeter.Children.Count);
        Assert.All(uiMeter.Children, c => Assert.IsType<UiText>(c));

        var valueWidget = (UiText)tree.FindElement(ValueId)!;
        Assert.True(valueWidget.RightAligned, "Type-12 text child with HJustify.Right must build as RightAligned=true");
    }

    // ── Test 6: Fix 5 — vitals meters (Type-3 only) are unaffected ────────────

    [Fact]
    public void BuildFromInfos_VitalsMeter_NoTextChildren_MeterHasNoUiChildren()
    {
        const uint MeterId  = 0x100000E6u;   // vitals health meter
        const uint BackId   = 0x100000E7u;
        const uint FrontId  = 0x100000E8u;

        var backContainer  = BuildSliceContainer(BackId,  ReadOrder: 0,
            l: 0x0600747Eu, t: 0x0600747Fu, r: 0x06007480u);
        var frontContainer = BuildSliceContainer(FrontId, ReadOrder: 1,
            l: 0x06007481u, t: 0x06007482u, r: 0x06007483u);

        var meter = new ElementInfo { Id = MeterId, Type = 7, Width = 150, Height = 16 };
        meter.Children.Add(backContainer);
        meter.Children.Add(frontContainer);

        var root = new ElementInfo { Id = 0x100005F9, Type = 3, Width = 160, Height = 58 };

        var tree = LayoutImporter.BuildFromInfos(root, new[] { meter }, NoTex, null);

        Assert.IsType<UiMeter>(tree.FindElement(MeterId));

        var uiMeter = (UiMeter)tree.FindElement(MeterId)!;
        Assert.Empty(uiMeter.Children);
    }

    // ── Test 4: Prototype-skip in BuildFromInfos ─────────────────────────────

    [Fact]
    public void BuildFromInfos_PrototypeSkipped_DerivedPresent_PrototypeAbsent()
    {
        var root    = new ElementInfo { Id = 0x10000001, Type = 3, Width = 200, Height = 100 };
        // The derived element has its own size + media (prototype was merged into it already).
        var derived = new ElementInfo
        {
            Id = 0xCCC00001u,
            Type = 0x10000031u, // UIElement_ItemList (toolbar slot type)
            X = 10, Y = 10, Width = 32, Height = 32,
        };
        derived.StateMedia[""] = (0x06001234u, 1);

        // Only the derived element appears in the tree (prototype was filtered by ImportInfos).
        var tree = LayoutImporter.BuildFromInfos(root, new[] { derived }, NoTex, null);

        // The derived element is present in the built tree.
        Assert.NotNull(tree.FindElement(0xCCC00001u));
        // The prototype id is NOT in the tree (was never added).
        Assert.Null(tree.FindElement(0xBBB00001u));
    }


    [Fact]
    public void BuildWidget_TooltipProperties_CopyOntoTheWidget_AndTextResolves()
    {
        var root = new ElementInfo { Id = 0x1, Type = 3, Width = 100, Height = 40 };
        var trigger = new ElementInfo
        {
            Id = 0x2, Type = 3, X = 0, Y = 0, Width = 40, Height = 20,
            TooltipEnabled = true,
            TooltipRootElementId = 0x10000487u,
            TooltipLayoutDid = 0x21000041u,
            TooltipTextChildElementId = 0x10000396u,
            TooltipDelaySeconds = 0.5f,
            TooltipText = new UiStringInfoValue(0, 0x0AAAAAAAu, 0x23000003u, 0, 0, 0),
        };

        string? Resolve(UiStringInfoValue info)
            => info.TableId == 0x23000003u && info.StringId == 0x0AAAAAAAu ? "Rotate left." : null;

        var tree = LayoutImporter.BuildFromInfos(
            root, [trigger], NoTex, null, fontResolve: null, stringResolve: Resolve);

        UiElement found = tree.FindElement(0x2)!;
        Assert.True(found.AuthoredTooltipEnabled);
        Assert.Equal(0x10000487u, found.AuthoredTooltipRootElementId);
        Assert.Equal(0x21000041u, found.AuthoredTooltipLayoutDid);
        Assert.Equal(0x10000396u, found.AuthoredTooltipTextChildElementId);
        Assert.Equal(0.5f, found.AuthoredTooltipDelaySeconds);
        Assert.Equal("Rotate left.", found.AuthoredTooltipText);
    }

    [Fact]
    public void BuildWidget_TooltipText_NoStringResolver_StaysNull()
    {
        var root = new ElementInfo { Id = 0x1, Type = 3, Width = 100, Height = 40 };
        var trigger = new ElementInfo
        {
            Id = 0x2, Type = 3, X = 0, Y = 0, Width = 40, Height = 20,
            TooltipText = new UiStringInfoValue(0, 0x0AAAAAAAu, 0x23000003u, 0, 0, 0),
        };

        var tree = LayoutImporter.BuildFromInfos(root, [trigger], NoTex, null);

        UiElement found = tree.FindElement(0x2)!;
        Assert.Null(found.AuthoredTooltipText);
    }

    [Fact]
    public void BuildWidget_NoTooltipProperties_EveryWidgetFieldStaysAtItsDefault()
    {
        var root = new ElementInfo { Id = 0x1, Type = 3, Width = 100, Height = 40 };
        var trigger = new ElementInfo { Id = 0x2, Type = 3, X = 0, Y = 0, Width = 40, Height = 20 };

        var tree = LayoutImporter.BuildFromInfos(root, [trigger], NoTex, null);

        UiElement found = tree.FindElement(0x2)!;
        Assert.False(found.AuthoredTooltipEnabled);
        Assert.Null(found.AuthoredTooltipText);
        Assert.Equal(0u, found.AuthoredTooltipRootElementId);
        Assert.Equal(0u, found.AuthoredTooltipLayoutDid);
        Assert.Equal(0u, found.AuthoredTooltipTextChildElementId);
        Assert.Null(found.AuthoredTooltipDelaySeconds);
    }

    [Fact]
    public void BuildWidget_AuthoredInvisible_HidesInitiallyButDoesNotLatchVisibility()
    {
        var root = new ElementInfo { Id = 0x1, Type = 3, Width = 100, Height = 40 };
        var hidden = new ElementInfo
        {
            Id = 0x2,
            Type = 3,
            Width = 20,
            Height = 20,
            Invisible = true,
        };

        ImportedLayout tree = LayoutImporter.BuildFromInfos(root, [hidden], NoTex, null);
        UiElement found = tree.FindElement(0x2)!;

        Assert.True(found.AuthoredInvisible);
        Assert.False(found.Visible);

        found.Visible = true;
        Assert.True(found.Visible);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ElementInfo BuildSliceContainer(uint id, uint ReadOrder, uint l, uint t, uint r)
    {
        var c = new ElementInfo { Id = id, Type = 3, ReadOrder = ReadOrder };
        c.Children.Add(new ElementInfo { X = 0,   StateMedia = { [""] = (l, 1) } });
        c.Children.Add(new ElementInfo { X = 10,  StateMedia = { [""] = (t, 1) } });
        c.Children.Add(new ElementInfo { X = 140, StateMedia = { [""] = (r, 1) } });
        return c;
    }
}
