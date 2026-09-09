using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Chat;
using AcDream.UI.Abstractions;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.App.Tests.UI.Layout;

public class ChatLayoutConformanceTests
{
    private static (uint, int, int) NoTex(uint _) => (0, 0, 0);

    private static ElementInfo? Find(ElementInfo n, uint id)
    {
        if (n.Id == id) return n;
        foreach (var c in n.Children)
        {
            var f = Find(c, id);
            if (f is not null) return f;
        }
        return null;
    }

    [Fact]
    public void ChatFixture_ResolvesKnownElements()
    {
        var root = FixtureLoader.LoadChatInfos();
        Assert.NotNull(Find(root, 0x10000011u)); // transcript
        Assert.NotNull(Find(root, 0x10000016u)); // input
        Assert.NotNull(Find(root, 0x10000012u)); // scrollbar track
        Assert.NotNull(Find(root, 0x10000014u));
        Assert.NotNull(Find(root, 0x10000019u)); // send button
        Assert.NotNull(Find(root, 0x1000046Fu)); // max/min button
    }

    [Fact]
    public void ChatFixture_ResolvedTypes_MatchRetailRegistry()
    {
        var root = FixtureLoader.LoadChatInfos();
        Assert.Equal(6u,  Find(root, 0x10000014u)!.Type);  // Menu
        Assert.Equal(11u, Find(root, 0x10000012u)!.Type);  // Scrollbar
        Assert.Equal(1u,  Find(root, 0x10000019u)!.Type);  // Button (Send)
        Assert.Equal(1u,  Find(root, 0x1000046Fu)!.Type);  // Button (Max/Min)
        Assert.Equal(12u, Find(root, 0x10000011u)!.Type);  // Text/style-prototype (transcript)
        Assert.Equal(12u, Find(root, 0x10000016u)!.Type);  // Text/style-prototype (input)
    }

    [Theory]
    [InlineData(0x10000522u)]
    [InlineData(0x10000523u)]
    [InlineData(0x10000524u)]
    [InlineData(0x10000525u)]
    public void ChatFixture_ChatWindowIndicatorButtons_ImportFromTheAuthoredTree(uint indicatorId)
    {
        var root = FixtureLoader.LoadChatInfos();
        Assert.NotNull(Find(root, indicatorId));
    }

    [Theory]
    [InlineData(0x10000522u)]
    [InlineData(0x10000523u)]
    [InlineData(0x10000524u)]
    [InlineData(0x10000525u)]
    public void MountedChatWindow_IndicatorButtons_BindAloneLeavesClickUnwiredButSelfFlipSuppressed(
        uint indicatorId)
    {
        var infos = FixtureLoader.LoadChatInfos();
        var layout = LayoutImporter.Build(infos, NoTex, null);
        var controller = ChatWindowController.Bind(
            infos, layout, new ChatVM(new ChatLog()), () => NullCommandBus.Instance, new ChatWindowState(), null, null, NoTex);
        Assert.NotNull(controller);

        UiElement? indicator = layout.FindElement(indicatorId);
        Assert.NotNull(indicator);
        Assert.True(indicator!.Visible);
        var button = Assert.IsType<UiButton>(indicator);
        Assert.Null(button.OnClick);
        Assert.Null(button.OnClickAt);

        Assert.True(button.SuppressSelfToggle);
        bool before = button.Selected;
        button.OnEvent(new UiEvent(0, button, UiEventType.MouseDown, Data1: 2, Data2: 2));
        button.OnEvent(new UiEvent(0, button, UiEventType.MouseUp, Data1: 2, Data2: 2));
        Assert.Equal(before, button.Selected);
    }

