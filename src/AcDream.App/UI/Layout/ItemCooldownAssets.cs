using AcDream.Content;
using DatReaderWriter;

namespace AcDream.App.UI.Layout;

public readonly record struct ItemCooldownAssets(IReadOnlyList<uint> Sprites)
{
    public const uint CatalogLayoutId = 0x21000037u;
    public const uint SharedItemPrototypeId = 0x1000033Eu;
    public const uint FirstOverlayElementId = 0x1000054Fu;
    public const int OverlayCount = 10;

    public static ItemCooldownAssets? TryLoad(IDatReaderWriter dats)
    {
        ArgumentNullException.ThrowIfNull(dats);
        ElementInfo? prototype = LayoutImporter.ImportInfos(
            dats,
            CatalogLayoutId,
            SharedItemPrototypeId);
        if (prototype is null)
            return null;

        var sprites = new uint[OverlayCount];
        for (int index = 0; index < sprites.Length; index++)
        {
            ElementInfo? overlay = Find(
                prototype,
                FirstOverlayElementId + (uint)index);
            if (overlay is null
                || !overlay.StateMedia.TryGetValue("", out var media)
                || media.File == 0u)
                return null;
            sprites[index] = media.File;
        }

        return new ItemCooldownAssets(sprites);
    }

    private static ElementInfo? Find(ElementInfo root, uint id)
    {
        if (root.Id == id)
            return root;
        foreach (ElementInfo child in root.Children)
            if (Find(child, id) is { } match)
                return match;
        return null;
    }
}
