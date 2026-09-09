using System.IO;
using System.Text.Json;
using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public class DormantDatWidgetConformanceTests
{
    // ── Type 8 — vendor's media-bearing, tab-table-less backdrop ────────────

    [Fact]
    public void Vendor_BackdropElement_DrawsItsAuthoredMedia_AndStaysClickThrough()
    {
        var layout = FixtureLoader.LoadVendor();
        var backdrop = Assert.IsType<UiTabPanel>(layout.FindElement(0x1000008Du));

        Assert.Empty(backdrop.Tabs); // no authored 0x2E on this element
        Assert.False(backdrop.BehaviorActive); // nobody activates it — dormant
        Assert.True(backdrop.ClickThrough); // UiDatElement's generic-decoration default
        (uint file, int _) = backdrop.ActiveMedia();
        Assert.NotEqual(0u, file); // the authored fill still draws
    }

    [Fact]
    public void Vendor_TabHost_StaysDormant_ControllerOwnsSwitching()
    {
        var layout = FixtureLoader.LoadVendor();
        var host = Assert.IsType<UiTabPanel>(layout.FindElement(0x100000B8u));

        Assert.Equal(3, host.Tabs.Count); // the authored table IS present…
        Assert.False(host.BehaviorActive); // …but nobody has activated it
        Assert.Equal(0u, host.ActivePageElementId);
        Assert.True(host.ClickThrough);
    }

    [Fact]
    public void Vendor_TabHost_DormantHost_NeverRaisesActivePageChanged()
    {
        var layout = FixtureLoader.LoadVendor();
        var host = Assert.IsType<UiTabPanel>(layout.FindElement(0x100000B8u));
        int raised = 0;
        host.ActivePageChanged += (_, _) => raised++;

        Assert.False(host.BehaviorActive);

        foreach (UiTabTableEntry tab in host.Tabs)
        {
            var button = layout.FindElement(tab.ButtonElementId);
            Assert.NotNull(button);
            Assert.False(button!.HandlesClick,
                $"dormant tab button 0x{tab.ButtonElementId:X8} has a click "
                + "handler — import-time activation regressed");
        }
        Assert.Equal(0, raised);
    }


    [Fact]
    public void CharacterRoot_StaysClickThrough_AndPropagatesState_WithoutActivation()
    {
        var layout = FixtureLoader.LoadCharacter();
        var root = Assert.IsType<UiTabPanel>(layout.Root);
        Assert.Equal(0x10000227u, root.DatElementId);

        Assert.NotEmpty(root.Tabs); // the authored table IS present…
        Assert.False(root.BehaviorActive); // …but nobody has activated it
        Assert.Equal(0u, root.ActivePageElementId);
        Assert.True(root.ClickThrough);
        Assert.IsAssignableFrom<IUiDatStateful>(root);
    }

    [Fact]
    public void SpellbookRoot_StaysClickThrough_AndPropagatesState_WithoutActivation()
    {
        var layout = FixtureLoader.LoadSpellbook();
        var root = Assert.IsType<UiTabPanel>(layout.Root);
        Assert.Equal(0x100002A8u, root.DatElementId);

        Assert.NotEmpty(root.Tabs);
        Assert.False(root.BehaviorActive);
        Assert.Equal(0u, root.ActivePageElementId);
        Assert.True(root.ClickThrough);
        Assert.IsAssignableFrom<IUiDatStateful>(root);
    }


    [Fact]
    public void Combat_TabHost_PerformsNoImportTimeTakeover()
    {
        var layout = FixtureLoader.LoadCombat();
        var host = Assert.IsType<UiTabPanel>(layout.FindElement(0x100000A2u));

        Assert.Equal(8, host.Tabs.Count); // the authored table IS present…
        Assert.False(host.BehaviorActive); // …but nothing ever activates it
        Assert.Equal(0u, host.ActivePageElementId);
        Assert.Equal(16, host.Children.Count);
        Assert.All(host.Children, child => Assert.True(child.Visible));
    }

    // ── Type 5 — a representative pre-existing ListBox with an authored template ──

    [Fact]
    public void EffectsList_StaysDormant_ClickThrough_NoInjectedViewport()
    {
        var layout = FixtureLoader.LoadPositiveEffects();
        var host = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(EffectsUiController.ListId));

        Assert.NotEmpty(host.Templates); // the authored 0x64 array IS present…
        Assert.Empty(host.Children); // …but no viewport was injected
        Assert.Equal(0, host.ContentHeight);
        Assert.True(host.ClickThrough);
    }

    [Fact]
    public void EveryAuthoredType5Element_HasZeroChildren_AcrossAllFixtures()
    {
        string fixturesDir = Path.Combine(
            AppContext.BaseDirectory, "UI", "Layout", "fixtures");
        string[] fixtures = Directory.GetFiles(fixturesDir, "*.json");
        Assert.NotEmpty(fixtures);

        var opts = new JsonSerializerOptions { IncludeFields = true };
        int type5Seen = 0;
        foreach (string file in fixtures)
        {
            var root = JsonSerializer.Deserialize<ElementInfo>(
                File.ReadAllText(file), opts);
            Assert.NotNull(root);
            Walk(root!, file);
        }

        Assert.True(type5Seen >= 10, $"sweep saw only {type5Seen} Type-5 elements");

        void Walk(ElementInfo node, string file)
        {
            if (node.Type == 5u)
            {
                type5Seen++;
                Assert.True(
                    node.Children.Count == 0,
                    $"{Path.GetFileName(file)}: Type-5 element 0x{node.Id:X8} authors "
                    + $"{node.Children.Count} children — UiTemplateListBox.ConsumesDatChildren "
                    + "would drop them; they must remain available to their controller.");
            }
            foreach (ElementInfo child in node.Children)
                Walk(child, file);
        }
    }
}
