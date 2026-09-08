using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;
using AcDream.Core.Net.Tests.Transport;

namespace AcDream.Core.Net.Tests;

public sealed class WorldSessionCharacterSelectionTests
{
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

    [Fact]
    public void CharacterManagementSends_UseRetailQueuesAndExactBodies()
    {
        using var session = CreateSession();
        var sent = new List<(byte[] Body, GameMessageGroup Queue)>();
        session.GameMessageCapture =
            (body, queue) => sent.Add((body, queue));

        session.SendDeleteCharacter("Canonical", activeIndex: 3);
        session.SendRestoreCharacter(0x50000001u);

        Assert.Collection(
            sent,
            delete =>
            {
                Assert.Equal(GameMessageGroup.LoginQueue, delete.Queue);
                Assert.Equal(
                    CharacterDelete.BuildRequestBody("Canonical", 3u),
                    delete.Body);
            },
            restore =>
            {
                Assert.Equal(GameMessageGroup.ControlQueue, restore.Queue);
                Assert.Equal(
                    CharacterRestore.BuildRequestBody(0x50000001u),
                    restore.Body);
            });
    }

    [Fact]
    public void UiQueueReplies_DispatchInWireOrderAndRosterRefreshReplacesCharacters()
    {
        using var session = CreateSession();
        session.GameMessageCapture = (_, _) => { };
        session.SendRestoreCharacter(0x50000001u);

        var events = new List<string>();
        session.CharacterListReceived += roster =>
            events.Add($"roster:{roster.Characters[0].SecondsGreyedOut}");
        session.CharacterDeleteAcknowledged += () => events.Add("delete");
        session.CharacterRestoreReceived += restore =>
            events.Add($"restore:{restore.Guid:X8}");
        session.CharacterErrorReceived += error =>
            events.Add($"error:{error.RawErrorCode:X}");

        byte[] packet = BuildPacket(
            BuildRoster(secondsGreyedOut: 0u),
            BitConverter.GetBytes(CharacterDelete.Opcode),
            BuildRestoreResponse(),
            BuildCharacterError(CharacterError.Code.Delete),
            BuildRoster(secondsGreyedOut: 1u));
        InvokeProcessDatagram(session, packet);

        Assert.Equal(
            [
                "roster:0",
                "delete",
                "restore:50000001",
                "error:6",
                "roster:1",
            ],
            events);
        CharacterList.Character current =
            Assert.Single(session.Characters!.Characters);
        Assert.Equal(1u, current.SecondsGreyedOut);
    }

    [Fact]
    public void ServerName_Dispatches_AndPopulatesServerInfo()
    {
        using var session = CreateSession();
        var events = new List<string>();
        session.CharacterListReceived += _ => events.Add("roster");
        session.ServerNameReceived += info => events.Add($"world:{info.WorldName}");

        byte[] packet = BuildPacket(
            BuildRoster(secondsGreyedOut: 0u),
            BuildServerName("sawato", currentConnections: 3, maxConnections: 100));
        InvokeProcessDatagram(session, packet);

        Assert.Equal(["roster", "world:sawato"], events);
        Assert.NotNull(session.ServerInfo);
        Assert.Equal("sawato", session.ServerInfo!.Value.WorldName);
        Assert.Equal(3, session.ServerInfo!.Value.CurrentConnections);
        Assert.Equal(100, session.ServerInfo!.Value.MaxConnections);
    }

    [Fact]
    public void ImmediateEnterWorld_IgnoresNumErrorsSentinelBeforeServerReady()
    {
        var transport = new FakeAceTransport
        {
            AutoReplyServerReady = false,
        };
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            transport);
        int errors = 0;
        session.CharacterErrorReceived += _ => errors++;
        ConfigureSentinelThenServerReady(transport);

        session.Connect(
            FakeAceTransport.DefaultAccountName,
            "testpassword",
            TimeSpan.FromSeconds(5));
        session.EnterWorld(0, TimeSpan.FromSeconds(5));

        Assert.Equal(WorldSession.State.InWorld, session.CurrentState);
        Assert.Equal(0, errors);
    }

    [Fact]
    public void PausedEnterWorld_IgnoresNumErrorsSentinelBeforeServerReady()
    {
        var transport = new FakeAceTransport
        {
            AutoReplyServerReady = false,
        };
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            transport);
        int errors = 0;
        session.CharacterErrorReceived += _ => errors++;
        ConfigureSentinelThenServerReady(transport);

        session.Connect(
            FakeAceTransport.DefaultAccountName,
            "testpassword",
            TimeSpan.FromSeconds(5));
        session.StartCharacterSelectionReceive();
        session.EnterWorld(0, TimeSpan.FromSeconds(5));

        Assert.Equal(WorldSession.State.InWorld, session.CurrentState);
        Assert.Equal(0, errors);
    }

    private static WorldSession CreateSession() =>
        new(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new NullTransport());

    private static void ConfigureSentinelThenServerReady(
        FakeAceTransport transport)
    {
        transport.Model.MessageDispatched += body =>
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(body)
                != CharacterEnterWorld.EnterWorldRequestOpcode)
            {
                return;
            }

            transport.Model.EnqueueGameMessage(
                BuildCharacterError(CharacterError.Code.NumErrors),
                GameMessageGroup.UIQueue);
            transport.Model.EnqueueGameMessage(
                BitConverter.GetBytes(0xF7DFu),
                GameMessageGroup.UIQueue);
        };
    }

    private static byte[] BuildRoster(uint secondsGreyedOut)
    {
        var writer = new PacketWriter(96);
        writer.WriteUInt32(CharacterList.Opcode);
        writer.WriteUInt32(0u);
        writer.WriteUInt32(1u);
        writer.WriteUInt32(0x50000001u);
        writer.WriteString16L("Character");
        writer.WriteUInt32(secondsGreyedOut);
        writer.WriteUInt32(0u);
        writer.WriteUInt32(11u);
        writer.WriteString16L("Canonical");
        writer.WriteUInt32(1u);
        writer.WriteUInt32(1u);
        return writer.ToArray();
    }

    private static byte[] BuildServerName(
        string worldName,
        int currentConnections,
        int maxConnections)
    {
        var writer = new PacketWriter(64);
        writer.WriteUInt32(ServerName.Opcode);
        writer.WriteUInt32(unchecked((uint)currentConnections));
        writer.WriteUInt32(unchecked((uint)maxConnections));
        writer.WriteString16L(worldName);
        return writer.ToArray();
    }

    private static byte[] BuildRestoreResponse()
    {
        var writer = new PacketWriter(64);
        writer.WriteUInt32(CharacterRestore.ResponseOpcode);
        writer.WriteUInt32(1u);
        writer.WriteUInt32(0x50000001u);
        writer.WriteString16L("Character");
        writer.WriteUInt32(0u);
        return writer.ToArray();
    }

    private static byte[] BuildCharacterError(CharacterError.Code error)
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(
            body,
            CharacterError.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(
            body.AsSpan(4),
            (uint)error);
        return body;
    }

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

    private static void InvokeProcessDatagram(
        WorldSession session,
        byte[] datagram)
    {
        MethodInfo method = typeof(WorldSession).GetMethod(
            "ProcessDatagram",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(
            session,
            [new ReadOnlyMemory<byte>(datagram), null, true]);
    }
}
