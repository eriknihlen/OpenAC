using AcDream.Core.World;

namespace AcDream.App.World;

public interface ILiveEntityRadarSource
{
    bool TryGetMaterialized(uint serverGuid, out WorldEntity entity);
    bool TryGetVisible(uint serverGuid, out WorldEntity entity);
    void CopyVisibleTo(List<KeyValuePair<uint, WorldEntity>> destination);
}
