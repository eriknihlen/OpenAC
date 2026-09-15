using AcDream.Content;
using DatReaderWriter.DBObjs;

namespace AcDream.App.UI.Layout;

internal static class ChatFontResolver
{
    private const uint InterfaceCategoryKey = 2u;
    private const uint ChatFontMapKey = 0x10000001u;

    private static readonly string[] Faces =
        { "Arial", "CourierNew", "PalatinoLinotype", "Tahoma", "TimesNewRoman" };

    private static readonly string[] Sizes =
        { "Tiny", "Small", "Medium", "Large", "XL" };

    internal static bool TryResolveFontId(
        IDatReaderWriter dats, int faceIndex, int sizeIndex, out uint fontDid)
    {
        ArgumentNullException.ThrowIfNull(dats);
        fontDid = 0;

        uint masterDid = (uint)dats.Portal.Db.Header.MasterMapId;
        if (masterDid == 0
            || !dats.Portal.TryGet<EnumIDMap>(masterDid, out var master)
            || master is null
            || !master.ClientEnumToID.TryGetValue(InterfaceCategoryKey, out uint categoryDid)
            || !dats.Portal.TryGet<EnumIDMap>(categoryDid, out var category)
            || category is null
            || !category.ClientEnumToID.TryGetValue(ChatFontMapKey, out uint fontMapDid)
            || !dats.Portal.TryGet<EnumIDMap>(fontMapDid, out var fontMap)
            || fontMap is null)
            return false;

        return TryResolveFontId(fontMap, faceIndex, sizeIndex, out fontDid);
    }

    internal static bool TryResolveFontId(
        EnumIDMap fontMap, int faceIndex, int sizeIndex, out uint fontDid)
    {
        ArgumentNullException.ThrowIfNull(fontMap);
        fontDid = 0;
        if ((uint)faceIndex >= (uint)Faces.Length || (uint)sizeIndex >= (uint)Sizes.Length)
            return false;

        string name = $"Chat_{Faces[faceIndex]}_{Sizes[sizeIndex]}";
        foreach ((uint enumValue, var enumName) in fontMap.ClientEnumToName)
        {
            if (enumName.Value != name) continue;
            return fontMap.ClientEnumToID.TryGetValue(enumValue, out fontDid);
        }
        return false;
    }
}
