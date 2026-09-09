using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.Runtime.Physics;

internal static class ShadowPositionSynchronizer
{
    public static void Sync(
        ShadowObjectRegistry registry,
        uint entityId,
        Vector3 position,
        Quaternion orientation,
        uint cellId,
        int liveCenterX,
        int liveCenterY)
    {
        if (cellId == 0)
            return;

        int landblockX = (int)((cellId >> 24) & 0xFFu);
        int landblockY = (int)((cellId >> 16) & 0xFFu);
        float worldOffsetX = (landblockX - liveCenterX) * 192f;
        float worldOffsetY = (landblockY - liveCenterY) * 192f;
        registry.UpdatePosition(
            entityId,
            position,
            orientation,
            worldOffsetX,
            worldOffsetY,
            cellId,
            seedCellId: cellId);
    }
}
