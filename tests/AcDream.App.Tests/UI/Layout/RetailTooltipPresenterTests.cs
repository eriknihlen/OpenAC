using System.Collections.Generic;
using System.Linq;
using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class RetailTooltipPresenterTests
{
    private sealed class HoverTarget : UiElement;

    private const uint PopupRootId = 0x900u;
    private const uint TextChildId = 0x901u;
    private const uint PopupLayoutDid = 0x21000041u;

    private static ImportedLayout BuildPopup()
    {
        var rootInfo = new ElementInfo
        {
            Id = PopupRootId, Type = 3, X = 0, Y = 0, Width = 30, Height = 30,
            TooltipTextChildElementId = TextChildId,
        };
        var textInfo = new ElementInfo
        {
            Id = TextChildId, Type = 12, X = 2, Y = 2, Width = 26, Height = 26,
        };
        return LayoutImporter.BuildFromInfos(
            rootInfo, [textInfo], _ => (0u, 0, 0), null);
    }

    private static (UiRoot Root, RetailTooltipPresenter Presenter, List<(uint, uint)> Requests)
        CreateHarness()
    {
        var root = new UiRoot { Width = 800f, Height = 600f };
        var requests = new List<(uint LayoutDid, uint RootElementId)>();
        var presenter = new RetailTooltipPresenter(root, (layoutDid, rootElementId) =>
        {
            requests.Add((layoutDid, rootElementId));
            return BuildPopup();
        });
        return (root, presenter, requests);
    }

    private static HoverTarget AddFullyAuthoredTarget(UiRoot root, string text = "Rotate left.")
    {
        var target = new HoverTarget
        {
            Left = 100, Top = 100, Width = 40, Height = 20,
            AuthoredTooltipEnabled = true,
            AuthoredTooltipText = text,
            AuthoredTooltipRootElementId = PopupRootId,
            AuthoredTooltipLayoutDid = PopupLayoutDid,
        };
        root.AddChild(target);
        return target;
    }

    [Fact]
    public void NoTooltipShows_WhenPropertiesAbsent()
    {
        // Regression pin: an element that authors NONE of the five tooltip
        // properties must never produce a popup, even after the dwell delay
        // and the mouse resting on it.
        var (root, _, requests) = CreateHarness();
        var target = new HoverTarget { Left = 100, Top = 100, Width = 40, Height = 20 };
        root.AddChild(target);
        int childrenBefore = root.Children.Count;

        root.OnMouseMove(110, 110);
        root.Tick(0.016, 0);
        root.Tick(0.016, root.TooltipDelayMs + 50);

        Assert.Empty(requests);
        Assert.Equal(childrenBefore, root.Children.Count);
    }

    [Fact]
    public void DelayThenShow_MountsPopupOnlyAfterTheDwellDelay()
    {
        var (root, _, requests) = CreateHarness();
        AddFullyAuthoredTarget(root);
        int childrenBefore = root.Children.Count;

        root.OnMouseMove(110, 110);
        root.Tick(0.016, 0);
        Assert.Equal(childrenBefore, root.Children.Count); // not yet — dwell hasn't elapsed

        root.Tick(0.016, root.TooltipDelayMs - 1);
        Assert.Equal(childrenBefore, root.Children.Count); // still one ms short

        root.Tick(0.016, root.TooltipDelayMs);
        Assert.Equal(childrenBefore + 1, root.Children.Count); // popup mounted
        Assert.Single(requests, r => r == (PopupLayoutDid, PopupRootId));
    }

    [Fact]
    public void GlobalEnableGateOff_SuppressesPresentation_ButTheDwellTimerStillFires()
    {
        var (root, presenter, requests) = CreateHarness();
        presenter.Enabled = false;
        AddFullyAuthoredTarget(root);
        int childrenBefore = root.Children.Count;
        bool eventFired = false;
        root.TooltipShow += _ => eventFired = true;

        root.OnMouseMove(110, 110);
        root.Tick(0.016, 0);
        root.Tick(0.016, root.TooltipDelayMs);

        Assert.True(eventFired);
        Assert.Empty(requests);
        Assert.Equal(childrenBefore, root.Children.Count);
    }

    [Fact]
    public void WidgetOwnTooltipDisabled_SuppressesPresentation()
    {
        var (root, _, requests) = CreateHarness();
        var target = AddFullyAuthoredTarget(root);
        target.AuthoredTooltipEnabled = false;

        root.OnMouseMove(110, 110);
        root.Tick(0.016, 0);
        root.Tick(0.016, root.TooltipDelayMs);

        Assert.Empty(requests);
    }

    [Fact]
    public void MissingText_SuppressesPresentation()
    {
        var (root, _, requests) = CreateHarness();
        var target = AddFullyAuthoredTarget(root);
        target.AuthoredTooltipText = null;

        root.OnMouseMove(110, 110);
        root.Tick(0.016, 0);
        root.Tick(0.016, root.TooltipDelayMs);

        Assert.Empty(requests);
    }

    [Fact]
    public void DismissesOnHoverTargetChange()
    {
        var (root, _, _) = CreateHarness();
        AddFullyAuthoredTarget(root);
        var other = new HoverTarget { Left = 400, Top = 400, Width = 40, Height = 20 };
        root.AddChild(other);
        int childrenBeforeShow = root.Children.Count;

        root.OnMouseMove(110, 110);
        root.Tick(0.016, 0);
        root.Tick(0.016, root.TooltipDelayMs);
        Assert.Equal(childrenBeforeShow + 1, root.Children.Count);

        root.OnMouseMove(410, 410);
        Assert.Equal(childrenBeforeShow, root.Children.Count);
    }

    [Fact]
    public void DismissesWhenTheOwnerElementIsRemoved()
    {
        var (root, _, _) = CreateHarness();
        var target = AddFullyAuthoredTarget(root);
        int childrenBeforeShow = root.Children.Count;

        root.OnMouseMove(110, 110);
        root.Tick(0.016, 0);
        root.Tick(0.016, root.TooltipDelayMs);
        Assert.Equal(childrenBeforeShow + 1, root.Children.Count);

        root.RemoveChild(target);
        Assert.Equal(childrenBeforeShow - 1, root.Children.Count); // target removed, popup removed
    }

    [Fact]
    public void AutoHidesAfterTheDurationElapses()
    {
        var (root, _, _) = CreateHarness();
        AddFullyAuthoredTarget(root);
        int childrenBeforeShow = root.Children.Count;

        root.OnMouseMove(110, 110);
        root.Tick(0.016, 0);
        root.Tick(0.016, root.TooltipDelayMs);
        Assert.Equal(childrenBeforeShow + 1, root.Children.Count);

        root.Tick(0.016, root.TooltipDelayMs + root.TooltipDurationMs);
        Assert.Equal(childrenBeforeShow, root.Children.Count);
    }

    [Fact]
    public void PerElementDelayOverride_ReplacesTheGlobalDelay()
    {
        var (root, _, requests) = CreateHarness();
        var target = AddFullyAuthoredTarget(root);
        target.AuthoredTooltipDelaySeconds = 0f; // the live-DAT-probed override value

        root.OnMouseMove(110, 110);
        root.Tick(0.016, 0); // the very next tick after hover starts — no dwell wait
        Assert.Single(requests);
    }

    [Fact]
    public void PositionClampsToStayFullyOnTheDisplay()
    {
        var (root, _, _) = CreateHarness();
        root.Width = 40f;
        root.Height = 40f;
        var target = new HoverTarget
        {
            Left = 0, Top = 0, Width = 40, Height = 40,
            AuthoredTooltipEnabled = true,
            AuthoredTooltipText = "hi",
            AuthoredTooltipRootElementId = PopupRootId,
            AuthoredTooltipLayoutDid = PopupLayoutDid,
        };
        root.AddChild(target);

        root.OnMouseMove(38, 38);
        root.Tick(0.016, 0);
        root.Tick(0.016, root.TooltipDelayMs);

        UiElement popup = root.Children.Single(c => !ReferenceEquals(c, target));
        Assert.True(popup.Left + popup.Width <= 40f, $"popup right edge {popup.Left + popup.Width} exceeds canvas width 40");
        Assert.True(popup.Top + popup.Height <= 40f, $"popup bottom edge {popup.Top + popup.Height} exceeds canvas height 40");
        Assert.True(popup.Left >= 0f);
        Assert.True(popup.Top >= 0f);
    }

    [Fact]
    public void AutoResizesTheRootByTheMeasuredTextDelta()
    {
        var (root, _, _) = CreateHarness();
        var target = AddFullyAuthoredTarget(
            root, text: "This is a much longer tooltip than the authored placeholder.");

        root.OnMouseMove(110, 110);
        root.Tick(0.016, 0);
        root.Tick(0.016, root.TooltipDelayMs);

        UiElement popup = root.Children.Single(c => !ReferenceEquals(c, target));
        Assert.True(popup.Width > 30f, $"expected the popup to grow past its authored 30px width, got {popup.Width}");
    }


    [Fact]
    public void F1_PositionsAtMouse_OffsetBy32PixelsOnBothAxes()
    {
        var (root, _, _) = CreateHarness();
        var target = AddFullyAuthoredTarget(root);

        root.OnMouseMove(110, 110);
        root.Tick(0.016, 0);
        root.Tick(0.016, root.TooltipDelayMs);

        UiElement popup = root.Children.Single(c => !ReferenceEquals(c, target));
        Assert.Equal(142f, popup.Left); // 110 + 32
        Assert.Equal(142f, popup.Top);  // 110 + 32
    }

    [Fact]
    public void F2_MouseMoveWithinTheSameWidget_ResetsTheDwellClock()
    {
        var (root, _, requests) = CreateHarness();
        AddFullyAuthoredTarget(root);

        root.OnMouseMove(110, 110);       // hover starts at nowMs=0
        root.Tick(0.016, 0);
        root.Tick(0.016, root.TooltipDelayMs - 10); // nowMs=240, 10ms short
        Assert.Empty(requests);

        // Jiggle within the same widget at nowMs=240 — resets the deadline.
        root.OnMouseMove(111, 111);
        root.Tick(0.016, root.TooltipDelayMs); // nowMs=250 — the OLD deadline
        Assert.Empty(requests);

        root.Tick(0.016, (root.TooltipDelayMs - 10) + root.TooltipDelayMs); // nowMs=490 (240+250)
        Assert.Single(requests);
    }

    [Fact]
    public void F5_TextChildResolvesToSomethingOtherThanUiText_NeverMounts()
    {
        var rootInfo = new ElementInfo
        {
            Id = PopupRootId, Type = 3, X = 0, Y = 0, Width = 30, Height = 30,
            TooltipTextChildElementId = TextChildId,
        };
        var notTextInfo = new ElementInfo
        {
            Id = TextChildId, Type = 3, X = 2, Y = 2, Width = 26, Height = 26, // type 3, NOT 12 -> UiDatElement
        };
        var root = new UiRoot { Width = 800f, Height = 600f };
        var requests = new List<(uint, uint)>();
        var presenter = new RetailTooltipPresenter(root, (layoutDid, rootElementId) =>
        {
            requests.Add((layoutDid, rootElementId));
            return LayoutImporter.BuildFromInfos(rootInfo, [notTextInfo], _ => (0u, 0, 0), null);
        });
        AddFullyAuthoredTarget(root);
        int childrenBefore = root.Children.Count;

        root.OnMouseMove(110, 110);
        root.Tick(0.016, 0);
        root.Tick(0.016, root.TooltipDelayMs);

        Assert.Single(requests); // the layout WAS built...
        Assert.Equal(childrenBefore, root.Children.Count); // ...but never mounted
    }

    [Fact]
    public void F6_MouseCaptureElsewhere_SuppressesTheDwellArm_UntilCaptureReleases()
    {
        var (root, _, requests) = CreateHarness();
        AddFullyAuthoredTarget(root);
        var other = new HoverTarget { Left = 400, Top = 400, Width = 40, Height = 20 };
        root.AddChild(other);

        root.OnMouseMove(110, 110);
        root.Tick(0.016, 0);

        root.SetCapture(other);
        root.Tick(0.016, root.TooltipDelayMs + 50);
        Assert.Empty(requests);

        root.ReleaseCapture(); // restarts the idle deadline at nowMs=300
        root.Tick(0.016, root.TooltipDelayMs + 50 + root.TooltipDelayMs + 50);
        Assert.Single(requests); // now arms normally
    }

    [Fact]
    public void F7_ReleaseCaptureWhileATooltipIsAlreadyShown_DoesNotHideAndReshowIt()
    {
        var (root, _, requests) = CreateHarness();
        var target = AddFullyAuthoredTarget(root);
        int childrenBeforeShow = root.Children.Count;
        bool hideFired = false;
        root.TooltipHide += _ => hideFired = true;

        root.OnMouseMove(110, 110);
        root.Tick(0.016, 0);
        root.Tick(0.016, root.TooltipDelayMs);
        Assert.Equal(childrenBeforeShow + 1, root.Children.Count); // tooltip showing
        Assert.Single(requests);

        root.SetCapture(target);
        root.ReleaseCapture(); // restarts the idle deadline at nowMs=TooltipDelayMs

        root.Tick(0.016, root.TooltipDelayMs + root.TooltipDelayMs + 10);
        Assert.False(hideFired, "tooltip must not hide on a mere capture release");
        Assert.Equal(childrenBeforeShow + 1, root.Children.Count); // still showing
        Assert.Single(requests);
    }

    [Fact]
    public void F8_AutoResizeAppliesTheAuthoredMaxWidthClamp()
    {
        var rootInfo = new ElementInfo
        {
            Id = PopupRootId, Type = 3, X = 0, Y = 0, Width = 30, Height = 30,
            TooltipTextChildElementId = TextChildId,
            MaxWidth = 40, // P0x3D
        };
        var textInfo = new ElementInfo
        {
            Id = TextChildId, Type = 12, X = 2, Y = 2, Width = 26, Height = 26,
        };
        var root = new UiRoot { Width = 800f, Height = 600f };
        var presenter = new RetailTooltipPresenter(root, (_, _) =>
            LayoutImporter.BuildFromInfos(rootInfo, [textInfo], _ => (0u, 0, 0), null));
        var target = AddFullyAuthoredTarget(
            root,
            text: "This is a much longer tooltip than the authored placeholder, long "
                + "enough to want to grow well past forty pixels wide.");

        root.OnMouseMove(110, 110);
        root.Tick(0.016, 0);
        root.Tick(0.016, root.TooltipDelayMs);

        UiElement popup = root.Children.Single(c => !ReferenceEquals(c, target));
        Assert.Equal(40f, popup.Width);
    }


    private sealed class RuntimeTextTarget : UiElement
    {
        public string? Runtime { get; set; }
        public override string? GetTooltipText() => Runtime;
    }

    private static RuntimeTextTarget AddRuntimeTextTarget(
        UiRoot root, string? runtime, bool authoredOn = true,
        uint layoutDid = PopupLayoutDid, uint sourceLayoutDid = 0u)
    {
        var target = new RuntimeTextTarget
        {
            Left = 100, Top = 100, Width = 40, Height = 20,
            Runtime = runtime,
            AuthoredTooltipEnabled = authoredOn,
            AuthoredTooltipRootElementId = PopupRootId,
            AuthoredTooltipLayoutDid = layoutDid,
            SourceLayoutDid = sourceLayoutDid,
        };
        root.AddChild(target);
        return target;
    }

    [Fact]
    public void LongTooltip_RewrapsAtTheClampedPopupWidth_AndGrowsHeightForTheExtraLines()
    {
        var (root, _, _) = CreateHarness();
        string longText = string.Join(
            " ", Enumerable.Repeat("description", 40)); // far wider than 200px
        var target = new HoverTarget
        {
            Left = 100, Top = 100, Width = 40, Height = 20,
            AuthoredTooltipEnabled = true,
            AuthoredTooltipText = longText,
            AuthoredTooltipRootElementId = PopupRootId,
            AuthoredTooltipLayoutDid = PopupLayoutDid,
        };
        root.AddChild(target);
        int childrenBefore = root.Children.Count;

        HoverAndDwellWithClampedPopup(root, maxWidth: 200);

        UiElement popup = root.Children[^1];
        Assert.True(root.Children.Count > childrenBefore, "popup did not mount");
        // Root = authored 30 frame grown to hold the <=200 text plus padding.
        Assert.True(popup.Width <= 200f + 30f, $"popup width {popup.Width} escaped the clamp");
        var text = FindText(popup);
        Assert.NotNull(text);
        Assert.False(text!.Centered);
        Assert.False(text.RightAligned);
        var lines = text!.LinesProvider();
        Assert.True(lines.Count >= 3,
            $"expected the clamped width to force >=3 lines, got {lines.Count}");
        Assert.All(lines, l => Assert.True(l.Text.Length * 8f <= 200f + 8f, $"line escaped wrap: {l.Text}"));
        // The root grew to hold the extra lines.
        float lineHeight = 14f;
        Assert.True(popup.Height >= lines.Count * lineHeight,
            $"popup height {popup.Height} does not fit {lines.Count} lines");
    }

    [Fact]
    public void AuthoredMargins_ShrinkTheWrapBound_AndInsetTheMeasuredWidth()
    {
        var (root, _, _) = CreateHarness();
        string longText = string.Join(
            " ", Enumerable.Repeat("description", 40));
        var target = new HoverTarget
        {
            Left = 100, Top = 100, Width = 40, Height = 20,
            AuthoredTooltipEnabled = true,
            AuthoredTooltipText = longText,
            AuthoredTooltipRootElementId = PopupRootId,
            AuthoredTooltipLayoutDid = PopupLayoutDid,
        };
        root.AddChild(target);

        HoverAndDwellWithClampedPopup(root, maxWidth: 200, marginX: 40, marginY: 8);

        var text = FindText(root.Children[^1]);
        Assert.NotNull(text);
        var lines = text!.LinesProvider();
        Assert.True(lines.Count >= 3, $"expected >=3 wrapped lines, got {lines.Count}");
        // Glyphs wrap INSIDE the margins...
        Assert.All(lines, l => Assert.True(
            l.Text.Length * 8f <= 200f - 80f + 8f,
            $"line ignored the margins: {l.Text}"));
        Assert.Equal(40f, text.MarginLeft);
        Assert.Equal(40f, text.MarginRight);
        // Height accounts for the vertical margins on top of the lines.
        float lineHeight = 14f;
        Assert.True(root.Children[^1].Height >= lines.Count * lineHeight + 16f,
            $"popup height {root.Children[^1].Height} lost the vertical margins");
    }

    private static void HoverAndDwellWithClampedPopup(
        UiRoot root, int maxWidth, int marginX = 0, int marginY = 0)
    {
        var presenter = new RetailTooltipPresenter(root, (_, _) =>
        {
            ImportedLayout layout = BuildPopup();
            if (layout.FindElement(TextChildId) is { } textChild)
            {
                textChild.AuthoredResizeMaxWidth = maxWidth;
                if (textChild is UiText t)
                {
                    t.MarginLeft = marginX;
                    t.MarginRight = marginX;
                    t.MarginTop = marginY;
                    t.MarginBottom = marginY;
                }
            }
            return layout;
        });
        HoverAndDwell(root);
    }

    private static UiText? FindText(UiElement element)
    {
        if (element is UiText text) return text;
        foreach (UiElement child in element.Children)
            if (FindText(child) is { } found) return found;
        return null;
    }

    private static void HoverAndDwell(UiRoot root)
    {
        root.OnMouseMove(110, 110);
        root.Tick(0.016, 0);
        root.Tick(0.016, root.TooltipDelayMs);
    }

    [Fact]
    public void RuntimeText_ShowsEvenWithNoAuthoredP0x49()
    {
        var (root, _, requests) = CreateHarness();
        var target = AddRuntimeTextTarget(
            root, "When this option is chosen, you will always appear as offline.");
        int childrenBefore = root.Children.Count;

        HoverAndDwell(root);

        Assert.Single(requests, r => r == (PopupLayoutDid, PopupRootId));
        Assert.Equal(childrenBefore + 1, root.Children.Count);
        UiElement popup = root.Children.Single(c => !ReferenceEquals(c, target));
        UiText text = Assert.IsType<UiText>(FindById(popup, TextChildId));
        Assert.Contains(
            "appear as offline",
            string.Join(' ', text.LinesProvider!().Select(l => l.Text)));
    }

    [Fact]
    public void RuntimeText_WinsOverTheAuthoredP0x49()
    {
        var (root, _, _) = CreateHarness();
        var target = AddRuntimeTextTarget(root, "runtime wins");
        target.AuthoredTooltipText = "authored loses";

        HoverAndDwell(root);

        UiElement popup = root.Children.Single(c => !ReferenceEquals(c, target));
        UiText text = Assert.IsType<UiText>(FindById(popup, TextChildId));
        string shown = string.Join(' ', text.LinesProvider!().Select(l => l.Text));
        Assert.Contains("runtime wins", shown);
        Assert.DoesNotContain("authored loses", shown);
    }

    [Fact]
    public void RuntimeText_DoesNotNeedTheAuthoredTooltipOnBit()
    {
        var (root, _, requests) = CreateHarness();
        AddRuntimeTextTarget(root, "runtime text, authored bit off", authoredOn: false);

        HoverAndDwell(root);

        Assert.Single(requests);
    }

    [Fact]
    public void AuthoredText_StillRequiresTheAuthoredTooltipOnBit()
    {
        var (root, _, requests) = CreateHarness();
        var target = AddRuntimeTextTarget(root, runtime: null, authoredOn: false);
        target.AuthoredTooltipText = "authored, but P0x4B is off";

        HoverAndDwell(root);

        Assert.Empty(requests);
    }

    [Fact]
    public void MissingP0x48_FallsBackToTheElementsOwnSourceLayout()
    {
        var (root, _, requests) = CreateHarness();
        AddRuntimeTextTarget(
            root, "no P0x48 authored", layoutDid: 0u, sourceLayoutDid: 0x21000099u);

        HoverAndDwell(root);

        Assert.Single(requests, r => r == (0x21000099u, PopupRootId));
    }

    [Fact]
    public void MissingP0x48_AndNoSourceLayout_ShowsNothing()
    {
        var (root, _, requests) = CreateHarness();
        AddRuntimeTextTarget(root, "nowhere to build the popup", layoutDid: 0u);

        HoverAndDwell(root);

        Assert.Empty(requests);
    }

    [Fact]
    public void MissingP0x47_ShowsNothing_EvenWithRuntimeText()
    {
        var (root, _, requests) = CreateHarness();
        var target = AddRuntimeTextTarget(root, "text but no popup root");
        target.AuthoredTooltipRootElementId = 0u;

        HoverAndDwell(root);

        Assert.Empty(requests);
    }

    private static UiElement? FindById(UiElement root, uint datElementId)
    {
        if (root.DatElementId == datElementId) return root;
        foreach (UiElement child in root.Children)
            if (FindById(child, datElementId) is { } found) return found;
        return null;
    }


    private const uint WorldFoundGuid = 0x80000123u;

    private sealed class DragSourceTarget : UiElement
    {
        public override bool IsDragSource => true;
        public override object? GetDragPayload() => "payload";
    }

    private static (UiRoot Root, RetailTooltipPresenter Presenter, List<(uint, uint)> Requests)
        CreateWorldHarness(Func<uint?> guidProvider, Func<uint, string?>? nameResolver = null,
                           Func<bool>? enabled = null)
    {
        var (root, presenter, requests) = CreateHarness();
        presenter.WorldHoverGuidProvider = guidProvider;
        presenter.WorldHoverNameResolver = nameResolver ?? (_ => "A Drudge");
        presenter.WorldTooltipsEnabled = enabled ?? (() => true);
        return (root, presenter, requests);
    }

    [Fact]
    public void WorldHover_StagesOnTheFoundEdge_MountsOnlyAfterTheIdleDwell()
    {
        var (root, presenter, requests) = CreateWorldHarness(() => WorldFoundGuid);
        int childrenBefore = root.Children.Count;

        root.Tick(0.016, 0);
        presenter.Tick();   // the found edge fires here — staged, not shown
        Assert.Empty(requests);
        Assert.Equal(childrenBefore, root.Children.Count);

        root.Tick(0.016, root.TooltipDelayMs - 1);
        presenter.Tick();
        Assert.Empty(requests);   // one ms short of the idle deadline

        root.Tick(0.016, root.TooltipDelayMs);
        presenter.Tick();
        Assert.Single(requests, r => r == (0x21000041u, 0x10000395u));
        Assert.Equal(childrenBefore + 1, root.Children.Count);
    }

    [Fact]
    public void WorldHover_MouseMovingContinuously_NeverMountsUntilItRests()
    {
        var (root, presenter, requests) = CreateWorldHarness(() => WorldFoundGuid);

        for (long t = 0; t <= 2000; t += 100)   // 100 ms between moves < 250 ms dwell
        {
            root.Tick(0.016, t);
            root.OnMouseMove(100 + (int)(t / 10), 100);
            presenter.Tick();
        }
        Assert.Empty(requests);

        // Rest: no further moves; the dwell elapses from the LAST move.
        root.Tick(0.016, 2000 + root.TooltipDelayMs);
        presenter.Tick();
        Assert.Single(requests);
    }

    [Fact]
    public void WorldHover_HidesWhenTheFoundGuidClears()
    {
        uint? found = WorldFoundGuid;
        var (root, presenter, _) = CreateWorldHarness(() => found);
        int childrenBefore = root.Children.Count;

        root.Tick(0.016, root.TooltipDelayMs);
        presenter.Tick();
        Assert.Equal(childrenBefore + 1, root.Children.Count);

        found = null;
        presenter.Tick();

        Assert.Equal(childrenBefore, root.Children.Count);
    }

    [Fact]
    public void WorldHover_ShowTooltipsOff_ShowsNothing()
    {
        var (root, presenter, requests) = CreateWorldHarness(
            () => WorldFoundGuid, enabled: () => false);
        int childrenBefore = root.Children.Count;

        root.Tick(0.016, root.TooltipDelayMs + 50);
        presenter.Tick();
        presenter.Tick();

        Assert.Empty(requests);
        Assert.Equal(childrenBefore, root.Children.Count);
    }

    [Fact]
    public void WorldHover_NoNameResolved_ShowsNothing()
    {
        var (root, presenter, requests) = CreateWorldHarness(
            () => WorldFoundGuid, nameResolver: _ => null);

        root.Tick(0.016, root.TooltipDelayMs + 50);
        presenter.Tick();
        presenter.Tick();

        Assert.Empty(requests);
    }

    [Fact]
    public void WorldHover_SuppressedWhileHoveringAUiElement()
    {
        var (root, presenter, requests) = CreateWorldHarness(() => WorldFoundGuid);
        var uiElement = new HoverTarget { Left = 100, Top = 100, Width = 40, Height = 20 };
        root.AddChild(uiElement);
        root.OnMouseMove(110, 110);

        root.Tick(0.016, root.TooltipDelayMs + 50);
        presenter.Tick();

        Assert.Empty(requests);
    }

    [Fact]
    public void WorldHover_IdleFoundSwap_ReplacesThePopupTheSameFrame_WithoutStacking()
    {
        const uint otherGuid = 0x80000456u;
        uint current = WorldFoundGuid;
        var (root, presenter, requests) = CreateWorldHarness(
            () => current,
            nameResolver: guid => guid == WorldFoundGuid ? "A Drudge" : "A Door");
        int childrenBefore = root.Children.Count;

        root.Tick(0.016, root.TooltipDelayMs);
        presenter.Tick();
        Assert.Equal(childrenBefore + 1, root.Children.Count);

        current = otherGuid;
        presenter.Tick();   // same frame: teardown + idle remount

        Assert.Equal(childrenBefore + 1, root.Children.Count);
        Assert.Equal(2, requests.Count);

        current = WorldFoundGuid;
        presenter.Tick();
        current = otherGuid;
        presenter.Tick();
        current = WorldFoundGuid;
        presenter.Tick();

        // Several more A/B/A swaps still leave exactly one popup mounted —
        // this is the "dozens of stacked name boxes" scenario, minus the bug.
        Assert.Equal(childrenBefore + 1, root.Children.Count);
    }

    [Fact]
    public void WorldHover_AutoHidesAfterTheDuration_AndRemountsOnlyAfterAMouseMovePlusDwell()
    {
        var (root, presenter, requests) = CreateWorldHarness(() => WorldFoundGuid);
        int childrenBefore = root.Children.Count;

        root.Tick(0.016, root.TooltipDelayMs);
        presenter.Tick();
        Assert.Equal(childrenBefore + 1, root.Children.Count);

        long expiry = root.TooltipDelayMs + root.TooltipDurationMs;
        root.Tick(0.016, expiry);
        presenter.Tick();
        Assert.Equal(childrenBefore, root.Children.Count);   // auto-hidden

        root.Tick(0.016, expiry + 500);
        presenter.Tick();
        Assert.Equal(childrenBefore, root.Children.Count);   // idle but latched — no flicker remount
        Assert.Single(requests);

        long moveAt = expiry + 600;
        root.Tick(0.016, moveAt);
        root.OnMouseMove(5, 5);   // re-enter; dwell restarts from this move
        presenter.Tick();
        Assert.Single(requests);  // dwell not yet elapsed

        root.Tick(0.016, moveAt + root.TooltipDelayMs);
        presenter.Tick();
        Assert.Equal(2, requests.Count);
        Assert.Equal(childrenBefore + 1, root.Children.Count);
    }

    [Fact]
    public void WorldHover_DragInProgress_MountsImmediatelyOnTheFoundEdge_NoDwell()
    {
        uint? found = null;
        var (root, presenter, requests) = CreateWorldHarness(() => found);
        var source = new DragSourceTarget { Left = 100, Top = 100, Width = 40, Height = 20 };
        root.AddChild(source);
        int childrenBefore = root.Children.Count;

        root.Tick(0.016, 0);
        root.OnMouseDown(UiMouseButton.Left, 110, 110);
        root.OnMouseMove(130, 130);   // beyond the 3px threshold -> BeginDrag
        Assert.NotNull(root.DragSource);
        root.OnMouseMove(300, 300);   // over the world, mid-drag, mouse JUST moved

        found = WorldFoundGuid;
        presenter.Tick();             // the found edge, zero idle time

        Assert.Single(requests);
        Assert.Equal(childrenBefore + 1, root.Children.Count);
    }

    [Fact]
    public void WorldHover_GlobalEnableOff_SuppressesTheDwellMount()
    {
        var (root, presenter, requests) = CreateWorldHarness(() => WorldFoundGuid);
        presenter.Enabled = false;

        root.Tick(0.016, root.TooltipDelayMs + 50);
        presenter.Tick();
        presenter.Tick();

        Assert.Empty(requests);
    }

    [Fact]
    public void WorldHover_ThenUiDwellTooltip_ReplacesRatherThanStacks()
    {
        var (root, presenter, _) = CreateWorldHarness(() => WorldFoundGuid);
        int childrenBefore = root.Children.Count;

        root.Tick(0.016, root.TooltipDelayMs);
        presenter.Tick();
        Assert.Equal(childrenBefore + 1, root.Children.Count); // world tooltip up

        var target = AddFullyAuthoredTarget(root);
        long moveAt = root.TooltipDelayMs + 10;
        root.Tick(0.016, moveAt);
        root.OnMouseMove(110, 110);
        root.Tick(0.016, moveAt + root.TooltipDelayMs);

        UiElement popup = root.Children.Single(c => !ReferenceEquals(c, target));
        Assert.NotNull(popup);
        Assert.Equal(childrenBefore + 2, root.Children.Count);

        presenter.Tick();
        Assert.Equal(childrenBefore + 2, root.Children.Count);
        Assert.Same(popup, root.Children.Single(c => !ReferenceEquals(c, target)));
    }

    [Fact]
    public void UiDwellTooltip_ThenModalStealsHitTesting_WorldHoverReplacesRatherThanStacks()
    {
        var (root, presenter, _) = CreateHarness();
        var target = AddFullyAuthoredTarget(root);
        int childrenBefore = root.Children.Count;

        root.OnMouseMove(110, 110);
        root.Tick(0.016, 0);
        root.Tick(0.016, root.TooltipDelayMs);
        Assert.Equal(childrenBefore + 1, root.Children.Count); // UI tooltip up

        // Modal opens elsewhere on screen, stealing exclusive hit-testing —
        // the mouse never moves.
        root.Modal = new UiPanel { Left = 0, Top = 0, Width = 10, Height = 10 };
        presenter.WorldHoverGuidProvider = () => WorldFoundGuid;
        presenter.WorldHoverNameResolver = _ => "A Drudge";
        presenter.WorldTooltipsEnabled = () => true;

        presenter.Tick();

        // Exactly one popup (the world one, having replaced the UI one) —
        // never two stacked.
        Assert.Equal(childrenBefore + 1, root.Children.Count);
        UiElement popup = root.Children.Single(c => !ReferenceEquals(c, target));
        Assert.NotNull(popup);
    }

    [Fact]
    public void WorldHover_ReEvaluatesGateAndTextOnlyOnTheFoundGuidEdge()
    {
        int gateReads = 0, nameReads = 0;
        var (root, presenter, requests) = CreateWorldHarness(
            () => WorldFoundGuid,
            nameResolver: _ => { nameReads++; return "A Drudge"; },
            enabled: () => { gateReads++; return true; });

        root.Tick(0.016, root.TooltipDelayMs);
        presenter.Tick();
        presenter.Tick();
        presenter.Tick();

        Assert.Equal(1, gateReads);
        Assert.Equal(1, nameReads);
        Assert.Single(requests);
    }

    [Fact]
    public void WorldHover_TextIsPlainAppropriateName_NoStackCountPrefix()
    {
        var (root, presenter, _) = CreateWorldHarness(
            () => WorldFoundGuid, nameResolver: _ => "Iron Bars");

        root.Tick(0.016, root.TooltipDelayMs);
        presenter.Tick();

        UiElement popup = Assert.Single(root.Children);
        UiElement? textChild = FindById(popup, TextChildId);
        Assert.IsType<UiText>(textChild);
    }
}
