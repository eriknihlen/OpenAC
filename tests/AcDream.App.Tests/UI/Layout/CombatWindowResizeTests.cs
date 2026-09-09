using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using AcDream.Runtime.Gameplay;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Tests.UI.Layout;

public sealed class CombatWindowResizeTests
{
    [Fact]
    public void HorizontalEdgeDrag_RevealsMoreFavoritesAndPreservesHeight()
    {
        using var fixture = new Fixture(30);
        float height = fixture.Layout.Root.Height;
        fixture.ResizeBy(6 * 32);
        Assert.Equal(747 + 6 * 32, fixture.Layout.Root.Width);
        Assert.Equal(height, fixture.Layout.Root.Height);
        Assert.Equal(24 * 32, fixture.List.Width);
        Assert.Equal(24, fixture.List.Children.OfType<UiCatalogSlot>().Count(slot => slot.Visible));
        Assert.Equal(30, fixture.Spellbook.GetFavorites(0).Count);
        Assert.Equal(6 * 32, fixture.List.Scroll.MaxScroll);
        Assert.True(fixture.List.GetItem(23)!.Visible);
        Assert.False(fixture.List.GetItem(24)!.Visible);
    }

    [Fact]
    public void Resizing_PreservesSelectedFavoriteAndClampsItsScrollRange()
    {
        using var fixture = new Fixture(30);
        fixture.Controller.Handle(InputAction.CombatLastSpell);
        fixture.ResizeBy(6 * 32);
        Assert.Equal(192, fixture.List.Scroll.ScrollY);
        Assert.True(fixture.List.GetItem(29)!.Visible);
        Assert.True(Assert.IsType<UiCatalogSlot>(fixture.List.GetItem(29)).Selected);
        fixture.ResizeBy(-15 * 32);
        Assert.Equal(9 * 32, fixture.List.Width);
        Assert.Equal(672, fixture.List.Scroll.ScrollY);
        Assert.True(fixture.List.GetItem(29)!.Visible);
        Assert.True(Assert.IsType<UiCatalogSlot>(fixture.List.GetItem(29)).Selected);
        fixture.ResizeBy(21 * 32);
        Assert.Equal(0, fixture.List.Scroll.ScrollY);
        Assert.Equal(0, fixture.List.Scroll.MaxScroll);
        Assert.Equal(30, fixture.List.Children.OfType<UiCatalogSlot>().Count(slot => slot.Visible));
    }

    [Fact]
    public void EmptySlots_FollowWidthAcrossTabsWithoutChangingFavorites()
    {
        using var fixture = new Fixture(2);
        fixture.ResizeBy(6 * 32);
        Assert.Equal(24, fixture.List.GetNumUIItems());
        Assert.Equal(22, fixture.List.Children.OfType<UiCatalogSlot>().Count(slot => slot.IsEmptySlot));
        fixture.Controller.Handle(InputAction.CombatNextSpellTab);
        fixture.Controller.Tick();
        var group = fixture.Layout.FindElement(0x100000ABu)!;
        var list = Descendants(group).OfType<UiItemList>().First();
        Assert.Equal(24, list.GetNumUIItems());
        Assert.All(list.Children.OfType<UiCatalogSlot>(), slot => Assert.True(slot.IsEmptySlot));
        Assert.Equal(2, fixture.Spellbook.GetFavorites(0).Count);
        Assert.Empty(fixture.Spellbook.GetFavorites(1));
    }

    [Fact]
    public void ResizeLimits_PreserveMinimumSlotsAndStopAtScreenEdge()
    {
        using var fixture = new Fixture(30);
        fixture.ResizeBy(-1000);
        Assert.Equal(459, fixture.Layout.Root.Width);
        Assert.Equal(9 * 32, fixture.List.Width);
        fixture.ResizeBy(10000);
        Assert.Equal(fixture.Screen.Width, fixture.Layout.Root.Left + fixture.Layout.Root.Width);
        Assert.False(fixture.Layout.Root.ResizeY);
        Assert.Equal(ResizeEdges.Left | ResizeEdges.Right, fixture.Layout.Root.ResizableEdges);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly ImportedLayout Layout;
        public readonly Spellbook Spellbook = new();
        public readonly UiRoot Screen = new() { Width = 1600, Height = 900 };
        public readonly SpellcastingUiController Controller;
        public readonly UiItemList List;

        public Fixture(int favorites)
        {
            var info = FixtureLoader.LoadCombatInfos();
            Layout = LayoutImporter.Build(info, _ => (0u, 0, 0), datFont: null);
            float width = RetailCombatLayout.FitFavoriteSlots(Layout);
            for (uint id = 1; id <= favorites; id++)
            {
                Spellbook.OnSpellLearned(id, 1f);
                Spellbook.SetFavorite(0, (int)id - 1, id);
            }
            var selection = new SelectionState();
            var objects = new ClientObjectTable();
            objects.AddOrUpdate(new ClientObject { ObjectId = 1u, Name = "Player" });
            Controller = SpellcastingUiController.Bind(Layout, Spellbook,
                new RuntimeSpellCastState(Spellbook, selection, new CastOperations()), objects,
                () => 1u, id => id, item => item.ObjectId, _ => { }, selection, null, null)!;
            Assert.NotNull(Controller);
            RetailWindowFrame.Mount(Screen, Layout.Root, _ => (0u, 0, 0),
                RetailCombatLayout.WithHorizontalResize(Layout, new RetailWindowFrame.Options
                {
                    WindowName = "combat", Chrome = RetailWindowChrome.Imported,
                    ContentWidth = width, DatConstraintSource = info,
                    Left = 0, Top = 300, Draggable = false, ContentClickThrough = false,
                }));
            ApplyAnchors(Layout.Root);
            Controller.Tick();
            List = Descendants(Layout.FindElement(0x100000AAu)!).OfType<UiItemList>().First();
        }

        public void ResizeBy(int pixels)
        {
            int x = (int)(Layout.Root.Left + Layout.Root.Width - 2);
            int y = (int)(Layout.Root.Top + Layout.Root.Height / 2);
            Screen.OnMouseDown(UiMouseButton.Left, x, y);
            Screen.OnMouseMove(x + pixels, y + 20);
            Screen.OnMouseUp(UiMouseButton.Left, x + pixels, y + 20);
            ApplyAnchors(Layout.Root);
            Controller.Tick();
        }

        public void Dispose() => Controller.Dispose();
    }

    private static void ApplyAnchors(UiElement parent)
    {
        foreach (UiElement child in parent.Children)
        {
            child.ApplyAnchor(parent.Width, parent.Height);
            ApplyAnchors(child);
        }
    }

    private static IEnumerable<UiElement> Descendants(UiElement parent)
    {
        foreach (UiElement child in parent.Children)
        {
            yield return child;
            foreach (UiElement descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class CastOperations : IRuntimeSpellCastOperations
    {
        public uint LocalPlayerId => 1;
        public bool CanSend => true;
        public bool HasRequiredComponents(uint spellId) => true;
        public bool IsTargetCompatible(uint targetId, SpellMetadata spell, bool showMessage) => true;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }
}
