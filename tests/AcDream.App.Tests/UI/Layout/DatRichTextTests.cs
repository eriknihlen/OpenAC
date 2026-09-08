using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
namespace AcDream.App.Tests.UI.Layout;

public class DatRichTextTests
{
    private static readonly Vector4 White = Vector4.One;
    private static readonly Vector4 Green = new(0f, 1f, 0f, 1f);

    private static UiText MakeTarget(float width) =>
        new() { Width = width, Height = 200f };

    [Fact]
    public void Compose_SplitsOnRealNewlines_AndKeepsLiteralPairsVerbatim()
    {
        UiText target = MakeTarget(1000f); // wide enough that nothing wraps
        var segments = new[]
        {
            new DatRichText.Segment("line one\nliteral \\n stays", White),
        };

        var lines = DatRichText.Compose(target, segments);

        Assert.Equal(2, lines.Count);
        Assert.Equal("line one", lines[0].Text);
        Assert.Equal("literal \\n stays", lines[1].Text);
    }

    [Fact]
    public void Compose_WordWrapsToTheTargetWidth()
    {
        UiText target = MakeTarget(80f);
        var segments = new[]
        {
            new DatRichText.Segment("one two three four five six seven eight", White),
        };

        var lines = DatRichText.Compose(target, segments);

        Assert.True(lines.Count > 1, "a long segment must wrap to more than one line");
        foreach (UiText.Line line in lines)
            Assert.True(line.Text.Length * 8f <= 80f, $"line '{line.Text}' overflowed the target width");
    }

    [Fact]
    public void Compose_EachSegmentKeepsItsOwnColorAcrossItsWrappedLines()
    {
        UiText target = MakeTarget(1000f);
        var segments = new[]
        {
            new DatRichText.Segment("Header:", Green),
            new DatRichText.Segment("Body text.", White),
        };

        var lines = DatRichText.Compose(target, segments);

        Assert.Equal(2, lines.Count);
        Assert.Equal(Green, lines[0].Color);
        Assert.Equal(White, lines[1].Color);
    }

    [Fact]
    public void Compose_NullOrEmptySegmentText_IsSkipped()
    {
        UiText target = MakeTarget(1000f);
        var segments = new[]
        {
            new DatRichText.Segment(null, White),
            new DatRichText.Segment(string.Empty, White),
            new DatRichText.Segment("real text", White),
        };

        var lines = DatRichText.Compose(target, segments);

        Assert.Single(lines);
        Assert.Equal("real text", lines[0].Text);
    }

    [Fact]
    public void Compose_NoSeparatorInsertedBetweenSegments()
    {
        UiText target = MakeTarget(1000f);
        var segments = new[]
        {
            new DatRichText.Segment("first", White),
            new DatRichText.Segment("second", White),
        };

        var lines = DatRichText.Compose(target, segments);

        Assert.Equal(2, lines.Count);
        Assert.Equal("first", lines[0].Text);
        Assert.Equal("second", lines[1].Text);
    }

    [Fact]
    public void Compose_WordWrapsToTheTargetWidth_MinusTheFourRetailMargins()
    {
        UiText fits = new() { Width = 100f, Height = 200f, MarginLeft = 10f, MarginRight = 10f };
        var fitsLines = DatRichText.Compose(fits, [new DatRichText.Segment("aaaaaaaaaa", White)]);
        Assert.Single(fitsLines);

        UiText overflows = new() { Width = 100f, Height = 200f, MarginLeft = 10f, MarginRight = 10f };
        var overflowLines = DatRichText.Compose(overflows, [new DatRichText.Segment("aaaaaaaaaaa", White)]);
        Assert.True(overflowLines.Count > 1, "an 88px word in an 80px content width must wrap");
    }

    [Fact]
    public void PaletteColor_ReturnsAuthoredPaletteEntry_WhenPresent()
    {
        UiText target = new()
        {
            FontColorPalette = [White, Green],
        };

        Assert.Equal(White, DatRichText.PaletteColor(target, 0, Green));
        Assert.Equal(Green, DatRichText.PaletteColor(target, 1, White));
    }

    [Fact]
    public void PaletteColor_FallsBack_WhenPaletteTooShortOrMissing()
    {
        UiText target = new(); // empty palette

        Assert.Equal(Green, DatRichText.PaletteColor(target, 1, Green));
        Assert.Equal(Green, DatRichText.PaletteColor(target, -1, Green));
    }
}
