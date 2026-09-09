using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI;

public class LayoutImporterMountTests
{
    [Fact]
    public void Mounts_ChildlessMediaLessInheritor_WithContentfulBase()
        => Assert.True(LayoutImporter.ShouldMountBaseChildren(derivedChildCount: 0, derivedMediaCount: 0, baseChildCount: 5));

    [Fact]
    public void DoesNotMount_WhenDerivedHasOwnMedia()
        => Assert.False(LayoutImporter.ShouldMountBaseChildren(0, 1, 5));

    [Fact]
    public void DoesNotMount_WhenDerivedHasOwnChildren()
        => Assert.False(LayoutImporter.ShouldMountBaseChildren(2, 0, 5));

    [Fact]
    public void DoesNotMount_WhenBaseIsChildless()
        => Assert.False(LayoutImporter.ShouldMountBaseChildren(0, 0, 0));
}
