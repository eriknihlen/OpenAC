using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.App.Rendering;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Core.Ui;

namespace AcDream.App.Tests.UI.Layout;

public sealed class RadarControllerTests
{
    [Fact]
    public void Bind_RealFixture_ReusesImportedCoordinateTextAndDatBackground()
    {
        var layout = FixtureLoader.LoadRadar();
        _ = RadarController.Bind(
            layout,
            () => new UiRadarSnapshot(0f, Array.Empty<UiRadarBlip>(), "42.1N,33.6E"));

        var coordinates = Assert.IsType<UiText>(
            layout.FindElement(RadarController.CoordinateContainerId));
        Assert.Equal(0x06004CC0u, coordinates.BackgroundSprite);
        Assert.Empty(coordinates.Children);
        Assert.True(coordinates.ClickThrough);
        Assert.False(coordinates.AcceptsFocus);
        Assert.Equal("42.1N,33.6E", Assert.Single(coordinates.LinesProvider()).Text);
    }

    [Fact]
    public void Bind_UsesImportedChildrenForCompassCoordinatesAndLockChrome()
    {
        var layout = BuildRadarLayout();
        var state = new UiRadarSnapshot(
            0f,
            Array.Empty<UiRadarBlip>(),
            "42.1N,33.6E",
            UiLocked: false);
        bool? requestedLock = null;

        _ = RadarController.Bind(
            layout,
            () => state,
            setUiLocked: value => requestedLock = value);

        var radar = Assert.IsType<UiRadar>(layout.Root);
        Assert.Equal(new Vector2(60f, 60f), radar.Center);
        Assert.True(radar.Draggable);

        var coordinateContainer = Assert.IsType<UiDatElement>(
            layout.FindElement(RadarController.CoordinateContainerId));
        Assert.True(coordinateContainer.Visible);
        var coordinateText = Assert.IsType<UiText>(Assert.Single(coordinateContainer.Children));
        Assert.Equal("42.1N,33.6E", Assert.Single(coordinateText.LinesProvider()).Text);

        var north = layout.FindElement(RadarController.NorthTokenId)!;
        Assert.Equal(55f, north.Left);
        Assert.Equal(1f, north.Top);

        var lockButton = Assert.IsType<UiButton>(layout.FindElement(RadarController.LockButtonId));
        Assert.Equal("UnlockedUI", lockButton.ActiveState);
        var click = new UiEvent(lockButton.EventId, lockButton, UiEventType.Click);
        Assert.True(lockButton.OnEvent(in click));
        Assert.True(requestedLock);

        state = state with
        {
            PlayerHeadingDegrees = 90f,
            CoordinatesText = null,
            UiLocked = true,
        };
        radar.Refresh();

        Assert.False(radar.Draggable);
        Assert.False(coordinateContainer.Visible);
        Assert.False(coordinateText.Visible);
        Assert.Equal("LockedUI", lockButton.ActiveState);

        var drag = layout.FindElement(RadarController.DragButtonId)!;
        Assert.False(drag.Visible);
        Assert.False(drag.ClickThrough);

        float northMagnitude = Vector2.Distance(new Vector2(60f, 5.5f), radar.Center);
        var expectedNorth = RetailRadar.GetCompassTokenTopLeft(
            90f,
            RadarCompassPoint.North,
            radar.Center,
            northMagnitude,
            new Vector2(north.Width, north.Height));
        Assert.Equal(expectedNorth.X, north.Left);
        Assert.Equal(expectedNorth.Y, north.Top);
    }

    [Fact]
    public void Bind_LeavesStaticDatChildrenInRetainedTree()
    {
        var layout = BuildRadarLayout();
        _ = RadarController.Bind(layout, () => UiRadarSnapshot.Empty);

        Assert.Equal(8, layout.Root.Children.Count);
        Assert.IsType<UiDatElement>(layout.FindElement(RadarController.RadarDiscId));
        Assert.IsType<UiButton>(layout.FindElement(RadarController.LockButtonId));
        Assert.IsType<UiDatElement>(layout.FindElement(RadarController.DragButtonId));
        Assert.False(layout.FindElement(RadarController.CoordinateContainerId)!.Visible);
    }

