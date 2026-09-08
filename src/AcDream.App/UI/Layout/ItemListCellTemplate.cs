using System.Collections.Generic;
using DatReaderWriter;
using AcDream.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.UI.Layout;

public static class ItemListCellTemplate
{
    public const uint CatalogLayoutId = 0x21000037u;

    private const uint CellTemplateAttr = 0x1000000eu;
    private const uint IconChildId = 0x1000033Bu;
    private const string ItemSlotEmpty = "ItemSlot_Empty";

    public static uint ResolveEmptySprite(IDatReaderWriter dats, uint listLayoutId, uint listElementId)
    {
        var listLd = dats.Get<LayoutDesc>(listLayoutId);
        if (listLd is null) return 0;
        var listElem = FindDesc(listLd, listElementId);
        if (listElem is null) return 0;

        uint protoId = ReadCellTemplateId(listElem);
        if (protoId == 0) return 0;

        return ResolvePrototypeEmptySprite(dats, protoId);
    }

    public static uint ResolveEmptySprite(
        IDatReaderWriter dats,
        ElementInfo resolvedRoot,
        uint listElementId)
    {
        ElementInfo? list = FindInfo(resolvedRoot, listElementId);
        if (list is null
            || !list.TryGetEffectiveProperty(CellTemplateAttr, out UiPropertyValue property)
            || property.Kind is not (UiPropertyKind.Enum or UiPropertyKind.DataId)
            || property.UnsignedValue is 0 or > uint.MaxValue)
            return 0;

        return ResolvePrototypeEmptySprite(dats, (uint)property.UnsignedValue);
    }

    public static uint ResolvePrototypeEmptySprite(IDatReaderWriter dats, uint prototypeElementId)
    {
        if (prototypeElementId == 0) return 0;

        var catalog = dats.Get<LayoutDesc>(CatalogLayoutId);
        if (catalog is null) return 0;
        var proto = FindDesc(catalog, prototypeElementId);
        if (proto is null) return 0;

        return FindIconEmpty(catalog, proto, new HashSet<uint>());
    }

    private static uint ReadCellTemplateId(ElementDesc elem)
    {
        uint id = ReadIdFromState(elem.StateDesc);
        if (id != 0) return id;
        foreach (var s in elem.States)
        {
            id = ReadIdFromState(s.Value);
            if (id != 0) return id;
        }
        return 0;
    }

    private static uint ReadIdFromState(StateDesc? sd)
    {
        if (sd?.Properties is null) return 0;
        return sd.Properties.TryGetValue(CellTemplateAttr, out var raw) ? ReadId(raw) : 0;
    }

    private static uint ReadId(BaseProperty raw)
    {
        if (raw is ArrayBaseProperty arr && arr.Value.Count > 0) return ReadId(arr.Value[0]);
        if (raw is DataIdBaseProperty did) return did.Value;
        if (raw is EnumBaseProperty ep) return ep.Value;
        return 0;
    }

    private static uint FindIconEmpty(LayoutDesc catalog, ElementDesc element, HashSet<uint> baseSeen)
    {
        if (element.ElementId == IconChildId)
        {
            uint e = IconEmptyState(element);
            if (e != 0) return e;
        }
        foreach (var kv in element.Children)
        {
            uint e = FindIconEmpty(catalog, kv.Value, baseSeen);
            if (e != 0) return e;
        }
        if (element.BaseElement != 0 && element.BaseLayoutId == CatalogLayoutId && baseSeen.Add(element.BaseElement))
        {
            var baseDesc = FindDesc(catalog, element.BaseElement);
            if (baseDesc is not null)
            {
                uint e = FindIconEmpty(catalog, baseDesc, baseSeen);
                if (e != 0) return e;
            }
        }
        return 0;
    }

    private static uint IconEmptyState(ElementDesc icon)
    {
        foreach (var s in icon.States)
            if (s.Key.ToString() == ItemSlotEmpty)
            {
                uint f = StateImage(s.Value);
                if (f != 0) return f;
            }
        return DirectStateImage(icon);
    }

    private static uint DirectStateImage(ElementDesc d)
        => d.StateDesc is null ? 0u : StateImage(d.StateDesc);

    private static uint StateImage(StateDesc sd)
    {
        foreach (var m in sd.Media)
            if (m is MediaDescImage img && img.File != 0) return img.File;
        return 0;
    }

    // ── depth-first element search by id (LayoutImporter.FindDesc is private there) ──
    private static ElementDesc? FindDesc(LayoutDesc ld, uint id)
    {
        foreach (var kv in ld.Elements)
        {
            var f = FindDescIn(kv.Value, id);
            if (f is not null) return f;
        }
        return null;
    }

    private static ElementDesc? FindDescIn(ElementDesc d, uint id)
    {
        if (d.ElementId == id) return d;
        foreach (var kv in d.Children)
        {
            var f = FindDescIn(kv.Value, id);
            if (f is not null) return f;
        }
        return null;
    }

    private static ElementInfo? FindInfo(ElementInfo element, uint id)
    {
        if (element.Id == id) return element;
        foreach (ElementInfo child in element.Children)
        {
            ElementInfo? found = FindInfo(child, id);
            if (found is not null) return found;
        }
        return null;
    }
}
