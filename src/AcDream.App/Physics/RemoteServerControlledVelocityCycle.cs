using AcDream.App.Rendering;

namespace AcDream.App.Physics;

internal static class RemoteServerControlledVelocityCycle
{
    private static bool IsPlayerGuid(uint guid) =>
        (guid & 0xFF000000u) == 0x50000000u;
    public static void Apply(
        uint serverGuid,
        LiveEntityAnimationState ae,
        RemoteMotion rm,
        System.Numerics.Vector3 velocity)
    {
        if (rm.Airborne) return;
        if (ae.Sequencer is null) return;
        if (rm.MoveTo is { MovementTypeState: not AcDream.Core.Physics.MovementType.Invalid }) return;

        if (IsPlayerGuid(serverGuid))
        {
            return;
        }

        uint currentMotion = ae.Sequencer.CurrentMotion;
        if (!AcDream.Core.Physics.ServerControlledLocomotion
            .CanApplyVelocityCycle(currentMotion))
            return;

        var plan = AcDream.Core.Physics.ServerControlledLocomotion
            .PlanFromVelocity(velocity);

        uint style = ae.Sequencer.CurrentStyle != 0
            ? ae.Sequencer.CurrentStyle
            : 0x8000003Du;

        if (System.Environment.GetEnvironmentVariable("ACDREAM_REMOTE_VEL_DIAG") == "1")
        {
            System.Console.WriteLine(
                $"[UPCYCLE] guid={serverGuid:X8} "
                + $"vel=({velocity.X:F2},{velocity.Y:F2},{velocity.Z:F2}) "
                + $"|v|={velocity.Length():F2} "
                + $"-> motion=0x{plan.Motion:X8} speedMod={plan.SpeedMod:F2} "
                + $"prev=0x{currentMotion:X8} "
                + $"airborne={rm.Airborne} moveTo={rm.MoveTo?.MovementTypeState ?? AcDream.Core.Physics.MovementType.Invalid}");
        }
        ae.Sequencer.SetCycle(style, plan.Motion, plan.SpeedMod);
    }

}
