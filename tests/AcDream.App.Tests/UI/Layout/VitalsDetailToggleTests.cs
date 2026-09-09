using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Category", "Conformance")]
public class VitalsDetailToggleTests
{
    private const uint DragBarTop = 0x1000063Cu;   // Type 2 — WindowMoveHandle
    private const uint GripTopLeft = 0x1000063Bu;  // Type 9 — UiResizeGrip

    // ── Import shape ─────────────────────────────────────────────────────────

    [Fact]
    public void VitalsTree_RootIsVitalsRootWidget()
    {
        var layout = FixtureLoader.LoadVitals();
        Assert.IsType<UiVitalsRoot>(layout.Root);
    }

    [Fact]
    public void VitalsTree_MetersAbsorbDetailIconOverlays()
    {
        var layout = FixtureLoader.LoadVitals();

        (uint MeterId, uint Back, uint Front)[] cases =
        [
            (VitalsController.Health,  0x06007490u, 0x06007491u), // heart
            (VitalsController.Stamina, 0x06007492u, 0x06007493u), // sword
            (VitalsController.Mana,    0x06007494u, 0x06007495u), // scepter
        ];

        foreach (var (meterId, back, front) in cases)
        {
            var m = Assert.IsType<UiMeter>(layout.FindElement(meterId));
            Assert.True(m.HasDetailOverlay);
            Assert.Equal(back, m.DetailBackSprite);
            Assert.Equal(front, m.DetailFrontSprite);
        }

        // Health's authored overlay rect: 18x16 at x=66 (the heart).
        var health = Assert.IsType<UiMeter>(layout.FindElement(VitalsController.Health));
        Assert.Equal((66f, 0f, 18f, 16f), health.DetailBackRect);
    }

    [Fact]
    public void VitalsTree_OverlaysCarryTheAuthoredCenterAnchors()
    {
        var layout = FixtureLoader.LoadVitals();
        foreach (uint meterId in new[]
        {
            VitalsController.Health,
            VitalsController.Stamina,
            VitalsController.Mana,
        })
        {
            var m = Assert.IsType<UiMeter>(layout.FindElement(meterId));
            foreach (UiMeterDetailOverlaySpec overlay in new[] { m.DetailBack, m.DetailFront })
            {
                Assert.Equal(3u, overlay.LeftMode);
                Assert.Equal(1u, overlay.TopMode);
                Assert.Equal(3u, overlay.RightMode);
                Assert.Equal(1u, overlay.BottomMode);
                Assert.Equal(150f, overlay.ParentW);
                Assert.Equal(16f, overlay.ParentH);
            }
        }
    }


    [Fact]
    public void DetailIcon_AuthoredRectStandsAtTheAuthoredMeterSize()
    {
        var sword = new UiMeterDetailOverlaySpec(
            0x06007492u, 32f, 0f, 85f, 16f, 3u, 1u, 3u, 1u, 150f, 16f);
        Assert.Equal(
            (32f, 0f, 85f, 16f),
            UiMeter.ComputeDetailOverlayRect(in sword, 150f, 16f));
    }

    [Theory]
    // Heart 18x16: near = W/2 - 9, far = W/2 + 9 - 1 -> width preserved.
    [InlineData(66f, 18f, 300f, 141f, 18f)]
    [InlineData(66f, 18f, 220f, 101f, 18f)]
    // Scepter 100x16: near = W/2 - 50.
    [InlineData(25f, 100f, 300f, 100f, 100f)]
    public void DetailIcon_CentersOnAWiderMeter(
        float authoredX, float authoredW, float meterW,
        float expectedX, float expectedW)
    {
        var overlay = new UiMeterDetailOverlaySpec(
            0x06007490u, authoredX, 0f, authoredW, 16f, 3u, 1u, 3u, 1u, 150f, 16f);
        (float x, float y, float w, float h) =
            UiMeter.ComputeDetailOverlayRect(in overlay, meterW, 16f);
        Assert.Equal(expectedX, x);
        Assert.Equal(0f, y);
        Assert.Equal(expectedW, w);
        Assert.Equal(16f, h);
    }

    [Fact]
    public void DetailIcon_OddWidthLosesOnePixelExactlyLikeRetail()
    {
        var sword = new UiMeterDetailOverlaySpec(
            0x06007492u, 32f, 0f, 85f, 16f, 3u, 1u, 3u, 1u, 150f, 16f);
        (float x, float _, float w, float _) =
            UiMeter.ComputeDetailOverlayRect(in sword, 300f, 16f);
        Assert.Equal(108f, x);
        Assert.Equal(84f, w);
    }

    // ── The press toggle ─────────────────────────────────────────────────────

    [Fact]
    public void Press_TogglesUndefThenHideDetailThenShowDetail()
    {
        var layout = FixtureLoader.LoadVitals();
        var root = Assert.IsType<UiVitalsRoot>(layout.Root);
        var stamina = Assert.IsType<UiMeter>(layout.FindElement(VitalsController.Stamina));
        var staminaText = Assert.IsType<UiText>(layout.FindElement(VitalsController.StaminaText));

        Assert.True(staminaText.Visible);

        Press(root, root);
        Assert.Equal(RetailUiStateIds.HideDetail, root.ActiveRetailStateId);
        Assert.Equal(RetailUiStateIds.HideDetail, stamina.ActiveRetailStateId);
        Assert.True(staminaText.Visible);

        // Press 2: ShowDetail — the graphical mode. Numbers hidden, icons on.
        Press(root, root);
        Assert.Equal(RetailUiStateIds.ShowDetail, root.ActiveRetailStateId);
        Assert.Equal(RetailUiStateIds.ShowDetail, stamina.ActiveRetailStateId);
        Assert.False(staminaText.Visible);

        // Press 3: back to numeric.
        Press(root, root);
        Assert.Equal(RetailUiStateIds.HideDetail, root.ActiveRetailStateId);
        Assert.True(staminaText.Visible);
    }

