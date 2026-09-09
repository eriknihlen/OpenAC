using System.Numerics;
using AcDream.Content;
using DatReaderWriter;

namespace AcDream.App.UI.Layout;

public readonly record struct SpellbookRowStyle(
    float Width,
    float Height,
    float IconLeft,
    float IconTop,
    float IconWidth,
    float IconHeight,
    float LabelLeft,
    float LabelWidth,
    uint FontDid,
    Vector4 LabelColor,
    uint BackgroundSprite,
    uint SelectedSprite)
{
    public const uint CatalogLayoutId = 0x21000037u;
    public const uint PrototypeId = 0x10000343u;
    public const uint SelectedOverlayId = 0x10000342u;
    public const uint LabelId = 0x10000344u;
    public const uint IconId = 0x1000033Bu;

    public static SpellbookRowStyle? TryLoad(IDatReaderWriter dats)
    {
        ArgumentNullException.ThrowIfNull(dats);
        ElementInfo? prototype = LayoutImporter.ImportInfos(dats, CatalogLayoutId, PrototypeId);
        if (prototype is null
            || Find(prototype, SelectedOverlayId) is not { } selected
            || Find(prototype, LabelId) is not { } label
            || Find(prototype, IconId) is not { } icon
            || !prototype.StateMedia.TryGetValue("", out var background)
            || !selected.StateMedia.TryGetValue("", out var selection)
            || prototype.Width <= 0f
            || prototype.Height <= 0f)
            return null;

        return new SpellbookRowStyle(
            prototype.Width,
            prototype.Height,
            icon.X,
            icon.Y,
            icon.Width,
            icon.Height,
            label.X,
            label.Width,
            label.FontDid,
            label.FontColor ?? Vector4.One,
            background.File,
            selection.File);
    }

    private static ElementInfo? Find(ElementInfo root, uint id)
    {
        if (root.Id == id) return root;
        foreach (ElementInfo child in root.Children)
            if (Find(child, id) is { } found) return found;
        return null;
    }
}
