using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public class UiNineSlicePanelTests
{
    [Fact]
    public void ComputeFrameRects_PlacesCornersEdgesAndCenter()
    {
        var r = UiNineSlicePanel.ComputeFrameRects(100, 80, 5);

        Assert.Equal(new UiNineSlicePanel.Rect(0,  0,  5, 5), r.TL);
        Assert.Equal(new UiNineSlicePanel.Rect(95, 0,  5, 5), r.TR);
        Assert.Equal(new UiNineSlicePanel.Rect(0,  75, 5, 5), r.BL);
        Assert.Equal(new UiNineSlicePanel.Rect(95, 75, 5, 5), r.BR);

        // edges span the interior (100-2*5 = 90 wide, 80-2*5 = 70 tall)
        Assert.Equal(new UiNineSlicePanel.Rect(5,  0,  90, 5),  r.Top);
        Assert.Equal(new UiNineSlicePanel.Rect(5,  75, 90, 5),  r.Bottom);
        Assert.Equal(new UiNineSlicePanel.Rect(0,  5,  5,  70), r.Left);
        Assert.Equal(new UiNineSlicePanel.Rect(95, 5,  5,  70), r.Right);

        Assert.Equal(new UiNineSlicePanel.Rect(5, 5, 90, 70), r.Center);
    }
}
