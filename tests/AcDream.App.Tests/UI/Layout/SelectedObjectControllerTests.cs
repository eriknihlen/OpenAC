using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Core.Selection;
using Xunit;

namespace AcDream.App.Tests.UI.Layout;

public class SelectedObjectControllerTests
{
    // ── Shared layout ────────────────────────────────────────────────────────

    private static (
        ImportedLayout layout,
        UiPanel nameEl,
        UiDatElement overlayEl,
        UiMeter healthMeterEl)
    FakeLayout()
    {
        var dict = new Dictionary<uint, UiElement>();
        var root = new UiPanel();

        var nameEl = new UiPanel { Width = 100, Height = 20 };
        dict[SelectedObjectController.NameId] = nameEl;
        root.AddChild(nameEl);

        var overlayInfo = new ElementInfo
        {
            Id   = SelectedObjectController.OverlayId,
            Type = 3,
            StateMedia =
            {
                [""]                    = (0x06000001u, 3),
                ["ObjectSelected"]      = (0x06001937u, 3),
                ["StackedItemSelected"] = (0x06004CF4u, 3),
            },
        };
        var overlayEl = new UiDatElement(overlayInfo, _ => (0u, 0, 0));
        dict[SelectedObjectController.OverlayId] = overlayEl;
        root.AddChild(overlayEl);

        var healthMeterEl = new UiMeter { Width = 100, Height = 10, Visible = true };
        dict[SelectedObjectController.HealthMeterId] = healthMeterEl;
        root.AddChild(healthMeterEl);

        var manaMeterEl = new UiMeter { Width = 100, Height = 10, Visible = true };
        dict[SelectedObjectController.ManaMeterId] = manaMeterEl;
        root.AddChild(manaMeterEl);

        var stackEntry = new UiField { Width = 50, Height = 14, Visible = true };
        dict[SelectedObjectController.StackSizeEntryId] = stackEntry;
        root.AddChild(stackEntry);

        var stackSlider = new UiScrollbar { Width = 90, Height = 14, Visible = true };
        dict[SelectedObjectController.StackSizeSliderId] = stackSlider;
        root.AddChild(stackSlider);

        return (new ImportedLayout(root, dict), nameEl, overlayEl, healthMeterEl);
    }

    // ── Recording delegates ──────────────────────────────────────────────────

    private sealed class Harness
    {
        public readonly SelectionState Selection = new();
        public readonly StackSplitQuantityState SplitQuantity = new();
        public Action<uint, float>? HealthHandler;
        public Action<uint, float, bool>? ItemManaHandler;
        public Action<ClientObject>? ObjectUpdatedHandler;
        public readonly List<uint> QueryHealthCalls = new();
        public readonly List<uint> QueryItemManaCalls = new();

        public readonly Dictionary<uint, bool>   HealthTargetMap = new();
        public readonly Dictionary<uint, bool>   OwnedMap        = new();
        public readonly Dictionary<uint, string> NameMap         = new();
        public readonly Dictionary<uint, float>  HealthMap       = new();
        public readonly Dictionary<uint, bool>   HasHealthMap    = new();
        public readonly Dictionary<uint, float>  ManaMap         = new();
        public readonly Dictionary<uint, uint>   StackMap        = new();
        public readonly Dictionary<uint, bool>   CoinstackMap    = new();
        public int CoinTotal;
        public readonly Dictionary<uint, bool>   VendorSplitExemptMap = new();

        public void FireSelection(uint? g)
        {
            if (g is { } guid)
                Selection.Select(guid, SelectionChangeSource.System);
            else
                Selection.Clear(SelectionChangeSource.System);
        }
        public void FireHealth(uint g, float pct) => HealthHandler?.Invoke(g, pct);
        public void FireItemMana(uint g, float pct, bool valid)
            => ItemManaHandler?.Invoke(g, pct, valid);

