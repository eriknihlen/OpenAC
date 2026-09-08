using AcDream.Core.World;

namespace AcDream.App.World;

public interface ILiveEntitySpatialQuery
{
    void CopyLiveEntitiesNearLandblock(
        uint centerCellOrLandblockId,
        int landblockRadius,
        List<KeyValuePair<uint, WorldEntity>> destination);
}
