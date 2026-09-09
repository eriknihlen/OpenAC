using DatReaderWriter;
using DatReaderWriter.DBObjs;

namespace AcDream.Content;

public static class RetailDataIdResolver
{
    public static uint Resolve(IDatReaderWriter dats, uint enumValue, uint enumCategory)
    {
        ArgumentNullException.ThrowIfNull(dats);

        uint masterDid = (uint)dats.Portal.Db.Header.MasterMapId;
        if (masterDid == 0
            || !dats.Portal.TryGet<EnumIDMap>(masterDid, out var master)
            || master is null
            || !master.ClientEnumToID.TryGetValue(enumCategory, out uint subMapDid)
            || !dats.Portal.TryGet<EnumIDMap>(subMapDid, out var subMap)
            || subMap is null
            || !subMap.ClientEnumToID.TryGetValue(enumValue, out uint did))
            return 0u;

        return did;
    }
}
