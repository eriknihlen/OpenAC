using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class LocalPlayerImmediatePositionTests
{
    [Fact]
    public void ForcePositionAcknowledgement_SendsOneExactAutonomousPositionAndStampsIt()
    {
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000));
        session.PublishAcceptedLocalPhysicsTimestamps(
            instance: 11,
            serverControlledMove: 12,
            teleport: 13,
            forcePosition: 14);
        var sent = new List<byte[]>();
        session.GameActionCapture = sent.Add;

        PlayerMovementController player = GroundedPlayer();
        Position preTurnCanonical = player.CellPosition;
        Quaternion rotation = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f));
        player.SetBodyOrientation(rotation);
        Assert.NotEqual(rotation, preTurnCanonical.Frame.Orientation);
        Assert.True(player.TryGetOutboundPosition(out Position outboundPosition));
        var outbound = new LocalPlayerOutboundController((_, _, _, _, _, _) => { });

        outbound.SendImmediatePosition(session, player);

        byte[] body = Assert.Single(sent);
        Assert.Equal(56, body.Length);
        Assert.Equal(AutonomousPosition.GameActionOpcode, U32(body, 0));
        Assert.Equal(1u, U32(body, 4));
        Assert.Equal(AutonomousPosition.AutonomousPositionAction, U32(body, 8));
        Assert.Equal(outboundPosition.ObjCellId, U32(body, 12));
        Assert.Equal(outboundPosition.Frame.Origin.X, F32(body, 16));
        Assert.Equal(outboundPosition.Frame.Origin.Y, F32(body, 20));
        Assert.Equal(outboundPosition.Frame.Origin.Z, F32(body, 24));
        Assert.Equal(rotation.W, F32(body, 28), precision: 6);
        Assert.Equal(rotation.X, F32(body, 32), precision: 6);
        Assert.Equal(rotation.Y, F32(body, 36), precision: 6);
        Assert.Equal(rotation.Z, F32(body, 40), precision: 6);
        Assert.Equal((ushort)11, U16(body, 44));
        Assert.Equal((ushort)12, U16(body, 46));
        Assert.Equal((ushort)13, U16(body, 48));
        Assert.Equal((ushort)14, U16(body, 50));
        Assert.Equal((byte)1, body[52]);
        Assert.True(player.TryGetOutboundPosition(out Position currentOutbound));
        Assert.False(player.ShouldSendPositionEvent(
            currentOutbound,
            player.ContactPlane,
            player.SimTimeSeconds));
    }

    [Fact]
    public void ImmediateAcknowledgement_RequiresSessionPlayerAndGroundContact()
    {
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000));
        var sent = new List<byte[]>();
        session.GameActionCapture = sent.Add;
        var outbound = new LocalPlayerOutboundController((_, _, _, _, _, _) => { });
        PlayerMovementController grounded = GroundedPlayer();

        outbound.SendImmediatePosition(null, grounded);
        outbound.SendImmediatePosition(session, null);
        outbound.SendImmediatePosition(
            session,
            new PlayerMovementController(new PhysicsEngine()));

        Assert.Empty(sent);
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
        Assert.True(player.CanSendPositionEvent);
        return player;
    }

    private static uint U32(byte[] body, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset, 4));

    private static ushort U16(byte[] body, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset, 2));

    private static float F32(byte[] body, int offset) =>
        BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset, 4)));
}
