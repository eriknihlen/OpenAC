using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Audio;

public static class UiSoundTableResolver
{
    /// <summary>The enum index the UI sound bank lives at.</summary>
    public const uint UiSoundTableEnumSlot = 7u;

    public const uint UiSoundTableTypeKey = 0x10000003u;

    public static uint Resolve(IDatReaderWriter dats)
    {
        if (dats is null)
            return 0u;

        uint masterDid = (uint)dats.Portal.Db.Header.MasterMapId;
        if (masterDid == 0)
            return 0u;

        if (!dats.Portal.TryGet<EnumIDMap>(masterDid, out var master) || master is null)
            return 0u;

        if (!master.ClientEnumToID.TryGetValue(UiSoundTableEnumSlot, out uint perSlotDid)
            || perSlotDid == 0)
        {
            return 0u;
        }

        if (!dats.Portal.TryGet<EnumIDMap>(perSlotDid, out var perSlot) || perSlot is null)
            return 0u;

        return perSlot.ClientEnumToID.TryGetValue(UiSoundTableTypeKey, out uint did) ? did : 0u;
    }
}