        public SelectedObjectController Bind(ImportedLayout layout, UiDatFont? datFont = null)
            => SelectedObjectController.Bind(
                layout,
                selection: Selection,
                subscribeHealthChanged:    h => HealthHandler = h,
                unsubscribeHealthChanged: h =>
                {
                    if (HealthHandler == h) HealthHandler = null;
                },
                subscribeItemManaChanged: h => ItemManaHandler = h,
                unsubscribeItemManaChanged: h =>
                {
                    if (ItemManaHandler == h) ItemManaHandler = null;
                },
                isHealthTarget:  g => HealthTargetMap.TryGetValue(g, out var v) && v,
                isOwnedByPlayer: g => OwnedMap.TryGetValue(g, out var v) && v,
                name:            g => NameMap.TryGetValue(g, out var v) ? v : null,
                healthPercent:   g => HealthMap.TryGetValue(g, out var v) ? v : 1f,
                hasHealth:       g => HasHealthMap.TryGetValue(g, out var v) && v,
                stackSize:       g => StackMap.TryGetValue(g, out var v) ? v : 0u,
                sendQueryHealth: g => QueryHealthCalls.Add(g),
                manaPercent:     g => ManaMap.TryGetValue(g, out var v) ? v : 0f,
                sendQueryItemMana: g => QueryItemManaCalls.Add(g),
                datFont:         datFont,
                splitQuantity:   SplitQuantity,
                subscribeObjectUpdated: h => ObjectUpdatedHandler = h,
                unsubscribeObjectUpdated: h =>
                {
                    if (ObjectUpdatedHandler == h) ObjectUpdatedHandler = null;
                },
                isVendorSplitExempt: g => VendorSplitExemptMap.TryGetValue(g, out var v) && v,
                isCoinstack: g => CoinstackMap.TryGetValue(g, out var v) && v,
                coinTotal: () => CoinTotal);
    }

    // ── B1: Bind initialisation ──────────────────────────────────────────────

    [Fact]
    public void Bind_healthMeterHidden_nameTextChildAttached_nameFloatedOnTop()
    {
        var (layout, nameEl, _, healthMeterEl) = FakeLayout();
        new Harness().Bind(layout);

        Assert.False(healthMeterEl.Visible, "health meter must be Visible=false immediately after Bind");

        var textChild = nameEl.Children.OfType<UiText>().FirstOrDefault();
        Assert.NotNull(textChild);
        Assert.True(textChild!.Centered,            "name UiText must be Centered");
        Assert.True(textChild.ClickThrough,         "name UiText must be ClickThrough");
        Assert.False(textChild.AcceptsFocus,        "AcceptsFocus must be false on name label");
        Assert.False(textChild.IsEditControl,       "IsEditControl must be false on name label");
        Assert.False(textChild.CapturesPointerDrag, "CapturesPointerDrag must be false on name label");

        Assert.True(nameEl.ZOrder > 1000, "name element must be floated above the overlay/meter z-order");
    }

    [Fact]
    public void OwnedCoinstack_usesRetailsExactStackNameAndTotalFormat()
    {
        var (layout, nameEl, _, _) = FakeLayout();
        var h = new Harness { CoinTotal = 12_345 };
        const uint coins = 0x50000111u;
        h.NameMap[coins] = "Pyreals";
        h.StackMap[coins] = 2_345u;
        h.OwnedMap[coins] = true;
        h.CoinstackMap[coins] = true;
        h.Bind(layout);

        h.FireSelection(coins);

        Assert.Equal(
            "2345 Pyreals (of 12345)",
            nameEl.Children.OfType<UiText>().First().LinesProvider().Single().Text);
    }

    [Fact]
    public void Bind_nameLinesProvider_yieldsEmpty_whenNothingSelected()
    {
        var (layout, nameEl, _, _) = FakeLayout();
        new Harness().Bind(layout);

        var textChild = nameEl.Children.OfType<UiText>().First();
        Assert.Empty(textChild.LinesProvider());
    }

    [Fact]
    public void ReentrantAutoTargetNotice_RendersCurrentReplacementNotStaleClear()
    {
        const uint Dead = 0xAA00u;
        const uint Replacement = 0xAA01u;
        var (layout, nameEl, _, _) = FakeLayout();
        var h = new Harness();
        h.NameMap[Dead] = "Dead Drudge";
        h.NameMap[Replacement] = "Drudge Prowler";
        h.StackMap[Dead] = 1u;
        h.StackMap[Replacement] = 1u;

        h.Selection.Select(Dead, SelectionChangeSource.World);
        h.Selection.Changed += transition =>
        {
            if (transition.SelectedObjectId is null)
                h.Selection.Select(Replacement, SelectionChangeSource.System);
        };
        h.Bind(layout);

        h.Selection.Clear(
            SelectionChangeSource.System,
            SelectionChangeReason.CombatTargetDied);

        Assert.Equal(Replacement, h.Selection.SelectedObjectId);
        var lines = nameEl.Children.OfType<UiText>().First().LinesProvider();
        Assert.Single(lines);
        Assert.Equal("Drudge Prowler", lines[0].Text);
    }

