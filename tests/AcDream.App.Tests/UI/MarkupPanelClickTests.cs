using AcDream.App.UI;
using Xunit;

namespace AcDream.App.Tests.UI;

public class MarkupPanelClickTests
{
    private sealed class Binding
    {
        public int Clicks { get; private set; }
        public Action Go => () => Clicks++;
        public bool Shown { get; set; } = true;
        public string Status => "ok";
    }

    private const string Markup =
        "<panel x=\"40\" y=\"120\" w=\"360\" h=\"132\" title=\"MossTank\" visible=\"{Shown}\">"
        + "  <label x=\"12\" y=\"30\" text=\"{Status}\"/>"
        + "  <button x=\"12\" y=\"94\" w=\"108\" h=\"28\" text=\"Buff\" onclick=\"{Go}\"/>"
        + "</panel>";

    private static (UiRoot Root, Binding Bound) Mount()
    {
        var bound = new Binding();
        UiNineSlicePanel panel =
            MarkupDocument.Build(Markup, bound, _ => ((uint)1, 32, 32));
        var root = new UiRoot { Width = 1280, Height = 720 };
        root.AddChild(panel);
        return (root, bound);
    }

    private static (int X, int Y) ButtonCentre() => (40 + 12 + 54, 120 + 94 + 14);

    [Fact]
    public void ClickingTheButtonRunsTheBoundAction()
    {
        var (root, bound) = Mount();
        (int x, int y) = ButtonCentre();

        root.OnMouseDown(UiMouseButton.Left, x, y);
        root.OnMouseUp(UiMouseButton.Left, x, y);

        Assert.Equal(1, bound.Clicks);
    }

    [Fact]
    public void ThePointerFindsTheButtonAtAll()
    {
        var (root, _) = Mount();
        (int x, int y) = ButtonCentre();

        UiElement? hit = root.Pick(x, y);

        Assert.NotNull(hit);
        Assert.IsType<UiSimpleButton>(hit);
    }

    [Fact]
    public void ClickingOutsideTheButtonDoesNotRunTheAction()
    {
        var (root, bound) = Mount();

        root.OnMouseDown(UiMouseButton.Left, 900, 600);
        root.OnMouseUp(UiMouseButton.Left, 900, 600);

        Assert.Equal(0, bound.Clicks);
    }

    [Fact]
    public void AHiddenPanelSwallowsNothing()
    {
        var (root, bound) = Mount();
        bound.Shown = false;
        root.Tick(0.016, 0);            // visibility source is evaluated on tick
        (int x, int y) = ButtonCentre();

        root.OnMouseDown(UiMouseButton.Left, x, y);
        root.OnMouseUp(UiMouseButton.Left, x, y);

        Assert.Equal(0, bound.Clicks);
        Assert.Null(root.Pick(x, y));
    }
}