    [Theory]
    [InlineData(0x10000522u, 1)]
    [InlineData(0x10000523u, 2)]
    [InlineData(0x10000524u, 3)]
    [InlineData(0x10000525u, 4)]
    public void MountedChatWindow_IndicatorButtons_ClickTogglesWindow_ThroughBindIndicatorClicks(
        uint indicatorId, int expectedWindowId)
    {
        var infos = FixtureLoader.LoadChatInfos();
        var layout = LayoutImporter.Build(infos, NoTex, null);
        var controller = ChatWindowController.Bind(
            infos, layout, new ChatVM(new ChatLog()), () => NullCommandBus.Instance, new ChatWindowState(), null, null, NoTex);
        Assert.NotNull(controller);

        var toggled = new List<int>();
        controller!.BindIndicatorClicks(windowId =>
        {
            toggled.Add(windowId);
            return true;
        });

        UiElement? indicator = layout.FindElement(indicatorId);
        var button = Assert.IsType<UiButton>(indicator);
        Assert.NotNull(button.OnClick);

        button.OnEvent(new UiEvent(0, button, UiEventType.MouseDown, Data1: 2, Data2: 2));
        button.OnEvent(new UiEvent(0, button, UiEventType.MouseUp, Data1: 2, Data2: 2));
        button.OnEvent(new UiEvent(0, button, UiEventType.Click, Data1: 2, Data2: 2));

        Assert.Equal(new[] { expectedWindowId }, toggled);
    }

    [Fact]
    public void MountedChatWindow_IndicatorButtons_ClickRoundTrip_KeepsMirrorConsistent()
    {
        var infos = FixtureLoader.LoadChatInfos();
        var layout = LayoutImporter.Build(infos, NoTex, null);
        var controller = ChatWindowController.Bind(
            infos, layout, new ChatVM(new ChatLog()), () => NullCommandBus.Instance, new ChatWindowState(), null, null, NoTex);
        Assert.NotNull(controller);

        bool windowOpen = false;
        controller!.BindIndicatorClicks(windowId =>
        {
            windowOpen = !windowOpen;
            controller.SetIndicatorOpen(windowId, windowOpen);
            return true;
        });

        var button = Assert.IsType<UiButton>(layout.FindElement(0x10000522u));
        Assert.False(button.Selected);

        button.OnEvent(new UiEvent(0, button, UiEventType.MouseDown, Data1: 2, Data2: 2));
        button.OnEvent(new UiEvent(0, button, UiEventType.MouseUp, Data1: 2, Data2: 2));
        button.OnEvent(new UiEvent(0, button, UiEventType.Click, Data1: 2, Data2: 2));
        Assert.True(button.Selected);

        button.OnEvent(new UiEvent(0, button, UiEventType.MouseDown, Data1: 2, Data2: 2));
        button.OnEvent(new UiEvent(0, button, UiEventType.MouseUp, Data1: 2, Data2: 2));
        button.OnEvent(new UiEvent(0, button, UiEventType.Click, Data1: 2, Data2: 2));
        Assert.False(button.Selected);
    }

    [Theory]
    [InlineData(0x10000693u)]
    [InlineData(0x10000694u)]
    [InlineData(0x10000695u)]
    [InlineData(0x10000696u)]
    [InlineData(0x10000697u)]
    [InlineData(0x10000698u)]
    [InlineData(0x10000699u)]
    [InlineData(0x1000069Au)]
    public void BoundChatWindow_LockedTwinBorderArt_SeedsUnlockedUntilRegistration(uint lockedTwinId)
    {
        var infos = FixtureLoader.LoadChatInfos();
        var layout = LayoutImporter.Build(infos, NoTex, null);
        var controller = ChatWindowController.Bind(
            infos, layout, new ChatVM(new ChatLog()), () => NullCommandBus.Instance, new ChatWindowState(), null, null, NoTex);
        Assert.NotNull(controller);

        UiElement? twin = layout.FindElement(lockedTwinId);
        Assert.NotNull(twin);
        Assert.False(twin!.Visible);
    }

    [Fact]
    public void ChatFixture_BuildsSelectableTranscriptAndEditableInputInPlace()
    {
        var layout = FixtureLoader.LoadChat();

        var transcript = Assert.IsType<UiText>(layout.FindElement(0x10000011u));
        var input = Assert.IsType<UiField>(layout.FindElement(0x10000016u));

        Assert.True(transcript.Selectable);
        Assert.True(input.Selectable);
        Assert.True(input.OneLine);
        Assert.Equal(0x10000016u, input.DatElementId);
    }

    [Fact]
    public void ChatFixture_TranscriptElementInfo_CarriesNoOutline()
    {
        var root = FixtureLoader.LoadChatInfos();
        var transcript = Find(root, 0x10000011u)!;

        Assert.False(transcript.Outline);
        Assert.Null(transcript.OutlineColor);
    }

