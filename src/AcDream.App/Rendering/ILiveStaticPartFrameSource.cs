using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal interface ILiveStaticPartFrameSource
{
    bool TryTakeLivePartFrames(
        LiveEntityRecord record,
        WorldEntity entity,
        LiveEntityAnimationState animation,
        ulong objectClockEpoch,
        ulong projectionMutationVersion,
        ulong presentationRevision,
        out IReadOnlyList<PartTransform> frames);
}
