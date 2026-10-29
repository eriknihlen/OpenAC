using System.Net;
using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Physics;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

/// <summary>
/// The movement event's last byte holds contact (bit 0) and the standing
/// long jump (bit 1). The standing long jump is the motion interpreter's
/// own: a jump charged from standing still sets it, and the server uses it
/// to keep the charging character in place.
/// </summary>
public sealed class LocalPlayerMoveToStateLongJumpTests
{
    [Fact]
    public void AStandingJumpChargeSetsTheStandingLongJumpBit()
    {
        PlayerMovementController player = GroundedPlayer();
        player.Update(0.1f, new MovementInput(Jump: true));
        Assert.True(player.JumpCharge.IsCharging);
        Assert.True(player.StandingLongJump);

        byte flags = SendMovement(player, player.Update(0.1f, new MovementInput(Forward: true, Run: true, Jump: true)));

        Assert.Equal(0x03, flags);
    }

    [Fact]
    public void WithoutAChargeOnlyContactIsSent()
    {
        PlayerMovementController player = GroundedPlayer();

        byte flags = SendMovement(player, player.Update(0.1f, new MovementInput(Forward: true, Run: true)));

        Assert.Equal(0x01, flags);
    }

    [Fact]
    public void AChargeBegunWhileMovingIsNotAStandingLongJump()
    {
        PlayerMovementController player = GroundedPlayer();
        player.Update(0.1f, new MovementInput(Forward: true, Run: true));
        MovementResult charging = player.Update(0.1f, new MovementInput(Forward: true, Run: true, Jump: true));
        Assert.True(player.JumpCharge.IsCharging);

        byte flags = SendMovement(player, charging);

        Assert.Equal(0x01, flags);
    }

    /// <summary>Send one movement event and return its flags byte.</summary>
    private static byte SendMovement(PlayerMovementController player, MovementResult movement)
    {
        using var session = new WorldSession(new IPEndPoint(IPAddress.Loopback, 9000));
        var sent = new List<byte[]>();
        session.GameActionCapture = sent.Add;
        var outbound = new LocalPlayerOutboundController((_, _, _, _, _, _) => { });

        Assert.True(outbound.TrySendMovement(session, player, movement));

        byte[] body = Assert.Single(sent);
        // The flags byte is followed by padding to four bytes; every field
        // before it is a whole number of four-byte words.
        Assert.Equal(0, body.Length % 4);
        Assert.Equal(new byte[3], body[^3..]);
        return body[^4];
    }

    private static PlayerMovementController GroundedPlayer()
    {
        var engine = new PhysicsEngine();
        var heights = new byte[81];
        Array.Fill(heights, (byte)50);
        var heightTable = new float[256];
        for (int i = 0; i < heightTable.Length; i++)
            heightTable[i] = i;
        engine.AddLandblock(
            0xA9B4FFFFu,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        var player = new PlayerMovementController(engine);
        player.SeedPlacementForTest(
            new Vector3(96f, 97f, 50f),
            0xA9B40001u,
            new Vector3(96f, 97f, 50f));
        return player;
    }
}