    /// <summary>Same fact at the built-widget level — <c>DatWidgetFactory.BuildText</c>
    /// must not turn a false <c>ElementInfo.Outline</c> into a true <c>UiText.Outline</c>.</summary>
    [Fact]
    public void ChatFixture_TranscriptWidget_OutlineIsOff()
    {
        var layout = FixtureLoader.LoadChat();
        var transcript = Assert.IsType<UiText>(layout.FindElement(0x10000011u));

        Assert.False(transcript.Outline);
    }

    [Fact]
    public void ChatFixture_TranscriptWidget_DefaultColorIsAuthoredOffWhite()
    {
        var layout = FixtureLoader.LoadChat();
        var transcript = Assert.IsType<UiText>(layout.FindElement(0x10000011u));

        var expected = new System.Numerics.Vector4(204f / 255f, 204f / 255f, 204f / 255f, 1f);
        Assert.Equal(expected, transcript.DefaultColor);
    }

    [Fact]
    public void ChatFixture_ScrollbarImportsInheritedMediaRoles()
    {
        var layout = FixtureLoader.LoadChat();
        var scrollbar = Assert.IsType<UiScrollbar>(layout.FindElement(0x10000012u));

        Assert.Equal(0x06004C5Fu, scrollbar.TrackSprite);
        Assert.Equal(0x06004C60u, scrollbar.ThumbTopSprite);
        Assert.Equal(0x06004C63u, scrollbar.ThumbSprite);
        Assert.Equal(0x06004C66u, scrollbar.ThumbBotSprite);
        Assert.Equal(0x06004C6Cu, scrollbar.UpSprite);
        Assert.Equal(0x06004C69u, scrollbar.DownSprite);
        Assert.Equal(0x06004C64u, scrollbar.ThumbRolloverSprite);
        Assert.Equal(0x06004C65u, scrollbar.ThumbPressedSprite);
    }

    [Fact]
    public void ChatFixture_WindowRootCarriesRetailHeightConstraints()
    {
        var root = FixtureLoader.LoadChatInfos();
        var window = Find(root, 0x10000600u)!;

        Assert.True(window.TryGetEffectiveInteger(0x3Eu, out int minHeight));
        Assert.True(window.TryGetEffectiveInteger(0x3Cu, out int maxHeight));
        Assert.Equal(100, minHeight);
        Assert.Equal(2000, maxHeight);
    }

    [Fact]
    public void ChatFixture_WindowRootCarriesRetailWidthConstraints()
    {
        var root = FixtureLoader.LoadChatInfos();
        var window = Find(root, 0x10000600u)!;

        Assert.True(window.TryGetEffectiveInteger(0x3Fu, out int minWidth));
        Assert.True(window.TryGetEffectiveInteger(0x3Du, out int maxWidth));
        Assert.Equal(300, minWidth);
        Assert.Equal(2000, maxWidth);
    }

    [Fact]
    public void ChatFixture_RootIsAuthored410x100_NoCropNeeded()
    {
        var root = FixtureLoader.LoadChatInfos();
        Assert.Equal(410f, root.Width);
        Assert.Equal(100f, root.Height);
    }

    [Fact]
    public void ChatFixture_DockedRoot_ReflowsContentAcrossMountedWidth()
    {
        var infos = FixtureLoader.LoadChatInfos();
        var layout = LayoutImporter.Build(infos, NoTex, null);
        var controller = ChatWindowController.Bind(
            infos,
            layout,
            new ChatVM(new ChatLog()),
            () => NullCommandBus.Instance,
            new ChatWindowState(),
            null,
            null,
            NoTex);
        Assert.NotNull(controller);

        var root = controller!.Root;
        Assert.Equal(410f, root.Width);
        root.Width = 615;

        // Both panels are authored with a 5px margin to BOTH the left and right
        // window edges (X=5, right-edge width = 410-(5+400) = 5) — a stretch
        // grows the panel by the delta MINUS both margins: 615 - 5 - 5 = 605.
        var transcriptPanel = layout.FindElement(0x10000010u)!;
        var inputBar = layout.FindElement(0x10000013u)!;
        transcriptPanel.ApplyAnchor(root.Width, root.Height);
        inputBar.ApplyAnchor(root.Width, root.Height);

        Assert.Equal(605f, transcriptPanel.Width);
        Assert.Equal(605f, inputBar.Width);
    }

