using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public class DatWidgetFactoryFontResolveTests
{
    private static (uint, int, int) NoTex(uint _) => (0, 0, 0);


    /// <summary>
    /// When fontResolve is null (the live GameWindow path), BuildText must NOT
    /// touch DatFont beyond setting it to the global datFont.  Null in → null out.
    /// </summary>
    [Fact]
    public void NullFontResolve_DatFont_EqualsGlobalFont()
    {
        var info = new ElementInfo { Type = 12, Width = 100, Height = 20, FontDid = 0x40000001u };
        var t = Assert.IsType<UiText>(DatWidgetFactory.Create(info, NoTex, null, fontResolve: null));
        Assert.Null(t.DatFont);
    }


    /// <summary>
    /// When the element has FontDid == 0, the fontResolve delegate must NOT be
    /// invoked (the element has no dat font; fall through to global datFont).
    /// </summary>
    [Fact]
    public void FontDidZero_ResolverNotCalled()
    {
        var info = new ElementInfo { Type = 12, Width = 100, Height = 20, FontDid = 0u };
        bool called = false;
        UiDatFont? Resolver(uint id) { called = true; return null; }

        // FontDid=0 → resolver should never fire, even though one is provided.
        Assert.IsType<UiText>(DatWidgetFactory.Create(info, NoTex, null, fontResolve: Resolver));
        Assert.False(called, "fontResolve must NOT be called when FontDid == 0");
    }

    // ── Test 3: resolver returns null (font missing) → fallback to datFont ───

    [Fact]
    public void ResolverReturnsNull_FallsBackToGlobalFont()
    {
        const uint SomeFontDid = 0x40000005u;
        var info = new ElementInfo { Type = 12, Width = 100, Height = 20, FontDid = SomeFontDid };
        int callCount = 0;
        UiDatFont? Resolver(uint id) { callCount++; return null; }   // always returns null (font missing)

        var t = Assert.IsType<UiText>(DatWidgetFactory.Create(info, NoTex, datFont: null, fontResolve: Resolver));

        Assert.Equal(1, callCount);
        // Fallback: DatFont == global datFont (null in unit tests).
        Assert.Null(t.DatFont);
    }


    [Fact]
    public void ResolverCalledWithElementFontDid()
    {
        const uint ExpectedFontDid = 0x40000002u;
        var info = new ElementInfo { Type = 12, Width = 100, Height = 20, FontDid = ExpectedFontDid };
        uint? capturedId = null;
        UiDatFont? Resolver(uint id) { capturedId = id; return null; }

        DatWidgetFactory.Create(info, NoTex, datFont: null, fontResolve: Resolver);

        Assert.Equal(ExpectedFontDid, capturedId);
    }


    [Fact]
    public void ControllerDatFontOverride_WinsOverBuildTimeFont()
    {
        const uint SomeFontDid = 0x40000003u;
        var info = new ElementInfo { Type = 12, Width = 100, Height = 20, FontDid = SomeFontDid };
        UiDatFont? Resolver(uint _) => null;   // returns null → build-time DatFont == null

        var t = Assert.IsType<UiText>(DatWidgetFactory.Create(info, NoTex, datFont: null, fontResolve: Resolver));
        Assert.Null(t.DatFont);   // build-time value set by factory

        t.DatFont = null;
        Assert.Null(t.DatFont);
    }

    // ── Test 6: LayoutImporter.Build threads fontResolve to factory ──────────

    [Fact]
    public void LayoutImporter_Build_ThreadsFontResolve_ToFactory()
    {
        const uint FontDid = 0x40000004u;
        var root = new ElementInfo { Id = 1u, Type = 3, Width = 200, Height = 100 };
        var child = new ElementInfo { Id = 2u, Type = 12, Width = 100, Height = 20, FontDid = FontDid };
        root.Children.Add(child);

        int resolveCallCount = 0;
        UiDatFont? Resolver(uint id) { resolveCallCount++; return null; }

        var layout = LayoutImporter.Build(root, NoTex, datFont: null, fontResolve: Resolver);

        Assert.True(resolveCallCount > 0, "fontResolve was not called for the child element");
        Assert.NotNull(layout.FindElement(2u));
    }

    // ── Test 7: Meter element also receives element font from resolver ────────

    [Fact]
    public void FontResolve_CalledForMeter_WhenFontDidPresent()
    {
        const uint MeterFontDid = 0x40000006u;
        var meter = new ElementInfo { Type = 7, Width = 150, Height = 16, FontDid = MeterFontDid };
        int callCount = 0;
        UiDatFont? Resolver(uint id) { callCount++; return null; }

        var w = DatWidgetFactory.Create(meter, NoTex, datFont: null, fontResolve: Resolver);

        var m = Assert.IsType<UiMeter>(w);
        Assert.True(callCount > 0, "fontResolve was not called for meter element");
        // Meter DatFont: resolver returned null, so DatFont falls back to global datFont (null).
        Assert.Null(m.DatFont);
    }


    [Fact]
    public void BuildFromInfos_WithoutFontResolve_WorksAsBeforeFix()
    {
        var root  = new ElementInfo { Id = 10u, Type = 3, Width = 200, Height = 100 };
        var child = new ElementInfo { Id = 11u, Type = 12, Width = 100, Height = 20, FontDid = 0x40000001u };

        var layout = LayoutImporter.BuildFromInfos(root, new[] { child }, NoTex, datFont: null);

        var t = Assert.IsType<UiText>(layout.FindElement(11u));
        // No resolver → DatFont == global datFont (null in unit tests).
        Assert.Null(t.DatFont);
    }
}
