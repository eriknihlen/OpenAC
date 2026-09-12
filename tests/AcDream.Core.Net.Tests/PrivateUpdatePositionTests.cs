using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using System.Reflection;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;
using AcDream.Core.Physics;
using AcDream.Core.Player;

namespace AcDream.Core.Net.Tests;

/// <summary>
/// Dying outdoors moves the corpse landmark. The server says so with one
/// private position update naming the slot it rewrote; without it the client
/// answers the "where is my corpse?" question from the login snapshot and names
/// a PREVIOUS death's coordinates.
/// </summary>
public sealed class PrivateUpdatePositionTests
{
    private const uint LastOutsideDeathSlot = 0x0Eu;

    private sealed class NullTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram) { }
        public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram) { }
        public int Receive(
            Span<byte> destination,
            TimeSpan timeout,
            out IPEndPoint? from)
        {
            from = null;
            return -1;
        }
        public ValueTask<NetReceiveResult> ReceiveAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromCanceled<NetReceiveResult>(cancellationToken);
        public void Dispose() { }
    }

    // --- parser ------------------------------------------------------------

    [Fact]
    public void TryParse_ReadsSequenceSlotAndPosition()
    {
        var p = PrivateUpdatePosition.TryParse(Build(
            seq: 7,
            positionType: LastOutsideDeathSlot,
            cell: 0xC6A9001Fu,
            x: 12.5f, y: -34.25f, z: 6.125f,
            qw: 0.5f, qx: 1.5f, qy: 2.5f, qz: 3.5f));

        Assert.NotNull(p);
        Assert.Equal((byte)7, p!.Value.Sequence);
        Assert.Equal(LastOutsideDeathSlot, p.Value.PositionType);
        Assert.Equal(0xC6A9001Fu, p.Value.Position.LandblockId);
        Assert.Equal(12.5f, p.Value.Position.X);
        Assert.Equal(-34.25f, p.Value.Position.Y);
        Assert.Equal(6.125f, p.Value.Position.Z);
        Assert.Equal(0.5f, p.Value.Position.Qw);
        Assert.Equal(1.5f, p.Value.Position.Qx);
        Assert.Equal(2.5f, p.Value.Position.Qy);
        Assert.Equal(3.5f, p.Value.Position.Qz);
    }

    [Fact]
    public void TryParse_RejectsForeignOpcode()
    {
        byte[] body = Build();
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0x02DCu);
        Assert.Null(PrivateUpdatePosition.TryParse(body));
    }

    [Fact]
    public void TryParse_RejectsTruncatedBody()
        => Assert.Null(PrivateUpdatePosition.TryParse(
            Build().AsSpan(0, PrivateUpdatePosition.BodySize - 1)));

    // --- local player state ------------------------------------------------

    [Fact]
    public void OnPosition_ReplacesOneSlotAndLeavesTheOthersAlone()
    {
        var player = new LocalPlayerState();
        var sanctuary = new Position(
            0xAAAA0001u, new Vector3(1f, 2f, 3f), Quaternion.Identity);
        var oldDeath = new Position(
            0xBBBB0002u, new Vector3(4f, 5f, 6f), Quaternion.Identity);
        player.OnPositions(new Dictionary<uint, Position>
        {
            [0x0Du] = sanctuary,
            [LastOutsideDeathSlot] = oldDeath,
        });

        var newDeath = new Position(
            0xCCCC0003u, new Vector3(7f, 8f, 9f), Quaternion.Identity);
        player.OnPosition(LastOutsideDeathSlot, newDeath);

        Assert.Equal(newDeath, player.GetPosition(LastOutsideDeathSlot));
        Assert.Equal(sanctuary, player.GetPosition(0x0Du));
        Assert.Equal(2, player.Positions.Count);
    }

    [Fact]
    public void OnPosition_AddsASlotTheSnapshotNeverCarried()
    {
        var player = new LocalPlayerState();
        player.OnPositions(new Dictionary<uint, Position>());
        Assert.Null(player.GetPosition(LastOutsideDeathSlot));

        var death = new Position(
            0xCCCC0003u, new Vector3(7f, 8f, 9f), Quaternion.Identity);
        player.OnPosition(LastOutsideDeathSlot, death);

        Assert.Equal(death, player.GetPosition(LastOutsideDeathSlot));
    }

    [Fact]
    public void OnPosition_RaisesCharacterChanged()
    {
        var player = new LocalPlayerState();
        int raised = 0;
        player.CharacterChanged += () => raised++;

        player.OnPosition(LastOutsideDeathSlot, default);

        Assert.Equal(1, raised);
    }

    // --- session to local player: the read the corpse command makes ---------

    [Fact]
    public void DeathPush_ReplacesTheCorpseLandmarkTheLoginSnapshotCarried()
    {
        using var session = CreateSession();
        var player = new LocalPlayerState();
        using IDisposable wiring = ObjectTableWiring.Wire(
            session,
            new ClientObjectTable(),
            playerGuid: () => 0x50000001u,
            localPlayer: player);

        // The login description snapshot: a PREVIOUS death.
        player.OnPositions(new Dictionary<uint, Position>
        {
            [LastOutsideDeathSlot] = new Position(
                0xAAAA0001u, new Vector3(1f, 2f, 3f), Quaternion.Identity),
        });

        InvokeProcessDatagram(session, BuildPacket(Build(
            positionType: LastOutsideDeathSlot,
            cell: 0xC6A9001Fu,
            x: 12.5f, y: -34.25f, z: 6.125f,
            qw: 0.5f, qx: 1.5f, qy: 2.5f, qz: 3.5f)));

        // This is exactly the read the corpse command makes.
        Position? corpse = player.GetPosition(LastOutsideDeathSlot);
        Assert.NotNull(corpse);
        Assert.Equal(0xC6A9001Fu, corpse!.Value.ObjCellId);
        Assert.Equal(new Vector3(12.5f, -34.25f, 6.125f), corpse.Value.Frame.Origin);
        // The wire writes the real part first; the engine quaternion takes it last.
        Assert.Equal(new Quaternion(1.5f, 2.5f, 3.5f, 0.5f), corpse.Value.Frame.Orientation);
    }

    [Fact]
    public void DeathPush_IsIgnoredWhileTheRouteIsNotAccepting()
    {
        using var session = CreateSession();
        var player = new LocalPlayerState();
        var snapshot = new Position(
            0xAAAA0001u, new Vector3(1f, 2f, 3f), Quaternion.Identity);
        using IDisposable wiring = ObjectTableWiring.Wire(
            session,
            new ClientObjectTable(),
            playerGuid: () => 0x50000001u,
            localPlayer: player,
            accepting: () => false);

        player.OnPositions(new Dictionary<uint, Position>
        {
            [LastOutsideDeathSlot] = snapshot,
        });

        InvokeProcessDatagram(session, BuildPacket(Build(
            positionType: LastOutsideDeathSlot, cell: 0xC6A9001Fu)));

        Assert.Equal(snapshot, player.GetPosition(LastOutsideDeathSlot));
    }

    // --- helpers -----------------------------------------------------------

    private static byte[] Build(
        byte seq = 1,
        uint positionType = LastOutsideDeathSlot,
        uint cell = 0xC6A9001Fu,
        float x = 0f, float y = 0f, float z = 0f,
        float qw = 1f, float qx = 0f, float qy = 0f, float qz = 0f)
    {
        byte[] body = new byte[PrivateUpdatePosition.BodySize];
        BinaryPrimitives.WriteUInt32LittleEndian(body, PrivateUpdatePosition.Opcode);
        body[4] = seq;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5), positionType);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), cell);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(13), x);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(17), y);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(21), z);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(25), qw);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(29), qx);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(33), qy);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(37), qz);
        return body;
    }

    private static WorldSession CreateSession() =>
        new(new IPEndPoint(IPAddress.Loopback, 9000), new NullTransport());

    private static byte[] BuildPacket(params byte[][] messages)
    {
        int length = messages.Sum(message =>
            MessageFragmentHeader.Size + message.Length);
        var fragments = new byte[length];
        int position = 0;
        uint sequence = 1u;
        foreach (byte[] message in messages)
        {
            position += GameMessageFragment.WriteSingleFragment(
                fragments.AsSpan(position),
                sequence++,
                GameMessageGroup.UIQueue,
                message);
        }
        return PacketCodec.Encode(
            new PacketHeader
            {
                Sequence = 1u,
                Flags = PacketHeaderFlags.BlobFragments,
            },
            fragments,
            outboundIsaac: null);
    }

    private static void InvokeProcessDatagram(WorldSession session, byte[] datagram)
    {
        MethodInfo method = typeof(WorldSession).GetMethod(
            "ProcessDatagram",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(session, [new ReadOnlyMemory<byte>(datagram), null, true]);
    }
}
