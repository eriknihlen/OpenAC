using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Core.Chat;
using AcDream.UI.Abstractions.Panels.Chat;
using System.Numerics;

namespace AcDream.App.Tests.UI.Layout;

public sealed class RetailWindowLockPresentationControllerTests
{
    private static readonly uint[] LockedChatChromeIds =
    [
        0x10000693u, 0x10000694u, 0x10000695u, 0x10000696u,
        0x10000697u, 0x10000698u, 0x10000699u, 0x1000069Au,
    ];

    private static readonly uint[] LiveChatChromeIds =
    [
        0x1000069Bu, 0x1000069Cu, 0x1000069Du, 0x1000069Eu,
        0x1000069Fu, 0x100006A0u, 0x100006A1u, 0x100006A2u,
    ];

    [Fact]
    public void ExistingAndFutureNineSliceWindows_TrackCurrentLockPresentationExactly()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var existing = NewNineSlice();
        root.AddChild(existing);
        root.RegisterWindow("existing", existing);

        using var presentation = new RetailWindowLockPresentationController(root.WindowManager);

        Assert.True(existing.DrawResizeAffordances);
        Assert.True(existing.DrawCenterFill);

        root.UiLocked = true;
        Assert.False(existing.DrawResizeAffordances);
        Assert.True(existing.DrawCenterFill);

        var registeredWhileLocked = NewNineSlice();
        registeredWhileLocked.DrawCenterFill = false;
        root.AddChild(registeredWhileLocked);
        root.RegisterWindow("future", registeredWhileLocked);

        Assert.False(registeredWhileLocked.DrawResizeAffordances);
        Assert.False(registeredWhileLocked.DrawCenterFill);

        root.UiLocked = true;
        Assert.False(existing.DrawResizeAffordances);
        Assert.False(registeredWhileLocked.DrawResizeAffordances);

