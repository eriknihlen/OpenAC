using System.Buffers.Binary;
using System.Net;
using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Packets;
using AcDream.Core.Net.Transport;

namespace AcDream.Core.Net.Tests.Transport;

public sealed class ConnectResponseRetransmitTests
{
    [Fact]
    public void Connect_FirstConnectResponseDropped_RetryHealsHandshake()
    {
        var fake = new FakeAceTransport
        {
            AutoAdvanceOnBlockingReceive = TimeSpan.FromMilliseconds(200),
        };
        int connectResponsesSent = 0;
        fake.Link.Drop(LinkDirection.ClientToServer, (_, datagram) =>
        {
            if (!IsConnectResponse(datagram))
                return false;
            connectResponsesSent++;
            return connectResponsesSent == 1; // only the FIRST one dies
        });
        int accepted = 0;
        fake.Model.ConnectResponseAccepted += () => accepted++;

        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            fake);
        session.TransportClockSource =
            (fake.Clock.GetTimestamp, fake.Clock.Frequency);

        session.Connect("testaccount", "testpassword", TimeSpan.FromSeconds(10));

        Assert.Equal(WorldSession.State.InCharacterSelect, session.CurrentState);
        Assert.NotNull(session.Characters);
        Assert.Equal(2, connectResponsesSent);
        Assert.Equal(1, accepted);
        Assert.Equal(0, fake.Model.StateDropCount);
        Assert.Equal(0, fake.Model.CrcDropCount);
        Assert.Equal(0, fake.Model.DuplicateDropCount);
        Assert.Equal(256, fake.Model.Crypto.Headroom);
    }

    [Fact]
    public void Connect_CleanHandshake_SendsExactlyOneConnectResponse()
    {
        var fake = new FakeAceTransport();
        int connectResponsesSent = 0;
        fake.Link.Drop(LinkDirection.ClientToServer, (_, datagram) =>
        {
            if (IsConnectResponse(datagram))
                connectResponsesSent++;
            return false; // tap only
        });

        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            fake);
        session.TransportClockSource =
            (fake.Clock.GetTimestamp, fake.Clock.Frequency);

        session.Connect("testaccount", "testpassword", TimeSpan.FromSeconds(10));

        Assert.NotNull(session.Characters);
        Assert.Equal(1, connectResponsesSent);
        Assert.Equal(0, fake.Model.StateDropCount);
    }

    [Fact]
    public void Connect_ServerResponsesLost_RetriesDropHarmlessly_SessionHeals()
    {
        var fake = new FakeAceTransport
        {
            AutoAdvanceOnBlockingReceive = TimeSpan.FromMilliseconds(200),
        };
        fake.Link.DropAt(LinkDirection.ServerToClient, 1);
        fake.Link.DropAt(LinkDirection.ServerToClient, 2);
        int connectResponsesSent = 0;
        fake.Link.Drop(LinkDirection.ClientToServer, (_, datagram) =>
        {
            if (IsConnectResponse(datagram))
                connectResponsesSent++;
            return false; // tap only
        });
        int accepted = 0;
        fake.Model.ConnectResponseAccepted += () => accepted++;

        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            fake);
        session.TransportClockSource =
            (fake.Clock.GetTimestamp, fake.Clock.Frequency);

        session.Connect("testaccount", "testpassword", TimeSpan.FromSeconds(10));

        Assert.NotNull(session.Characters);
        Assert.Equal(1, accepted);
        Assert.True(connectResponsesSent >= 2,
            $"expected retries, saw {connectResponsesSent}");
        Assert.True(fake.Model.StateDropCount >= 1,
            $"expected CheckState drops, saw {fake.Model.StateDropCount}");
        Assert.Equal(0, fake.Model.CrcDropCount);
        Assert.Equal(0, fake.Model.DuplicateDropCount);
        Assert.True(session.Transport!.Stats.NaksSent >= 1);
        Assert.True(session.Transport.Stats.KeysParked >= 2);
        Assert.Equal(0, session.Transport.Stats.ChecksumFailures);
        Assert.Equal(256, fake.Model.Crypto.Headroom);
    }

    [Fact]
    public void AceModel_DuplicateConnectResponse_AfterAcceptance_DropsViaCheckState()
    {
        var clock = new VirtualClock();
        var model = new AceSessionModel(
            clock,
            FakeAceTransport.DefaultClientSeed,
            FakeAceTransport.DefaultServerSeed,
            FakeAceTransport.DefaultClientId,
            FakeAceTransport.DefaultCookie);
        int accepted = 0;
        model.ConnectResponseAccepted += () => accepted++;

        byte[] cookieBody = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(
            cookieBody, FakeAceTransport.DefaultCookie);
        byte[] connectResponse = PacketCodec.Encode(
            new PacketHeader
            {
                Sequence = 1,
                Flags = PacketHeaderFlags.ConnectResponse,
                Id = 0,
            },
            cookieBody,
            null);

        model.SendConnectRequest();
        Assert.Equal(AceSessionState.AuthConnectResponse, model.State);

        model.Receive(connectResponse);
        Assert.Equal(AceSessionState.AuthConnected, model.State);
        Assert.Equal(1, accepted);
        Assert.Equal(0, model.StateDropCount);

        model.Receive(connectResponse);
        Assert.Equal(AceSessionState.AuthConnected, model.State);
        Assert.Equal(1, accepted);
        Assert.Equal(1, model.StateDropCount);
        Assert.Equal(0, model.CrcDropCount);
        Assert.Equal(0, model.DuplicateDropCount);
        Assert.Equal(256, model.Crypto.Headroom);
    }

    private static bool IsConnectResponse(byte[] datagram) =>
        datagram.Length >= PacketHeader.Size
        && (BinaryPrimitives.ReadUInt32LittleEndian(datagram.AsSpan(4))
            & (uint)PacketHeaderFlags.ConnectResponse) != 0;
}

