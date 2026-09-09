using System.Linq;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using Xunit;

namespace AcDream.App.Tests.UI;

public sealed class UiMarkupListScrollSelectionPersistenceTests
{
    private sealed class TestElement : UiElement { }

    private const float ListWidth = 100f;
    private const float ListHeight = 100f; // RowHeight 10 => 10 visible rows
    private const float RowHeight = 10f;
    private const int RowCount = 40;

    // Non-zero nesting offsets at every level (panel > group > list), mirroring
    // MossTank's own panel(28,42) > group(8,42) > list(4,24) structure.
    private const float PanelLeft = 50f, PanelTop = 60f;
    private const float GroupLeft = 10f, GroupTop = 20f;
    private const float ListLeft = 5f, ListTop = 15f;

    private static (uint tex, int w, int h) Resolve(uint id) => (id, 16, 16);

    private sealed class Harness
    {
        public readonly UiRoot Root = new() { Width = 1000f, Height = 800f };
        public readonly TestElement Panel;
        public readonly TestElement Group;
        public readonly UiMarkupList List;

        public Harness(UiMarkupList list)
        {
            List = list;
            Panel = new TestElement
            {
                Left = PanelLeft, Top = PanelTop, Width = 900f, Height = 700f,
                Draggable = true,
            };
            Group = new TestElement
            {
                Left = GroupLeft, Top = GroupTop, Width = 800f, Height = 600f,
            };
            List.Left = ListLeft; List.Top = ListTop;
            Group.AddChild(List);
            Panel.AddChild(Group);
            Root.AddChild(Panel);
        }

        public (int x, int y) ToScreen(float localX, float localY) =>
            ((int)(PanelLeft + GroupLeft + ListLeft + localX),
             (int)(PanelTop + GroupTop + ListTop + localY));

        public void Click(float localX, float localY)
        {
            var (x, y) = ToScreen(localX, localY);
            Root.OnMouseDown(UiMouseButton.Left, x, y);
            Root.OnMouseUp(UiMouseButton.Left, x, y);
        }

        public void PressMoveRelease(float downLocalX, float downLocalY, float moveLocalX, float moveLocalY)
        {
            var (dx, dy) = ToScreen(downLocalX, downLocalY);
            Root.OnMouseDown(UiMouseButton.Left, dx, dy);
            var (mx, my) = ToScreen(moveLocalX, moveLocalY);
            Root.OnMouseMove(mx, my);
            Root.OnMouseUp(UiMouseButton.Left, mx, my);
        }
    }


    [Fact]
    public void SingleColumn_DownArrowClick_TopRowSurvivesTheNextDraw()
    {
        var list = new UiMarkupList
        {
            Width = ListWidth, Height = ListHeight, RowHeight = RowHeight,
            SpriteResolve = Resolve,
            // A REALISTIC stable selection (unlike the -1 the existing
            // scrollbar tests use) — the user has row 0 selected and is not
            // touching selection while scrolling elsewhere via the bar.
            SelectedIndexSource = () => 0,
            ItemsSource = () => Enumerable.Range(0, RowCount).Select(i => $"row{i}").ToArray(),
        };
        var h = new Harness(list);
        h.Root.DrawSelfAndChildren(NullCtx());

        h.Click(localX: 90, localY: 90);

        h.Root.DrawSelfAndChildren(NullCtx());

        int? selected = null;
        list.SelectionChanged = row => selected = row;
        h.Click(localX: 10, localY: 2);

        Assert.Equal(1, selected);
    }

    [Fact]
    public void SingleColumn_ThumbDrag_TopRowAdvancesThreeRows_AndSurvivesTheNextDraw()
    {
        var list = new UiMarkupList
        {
            Width = ListWidth, Height = ListHeight, RowHeight = RowHeight,
            SpriteResolve = Resolve,
            SelectedIndexSource = () => 0,
            ItemsSource = () => Enumerable.Range(0, RowCount).Select(i => $"row{i}").ToArray(),
        };
        var h = new Harness(list);
        h.Root.DrawSelfAndChildren(NullCtx());

        h.PressMoveRelease(downLocalX: 90, downLocalY: 16, moveLocalX: 90, moveLocalY: 21);

        h.Root.DrawSelfAndChildren(NullCtx());

        int? selected = null;
        list.SelectionChanged = row => selected = row;
        h.Click(localX: 10, localY: 2);

        Assert.Equal(3, selected);
    }


    [Fact]
    public void Columns_DownArrowClick_TopRowSurvivesTheNextDraw()
    {
        var list = new UiMarkupList
        {
            Width = ListWidth, Height = ListHeight, RowHeight = RowHeight,
            SpriteResolve = Resolve,
            SelectedIndexSource = () => 0,
            Columns = new[]
            {
                UiMarkupListColumn.Text(
                    ListWidth, () => Enumerable.Range(0, RowCount).Select(i => $"row{i}").ToArray(), null),
            },
        };
        var h = new Harness(list);
        h.Root.DrawSelfAndChildren(NullCtx());

        h.Click(localX: 90, localY: 90);
        h.Root.DrawSelfAndChildren(NullCtx());

        int? selected = null;
        list.SelectionChanged = row => selected = row;
        h.Click(localX: 10, localY: 2);

        Assert.Equal(1, selected);
    }

    [Fact]
    public void Columns_ThumbDrag_TopRowAdvancesThreeRows_AndSurvivesTheNextDraw()
    {
        var list = new UiMarkupList
        {
            Width = ListWidth, Height = ListHeight, RowHeight = RowHeight,
            SpriteResolve = Resolve,
            SelectedIndexSource = () => 0,
            Columns = new[]
            {
                UiMarkupListColumn.Text(
                    ListWidth, () => Enumerable.Range(0, RowCount).Select(i => $"row{i}").ToArray(), null),
            },
        };
        var h = new Harness(list);
        h.Root.DrawSelfAndChildren(NullCtx());

        h.PressMoveRelease(downLocalX: 90, downLocalY: 16, moveLocalX: 90, moveLocalY: 21);
        h.Root.DrawSelfAndChildren(NullCtx());

        int? selected = null;
        list.SelectionChanged = row => selected = row;
        h.Click(localX: 10, localY: 2);

        Assert.Equal(3, selected);
    }

    private static UiRenderContext NullCtx()
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(1000f, 800f));
        return new UiRenderContext(renderer, new Vector2(1000f, 800f));
    }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }
}
