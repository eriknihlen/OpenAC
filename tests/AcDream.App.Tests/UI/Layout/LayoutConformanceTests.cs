using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Category", "Conformance")]
public class LayoutConformanceTests
{
    // ── Test 1: Three meters at expected rects ────────────────────────────────

    /// <summary>
    /// The three vital bars must be UiMeters positioned at x=5, width=150, height=16,
    /// at y=5 (health), y=21 (stamina), y=37 (mana).
    /// </summary>
    [Fact]
    public void VitalsTree_HasThreeMetersAtExpectedRects()
    {
        var layout = FixtureLoader.LoadVitals();

        (uint Id, float Y)[] expected =
        [
            (0x100000E6u, 5f),   // health
            (0x100000ECu, 21f),  // stamina
            (0x100000EEu, 37f),  // mana
        ];

        foreach (var (id, y) in expected)
        {
            var elem = layout.FindElement(id);
            Assert.NotNull(elem);
            var meter = Assert.IsType<UiMeter>(elem);
            Assert.Equal(5f,   meter.Left);
            Assert.Equal(y,    meter.Top);
            Assert.Equal(150f, meter.Width);
            Assert.Equal(16f,  meter.Height);
        }
    }


    [Fact]
    public void VitalsTree_MetersHaveExpectedSliceIds()
    {
        var layout = FixtureLoader.LoadVitals();

        (uint MeterId, uint[] Slices)[] cases =
        [
            (0x100000E6u, [0x0600747Eu, 0x0600747Fu, 0x06007480u, 0x06007481u, 0x06007482u, 0x06007483u]), // health
            (0x100000ECu, [0x06007484u, 0x06007485u, 0x06007486u, 0x06007487u, 0x06007488u, 0x06007489u]), // stamina
            (0x100000EEu, [0x0600748Au, 0x0600748Bu, 0x0600748Cu, 0x0600748Du, 0x0600748Eu, 0x0600748Fu]), // mana
        ];

        foreach (var (meterId, s) in cases)
        {
            var m = Assert.IsType<UiMeter>(layout.FindElement(meterId));
            Assert.Equal(s[0], m.BackLeft);   Assert.Equal(s[1], m.BackTile);   Assert.Equal(s[2], m.BackRight);
            Assert.Equal(s[3], m.FrontLeft);  Assert.Equal(s[4], m.FrontTile);  Assert.Equal(s[5], m.FrontRight);
        }
    }


    [Fact]
    public void VitalsTree_ChromeCornerHasExpectedSprite()
    {
        var layout = FixtureLoader.LoadVitals();

        var elem = layout.FindElement(0x10000633u);
        Assert.NotNull(elem);
        var datElem = Assert.IsType<UiDatElement>(elem);
        var (file, _) = datElem.ActiveMedia();
        Assert.Equal(0x060074C3u, file);
    }

    // ── Test 4 (N4): Inheritance resolution — FontDid propagated from base ───

    [Fact]
    public void VitalsTree_TextLabel_InheritsFontDidFromBaseLayout()
    {
        var root = FixtureLoader.LoadVitalsInfos();

        var fontDids = new System.Collections.Generic.List<uint>();
        CollectFontDids(root, fontDids);

        Assert.Contains(0x40000000u, fontDids);
    }

    [Fact]
    public void VitalsBinding_ReusesAuthoredTextNodesWithoutDuplicates()
    {
        var layout = FixtureLoader.LoadVitals();
        VitalsController.Bind(
            layout,
            () => 1f,
            () => 1f,
            () => 1f,
            () => "100/100",
            () => "90/100",
            () => "80/100");

        (uint MeterId, uint TextId)[] cases =
        [
            (VitalsController.Health, VitalsController.HealthText),
            (VitalsController.Stamina, VitalsController.StaminaText),
            (VitalsController.Mana, VitalsController.ManaText),
        ];

        foreach (var (meterId, textId) in cases)
        {
            var meter = Assert.IsType<UiMeter>(layout.FindElement(meterId));
            var text = Assert.IsType<UiText>(layout.FindElement(textId));
            Assert.Same(meter, text.Parent);
            Assert.Single(meter.Children, child => child == text);
            Assert.False(text.Selectable);
        }
    }

    private static void CollectFontDids(ElementInfo node, System.Collections.Generic.List<uint> acc)
    {
        if (node.FontDid != 0) acc.Add(node.FontDid);
        foreach (var child in node.Children)
            CollectFontDids(child, acc);
    }


    [Fact]
    public void HorizontalResize_160to200_ReflowsCorrectly()
    {
        const float designParentW = 160f;
        const float newParentW    = 200f;
        const float parentH       = 58f;

        // (piece, designX, designW, LeftEdge, RightEdge, expectedX, expectedW)
        (string Piece, float DesignX, float DesignW, uint L, uint R, float ExpX, float ExpW)[] cases =
        [
            ("TL corner", 0f,   5f,   1u, 2u, 0f,   5f  ),
            ("top edge",  5f,   150f, 1u, 1u, 5f,   190f),
            ("TR corner", 155f, 5f,   2u, 1u, 195f, 5f  ),
            ("meter",     5f,   150f, 1u, 1u, 5f,   190f),
        ];

        foreach (var (piece, dX, dW, l, r, expX, expW) in cases)
        {
            // T/B values don't affect x/w; use real vitals values (top=1, bottom=2)
            var anchors = ElementReader.ToAnchors(l, top: 1u, r, bottom: 2u);

            // Margins from the design rect at parentW=160
            float mL = dX;
            float mR = designParentW - (dX + dW);

            // Reflow at parentW=200 (parentH irrelevant for x/w assertions)
            var (x, _, w, _) = UiElement.ComputeAnchoredRect(
                anchors, mL, mT: 0f, mR, mB: 0f, w0: dW, h0: 5f, parentW: newParentW, parentH);

            // xUnit 2.x Assert.Equal(float,float,int) = decimal-place precision
            Assert.True(Math.Abs(x - expX) < 0.5f, $"{piece}: expected x={expX} got {x}");
            Assert.True(Math.Abs(w - expW) < 0.5f, $"{piece}: expected w={expW} got {w}");
        }
    }
}
