using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Runtime;

namespace AcDream.App.Tests.UI.Layout;

public sealed class CharacterTitlesControllerTests
{
    private const uint RowTemplateLayoutId = 0x2100005Eu;
    private const uint RowTemplateElementId = 0x10000536u;
    private const uint RowTextId = 0x10000537u;
    private const uint RowNormalSprite = 0x06004CCAu;
    private const uint RowHighlightSprite = 0x06001AAFu;

    private static ElementInfo BuildRowTemplateInfo()
    {
        var info = new ElementInfo
        {
            Id = RowTemplateElementId,
            Type = 3u,
            Width = 270f,
            Height = 24f,
        };
        info.StateMedia[""] = (RowNormalSprite, 3);
        info.StateMedia["Highlight"] = (RowHighlightSprite, 1);
        info.Children.Add(new ElementInfo
        {
            Id = RowTextId,
            Type = 0xCu,
            Width = 270f,
            Height = 24f,
            HJustify = HJustify.Left,
            FontColor = Vector4.One,
        });
        return info;
    }

    private static UiElement? FakeRowTemplateResolver(uint layoutId, uint elementId)
        => LayoutImporter.Build(BuildRowTemplateInfo(), static _ => (0u, 0, 0), null).Root;

    private sealed class Harness
    {
        public required ImportedLayout Layout;
        public required UiTemplateListBox ListBox;
        public required UiText DisplayText;
        public required UiButton SetDisplayButton;
        public required RuntimeCharacterTitleState Titles;
        public required Dictionary<uint, string> Names;
        public required List<uint> SentTitleIds;
        public required CharacterTitlesController Controller;

        public IReadOnlyList<UiElement> Rows =>
            ListBox.ViewportForTest?.Children ?? [];

        public string RowText(UiElement row) =>
            ((UiText)UiElement.FindDescendant(row, RowTextId)!).LinesProvider().Single().Text;

        public uint RowMedia(UiElement row) =>
            ((UiDatElement)row).ActiveMedia().File;
    }

    // ── Binding seam ─────────────────────────────────────────────────────

    [Fact]
    public void Bind_FindsEveryTitlesPageElement_InTheRealImportedFixture()
    {
        Harness h = BindWithEarnedTitles([], displayTitleId: 0u);

        Assert.NotNull(h.Controller);
        Assert.Equal(CharacterTitlesController.TitleListBoxId, h.ListBox.DatElementId);
        Assert.Equal(CharacterTitlesController.CurrentDisplayTitleTextId, h.DisplayText.DatElementId);
        Assert.Equal(CharacterTitlesController.SetDisplayButtonId, h.SetDisplayButton.DatElementId);
        var scrollbar = Assert.IsType<UiScrollbar>(
            h.Layout.FindElement(h.ListBox.ScrollbarElementId));
        Assert.Same(h.ListBox.Scroll, scrollbar.Model);
    }

    [Fact]
    public void Fixture_PageCaptions_ResolveToNonEmptyText()
    {
        static string? StubResolve(UiStringInfoValue info) =>
            info.TableId != 0u && info.StringId != 0u ? "<resolved>" : null;
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCharacterInfos(),
            static _ => (0u, 0, 0),
            null,
            stringResolve: StubResolve);

        var currentTitleCaption = Assert.IsType<UiText>(layout.FindElement(0x1000052Eu));
        var titlesEarnedCaption = Assert.IsType<UiText>(layout.FindElement(0x10000531u));

