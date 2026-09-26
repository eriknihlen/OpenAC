using AcDream.App.UI;
namespace AcDream.App.Tests.UI;

public sealed class UiMarkupLogTests
{
    private sealed class Transcript
    {
        public IReadOnlyList<string> Lines { get; set; } = Enumerable.Range(0, 30).Select(i => $"Action {i}").ToArray();
        public int First { get; set; }
    }
    private static (UiRoot Root, UiMarkupLog Log, Transcript Data) Create()
    {
        var data = new Transcript();
        var panel = MarkupDocument.Build("""
            <panel x="0" y="0" w="300" h="100">
              <log x="0" y="0" w="280" h="88" items="{Lines}" firstindex="{First}" />
            </panel>
            """, data, _ => (0u, 0, 0));
        var root = new UiRoot { Width = 500, Height = 500 };
        root.AddChild(panel); root.Tick(.016, 1);
        return (root, Assert.IsType<UiMarkupLog>(panel.Children[0]), data);
    }
    [Fact]
    public void AppendFollowsOnlyAtBottomAndWheelScrollResumesAtEnd()
    {
        var (root, log, data) = Create();
        Assert.True(log.Scroll.AtEnd);
        data.Lines = data.Lines.Append("Next action").ToArray(); root.Tick(.016, 2);
        Assert.True(log.Scroll.AtEnd);
        root.OnMouseMove(20, 20); root.OnScroll(1);
        Assert.False(log.FollowingEnd);
        string? anchor = log.FirstVisibleText; int offset = log.Scroll.ScrollY;
        data.Lines = data.Lines.Append("Another action").ToArray(); root.Tick(.016, 3);
        Assert.Equal(offset, log.Scroll.ScrollY);
        Assert.Equal(anchor, log.FirstVisibleText);
        log.Scroll.ScrollToEnd();
        data.Lines = data.Lines.Append("At bottom again").ToArray(); root.Tick(.016, 4);
        Assert.True(log.Scroll.AtEnd);
        Assert.True(log.FollowingEnd);
    }
    [Fact]
    public void ScrollbarKeepsItsLayoutAndPausesFollowingThroughRootInput()
    {
        var (root, log, data) = Create();
        var bar = Assert.IsType<UiScrollbar>(Assert.Single(log.Children));
        bar.ApplyAnchor(log.Width, log.Height);
        Assert.Equal(log.Height - 2, bar.Height);
        Assert.Equal(log.Width - 17, bar.Left);
        root.OnMouseDown(UiMouseButton.Left, (int)bar.Left + 5, 5);
        root.OnMouseUp(UiMouseButton.Left, (int)bar.Left + 5, 5);
        Assert.False(log.FollowingEnd);
        int position = log.Scroll.ScrollY;
        data.Lines = data.Lines.Append("Arrived during reading").ToArray(); root.Tick(.016, 2);
        Assert.Equal(position, log.Scroll.ScrollY);
    }

    [Fact]
    public void TrimmingOldEntriesPreservesVisibleEntryAndDoesNotResumeFollowing()
    {
        var (root, log, data) = Create();
        log.Scroll.SetScrollY(10 * log.Scroll.LineHeight + 3);
        long? entry = log.FirstVisibleEntry; string? text = log.FirstVisibleText;
        data.Lines = data.Lines.Skip(5).Append("New").ToArray(); data.First += 5;
        root.Tick(.016, 2);
        Assert.Equal(entry, log.FirstVisibleEntry);
        Assert.Equal(text, log.FirstVisibleText);
        Assert.False(log.FollowingEnd);
        Assert.Equal(3, log.Scroll.ScrollY % log.Scroll.LineHeight);
    }
    [Fact]
    public void RemovedAnchorClampsToOldestWithoutFollowingNewEntries()
    {
        var (root, log, data) = Create(); log.Scroll.SetScrollY(0);
        data.Lines = ["Remaining"]; data.First = 100; root.Tick(.016, 2);
        Assert.False(log.FollowingEnd);
        data.Lines = Enumerable.Range(0, 30).Select(i => $"New {i}").ToArray(); root.Tick(.016, 3);
        Assert.Equal(0, log.Scroll.ScrollY);
        Assert.False(log.FollowingEnd);
    }
    [Fact]
    public void ResizeRewrapsWhilePreservingReadingEntry()
    {
        var (root, log, data) = Create();
        data.Lines = Enumerable.Range(0, 30).Select(i => $"Long action {i} with a target name that needs wrapping on narrow windows").ToArray();
        root.Tick(.016, 2); log.Scroll.SetScrollY(10 * log.Scroll.LineHeight);
        long? entry = log.FirstVisibleEntry;
        log.Width = 160; log.Height = 70; root.Tick(.016, 3);
        Assert.Equal(entry, log.FirstVisibleEntry);
        Assert.False(log.FollowingEnd);
    }
}

