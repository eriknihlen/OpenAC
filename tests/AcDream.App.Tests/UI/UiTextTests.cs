using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI;

public class UiTextTests
{

    private static UiText.TextRun Run(string text)
        => new(text, Vector4.One);

    [Fact]
    public void LayoutRuns_AdvancesThePenAcrossRuns()
    {
        var placed = UiText.LayoutRuns(
            [
                new UiText.TextRun("Dww", new Vector4(0f, 0.698f, 0f, 1f)),
                new UiText.TextRun(" tells you", Vector4.One),
            ],
            startX: 4f,
            measure: text => text.Length * 10f);

        Assert.Equal(2, placed.Count);
        Assert.Equal(4f, placed[0].X);           // first run starts at the line origin
        Assert.Equal(34f, placed[1].X);
        Assert.Equal(new Vector4(0f, 0.698f, 0f, 1f), placed[0].Color);
        Assert.Equal(Vector4.One, placed[1].Color);
    }

    [Fact]
    public void LayoutRuns_EmptyRunsDrawNothingAndShiftNothing()
    {
        var placed = UiText.LayoutRuns(
            [
                new UiText.TextRun(string.Empty, Vector4.One),
                new UiText.TextRun("ab", Vector4.One),
                new UiText.TextRun(string.Empty, Vector4.One),
                new UiText.TextRun("cd", Vector4.One),
            ],
            startX: 0f,
            measure: text => text.Length * 10f);

        Assert.Equal(2, placed.Count);
        Assert.Equal(0f, placed[0].X);
        Assert.Equal(20f, placed[1].X);
    }

    [Fact]
    public void RunsMatchLine_AcceptsAnExactSplitOfTheLine()
    {
        Assert.True(UiText.RunsMatchLine(
            [Run("Dww"), Run(" tells you, \"hi\"")],
            "Dww tells you, \"hi\""));

        // Order matters: the same pieces rearranged are a different line.
        Assert.False(UiText.RunsMatchLine(
            [Run(" tells you, \"hi\""), Run("Dww")],
            "Dww tells you, \"hi\""));
    }

    [Fact]
    public void RunsMatchLine_RejectsAnythingThatWouldDesyncSelection()
    {
        const string line = "Dww tells you";

        Assert.False(UiText.RunsMatchLine([Run("Dww")], line));                 // short
        Assert.False(UiText.RunsMatchLine([Run(line), Run("!")], line));        // long
        Assert.False(UiText.RunsMatchLine([Run("Dwx"), Run(" tells you")], line)); // altered
        Assert.False(UiText.RunsMatchLine([], line));                          // empty vs text
    }

    [Fact]
    public void RunsMatchLine_HandlesTheDegenerateCases()
    {
        Assert.True(UiText.RunsMatchLine([], string.Empty));
        Assert.True(UiText.RunsMatchLine([Run(string.Empty)], string.Empty));
        Assert.True(UiText.RunsMatchLine([Run("ab"), Run(string.Empty), Run("cd")], "abcd"));
    }

    [Fact]
    public void WrapWords_UsesMeasuredWidthAndPreservesParagraphs()
    {
        IReadOnlyList<string> lines = UiText.WrapWords(
            "one two three\nfour",
            text => text.Length * 10f,
            maximumWidth: 75f);

        Assert.Equal(["one two", "three", "four"], lines);
    }

    [Fact]
    public void ClampScroll_PinsToZero_WhenContentFitsView()
    {
        Assert.Equal(0f, UiText.ClampScroll(50f, contentHeight: 80f, viewHeight: 200f));
        Assert.Equal(0f, UiText.ClampScroll(0f, contentHeight: 80f, viewHeight: 200f));
    }

    [Fact]
    public void ClampScroll_CapsAtContentMinusView_WhenOverflowing()
    {
        Assert.Equal(300f, UiText.ClampScroll(1000f, contentHeight: 500f, viewHeight: 200f));
        Assert.Equal(120f, UiText.ClampScroll(120f, contentHeight: 500f, viewHeight: 200f));
    }

    [Fact]
    public void ClampScroll_NeverNegative()
    {
        Assert.Equal(0f, UiText.ClampScroll(-50f, contentHeight: 500f, viewHeight: 200f));
    }


    private static readonly Func<char, float> Mono10 = static _ => 10f;

    [Fact]
    public void CharIndexAt_ZeroOrNegative_IsColumnZero()
    {
        Assert.Equal(0, UiText.CharIndexAt("hello", Mono10, 0f));
        Assert.Equal(0, UiText.CharIndexAt("hello", Mono10, -5f));
    }

    [Fact]
    public void CharIndexAt_SnapsToGlyphMidpoint()
    {
        // glyph[0] spans 0..10 (midpoint 5), glyph[1] 10..20 (midpoint 15), ...
        Assert.Equal(0, UiText.CharIndexAt("hello", Mono10, 4f));   // before mid of glyph 0
        Assert.Equal(1, UiText.CharIndexAt("hello", Mono10, 6f));   // past mid of glyph 0
        Assert.Equal(1, UiText.CharIndexAt("hello", Mono10, 14f));  // before mid of glyph 1
        Assert.Equal(2, UiText.CharIndexAt("hello", Mono10, 16f));  // past mid of glyph 1
    }