public sealed class ReliableTransportAssemblerSweepTests
{
    [Fact]
    public void Sweep_EvictsAgedPartial_KeepsFreshOne()
    {
        var clock = new VirtualClock();
        var assembler = new FragmentAssembler(() => clock.Seconds);
        var transport = new ReliableTransport(
            MakeIsaac(0x11AA22BBu),
            MakeIsaac(0x33CC44DDu),
            0x1234,
            1,
            _ => { },
            new TransportClock(clock.GetTimestamp, clock.Frequency),
            assembler: assembler);

        // Park a partial at t=0.
        IngestPartial(assembler, sequence: 10);
        Assert.Equal(1, assembler.PartialCount);

        clock.Advance(TimeSpan.FromSeconds(30));
        transport.Sweep();
        Assert.Equal(1, assembler.PartialCount);

        clock.Advance(TimeSpan.FromSeconds(28));
        IngestPartial(assembler, sequence: 11);
        clock.Advance(TimeSpan.FromSeconds(3.5)); // t = 61.5
        transport.Sweep();

        Assert.Equal(1, assembler.PartialCount); // 10 evicted, 11 kept
        transport.Dispose();
    }

    private static void IngestPartial(FragmentAssembler assembler, uint sequence)
    {
        var header = new MessageFragmentHeader
        {
            Sequence = sequence,
            Id = 0x80000000u,
            Count = 2,
            Index = 0,
            TotalSize = (ushort)(MessageFragmentHeader.Size + 1),
            Queue = 7,
        };
        byte[] payload = { 0x42 };
        Assert.False(assembler.TryIngest(
            new BorrowedMessageFragment(header, payload),
            out _,
            out _));
    }

    private static IsaacRandom MakeIsaac(uint seed)
    {
        Span<byte> seedBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seedBytes, seed);
        return new IsaacRandom(seedBytes);
    }
}