    // ── H1: Select a health target — meter does NOT show on select alone ─────

    [Fact]
    public void SelectHealthTarget_unknownHealth_meterStaysHidden_queryFired_nameAndOverlaySet()
    {
        const uint Guid = 0xAA01u;
        const string ExpectedName = "Drudge Prowler";

        var (layout, nameEl, overlayEl, healthMeterEl) = FakeLayout();
        var h = new Harness();
        h.HealthTargetMap[Guid] = true;
        h.NameMap[Guid] = ExpectedName;
        h.StackMap[Guid] = 1u;            // ObjectSelected
        // HasHealthMap[Guid] not set → false (no health known yet)
        h.Bind(layout);

        h.FireSelection(Guid);

        Assert.False(healthMeterEl.Visible,
            "meter must stay hidden on select when no health is known yet");
        Assert.Single(h.QueryHealthCalls);
        Assert.Equal(Guid, h.QueryHealthCalls[0]);
        Assert.Equal("ObjectSelected", overlayEl.ActiveState);

        var lines = nameEl.Children.OfType<UiText>().First().LinesProvider();
        Assert.Single(lines);
        Assert.Equal(ExpectedName, lines[0].Text);
        Assert.Equal(new Vector4(1f, 1f, 1f, 1f), lines[0].Color);
    }

    // ── H1b: Health arrives for the selected guid → meter appears ───────────

    [Fact]
    public void HealthChanged_forSelectedGuid_showsMeter()
    {
        const uint Guid = 0xAA02u;

        var (layout, _, _, healthMeterEl) = FakeLayout();
        var h = new Harness();
        h.HealthTargetMap[Guid] = true;
        h.NameMap[Guid] = "Drudge Slinker";
        h.Bind(layout);

        h.FireSelection(Guid);
        Assert.False(healthMeterEl.Visible, "hidden until health arrives");

        // Simulate UpdateHealth (0x01C0) for the selected guid.
        h.FireHealth(Guid, 0.6f);
        Assert.True(healthMeterEl.Visible, "meter must appear when health arrives for the selected guid");
    }

    [Fact]
    public void HealthChanged_forOtherGuid_doesNotShowMeter()
    {
        const uint Sel = 0xAA03u, Other = 0xBB03u;

        var (layout, _, _, healthMeterEl) = FakeLayout();
        var h = new Harness();
        h.HealthTargetMap[Sel] = true;
        h.HealthTargetMap[Other] = true;
        h.NameMap[Sel] = "Selected";
        h.Bind(layout);

        h.FireSelection(Sel);
        h.FireHealth(Other, 0.5f);   // health for a DIFFERENT entity

        Assert.False(healthMeterEl.Visible, "health for a non-selected guid must not show the meter");
    }

    // ── H1c: Already-known health → meter shows immediately on select ───────

    [Fact]
    public void SelectHealthTarget_alreadyKnownHealth_meterVisibleImmediately()
    {
        const uint Guid = 0xAA04u;

        var (layout, _, _, healthMeterEl) = FakeLayout();
        var h = new Harness();
        h.HealthTargetMap[Guid] = true;
        h.HasHealthMap[Guid] = true;
        h.HealthMap[Guid] = 0.9f;
        h.NameMap[Guid] = "Olthoi";
        h.Bind(layout);

        h.FireSelection(Guid);
        Assert.True(healthMeterEl.Visible,
            "meter must show immediately when health is already known for the target");
    }

    // ── H2: Stacked item ─────────────────────────────────────────────────────

    [Fact]
    public void SelectStackedItem_overlayStackedItemSelected_meterHidden()
    {
        const uint Guid = 0xBB02u;

        var (layout, _, overlayEl, healthMeterEl) = FakeLayout();
        var h = new Harness();
        h.HealthTargetMap[Guid] = false;
        h.NameMap[Guid] = "Heal Kits";
        h.StackMap[Guid] = 5u;            // stackSize > 1
        h.Bind(layout);

        h.FireSelection(Guid);

        Assert.Equal("StackedItemSelected", overlayEl.ActiveState);
        Assert.False(healthMeterEl.Visible);
    }

