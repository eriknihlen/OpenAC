using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class UiTextLayoutCacheTests
{
    [Fact]
    public void Stable_content_and_layout_borrow_same_shaped_lines()
    {
        var text = new UiText { Width = 120f };
        int shapes = 0;
        var cache = new UiTextLayoutCache<string>(
            text,
            (target, value) =>
            {
                shapes++;
                return IndicatorDetailText.Shape(target, value);
            },
            "alpha beta gamma",
            StringComparer.Ordinal);

        IReadOnlyList<UiText.Line> first = cache.GetLines();
        IReadOnlyList<UiText.Line> second = cache.GetLines();

        Assert.Same(first, second);
        Assert.Equal(1, shapes);

        cache.SetValue(new string("alpha beta gamma".ToCharArray()));
        Assert.Same(first, cache.GetLines());
        Assert.Equal(1, shapes);
    }

    [Fact]
    public void Content_width_and_font_inputs_invalidate_shaping()
    {
        var text = new UiText { Width = 120f };
        int shapes = 0;
        var cache = new UiTextLayoutCache<string>(
            text,
            (target, value) =>
            {
                shapes++;
                return IndicatorDetailText.Shape(target, value);
            },
            "alpha beta gamma",
            StringComparer.Ordinal);

        IReadOnlyList<UiText.Line> first = cache.GetLines();
        cache.SetValue("delta epsilon");
        IReadOnlyList<UiText.Line> changed = cache.GetLines();
        text.Width = 48f;
        IReadOnlyList<UiText.Line> resized = cache.GetLines();

        Assert.NotSame(first, changed);
        Assert.NotSame(changed, resized);
        Assert.Equal(3, shapes);
    }

    [Fact]
    public void Stable_provider_poll_allocates_no_managed_memory()
    {
        var text = new UiText { Width = 120f };
        var cache = new UiTextLayoutCache<string>(
            text,
            static (target, value) => IndicatorDetailText.Shape(target, value),
            "alpha beta gamma",
            StringComparer.Ordinal);
        Func<IReadOnlyList<UiText.Line>> provider = cache.Provider;
        _ = provider();

        ZeroAllocationProbe.AssertAllocatesNothing(
            "UiTextLayoutCache stable provider poll",
            () => _ = provider());
    }
}
