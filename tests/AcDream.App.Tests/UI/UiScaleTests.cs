using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Core.Selection;
using Xunit;

namespace AcDream.App.Tests.UI;

/// <summary>
/// The gameplay interface scale (Options, UI Scale, 50% to 300%): the
/// interface canvas is the window divided by the scale, drawing multiplies by
/// it, and everything that crosses between window pixels and interface units
/// goes through one conversion on the root.
/// </summary>
public sealed class UiScaleTests
{
    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private static (TextRenderer Renderer, UiRenderContext Context) MakeContext(Vector2 screen)
    {
        var renderer = new TextRenderer(new RecordingGpuDevice(), new NullGpuFrameSource(), "unused");
        renderer.Begin(screen);
        return (renderer, new UiRenderContext(renderer, screen));
    }

    private static UiRoot ScaledRoot(Vector2 screen, float scale)
    {
        var root = new UiRoot();
        root.SetScreenSize(screen);
        root.GameplayUiScale = scale;
        return root;
    }

    private static IEnumerable<Vector2> Vertices(TextRenderer renderer, uint texture)
    {
        foreach (var seg in renderer.DebugSpriteSegmentVerts)
        {
            if (seg.Texture != texture) continue;
            for (int i = 0; i < seg.Verts.Count / 8; i++)
                yield return new Vector2(seg.Verts[i * 8], seg.Verts[i * 8 + 1]);
        }
    }

    private static IEnumerable<Vector2> AllVertices(TextRenderer renderer)
    {
        foreach (var seg in renderer.DebugSpriteSegmentVerts)
            for (int i = 0; i < seg.Verts.Count / 8; i++)
                yield return new Vector2(seg.Verts[i * 8], seg.Verts[i * 8 + 1]);
    }

    private static IEnumerable<Vector2> OverlayVertices(TextRenderer renderer)
    {
        foreach (var seg in renderer.DebugOverlaySpriteSegmentVerts)
            for (int i = 0; i < seg.Verts.Count / 8; i++)
                yield return new Vector2(seg.Verts[i * 8], seg.Verts[i * 8 + 1]);
    }

