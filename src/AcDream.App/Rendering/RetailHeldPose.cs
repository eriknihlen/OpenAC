using System.Numerics;
using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Rendering;

internal static class RetailHeldPose
{
    public static uint ResolvePoseDid(IDatReaderWriter dats, uint poseEnum)
    {
        uint masterDid = (uint)dats.Portal.Db.Header.MasterMapId;
        if (masterDid == 0
            || !dats.Portal.TryGet<EnumIDMap>(masterDid, out var master)
            || !master.ClientEnumToID.TryGetValue(7u, out uint subDid)
            || !dats.Portal.TryGet<EnumIDMap>(subDid, out var sub))
        {
            return 0u;
        }

        return sub.ClientEnumToID.TryGetValue(poseEnum, out uint did) ? did : 0u;
    }

    public static Matrix4x4 ComposePartTransform(Vector3 defaultScale, Vector3 origin, Quaternion orientation) =>
        Matrix4x4.CreateScale(defaultScale)
        * Matrix4x4.CreateFromQuaternion(orientation)
        * Matrix4x4.CreateTranslation(origin);
}
