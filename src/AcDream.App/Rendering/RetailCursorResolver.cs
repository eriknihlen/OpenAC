using AcDream.App.UI;
using DatReaderWriter;
using AcDream.Content;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Rendering;

internal sealed class RetailCursorResolver
{
    private readonly IDatReaderWriter _dats;
    private readonly object _datLock;
    private readonly Dictionary<uint, uint> _didByEnum = new();
    private readonly HashSet<uint> _missingEnums = new();

    public RetailCursorResolver(IDatReaderWriter dats, object datLock)
    {
        _dats = dats;
        _datLock = datLock;
    }

    public bool TryResolve(RetailGlobalCursorKind kind, out UiCursorMedia cursor)
    {
        cursor = default;
        if (!RetailCursorCatalog.TryGetGlobalCursor(kind, out var spec))
            return false;

        return TryResolve(spec, out cursor);
    }

    internal bool TryResolve(RetailCursorSpec spec, out UiCursorMedia cursor)
    {
        cursor = default;
        if (!spec.IsValid)
            return false;

        if (_didByEnum.TryGetValue(spec.EnumId, out uint cachedDid))
        {
            cursor = new UiCursorMedia(cachedDid, spec.HotspotX, spec.HotspotY);
            return true;
        }
        if (_missingEnums.Contains(spec.EnumId))
            return false;

        uint did = ResolveDidByEnum(spec.EnumId);
        if (did == 0)
        {
            _missingEnums.Add(spec.EnumId);
            return false;
        }

        cursor = new UiCursorMedia(did, spec.HotspotX, spec.HotspotY);
        _didByEnum[spec.EnumId] = did;
        return true;
    }

    private uint ResolveDidByEnum(uint cursorEnum)
    {
        lock (_datLock)
        {
            uint masterDid = (uint)_dats.Portal.Db.Header.MasterMapId;
            if (masterDid == 0)
                return 0;

            if (!_dats.Portal.TryGet<EnumIDMap>(masterDid, out var master) || master is null)
                return 0;

            if (!master.ClientEnumToID.TryGetValue(RetailCursorCatalog.CursorEnumTable, out uint cursorMapDid))
                return 0;

            if (!_dats.Portal.TryGet<EnumIDMap>(cursorMapDid, out var cursorMap) || cursorMap is null)
                return 0;

            return cursorMap.ClientEnumToID.TryGetValue(cursorEnum, out uint did) ? did : 0;
        }
    }
}
