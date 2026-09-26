using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;
using AcDream.Core.Net.Transport;

namespace AcDream.Core.Net.Tests.Transport;

public sealed class OutboundFragmentationTests
{
    [Theory]
    [InlineData("Keep heading north until you get to a long room with a throne. This is the first checkpoint. The wall behind the throne will only open if all the Thugs prior to this point have been killed", 450)]
    [InlineData("From the drop stick to the middle until you come to a room with an acid pit in the middle, go north to a small room where you'll go east, continue east until you come to a large L shaped room", 456)]
    public void LongChannelMessages_AreTransmittedWhole(string text, int expectedSize)
    {
        using var session = new WorldSession(new IPEndPoint(IPAddress.Loopback, 65000));
        bool captured = false;
        session.GameMessageCapture = (body, group) =>
        {
            captured = true;
            Assert.Equal(GameMessageGroup.UIQueue, group);
            Assert.Equal(expectedSize, body.Length);
            VerifyRoundTrip(body);
        };
        session.SendTurbineChatTo(2, 0, 0, 3, text, 1);
        Assert.True(captured);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(447)]
    [InlineData(448)]
    [InlineData(449)]
    [InlineData(896)]
    [InlineData(897)]
    [InlineData(65536)]
    public void PayloadBoundaries_RoundTripWithOneMessageIdentity(int size)
    {
        byte[] body = Enumerable.Range(0, size).Select(i => (byte)i).ToArray();
        VerifyRoundTrip(body);
    }

    private static void VerifyRoundTrip(byte[] body)
    {
        var sent = new List<byte[]>();
        using var queue = new OutboundFlowQueue(new IsaacRandom(new byte[4]), 1, 1,
            new TransportClock(), new TransportStats(), bytes => sent.Add(bytes.ToArray()));
        queue.SendGameMessage(body, GameMessageGroup.UIQueue);
        int count = Math.Max(1, (body.Length + 447) / 448);
        Assert.Equal(count, sent.Count);
        Assert.Equal(2u, queue.FragmentSequence);
        Assert.Equal(count, queue.CacheDepth);
        var cipher = new IsaacRandom(new byte[4]);
        foreach (byte[] packet in sent)
            Assert.True(PacketCodec.TryDecode(packet, cipher).IsOk);
        var assembler = new FragmentAssembler();
        byte[]? assembled = null;
        for (int i = count - 1; i >= 0; i--)
        {
            byte[] packet = sent[i];
            PacketHeader header = PacketHeader.Unpack(packet);
            Assert.Equal((uint)(2 + i), header.Sequence);
            Assert.InRange(packet.Length, PacketHeader.Size + MessageFragmentHeader.Size, 484);
            MessageFragmentHeader fragment = MessageFragmentHeader.Unpack(packet.AsSpan(PacketHeader.Size));
            Assert.Equal(1u, fragment.Sequence);
            Assert.Equal(GameMessageFragment.OutboundFragmentId, fragment.Id);
            Assert.Equal((ushort)count, fragment.Count);
            Assert.Equal((ushort)i, fragment.Index);
            Assert.Equal((ushort)GameMessageGroup.UIQueue, fragment.Queue);
            Assert.Equal(header.DataSize, fragment.TotalSize);
            byte[] payload = packet[(PacketHeader.Size + MessageFragmentHeader.Size)..];
            assembled = assembler.Ingest(new MessageFragment(fragment, payload), out _) ?? assembled;
        }
        Assert.Equal(body, assembled);
        queue.SendGameMessage([1, 2, 3], GameMessageGroup.ControlQueue);
        var next = MessageFragmentHeader.Unpack(sent[^1].AsSpan(PacketHeader.Size));
        Assert.Equal(2u, next.Sequence);
        Assert.Equal((ushort)1, next.Count);
        Assert.Equal((ushort)0, next.Index);
        Assert.True(PacketCodec.TryDecode(sent[^1], cipher).IsOk);
    }

    [Fact]
    public void LostMiddleFragment_IsRecoveredByNakWithoutAdvancingCipher()
    {
        var sent = new List<byte[]>();
        var stats = new TransportStats();
        using var queue = new OutboundFlowQueue(new IsaacRandom(new byte[4]), 1, 1,
            new TransportClock(), stats, bytes => sent.Add(bytes.ToArray()));
        byte[] body = Enumerable.Range(0, 897).Select(i => (byte)i).ToArray();
        queue.SendGameMessage(body, GameMessageGroup.UIQueue);
        var assembler = new FragmentAssembler();
        var cipher = new IsaacRandom(new byte[4]);
        var packets = sent.Select(p => PacketCodec.TryDecode(p, cipher).Packet!).ToArray();
        Assert.Null(assembler.Ingest(packets[0].Fragments[0], out _));
        Assert.Null(assembler.Ingest(packets[2].Fragments[0], out _));
        byte[] request = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 3);
        queue.OnRetransmitRequest(request, 1);
        queue.TransmitPendingResends();
        byte[] resent = sent[3];
        PacketHeader header = PacketHeader.Unpack(resent);
        Assert.Equal(3u, header.Sequence);
        Assert.True(header.HasFlag(PacketHeaderFlags.Retransmission));
        Assert.Equal(sent[1][PacketHeader.Size..], resent[PacketHeader.Size..]);
        uint sealedChecksum = packets[1].Header.Checksum - packets[1].Header.CalculateHeaderHash32();
        Assert.Equal(header.CalculateHeaderHash32() + sealedChecksum, header.Checksum);
        Assert.Equal(body, assembler.Ingest(packets[1].Fragments[0], out _));
        Assert.Equal(1, stats.ResendsSent);
        queue.SendGameMessage([4], GameMessageGroup.UIQueue);
        Assert.True(PacketCodec.TryDecode(sent[^1], cipher).IsOk);
    }

    [Fact]
    public void FailedLaterSend_KeepsEarlierCacheAndDoesNotReuseMessageIdentity()
    {
        var pool = new TrackingPool();
        var sent = new List<byte[]>();
        int attempts = 0;
        using (var queue = new OutboundFlowQueue(new IsaacRandom(new byte[4]), 1, 1,
            new TransportClock(), new TransportStats(), bytes =>
            {
                if (++attempts == 2) throw new IOException("send failed");
                sent.Add(bytes.ToArray());
            }, pool))
        {
            Assert.Throws<IOException>(() => queue.SendGameMessage(new byte[897], GameMessageGroup.UIQueue));
            Assert.Equal(1, queue.CacheDepth);
            Assert.Single(pool.Outstanding);
            Assert.Equal(2u, queue.FragmentSequence);
            queue.SendGameMessage([7], GameMessageGroup.UIQueue);
            Assert.Equal(2u, MessageFragmentHeader.Unpack(sent[^1].AsSpan(PacketHeader.Size)).Sequence);
            Assert.Equal(4u, PacketHeader.Unpack(sent[^1]).Sequence);
        }
        Assert.Empty(pool.Outstanding);
    }

    private sealed class TrackingPool : ArrayPool<byte>
    {
        public HashSet<byte[]> Outstanding { get; } = [];
        public override byte[] Rent(int minimumLength)
        {
            byte[] buffer = new byte[minimumLength];
            Assert.True(Outstanding.Add(buffer));
            return buffer;
        }
        public override void Return(byte[] array, bool clearArray = false) => Assert.True(Outstanding.Remove(array));
    }
}
