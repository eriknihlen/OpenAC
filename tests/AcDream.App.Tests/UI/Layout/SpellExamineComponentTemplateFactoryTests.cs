using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class SpellExamineComponentTemplateFactoryTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Create_InstallsIconOnRetailRootAndKeepsMissingOverlayAboveIt(
        bool owned,
        bool missingVisible)
    {
        ElementInfo template =
            FixtureLoader.LoadExaminationComponentTemplateInfos();
        var factory = new SpellExamineComponentTemplateFactory(
            template,
            did => (did + 1_000u, 32, 32),
            defaultFont: null);

        UiDatElement root = Assert.IsType<UiDatElement>(
            factory.Create(0xABCDu, owned));
        UiDatElement missing = Assert.IsType<UiDatElement>(
            Assert.Single(root.Children));

        Assert.Equal(SpellExamineComponentTemplateFactory.TemplateId, root.ElementId);
        Assert.Equal(0xABCDu, root.RuntimeImageTexture);
        Assert.Empty(root.Children.OfType<UiTextureElement>());
        Assert.Equal(
            SpellExamineComponentTemplateFactory.MissingOverlayId,
            missing.ElementId);
        Assert.Equal(missingVisible, missing.Visible);
    }
}
