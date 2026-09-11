using System.Linq;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using Xunit;

namespace AcDream.App.Tests.UI;

public class UiScrollbarTests
{

    [Fact]
    public void ThumbRect_AtStart_HasCorrectSizeAndZeroOffset()
    {
        var m = new UiScrollable { ContentHeight = 400, ViewHeight = 100 };
        // PositionRatio = 0 (start).
        var (y, h) = UiScrollbar.ThumbRect(m, trackTop: 0f, trackLen: 200f);
        Assert.Equal(50f, h, 3f);
        Assert.Equal(0f, y, 3f);
    }

    [Fact]
    public void ThumbRect_AtEnd_PinsToBottomOfTrack()
    {
        var m = new UiScrollable { ContentHeight = 400, ViewHeight = 100 };
        m.ScrollToEnd(); // PositionRatio = 1.
        float trackTop = 16f, trackLen = 200f;
        var (y, h) = UiScrollbar.ThumbRect(m, trackTop, trackLen);
        Assert.Equal(50f, h, 3f);
        // y = trackTop + travel * 1 = 16 + 150 = 166.
        Assert.Equal(166f, y, 3f);
    }

    [Fact]
    public void ThumbRect_WithButtonH_CorrectlyOffsetsFromTrackTop()
    {
        var m = new UiScrollable { ContentHeight = 400, ViewHeight = 100 };
        m.ScrollToEnd();
        var (y, h) = UiScrollbar.ThumbRect(m, trackTop: 16f, trackLen: 200f);
        Assert.Equal(50f, h, 3f);
        Assert.Equal(166f, y, 3f); // 16 + 150
    }

    [Fact]
    public void ThumbRect_MidScroll_InterpolatesPosition()
    {
        var m = new UiScrollable { ContentHeight = 400, ViewHeight = 100 };
        m.SetScrollY(150);
        Assert.Equal(0.5f, m.PositionRatio, 3);

        var (y, h) = UiScrollbar.ThumbRect(m, trackTop: 0f, trackLen: 200f);
        Assert.Equal(50f, h, 3f);
        // y = 0 + 150 * 0.5 = 75.
        Assert.Equal(75f, y, 3f);
    }

    [Fact]
    public void ThumbRect_SmallContent_EnforcesMinThumb()
    {
        var m = new UiScrollable { ContentHeight = 1000, ViewHeight = 10 };
        var (_, h) = UiScrollbar.ThumbRect(m, trackTop: 0f, trackLen: 200f);
        Assert.Equal(8f, h, 3f);
    }

    [Fact]
    public void ThumbRect_NoOverflow_ThumbFillsTrack()
    {
        var m = new UiScrollable { ContentHeight = 50, ViewHeight = 100 };
        var (y, h) = UiScrollbar.ThumbRect(m, trackTop: 16f, trackLen: 100f);
        Assert.Equal(100f, h, 3f);
        Assert.Equal(16f, y, 3f); // travel = 0 → y = trackTop
    }