    [Fact]
    public void Bind_RealFixture_RootPointerLockCycleRoundTripsAuthorityAndMedia()
    {
        var layout = LayoutImporter.Build(
            FixtureLoader.LoadRadarInfos(),
            static file => (file, 8, 8),
            null);
        var root = new UiRoot { Width = 800, Height = 600 };
        root.AddChild(layout.Root);
        var state = UiRadarSnapshot.Empty with { UiLocked = false };
        var requestedLocks = new List<bool>();
        using var controller = RadarController.Bind(
            layout,
            () => state,
            setUiLocked: value =>
            {
                requestedLocks.Add(value);
                state = state with { UiLocked = value };
                root.UiLocked = value;
            });

        var radar = Assert.IsType<UiRadar>(layout.Root);
        var lockButton = Assert.IsType<UiButton>(
            layout.FindElement(RadarController.LockButtonId));
        var dragButton = Assert.IsType<UiDatElement>(
            layout.FindElement(RadarController.DragButtonId));

        radar.Refresh();
        Assert.Equal("UnlockedUI", lockButton.ActiveState);
        Assert.Equal(0x060074B8u, DrawnFaceFile(lockButton));
        Assert.True(lockButton.Visible);
        Assert.False(lockButton.ClickThrough);
        Assert.True(dragButton.Visible);
        Assert.True(radar.Draggable);

        ClickThroughRoot(root, lockButton);
        radar.Refresh();

        Assert.Equal([true], requestedLocks);
        Assert.True(root.UiLocked);
        Assert.Equal("LockedUI", lockButton.ActiveState);
        Assert.Equal(0x060074B7u, DrawnFaceFile(lockButton));
        Assert.True(lockButton.Visible);
        Assert.False(lockButton.ClickThrough);
        Assert.False(dragButton.Visible);
        Assert.False(radar.Draggable);

        MoveAwayAndBack(root, lockButton);
        Assert.Equal("LockedUI", lockButton.ActiveState);
        Assert.Equal(0x060074B7u, DrawnFaceFile(lockButton));

        // UiLocked suppresses move/resize only. The same radar button remains
        // hit-testable and must be able to publish the authoritative unlock.
        ClickThroughRoot(root, lockButton);
        radar.Refresh();

        Assert.Equal([true, false], requestedLocks);
        Assert.False(root.UiLocked);
        Assert.Equal("UnlockedUI", lockButton.ActiveState);
        Assert.Equal(0x060074B8u, DrawnFaceFile(lockButton));
        Assert.True(lockButton.Visible);
        Assert.False(lockButton.ClickThrough);
        Assert.True(dragButton.Visible);
        Assert.True(radar.Draggable);

        MoveAwayAndBack(root, lockButton);
        Assert.Equal("UnlockedUI", lockButton.ActiveState);
        Assert.Equal(0x060074B8u, DrawnFaceFile(lockButton));
    }

    private static void ClickThroughRoot(UiRoot root, UiButton button)
    {
        Vector2 position = button.ScreenPosition;
        int x = (int)(position.X + button.Width * 0.5f);
        int y = (int)(position.Y + button.Height * 0.5f);
        root.OnMouseMove(x, y);
        root.OnMouseDown(UiMouseButton.Left, x, y);
        root.OnMouseUp(UiMouseButton.Left, x, y);
    }

    private static void MoveAwayAndBack(UiRoot root, UiButton button)
    {
        root.OnMouseMove(700, 500);
        Vector2 position = button.ScreenPosition;
        root.OnMouseMove(
            (int)(position.X + button.Width * 0.5f),
            (int)(position.Y + button.Height * 0.5f));
    }

    private static uint DrawnFaceFile(UiButton button)
    {
        var renderer = new TextRenderer(
            new RecordingGpuDevice(), new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(200f, 200f));
        button.DrawSelfAndChildren(new UiRenderContext(renderer, new Vector2(200f, 200f)));
        return Assert.Single(renderer.DebugSpriteSegments).Texture;
    }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public AcDream.App.Rendering.Gpu.IGpuFrame? CurrentFrame => null;
    }

    private static ImportedLayout BuildRadarLayout()
    {
        var root = new ElementInfo
        {
            Id = RadarController.RootId,
            Type = UiRadar.RetailClassId,
            Width = 120,
            Height = 140,
        };

        root.Children.Add(Sprite(RadarController.CoordinateContainerId, 0, 120, 120, 18, 0x06004CC0u));
        root.Children.Add(Sprite(RadarController.RadarDiscId, 0, 0, 120, 120, 0x06004CC1u));
        root.Children.Add(Sprite(RadarController.NorthTokenId, 55, 1, 10, 9, 0x060011FBu));
        root.Children.Add(Sprite(RadarController.EastTokenId, 110, 55, 10, 9, 0x06001938u));
        root.Children.Add(Sprite(RadarController.SouthTokenId, 55, 110, 10, 9, 0x0600193Au));
        root.Children.Add(Sprite(RadarController.WestTokenId, 0, 55, 10, 9, 0x0600193Cu));

        var lockButton = new ElementInfo
        {
            Id = RadarController.LockButtonId,
            Type = 1,
            X = 6,
            Y = 6,
            Width = 27,
            Height = 27,
        };
        lockButton.StateMedia["LockedUI"] = (0x060074B7u, 3);
        lockButton.StateMedia["UnlockedUI"] = (0x060074B8u, 3);
        root.Children.Add(lockButton);

        root.Children.Add(Sprite(RadarController.DragButtonId, 87, 6, 27, 27, 0x060074C9u, type: 2));
        return LayoutImporter.Build(root, _ => (0u, 0, 0), null);
    }

    private static ElementInfo Sprite(
        uint id,
        float x,
        float y,
        float width,
        float height,
        uint sprite,
        uint type = 3)
    {
        var info = new ElementInfo
        {
            Id = id,
            Type = type,
            X = x,
            Y = y,
            Width = width,
            Height = height,
        };
        info.StateMedia[""] = (sprite, 1);
        return info;
    }
}
