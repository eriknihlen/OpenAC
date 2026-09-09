using DatReaderWriter;
using DatReaderWriter.DBObjs;
using AcDream.Core.Content;
using DatReaderWriter.Enums;

namespace AcDream.Core.Meshing;

public static class GfxObjDegradeResolver
{
    public static bool TryResolveCloseGfxObj(
        IDatObjectSource dats,
        uint gfxObjId,
        out uint resolvedId,
        out GfxObj? resolvedGfxObj)
        => TryResolveCloseGfxObj(
            id => dats.Get<GfxObj>(id),
            id => dats.Get<GfxObjDegradeInfo>(id),
            gfxObjId,
            out resolvedId,
            out resolvedGfxObj);

    public static bool TryResolveCloseGfxObj(
        Func<uint, GfxObj?> getGfxObj,
        Func<uint, GfxObjDegradeInfo?> getDegradeInfo,
        uint gfxObjId,
        out uint resolvedId,
        out GfxObj? resolvedGfxObj)
    {
        var gfxObj = getGfxObj(gfxObjId);
        if (gfxObj is null)
        {
            resolvedId = gfxObjId;
            resolvedGfxObj = null;
            return false;
        }

        resolvedId = gfxObjId;
        resolvedGfxObj = gfxObj;

        if (!gfxObj.Flags.HasFlag(GfxObjFlags.HasDIDDegrade) || gfxObj.DIDDegrade == 0)
            return true;

        var degradeInfo = getDegradeInfo(gfxObj.DIDDegrade);
        if (degradeInfo is null || degradeInfo.Degrades.Count == 0)
            return true;

        uint closeId = (uint)degradeInfo.Degrades[0].Id;
        if (closeId == 0)
            return true;

        var closeGfxObj = getGfxObj(closeId);
        if (closeGfxObj is null)
            return true;

        resolvedId = closeId;
        resolvedGfxObj = closeGfxObj;
        return true;
    }

    public static bool IsRuntimeHiddenMarker(IDatObjectSource dats, uint gfxObjId)
        => IsRuntimeHiddenMarker(
            id => dats.Get<GfxObj>(id),
            id => dats.Get<GfxObjDegradeInfo>(id),
            gfxObjId);

    public static bool IsRuntimeHiddenMarker(
        Func<uint, GfxObj?> getGfxObj,
        Func<uint, GfxObjDegradeInfo?> getDegradeInfo,
        uint gfxObjId)
    {
        var gfxObj = getGfxObj(gfxObjId);
        if (gfxObj is null
            || !gfxObj.Flags.HasFlag(GfxObjFlags.HasDIDDegrade)
            || gfxObj.DIDDegrade == 0)
            return false;

        var info = getDegradeInfo(gfxObj.DIDDegrade);
        if (info is null || info.Degrades.Count == 0)
            return false;

        bool firstSlotEditorOnly = info.Degrades[0].MaxDist == 0f;
        if (!firstSlotEditorOnly)
            return false;

        foreach (var d in info.Degrades)
            if ((uint)d.Id == 0u)
                return true;
        return false;
    }
}
