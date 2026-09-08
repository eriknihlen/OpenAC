using System.Collections.Generic;
using AcDream.App.UI;
using AcDream.Core.Chat;
using AcDream.UI.Abstractions.Panels.SpewBox;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.UI;

public sealed class SpewBoxControllerTests
{
    [Fact]
    public void Controller_IsClickThroughOverlayAndDisposesFromRetainedRoot()
    {
        var root = new UiRoot
        {
            Width = 1280f,
            Height = 720f,
        };
        var state = new SpewBoxState();

        using (var controller = new SpewBoxController(root, new SpewBoxVM(state)))
        {
            UiText text = Assert.IsType<UiText>(root.Children.OfType<UiText>().Single());
            Assert.False(text.Visible);
            Assert.True(text.ClickThrough);
            Assert.Equal(int.MaxValue, text.ZOrder);

            state.Enqueue("You can't jump while in the air");

            // Drive the frame hook, NOT the provider — this is the tick
            // that drains the pending queue in production, wired through
            // UiRoot.Tick's IUiGlobalTimeListener broadcast.
            root.Tick(dt: 0d, nowMs: 1000L);

            UiText.Line line = Assert.Single(text.LinesProvider!());
            Assert.Equal("You can't jump while in the air", line.Text);
            Assert.True(text.Visible);
        }

        Assert.Empty(root.Children);
    }

    [Fact]
    public void Controller_NoContent_LeavesTextHiddenWithEmptyLines()
    {
        var root = new UiRoot { Width = 1280f, Height = 720f };
        using var controller = new SpewBoxController(root, new SpewBoxVM(new SpewBoxState()));

        root.Tick(dt: 0d, nowMs: 1000L);

        UiText text = Assert.IsType<UiText>(root.Children.OfType<UiText>().Single());
        Assert.Empty(text.LinesProvider!());
        Assert.False(text.Visible);
    }

    [Fact]
    public void Controller_BecomesVisible_WithoutAnyDrawHavingHappenedFirst()
    {
        var root = new UiRoot { Width = 1280f, Height = 720f };
        var state = new SpewBoxState();
        using var controller = new SpewBoxController(root, new SpewBoxVM(state));

        UiText text = Assert.IsType<UiText>(root.Children.OfType<UiText>().Single());
        Assert.False(text.Visible);

        state.Enqueue("Out of Range!");
        root.Tick(dt: 0d, nowMs: 500L);

        Assert.True(text.Visible);
        Assert.Equal("Out of Range!", Assert.Single(text.LinesProvider!()).Text);
    }

    [Fact]
    public void Controller_PendingQueueDrains_WithoutAnyDrawPass()
    {
        var root = new UiRoot { Width = 1280f, Height = 720f };
        var state = new SpewBoxState();
        using var controller = new SpewBoxController(root, new SpewBoxVM(state));

        state.Enqueue("first");
        Assert.Equal(0, state.Count); // still pending, not yet drained

        root.Tick(dt: 0d, nowMs: 100L);

        Assert.Equal(1, state.Count); // drained into the visible list
        Assert.Equal("first", state.Snapshot()[0].Text);
    }

    [Fact]
    public void Controller_QueueStaysBounded_AcrossManyTicksWithoutADrawPass()
    {
        var root = new UiRoot { Width = 1280f, Height = 720f };
        var state = new SpewBoxState();
        using var controller = new SpewBoxController(root, new SpewBoxVM(state));

        for (int i = 0; i < 50; i++)
        {
            state.Enqueue($"line {i}");
            root.Tick(dt: 0d, nowMs: 1000L + i);
            Assert.True(state.Count <= SpewBoxState.MaxConcurrentItems);
        }

        UiText text = Assert.IsType<UiText>(root.Children.OfType<UiText>().Single());
        Assert.True(text.LinesProvider!().Count <= SpewBoxState.MaxConcurrentItems);
        Assert.Equal("line 49", text.LinesProvider!()[0].Text);
    }

    [Fact]
    public void Controller_RenderedOrder_NewerMessageIsTheTopmostLine()
    {
        var root = new UiRoot { Width = 1280f, Height = 720f };
        var state = new SpewBoxState();
        using var controller = new SpewBoxController(root, new SpewBoxVM(state));

        state.Enqueue("older message");
        root.Tick(dt: 0d, nowMs: 1000L);

        state.Enqueue("newer message");
        root.Tick(dt: 0d, nowMs: 1001L);

        UiText text = Assert.IsType<UiText>(root.Children.OfType<UiText>().Single());
        IReadOnlyList<UiText.Line> rendered = text.LinesProvider!();
        Assert.Equal(2, rendered.Count);
        Assert.Equal("newer message", rendered[0].Text);
        Assert.Equal("older message", rendered[1].Text);
    }