    [Fact]
    public void ChatFixture_ChannelButton_KeepsAuthoredWidthThroughLayoutPass()
    {
        var infos = FixtureLoader.LoadChatInfos();
        var layout = LayoutImporter.Build(infos, NoTex, null);
        var controller = ChatWindowController.Bind(
            infos,
            layout,
            new ChatVM(new ChatLog()),
            () => NullCommandBus.Instance,
            new ChatWindowState(),
            null,
            null,
            NoTex);
        Assert.NotNull(controller);

        float inputLeft = controller!.Input.Left;
        controller.Menu.OnSelect!.Invoke(ChatChannelKind.General);
        controller.Menu.ApplyAnchor(controller.Menu.Parent!.Width, controller.Menu.Parent.Height);

        Assert.Equal(46f, controller.Menu.Width);
        Assert.Equal(inputLeft, controller.Input.Left);
    }

    [Fact]
    public void ChatMaximize_ResizesOuterFrameByHalfParent_AndRestores()
    {
        var infos = FixtureLoader.LoadChatInfos();
        var layout = LayoutImporter.Build(infos, NoTex, null);
        var controller = ChatWindowController.Bind(
            infos,
            layout,
            new ChatVM(new ChatLog()),
            () => NullCommandBus.Instance,
            new ChatWindowState(),
            null,
            null,
            NoTex)!;
        var root = new UiRoot { Width = 800, Height = 600 };
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            root,
            controller.Root,
            NoTex,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Chat,
                Chrome = RetailWindowChrome.Imported,
                Left = 12f,
                Top = 390f,
                DatConstraintSource = controller.DatWindowInfo,
            });
        controller.AttachWindow(handle);
        var maxMin = Assert.IsType<UiButton>(layout.FindElement(0x1000046Fu));

        maxMin.OnClick!();

        Assert.True(controller.IsMaximized);
        Assert.Equal(90f, handle.Top);
        Assert.Equal(400f, handle.Height);
        Assert.Equal(490f, handle.Top + handle.Height); // lower edge stays fixed while growing upward
        Assert.Equal(RetailUiStateIds.Maximized, maxMin.ActiveRetailStateId);

        maxMin.OnClick!();

        Assert.False(controller.IsMaximized);
        Assert.Equal(390f, handle.Top);
        Assert.Equal(100f, handle.Height);
        Assert.Equal(RetailUiStateIds.Minimized, maxMin.ActiveRetailStateId);
    }

    [Theory]
    [InlineData(0x1000069Bu, UiResizeGrip.Border.UpperLeft)]
    [InlineData(0x1000069Du, UiResizeGrip.Border.UpperRight)]
    [InlineData(0x1000069Fu, UiResizeGrip.Border.LowerLeft)]
    [InlineData(0x100006A1u, UiResizeGrip.Border.LowerRight)]
    public void MountedChatWindow_CornerGrip_ImportsWithCorrectBorderLocation(
        uint elementId, UiResizeGrip.Border expected)
    {
        var layout = FixtureLoader.LoadChat();
        var grip = Assert.IsType<UiResizeGrip>(layout.FindElement(elementId));
        Assert.Equal(expected, grip.BorderLocation);
    }

    [Fact]
    public void MountedChatWindow_TopStrip_IsAMoveHandleNotAGrip()
    {
        var layout = FixtureLoader.LoadChat();
        var topStrip = layout.FindElement(0x1000069Cu);
        Assert.NotNull(topStrip);
        Assert.IsNotType<UiResizeGrip>(topStrip);
        Assert.True(topStrip!.WindowMoveHandle);
    }

    [Theory]
    [InlineData(0x1000069Bu)]
    [InlineData(0x1000069Du)]
    [InlineData(0x1000069Eu)]   // left edge
    [InlineData(0x1000069Fu)]
    [InlineData(0x100006A0u)]   // bottom edge
    [InlineData(0x100006A1u)]
    [InlineData(0x100006A2u)]   // right edge
    public void MountedChatWindow_LiveGrip_ResolvesNonZeroSprite(uint elementId)
    {
        var layout = FixtureLoader.LoadChat();
        var grip = Assert.IsType<UiResizeGrip>(layout.FindElement(elementId));
        Assert.NotEqual(0u, grip.SpriteFile);
    }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    [Theory]
    [InlineData(0x1000069Bu)]
    [InlineData(0x1000069Du)]
    [InlineData(0x1000069Eu)]   // left edge
    [InlineData(0x1000069Fu)]
    [InlineData(0x100006A0u)]   // bottom edge
    [InlineData(0x100006A1u)]
    [InlineData(0x100006A2u)]   // right edge
    public void MountedChatWindow_LiveGrip_ActuallyEmitsASpriteDraw_NotJustResolvesSpriteFile(uint elementId)
    {
        var infos = FixtureLoader.LoadChatInfos();
        var layout = LayoutImporter.Build(infos, id => (id, 8, 8), null);
        var grip = Assert.IsType<UiResizeGrip>(layout.FindElement(elementId));
        Assert.NotEqual(0u, grip.SpriteFile);
        Assert.True(grip.Visible);

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        var ctx = new UiRenderContext(renderer, new Vector2(800f, 600f));

        grip.DrawSelfAndChildren(ctx);

        var seg = Assert.Single(
            renderer.DebugSpriteSegments,
            s => s.Texture == grip.SpriteFile);
        Assert.True(seg.VertexCount > 0);
    }

    [Fact]
    public void MountedChatWindow_BottomRightGrip_GrowsBothAxes_NotOnlyShrinks()
    {
        var infos = FixtureLoader.LoadChatInfos();
        var layout = LayoutImporter.Build(infos, NoTex, null);
        var controller = ChatWindowController.Bind(
            infos,
            layout,
            new ChatVM(new ChatLog()),
            () => NullCommandBus.Instance,
            new ChatWindowState(),
            null,
            null,
            NoTex)!;
        var root = new UiRoot { Width = 1600, Height = 1200 };
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            root,
            controller.Root,
            NoTex,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Chat,
                Chrome = RetailWindowChrome.Imported,
                Left = 10f,
                Top = 440f,
                DatConstraintSource = controller.DatWindowInfo,
            });
        controller.AttachWindow(handle);

        var brGrip = Assert.IsType<UiResizeGrip>(layout.FindElement(0x100006A1u));
        Assert.Equal(UiResizeGrip.Border.LowerRight, brGrip.BorderLocation);

        var gs = brGrip.ScreenPosition;
        int pressX = (int)(gs.X + 2), pressY = (int)(gs.Y + 2);

        root.OnMouseDown(UiMouseButton.Left, pressX, pressY);
        root.OnMouseMove(pressX - 20, pressY - 20);
        root.OnMouseUp(UiMouseButton.Left, pressX - 20, pressY - 20);
        Assert.Equal(390f, handle.Width);
        Assert.Equal(100f, handle.Height);

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(root.Width, root.Height));
        var drawCtx = new UiRenderContext(renderer, new Vector2(root.Width, root.Height));
        root.DrawSelfAndChildren(drawCtx);

        // Now grow from the shrunken state — this is the reported-broken direction.
        var brGripAfterShrink = Assert.IsType<UiResizeGrip>(layout.FindElement(0x100006A1u));
        var gs2 = brGripAfterShrink.ScreenPosition;
        int pressX2 = (int)(gs2.X + 2), pressY2 = (int)(gs2.Y + 2);
        root.OnMouseDown(UiMouseButton.Left, pressX2, pressY2);
        root.OnMouseMove(pressX2 + 70, pressY2 + 70);
        root.OnMouseUp(UiMouseButton.Left, pressX2 + 70, pressY2 + 70);

        Assert.Equal(460f, handle.Width);
        Assert.Equal(170f, handle.Height);
    }

    [Fact]
    public void MountedChatScrollbar_ButtonsAndThumbDragDriveTranscriptModel()
    {
        var infos = FixtureLoader.LoadChatInfos();
        var layout = LayoutImporter.Build(infos, NoTex, null);
        var controller = ChatWindowController.Bind(
            infos,
            layout,
            new ChatVM(new ChatLog()),
            () => NullCommandBus.Instance,
            new ChatWindowState(),
            null,
            null,
            NoTex)!;
        var root = new UiRoot { Width = 800, Height = 600 };
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            root,
            controller.Root,
            NoTex,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Chat,
                Chrome = RetailWindowChrome.Imported,
                Left = 10f,
                Top = 440f,
                DatConstraintSource = controller.DatWindowInfo,
            });
        controller.AttachWindow(handle);

        UiScrollbar bar = controller.Scrollbar;
        UiScrollable scroll = controller.Transcript.Scroll;
        scroll.LineHeight = 16;
        scroll.SetExtents(contentHeight: 400, viewHeight: 50, preserveEnd: true);
        Assert.Equal(350, scroll.ScrollY);

        var screen = bar.ScreenPosition;
        int centerX = (int)(screen.X + bar.Width / 2f);

        // Top/decrement button.
        int upY = (int)(screen.Y + 8f);
        root.OnMouseDown(UiMouseButton.Left, centerX, upY);
        root.OnMouseUp(UiMouseButton.Left, centerX, upY);
        Assert.Equal(334, scroll.ScrollY);

        scroll.SetExtents(contentHeight: 400, viewHeight: 50, preserveEnd: true);
        Assert.Equal(334, scroll.ScrollY);

        // Drag the thumb toward the top of the track.
        float trackTop = 16f;
        float trackLength = bar.Height - 32f;
        var (thumbY, thumbHeight) = UiScrollbar.ThumbRect(scroll, trackTop, trackLength);
        int dragStartY = (int)(screen.Y + thumbY + thumbHeight / 2f);
        int dragEndY = (int)(screen.Y + trackTop + thumbHeight / 2f);
        root.OnMouseDown(UiMouseButton.Left, centerX, dragStartY);
        root.OnMouseMove(centerX, dragEndY);
        root.OnMouseUp(UiMouseButton.Left, centerX, dragEndY);

        Assert.True(scroll.ScrollY < 334);
        int draggedPosition = scroll.ScrollY;
        scroll.SetExtents(contentHeight: 400, viewHeight: 50, preserveEnd: true);
        Assert.Equal(draggedPosition, scroll.ScrollY);
    }

    [Theory]
    [InlineData(300f, 100f)]
    [InlineData(600f, 160f)]
    [InlineData(220f, 80f)]
    public void ResizingTheWindow_KeepsTheInputRowInsideIt(float width, float height)
    {
        var infos = FixtureLoader.LoadChatInfos();
        ImportedLayout layout = LayoutImporter.Build(infos, NoTex, null);
        var controller = ChatWindowController.Bind(
            infos, layout, new ChatVM(new ChatLog()), () => NullCommandBus.Instance,
            new ChatWindowState(), null, null, NoTex);
        Assert.NotNull(controller);
        UiElement window = layout.FindElement(0x10000600u)!;
        var root = new UiRoot { Width = 800f, Height = 600f };
        root.AddChild(window);
        ApplyLayoutPassLocal(window);

        window.Width = width;
        window.Height = height;
        window.ResetAnchorCapture();
        ApplyLayoutPassLocal(window);
        ApplyLayoutPassLocal(window); // second frame — policies settle

        UiElement inputBar = layout.FindElement(0x10000013u)!;
        UiElement input = layout.FindElement(0x10000016u)!;
        UiElement send = layout.FindElement(0x10000019u)!;
        UiElement menuButton = layout.FindElement(0x10000014u)!;

        Assert.True(inputBar.Left >= 0f && inputBar.Left + inputBar.Width <= width + 0.5f,
            $"input bar [{inputBar.Left},{inputBar.Left + inputBar.Width}] escapes window width {width}");
        float inputRight = inputBar.Left + input.Left + input.Width;
        Assert.True(inputRight <= width + 0.5f,
            $"input field right {inputRight} escapes window width {width}");
        float sendRight = inputBar.Left + send.Left + send.Width;
        Assert.True(sendRight <= width + 0.5f,
            $"send right {sendRight} escapes window width {width}");
        Assert.True(menuButton.Left >= 0f, "menu button escaped left");
        // The field must stay BETWEEN the menu button and the send button.
        Assert.True(input.Left >= menuButton.Left + menuButton.Width - 0.5f,
            $"input {input.Left} overlaps menu button ending {menuButton.Left + menuButton.Width}");
        Assert.True(input.Left + input.Width <= send.Left + 0.5f,
            $"input ends {input.Left + input.Width} past send start {send.Left}");
    }

    [Theory]
    [InlineData(300f, 100f)]
    [InlineData(120f, 40f)]
    [InlineData(80f, 30f)]
    public void ResizingTheWindowSmall_NoInputRowQuadRendersOutsideTheWindowRect(float width, float height)
    {
        var infos = FixtureLoader.LoadChatInfos();
        ImportedLayout layout = LayoutImporter.Build(infos, id => (id, 8, 8), null);
        var controller = ChatWindowController.Bind(
            infos, layout, new ChatVM(new ChatLog()), () => NullCommandBus.Instance,
            new ChatWindowState(), null, null, NoTex);
        Assert.NotNull(controller);
        UiElement window = layout.FindElement(0x10000600u)!;
        var root = new UiRoot { Width = 800f, Height = 600f };
        root.AddChild(window);

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(root.Width, root.Height));
        var ctx = new UiRenderContext(renderer, new Vector2(root.Width, root.Height));
        window.DrawSelfAndChildren(ctx);

        window.Width = width;
        window.Height = height;
        window.ResetAnchorCapture();
        // Two frames — same "raw-edge LayoutPolicy needs a settle pass" reasoning
        // as the geometry sibling test above.
        renderer.Begin(new Vector2(root.Width, root.Height));
        window.DrawSelfAndChildren(ctx);
        renderer.Begin(new Vector2(root.Width, root.Height));
        window.DrawSelfAndChildren(ctx);

        float windowLeft = window.ScreenPosition.X;
        float windowTop = window.ScreenPosition.Y;
        const float Slop = 0.5f;
        foreach (var seg in renderer.DebugSpriteSegmentVerts)
        {
            for (int i = 0; i < seg.Verts.Count / 8; i++)
            {
                float vx = seg.Verts[i * 8];
                float vy = seg.Verts[i * 8 + 1];
                Assert.True(
                    vx >= windowLeft - Slop && vx <= windowLeft + width + Slop
                    && vy >= windowTop - Slop && vy <= windowTop + height + Slop,
                    $"quad vertex ({vx},{vy}) escapes the {width}x{height} chat window rect " +
                    $"[{windowLeft},{windowTop}]-[{windowLeft + width},{windowTop + height}] " +
                    $"(texture {seg.Texture})");
            }
        }
    }

    private static void ApplyLayoutPassLocal(UiElement parent)
    {
        foreach (var child in parent.Children)
        {
            child.ApplyAnchor(parent.Width, parent.Height);
            ApplyLayoutPassLocal(child);
        }
    }

    [Fact]
    public void TalkButtonAndSend_KeepAuthoredSizeFontAndWhiteCaptions()
    {
        var infos = FixtureLoader.LoadChatInfos();
        ImportedLayout layout = LayoutImporter.Build(infos, NoTex, null);
        var requestedFonts = new List<uint>();
        var controller = ChatWindowController.Bind(
            infos, layout, new ChatVM(new ChatLog()), () => NullCommandBus.Instance,
            new ChatWindowState(), null, null, NoTex,
            resolveFont: did => { requestedFonts.Add(did); return null; });
        Assert.NotNull(controller);

        UiMenu menu = Assert.IsType<UiMenu>(layout.FindElement(0x10000014u));
        Assert.Equal(46f, menu.Width);
        menu.OnSelect!.Invoke(ChatChannelKind.General);
        Assert.Equal(46f, menu.Width);
        Assert.Equal(new System.Numerics.Vector4(1f, 1f, 1f, 1f), menu.TextColor);
        Assert.True(menu.ButtonTextCentered);

        var send = Assert.IsType<UiButton>(layout.FindElement(0x10000019u));
        Assert.Equal(new System.Numerics.Vector4(1f, 1f, 1f, 1f), send.LabelColor);

        Assert.Contains(0x40000002u, requestedFonts);
    }
}