    [Fact]
    public void SelectStackedItem_showsRetailCountEntryAndSlider_atFullStack()
    {
        const uint Guid = 0xBB03u;
        var (layout, nameEl, _, _) = FakeLayout();
        var h = new Harness();
        h.NameMap[Guid] = "Healing Kits";
        h.StackMap[Guid] = 17u;
        h.Bind(layout);

        h.FireSelection(Guid);

        var entry = Assert.IsType<UiField>(layout.FindElement(SelectedObjectController.StackSizeEntryId));
        var slider = Assert.IsType<UiScrollbar>(layout.FindElement(SelectedObjectController.StackSizeSliderId));
        Assert.True(entry.Visible);
        Assert.True(slider.Visible);
        Assert.Equal("17", entry.Text);
        Assert.Equal(17u, h.SplitQuantity.Value);
        Assert.Equal(17u, h.SplitQuantity.Maximum);
        Assert.Equal(1f, slider.ScalarPosition);
        Assert.Equal("17 Healing Kits", nameEl.Children.OfType<UiText>().First().LinesProvider().Single().Text);

        slider.SetScalarPosition(0.5f);
        slider.ScalarChanged!(0.5f);
        Assert.Equal(9u, h.SplitQuantity.Value);
        Assert.Equal("9", entry.Text);
        Assert.Equal(0.5f, slider.ScalarPosition);

        slider.SetScalarPosition(0f);
        slider.ScalarChanged!(0f);
        Assert.Equal(1u, h.SplitQuantity.Value);
        Assert.Equal("1", entry.Text);
        Assert.Equal(0f, slider.ScalarPosition);

        slider.SetScalarPosition(1f);
        slider.ScalarChanged!(1f);
        Assert.Equal(17u, h.SplitQuantity.Value);
        Assert.Equal("17", entry.Text);
        Assert.Equal(1f, slider.ScalarPosition);

        entry.SetText("4");
        entry.Submit();
        Assert.Equal(4u, h.SplitQuantity.Value);
        Assert.Equal("4", entry.Text);
        Assert.Equal(4f / 17f, slider.ScalarPosition, 5);
    }

    [Fact]
    public void StackSizeUpdate_refreshesSelectedControls_andHidesThemAtOne()
    {
        const uint Guid = 0xBB04u;
        var (layout, _, _, _) = FakeLayout();
        var h = new Harness();
        h.NameMap[Guid] = "Healing Kits";
        h.StackMap[Guid] = 5u;
        h.Bind(layout);
        h.FireSelection(Guid);

        h.StackMap[Guid] = 1u;
        h.ObjectUpdatedHandler!(new ClientObject { ObjectId = Guid, StackSize = 1 });

        Assert.False(layout.FindElement(SelectedObjectController.StackSizeEntryId)!.Visible);
        Assert.False(layout.FindElement(SelectedObjectController.StackSizeSliderId)!.Visible);
        Assert.Equal(1u, h.SplitQuantity.Value);
        Assert.Equal(1u, h.SplitQuantity.Maximum);
    }

    // ── H3: Non-health target (friendly NPC / scenery / Door) ───────────────

    [Fact]
    public void SelectNonHealthTarget_meterHidden_noQuery_nameSet()
    {
        const uint Guid = 0xCC03u;
        const string ExpectedName = "Town Crier";

        var (layout, nameEl, overlayEl, healthMeterEl) = FakeLayout();
        var h = new Harness();
        h.HealthTargetMap[Guid] = false;
        h.NameMap[Guid] = ExpectedName;
        h.Bind(layout);

        h.FireSelection(Guid);

        Assert.False(healthMeterEl.Visible, "meter must stay hidden for a non-health target");
        Assert.Empty(h.QueryHealthCalls);
        Assert.Equal("ObjectSelected", overlayEl.ActiveState);

        var lines = nameEl.Children.OfType<UiText>().First().LinesProvider();
        Assert.Single(lines);
        Assert.Equal(ExpectedName, lines[0].Text);
    }


    [Fact]
    public void SelectNull_clearsStrip()
    {
        const uint Guid = 0xDD04u;

        var (layout, nameEl, overlayEl, healthMeterEl) = FakeLayout();
        var h = new Harness();
        h.HealthTargetMap[Guid] = true;
        h.HasHealthMap[Guid] = true;        // so the meter is shown on select
        h.HealthMap[Guid] = 0.5f;
        h.NameMap[Guid] = "Wolf";
        h.Bind(layout);

        h.FireSelection(Guid);
        Assert.True(healthMeterEl.Visible);

        h.FireSelection(null);

        Assert.False(healthMeterEl.Visible, "meter must be hidden after deselect");
        Assert.Equal("", overlayEl.ActiveState);
        Assert.Empty(nameEl.Children.OfType<UiText>().First().LinesProvider());
        Assert.Equal(new[] { Guid, 0u }, h.QueryHealthCalls);
    }