    private static UiSolidSpriteFill Fill(uint sprite, float left, float top, float width, float height) =>
        new()
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            SpriteId = sprite,
            SpriteResolve = id => (id, 8, 8),
        };

    // ── The popup clip (dropdowns broke below 90%) ──────────────────────

    [Theory]
    [InlineData(0.5f)]
    [InlineData(0.8f)]
    [InlineData(1.0f)]
    [InlineData(2.0f)]
    public void ADropdownOpenNearTheBottomRight_DrawsItsListAtEveryScale(float scale)
    {
        var screen = new Vector2(800f, 600f);
        UiRoot root = ScaledRoot(screen, scale);
        // A dropdown at 85% across and 75% down the canvas, opening downward.
        var window = new UiPanl { Left = root.Width * 0.85f, Top = root.Height * 0.75f, Width = 80f, Height = 18f };
        var menu = new UiMenu
        {
            Width = 80f,
            Height = 18f,
            OpenUpward = false,
            RetailButtonArt = false,
            Items = new[] { new UiMenu.MenuItem("One", (object?)1), new UiMenu.MenuItem("Two", (object?)2) },
        };
        root.AddChild(window);
        window.AddChild(menu);
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5)));
        Assert.True(menu.IsOpen);

        (TextRenderer renderer, UiRenderContext ctx) = MakeContext(screen);
        root.Draw(ctx);

        // The list hangs below the face, which ends at (top + 18) units.
        float faceBottomPixels = (window.Top + 18f) * scale;
        Assert.Contains(OverlayVertices(renderer), v => v.Y > faceBottomPixels + 1f);
    }

    private sealed class UiPanl : UiElement { }

    // ── Whole pixels and filtering ──────────────────────────────────────

    [Fact]
    public void AtAFractionalScale_NeighbouringPiecesMeetOnTheSameWholePixel()
    {
        UiRoot root = ScaledRoot(new Vector2(1920f, 1080f), 1.1f);
        root.AddChild(Fill(1u, 0f, 0f, 37f, 20f));
        root.AddChild(Fill(2u, 37f, 0f, 10f, 20f));

        (TextRenderer renderer, UiRenderContext ctx) = MakeContext(new Vector2(1920f, 1080f));
        root.Draw(ctx);

        Assert.All(AllVertices(renderer), v =>
        {
            Assert.Equal(MathF.Round(v.X), v.X);
            Assert.Equal(MathF.Round(v.Y), v.Y);
        });
        float leftPieceRight = 0f, rightPieceLeft = float.MaxValue;
        foreach (Vector2 v in Vertices(renderer, 1u)) leftPieceRight = MathF.Max(leftPieceRight, v.X);
        foreach (Vector2 v in Vertices(renderer, 2u)) rightPieceLeft = MathF.Min(rightPieceLeft, v.X);
        Assert.Equal(41f, leftPieceRight);   // 37 x 1.1 = 40.7
        Assert.Equal(leftPieceRight, rightPieceLeft);
    }

    [Fact]
    public void BelowOneHundredPercent_AOneUnitBorderKeepsOnePixel()
    {
        Assert.Equal((19f, 1f), TextRenderer.SnapSpan(37f, 1f, 0.5f));
        Assert.Equal((41f, 11f), TextRenderer.SnapSpan(37f, 10f, 1.1f));
        Assert.Equal((3f, 0f), TextRenderer.SnapSpan(5f, 0f, 0.5f));   // nothing stays nothing
    }

    [Fact]
    public void AtOneHundredPercent_NothingIsSnappedOrSwapped()
    {
        UiRoot root = ScaledRoot(new Vector2(1920f, 1080f), 1.0f);
        root.AddChild(Fill(1u, 10.25f, 3.5f, 7.25f, 4f));

        (TextRenderer renderer, UiRenderContext ctx) = MakeContext(new Vector2(1920f, 1080f));
        renderer.LinearTwinResolver = _ => throw new InvalidOperationException(
            "the smooth twin must not be asked for at 100%");
        root.Draw(ctx);

        Assert.Contains(Vertices(renderer, 1u), v => v == new Vector2(10.25f, 3.5f));
        Assert.Contains(Vertices(renderer, 1u), v => v == new Vector2(17.5f, 7.5f));
    }

    [Theory]
    [InlineData(2.0f, false)]
    [InlineData(3.0f, false)]
    [InlineData(1.5f, true)]
    [InlineData(1.1f, true)]
    [InlineData(0.5f, true)]
    public void SpritesAreSmoothedOnlyAtFractionalScales(float scale, bool smoothed)
    {
        UiRoot root = ScaledRoot(new Vector2(1920f, 1080f), scale);
        root.AddChild(Fill(5u, 0f, 0f, 20f, 20f));

        (TextRenderer renderer, UiRenderContext ctx) = MakeContext(new Vector2(1920f, 1080f));
        renderer.LinearTwinResolver = handle => handle == 5u ? 999u : handle;
        root.Draw(ctx);

        Assert.Contains(renderer.DebugSpriteSegments, s => s.Texture == (smoothed ? 999u : 5u));
    }

    [Fact]
    public void TheFixedLoginCanvas_KeepsItsSmoothUnsnappedDrawing()
    {
        var root = new UiRoot();
        root.SetScreenSize(new Vector2(1600f, 1200f));
        root.DeclareFixedCanvas(this, new Vector2(800f, 600f));   // exactly 2x
        root.AddChild(Fill(5u, 10.25f, 0f, 20f, 20f));

        (TextRenderer renderer, UiRenderContext ctx) = MakeContext(new Vector2(1600f, 1200f));
        renderer.LinearTwinResolver = handle => handle == 5u ? 999u : handle;
        root.Draw(ctx);

        Assert.Contains(renderer.DebugSpriteSegments, s => s.Texture == 999u);
        Assert.Contains(Vertices(renderer, 999u), v => v.X == 20.5f);
    }

    // ── World positions placed in the interface ─────────────────────────

    private static (Matrix4x4 View, Matrix4x4 Projection, Vector2 Viewport) Camera(Vector2 viewport) =>
        (Matrix4x4.CreateLookAt(new Vector3(0f, -10f, 2f), new Vector3(0f, 0f, 1f), Vector3.UnitZ),
         Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, viewport.X / viewport.Y, 0.1f, 500f),
         viewport);

    [Theory]
    [InlineData(1.5f)]
    [InlineData(0.5f)]
    [InlineData(2.0f)]
    public void TheTargetIndicator_FramesTheCreaturesPixel_AtEveryScale(float scale)
    {
        var screen = new Vector2(1920f, 1080f);
        UiRoot root = ScaledRoot(screen, scale);
        var camera = Camera(screen);
        // A creature down and to the right of the screen centre.
        var creature = new Vector3(3f, 2f, 0.5f);

        Assert.True(ScreenProjection.TryProjectSphereToScreenRect(
            creature, 0.8f, camera.View, camera.Projection, camera.Viewport,
            out Vector2 pixelMin, out Vector2 pixelMax, out _, minSidePixels: 0f));

        var ui = root.ToUiSpace(camera);
        VividTargetProjection projection = VividTargetIndicatorController.ComputeProjection(
            creature, 0.8f, ui.View, ui.Projection, ui.Viewport);

        Assert.Equal(VividTargetProjectionStatus.OnScreen, projection.Status);
        Vector2 drawnMin = root.UiToPixel(projection.RectMin);
        Vector2 drawnMax = root.UiToPixel(projection.RectMax);
        Assert.Equal(pixelMin.X, drawnMin.X, 2);
        Assert.Equal(pixelMin.Y, drawnMin.Y, 2);
        Assert.Equal(pixelMax.X, drawnMax.X, 2);
        Assert.Equal(pixelMax.Y, drawnMax.Y, 2);
    }

    [Fact]
    public void PixelAndInterfaceUnits_ConvertBothWays()
    {
        UiRoot root = ScaledRoot(new Vector2(1920f, 1080f), 1.5f);

        Assert.Equal(new Vector2(1280f, 720f), root.PixelToUi(new Vector2(1920f, 1080f)));
        Assert.Equal(new Vector2(900f, 600f), root.UiToPixel(new Vector2(600f, 400f)));
        Assert.Equal(new Vector2(1280f, 720f), root.ToUiSpace(Camera(new Vector2(1920f, 1080f))).Viewport);
        Assert.Equal((1280, 720), root.GameplayCanvasSize((1920, 1080)));

        UiRoot plain = ScaledRoot(new Vector2(1920f, 1080f), 1.0f);
        Assert.Equal(new Vector2(17.25f, 3f), plain.PixelToUi(new Vector2(17.25f, 3f)));
        Assert.Equal((1921, 1079), plain.GameplayCanvasSize((1921, 1079)));
    }

    // ── The pointer: interface in units, world in pixels ────────────────

    [Fact]
    public void AClickOnTheWorld_IsReportedAtItsWindowPixel()
    {
        UiRoot root = ScaledRoot(new Vector2(1920f, 1080f), 1.5f);
        var presses = new List<(int X, int Y)>();
        var moves = new List<(int X, int Y)>();
        root.WorldMouseFallThrough += (_, x, y, _) => presses.Add((x, y));
        root.WorldMouseMoveFallThrough += (x, y) => moves.Add((x, y));

        root.OnMouseMove(901, 601);
        root.OnMouseDown(UiMouseButton.Right, 900, 600);

        Assert.Equal((600, 400), (root.MouseX, root.MouseY));
        Assert.Equal([(901, 601)], moves);
        Assert.Equal([(900, 600)], presses);
    }

    [Fact]
    public void AWorldDrag_LiftsAndDropsAtWindowPixels()
    {
        UiRoot root = ScaledRoot(new Vector2(1920f, 1080f), 2.0f);
        var asked = new List<(int X, int Y)>();
        (int X, int Y)? dropped = null;
        root.WorldDragRequested = (x, y) =>
        {
            asked.Add((x, y));
            return new WorldDragStart(
                new ItemDragPayload(0x50000A01u, ItemDragSource.Inventory, 0, SourceCell: null),
                (1u, 32, 32));
        };
        root.DragReleasedOutsideUi += (_, x, y) => dropped = (x, y);

        root.OnMouseDown(UiMouseButton.Left, 1000, 800);
        root.OnMouseMove(1040, 820);
        root.OnMouseUp(UiMouseButton.Left, 1201, 703);

        Assert.Equal([(1000, 800)], asked);
        Assert.Equal((1201, 703), dropped);
    }

    [Fact]
    public void AClickOnAnElement_HitsItAtEveryScale()
    {
        foreach (float scale in new[] { 0.5f, 1.1f, 1.5f, 3.0f })
        {
            UiRoot root = ScaledRoot(new Vector2(1920f, 1080f), scale);
            int clicks = 0;
            root.AddChild(new UiButton(new ElementInfo { Width = 40, Height = 20 }, _ => (0u, 0, 0))
            {
                Left = 100f,
                Top = 50f,
                Width = 40f,
                Height = 20f,
                OnClick = () => clicks++,
            });
            Vector2 pixel = root.UiToPixel(new Vector2(120f, 60f));
            root.OnMouseDown(UiMouseButton.Left, (int)pixel.X, (int)pixel.Y);
            root.OnMouseUp(UiMouseButton.Left, (int)pixel.X, (int)pixel.Y);
            Assert.True(clicks == 1, $"no click at {scale}");
        }
    }

    // ── Off-screen figures ──────────────────────────────────────────────

    [Theory]
    [InlineData(1.0f, 101, 80)]
    [InlineData(2.0f, 203, 160)]
    [InlineData(1.1f, 112, 88)]
    [InlineData(0.5f, 51, 40)]
    public void APreviewFigure_IsRenderedAtItsOnScreenPixelSize(float scale, int width, int height)
    {
        UiRoot root = ScaledRoot(new Vector2(1920f, 1080f), scale);
        var viewport = new UiViewport { Width = 101.5f, Height = 80f };
        root.AddChild(viewport);

        Assert.Equal((width, height), viewport.RenderTargetSize);
    }

    [Fact]
    public void APreviewFigure_OnAFixedLoginCanvas_KeepsItsInterfaceSize()
    {
        var root = new UiRoot();
        root.SetScreenSize(new Vector2(1600f, 1200f));
        root.GameplayUiScale = 2f;
        root.DeclareFixedCanvas(this, new Vector2(800f, 600f));
        var viewport = new UiViewport { Width = 101.5f, Height = 80f };
        root.AddChild(viewport);

        Assert.Equal((101, 80), viewport.RenderTargetSize);
    }

    // ── Window placements across a scale change ─────────────────────────

    [Fact]
    public void AWindowAtTheRightEdge_StaysOnScreen_WhenTheScaleGrows_AndComesBack()
    {
        var screen = new Vector2(1600f, 1200f);
        UiRoot root = ScaledRoot(screen, 1f);
        RetailWindowHandle handle = Window(root, WindowNames.Options);
        RetailWindowHandle middle = Window(root, WindowNames.Chat);
        using var persistence = new RetailWindowLayoutPersistence(
            root.WindowManager, null, () => "Alice",
            () => root.GameplayCanvasSize(((int)screen.X, (int)screen.Y)));
        handle.MoveTo(1400f, 1100f);
        middle.MoveTo(700f, 550f);

        root.GameplayUiScale = 2f;
        persistence.ReflowToScreen();

        Assert.Equal(new Vector2(800f, 600f), root.EffectiveCanvasSize);
        Assert.InRange(handle.Left + handle.Width, 0f, 800f);
        Assert.InRange(handle.Top + handle.Height, 0f, 600f);
        Assert.Equal(600f, handle.Left);   // still against the right edge
        Assert.Equal(500f, handle.Top);    // still against the bottom edge
        // A centred window stays centred on the smaller canvas.
        Assert.Equal((300f, 250f), (middle.Left, middle.Top));

        root.GameplayUiScale = 1f;
        persistence.ReflowToScreen();
        Assert.Equal((1400f, 1100f), (handle.Left, handle.Top));
        Assert.Equal((700f, 550f), (middle.Left, middle.Top));
    }

    private static RetailWindowHandle Window(UiRoot root, string name)
    {
        var frame = new UiPanel
        {
            Width = 200f,
            Height = 100f,
            MinWidth = 100f,
            MinHeight = 80f,
            MaxWidth = 600f,
            MaxHeight = 400f,
            Draggable = true,
        };
        root.AddChild(frame);
        return root.RegisterWindow(name, frame);
    }
}