        root.UiLocked = false;
        Assert.True(existing.DrawResizeAffordances);
        Assert.True(registeredWhileLocked.DrawResizeAffordances);
        Assert.True(existing.DrawCenterFill);
        Assert.False(registeredWhileLocked.DrawCenterFill);
    }

    [Fact]
    public void LockedNineSlice_DrawsCenterAndEightBevelPieces_ButNoEightGripOverlays()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var frame = NewNineSlice();
        root.AddChild(frame);
        root.RegisterWindow("frame", frame);
        using var presentation = new RetailWindowLockPresentationController(root.WindowManager);

        TextRenderer unlocked = Draw(frame);
        Assert.Equal(17, SpriteCallCount(unlocked));
        Assert.Equal(8, GripCallCount(unlocked));

        root.UiLocked = true;
        TextRenderer locked = Draw(frame);

        Assert.Equal(9, SpriteCallCount(locked));
        Assert.Equal(0, GripCallCount(locked));
        Assert.Equal(1, CallsFor(locked, RetailChromeSprites.CenterFill));
        Assert.Equal(1, CallsFor(locked, RetailChromeSprites.TopEdge));
        Assert.Equal(1, CallsFor(locked, RetailChromeSprites.BottomEdge));
        Assert.Equal(1, CallsFor(locked, RetailChromeSprites.LeftEdge));
        Assert.Equal(1, CallsFor(locked, RetailChromeSprites.RightEdge));
        Assert.Equal(1, CallsFor(locked, RetailChromeSprites.CornerTL));
        Assert.Equal(1, CallsFor(locked, RetailChromeSprites.CornerTR));
        Assert.Equal(1, CallsFor(locked, RetailChromeSprites.CornerBL));
        Assert.Equal(1, CallsFor(locked, RetailChromeSprites.CornerBR));
    }

    [Fact]
    public void AuthoredChatChrome_SwapsLiveAndLockedSets_AndUnlockRestoresExactly()
    {
        var infos = FixtureLoader.LoadChatInfos();
        var layout = LayoutImporter.Build(infos, NoTex, null);
        _ = ChatWindowController.Bind(
            infos,
            layout,
            new ChatVM(new ChatLog()),
            () => NullCommandBus.Instance,
            new ChatWindowState(),
            null,
            null,
            NoTex);
        var root = new UiRoot { Width = 800, Height = 600 };
        root.AddChild(layout.Root);
        root.RegisterWindow("chat", layout.Root, layout.Root);

        using var presentation = new RetailWindowLockPresentationController(root.WindowManager);

        AssertChrome(layout, liveVisible: true);

        root.UiLocked = true;
        AssertChrome(layout, liveVisible: false);

        root.UiLocked = true;
        AssertChrome(layout, liveVisible: false);

        root.UiLocked = false;
        AssertChrome(layout, liveVisible: true);

        root.UiLocked = false;
        AssertChrome(layout, liveVisible: true);
    }

    [Fact]
    public void AuthoredWindowRegisteredWhileLocked_StartsWithLockedChrome()
    {
        var root = new UiRoot { Width = 800, Height = 600, UiLocked = true };
        using var presentation = new RetailWindowLockPresentationController(root.WindowManager);
        var infos = FixtureLoader.LoadChatInfos();
        var layout = LayoutImporter.Build(infos, NoTex, null);
        _ = ChatWindowController.Bind(
            infos,
            layout,
            new ChatVM(new ChatLog()),
            () => NullCommandBus.Instance,
            new ChatWindowState(),
            null,
            null,
            NoTex);

        root.AddChild(layout.Root);
        root.RegisterWindow("late-chat", layout.Root, layout.Root);

        AssertChrome(layout, liveVisible: false);
    }

    [Fact]
    public void FloatingChat_LockHidesAllResizeGrips_ButLeavesTitleDragElementVisible()
    {
        var layout = FixtureLoader.LoadFloatyChat();
        var root = new UiRoot { Width = 800, Height = 600 };
        root.AddChild(layout.Root);
        root.RegisterWindow("floaty", layout.Root, layout.Root);
        using var presentation = new RetailWindowLockPresentationController(root.WindowManager);
        UiElement? titleDrag = layout.FindElement(0x10000529u);
        Assert.NotNull(titleDrag);
        UiResizeGrip[] grips = DescendantsAndSelf(layout.Root).OfType<UiResizeGrip>().ToArray();
        Assert.Equal(8, grips.Length);
        Assert.All(grips, grip => Assert.True(grip.Visible));
        Assert.True(titleDrag.Visible);

        root.UiLocked = true;

        Assert.All(grips, grip => Assert.False(grip.Visible));
        Assert.True(titleDrag.Visible);

        root.UiLocked = false;
        Assert.All(grips, grip => Assert.True(grip.Visible));
        Assert.True(titleDrag.Visible);
    }

    [Fact]
    public void AuthoredChrome_MissingPairMembersAndUnrelatedDecoration_TransitionSafely()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var frame = new UiPanel { Width = 200, Height = 100 };
        var lockedOnly = new UiPanel { DatElementId = 0x10000633u, Visible = false };
        var liveOnly = new UiPanel { DatElementId = 0x1000063Cu, Visible = true };
        var unrelated = new UiPanel { DatElementId = 0x10000632u, Visible = true };
        var initiallyHiddenLive = new UiPanel { DatElementId = 0x1000063Du, Visible = false };
        frame.AddChild(lockedOnly);
        frame.AddChild(liveOnly);
        frame.AddChild(unrelated);
        frame.AddChild(initiallyHiddenLive);
        root.AddChild(frame);
        root.RegisterWindow("partial", frame);
        using var presentation = new RetailWindowLockPresentationController(root.WindowManager);

        root.UiLocked = true;
        Assert.True(lockedOnly.Visible);
        Assert.False(liveOnly.Visible);
        Assert.True(unrelated.Visible);
        Assert.False(initiallyHiddenLive.Visible);

        root.UiLocked = false;
        Assert.False(lockedOnly.Visible);
        Assert.True(liveOnly.Visible);
        Assert.True(unrelated.Visible);
        Assert.False(initiallyHiddenLive.Visible);
    }

    [Theory]
    [InlineData(0x10000633u, 0x1000063Bu)]
    [InlineData(0x10000643u, 0x1000064Bu)]
    [InlineData(0x10000653u, 0x1000065Bu)]
    [InlineData(0x10000663u, 0x1000066Bu)]
    [InlineData(0x10000673u, 0x1000067Bu)]
    [InlineData(0x10000683u, 0x1000068Bu)]
    [InlineData(0x10000693u, 0x1000069Bu)]
    [InlineData(0x100006A5u, 0x100006ADu)]
    public void EveryRetailAuthoredChromeBlock_SwapsAllEightMembers(
        uint lockedStart,
        uint liveStart)
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var frame = new UiPanel { Width = 200, Height = 100 };
        UiElement[] lockedChrome = Enumerable.Range(0, 8)
            .Select(i => new UiPanel { DatElementId = lockedStart + (uint)i })
            .Cast<UiElement>()
            .ToArray();
        UiElement[] liveChrome = Enumerable.Range(0, 8)
            .Select(i => new UiPanel { DatElementId = liveStart + (uint)i })
            .Cast<UiElement>()
            .ToArray();
        foreach (UiElement element in lockedChrome.Concat(liveChrome))
            frame.AddChild(element);
        root.AddChild(frame);
        root.RegisterWindow("authored", frame);
        using var presentation = new RetailWindowLockPresentationController(root.WindowManager);

        Assert.All(lockedChrome, element => Assert.False(element.Visible));
        Assert.All(liveChrome, element => Assert.True(element.Visible));

        root.UiLocked = true;
        Assert.All(lockedChrome, element => Assert.True(element.Visible));
        Assert.All(liveChrome, element => Assert.False(element.Visible));

        root.UiLocked = false;
        Assert.All(lockedChrome, element => Assert.False(element.Visible));
        Assert.All(liveChrome, element => Assert.True(element.Visible));
    }

    [Fact]
    public void SmartBoxLiveOnlyChrome_HidesAndRestores_WithoutTouchingOtherType2OrType3Decoration()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var frame = new UiPanel { Width = 200, Height = 100 };
        UiElement[] smartBoxChrome = Enumerable.Range(0, 8)
            .Select(i => new UiPanel { DatElementId = 0x100006CAu + (uint)i })
            .Cast<UiElement>()
            .ToArray();
        var unrelatedType2 = new UiPanel { DatElementId = 0x10000529u };
        var unrelatedType3 = new UiPanel { DatElementId = 0x100006D2u };
        foreach (UiElement chrome in smartBoxChrome)
            frame.AddChild(chrome);
        frame.AddChild(unrelatedType2);
        frame.AddChild(unrelatedType3);
        root.AddChild(frame);
        root.RegisterWindow("smartbox", frame);
        using var presentation = new RetailWindowLockPresentationController(root.WindowManager);

        root.UiLocked = true;
        Assert.All(smartBoxChrome, chrome => Assert.False(chrome.Visible));
        Assert.True(unrelatedType2.Visible);
        Assert.True(unrelatedType3.Visible);

        root.UiLocked = false;
        Assert.All(smartBoxChrome, chrome => Assert.True(chrome.Visible));
        Assert.True(unrelatedType2.Visible);
        Assert.True(unrelatedType3.Visible);
    }

    [Fact]
    public void LockedLateRegistration_AppliesChromeBeforeControllerOnShown()
    {
        var root = new UiRoot { Width = 800, Height = 600, UiLocked = true };
        using var presentation = new RetailWindowLockPresentationController(root.WindowManager);
        var frame = NewNineSlice();
        root.AddChild(frame);
        var observer = new OnShownObserver(() => !frame.DrawResizeAffordances);

        root.RegisterWindow("late", frame, frame, observer);

        Assert.True(observer.ObservedLockedChrome);
        Assert.Equal(1, observer.ShownCount);
    }

    private static UiNineSlicePanel NewNineSlice() =>
        new(static id => (id, 5, 5)) { Width = 200, Height = 100 };

    private static TextRenderer Draw(UiElement element)
    {
        var renderer = new TextRenderer(new RecordingGpuDevice(), new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        element.DrawSelfAndChildren(new UiRenderContext(renderer, new Vector2(800f, 600f)));
        return renderer;
    }

    private static int SpriteCallCount(TextRenderer renderer) =>
        renderer.DebugSpriteSegments.Sum(segment => segment.VertexCount / 6);

    private static int CallsFor(TextRenderer renderer, uint texture) =>
        renderer.DebugSpriteSegments
            .Where(segment => segment.Texture == texture)
            .Sum(segment => segment.VertexCount / 6);

    private static int GripCallCount(TextRenderer renderer) =>
        CallsFor(renderer, RetailChromeSprites.GripTop)
        + CallsFor(renderer, RetailChromeSprites.GripBottom)
        + CallsFor(renderer, RetailChromeSprites.GripLeft)
        + CallsFor(renderer, RetailChromeSprites.GripRight)
        + CallsFor(renderer, RetailChromeSprites.GripCorner);

    private static IEnumerable<UiElement> DescendantsAndSelf(UiElement root)
    {
        yield return root;
        foreach (UiElement child in root.Children)
            foreach (UiElement descendant in DescendantsAndSelf(child))
                yield return descendant;
    }

    private static void AssertChrome(ImportedLayout layout, bool liveVisible)
    {
        foreach (uint id in LiveChatChromeIds)
        {
            UiElement? element = layout.FindElement(id);
            Assert.NotNull(element);
            Assert.Equal(liveVisible, element.Visible);
        }
        foreach (uint id in LockedChatChromeIds)
        {
            UiElement? element = layout.FindElement(id);
            Assert.NotNull(element);
            Assert.Equal(!liveVisible, element.Visible);
        }
    }

    private static (uint tex, int width, int height) NoTex(uint _) => (0u, 0, 0);

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private sealed class OnShownObserver(Func<bool> observe) : IRetainedPanelController
    {
        public int ShownCount { get; private set; }
        public bool ObservedLockedChrome { get; private set; }

        public void OnShown()
        {
            ShownCount++;
            ObservedLockedChrome = observe();
        }

        public void Dispose()
        {
        }
    }
}
