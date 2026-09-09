using AcDream.App.UI;
using Xunit;

namespace AcDream.App.Tests.UI;

public sealed class UiClipRectTests
{
    [Fact]
    public void TryClipSprite_cropsGeometryAndMatchingUvs()
    {
        var clip = new UiClipRect(0f, 0f, 100f, 100f);
        float x = -8f, y = 88f, w = 32f, h = 32f;
        float u0 = 0f, v0 = 0f, u1 = 1f, v1 = 1f;

        bool visible = UiClipRect.TryClipSprite(
            clip, ref x, ref y, ref w, ref h, ref u0, ref v0, ref u1, ref v1);

        Assert.True(visible);
        Assert.Equal((0f, 88f, 24f, 12f), (x, y, w, h));
        Assert.Equal((0.25f, 0f, 1f, 0.375f), (u0, v0, u1, v1));
    }

    [Fact]
    public void Intersection_andFullyOutsideSprite_areEmpty()
    {
        UiClipRect intersection = UiClipRect.Intersect(
            new UiClipRect(0f, 0f, 20f, 20f),
            new UiClipRect(30f, 30f, 40f, 40f));
        float x = 0f, y = 0f, w = 10f, h = 10f;
        float u0 = 0f, v0 = 0f, u1 = 1f, v1 = 1f;

        Assert.True(intersection.IsEmpty);
        Assert.False(UiClipRect.TryClipSprite(
            intersection, ref x, ref y, ref w, ref h, ref u0, ref v0, ref u1, ref v1));
    }
}