    [Fact]
    public void CharIndexAt_PastEnd_IsLength()
    {
        Assert.Equal(5, UiText.CharIndexAt("hello", Mono10, 1000f));
    }

    [Fact]
    public void CharIndexAt_EmptyString_IsZero()
    {
        Assert.Equal(0, UiText.CharIndexAt("", Mono10, 50f));
    }

    // ── SelectedText assembly ────────────────────────────────────────────

    private static IReadOnlyList<UiText.Line> Lines(params string[] texts)
    {
        var list = new List<UiText.Line>(texts.Length);
        foreach (var t in texts)
            list.Add(new UiText.Line(t, new Vector4(1, 1, 1, 1)));
        return list;
    }

    [Fact]
    public void SelectedText_SingleLine_Substring()
    {
        var lines = Lines("hello world");
        var s = UiText.SelectedText(lines, new UiText.Pos(0, 6), new UiText.Pos(0, 11));
        Assert.Equal("world", s);
    }

    [Fact]
    public void SelectedText_SingleLine_ReversedAnchorCaret_IsNormalised()
    {
        var lines = Lines("hello world");
        var s = UiText.SelectedText(lines, new UiText.Pos(0, 11), new UiText.Pos(0, 6));
        Assert.Equal("world", s);
    }

    [Fact]
    public void SelectedText_SamePosition_IsEmpty()
    {
        var lines = Lines("hello");
        Assert.Equal("", UiText.SelectedText(lines, new UiText.Pos(0, 3), new UiText.Pos(0, 3)));
    }

    [Fact]
    public void SelectedText_MultiLine_JoinsWithNewline()
    {
        var lines = Lines("first line", "second line", "third line");
        // from col 6 of line 0 ("line") through col 5 of line 2 ("third")
        var s = UiText.SelectedText(lines, new UiText.Pos(0, 6), new UiText.Pos(2, 5));
        Assert.Equal("line\nsecond line\nthird", s);
    }

    [Fact]
    public void SelectedText_MultiLine_TwoLines_NoMiddle()
    {
        var lines = Lines("alpha", "bravo");
        var s = UiText.SelectedText(lines, new UiText.Pos(0, 2), new UiText.Pos(1, 3));
        Assert.Equal("pha\nbra", s);
    }

    [Fact]
    public void SelectedText_MultiLine_ReversedAnchorCaret_IsNormalised()
    {
        var lines = Lines("alpha", "bravo");
        // end before start → Order() swaps them.
        var s = UiText.SelectedText(lines, new UiText.Pos(1, 3), new UiText.Pos(0, 2));
        Assert.Equal("pha\nbra", s);
    }

    [Fact]
    public void SelectedText_EmptyLineList_IsEmpty()
    {
        Assert.Equal("", UiText.SelectedText(Array.Empty<UiText.Line>(),
            new UiText.Pos(0, 0), new UiText.Pos(0, 0)));
    }

    [Fact]
    public void Order_SortsByLineThenColumn()
    {
        var (s1, e1) = UiText.Order(new UiText.Pos(2, 1), new UiText.Pos(0, 5));
        Assert.Equal(new UiText.Pos(0, 5), s1);
        Assert.Equal(new UiText.Pos(2, 1), e1);

        var (s2, e2) = UiText.Order(new UiText.Pos(1, 8), new UiText.Pos(1, 2));
        Assert.Equal(new UiText.Pos(1, 2), s2);
        Assert.Equal(new UiText.Pos(1, 8), e2);
    }

    [Fact]
    public void WrapWords_PreservesNewlinesAndWrapsAtSpaces()
    {
        IReadOnlyList<string> lines = UiText.WrapWords(
            "one two three\nfour",
            static text => text.Length,
            maximumWidth: 7f);

        Assert.Equal(["one two", "three", "four"], lines);
    }

    [Fact]
    public void WrapWords_SplitsAnOverWidthWordAtCharacterBoundaries()
    {
        IReadOnlyList<string> lines = UiText.WrapWords(
            "go abcdef",
            static text => text.Length,
            maximumWidth: 5f);

        Assert.Equal(["go ab", "cdef"], lines);
    }

    // ── VOffset: vertical positioning for single-line mode ───────────────────

    [Fact]
    public void VOffset_Center_ReturnsHalfHeightMinusLineHeight()
    {
        float y = UiText.VOffset(height: 55f, lineHeight: 12f, padding: 0f, VJustify.Center);
        Assert.Equal((55f - 12f) * 0.5f, y);
    }

    [Fact]
    public void VOffset_Top_ReturnsPadding()
    {
        float y = UiText.VOffset(height: 55f, lineHeight: 12f, padding: 0f, VJustify.Top);
        Assert.Equal(0f, y);

        float yWithPadding = UiText.VOffset(height: 55f, lineHeight: 12f, padding: 4f, VJustify.Top);
        Assert.Equal(4f, yWithPadding);
    }

