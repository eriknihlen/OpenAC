using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class RowTemplateResolverTests
{
    [Fact]
    public void Resolve_CachesTheImport_ButBuildsAFreshWidgetEveryCall()
    {
        int importCalls = 0;
        int buildCalls = 0;
        var resolver = new RowTemplateResolver(
            importInfos: (layoutId, elementId) =>
            {
                importCalls++;
                return new ElementInfo { Id = elementId, Type = 0xCu };
            },
            build: info =>
            {
                buildCalls++;
                return new UiText();
            });

        UiElement? first = resolver.Resolve(0x21000030u, 0x10000281u);
        UiElement? second = resolver.Resolve(0x21000030u, 0x10000281u);
        UiElement? third = resolver.Resolve(0x21000030u, 0x10000281u);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        // The expensive DAT tree walk (ImportInfos) ran exactly once...
        Assert.Equal(1, importCalls);
        Assert.Equal(1, resolver.ImportCount);
        // ...but every row still gets its OWN built widget instance (a
        // shared instance would mean every fellow's row points at the same
        // UiElement, breaking per-row state like OnClick/LinesProvider).
        Assert.Equal(3, buildCalls);
        Assert.NotSame(first, second);
        Assert.NotSame(second, third);
    }

    [Fact]
    public void Resolve_DifferentTemplatePairs_ImportIndependently()
    {
        int importCalls = 0;
        var resolver = new RowTemplateResolver(
            importInfos: (layoutId, elementId) =>
            {
                importCalls++;
                return new ElementInfo { Id = elementId, Type = 0xCu };
            },
            build: _ => new UiText());

        // Fellowship's own template pair, and a DIFFERENT one (Friends'),
        // through the SAME resolver instance — matches production, where
        // MountSocialPanel feeds all three row families through one
        // RowTemplateResolver.
        resolver.Resolve(0x21000030u, 0x10000281u);
        resolver.Resolve(0x2100005Du, 0x10000519u);
        resolver.Resolve(0x21000030u, 0x10000281u);
        resolver.Resolve(0x2100005Du, 0x10000519u);

        Assert.Equal(2, importCalls);
    }

    [Fact]
    public void Resolve_ImportMiss_CachesTheNull_AndNeverRetriesTheImport()
    {
        int importCalls = 0;
        var resolver = new RowTemplateResolver(
            importInfos: (_, _) => { importCalls++; return null; },
            build: _ => throw new InvalidOperationException("build must not run for a null import"));

        UiElement? first = resolver.Resolve(0x21000030u, 0x10000281u);
        UiElement? second = resolver.Resolve(0x21000030u, 0x10000281u);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(1, importCalls);
    }
}
