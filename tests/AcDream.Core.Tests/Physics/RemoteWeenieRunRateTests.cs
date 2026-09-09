using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class RemoteWeenieRunRateTests
{
    [Fact]
    public void ApplyRunToCommand_RemoteWeenie_ScalesByWireRunRate()
    {
        var interp = new MotionInterpreter(new PhysicsBody())
        {
            WeenieObj = new RemoteWeenie(),
            MyRunRate = 4.5f, // the mt-6 wire write (unpack @300603)
        };

        uint motion = MotionCommand.WalkForward;
        float speed = 1.0f;
        interp.apply_run_to_command(ref motion, ref speed);

        Assert.Equal(MotionCommand.RunForward, motion);
        Assert.Equal(4.5f, speed, 3);
    }

    [Fact]
    public void ApplyRunToCommand_NoWeenie_KeepsRetailDegenerateBranch()
    {
        var interp = new MotionInterpreter(new PhysicsBody())
        {
            MyRunRate = 4.5f,
        };

        uint motion = MotionCommand.WalkForward;
        float speed = 1.0f;
        interp.apply_run_to_command(ref motion, ref speed);

        Assert.Equal(MotionCommand.RunForward, motion);
        Assert.Equal(1.0f, speed, 3);
    }
}