    [Fact]
    public void RightPress_TogglesLikeLeftPress()
    {
        var layout = FixtureLoader.LoadVitals();
        var root = Assert.IsType<UiVitalsRoot>(layout.Root);

        Press(root, root, UiEventType.RightDown);
        Assert.Equal(RetailUiStateIds.HideDetail, root.ActiveRetailStateId);
        Press(root, root, UiEventType.RightDown);
        Assert.Equal(RetailUiStateIds.ShowDetail, root.ActiveRetailStateId);
    }

    [Fact]
    public void Press_CascadesToAllThreeLabels()
    {
        var layout = FixtureLoader.LoadVitals();
        var root = Assert.IsType<UiVitalsRoot>(layout.Root);

        Press(root, root); // Undef → HideDetail
        Press(root, root); // HideDetail → ShowDetail
        foreach (uint textId in new[]
        {
            VitalsController.HealthText,
            VitalsController.StaminaText,
            VitalsController.ManaText,
        })
        {
            var text = Assert.IsType<UiText>(layout.FindElement(textId));
            Assert.False(text.Visible);
        }
    }


    [Fact]
    public void Press_OnDragBar_DoesNotToggle()
    {
        var layout = FixtureLoader.LoadVitals();
        var root = Assert.IsType<UiVitalsRoot>(layout.Root);
        var dragBar = layout.FindElement(DragBarTop);
        Assert.NotNull(dragBar);
        Assert.True(dragBar!.WindowMoveHandle);

        uint before = root.ActiveRetailStateId;
        Press(root, dragBar);
        Assert.Equal(before, root.ActiveRetailStateId);
    }

    [Fact]
    public void Press_OnResizeGrip_DoesNotToggle()
    {
        var layout = FixtureLoader.LoadVitals();
        var root = Assert.IsType<UiVitalsRoot>(layout.Root);
        var grip = layout.FindElement(GripTopLeft);
        Assert.NotNull(grip);
        Assert.IsType<UiResizeGrip>(grip);

        uint before = root.ActiveRetailStateId;
        Press(root, grip!);
        Assert.Equal(before, root.ActiveRetailStateId);
    }

    // ── Independence: each window keeps its own state ─────────────────────────

    [Fact]
    public void TwoWindows_ToggleIndependently()
    {
        var a = FixtureLoader.LoadVitals();
        var b = FixtureLoader.LoadVitals();
        var rootA = Assert.IsType<UiVitalsRoot>(a.Root);
        var rootB = Assert.IsType<UiVitalsRoot>(b.Root);

        Press(rootA, rootA);
        Press(rootA, rootA);
        Assert.Equal(RetailUiStateIds.ShowDetail, rootA.ActiveRetailStateId);
        Assert.NotEqual(RetailUiStateIds.ShowDetail, rootB.ActiveRetailStateId);
    }

    // ── End-to-end: the REAL mouse path (hit test + bubble) ──────────────────

    [Fact]
    public void MouseDown_OnWindowBody_TogglesThroughTheRealHitPath()
    {
        var (root, vitalsRoot) = MountedWindow();

        root.OnMouseDown(UiMouseButton.Left, 90, 59);
        root.OnMouseUp(UiMouseButton.Left, 90, 59);
        Assert.Equal(RetailUiStateIds.HideDetail, vitalsRoot.ActiveRetailStateId);

        root.OnMouseDown(UiMouseButton.Right, 90, 59);
        root.OnMouseUp(UiMouseButton.Right, 90, 59);
        Assert.Equal(RetailUiStateIds.ShowDetail, vitalsRoot.ActiveRetailStateId);
    }

    [Fact]
    public void MouseDown_OnTopDragBar_MovesArmWithoutToggling()
    {
        var (root, vitalsRoot) = MountedWindow();

        root.OnMouseDown(UiMouseButton.Left, 90, 32);
        root.OnMouseUp(UiMouseButton.Left, 90, 32);
        Assert.NotEqual(RetailUiStateIds.HideDetail, vitalsRoot.ActiveRetailStateId);
        Assert.NotEqual(RetailUiStateIds.ShowDetail, vitalsRoot.ActiveRetailStateId);
    }

    private static (UiRoot Root, UiVitalsRoot VitalsRoot) MountedWindow()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var layout = FixtureLoader.LoadVitals();
        RetailWindowFrame.Mount(root, layout.Root, static _ => (0u, 0, 0),
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Vitals,
                Chrome = RetailWindowChrome.Imported,
                Left = 10f,
                Top = 30f,
                ResizeX = true,
                ResizeY = false,
                MinWidth = 40f,
                ContentClickThrough = false,
            });
        return (root, Assert.IsType<UiVitalsRoot>(layout.Root));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void Press(UiVitalsRoot root, UiElement target,
        int type = UiEventType.MouseDown)
        => root.OnEvent(new UiEvent(target.EventId, target, type));
}
