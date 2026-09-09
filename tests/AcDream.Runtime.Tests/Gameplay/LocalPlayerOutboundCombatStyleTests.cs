using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;
using AcDream.Core.Physics;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class LocalPlayerOutboundCombatStyleTests
{
    [Theory]
    [InlineData(0x8000003Cu)] // HandCombat
    [InlineData(0x8000003Eu)] // Magic
    [InlineData(0x8000003Fu)] // BowCombat
    [InlineData(0x80000041u)]
    public void CaptureAndBuild_PreservesCanonicalRawCombatStyle(uint combatStyle)
    {
        var controller = new PlayerMovementController(new PhysicsEngine());
        controller.Motion.RawState.CurrentStyle = combatStyle;

        MovementResult movement = controller.CaptureMovementResult(
            mouseLookEvent: false);
        RawMotionState outbound = LocalPlayerOutboundController.BuildRawMotionState(
            movement);

        Assert.Equal(combatStyle, movement.CurrentStyle);
        Assert.Equal(combatStyle, outbound.CurrentStyle);

        var writer = new PacketWriter(16);
        RawMotionStatePacker.Pack(writer, outbound);
        byte[] bytes = writer.ToArray();

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        Assert.NotEqual(0u, flags & 0x2u);
        Assert.Equal(
            combatStyle,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
    }

    [Fact]
    public void CaptureAndBuild_NonCombatRetainsRetailDefault()
    {
        var controller = new PlayerMovementController(new PhysicsEngine());

        RawMotionState outbound = LocalPlayerOutboundController.BuildRawMotionState(
            controller.CaptureMovementResult(mouseLookEvent: false));

        Assert.Equal(RawMotionState.Default.CurrentStyle, outbound.CurrentStyle);
    }
}
