using AcDream.App.UI.Layout;
using DatReaderWriter;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class TooltipSkinLiveDatTests
{
    private static string DatDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");

    [InstalledDatFact]
    public void EveryPopupSkinTextChildAuthorsTheRetailWrapBound()
    {
        using var dats = new DatCollection(
            DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        ElementInfo? tree = LayoutImporter.ImportInfos(dats, 0x21000041u);
        Assert.NotNull(tree);

        int textChildren = 0;
        void Walk(ElementInfo e, uint parentId)
        {
            if (e.Id == 0x10000396u && e.Type == 12)
            {
                textChildren++;
                Assert.Equal(256, e.MaxWidth);
                Assert.Equal(2, e.MarginLeft);
                Assert.Equal(2, e.MarginRight);
                int vertical = parentId == 0x10000398u ? 0 : 2;
                Assert.Equal(vertical, e.MarginTop);
                Assert.Equal(vertical, e.MarginBottom);
            }
            foreach (var c in e.Children) Walk(c, e.Id);
        }
        Walk(tree!, 0);
        Assert.True(textChildren >= 4,
            $"expected the text child under all four popup skins, found {textChildren}");
    }
}