    // ── H5: Re-select a different guid ───────────────────────────────────────

    [Fact]
    public void ReSelect_differentGuid_clearsFirstThenAppliesSecond()
    {
        const uint GuidA = 0xEE05u, GuidB = 0xFF06u;

        var (layout, nameEl, overlayEl, healthMeterEl) = FakeLayout();
        var h = new Harness();
        h.HealthTargetMap[GuidA] = true;  h.HealthTargetMap[GuidB] = false;
        h.HasHealthMap[GuidA] = true;     // A shows its bar on select
        h.NameMap[GuidA] = "Bandit";      h.NameMap[GuidB] = "Chest";
        h.HealthMap[GuidA] = 1.0f;
        h.Bind(layout);

        h.FireSelection(GuidA);
        Assert.True(healthMeterEl.Visible);
        Assert.Single(h.QueryHealthCalls);

        h.FireSelection(GuidB);

        Assert.False(healthMeterEl.Visible, "meter must clear when switching to a non-health target");
        Assert.Equal("ObjectSelected", overlayEl.ActiveState);
        Assert.Equal(new[] { GuidA, 0u }, h.QueryHealthCalls);

        var lines = nameEl.Children.OfType<UiText>().First().LinesProvider();
        Assert.Single(lines);
        Assert.Equal("Chest", lines[0].Text);
    }

    // ── H6: Overlay flash reverts after the flash window (Tick) ─────────────

    [Fact]
    public void Tick_revertsOverlayFlash_afterDuration()
    {
        const uint Guid = 0xAB06u;

        var (layout, _, overlayEl, _) = FakeLayout();
        var h = new Harness();
        h.HealthTargetMap[Guid] = false;
        h.NameMap[Guid] = "Lever";
        var c = h.Bind(layout);

        h.FireSelection(Guid);
        Assert.Equal("ObjectSelected", overlayEl.ActiveState);

        // A small tick before the window elapses → still flashing.
        c.Tick(0.1);
        Assert.Equal("ObjectSelected", overlayEl.ActiveState);

        // Tick past the 0.25s window → overlay reverts to blank.
        c.Tick(0.2);
        Assert.Equal("", overlayEl.ActiveState);
    }

    // ── H7: Partial layout (missing elements) ────────────────────────────────

    [Fact]
    public void PartialLayout_noElements_doesNotThrow()
    {
        var root = new UiPanel();
        var layout = new ImportedLayout(root, new Dictionary<uint, UiElement>());

        var h = new Harness();
        h.HealthTargetMap[0x12345678u] = true;
        h.NameMap[0x12345678u] = "Something";
        var c = h.Bind(layout);

        Assert.Null(Record.Exception(() => h.FireSelection(0x12345678u)));
        Assert.Null(Record.Exception(() => h.FireHealth(0x12345678u, 0.5f)));
        Assert.Null(Record.Exception(() => c.Tick(0.5)));
        Assert.Null(Record.Exception(() => h.FireSelection(null)));

        Assert.Single(h.QueryHealthCalls);
        Assert.Equal(0x12345678u, h.QueryHealthCalls[0]);
    }

    // ── H8: Fill reflects live health; returns 0 when nothing selected ──────

    [Fact]
    public void HealthMeterFill_reflectsLiveHealthPercent()
    {
        const uint Guid = 0xAA07u;

        var (layout, _, _, healthMeterEl) = FakeLayout();
        var h = new Harness();
        h.HealthTargetMap[Guid] = true;
        h.NameMap[Guid] = "Arwic Banderling";
        h.HealthMap[Guid] = 0.5f;
        h.Bind(layout);

        h.FireSelection(Guid);
        Assert.Equal(0.5f, healthMeterEl.Fill());

        h.HealthMap[Guid] = 0.25f;        // server updates health
        Assert.Equal(0.25f, healthMeterEl.Fill());
    }