    [Fact]
    public void Dispose_RemovesBothTheTextAndTheGlobalTimeSinkFromTheRoot()
    {
        var root = new UiRoot { Width = 1280f, Height = 720f };
        var controller = new SpewBoxController(root, new SpewBoxVM(new SpewBoxState()));

        Assert.Equal(2, root.Children.Count); // UiText + the tick sink

        controller.Dispose();

        Assert.Empty(root.Children);
    }


    [Fact]
    public void Construction_MountsFlushToTheViewportTop()
    {
        // The user reported the box "still not aligned all the way to the
        // top" — TopOffset moved from the round-1 60px placeholder to 0.
        var root = new UiRoot { Width = 1280f, Height = 720f };
        using var controller = new SpewBoxController(root, new SpewBoxVM(new SpewBoxState()));

        UiText text = Assert.IsType<UiText>(root.Children.OfType<UiText>().Single());
        Assert.Equal(0f, text.Top);
    }

    [Fact]
    public void Tick_KeepsTopFlushAndRecentersX_AcrossAResize()
    {
        var root = new UiRoot { Width = 1280f, Height = 720f };
        var state = new SpewBoxState();
        using var controller = new SpewBoxController(root, new SpewBoxVM(state));

        state.Enqueue("resize me");
        root.Tick(dt: 0d, nowMs: 1000L);

        UiText text = Assert.IsType<UiText>(root.Children.OfType<UiText>().Single());
        float widthBefore = root.Width;
        Assert.Equal((widthBefore - 450f) / 2f, text.Left);
        Assert.Equal(0f, text.Top);

        // Simulate a window resize, then the next per-frame tick.
        root.Width = 1920f;
        root.Tick(dt: 0d, nowMs: 1016L);

        Assert.Equal((1920f - 450f) / 2f, text.Left);
        Assert.Equal(0f, text.Top); // top offset never depends on width
    }

    [Fact]
    public void Construction_WithResolvedRetailFont_WiresDatFontOntoTheText()
    {
        var root = new UiRoot { Width = 1280f, Height = 720f };
        var font = new UiDatFont(
            fgTex: 1, fgW: 64, fgH: 64,
            bgTex: 0, bgW: 0, bgH: 0,
            lineHeight: 11f, baselineOffset: 9f,
            glyphs: new Dictionary<char, FontCharDesc>());

        using var controller = new SpewBoxController(
            root, new SpewBoxVM(new SpewBoxState()), font, debugFont: null);

        UiText text = Assert.IsType<UiText>(root.Children.OfType<UiText>().Single());
        Assert.Same(font, text.DatFont);
        Assert.Equal(11f, text.DatFont!.LineHeight);
    }

    [Fact]
    public void Construction_WithoutAResolvedFont_FallsBackToTheSuppliedDebugFont()
    {
        var root = new UiRoot { Width = 1280f, Height = 720f };

        using var controller = new SpewBoxController(
            root, new SpewBoxVM(new SpewBoxState()), font: null, debugFont: null);

        UiText text = Assert.IsType<UiText>(root.Children.OfType<UiText>().Single());
        Assert.Null(text.DatFont);
        Assert.Null(text.Font);
    }


    [Fact]
    public void RetailFontId_IsTheAuthoredEighteenPixelFace_NotTheRound3Heuristic()
    {
        Assert.Equal(0x40000001u, SpewBoxController.RetailFontId);
    }

    [Fact]
    public void Construction_SetsOutlineTrue_MatchingTheAuthoredLineTemplate()
    {
        var root = new UiRoot { Width = 1280f, Height = 720f };
        using var controller = new SpewBoxController(root, new SpewBoxVM(new SpewBoxState()));

        UiText text = Assert.IsType<UiText>(root.Children.OfType<UiText>().Single());
        Assert.True(text.Outline);
        Assert.Equal(UiRenderContext.DefaultOutlineColor, text.OutlineColor);
    }

    [Fact]
    public void Construction_WithTheAuthoredFont_LineHeightMatchesTheAuthoredEighteenPixelBox()
    {
        var root = new UiRoot { Width = 1280f, Height = 720f };
        var font = new UiDatFont(
            fgTex: 1, fgW: 64, fgH: 64,
            bgTex: 2, bgW: 64, bgH: 64,
            lineHeight: 18f, baselineOffset: 14f,
            glyphs: new Dictionary<char, FontCharDesc>());

        using var controller = new SpewBoxController(
            root, new SpewBoxVM(new SpewBoxState()), font, debugFont: null);

        UiText text = Assert.IsType<UiText>(root.Children.OfType<UiText>().Single());
        Assert.Equal(18f, text.DatFont!.LineHeight);
    }
}