    [Fact]
    public void HorizontalScalar_clickAndDrag_updatesNormalizedValue()
    {
        float value = 1f;
        var bar = new UiScrollbar
        {
            Width = 90f,
            Height = 14f,
            Horizontal = true,
            ScalarChanged = next => value = next,
        };
        bar.SetScalarPosition(1f);

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseDown, Data1: 8)));
        Assert.Equal(0f, value, 3);
        Assert.Equal(0f, bar.ScalarPosition, 3);

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseMove, Data1: 45)));
        Assert.Equal(0.5f, value, 3);
        Assert.Equal(0.5f, bar.ScalarPosition, 3);

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseUp, Data1: 45)));
    }

    [Fact]
    public void VerticalScalar_clickAndDrag_updatesNormalizedValue()
    {
        float value = 1f;
        var bar = new UiScrollbar
        {
            Width = 14f,
            Height = 90f,
            Horizontal = false,
            ScalarChanged = next => value = next,
        };
        bar.SetScalarPosition(1f);

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseDown, Data2: 8)));
        Assert.Equal(0f, value, 3);
        Assert.Equal(0f, bar.ScalarPosition, 3);

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseMove, Data2: 45)));
        Assert.Equal(0.5f, value, 3);
        Assert.Equal(0.5f, bar.ScalarPosition, 3);

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseUp, Data2: 45)));
    }

    // ── OP5 review fix S1: the drag-end seam (IsDragging / DragCompleted) ────

    [Fact]
    public void HorizontalScalar_DragCompleted_FiresOnceAtMouseUp_NotOnEachMove()
    {
        int completedCount = 0;
        var bar = new UiScrollbar
        {
            Width = 90f,
            Height = 14f,
            Horizontal = true,
            ScalarChanged = _ => { },
            DragCompleted = () => completedCount++,
        };
        bar.SetScalarPosition(0f); // thumb spans [0, 16]

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseDown, Data1: 5)));
        Assert.True(bar.IsDragging);
        Assert.Equal(0, completedCount);

        for (int i = 0; i < 10; i++)
        {
            Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseMove, Data1: 10 + i)));
            Assert.Equal(0, completedCount);
        }

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseUp, Data1: 50)));
        Assert.False(bar.IsDragging);
        Assert.Equal(1, completedCount); // drag end fires exactly one
    }

    [Fact]
    public void HorizontalScalar_DragCompleted_DoesNotFireOnAMouseUpThatWasNeverADrag()
    {
        int completedCount = 0;
        var bar = new UiScrollbar
        {
            Width = 90f,
            Height = 14f,
            Horizontal = true,
            ScalarChanged = _ => { },
            DragCompleted = () => completedCount++,
        };

        bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseUp, Data1: 10));
        Assert.Equal(0, completedCount);
    }

    [Fact]
    public void VerticalModel_DragCompleted_FiresOnlyForAnActualThumbDrag_NotAButtonClick()
    {
        var model = new UiScrollable { ContentHeight = 400, ViewHeight = 100 };
        int completedCount = 0;
        var bar = new UiScrollbar { Width = 16f, Height = 200f, Model = model, DragCompleted = () => completedCount++ };

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseDown, Data1: 0, Data2: 5)));
        Assert.False(bar.IsDragging);
        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseUp, Data1: 0, Data2: 5)));
        Assert.Equal(0, completedCount);

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseDown, Data1: 0, Data2: 30)));
        Assert.True(bar.IsDragging);
        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseMove, Data1: 0, Data2: 40)));
        Assert.Equal(0, completedCount);
        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseUp, Data1: 0, Data2: 40)));
        Assert.Equal(1, completedCount);
    }


    [Fact]
    public void CaptureLossMidDrag_EndsTheGesture_CompletesOnce_AndUnlatchesIsDragging()
    {
        int completedCount = 0;
        var bar = new UiScrollbar
        {
            Width = 90f,
            Height = 14f,
            Horizontal = true,
            ScalarChanged = _ => { },
            DragCompleted = () => completedCount++,
        };
        bar.SetScalarPosition(0f);

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseDown, Data1: 5)));
        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseMove, Data1: 30)));
        Assert.True(bar.IsDragging);

        bar.OnEvent(new UiEvent(0u, bar, UiEventType.CaptureChanged));

        Assert.False(bar.IsDragging);
        Assert.Equal(1, completedCount);

        bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseUp, Data1: 30));
        Assert.Equal(1, completedCount);
    }

    [Fact]
    public void CaptureChange_WithNoActiveDrag_IsANoOp()
    {
        int completedCount = 0;
        var bar = new UiScrollbar
        {
            Width = 90f,
            Height = 14f,
            Horizontal = true,
            DragCompleted = () => completedCount++,
        };

        bar.OnEvent(new UiEvent(0u, bar, UiEventType.CaptureChanged));

        Assert.False(bar.IsDragging);
        Assert.Equal(0, completedCount);
    }

    [Fact]
    public void HorizontalScalar_BareTrackClickJump_DefersItsTickAndCompletesExactlyOnce()
    {
        int completedCount = 0;
        bool draggingDuringTick = false;
        UiScrollbar bar = null!;
        bar = new UiScrollbar
        {
            Width = 90f,
            Height = 14f,
            Horizontal = true,
            ScalarChanged = _ => draggingDuringTick = bar.IsDragging,
            DragCompleted = () => completedCount++,
        };
        bar.SetScalarPosition(0f); // thumb spans [0, 16]

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseDown, Data1: 70)));
        Assert.True(draggingDuringTick); // the jump tick saw the latch armed
        Assert.Equal(0, completedCount);

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseUp, Data1: 70)));
        Assert.Equal(1, completedCount);
    }

    [Fact]
    public void HorizontalModel_DragCompleted_FiresOnceAtMouseUp()
    {
        var model = new UiScrollable { ContentHeight = 320, ViewHeight = 80, LineHeight = 32 };
        int completedCount = 0;
        var bar = new UiScrollbar
        {
            Width = 160f,
            Height = 16f,
            Horizontal = true,
            Model = model,
            DragCompleted = () => completedCount++,
        };

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseDown, Data1: 20)));
        Assert.True(bar.IsDragging);
        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseMove, Data1: 144)));
        Assert.Equal(0, completedCount);
        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseUp, Data1: 144)));
        Assert.False(bar.IsDragging);
        Assert.Equal(1, completedCount);
    }

    [Fact]
    public void HorizontalModel_ButtonsTrackAndThumbDriveSharedScroll()
    {
        var model = new UiScrollable { ContentHeight = 320, ViewHeight = 80, LineHeight = 32 };
        var bar = new UiScrollbar
        {
            Width = 160f,
            Height = 16f,
            Horizontal = true,
            Model = model,
        };

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseDown, Data1: 159)));
        Assert.Equal(32, model.ScrollY);

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseDown, Data1: 100)));
        Assert.True(model.ScrollY >= 80);

        model.SetScrollY(0);
        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseDown, Data1: 20)));
        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseMove, Data1: 144)));
        Assert.Equal(model.MaxScroll, model.ScrollY);
        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseUp, Data1: 144)));
    }

    [Fact]
    public void HorizontalModel_UsesAuthoredArrowExtentsAndOneCellStep()
    {
        var model = new UiScrollable
        {
            ContentHeight = 640,
            ViewHeight = 320,
            LineHeight = 32,
        };
        model.SetScrollY(64);
        var bar = new UiScrollbar
        {
            Width = 160f,
            Height = 36f,
            Horizontal = true,
            Model = model,
            DecrementButtonExtent = 23f,
            IncrementButtonExtent = 29f,
        };

        Assert.True(bar.OnEvent(new UiEvent(
            0u, bar, UiEventType.MouseDown, Data1: 22)));
        Assert.Equal(32, model.ScrollY);

        Assert.True(bar.OnEvent(new UiEvent(
            0u, bar, UiEventType.MouseDown, Data1: 131)));
        Assert.Equal(64, model.ScrollY);
    }

    [Fact]
    public void HorizontalModel_UsesIndependentRolloverAndPressedArrowMedia()
    {
        var root = new UiRoot { Width = 300f, Height = 100f };
        var model = new UiScrollable
        {
            ContentHeight = 640,
            ViewHeight = 320,
            LineHeight = 32,
        };
        var bar = new UiScrollbar
        {
            Width = 160f,
            Height = 36f,
            Horizontal = true,
            Model = model,
            UpSprite = 1u,
            UpRolloverSprite = 2u,
            UpPressedSprite = 3u,
            DownSprite = 4u,
            DownRolloverSprite = 5u,
            DownPressedSprite = 6u,
        };
        root.AddChild(bar);

        root.OnMouseMove(5, 10);
        Assert.Equal(2u, bar.ActiveStartSpriteForTest);
        Assert.Equal(4u, bar.ActiveEndSpriteForTest);

        root.OnMouseMove(155, 10);
        Assert.Equal(1u, bar.ActiveStartSpriteForTest);
        Assert.Equal(5u, bar.ActiveEndSpriteForTest);

        root.OnMouseDown(UiMouseButton.Left, 155, 10);
        Assert.Equal(6u, bar.ActiveEndSpriteForTest);
        root.OnMouseUp(UiMouseButton.Left, 155, 10);
        Assert.Equal(5u, bar.ActiveEndSpriteForTest);
    }

    [Fact]
    public void ModelWithoutOverflow_IsDisabledAndHideDisabledSuppressesPresentation()
    {
        var model = new UiScrollable
        {
            ContentHeight = 320,
            ViewHeight = 320,
            LineHeight = 32,
        };
        var bar = new UiScrollbar
        {
            Width = 160f,
            Height = 36f,
            Horizontal = true,
            Model = model,
            HideWhenDisabled = true,
        };

        Assert.True(bar.IsModelDisabled);
        Assert.False(bar.IsPresentationVisible);
        Assert.False(bar.OnEvent(new UiEvent(
            0u, bar, UiEventType.MouseDown, Data1: 159)));
        Assert.Equal(0, model.ScrollY);
    }

    [Theory]
    [InlineData(0f,   0f,   0f)]
    [InlineData(0.5f, 0f,  50f)]
    [InlineData(1f,   0f, 100f)]
    public void ScalarFillRect_CombatPower_GrowsLeftToRight(
        float fill, float expectedX, float expectedWidth)
    {
        var (x, width) = UiScrollbar.ScalarFillRect(100f, fill, fromRight: false);
        Assert.Equal(expectedX, x, 3);
        Assert.Equal(expectedWidth, width, 3);
    }

    [Theory]
    [InlineData(0f,   104f,   0f)]
    [InlineData(0.5f, 104f, 149.5f)]
    [InlineData(1f,   104f, 299f)]
    public void ScalarFillRect_CombatPower_StaysBetweenAuthoredLabels(
        float fill, float expectedX, float expectedWidth)
    {
        var (x, width) = UiScrollbar.ScalarFillRect(
            rangeLeft: 104f, rangeWidth: 299f, fill, fromRight: false);
        Assert.Equal(expectedX, x, 3);
        Assert.Equal(expectedWidth, width, 3);
    }

    [Fact]
    public void NoOverflow_DrawsAFullTrackThumb()
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        var ctx = new UiRenderContext(renderer, new Vector2(800f, 600f));

        const uint topTex = 60u, midTex = 63u, botTex = 66u;
        var model = new UiScrollable { ContentHeight = 150, ViewHeight = 150 };
        var bar = new UiScrollbar
        {
            Width = 16f,
            Height = 200f,
            SpriteResolve = id => id is topTex or midTex or botTex ? (id, 16, 3) : (0u, 0, 0),
            ThumbTopSprite = topTex,
            ThumbSprite = midTex,
            ThumbBotSprite = botTex,
            Model = model,
        };
        Assert.True(bar.IsModelDisabled);

        bar.DrawSelfAndChildren(ctx);

        // Top cap sits at the top of the track (below the 16px up button)…
        var top = Assert.Single(
            renderer.DebugSpriteSegmentVerts, s => s.Texture == topTex);
        float topMinY = Enumerable.Range(0, top.Verts.Count / 8)
            .Min(i => top.Verts[i * 8 + 1]);
        Assert.Equal(16f, topMinY, 1);
        // …and the bottom cap ends at the bottom of the track (above the
        // 16px down button) — a full-track thumb.
        var bot = Assert.Single(
            renderer.DebugSpriteSegmentVerts, s => s.Texture == botTex);
        float botMaxY = Enumerable.Range(0, bot.Verts.Count / 8)
            .Max(i => bot.Verts[i * 8 + 1]);
        Assert.Equal(184f, botMaxY, 1);
    }

    [Fact]
    public void ThumbHoverAndDrag_SelectRolloverAndPressedMedia()
    {
        var model = new UiScrollable { ContentHeight = 200, ViewHeight = 150, LineHeight = 10 };
        var bar = new UiScrollbar
        {
            Width = 16f,
            Height = 200f,
            Model = model,
            ThumbSprite = 1u,
            ThumbRolloverSprite = 2u,
            ThumbPressedSprite = 3u,
            ThumbTopSprite = 10u,
            ThumbTopRolloverSprite = 20u,
            ThumbTopPressedSprite = 30u,
            ThumbBotSprite = 100u,
            ThumbBotRolloverSprite = 200u,
            ThumbBotPressedSprite = 300u,
        };
        // Track 16..184 (168px), ratio 0.75 → thumb 16..142 at position 0.
        Assert.Equal(1u, bar.ActiveThumbSpriteForTest);

        // Hover over the thumb → rollover on every slice.
        bar.OnEvent(new UiEvent(0u, bar, UiEventType.HoverEnter, Data1: 8, Data2: 50));
        Assert.Equal(2u, bar.ActiveThumbSpriteForTest);
        Assert.Equal(20u, bar.ActiveThumbTopSpriteForTest);
        Assert.Equal(200u, bar.ActiveThumbBotSpriteForTest);

        // Press and hold (drag) → pressed media.
        bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseDown, Data1: 8, Data2: 50));
        Assert.True(bar.IsDragging);
        Assert.Equal(3u, bar.ActiveThumbSpriteForTest);
        Assert.Equal(30u, bar.ActiveThumbTopSpriteForTest);
        Assert.Equal(300u, bar.ActiveThumbBotSpriteForTest);

        // Release while still over the thumb → back to the hover highlight.
        bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseUp, Data1: 8, Data2: 50));
        Assert.Equal(2u, bar.ActiveThumbSpriteForTest);

        // Move to the track BELOW the thumb → back to normal.
        bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseMove, Data1: 8, Data2: 170));
        Assert.Equal(1u, bar.ActiveThumbSpriteForTest);

        // Leave the bar entirely → normal.
        bar.OnEvent(new UiEvent(0u, bar, UiEventType.HoverEnter, Data1: 8, Data2: 50));
        bar.OnEvent(new UiEvent(0u, bar, UiEventType.HoverLeave));
        Assert.Equal(1u, bar.ActiveThumbSpriteForTest);
    }

    [Fact]
    public void DisabledVisibleBar_StillHoverHighlights_ButNeverScrolls()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var model = new UiScrollable { ContentHeight = 100, ViewHeight = 150, LineHeight = 10 };
        var bar = new UiScrollbar
        {
            Left = 100, Top = 100, Width = 16f, Height = 200f,
            Model = model,
            UpSprite = 1u, UpRolloverSprite = 2u,
            ThumbSprite = 4u, ThumbRolloverSprite = 5u,
        };
        root.AddChild(bar);
        Assert.True(bar.IsModelDisabled);
        Assert.True(bar.IsPresentationVisible);

        root.OnMouseMove(108, 105);
        Assert.Equal(2u, bar.ActiveStartSpriteForTest);

        // Hover the (full-track) thumb: highlight.
        root.OnMouseMove(108, 130);
        Assert.Equal(5u, bar.ActiveThumbSpriteForTest);

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseDown, Data1: 8, Data2: 5)));
        Assert.Equal(0, model.ScrollY);

        // A presentation-HIDDEN bar (0x79 + disabled) stays inert.
        bar.HideWhenDisabled = true;
        Assert.False(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseDown, Data1: 8, Data2: 5)));
    }

    [Fact]
    public void RootMouseMove_OverArrowAndThumb_SelectsRolloverMedia()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var model = new UiScrollable { ContentHeight = 400, ViewHeight = 150, LineHeight = 10 };
        var bar = new UiScrollbar
        {
            Left = 100, Top = 100, Width = 16f, Height = 200f,
            Model = model,
            UpSprite = 1u, UpRolloverSprite = 2u, UpPressedSprite = 3u,
            DownSprite = 7u, DownRolloverSprite = 8u,
            ThumbSprite = 4u, ThumbRolloverSprite = 5u,
        };
        root.AddChild(bar);

        // Over the up arrow (local y = 5, inside the 16px button).
        root.OnMouseMove(108, 105);
        Assert.Equal(2u, bar.ActiveStartSpriteForTest);

        // Over the thumb (track 16..184, ratio 150/400=0.375 → thumb 16..79
        // local, 116..179 screen).
        root.OnMouseMove(108, 130);
        Assert.Equal(1u, bar.ActiveStartSpriteForTest);
        Assert.Equal(5u, bar.ActiveThumbSpriteForTest);

        // Over the down arrow (local y >= 184).
        root.OnMouseMove(108, 290);
        Assert.Equal(4u, bar.ActiveThumbSpriteForTest);
        Assert.Equal(8u, bar.ActiveEndSpriteForTest);
    }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    [Fact]
    public void SingleSpriteThumb_DrawsOneUntiledInstance_NotRepeatedDownTrack()
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        var ctx = new UiRenderContext(renderer, new Vector2(800f, 600f));

        const uint thumbTex = 42u;
        var model = new UiScrollable { ContentHeight = 200, ViewHeight = 150 };
        var bar = new UiScrollbar
        {
            Width = 16f,
            Height = 200f,
            SpriteResolve = id => id == thumbTex ? (thumbTex, 16, 16) : (0u, 0, 0),
            ThumbSprite = thumbTex,
            Model = model,
        };

        bar.DrawSelfAndChildren(ctx);

        var thumbSegments = renderer.DebugSpriteSegmentVerts
            .Where(s => s.Texture == thumbTex)
            .ToArray();
        Assert.Single(thumbSegments);
        var verts = thumbSegments[0].Verts;
        // 8 floats/vertex (x,y,u,v,r,g,b,a), one quad = 6 vertices.
        Assert.Equal(6, verts.Count / 8);
        for (int i = 0; i < verts.Count; i += 8)
            Assert.True(verts[i + 3] <= 1.0001f, $"thumb sprite V={verts[i + 3]} exceeds native (tiled)");
    }

    // ── #33: a fixed-size thumb (not proportional) slides over the whole track.

    private static UiScrollbar FixedThumbBar(UiScrollable model) => new()
    {
        Width = 37f,
        Height = 307f,
        DecrementButtonExtent = 17f,
        IncrementButtonExtent = 17f,
        Proportional = false,
        ThumbExtent = 39f,
        Model = model,
    };

    [Fact]
    public void VerticalModel_FixedThumb_KeepsItsExtentAndReachesTheTrackEnd()
    {
        var model = new UiScrollable { ContentHeight = 400, ViewHeight = 300 };
        UiScrollbar bar = FixedThumbBar(model);

        var (startAtTop, extent) = bar.ModelThumbRect(model, trackStart: 17f, trackLen: 273f);
        Assert.Equal(39f, extent);
        Assert.Equal(17f, startAtTop);

        model.ScrollToEnd();
        var (startAtEnd, _) = bar.ModelThumbRect(model, trackStart: 17f, trackLen: 273f);
        Assert.Equal(17f + 273f - 39f, startAtEnd);
    }

    [Fact]
    public void VerticalModel_FixedThumb_DragMapsTheWholeTrackNotAQuarterOfIt()
    {
        var model = new UiScrollable { ContentHeight = 400, ViewHeight = 300 };
        UiScrollbar bar = FixedThumbBar(model);

        // Grab the thumb 3 px below its top edge, then drag to the middle of
        // its travel (17 + 3 + 234 / 2): the content should be half way, not
        // already pinned to the end.
        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseDown, Data1: 10, Data2: 20)));
        Assert.True(bar.IsDragging);
        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseMove, Data1: 10, Data2: 137)));
        Assert.Equal(50, model.ScrollY);

        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseMove, Data1: 10, Data2: 254)));
        Assert.Equal(model.MaxScroll, model.ScrollY);
        Assert.True(bar.OnEvent(new UiEvent(0u, bar, UiEventType.MouseUp, Data1: 10, Data2: 254)));
    }

    [Fact]
    public void VerticalModel_ProportionalThumb_HonoursTheAuthoredMinimum()
    {
        var model = new UiScrollable { ContentHeight = 10_000, ViewHeight = 100 };
        var bar = new UiScrollbar { Width = 37f, Height = 307f, MinThumbExtent = 37f, Model = model };

        var (_, extent) = bar.ModelThumbRect(model, trackStart: 17f, trackLen: 273f);
        Assert.Equal(37f, extent);
    }
}