    [Fact]
    public void HealthMeterFill_returnsZero_whenNothingSelected()
    {
        const uint Guid = 0xAA08u;

        var (layout, _, _, healthMeterEl) = FakeLayout();
        var h = new Harness();
        h.HealthTargetMap[Guid] = true;
        h.NameMap[Guid] = "Spider";
        h.HealthMap[Guid] = 0.8f;
        h.Bind(layout);

        h.FireSelection(Guid);
        Assert.Equal(0.8f, healthMeterEl.Fill());

        h.FireSelection(null);
        Assert.Equal(0f, healthMeterEl.Fill() ?? 0f);
    }

    [Fact]
    public void OwnedNonStackItem_QueriesMana_AndValidResponseShowsMeter()
    {
        const uint Guid = 0x50000A01u;
        var (layout, _, _, _) = FakeLayout();
        var manaMeter = Assert.IsType<UiMeter>(layout.FindElement(SelectedObjectController.ManaMeterId));
        var h = new Harness();
        h.OwnedMap[Guid] = true;
        h.NameMap[Guid] = "Wand";
        h.StackMap[Guid] = 1u;
        h.ManaMap[Guid] = 0.625f;
        h.Bind(layout);

        h.FireSelection(Guid);

        Assert.Equal(new[] { Guid }, h.QueryItemManaCalls);
        Assert.False(manaMeter.Visible);

        h.FireItemMana(Guid, 0.625f, valid: true);

        Assert.True(manaMeter.Visible);
        Assert.Equal(0.625f, manaMeter.Fill());
    }

    [Fact]
    public void InvalidManaResponse_CancelsQueryWithoutShowingMeter()
    {
        const uint Guid = 0x50000A02u;
        var (layout, _, _, _) = FakeLayout();
        var manaMeter = Assert.IsType<UiMeter>(layout.FindElement(SelectedObjectController.ManaMeterId));
        var h = new Harness();
        h.OwnedMap[Guid] = true;
        h.StackMap[Guid] = 1u;
        h.Bind(layout);
        h.FireSelection(Guid);

        h.FireItemMana(Guid, 0f, valid: false);

        Assert.Equal(new[] { Guid, 0u }, h.QueryItemManaCalls);
        Assert.False(manaMeter.Visible);
    }

    [Fact]
    public void ChangingAwayFromVisibleMana_CancelsWithZero()
    {
        const uint Guid = 0x50000A03u;
        var (layout, _, _, _) = FakeLayout();
        var h = new Harness();
        h.OwnedMap[Guid] = true;
        h.StackMap[Guid] = 1u;
        h.Bind(layout);
        h.FireSelection(Guid);
        h.FireItemMana(Guid, 0.5f, valid: true);

        h.FireSelection(0x60000001u);

        Assert.Equal(new[] { Guid, 0u }, h.QueryItemManaCalls);
    }

    [Fact]
    public void StackedOwnedItem_DoesNotQueryMana()
    {
        const uint Guid = 0x50000A04u;
        var (layout, _, _, _) = FakeLayout();
        var h = new Harness();
        h.OwnedMap[Guid] = true;
        h.StackMap[Guid] = 2u;
        h.Bind(layout);

        h.FireSelection(Guid);

        Assert.Empty(h.QueryItemManaCalls);
    }

    [Fact]
    public void Dispose_UnsubscribesBothHandlers_AndIsIdempotent()
    {
        var (layout, _, _, healthMeterEl) = FakeLayout();
        var h = new Harness();
        h.HealthTargetMap[0xAA09u] = true;
        var controller = h.Bind(layout);

        controller.Dispose();
        controller.Dispose();
        h.FireSelection(0xAA09u);
        h.FireHealth(0xAA09u, 0.5f);

        Assert.Null(h.HealthHandler);
        Assert.Null(h.ItemManaHandler);
        Assert.False(healthMeterEl.Visible);
        Assert.Empty(h.QueryHealthCalls);
    }


