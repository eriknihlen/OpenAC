using System.Numerics;

namespace AcDream.Core.Vfx;

public interface IEntityEffectPoseSource
{
    bool TryGetRootPose(uint localEntityId, out Matrix4x4 rootWorld);

    bool TryGetPartPose(uint localEntityId, int partIndex, out Matrix4x4 partLocal);
}

public interface IEntityEffectPoseChangeSource
{
    event Action<uint>? EffectPoseChanged;
}

public interface IEntityEffectCellSource
{
    bool TryGetCellId(uint localEntityId, out uint cellId);
}
