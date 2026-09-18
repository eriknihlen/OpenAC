using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using Xunit;

namespace AcDream.App.Tests.UI;

public class UiRootFixedCanvasTests
{
    [Fact]
    public void CanvasScale_IsOne_WithoutFixedCanvas()
    {
        var root = new UiRoot { Width = 1920f, Height = 1080f };
        Assert.Equal(Vector2.One, root.CanvasScale);
    }

    [Fact]
    public void CanvasScale_IsWindowOverCanvas_WhenFixed()
    {
        var root = new UiRoot
        {
            Width = 1920f,
            Height = 1080f,
            FixedCanvasSize = new Vector2(800f, 600f),
        };
        Assert.Equal(new Vector2(2.4f, 1.8f), root.CanvasScale);
    }

    [Fact]
    public void CanvasScale_IsOne_ForDegenerateCanvasOrWindow()
    {
        var zeroCanvas = new UiRoot
        {
            Width = 1920f,
            Height = 1080f,
            FixedCanvasSize = new Vector2(0f, 600f),
        };
        Assert.Equal(Vector2.One, zeroCanvas.CanvasScale);

        var zeroWindow = new UiRoot
        {
            Width = 0f,
            Height = 0f,
            FixedCanvasSize = new Vector2(800f, 600f),
        };
        Assert.Equal(Vector2.One, zeroWindow.CanvasScale);
    }

    [Fact]
    public void MouseInput_MapsWindowCoordsToCanvasSpace()
    {
        var root = new UiRoot
        {
            Width = 1920f,
            Height = 1080f,
            FixedCanvasSize = new Vector2(800f, 600f),
        };
        int clicks = 0;
        var button = new UiButton(
            new ElementInfo { Width = 120, Height = 40 },
            _ => (0u, 0, 0))
        {
            Left = 300f,
            Top = 400f,
            Width = 120f,
            Height = 40f,
            OnClick = () => clicks++,
        };
        root.AddChild(button);

        root.OnMouseDown(UiMouseButton.Left, 860, 750);
        root.OnMouseUp(UiMouseButton.Left, 860, 750);
        Assert.Equal(1, clicks);
        Assert.Equal(358, root.MouseX);
        Assert.Equal(416, root.MouseY);

        root.OnMouseMove(1919, 1079);
        Assert.Equal(799, root.MouseX);
        Assert.Equal(599, root.MouseY);

        root.OnMouseDown(UiMouseButton.Left, 300, 400);
        root.OnMouseUp(UiMouseButton.Left, 300, 400);
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void MouseInput_IsUntouched_WithoutFixedCanvas()
    {
        var root = new UiRoot { Width = 1920f, Height = 1080f };
        int clicks = 0;
        var button = new UiButton(
            new ElementInfo { Width = 120, Height = 40 },
            _ => (0u, 0, 0))
        {
            Left = 300f,
            Top = 400f,
            Width = 120f,
            Height = 40f,
            OnClick = () => clicks++,
        };
        root.AddChild(button);

        root.OnMouseDown(UiMouseButton.Left, 360, 420);
        root.OnMouseUp(UiMouseButton.Left, 360, 420);
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void GameplayScale_ResizesLogicalCanvas_AndMapsPointer()
    {
        var root = new UiRoot();
        root.SetScreenSize(new Vector2(3840f, 2160f));
        root.GameplayUiScale = 2f;

        Assert.Equal(new Vector2(1920f, 1080f), root.EffectiveCanvasSize);
        Assert.Equal(new Vector2(2f, 2f), root.CanvasScale);

        int clicks = 0;
        root.AddChild(new UiButton(
            new ElementInfo { Width = 120, Height = 40 },
            _ => (0u, 0, 0))
        {
            Left = 350f,
            Top = 280f,
            Width = 120f,
            Height = 40f,
            OnClick = () => clicks++,
        });

        root.OnMouseMove(800, 600);
        Assert.Equal(400, root.MouseX);
        Assert.Equal(300, root.MouseY);
        root.OnMouseDown(UiMouseButton.Left, 800, 600);
        root.OnMouseUp(UiMouseButton.Left, 800, 600);
        Assert.Equal(1, clicks);

        root.DeclareFixedCanvas(this, new Vector2(800f, 600f));
        Assert.Equal(new Vector2(4.8f, 3.6f), root.CanvasScale);
        root.RevokeFixedCanvas(this);
        Assert.Equal(new Vector2(2f, 2f), root.CanvasScale);
        Assert.Equal(new Vector2(1920f, 1080f), root.EffectiveCanvasSize);
    }
}
