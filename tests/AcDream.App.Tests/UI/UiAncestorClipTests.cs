using System.Linq;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI;

public sealed class UiAncestorClipTests
{
    private sealed class TestElement : UiElement { }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private static (RecordingGpuDevice device, TextRenderer renderer, UiRenderContext ctx) MakeContext(
        float w, float h)
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(w, h));
        var ctx = new UiRenderContext(renderer, new Vector2(w, h));
        return (device, renderer, ctx);
    }

    private static bool AnyQuadAt(TextRenderer renderer, System.Func<float, float, bool> predicate)
    {
        foreach (var seg in renderer.DebugSpriteSegmentVerts)
        {
            for (int i = 0; i < seg.Verts.Count / 8; i++)
            {
                if (predicate(seg.Verts[i * 8], seg.Verts[i * 8 + 1]))
                    return true;
            }
        }
        return false;
    }

    [Fact]
    public void ChildOutsideParentBounds_RendersNothing_SiblingInsideStillRenders()
    {
        var parent = new TestElement { Width = 50f, Height = 50f };
        var outside = new UiSolidSpriteFill
        {
            Left = -100f, Top = -100f, Width = 20f, Height = 20f,
            SpriteId = 7u,
            SpriteResolve = id => (id, 8, 8),
        };
        var inside = new UiSolidSpriteFill
        {
            Left = 5f, Top = 5f, Width = 10f, Height = 10f,
            SpriteId = 9u,
            SpriteResolve = id => (id, 8, 8),
        };
        parent.AddChild(outside);
        parent.AddChild(inside);

        var (_, renderer, ctx) = MakeContext(200f, 200f);
        parent.DrawSelfAndChildren(ctx);

        Assert.DoesNotContain(renderer.DebugSpriteSegmentVerts, s => s.Texture == 7u);
        Assert.Contains(renderer.DebugSpriteSegmentVerts, s => s.Texture == 9u);
    }

    [Fact]
    public void ChildStraddlingParentEdge_ClipsToTheVisibleSliver()
    {
        var parent = new TestElement { Width = 50f, Height = 50f };
        var straddling = new UiSolidSpriteFill
        {
            Left = 40f, Top = 10f, Width = 30f, Height = 10f,   // spans x=[40,70), parent ends at 50
            SpriteId = 3u,
            SpriteResolve = id => (id, 8, 8),
        };
        parent.AddChild(straddling);

        var (_, renderer, ctx) = MakeContext(200f, 200f);
        parent.DrawSelfAndChildren(ctx);

        var seg = Assert.Single(renderer.DebugSpriteSegmentVerts, s => s.Texture == 3u);
        float maxX = 0f;
        for (int i = 0; i < seg.Verts.Count / 8; i++)
            maxX = System.MathF.Max(maxX, seg.Verts[i * 8]);
        Assert.True(maxX <= 50.01f, $"clipped quad's rightmost X ({maxX}) must not exceed the parent's edge (50)");
    }

    [Fact]
    public void ChildOutsideParentBounds_IsNeverHit()
    {
        var parent = new TestElement { Width = 50f, Height = 50f };
        var outside = new TestElement { Left = -30f, Top = -30f, Width = 20f, Height = 20f };
        parent.AddChild(outside);

        UiElement? hit = parent.HitTest(-20f, -20f);   // lands inside `outside`'s own local rect

        Assert.Null(hit);
    }

    [Fact]
    public void UiMenuPopup_StillRendersOutsideItsOwningWindow_AncestorClipDoesNotCutItOff()
    {
        var root = new TestElement { Width = 200f, Height = 200f };
        var window = new TestElement { Left = 10f, Top = 150f, Width = 80f, Height = 18f };
        var menu = new UiMenu
        {
            Width = 80f,
            Height = 18f,
            Items = new[] { new UiMenu.MenuItem("Row", (object?)null) },
            SpriteResolve = id => (id, 8, 8),
        };
        root.AddChild(window);
        window.AddChild(menu);

        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5)));
        Assert.True(menu.IsOpen);

        var (_, renderer, ctx) = MakeContext(200f, 200f);
        root.DrawOverlays(ctx);

        // The popup opens upward (bottom touches the button's top, y=0), so its
        // absolute screen top is window.Top(150) minus its own outer height — well
        // above window.Top. Assert at least one popup quad renders strictly above the
        // owning window's own top edge, i.e. outside window's [0,18] local rect.
        bool escapedAboveWindow = AnyQuadAt(renderer, (_, y) => y < 150f - 0.5f);
        Assert.True(
            escapedAboveWindow,
            "expected the open UiMenu popup to render above its owning window's top edge");
    }

    [Fact]
    public void UiMenuClosed_NothingRendersAboveItsOwningWindow()
    {
        var root = new TestElement { Width = 200f, Height = 200f };
        var window = new TestElement { Left = 10f, Top = 150f, Width = 80f, Height = 18f };
        var menu = new UiMenu
        {
            Width = 80f,
            Height = 18f,
            Items = new[] { new UiMenu.MenuItem("Row", (object?)null) },
            SpriteResolve = id => (id, 8, 8),
        };
        root.AddChild(window);
        window.AddChild(menu);
        Assert.False(menu.IsOpen);

        var (_, renderer, ctx) = MakeContext(200f, 200f);
        root.DrawOverlays(ctx);

        Assert.False(AnyQuadAt(renderer, (_, y) => y < 150f - 0.5f));
    }

    [Fact]
    public void RetailTooltip_StillRendersOutsideATinyAncestorWindow_BecauseItMountsAtRootLevel()
    {
        const uint popupRootId = 0x900u;
        const uint textChildId = 0x901u;
        const uint popupLayoutDid = 0x21000041u;
        const uint popupBgSprite = 42u;

        ImportedLayout BuildPopup()
        {
            var rootInfo = new ElementInfo
            {
                Id = popupRootId, Type = 3, X = 0, Y = 0, Width = 30, Height = 30,
                TooltipTextChildElementId = textChildId,
            };
            rootInfo.StateMedia[""] = (popupBgSprite, 1);
            var textInfo = new ElementInfo
            {
                Id = textChildId, Type = 12, X = 2, Y = 2, Width = 26, Height = 26,
            };
            return LayoutImporter.BuildFromInfos(
                rootInfo, new[] { textInfo }, id => (id, 8, 8), null);
        }

        var root = new UiRoot { Width = 800f, Height = 600f };
        var presenter = new RetailTooltipPresenter(root, (_, _) => BuildPopup());

        var window = new TestElement { Left = 5f, Top = 5f, Width = 20f, Height = 20f };
        var target = new TestElement
        {
            Left = 2f, Top = 2f, Width = 10f, Height = 10f,
            AuthoredTooltipEnabled = true,
            AuthoredTooltipText = "Rotate left.",
            AuthoredTooltipRootElementId = popupRootId,
            AuthoredTooltipLayoutDid = popupLayoutDid,
        };
        window.AddChild(target);
        root.AddChild(window);

        root.OnMouseMove(10, 10);   // inside `target`, well inside the 20x20 window
        root.Tick(0.016, 0);
        root.Tick(0.016, root.TooltipDelayMs);

        // Mounted as a UiRoot SIBLING of `window`, not nested inside it.
        UiElement popup = Assert.Single(root.Children, c => c != window);
        Assert.Same(root, popup.Parent);

        var (_, renderer, ctx) = MakeContext(800f, 600f);
        root.DrawSelfAndChildren(ctx);

        Assert.Contains(renderer.DebugSpriteSegmentVerts, s => s.Texture == popupBgSprite);

        presenter.Dispose();
    }

    [Fact]
    public void EscapedPopupClick_ReachesTheMenu_ThroughAShortOwningWindow()
    {
        var root = new UiRoot { Width = 200f, Height = 200f };
        var window = new TestElement { Left = 10f, Top = 150f, Width = 80f, Height = 18f };
        string? picked = null;
        var menu = new UiMenu
        {
            Width = 80f,
            Height = 18f,
            OpenUpward = true,
            RowsPerColumn = 1,   // one row -> a small, exactly-known popup rect
            Items = new[] { new UiMenu.MenuItem("Row", (object?)"row") },
            SpriteResolve = id => (id, 8, 8),
        };
        menu.OnSelect = p => picked = p as string;
        window.AddChild(menu);
        root.AddChild(window);

        root.OnMouseDown(UiMouseButton.Left, 20, 155);
        root.OnMouseUp(UiMouseButton.Left, 20, 155);
        Assert.True(menu.IsOpen);

        const int rowScreenY = 135;   // inside [123,150)
        const int rowScreenX = 60;    // inside [10,211)
        Assert.True(rowScreenY < 150, "sanity: the row must sit above the window's own top edge");

        Assert.Null(root.Pick(rowScreenX, rowScreenY));

        // WantsMouse must recognize the escaped popup region too (S4), so a game
        // action does not fire underneath an open dropdown.
        root.OnMouseMove(rowScreenX, rowScreenY);
        Assert.True(root.WantsMouse, "WantsMouse must see the escaped popup through PopupHit");

        root.OnMouseDown(UiMouseButton.Left, rowScreenX, rowScreenY);
        root.OnMouseUp(UiMouseButton.Left, rowScreenX, rowScreenY);

        Assert.Equal("row", picked);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void UiMenuPopup_StillDraws_EvenWhenItsOwningWindowIsFullyClippedAway()
    {
        var root = new TestElement { Width = 200f, Height = 200f };
        var window = new TestElement { Left = 10f, Top = 150f, Width = 0f, Height = 18f };
        var menu = new UiMenu
        {
            Width = 80f,
            Height = 18f,
            Items = new[] { new UiMenu.MenuItem("Row", (object?)null) },
            SpriteResolve = id => (id, 8, 8),
        };
        root.AddChild(window);
        window.AddChild(menu);

        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5)));
        Assert.True(menu.IsOpen);

        var (_, renderer, ctx) = MakeContext(200f, 200f);

        root.DrawSelfAndChildren(ctx);
        Assert.Empty(renderer.DebugSpriteSegmentVerts);

        root.DrawOverlays(ctx);
        Assert.NotEmpty(renderer.DebugSpriteSegmentVerts);
    }

    [Fact]
    public void UnsizedMarkupLabel_InsideASizedPanel_DoesNotSelfClipItsSubtree()
    {
        var (device, renderer, ctx) = MakeContext(800f, 600f);
        var panel = new UiPanel
        {
            Left = 50f, Top = 50f, Width = 200f, Height = 100f,
            BackgroundColor = default, BorderColor = default,
        };
        var label = new UiLabel { Left = 10f, Top = 10f, Text = "MossTank status line" };
        var probe = new UiPanel
        {
            Left = 0f, Top = 0f, Width = 100f, Height = 10f,
            BackgroundSprite = 42u,
            SpriteResolve = id => id == 42u ? (42u, 8, 8) : (0u, 0, 0),
            BackgroundColor = default, BorderColor = default,
        };
        label.AddChild(probe);
        panel.AddChild(label);

        panel.DrawSelfAndChildren(ctx);

        Assert.True(
            AnyQuadAt(renderer, (x, y) => x >= 60f && x <= 160f && y >= 60f && y <= 70f),
            "the unsized label self-clipped its subtree away (MossTank regression)");
        // The box became truthful (fallback-measured without a font), not degenerate.
        Assert.True(label.Width > 0f && label.Height > 0f,
            $"label box stayed degenerate: {label.Width}x{label.Height}");
    }
}
