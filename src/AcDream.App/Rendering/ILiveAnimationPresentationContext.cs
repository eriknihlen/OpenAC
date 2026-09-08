using AcDream.App.World;
using AcDream.Core.Physics;

namespace AcDream.App.Rendering;

internal interface ILiveAnimationPresentationContext
{
    uint LocalPlayerGuid { get; }
    MotionInterpreter? ResolveMotionInterpreter(LiveEntityRecord record);
}
