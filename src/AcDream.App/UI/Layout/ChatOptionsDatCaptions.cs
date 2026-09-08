using AcDream.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.UI.Layout;

public static class ChatOptionsDatCaptions
{
    private const uint EnumCategory = 2u;

    private const uint EnumValue = 0x15u;

    private const uint CatalogArrayPropertyId = 0xD2u;
    private const uint EntryNamePropertyId = 0xD4u;
    private const uint EntryTooltipPropertyId = 0xD5u;
    private const uint EntryOwningPropertyId = 0xD6u;

    public readonly record struct Caption(string? Name, string? Tooltip);

    public static bool TryRead(
        IDatReaderWriter dats,
        DatStringResolver strings,
        out Caption defaultOpacity,
        out Caption activeOpacity)
    {
        defaultOpacity = default;
        activeOpacity = default;

        uint did = RetailDataIdResolver.Resolve(dats, EnumValue, EnumCategory);
        if (did == 0u || !dats.Portal.TryGet<DBProperties>(did, out DBProperties? props) || props is null)
            return false;

        if (!props.Properties.TryGetValue(CatalogArrayPropertyId, out BaseProperty? arrayProp)
            || arrayProp is not ArrayBaseProperty array)
            return false;

        foreach (BaseProperty entry in array.Value)
        {
            if (entry is not StructBaseProperty entryStruct) continue;
            if (!entryStruct.Value.TryGetValue(EntryOwningPropertyId, out BaseProperty? owningProp)
                || owningProp is not EnumBaseProperty owningEnum)
                continue;

            if (owningEnum.Value == ChatOptionsDatDefaults.DefaultOpacityPropertyId)
                defaultOpacity = ReadCaption(entryStruct, strings);
            else if (owningEnum.Value == ChatOptionsDatDefaults.ActiveOpacityPropertyId)
                activeOpacity = ReadCaption(entryStruct, strings);
        }

        return true;
    }

    private static Caption ReadCaption(StructBaseProperty entry, DatStringResolver strings)
    {
        string? name = entry.Value.TryGetValue(EntryNamePropertyId, out BaseProperty? nameProp)
            && nameProp is StringInfoBaseProperty nameInfo
                ? strings.Resolve(nameInfo.Value.TableId.DataId, nameInfo.Value.StringId, nameInfo.Value.Token)
                : null;
        string? tooltip = entry.Value.TryGetValue(EntryTooltipPropertyId, out BaseProperty? tooltipProp)
            && tooltipProp is StringInfoBaseProperty tooltipInfo
                ? strings.Resolve(tooltipInfo.Value.TableId.DataId, tooltipInfo.Value.StringId, tooltipInfo.Value.Token)
                : null;
        return new Caption(name, tooltip);
    }
}