    [Fact]
    public void VendorStackSelection_ThroughRealMaterializer_ShowsSplitSlider()
    {
        const uint vendorGuid = 0x70000011u;
        const uint taperGuid = 0x60009011u;

        ImportedLayout layout = FixtureLoader.LoadToolbar();
        var objects = new ClientObjectTable();
        var vendor = new VendorState();
        var selection = new SelectionState();
        var splitQuantity = new StackSplitQuantityState();

        using var materializer = new VendorShopItemMaterializer(vendor, objects);

        SelectedObjectController controller = SelectedObjectController.Bind(
            layout,
            selection,
            subscribeHealthChanged: _ => { },
            unsubscribeHealthChanged: _ => { },
            subscribeItemManaChanged: _ => { },
            unsubscribeItemManaChanged: _ => { },
            isHealthTarget: _ => false,
            isOwnedByPlayer: _ => false,
            name: guid => objects.Get(guid)?.GetAppropriateName(),
            healthPercent: _ => 0f,
            hasHealth: _ => false,
            stackSize: guid => (uint)(objects.Get(guid)?.StackSize ?? 0),
            sendQueryHealth: _ => { },
            manaPercent: _ => 0f,
            sendQueryItemMana: _ => { },
            datFont: null,
            splitQuantity: splitQuantity,
            // REAL subscription this time (C4 used no-op lambdas here).
            subscribeObjectUpdated: h => objects.ObjectUpdated += h,
            unsubscribeObjectUpdated: h => objects.ObjectUpdated -= h,
            isVendorSplitExempt: guid =>
                vendor.VendorId != 0u
                && objects.Get(guid) is { } vendorCandidate
                && vendorCandidate.ContainerId == vendor.VendorId
                && VendorSplitPolicy.IsSplitExempt(vendorCandidate.Type));

        vendor.Apply(
            vendorGuid,
            new VendorShopProfile(0u, 0u, 0u, false, 1.0f, 1.5f, 0u, 0u, ""),
            new[]
            {
                new VendorShopItem(
                    taperGuid, StackSize: -1, WeenieClassId: 5u, Name: "Prismatic Taper",
                    ItemType: (uint)ItemType.SpellComponents, IconId: 200u, Value: 100,
                    DescStackSize: null, MaxStackSize: 1000, PluralName: "Prismatic Tapers"),
            });

        Assert.NotNull(objects.Get(taperGuid));
        Assert.Equal(1000, objects.Get(taperGuid)!.StackSize);

        selection.Select(taperGuid, SelectionChangeSource.Vendor);

        var slider = Assert.IsType<UiScrollbar>(
            layout.FindElement(SelectedObjectController.StackSizeSliderId));
        Assert.True(slider.Visible);
        Assert.Equal(1000u, splitQuantity.Maximum);

        controller.Dispose();
    }

    [Fact]
    public void LiveAceWireShape_DescOneMaxHundred_ShowsSplitSliderWithTheAuthoredCeiling()
    {
        const uint vendorGuid = 0x70000012u;
        const uint scarabGuid = 0x60009012u;

        ImportedLayout layout = FixtureLoader.LoadToolbar();
        var objects = new ClientObjectTable();
        var vendor = new VendorState();
        var selection = new SelectionState();
        var splitQuantity = new StackSplitQuantityState();
        using var materializer = new VendorShopItemMaterializer(vendor, objects);

        SelectedObjectController controller = SelectedObjectController.Bind(
            layout,
            selection,
            subscribeHealthChanged: _ => { },
            unsubscribeHealthChanged: _ => { },
            subscribeItemManaChanged: _ => { },
            unsubscribeItemManaChanged: _ => { },
            isHealthTarget: _ => false,
            isOwnedByPlayer: _ => false,
            name: guid => objects.Get(guid)?.GetAppropriateName(),
            healthPercent: _ => 0f,
            hasHealth: _ => false,
            stackSize: guid => (uint)(objects.Get(guid)?.StackSize ?? 0),
            sendQueryHealth: _ => { },
            manaPercent: _ => 0f,
            sendQueryItemMana: _ => { },
            datFont: null,
            splitQuantity: splitQuantity,
            subscribeObjectUpdated: h => objects.ObjectUpdated += h,
            unsubscribeObjectUpdated: h => objects.ObjectUpdated -= h,
            isVendorSplitExempt: guid =>
                vendor.VendorId != 0u
                && objects.Get(guid) is { } vendorCandidate
                && vendorCandidate.ContainerId == vendor.VendorId
                && VendorSplitPolicy.IsSplitExempt(vendorCandidate.Type));

        vendor.Apply(
            vendorGuid,
            new VendorShopProfile(0u, 0u, 0u, false, 1.0f, 1.5f, 0u, 0u, ""),
            new[]
            {
                new VendorShopItem(
                    scarabGuid, StackSize: -1, WeenieClassId: 5u, Name: "Lead Scarab",
                    ItemType: (uint)ItemType.SpellComponents, IconId: 200u, Value: 10,
                    DescStackSize: 1, MaxStackSize: 100, PluralName: "Lead Scarabs"),
            });
        Assert.Equal(100, objects.Get(scarabGuid)!.StackSize);

        selection.Select(scarabGuid, SelectionChangeSource.Vendor);

        var slider = Assert.IsType<UiScrollbar>(
            layout.FindElement(SelectedObjectController.StackSizeSliderId));
        Assert.True(slider.Visible);
        Assert.Equal(100u, splitQuantity.Maximum);
        Assert.Equal(1u, splitQuantity.GetObjectSplitSize(
            scarabGuid, scarabGuid, 100u));

        var nameElement = layout.FindElement(SelectedObjectController.NameId);
        Assert.NotNull(nameElement);
        UiText nameLabel = nameElement!.Children.OfType<UiText>().First();
        string renderedName = string.Concat(
            nameLabel.LinesProvider().Select(static line => line.Text));
        Assert.Equal("100 Lead Scarabs", renderedName);

        controller.Dispose();
    }

