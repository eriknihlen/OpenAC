using System.Linq;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.App.Tests.UI.Layout;

/// <summary>
/// An element's own (authored) string is laid out the way the element says:
/// word-wrapped to its width between the margins unless it is one line, and
/// shown from its first line when it is taller than the box.
/// </summary>
public sealed class DatWidgetFactoryAuthoredTextTests
{
    private const string Sentence = "From this screen you can modify";

    private static (uint, int, int) NoTex(uint _) => (0, 0, 0);

    [Fact]
    public void AuthoredTextWithoutLineBreak_WrapsToTheElement()
    {
        // 100 pixels wide, no font: eight pixels a character, twelve a line.
        UiText text = Build(TextInfo(), Sentence);

        Assert.Equal(
            new[] { "From this", "screen you", "can modify" },
            text.LinesProvider().Select(static line => line.Text));
    }

    [Fact]
    public void AuthoredTextWithOneLineFalse_WrapsToTheElement()
    {
        UiText text = Build(TextInfo((0x20u, Bool(false))), Sentence);

        Assert.Equal(3, text.LinesProvider().Count);
    }

    [Fact]
    public void AuthoredTextOnAOneLineElement_StaysOneLine()
    {
        UiText text = Build(TextInfo((0x20u, Bool(true))), Sentence);

        Assert.Equal(Sentence, Assert.Single(text.LinesProvider()).Text);
    }

    [Fact]
    public void AuthoredTextThatFits_IsOneLine()
    {
        UiText text = Build(TextInfo(), "Fits");

        Assert.Equal("Fits", Assert.Single(text.LinesProvider()).Text);
    }

    [Fact]
    public void AuthoredText_WrapsBetweenTheMargins()
    {
        // 100 wide less 10 + 10 of margin: ten characters a line.
        ElementInfo info = TextInfo();
        info.MarginLeft = 10;
        info.MarginRight = 10;
        UiText text = Build(info, "abcd efgh ijkl");

        Assert.Equal(
            new[] { "abcd efgh", "ijkl" },
            text.LinesProvider().Select(static line => line.Text));
    }

    [Fact]
    public void AStateString_WrapsLikeTheDirectOne()
    {
        const uint Locked = 0x10000019u;
        ElementInfo info = TextInfo();
        var state = new UiStateInfo { Id = Locked, Name = "Locked" };
        state.Properties.Set(0x17u, StringInfo(43u));
        info.States[Locked] = state;

        UiText text = Build(info, "Short", (43u, Sentence));
        Assert.Equal("Short", Assert.Single(text.LinesProvider()).Text);

        Assert.True(text.TrySetRetailState(Locked));

        Assert.Equal(
            new[] { "From this", "screen you", "can modify" },
            text.LinesProvider().Select(static line => line.Text));
    }

    [Fact]
    public void AuthoredTextTallerThanItsBox_ShowsItsFirstLine()
    {
        // Three 16-pixel lines in a 20-pixel box.
        UiText text = Build(TextInfo(), "one\ntwo\nthree");
        text.DatFont = DatFont();

        Draw(text);

        Assert.True(text.Scroll.MaxScroll > 0);
        Assert.Equal(0, text.Scroll.ScrollY);
    }

    [Fact]
    public void LinesSetByCode_StillFollowTheEnd()
    {
        // The same element given lines by a panel (a log) keeps following
        // the newest line.
        UiText text = Build(TextInfo(), "Authored");
        text.DatFont = DatFont();
        UiText.Line[] log =
        [
            new("one", Vector4.One),
            new("two", Vector4.One),
            new("three", Vector4.One),
        ];
        text.LinesProvider = () => log;

        Draw(text);

        Assert.True(text.Scroll.MaxScroll > 0);
        Assert.Equal(text.Scroll.MaxScroll, text.Scroll.ScrollY);
    }

    private static UiText Build(
        ElementInfo info,
        string authored,
        params (uint Id, string Text)[] others)
        => Assert.IsType<UiText>(DatWidgetFactory.Create(
            info, NoTex, null,
            stringResolve: value =>
                others.FirstOrDefault(o => o.Id == value.StringId).Text ?? authored));

    private static ElementInfo TextInfo(params (uint Id, UiPropertyValue Value)[] properties)
    {
        var info = new ElementInfo { Id = 0x10000016u, Type = 12, Width = 100, Height = 20 };
        var state = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        state.Properties.Set(0x17u, StringInfo(42u));
        foreach (var property in properties)
            state.Properties.Set(property.Id, property.Value);
        info.States[UiStateInfo.DirectStateId] = state;
        return info;
    }

    private static UiPropertyValue StringInfo(uint stringId)
        => new()
        {
            Kind = UiPropertyKind.StringInfo,
            StringInfoValue = new UiStringInfoValue(0, stringId, 2, 0, 1, 0),
        };

    private static UiPropertyValue Bool(bool value)
        => new() { Kind = UiPropertyKind.Bool, BoolValue = value };

    private static UiDatFont DatFont() => new(
        fgTex: 1u, fgW: 1, fgH: 1,
        bgTex: 0, bgW: 0, bgH: 0,
        lineHeight: 16f, baselineOffset: 12f,
        glyphs: new Dictionary<char, FontCharDesc>());

    private static void Draw(UiText text)
    {
        var renderer = new TextRenderer(new RecordingGpuDevice(), new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        text.DrawSelfAndChildren(new UiRenderContext(renderer, new Vector2(800f, 600f)));
    }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }
}
