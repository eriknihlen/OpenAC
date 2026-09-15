using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

/// <summary>
/// A chat transcript that has been scrolled up must stay on the text the reader scrolled up
/// to while new lines arrive. The scroll model holds a pixel offset, and the chat log is a
/// ring: once it is full every new message drops the oldest line, so the same offset lands
/// one line further along per message and the view runs away toward the newest text. The
/// view is held on the identity of its top line instead, and pins to the end again only when
/// the reader scrolls back there.
/// </summary>
public class UiTextScrollHoldTests
{
    private const float LineHeight = 16f;
    private const float ViewHeight = 4 * LineHeight;   // four lines visible

    private static readonly Vector4 White = Vector4.One;

    private static IReadOnlyList<UiText.LineKey> Keys(params (long Source, int Start)[] keys)
        => keys.Select(k => new UiText.LineKey(k.Source, k.Start)).ToList();

    // ── TryResolveLine: the pure mapping from "this line's text" back to "this row" ──────

    [Fact]
    public void TryResolveLine_FindsTheRowAfterOlderLinesWereDropped()
    {
        // Message 7 was row 2; two older messages were dropped off the front.
        var keys = Keys((7, 0), (8, 0), (9, 0));

        Assert.True(UiText.TryResolveLine(keys, source: 7, start: 0, out int line));
        Assert.Equal(0, line);
    }

    [Fact]
    public void TryResolveLine_PicksTheExactWrappedLineNotItsNeighbour()
    {
        // A message wrapped mid-word: row 1 ends where row 2 begins, and the row that begins
        // at 10 must be row 2, not the row that merely ends there.
        var keys = Keys((3, 0), (3, 10), (3, 20));

        Assert.True(UiText.TryResolveLine(keys, source: 3, start: 10, out int line));
        Assert.Equal(1, line);
    }

    [Fact]
    public void TryResolveLine_AfterARewrapTakesTheNextBreakOrTheLastLine()
    {
        var keys = Keys((3, 0), (3, 12), (3, 24));

        Assert.True(UiText.TryResolveLine(keys, source: 3, start: 10, out int next));
        Assert.Equal(1, next);
        Assert.True(UiText.TryResolveLine(keys, source: 3, start: 30, out int last));
        Assert.Equal(2, last);
    }

    [Fact]
    public void TryResolveLine_IsFalseForAMessageNoLongerShown()
    {
        Assert.False(UiText.TryResolveLine(Keys((3, 0), (4, 0)), source: 2, start: 0, out _));
    }

    // ── The element: a scrolled-up view, seen through many rebuilds ─────────────────────

    private sealed class Transcript
    {
        public readonly UiText Text = new() { Selectable = true };
        private readonly int _capacity;
        private readonly List<long> _shown = new();
        private long _nextSource = 1;

        public Transcript(int capacity, int initialCount)
        {
            _capacity = capacity;
            Text.LinesProvider = () => _shown
                .Select(s => new UiText.Line($"message {s}", White)).ToList();
            Text.LineKeysProvider = () => _shown
                .Select(s => new UiText.LineKey(s, 0)).ToList();
            for (int i = 0; i < initialCount; i++)
                _shown.Add(_nextSource++);
            Frame();
        }

        /// <summary>A new message arrives; the ring drops its oldest line when full.</summary>
        public void Arrive(int count = 1)
        {
            for (int i = 0; i < count; i++)
            {
                _shown.Add(_nextSource++);
                if (_shown.Count > _capacity)
                    _shown.RemoveAt(0);
            }
        }

        public void Frame()
        {
            Text.RefreshLines();
            Text.LayoutScroll(LineHeight, ViewHeight);
        }

        /// <summary>The message whose line is at the top of the view.</summary>
        public long TopMessage => _shown[(int)(Text.Scroll.ScrollY / LineHeight)];
    }