    [Fact]
    public void VendorOwnedSplitExemptStackSelection_MatchesRetailsToolbarPresentation()
    {
        const uint vendorGuid = 0x70000010u;
        const uint tradeNotesGuid = 0x60009001u;

        ImportedLayout layout = FixtureLoader.LoadToolbar();
        var objects = new ClientObjectTable();
        var vendor = new VendorState();
        var selection = new SelectionState();
        var splitQuantity = new StackSplitQuantityState();

        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = tradeNotesGuid,
            Name = "Trade Note",
            PluralName = "Trade Notes",
            Type = ItemType.PromissoryNote,
            StackSize = 250,
            ContainerId = vendorGuid,
        });
        vendor.Apply(
            vendorGuid,
            new VendorShopProfile(0u, 0u, 0u, false, 1.0f, 1.5f, 0u, 0u, ""),
            Array.Empty<VendorShopItem>());

        SelectedObjectController controller = SelectedObjectController.Bind(
            layout,
            selection,
            subscribeHealthChanged: _ => { },
            unsubscribeHealthChanged: _ => { },
            subscribeItemManaChanged: _ => { },
            unsubscribeItemManaChanged: _ => { },
            isHealthTarget: _ => false,
            isOwnedByPlayer: _ => false,
            // Production's EXACT name resolver (InteractionRetainedUiComposition.cs:676).
            name: guid => objects.Get(guid)?.GetAppropriateName(),
            healthPercent: _ => 0f,
            hasHealth: _ => false,
            // Production's EXACT stackSize resolver (InteractionRetainedUiComposition.cs:679-680).
            stackSize: guid => (uint)(objects.Get(guid)?.StackSize ?? 0),
            sendQueryHealth: _ => { },
            manaPercent: _ => 0f,
            sendQueryItemMana: _ => { },
            datFont: null,
            splitQuantity: splitQuantity,
            subscribeObjectUpdated: _ => { },
            unsubscribeObjectUpdated: _ => { },
            isVendorSplitExempt: guid =>
                vendor.VendorId != 0u
                && objects.Get(guid) is { } vendorCandidate
                && vendorCandidate.ContainerId == vendor.VendorId
                && VendorSplitPolicy.IsSplitExempt(vendorCandidate.Type));

        selection.Select(tradeNotesGuid, SelectionChangeSource.Vendor);

        var nameElement = layout.FindElement(SelectedObjectController.NameId);
        Assert.NotNull(nameElement);
        UiText nameLabel = nameElement!.Children.OfType<UiText>().First();
        string renderedName = string.Concat(
            nameLabel.LinesProvider().Select(static line => line.Text));

        var slider = Assert.IsType<UiScrollbar>(
            layout.FindElement(SelectedObjectController.StackSizeSliderId));

        Assert.Equal("250 Trade Notes", renderedName);
        Assert.DoesNotContain("250,000", renderedName);
        Assert.DoesNotContain("(", renderedName);
        // The slider is visible (a real multi-unit stack) and seeds to 1 —
        // PromissoryNote intersects VendorSplitPolicy.SplitExemptMask.
        Assert.True(slider.Visible);
        Assert.Equal(1u, splitQuantity.Value);
        Assert.Equal(250u, splitQuantity.Maximum);

        controller.Dispose();
    }
}