        Assert.Equal("<resolved>", currentTitleCaption.LinesProvider()[0].Text);
        Assert.Equal("<resolved>", titlesEarnedCaption.LinesProvider()[0].Text);
    }

    [Fact]
    public void Bind_MissingListBox_ReturnsNullWithoutThrowing()
    {
        var root = new UiPanel();
        var titles = new RuntimeCharacterTitleState();

        CharacterTitlesController? controller = CharacterTitlesController.Bind(
            root,
            titles,
            static _ => null,
            FakeRowTemplateResolver,
            static _ => new RuntimeCommandResult(RuntimeCommandStatus.Inactive, default));

        Assert.Null(controller);
    }

    [Fact]
    public void RowTemplateResolver_ReceivesTheFixturesOwnAuthoredTemplateIds()
    {
        var seen = new List<(uint LayoutId, uint ElementId)>();
        UiElement? Recording(uint layoutId, uint elementId)
        {
            seen.Add((layoutId, elementId));
            return FakeRowTemplateResolver(layoutId, elementId);
        }

        BindWithEarnedTitles(
            [1u], displayTitleId: 0u,
            names: new() { [1u] = "Adventurer" },
            rowResolver: Recording);

        (uint layoutId, uint elementId) = Assert.Single(seen);
        Assert.Equal(RowTemplateLayoutId, layoutId);
        Assert.Equal(RowTemplateElementId, elementId);
    }

    private static Harness BindWithEarnedTitles(
        IReadOnlyList<uint> earnedIds,
        uint displayTitleId,
        Dictionary<uint, string>? names = null,
        Func<uint, uint, UiElement?>? rowResolver = null)
    {
        ImportedLayout layout = FixtureLoader.LoadCharacter();
        var titles = new RuntimeCharacterTitleState();
        titles.ReplaceTable(displayTitleId, earnedIds.ToArray());
        Dictionary<uint, string> resolvedNames = names ?? new Dictionary<uint, string>();
        var sent = new List<uint>();

        RuntimeCommandResult SendSetTitle(uint id)
        {
            sent.Add(id);
            return new RuntimeCommandResult(RuntimeCommandStatus.Accepted, default);
        }

        string? ResolveTitle(uint id) =>
            resolvedNames.TryGetValue(id, out string? name) ? name : null;

        CharacterTitlesController? controller = CharacterTitlesController.Bind(
            layout.Root,
            titles,
            ResolveTitle,
            rowResolver ?? FakeRowTemplateResolver,
            SendSetTitle);
        Assert.NotNull(controller);

        var listBox = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(CharacterTitlesController.TitleListBoxId));
        var displayText = Assert.IsType<UiText>(
            layout.FindElement(CharacterTitlesController.CurrentDisplayTitleTextId));
        var button = Assert.IsType<UiButton>(
            layout.FindElement(CharacterTitlesController.SetDisplayButtonId));

        return new Harness
        {
            Layout = layout,
            ListBox = listBox,
            DisplayText = displayText,
            SetDisplayButton = button,
            Titles = titles,
            Names = resolvedNames,
            SentTitleIds = sent,
            Controller = controller!,
        };
    }


    [Fact]
    public void Rows_AreSortedAlphabeticallyByResolvedTitleText()
    {
        Harness h = BindWithEarnedTitles(
            [13u, 5u, 1u],
            displayTitleId: 0u,
            names: new()
            {
                [1u] = "Adventurer",
                [5u] = "Life Mage",
                [13u] = "War Mage",
            });

        Assert.Equal(3, h.Rows.Count);
        Assert.Equal(
            ["Adventurer", "Life Mage", "War Mage"],
            h.Rows.Select(h.RowText));
    }

    [Fact]
    public void Rows_UnresolvedTitle_ProducesNoRow()
    {
        Harness h = BindWithEarnedTitles(
            [99u],
            displayTitleId: 0u,
            names: []);

        Assert.Empty(h.Rows);
    }

    [Fact]
    public void Rows_TitleIdZero_ProducesNoRow()
    {
        Harness h = BindWithEarnedTitles(
            [0u, 1u],
            displayTitleId: 0u,
            names: new() { [0u] = "Should Never Appear", [1u] = "Adventurer" });

        UiElement row = Assert.Single(h.Rows);
        Assert.Equal("Adventurer", h.RowText(row));
    }

    // ── Selection + highlight ─────────────────────────────────────────────

    [Fact]
    public void SelectingARow_AppliesHighlightMedia_AndDeselectsTheOthers()
    {
        Harness h = BindWithEarnedTitles(
            [1u, 5u],
            displayTitleId: 0u,
            names: new() { [1u] = "Adventurer", [5u] = "Life Mage" });
        UiElement first = h.Rows[0];
        UiElement second = h.Rows[1];

        ((UiDatElement)first).OnClick!();

        Assert.Equal(RowHighlightSprite, h.RowMedia(first));
        Assert.Equal(RowNormalSprite, h.RowMedia(second));

        ((UiDatElement)second).OnClick!();

        Assert.Equal(RowNormalSprite, h.RowMedia(first));
        Assert.Equal(RowHighlightSprite, h.RowMedia(second));
    }


    [Fact]
    public void Ghost_NoSelection_ButtonIsGhosted()
    {
        Harness h = BindWithEarnedTitles([1u], displayTitleId: 0u, names: new() { [1u] = "Adventurer" });

        Assert.False(h.SetDisplayButton.Enabled);
    }

    [Fact]
    public void Ghost_SelectedRowEqualsCurrentDisplayTitle_ButtonIsGhosted()
    {
        Harness h = BindWithEarnedTitles(
            [1u, 5u], displayTitleId: 5u,
            names: new() { [1u] = "Adventurer", [5u] = "Life Mage" });
        UiElement lifeMageRow = h.Rows.Single(r => h.RowText(r) == "Life Mage");

        ((UiDatElement)lifeMageRow).OnClick!();

        Assert.False(h.SetDisplayButton.Enabled);
    }

    [Fact]
    public void Ghost_SelectedRowDiffersFromCurrentDisplayTitle_ButtonIsNormal()
    {
        Harness h = BindWithEarnedTitles(
            [1u, 5u], displayTitleId: 5u,
            names: new() { [1u] = "Adventurer", [5u] = "Life Mage" });
        UiElement adventurerRow = h.Rows.Single(r => h.RowText(r) == "Adventurer");

        ((UiDatElement)adventurerRow).OnClick!();

        Assert.True(h.SetDisplayButton.Enabled);
    }


    [Fact]
    public void ClickingSetDisplay_SendsExactlyOneSetTitleWithTheSelectedId_AndMutatesNothingLocally()
    {
        Harness h = BindWithEarnedTitles(
            [1u, 13u], displayTitleId: 1u,
            names: new() { [1u] = "Adventurer", [13u] = "War Mage" });
        UiElement warMageRow = h.Rows.Single(r => h.RowText(r) == "War Mage");
        ((UiDatElement)warMageRow).OnClick!();
        List<UiElement> rowsBeforeClick = h.Rows.ToList();

        h.SetDisplayButton.OnClick!();

        Assert.Equal([13u], h.SentTitleIds);
        Assert.Equal(1u, h.Titles.DisplayTitleId);
        Assert.Equal("Adventurer", h.DisplayText.LinesProvider().Single().Text);
        Assert.Equal(rowsBeforeClick, h.Rows);
        Assert.Equal(RowHighlightSprite, h.RowMedia(warMageRow));
        Assert.True(h.SetDisplayButton.Enabled);
    }

    [Fact]
    public void ClickingSetDisplay_WhileGhosted_SendsNothing()
    {
        Harness h = BindWithEarnedTitles([1u], displayTitleId: 0u, names: new() { [1u] = "Adventurer" });

        h.SetDisplayButton.OnClick!();

        Assert.Empty(h.SentTitleIds);
    }

    // ── Wire events ─────────────────────────────────────────────────────

    [Fact]
    public void TableReplaced_RebuildsRows_AndClearsSelection()
    {
        Harness h = BindWithEarnedTitles(
            [1u], displayTitleId: 0u, names: new() { [1u] = "Adventurer", [13u] = "War Mage" });
        ((UiDatElement)h.Rows[0]).OnClick!();
        Assert.True(h.SetDisplayButton.Enabled); // selected, differs from display(0)

        h.Titles.ReplaceTable(0u, [13u]);

        Assert.Equal(["War Mage"], h.Rows.Select(h.RowText));
        // The previously-selected row no longer exists post-rebuild -> back
        // to the no-selection Ghosted state.
        Assert.False(h.SetDisplayButton.Enabled);
    }

    [Fact]
    public void TableReplaced_ClearsSelection_EvenWhenTheSelectedIdIsStillEarned()
    {
        Harness h = BindWithEarnedTitles(
            [1u, 5u], displayTitleId: 0u,
            names: new() { [1u] = "Adventurer", [5u] = "Life Mage" });
        UiElement lifeMageRow = h.Rows.Single(r => h.RowText(r) == "Life Mage");
        ((UiDatElement)lifeMageRow).OnClick!();
        Assert.True(h.SetDisplayButton.Enabled);

        h.Titles.ReplaceTable(0u, [1u, 5u]);

        UiElement rebuiltLifeMageRow = h.Rows.Single(r => h.RowText(r) == "Life Mage");
        Assert.Equal(RowNormalSprite, h.RowMedia(rebuiltLifeMageRow));
        Assert.False(h.SetDisplayButton.Enabled);
    }

    [Fact]
    public void DisplayTitleChanged_ClearsSelection_EvenWhenTheSelectedIdIsStillEarned()
    {
        Harness h = BindWithEarnedTitles(
            [1u, 5u], displayTitleId: 0u,
            names: new() { [1u] = "Adventurer", [5u] = "Life Mage" });
        UiElement adventurerRow = h.Rows.Single(r => h.RowText(r) == "Adventurer");
        ((UiDatElement)adventurerRow).OnClick!();
        Assert.Equal(RowHighlightSprite, h.RowMedia(adventurerRow));
        Assert.True(h.SetDisplayButton.Enabled); // selected(1) != display(0)

        h.Titles.ApplyUpdateTitle(5u, setAsDisplay: true);

        Assert.Equal(RowNormalSprite, h.RowMedia(adventurerRow));
        Assert.False(h.SetDisplayButton.Enabled);
    }

    [Fact]
    public void TitleAdded_PreservesSelection()
    {
        Harness h = BindWithEarnedTitles(
            [1u], displayTitleId: 0u,
            names: new() { [1u] = "Adventurer", [13u] = "War Mage" });
        UiElement adventurerRow = Assert.Single(h.Rows);
        ((UiDatElement)adventurerRow).OnClick!();
        Assert.Equal(RowHighlightSprite, h.RowMedia(adventurerRow));
        Assert.True(h.SetDisplayButton.Enabled);

        h.Titles.ApplyUpdateTitle(13u, setAsDisplay: false);

        UiElement rebuiltAdventurerRow = h.Rows.Single(r => h.RowText(r) == "Adventurer");
        Assert.Equal(RowHighlightSprite, h.RowMedia(rebuiltAdventurerRow));
        Assert.True(h.SetDisplayButton.Enabled);
    }

    [Fact]
    public void TitleAdded_InsertsExactlyOneRow_PreservingExistingRowsInSortedOrder()
    {
        Harness h = BindWithEarnedTitles(
            [1u], displayTitleId: 0u,
            names: new() { [1u] = "Adventurer", [5u] = "Life Mage" });
        Assert.Single(h.Rows);

        h.Titles.ApplyUpdateTitle(5u, setAsDisplay: false);

        Assert.Equal(["Adventurer", "Life Mage"], h.Rows.Select(h.RowText));
    }

    [Fact]
    public void DisplayTitleChanged_UpdatesTextAndReevaluatesGhost()
    {
        Harness h = BindWithEarnedTitles(
            [1u, 5u], displayTitleId: 0u,
            names: new() { [1u] = "Adventurer", [5u] = "Life Mage" });
        UiElement lifeMageRow = h.Rows.Single(r => h.RowText(r) == "Life Mage");
        ((UiDatElement)lifeMageRow).OnClick!();
        Assert.True(h.SetDisplayButton.Enabled); // selected(5) != display(0)

        // Simulates the server echo (0x002B UpdateTitle, setAsDisplay=true)
        // that a real Set-as-Display send would eventually produce.
        h.Titles.ApplyUpdateTitle(5u, setAsDisplay: true);

        Assert.Equal("Life Mage", h.DisplayText.LinesProvider().Single().Text);
        Assert.False(h.SetDisplayButton.Enabled);
    }

    [Fact]
    public void DisplayTitleText_UnresolvedId_ShowsRetailUnknownLiteral()
    {
        Harness h = BindWithEarnedTitles([1u], displayTitleId: 77u, names: new() { [1u] = "Adventurer" });

        Assert.Equal("Unknown", h.DisplayText.LinesProvider().Single().Text);
    }

    [Fact]
    public void DisplayTitleText_NoDisplayTitleSet_ShowsRetailUnknownLiteral()
    {
        Harness h = BindWithEarnedTitles([1u], displayTitleId: 0u, names: new() { [1u] = "Adventurer" });

        Assert.Equal("Unknown", h.DisplayText.LinesProvider().Single().Text);
    }


    [Fact]
    public void TitlesList_ReflowsWithWindowResize_AndScrollbarOverflowFlips()
    {
        uint[] earnedIds = Enumerable.Range(1, 15).Select(i => (uint)i).ToArray();
        var names = earnedIds.ToDictionary(id => id, id => $"Title {id}");
        Harness h = BindWithEarnedTitles(earnedIds, displayTitleId: 0u, names: names);
        Assert.Equal(15, h.Rows.Count);

        var root = new UiRoot { Width = 1280, Height = 1400 };
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            root,
            h.Layout.Root,
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
                MinHeight = 40f,
                ContentAnchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Bottom,
            });

        ApplyAnchors(handle.OuterFrame);

        var scrollbar = Assert.IsType<UiScrollbar>(
            h.Layout.FindElement(h.ListBox.ScrollbarElementId));
        Assert.Same(h.ListBox.Scroll, scrollbar.Model);

        float originalListHeight = h.ListBox.Height;
        h.ListBox.ViewportForTest!.LayoutScrollableChildren();
        Assert.False(
            h.ListBox.Scroll.HasOverflow,
            "the fixture's authored default height fits all 15 rows without scrolling");

        handle.ResizeTo(handle.Width, 200f);
        ApplyAnchors(handle.OuterFrame);
        h.ListBox.ViewportForTest!.LayoutScrollableChildren();

        Assert.True(h.ListBox.Height < originalListHeight, "the Titles list must shrink with the window");
        Assert.True(h.ListBox.Scroll.HasOverflow, "360px of rows must overflow the shrunk view");
        Assert.Equal((int)MathF.Floor(h.ListBox.ViewportForTest!.Height), h.ListBox.Scroll.ViewHeight);

        // Growing back restores the original height and the no-overflow state.
        handle.ResizeTo(handle.Width, handle.AuthoredHeight);
        ApplyAnchors(handle.OuterFrame);
        h.ListBox.ViewportForTest!.LayoutScrollableChildren();

        Assert.Equal(originalListHeight, h.ListBox.Height, precision: 2);
        Assert.False(h.ListBox.Scroll.HasOverflow);
    }

    private static void ApplyAnchors(UiElement parent)
    {
        foreach (UiElement child in parent.Children)
        {
            child.ApplyAnchor(parent.Width, parent.Height);
            ApplyAnchors(child);
        }
    }


    [Fact]
    public void TitlesPage_Divider_ClipsAwayAtTheCT6Default_AndAppearsWhenTheWindowGrowsTaller()
    {
        ImportedLayout layout = LayoutImporter.Build(
            FixtureLoader.LoadCharacterInfos(), id => (id, 8, 8), null);
        CharacterStatController.Bind(
            layout, SampleData.SampleCharacter, spriteResolve: id => (id, 8, 8));

        var titlesTab = Assert.IsType<UiText>(
            layout.FindElement(CharacterStatController.TabTitlesId));
        Assert.NotNull(titlesTab.OnClick);
        titlesTab.OnClick!();   // the REAL tab-switch path — flips TitlesPage.Visible

        UiElement divider = UiElement.FindDescendant(layout.Root, 0x10000530u)!;
        Assert.NotNull(divider);
        UiElement siblingDivider = UiElement.FindDescendant(layout.Root, 0x10000534u)!;
        Assert.NotNull(siblingDivider);

        var screen = new UiRoot { Width = 1600f, Height = 1200f };
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            screen,
            layout.Root,
            id => (id, 8, 8),
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Character,
                Chrome = RetailWindowChrome.NineSlice,
                Left = 0f,
                Top = 0f,
                ContentHeight = 362f,
                MinWidth = 310f,
                MaxWidth = 310f,
                MinHeight = 372f,
                MaxHeight = 1000f,
                ResizeX = false,
                ResizeY = true,
                ContentAnchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Bottom,
            });
        Assert.Equal(372f, handle.Height);

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(screen.Width, screen.Height));
        var ctx = new UiRenderContext(renderer, new Vector2(screen.Width, screen.Height));
        handle.OuterFrame.DrawSelfAndChildren(ctx);

        Vector2 dividerAtDefault = divider.ScreenPosition;
        Assert.True(
            dividerAtDefault.Y + divider.Height <= 0f,
            "expected the Titles divider to compute a Y above the window at the 372px " +
            $"default (owner-reported ≈-178); got {dividerAtDefault.Y}");
        // Nothing at all may render meaningfully above the window's own top
        // edge (Y=0 itself is the window's own top border/frame, not "above
        // the window") — the exact shape of the owner's screenshot finding.
        AssertNoQuadCoversY(renderer, -10_000f, -1f);

        handle.OuterFrame.Height = 600f;
        renderer.Begin(new Vector2(screen.Width, screen.Height));
        handle.OuterFrame.DrawSelfAndChildren(ctx);
        renderer.Begin(new Vector2(screen.Width, screen.Height));
        handle.OuterFrame.DrawSelfAndChildren(ctx);

        Vector2 dividerGrown = divider.ScreenPosition;
        Assert.True(
            dividerGrown.Y >= 0f && dividerGrown.Y + divider.Height <= 600f,
            "expected the Titles divider to land inside the grown window at its authored " +
            $"spot; got {dividerGrown.Y}");

        AssertQuadCoversRect(
            renderer, dividerGrown.X, dividerGrown.Y,
            dividerGrown.X + divider.Width, dividerGrown.Y + divider.Height);

        divider.Visible = false;
        renderer.Begin(new Vector2(screen.Width, screen.Height));
        handle.OuterFrame.DrawSelfAndChildren(ctx);
        AssertNoQuadCoversRect(
            renderer, dividerGrown.X, dividerGrown.Y,
            dividerGrown.X + divider.Width, dividerGrown.Y + divider.Height);
    }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private static void AssertNoQuadCoversY(TextRenderer renderer, float yLo, float yHi)
    {
        foreach (var seg in renderer.DebugSpriteSegmentVerts)
        {
            for (int i = 0; i < seg.Verts.Count / 8; i++)
            {
                float vy = seg.Verts[i * 8 + 1];
                Assert.False(
                    vy > yLo - 0.01f && vy < yHi + 0.01f,
                    $"unexpected quad vertex at Y={vy} inside the clipped-away range " +
                    $"[{yLo},{yHi}] (texture {seg.Texture})");
            }
        }
    }

    private static void AssertQuadCoversRect(TextRenderer renderer, float xLo, float yLo, float xHi, float yHi)
    {
        bool found = renderer.DebugSpriteSegmentVerts.Any(seg =>
        {
            for (int i = 0; i < seg.Verts.Count / 8; i++)
            {
                float vx = seg.Verts[i * 8];
                float vy = seg.Verts[i * 8 + 1];
                if (vx >= xLo - 0.5f && vx <= xHi + 0.5f && vy >= yLo - 0.5f && vy <= yHi + 0.5f)
                    return true;
            }
            return false;
        });
        Assert.True(found, $"expected at least one quad vertex inside rect [{xLo},{yLo}]..[{xHi},{yHi}]");
    }

    private static void AssertNoQuadCoversRect(TextRenderer renderer, float xLo, float yLo, float xHi, float yHi)
    {
        foreach (var seg in renderer.DebugSpriteSegmentVerts)
        {
            for (int i = 0; i < seg.Verts.Count / 8; i++)
            {
                float vx = seg.Verts[i * 8];
                float vy = seg.Verts[i * 8 + 1];
                Assert.False(
                    vx >= xLo - 0.5f && vx <= xHi + 0.5f && vy >= yLo - 0.5f && vy <= yHi + 0.5f,
                    $"unexpected quad vertex at ({vx},{vy}) inside the divider's own rect " +
                    $"[{xLo},{yLo}]..[{xHi},{yHi}] after hiding it");
            }
        }
    }

    private static void AssertQuadCoversY(TextRenderer renderer, float yLo, float yHi)
    {
        bool found = renderer.DebugSpriteSegmentVerts.Any(seg =>
        {
            for (int i = 0; i < seg.Verts.Count / 8; i++)
            {
                float vy = seg.Verts[i * 8 + 1];
                if (vy >= yLo - 0.5f && vy <= yHi + 0.5f) return true;
            }
            return false;
        });
        Assert.True(found, $"expected at least one quad in Y range [{yLo},{yHi}]");
    }

    // ── Lifecycle ───────────────────────────────────────────────────────

    [Fact]
    public void Dispose_UnsubscribesFromTitleEvents()
    {
        Harness h = BindWithEarnedTitles([1u], displayTitleId: 0u, names: new() { [1u] = "Adventurer" });

        h.Controller.Dispose();

        // Must not throw, and must not rebuild the (now-orphaned) rows.
        h.Titles.ReplaceTable(0u, [1u, 5u]);
        Assert.Single(h.Rows);
    }
}
