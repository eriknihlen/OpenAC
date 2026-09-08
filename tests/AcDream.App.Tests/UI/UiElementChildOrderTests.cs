using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public sealed class UiElementChildOrderTests
{
    [Fact]
    public void StableTree_ReusesBothTraversalSnapshots()
    {
        var parent = new TestElement();
        parent.AddChild(new TestElement { ZOrder = 2 });
        parent.AddChild(new TestElement { ZOrder = 1 });

        UiElement[] drawFirst = parent.ChildrenBackToFrontSnapshot();
        UiElement[] drawSecond = parent.ChildrenBackToFrontSnapshot();
        UiElement[] hitFirst = parent.ChildrenFrontToBackSnapshot();
        UiElement[] hitSecond = parent.ChildrenFrontToBackSnapshot();

        Assert.Same(drawFirst, drawSecond);
        Assert.Same(hitFirst, hitSecond);
        Assert.Equal([1, 2], drawFirst.Select(child => child.ZOrder).ToArray());
        Assert.Equal([2, 1], hitFirst.Select(child => child.ZOrder).ToArray());
    }

    [Fact]
    public void ChildZOrderChange_InvalidatesBothTraversalSnapshots()
    {
        var parent = new TestElement();
        var first = new TestElement { ZOrder = 1 };
        var second = new TestElement { ZOrder = 2 };
        parent.AddChild(first);
        parent.AddChild(second);
        UiElement[] oldDraw = parent.ChildrenBackToFrontSnapshot();
        UiElement[] oldHit = parent.ChildrenFrontToBackSnapshot();

        first.ZOrder = 3;
        UiElement[] newDraw = parent.ChildrenBackToFrontSnapshot();
        UiElement[] newHit = parent.ChildrenFrontToBackSnapshot();

        Assert.NotSame(oldDraw, newDraw);
        Assert.NotSame(oldHit, newHit);
        Assert.Same(second, newDraw[0]);
        Assert.Same(first, newDraw[1]);
        Assert.Same(first, newHit[0]);
        Assert.Same(second, newHit[1]);
    }

    [Fact]
    public void MembershipChange_InvalidatesSnapshotsWithoutMutatingPriorWalk()
    {
        var parent = new TestElement();
        var first = new TestElement { ZOrder = 1 };
        var second = new TestElement { ZOrder = 2 };
        parent.AddChild(first);
        UiElement[] prior = parent.ChildrenBackToFrontSnapshot();

        parent.AddChild(second);
        UiElement[] current = parent.ChildrenBackToFrontSnapshot();

        Assert.Single(prior);
        Assert.Same(first, prior[0]);
        Assert.Equal(2, current.Length);
        Assert.NotSame(prior, current);
    }

    private sealed class TestElement : UiElement;
}
