using System.Linq;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public class MarkupDocumentTests
{
    private sealed class EditorBinding
    {
        public string Draft { get; private set; } = "initial";
        public string Submitted { get; private set; } = string.Empty;
        public string Selected { get; private set; } = "First";
        public int SelectedIndex { get; private set; }
        public IReadOnlyList<string> Choices => ["First", "Second"];
        public IReadOnlyList<uint> ChoiceColors => [0xFF0000u, 0x00FF00u];
        public Action<string> ChangeDraft => value => Draft = value;
        public Action<string> SubmitDraft => value => Submitted = value;
        public Action<string> SelectChoice => value => Selected = value;
        public Action<int> SelectIndex => value => SelectedIndex = value;
    }

    [Fact]
    public void FieldAndMenuBindEditablePluginState()
    {
        const string xml = """
            <panel x="0" y="0" w="240" h="120">
              <field x="4" y="4" w="120" h="20" text="{Draft}"
                     onchange="{ChangeDraft}" onsubmit="{SubmitDraft}" />
              <menu x="4" y="32" w="120" h="20" items="{Choices}"
                    selected="{Selected}" onchange="{SelectChoice}" />
              <list x="132" y="4" w="100" h="40" items="{Choices}"
                    colors="{ChoiceColors}"
                    selected="{SelectedIndex}" onchange="{SelectIndex}" />
            </panel>
            """;
        var binding = new EditorBinding();

        UiNineSlicePanel panel = MarkupDocument.Build(
            xml,
            binding,
            _ => (1u, 32, 32));

        UiField field = Assert.IsType<UiField>(panel.Children[0]);
        UiMenu menu = Assert.IsType<UiMenu>(panel.Children[1]);
        UiMarkupList list = Assert.IsType<UiMarkupList>(panel.Children[2]);
        field.SetText("named profile");
        field.OnSubmit?.Invoke(field.Text);
        menu.OnSelect?.Invoke("Second");
        list.OnEvent(new UiEvent
        {
            Type = UiEventType.MouseDown,
            Data2 = 19,
        });

        Assert.Equal("named profile", binding.Draft);
        Assert.Equal("named profile", binding.Submitted);
        Assert.Equal("Second", binding.Selected);
        Assert.Equal(1, binding.SelectedIndex);
        Assert.Equal(2, menu.Items.Count);
        Assert.Equal([0xFF0000u, 0x00FF00u], list.ItemColorsSource());
        Assert.False(menu.OpenUpward);
    }

    [Fact]
    public void ControlIdAndNameBecomeStablePluginControlNames()
    {
        const string xml = """
            <panel x="0" y="0" w="200" h="80">
              <button id="ById" x="4" y="4" w="80" h="20" text="One" />
              <label name="ByName" x="4" y="28" text="Two" />
            </panel>
            """;

        UiNineSlicePanel panel = MarkupDocument.Build(
            xml, new object(), _ => (1u, 32, 32));

        Assert.Equal("ById", panel.Children[0].Name);
        Assert.Equal("ByName", panel.Children[1].Name);
    }

    private sealed class FakeBinding
    {
        public float HealthPercent => 0.5f;
        public uint? HealthCurrent => 109;
        public uint? HealthMax => 218;
        public float? ManaPercent => null;
        public uint? ManaCurrent => null;
        public uint? ManaMax => null;
    }

    [Fact]
    public void Build_CreatesPanelWithMeterFillLabelAndGeometry()
    {
        const string xml =
            "<panel id=\"acdream.vitals\" x=\"10\" y=\"30\" w=\"220\" h=\"96\" title=\"Vitals\">" +
            "  <meter id=\"health\" x=\"8\" y=\"24\" w=\"200\" h=\"14\" fill=\"{HealthPercent}\" cur=\"{HealthCurrent}\" max=\"{HealthMax}\" color=\"#FFFF0000\"/>" +
            "</panel>";

        var panel = MarkupDocument.Build(xml, new FakeBinding(), _ => ((uint)1, 32, 32));

        Assert.IsType<UiNineSlicePanel>(panel);
        Assert.Equal(10f, panel.Left);
        Assert.Equal(220f, panel.Width);
        Assert.Equal(2, panel.Children.Count);            // title UiLabel + 1 meter
        var meter = Assert.IsType<UiMeter>(panel.Children[1]);
        Assert.Equal(8f, meter.Left);
        Assert.Equal(200f, meter.Width);
        Assert.Equal(0.5f, meter.Fill());
        Assert.Equal("109/218", meter.Label());
    }

    [Fact]
    public void Build_NullBindingValuesYieldNullFillAndLabel()
    {
        const string xml =
            "<panel id=\"v\" x=\"0\" y=\"0\" w=\"10\" h=\"10\" title=\"V\">" +
            "  <meter id=\"mana\" x=\"0\" y=\"0\" w=\"10\" h=\"2\" fill=\"{ManaPercent}\" cur=\"{ManaCurrent}\" max=\"{ManaMax}\" color=\"#FF0000FF\"/>" +
            "</panel>";
        var panel = MarkupDocument.Build(xml, new FakeBinding(), _ => ((uint)1, 32, 32));
        var meter = Assert.IsType<UiMeter>(panel.Children[1]);
        Assert.Null(meter.Fill());
        Assert.Null(meter.Label());
    }

    [Fact]
    public void Build_ResizeAttrX_SetsHorizontalOnly()
    {
        const string xml = "<panel id=\"v\" x=\"0\" y=\"0\" w=\"100\" h=\"50\" title=\"V\" resize=\"x\"></panel>";
        var panel = MarkupDocument.Build(xml, new object(), _ => ((uint)1, 32, 32));
        Assert.True(panel.ResizeX);
        Assert.False(panel.ResizeY);
    }

    [Fact]
    public void Build_ParsesNineSliceBarSpriteIds()
    {
        const string xml = "<panel id=\"v\" x=\"0\" y=\"0\" w=\"100\" h=\"50\" title=\"V\">" +
            "<meter id=\"h\" x=\"0\" y=\"0\" w=\"100\" h=\"14\" fill=\"{HealthPercent}\" " +
            "backleft=\"0x06001141\" backtile=\"0x06001140\" backright=\"0x0600113F\" " +
            "frontleft=\"0x06001131\" fronttile=\"0x06001132\" frontright=\"0x06001133\"/>" +
            "</panel>";
        var panel = MarkupDocument.Build(xml, new FakeBinding(), _ => ((uint)7, 32, 32));
        var meter = Assert.IsType<UiMeter>(panel.Children[1]);
        Assert.Equal(0x06001141u, meter.BackLeft);
        Assert.Equal(0x06001140u, meter.BackTile);
        Assert.Equal(0x0600113Fu, meter.BackRight);
        Assert.Equal(0x06001131u, meter.FrontLeft);
        Assert.Equal(0x06001132u, meter.FrontTile);
        Assert.Equal(0x06001133u, meter.FrontRight);
        Assert.NotNull(meter.SpriteResolve);
    }

    private sealed class ButtonBinding
    {
        public int Clicks { get; private set; }
        public string Status { get; set; } = "idle";
        public Action Go => () => Clicks++;
        public Action? Missing => null;
        public bool CanGo { get; set; } = true;
        public bool OptionsSelected { get; set; } = true;
        public bool OptionsVisible { get; set; } = true;
        public bool CombatEnabled { get; set; }
        public float AttackPower { get; private set; } = 0.5f;
        public Action ShowOptions => () => OptionsSelected = true;
        public Action ToggleCombat => () => CombatEnabled = !CombatEnabled;
        public Action<float> SetAttackPower => value => AttackPower = value;
    }

    [Fact]
    public void Build_ButtonInvokesTheBoundActionOnClick()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "  <button x=\"4\" y=\"8\" w=\"60\" h=\"20\" text=\"Buff\" onclick=\"{Go}\"/>" +
            "</panel>";

        var binding = new ButtonBinding();
        var panel = MarkupDocument.Build(xml, binding, _ => ((uint)1, 32, 32));

        var button = Assert.IsType<UiSimpleButton>(panel.Children[0]);
        Assert.Equal("Buff", button.Text);
        Assert.Equal(60f, button.Width);

        button.OnEvent(new UiEvent { Type = UiEventType.Click });
        button.OnEvent(new UiEvent { Type = UiEventType.Click });
        Assert.Equal(2, binding.Clicks);
    }

    [Fact]
    public void Build_ButtonWithUnresolvableHandlerFailsLoudly()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "  <button x=\"0\" y=\"0\" w=\"10\" h=\"10\" text=\"X\" onclick=\"{NoSuchProperty}\"/>" +
            "</panel>";

        Assert.Throws<FormatException>(
            () => MarkupDocument.Build(xml, new ButtonBinding(), _ => ((uint)1, 32, 32)));
    }

    [Fact]
    public void Build_BoundLabelTracksTheBindingRatherThanFreezing()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "  <label x=\"2\" y=\"4\" text=\"{Status}\"/>" +
            "</panel>";

        var binding = new ButtonBinding();
        var panel = MarkupDocument.Build(xml, binding, _ => ((uint)1, 32, 32));

        var label = Assert.IsType<UiLabel>(panel.Children[0]);
        Assert.Equal("idle", label.TextSource!());

        binding.Status = "casting";
        Assert.Equal("casting", label.TextSource!());
    }

    [Fact]
    public void Build_LiteralLabelTextIsUsedVerbatim()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "  <label x=\"0\" y=\"0\" text=\"MossTank\"/>" +
            "</panel>";

        var panel = MarkupDocument.Build(xml, new ButtonBinding(), _ => ((uint)1, 32, 32));
        var label = Assert.IsType<UiLabel>(panel.Children[0]);
        Assert.Equal("MossTank", label.TextSource!());
    }

    [Fact]
    public void Build_ProjectsBoundEnabledAndButtonColors()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<button x=\"0\" y=\"0\" w=\"50\" h=\"20\" text=\"Go\" " +
            "onclick=\"{Go}\" enabled=\"{CanGo}\" " +
            "background=\"#FF112233\" border=\"#FF445566\"/>" +
            "</panel>";
        var binding = new ButtonBinding();
        UiNineSlicePanel panel = MarkupDocument.Build(
            xml, binding, _ => ((uint)1, 32, 32));
        var button = Assert.IsType<UiSimpleButton>(panel.Children[0]);

        Assert.NotNull(button.EnabledSource);
        Assert.True(button.EnabledSource!());
        binding.CanGo = false;
        Assert.False(button.EnabledSource!());
        Assert.Equal(new System.Numerics.Vector4(
            0x11 / 255f, 0x22 / 255f, 0x33 / 255f, 1f),
            button.BackgroundColor);
        Assert.Equal(new System.Numerics.Vector4(
            0x44 / 255f, 0x55 / 255f, 0x66 / 255f, 1f),
            button.BorderColor);
    }

    [Fact]
    public void Build_RuntimeTooltipUsesRetailPopupLocatorAndLiveBinding()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"100\" h=\"60\">" +
            "<button x=\"0\" y=\"0\" w=\"50\" h=\"20\" text=\"Go\" " +
            "tooltip=\"{Status}\"/>" +
            "</panel>";
        var binding = new ButtonBinding();
        UiNineSlicePanel panel = MarkupDocument.Build(
            xml, binding, _ => ((uint)1, 32, 32));
        var button = Assert.IsType<UiSimpleButton>(panel.Children[0]);

        Assert.Equal("idle", button.GetTooltipText());
        Assert.True(button.AuthoredTooltipEnabled);
        Assert.Equal(0x10000397u, button.AuthoredTooltipRootElementId);
        Assert.Equal(0x21000041u, button.AuthoredTooltipLayoutDid);

        binding.Status = "casting";
        Assert.Equal("casting", button.GetTooltipText());
    }

    [Fact]
    public void Build_NestedGroupTabToggleAndSliderStayLiveAndInteractive()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"300\" h=\"180\">" +
            "<tab x=\"4\" y=\"4\" w=\"60\" h=\"18\" text=\"Options\" " +
            "selected=\"{OptionsSelected}\" onclick=\"{ShowOptions}\"/>" +
            "<group x=\"4\" y=\"28\" w=\"280\" h=\"140\" visible=\"{OptionsVisible}\">" +
            "<toggle x=\"2\" y=\"2\" w=\"140\" h=\"20\" text=\"Enable Combat\" " +
            "checked=\"{CombatEnabled}\" onclick=\"{ToggleCombat}\"/>" +
            "<slider x=\"2\" y=\"30\" w=\"140\" h=\"16\" value=\"{AttackPower}\" " +
            "onchange=\"{SetAttackPower}\"/>" +
            "</group></panel>";
        var binding = new ButtonBinding();

        UiNineSlicePanel panel = MarkupDocument.Build(
            xml,
            binding,
            _ => ((uint)1, 16, 16));

        var tab = Assert.IsType<UiMarkupTabButton>(panel.Children[0]);
        var group = Assert.IsType<UiPanel>(panel.Children[1]);
        var toggle = Assert.IsType<UiMarkupToggle>(group.Children[0]);
        var slider = Assert.IsType<UiScrollbar>(group.Children[1]);
        Assert.True(tab.IsSelected);
        Assert.True(group.VisibleSource!());
        Assert.False(toggle.IsChecked);

        toggle.OnEvent(new UiEvent { Type = UiEventType.Click });
        Assert.True(binding.CombatEnabled);
        Assert.True(toggle.IsChecked);

        slider.ScalarChanged!(0.78f);
        Assert.Equal(0.78f, binding.AttackPower);
        Assert.Equal(0.78f, slider.ScalarPositionSource!());
    }


    private sealed class RangeBinding
    {
        public float Value { get; private set; } = 50f;
        public Action<float> SetValue => value => Value = value;
    }

    [Fact]
    public void Build_SliderWithNoMinMax_KeepsTheHistoricZeroToOneIdentityRange()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"160\" h=\"20\">" +
            "<slider x=\"0\" y=\"0\" w=\"150\" h=\"16\" value=\"{Value}\" " +
            "onchange=\"{SetValue}\"/>" +
            "</panel>";
        var binding = new RangeBinding();

        UiNineSlicePanel panel = MarkupDocument.Build(
            xml, binding, _ => (1u, 16, 16));
        var slider = Assert.IsType<UiScrollbar>(panel.Children[0]);

        Assert.Equal(1f, slider.ScalarPositionSource!());

        slider.ScalarChanged!(0.6f);
        Assert.Equal(0.6f, binding.Value, 3);
    }

    [Fact]
    public void Build_SliderWithMinMax_RescalesTheDeclaredRangeToAndFromTheInternalZeroToOnePosition()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"160\" h=\"20\">" +
            "<slider x=\"0\" y=\"0\" w=\"150\" h=\"16\" min=\"0\" max=\"200\" " +
            "value=\"{Value}\" onchange=\"{SetValue}\"/>" +
            "</panel>";
        var binding = new RangeBinding();

        UiNineSlicePanel panel = MarkupDocument.Build(
            xml, binding, _ => (1u, 16, 16));
        var slider = Assert.IsType<UiScrollbar>(panel.Children[0]);

        Assert.Equal(0.25f, slider.ScalarPositionSource!()!.Value, 3);

        // The reverse direction: an internal drag position of 0.6 (60%) must
        // be rescaled back up into the declared 0-200 range before it ever
        // reaches the plugin's bound Action<float>.
        slider.ScalarChanged!(0.6f);
        Assert.Equal(120f, binding.Value, 3);
    }

    [Theory]
    [InlineData("100", "0")]  // max < min
    [InlineData("50", "50")]  // max == min
    public void Build_SliderWithMaxLessThanOrEqualToMin_Throws(string min, string max)
    {
        string xml =
            "<panel x=\"0\" y=\"0\" w=\"160\" h=\"20\">" +
            $"<slider x=\"0\" y=\"0\" w=\"150\" h=\"16\" min=\"{min}\" max=\"{max}\" " +
            "value=\"{Value}\" onchange=\"{SetValue}\"/>" +
            "</panel>";
        var binding = new RangeBinding();

        Assert.Throws<FormatException>(
            () => MarkupDocument.Build(xml, binding, _ => (1u, 16, 16)));
    }

    [Fact]
    public void Slider_MinMax_DrawsTheThumbAtTheRescaledNormalizedPosition()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"160\" h=\"20\">" +
            "<slider x=\"0\" y=\"0\" w=\"150\" h=\"16\" min=\"0\" max=\"200\" " +
            "value=\"{Value}\" style=\"retail\"/>" +
            "</panel>";
        var binding = new RangeBinding();

        UiNineSlicePanel panel = MarkupDocument.Build(
            xml, binding, id => (id, 16, 16));
        var slider = Assert.IsType<UiScrollbar>(panel.Children[0]);
        Assert.True(slider.RetailArt);

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        var ctx = new UiRenderContext(renderer, new Vector2(800f, 600f));

        // TickSelfAndChildren pulls ScalarPositionSource into ScalarPosition —
        // the same per-frame step the real host performs before drawing.
        slider.TickSelfAndChildren(0);
        Assert.Equal(0.25f, slider.ScalarPosition, 3);

        slider.DrawSelfAndChildren(ctx);

        var thumb = Assert.Single(
            renderer.DebugSpriteSegmentVerts,
            s => s.Texture == RetailScrollbarChrome.HThumbMidNormal);
        float thumbMinX = Enumerable.Range(0, thumb.Verts.Count / 8)
            .Min(i => thumb.Verts[i * 8]);
        Assert.True(
            thumbMinX > 0f,
            $"expected the thumb offset right of the origin at 25%, got x={thumbMinX}");
    }

    [Fact]
    public void Slider_NoStyleAttribute_DrawsAPlainFlatNubAtTheRescaledNormalizedPosition()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"160\" h=\"20\">" +
            "<slider x=\"0\" y=\"0\" w=\"150\" h=\"16\" min=\"0\" max=\"200\" " +
            "value=\"{Value}\"/>" +
            "</panel>";
        var binding = new RangeBinding();

        UiNineSlicePanel panel = MarkupDocument.Build(
            xml, binding, id => (id, 16, 16));
        var slider = Assert.IsType<UiScrollbar>(panel.Children[0]);
        Assert.False(slider.RetailArt);

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        var ctx = new UiRenderContext(renderer, new Vector2(800f, 600f));

        slider.TickSelfAndChildren(0);
        Assert.Equal(0.25f, slider.ScalarPosition, 3);

        slider.DrawSelfAndChildren(ctx);

        const int floatsPerQuad = 6 * 8;
        bool foundOffsetNub = renderer.DebugSpriteSegmentVerts.Any(s =>
        {
            if (s.Texture != 0u) return false;
            for (int q = 0; q + floatsPerQuad <= s.Verts.Count; q += floatsPerQuad)
            {
                float xMin = float.MaxValue;
                for (int v = 0; v < 6; v++)
                    xMin = MathF.Min(xMin, s.Verts[q + v * 8]);
                float r = s.Verts[q + 4], g = s.Verts[q + 5], b = s.Verts[q + 6], a = s.Verts[q + 7];
                bool isNubColor = MathF.Abs(r - slider.PlainNubColor.X) < 0.01f
                    && MathF.Abs(g - slider.PlainNubColor.Y) < 0.01f
                    && MathF.Abs(b - slider.PlainNubColor.Z) < 0.01f
                    && MathF.Abs(a - slider.PlainNubColor.W) < 0.01f;
                if (isNubColor && xMin > 0f)
                    return true;
            }
            return false;
        });
        Assert.True(foundOffsetNub, "expected a plain flat nub offset right of the origin at 25%");
    }


    [Fact]
    public void Build_MenuWithScrollAttribute_SetsUiMenuScrollableAndItsChromeSprites()
    {
        const string xml = """
            <panel x="0" y="0" w="160" h="40">
              <menu x="4" y="4" w="120" h="20" items="{Choices}"
                    selected="{Selected}" onchange="{SelectChoice}"
                    scroll="true" />
            </panel>
            """;
        var binding = new EditorBinding();

        UiNineSlicePanel panel = MarkupDocument.Build(
            xml, binding, _ => (1u, 32, 32));
        var menu = Assert.IsType<UiMenu>(panel.Children[0]);

        Assert.True(menu.Scrollable);
        Assert.NotEqual(0u, menu.ScrollTrackSprite);
        Assert.NotEqual(0u, menu.ScrollThumbSprite);
        Assert.NotEqual(0u, menu.ScrollUpSprite);
        Assert.NotEqual(0u, menu.ScrollDownSprite);
    }

    [Fact]
    public void Menu_Scroll_DrawsAScrollbarWhenTheMarkupItemCountOverflowsTheVisibleRows()
    {
        const string xml = """
            <panel x="0" y="0" w="160" h="40">
              <menu x="4" y="4" w="120" h="18" items="{ManyChoices}"
                    selected="{Selected}" onchange="{SelectChoice}"
                    rows="6" rowheight="18" scroll="true" style="retail" />
            </panel>
            """;
        var binding = new ManyChoicesBinding();

        UiNineSlicePanel panel = MarkupDocument.Build(
            xml, binding, id => (id, 16, 16));
        var menu = Assert.IsType<UiMenu>(panel.Children[0]);
        Assert.True(menu.RetailButtonArt);

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        var ctx = new UiRenderContext(renderer, new Vector2(800f, 600f));

        menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, 0, 10, 5));
        menu.OnEvent(new UiEvent(0, menu, UiEventType.Scroll, Data0: 0));
        Assert.True(menu.PopupScroll.HasOverflow);

        menu.DrawOverlays(ctx);

        Assert.Contains(
            renderer.DebugSpriteSegmentVerts,
            s => s.Texture == menu.ScrollThumbSprite);
    }


    private sealed class ManyChoicesBinding
    {
        public string Selected { get; private set; } = "Item 0";
        public IReadOnlyList<string> ManyChoices { get; } =
            Enumerable.Range(0, 18).Select(i => $"Item {i}").ToArray();
        public Action<string> SelectChoice => value => Selected = value;
    }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }


    private sealed class MenuStyleBinding
    {
        public IReadOnlyList<string> Choices => ["First", "Second"];
        public string Selected { get; } = "First";
    }

    private static string MenuXml(string? styleAttribute) =>
        "<panel x=\"0\" y=\"0\" w=\"240\" h=\"60\">" +
        $"<menu x=\"4\" y=\"4\" w=\"120\" h=\"20\" items=\"{{Choices}}\" selected=\"{{Selected}}\"{styleAttribute}/>" +
        "</panel>";

    [Fact]
    public void Menu_NoStyleAttribute_DefaultsToPlain_RetailButtonArtFalse()
    {
        var panel = MarkupDocument.Build(MenuXml(styleAttribute: ""), new MenuStyleBinding(), _ => (1u, 32, 32));
        var menu = Assert.IsType<UiMenu>(panel.Children[0]);

        Assert.False(menu.RetailButtonArt);
    }

    [Fact]
    public void Menu_StylePlain_Explicit_RetailButtonArtFalse()
    {
        var panel = MarkupDocument.Build(
            MenuXml(" style=\"plain\""), new MenuStyleBinding(), _ => (1u, 32, 32));
        var menu = Assert.IsType<UiMenu>(panel.Children[0]);

        Assert.False(menu.RetailButtonArt);
    }

    [Fact]
    public void Menu_StyleRetail_OptsIntoTheGoldButtonArt()
    {
        var panel = MarkupDocument.Build(
            MenuXml(" style=\"retail\""), new MenuStyleBinding(), _ => (1u, 32, 32));
        var menu = Assert.IsType<UiMenu>(panel.Children[0]);

        Assert.True(menu.RetailButtonArt);
    }

    [Fact]
    public void Menu_UnknownStyle_ThrowsFormatException_NamingTheElement()
    {
        var ex = Assert.Throws<FormatException>(
            () => MarkupDocument.Build(
                MenuXml(" style=\"chrome\""), new MenuStyleBinding(), _ => (1u, 32, 32)));

        Assert.Contains("menu", ex.Message);
        Assert.Contains("chrome", ex.Message);
    }


    private sealed class OverflowMenuBinding
    {
        public IReadOnlyList<string> Choices { get; } =
            Enumerable.Range(0, 12).Select(i => $"row{i}").ToList();
        public string Selected { get; } = "row0";
    }

    [Fact]
    public void Menu_Markup_IsAlwaysScrollable_WithRetailScrollbarChromeWired()
    {
        var panel = MarkupDocument.Build(
            MenuXml(styleAttribute: ""), new MenuStyleBinding(), _ => (1u, 32, 32));
        var menu = Assert.IsType<UiMenu>(panel.Children[0]);

        Assert.True(menu.Scrollable);
        Assert.True(menu.PopupScrollbarHideWhenDisabled);
        Assert.Equal(RetailScrollbarChrome.Track, menu.ScrollTrackSprite);
        Assert.Equal(RetailScrollbarChrome.ThumbTopNormal, menu.ScrollThumbTopSprite);
        Assert.Equal(RetailScrollbarChrome.ThumbMidNormal, menu.ScrollThumbSprite);
        Assert.Equal(RetailScrollbarChrome.ThumbBotNormal, menu.ScrollThumbBottomSprite);
        Assert.Equal(RetailScrollbarChrome.UpNormal, menu.ScrollUpSprite);
        Assert.Equal(RetailScrollbarChrome.DownNormal, menu.ScrollDownSprite);
    }

    [Fact]
    public void Menu_Markup_StyleRetail_IsAlsoScrollable_WithTheSameChrome()
    {
        var panel = MarkupDocument.Build(
            MenuXml(" style=\"retail\""), new MenuStyleBinding(), _ => (1u, 32, 32));
        var menu = Assert.IsType<UiMenu>(panel.Children[0]);

        Assert.True(menu.RetailButtonArt);
        Assert.True(menu.Scrollable);
        Assert.Equal(RetailScrollbarChrome.Track, menu.ScrollTrackSprite);
    }

    [Fact]
    public void Menu_Markup_OverflowingItems_DrawsRetailScrollbarChrome_OnOpen()
    {
        var binding = new OverflowMenuBinding();
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"200\" h=\"100\">" +
            "<menu x=\"4\" y=\"4\" w=\"120\" h=\"20\" items=\"{Choices}\" " +
            "selected=\"{Selected}\" openupward=\"false\"/>" +
            "</panel>";
        var panel = MarkupDocument.Build(xml, binding, id => (id, 8, 8));
        var menu = Assert.IsType<UiMenu>(panel.Children[0]);

        // Default rows=7, 12 items -> overflow.
        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, Data1: 10, Data2: 10)));
        Assert.True(menu.IsOpen);

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSourceForMenuTests(), "unused");
        renderer.Begin(new Vector2(200f, 200f));
        var ctx = new UiRenderContext(renderer, new Vector2(200f, 200f));
        menu.DrawOverlays(ctx);

        int TrackQuads() => renderer.DebugSpriteSegmentVerts
            .Where(s => s.Texture == RetailScrollbarChrome.Track)
            .Sum(s => s.Verts.Count) / 48;
        Assert.True(TrackQuads() > 0, "expected the overflowing popup to draw the retail scrollbar track");
    }

    [Fact]
    public void Menu_Markup_FewItems_DrawsNoScrollbarChrome_OnOpen()
    {
        var panel = MarkupDocument.Build(MenuXml(styleAttribute: ""), new MenuStyleBinding(), id => (id, 8, 8));
        var menu = Assert.IsType<UiMenu>(panel.Children[0]);

        Assert.True(menu.OnEvent(new UiEvent(0, menu, UiEventType.MouseDown, Data1: 10, Data2: 10)));
        Assert.True(menu.IsOpen);

        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSourceForMenuTests(), "unused");
        renderer.Begin(new Vector2(200f, 200f));
        var ctx = new UiRenderContext(renderer, new Vector2(200f, 200f));
        menu.DrawOverlays(ctx);

        int TrackQuads() => renderer.DebugSpriteSegmentVerts
            .Where(s => s.Texture == RetailScrollbarChrome.Track)
            .Sum(s => s.Verts.Count) / 48;
        Assert.Equal(0, TrackQuads());
    }

    private sealed class NullGpuFrameSourceForMenuTests : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }
}