    /// <summary>
    /// VJustify.Bottom: vertical offset = height - lineHeight - padding.
    /// </summary>
    [Fact]
    public void VOffset_Bottom_ReturnsHeightMinusLineHeightMinusPadding()
    {
        float y = UiText.VOffset(height: 55f, lineHeight: 12f, padding: 2f, VJustify.Bottom);
        Assert.Equal(55f - 12f - 2f, y);
    }

    [Fact]
    public void VOffset_Center_IgnoresPadding()
    {
        float yNoPad  = UiText.VOffset(height: 40f, lineHeight: 12f, padding: 0f, VJustify.Center);
        float yWithPad = UiText.VOffset(height: 40f, lineHeight: 12f, padding: 4f, VJustify.Center);
        Assert.Equal(yNoPad, yWithPad);
        Assert.Equal((40f - 12f) * 0.5f, yNoPad);
    }

    [Theory]
    [InlineData(VJustify.Top, 0f)]
    [InlineData(VJustify.Center, 4.5f)]
    [InlineData(VJustify.Bottom, 9f)]
    public void ContentBaseY_FittingAuthoredText_UsesRetailVerticalJustification(
        VJustify justification,
        float expected)
    {
        float y = UiText.ContentBaseY(
            top: 0f,
            bottom: 25f,
            contentHeight: 16f,
            maxScroll: 0f,
            scrollY: 0f,
            justification,
            honorJustification: true);

        Assert.Equal(expected, y);
    }

    [Fact]
    public void ContentBaseY_OverflowingAuthoredText_PreservesScrollPosition()
    {
        float y = UiText.ContentBaseY(
            top: 0f,
            bottom: 25f,
            contentHeight: 64f,
            maxScroll: 39f,
            scrollY: 12f,
            VJustify.Center,
            honorJustification: true);

        Assert.Equal(-12f, y);
    }

    [Fact]
    public void ContentBaseY_SynthesizedText_RetainsBottomPinWhenContentFits()
    {
        float y = UiText.ContentBaseY(
            top: 0f,
            bottom: 25f,
            contentHeight: 16f,
            maxScroll: 0f,
            scrollY: 0f,
            VJustify.Center,
            honorJustification: false);

        Assert.Equal(9f, y);
    }

    [Fact]
    public void ContentOffsetX_ZeroMargins_MatchesBarePaddingMath()
    {
        float left = UiText.ContentOffsetX(
            elementWidth: 200f, padding: 4f, marginLeft: 0f, marginRight: 0f,
            lineWidth: 30f, centered: false, rightAligned: false);
        Assert.Equal(4f, left);

        float centered = UiText.ContentOffsetX(
            elementWidth: 200f, padding: 4f, marginLeft: 0f, marginRight: 0f,
            lineWidth: 30f, centered: true, rightAligned: false);
        Assert.Equal(Math.Max(4f, (200f - 30f) * 0.5f), centered);

        float right = UiText.ContentOffsetX(
            elementWidth: 200f, padding: 4f, marginLeft: 0f, marginRight: 0f,
            lineWidth: 30f, centered: false, rightAligned: true);
        Assert.Equal(200f - 4f - 30f, right);
    }

    [Fact]
    public void ContentOffsetX_LeftJustified_HonorsAuthoredMarginLeft()
    {
        float x = UiText.ContentOffsetX(
            elementWidth: 265f, padding: 0f, marginLeft: 9f, marginRight: 26f,
            lineWidth: 100f, centered: false, rightAligned: false);
        Assert.Equal(9f, x);
    }

    /// <summary>
    /// A right-aligned line must stop before MarginRight, not at the raw
    /// element edge — the R2-1 fix's other half (the wrap width shrinks by
    /// the same inset so text no longer overflows the visible right edge
    /// either).
    /// </summary>
    [Fact]
    public void ContentOffsetX_RightAligned_HonorsAuthoredMarginRight()
    {
        float x = UiText.ContentOffsetX(
            elementWidth: 265f, padding: 0f, marginLeft: 9f, marginRight: 26f,
            lineWidth: 50f, centered: false, rightAligned: false);
        _ = x;

        float right = UiText.ContentOffsetX(
            elementWidth: 265f, padding: 0f, marginLeft: 9f, marginRight: 26f,
            lineWidth: 50f, centered: false, rightAligned: true);
        Assert.Equal(189f, right);
    }

    [Fact]
    public void LineIntersectsViewport_PartialLineRemainsDrawable()
    {
        Assert.True(UiText.LineIntersectsViewport(
            lineTop: 0f,
            lineHeight: 16f,
            viewportTop: 0f,
            viewportBottom: 15f));

        Assert.True(UiText.LineIntersectsViewport(
            lineTop: -1f,
            lineHeight: 16f,
            viewportTop: 0f,
            viewportBottom: 15f));
    }

    [Theory]
    [InlineData(-16f, 16f)]
    [InlineData(15f, 16f)]
    public void LineIntersectsViewport_FullyClippedLineIsSkipped(
        float lineTop,
        float lineHeight)
    {
        Assert.False(UiText.LineIntersectsViewport(
            lineTop,
            lineHeight,
            viewportTop: 0f,
            viewportBottom: 15f));
    }
}
