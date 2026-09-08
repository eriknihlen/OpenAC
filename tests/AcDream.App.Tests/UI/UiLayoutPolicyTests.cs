using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public sealed class UiLayoutPolicyTests
{
    private static readonly UiPixelRect OriginalParent =
        UiPixelRect.FromPositionAndSize(0, 0, 800, 600);
    private static readonly UiPixelRect OriginalChild =
        UiPixelRect.FromPositionAndSize(100, 50, 200, 100);

    [Fact]
    public void ModeZero_PreservesEveryCurrentEdge()
    {
        var current = new UiPixelRect(111, 66, 333, 177);
        var resizedParent = UiPixelRect.FromPositionAndSize(0, 0, 1600, 900);

        var result = UiLayoutPolicy.Apply(
            0, 0, 0, 0,
            OriginalChild,
            OriginalParent,
            current,
            resizedParent);

        Assert.Equal(current, result);
    }

    [Fact]
    public void ModeOne_KeepsNearEdgesAndAddsParentDeltaToFarEdges()
    {
        var resizedParent = UiPixelRect.FromPositionAndSize(0, 0, 1000, 700);

        var result = UiLayoutPolicy.Apply(
            1, 1, 1, 1,
            OriginalChild,
            OriginalParent,
            OriginalChild,
            resizedParent);

        Assert.Equal(new UiPixelRect(100, 50, 499, 249), result);
        Assert.Equal(400, result.Width);
        Assert.Equal(200, result.Height);
    }

    [Fact]
    public void MixedModeTwoAndOne_MovesFixedSizeChildWithFarSide()
    {
        var resizedParent = UiPixelRect.FromPositionAndSize(0, 0, 1000, 700);

        var result = UiLayoutPolicy.Apply(
            2, 2, 1, 1,
            OriginalChild,
            OriginalParent,
            OriginalChild,
            resizedParent);

        Assert.Equal(new UiPixelRect(300, 150, 499, 249), result);
        Assert.Equal(OriginalChild.Width, result.Width);
        Assert.Equal(OriginalChild.Height, result.Height);
    }

    [Fact]
    public void ModeThree_UsesInclusivePixelCenterFormula()
    {
        var resizedParent = UiPixelRect.FromPositionAndSize(0, 0, 1001, 701);

        var result = UiLayoutPolicy.Apply(
            3, 3, 3, 3,
            OriginalChild,
            OriginalParent,
            OriginalChild,
            resizedParent);

        Assert.Equal(new UiPixelRect(400, 300, 599, 399), result);
    }

    [Fact]
    public void ModeFour_ScalesEachCoordinateWithTruncationTowardZero()
    {
        var originalParent = UiPixelRect.FromPositionAndSize(0, 0, 10, 10);
        var originalChild = new UiPixelRect(-3, -3, -1, -1);
        var resizedParent = UiPixelRect.FromPositionAndSize(0, 0, 15, 15);

        var result = UiLayoutPolicy.Apply(
            4, 4, 4, 4,
            originalChild,
            originalParent,
            originalChild,
            resizedParent);

        Assert.Equal(new UiPixelRect(-4, -4, -1, -1), result);
    }

    [Fact]
    public void AssigningCompatibilityAnchors_OptsOutOfImportedPolicy()
    {
        var element = new UiPanel
        {
            LayoutPolicy = new UiLayoutPolicy(
                1, 1, 1, 1,
                OriginalChild,
                OriginalParent),
        };

        element.Anchors = AnchorEdges.Left | AnchorEdges.Top;

        Assert.Null(element.LayoutPolicy);
    }

    [Fact]
    public void ApplyAnchor_UsesImportedPolicyInsteadOfCompatibilityMargins()
    {
        var element = new UiPanel
        {
            Left = 100,
            Top = 50,
            Width = 200,
            Height = 100,
            LayoutPolicy = new UiLayoutPolicy(
                1, 1, 1, 1,
                OriginalChild,
                OriginalParent),
        };

        element.ApplyAnchor(1000, 700);

        Assert.Equal(100f, element.Left);
        Assert.Equal(50f, element.Top);
        Assert.Equal(400f, element.Width);
        Assert.Equal(200f, element.Height);
    }

    [Fact]
    public void ResetAnchorCapture_RebasesImportedPolicyAfterControllerResize()
    {
        var parent = new UiPanel { Width = 490, Height = 17 };
        var child = new UiPanel
        {
            Left = 0,
            Top = 0,
            Width = 46,
            Height = 17,
            LayoutPolicy = new UiLayoutPolicy(
                1, 1, 2, 1,
                UiPixelRect.FromPositionAndSize(0, 0, 46, 17),
                UiPixelRect.FromPositionAndSize(0, 0, 490, 17)),
        };
        parent.AddChild(child);
        child.Width = 72;

        child.ResetAnchorCapture();
        child.ApplyAnchor(parent.Width, parent.Height);

        Assert.Equal(72f, child.Width);
    }

    [Fact]
    public void RebaseChildLayoutBaselines_CroppedChatRegionThenGrowsWithFrame()
    {
        var root = new UiPanel { Width = 490, Height = 100 };
        var transcriptPanel = new UiPanel
        {
            Width = 490,
            Height = 83,
            LayoutPolicy = new UiLayoutPolicy(
                1, 1, 1, 1,
                UiPixelRect.FromPositionAndSize(0, 0, 490, 83),
                UiPixelRect.FromPositionAndSize(0, 0, 800, 100)),
        };
        root.AddChild(transcriptPanel);

        root.RebaseChildLayoutBaselines();
        root.Width = 615;
        transcriptPanel.ApplyAnchor(root.Width, root.Height);

        Assert.Equal(615f, transcriptPanel.Width);
    }
}
