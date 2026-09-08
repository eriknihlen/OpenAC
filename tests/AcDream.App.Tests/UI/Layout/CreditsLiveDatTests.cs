using AcDream.App.UI.Layout;
using AcDream.App.UI;
using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class CreditsLiveDatTests
{
    [InstalledDatFact]
    public void Category4_ResolvesAuthoredPictureAndTextRoots()
    {
        string datDirectory = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDirectory, DatAccessType.Read);

        uint pictureLayout = RetailDataIdResolver.Resolve(dats, 0x10000004u, 5u);
        Assert.Equal(0x21000003u, pictureLayout);

        ElementInfo picture = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(dats, pictureLayout, 0x10000413u));
        ElementInfo text = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(dats, pictureLayout, 0x10000410u));

        Assert.Equal(CreditsUiController.PictureRootElementId, picture.Id);
        Assert.Equal(3u, picture.Type);
        Assert.Equal((0f, 0f, 400f, 600f),
            (picture.X, picture.Y, picture.Width, picture.Height));
        Assert.Equal(CreditsUiController.TextRootElementId, text.Id);
        Assert.Equal(3u, text.Type);
        Assert.Equal((400f, 0f, 400f, 600f),
            (text.X, text.Y, text.Width, text.Height));

        Assert.True(TryDataId(text, 0x10000002u, out uint textAreaId));
        Assert.Equal(CreditsUiController.TextAreaElementId, textAreaId);
        Assert.True(TryDataId(text, 0x10000003u, out uint stringTableId));
        Assert.Equal(0x23000008u, stringTableId);
        Assert.True(text.TryGetEffectiveFloat(0x10000004u, out float seconds));
        Assert.Equal(20f, seconds);

        ElementInfo textArea = Assert.Single(text.Children);
        Assert.Equal(CreditsUiController.TextAreaElementId, textArea.Id);
        Assert.Equal(12u, textArea.Type);
        Assert.Equal(0x40000000u, textArea.FontDid);
        Assert.Equal(HJustify.Center, textArea.HJustify);
        Assert.Equal(VJustify.Center, textArea.VJustify);

        Assert.True(picture.TryGetEffectiveProperty(
            0x10000005u,
            out UiPropertyValue pictureArray));
        Assert.Equal(UiPropertyKind.Array, pictureArray.Kind);
        Assert.Equal(
            Enumerable.Range(0, 7).Select(index => 0x06005F14u + (uint)index),
            pictureArray.ArrayValue.Select(static value => (uint)value.UnsignedValue));

        ImportedLayout builtText = Assert.IsType<ImportedLayout>(
            LayoutImporter.Import(
                dats,
                pictureLayout,
                CreditsUiController.TextRootElementId,
                static id => (id, 1, 1),
                null));
        Assert.IsType<UiText>(builtText.FindElement(
            CreditsUiController.TextAreaElementId));

        var strings = new DatStringResolver(dats);
        int creditsCount = 0;
        for (int i = 1; i <= 4096; i++)
        {
            string? value = strings.Resolve(
                stringTableId,
                DatStringResolver.ComputeHash($"ID_Credits{i}"));
            if (value is null)
                break;
            creditsCount++;
        }
        Assert.Equal(2345, creditsCount);
        Assert.NotNull(strings.Resolve(
            0x23000001u,
            DatStringResolver.ComputeHash("ID_Wait_PleaseWait")));
    }

    private static bool TryDataId(
        ElementInfo info,
        uint propertyId,
        out uint value)
    {
        if (info.TryGetEffectiveProperty(propertyId, out UiPropertyValue property)
            && property.Kind is UiPropertyKind.DataId or UiPropertyKind.Enum)
        {
            value = checked((uint)property.UnsignedValue);
            return true;
        }
        value = 0u;
        return false;
    }
}