    [Fact]
    public void AViewAtTheEndFollowsNewLines()
    {
        var t = new Transcript(capacity: 20, initialCount: 10);
        Assert.True(t.Text.Scroll.AtEnd);

        t.Arrive(3);
        t.Frame();

        Assert.True(t.Text.Scroll.AtEnd);
        Assert.Equal(13 - 4 + 1, t.TopMessage);
    }

    [Fact]
    public void AScrolledUpViewKeepsItsOffsetWhileLinesAppend()
    {
        var t = new Transcript(capacity: 200, initialCount: 10);
        t.Text.Scroll.ScrollByLines(-3);
        t.Frame();
        long reading = t.TopMessage;
        int offset = t.Text.Scroll.ScrollY;

        t.Arrive(5);
        t.Frame();

        Assert.Equal(reading, t.TopMessage);
        Assert.Equal(offset, t.Text.Scroll.ScrollY);
        Assert.False(t.Text.Scroll.AtEnd);
    }

    [Fact]
    public void AScrolledUpViewStaysOnItsTextWhileTheRingDropsOlderLines()
    {
        var t = new Transcript(capacity: 10, initialCount: 10);
        t.Text.Scroll.ScrollByLines(-3);
        t.Frame();
        long reading = t.TopMessage;
        int offset = t.Text.Scroll.ScrollY;

        // Each arrival drops the oldest line: the offset alone would now show different text.
        for (int i = 0; i < 2; i++)
        {
            t.Arrive();
            t.Frame();
        }

        Assert.Equal(reading, t.TopMessage);
        Assert.Equal(offset - 2 * (int)LineHeight, t.Text.Scroll.ScrollY);
        Assert.False(t.Text.Scroll.AtEnd);
    }

    [Fact]
    public void AViewPartWayIntoALineKeepsThatPartAsWell()
    {
        var t = new Transcript(capacity: 10, initialCount: 10);
        t.Text.Scroll.SetScrollY(3 * (int)LineHeight + 5);
        t.Frame();
        long reading = t.TopMessage;

        t.Arrive();
        t.Frame();

        Assert.Equal(reading, t.TopMessage);
        Assert.Equal(2 * (int)LineHeight + 5, t.Text.Scroll.ScrollY);
    }

    [Fact]
    public void TheViewRestsAtTheTopOnceItsTextHasDroppedOutOfTheLog()
    {
        var t = new Transcript(capacity: 10, initialCount: 10);
        t.Text.Scroll.SetScrollY(0);
        t.Frame();

        t.Arrive(3);
        t.Frame();

        Assert.Equal(0, t.Text.Scroll.ScrollY);
        Assert.False(t.Text.Scroll.AtEnd);
    }

    [Fact]
    public void ScrollingBackToTheEndResumesFollowing()
    {
        var t = new Transcript(capacity: 10, initialCount: 10);
        t.Text.Scroll.ScrollByLines(-3);
        t.Frame();

        t.Text.Scroll.ScrollToEnd();
        t.Arrive(2);
        t.Frame();

        Assert.True(t.Text.Scroll.AtEnd);
        Assert.Equal(t.Text.Scroll.MaxScroll, t.Text.Scroll.ScrollY);
    }

    [Fact]
    public void ASelectionMadeWhileScrolledUpStaysWithItsTextAsLinesArrive()
    {
        var t = new Transcript(capacity: 10, initialCount: 10);
        t.Text.Scroll.ScrollByLines(-3);
        t.Frame();
        int topRow = (int)(t.Text.Scroll.ScrollY / LineHeight);

        t.Text.OnEvent(new UiEvent(0, t.Text, UiEventType.MouseDown, 0, 0, (int)(topRow * LineHeight) + 1));
        t.Text.OnEvent(new UiEvent(0, t.Text, UiEventType.MouseMove, 0, 0, (int)((topRow + 2) * LineHeight) + 1));
        string before = t.Text.SelectedText();
        Assert.NotEmpty(before);

        t.Arrive(2);
        t.Frame();

        Assert.Equal(before, t.Text.SelectedText());
    }
}
