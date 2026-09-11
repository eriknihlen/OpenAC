using System.Numerics;
using AcDream.Core.Physics.Motion;

namespace AcDream.Core.Physics;

public sealed class RemoteMotionCombiner
{
    public bool ComposeOffset(
        double dt,
        Vector3 currentBodyPosition,
        Quaternion ori,
        MotionDeltaFrame rootMotionLocalFrame,
        InterpolationManager interp,
        float maxSpeed,
        MotionDeltaFrame output,
        bool inContact = true,
        bool isSticky = false)
    {
        ArgumentNullException.ThrowIfNull(rootMotionLocalFrame);
        ArgumentNullException.ThrowIfNull(interp);
        ArgumentNullException.ThrowIfNull(output);

        output.Origin = rootMotionLocalFrame.Origin;
        output.Orientation = rootMotionLocalFrame.Orientation;
        bool interpolationOverwrote = interp.AdjustOffset(
            dt,
            currentBodyPosition,
            ori,
            maxSpeed,
            output,
            inContact,
            isSticky);

        return interpolationOverwrote;
    }

    public Vector3 ComputeOffset(
        double dt,
        Vector3 currentBodyPosition,
        Vector3 rootMotionLocalDelta,
        Quaternion ori,
        InterpolationManager interp,
        float maxSpeed)
    {
        var root = new MotionDeltaFrame
        {
            Origin = rootMotionLocalDelta,
        };
        var output = new MotionDeltaFrame();
        ComposeOffset(
            dt,
            currentBodyPosition,
            ori,
            root,
            interp,
            maxSpeed,
            output);
        return Vector3.Transform(output.Origin, ori);
    }
}
