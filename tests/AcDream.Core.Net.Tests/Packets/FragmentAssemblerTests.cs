using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Packets;

public class FragmentAssemblerTests
{
    private static MessageFragment MakeFrag(uint id, ushort count, ushort index, byte[] payload, ushort queue = 7)
        => new(
            new MessageFragmentHeader
            {
                Sequence  = id,
                Id        = 0x80000000u,
                Count     = count,
                Index     = index,
                TotalSize = (ushort)(MessageFragmentHeader.Size + payload.Length),
                Queue     = queue,
            },
            payload);

    [Fact]
    public void Ingest_SingleFragmentMessage_ReleasesImmediately()
    {
        var assembler = new FragmentAssembler();
        var frag = MakeFrag(id: 1, count: 1, index: 0, payload: new byte[] { 1, 2, 3 }, queue: 42);

        var result = assembler.Ingest(frag, out var queue);

        Assert.NotNull(result);
        Assert.Equal(new byte[] { 1, 2, 3 }, result);
        Assert.Equal(42, queue);
        Assert.Equal(0, assembler.PartialCount);
    }

    [Fact]
    public void Ingest_ThreeFragmentsInOrder_ReleasesOnLast()
    {
        var assembler = new FragmentAssembler();

        Assert.Null(assembler.Ingest(MakeFrag(7, 3, 0, new byte[] { 0xAA, 0xBB }, queue: 9), out _));
        Assert.Equal(1, assembler.PartialCount);
        Assert.Null(assembler.Ingest(MakeFrag(7, 3, 1, new byte[] { 0xCC, 0xDD }, queue: 9), out _));
        var result = assembler.Ingest(MakeFrag(7, 3, 2, new byte[] { 0xEE }, queue: 9), out var queue);

        Assert.NotNull(result);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE }, result);
        Assert.Equal(9, queue);
        Assert.Equal(0, assembler.PartialCount);
    }

    [Fact]
    public void Ingest_OutOfOrderFragments_ReleasesCorrectlyOnLastArrival()
    {
        var assembler = new FragmentAssembler();

        Assert.Null(assembler.Ingest(MakeFrag(3, 3, 2, new byte[] { 0xCC }), out _));
        Assert.Null(assembler.Ingest(MakeFrag(3, 3, 0, new byte[] { 0xAA }), out _));
        var result = assembler.Ingest(MakeFrag(3, 3, 1, new byte[] { 0xBB }), out _);

        Assert.NotNull(result);
        // Result must be assembled in INDEX order, not arrival order.
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, result);
    }

    [Fact]
    public void Ingest_DuplicateFragment_IsIdempotent()
    {
        var assembler = new FragmentAssembler();
        Assert.Null(assembler.Ingest(MakeFrag(5, 2, 0, new byte[] { 0x11 }), out _));
        Assert.Null(assembler.Ingest(MakeFrag(5, 2, 0, new byte[] { 0x11 }), out _));
        // Assembler should still be waiting for index 1.
        Assert.Equal(1, assembler.PartialCount);

        var result = assembler.Ingest(MakeFrag(5, 2, 1, new byte[] { 0x22 }), out _);
        Assert.NotNull(result);
        Assert.Equal(new byte[] { 0x11, 0x22 }, result);
    }

    [Fact]
    public void Ingest_MissingFragment_DoesNotRelease()
    {
        var assembler = new FragmentAssembler();
        Assert.Null(assembler.Ingest(MakeFrag(9, 3, 0, new byte[] { 1 }), out _));
        Assert.Null(assembler.Ingest(MakeFrag(9, 3, 2, new byte[] { 3 }), out _));
        // Only 2 of 3 arrived → still waiting
        Assert.Equal(1, assembler.PartialCount);
    }

    [Fact]
    public void Ingest_TwoIndependentMessages_BuiltInParallel()
    {
        var assembler = new FragmentAssembler();

        Assert.Null(assembler.Ingest(MakeFrag(100, 2, 0, new byte[] { 0xA1 }), out _));
        Assert.Null(assembler.Ingest(MakeFrag(200, 2, 0, new byte[] { 0xB1 }), out _));
        Assert.Equal(2, assembler.PartialCount);

        var resultA = assembler.Ingest(MakeFrag(100, 2, 1, new byte[] { 0xA2 }), out _);
        Assert.Equal(new byte[] { 0xA1, 0xA2 }, resultA);
        Assert.Equal(1, assembler.PartialCount);

        var resultB = assembler.Ingest(MakeFrag(200, 2, 1, new byte[] { 0xB2 }), out _);
        Assert.Equal(new byte[] { 0xB1, 0xB2 }, resultB);
        Assert.Equal(0, assembler.PartialCount);
    }

    [Fact]
    public void DropAll_ClearsInFlightPartials()
    {
        var assembler = new FragmentAssembler();
        assembler.Ingest(MakeFrag(1, 5, 0, new byte[] { 1 }), out _);
        assembler.Ingest(MakeFrag(2, 5, 0, new byte[] { 2 }), out _);
        Assert.Equal(2, assembler.PartialCount);

        assembler.DropAll();
        Assert.Equal(0, assembler.PartialCount);
    }

    [Fact]
    public void TryIngest_BorrowedSingleFragment_ReturnsOriginalMemory()
    {
        var assembler = new FragmentAssembler();
        byte[] payload = [1, 2, 3];
        var fragment = new BorrowedMessageFragment(
            MakeFrag(10, 1, 0, payload, 11).Header,
            payload);

        bool complete = assembler.TryIngest(
            fragment,
            out ReadOnlyMemory<byte> message,
            out ushort queue);
        payload[1] = 0xAA;

        Assert.True(complete);
        Assert.Equal(11, queue);
        Assert.Equal(0xAA, message.Span[1]);
        Assert.Equal(0, assembler.PartialCount);
    }

    [Fact]
    public void TryIngest_BorrowedMultiFragment_CopiesAcrossDatagrams()
    {
        var assembler = new FragmentAssembler();
        byte[] firstPayload = [1, 2];
        byte[] secondPayload = [3, 4];
        var first = new BorrowedMessageFragment(
            MakeFrag(20, 2, 0, firstPayload, 12).Header,
            firstPayload);
        var second = new BorrowedMessageFragment(
            MakeFrag(20, 2, 1, secondPayload, 12).Header,
            secondPayload);

        Assert.False(assembler.TryIngest(
            first,
            out _,
            out _));
        firstPayload[0] = 0xFF;
        Assert.True(assembler.TryIngest(
            second,
            out ReadOnlyMemory<byte> message,
            out ushort queue));
        secondPayload[0] = 0xEE;

        Assert.Equal(12, queue);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, message.ToArray());
        Assert.Equal(0, assembler.PartialCount);
    }

    [Fact]
    public void TryIngest_ConflictingBorrowedIdentity_PreservesOriginalPartial()
    {
        var assembler = new FragmentAssembler();
        var first = new BorrowedMessageFragment(
            MakeFrag(30, 2, 0, [1], 13).Header,
            new byte[] { 1 });
        var conflict = new BorrowedMessageFragment(
            MakeFrag(30, 3, 1, [9], 14).Header,
            new byte[] { 9 });
        var completion = new BorrowedMessageFragment(
            MakeFrag(30, 2, 1, [2], 13).Header,
            new byte[] { 2 });

        Assert.False(assembler.TryIngest(first, out _, out _));
        Assert.False(assembler.TryIngest(conflict, out _, out _));
        Assert.Equal(1, assembler.PartialCount);
        Assert.True(assembler.TryIngest(
            completion,
            out ReadOnlyMemory<byte> message,
            out ushort queue));

        Assert.Equal(13, queue);
        Assert.Equal(new byte[] { 1, 2 }, message.ToArray());
        Assert.Equal(0, assembler.PartialCount);
    }


    private static BorrowedMessageFragment MakeBorrowed(
        uint sequence, ushort count, ushort index, byte[] payload, ushort queue = 7)
        => new(MakeFrag(sequence, count, index, payload, queue).Header, payload);

    [Fact]
    public void SweepExpired_EvictsAgedPartial_KeepsFresh()
    {
        double now = 0;
        var assembler = new FragmentAssembler(() => now);

        Assert.False(assembler.TryIngest(MakeBorrowed(1, 2, 0, [0xA1]), out _, out _));
        now = 30;
        Assert.False(assembler.TryIngest(MakeBorrowed(2, 3, 0, [0xB1]), out _, out _));
        Assert.Equal(2, assembler.PartialCount);

        now = 60;
        Assert.Equal(0, assembler.SweepExpired());
        Assert.Equal(2, assembler.PartialCount);

        // Past the floor: the aged partial goes, the fresh one stays.
        now = 61;
        Assert.Equal(1, assembler.SweepExpired());
        Assert.Equal(1, assembler.PartialCount);

        Assert.False(assembler.TryIngest(MakeBorrowed(2, 3, 1, [0xB2]), out _, out _));
        Assert.True(assembler.TryIngest(
            MakeBorrowed(2, 3, 2, [0xB3]),
            out ReadOnlyMemory<byte> message,
            out _));
        Assert.Equal(new byte[] { 0xB1, 0xB2, 0xB3 }, message.ToArray());
        Assert.Equal(0, assembler.PartialCount);
    }

    [Fact]
    public void SweepExpired_SlowButAlivePartial_RefreshesOnEachNewFragment()
    {
        double now = 0;
        var assembler = new FragmentAssembler(() => now);

        Assert.False(assembler.TryIngest(MakeBorrowed(5, 3, 0, [1]), out _, out _));
        now = 50;
        Assert.False(assembler.TryIngest(MakeBorrowed(5, 3, 1, [2]), out _, out _));

        now = 61;
        Assert.Equal(0, assembler.SweepExpired());
        Assert.Equal(1, assembler.PartialCount);

        // A DUPLICATE of an already-held index adds nothing and must not
        // refresh the stamp: 61 s after the last new fragment, it goes.
        now = 100;
        Assert.False(assembler.TryIngest(MakeBorrowed(5, 3, 1, [2]), out _, out _));
        now = 111.5;
        Assert.Equal(1, assembler.SweepExpired());
        Assert.Equal(0, assembler.PartialCount);
    }

    [Fact]
    public void TryIngest_LateDuplicateOfCompletedMessage_DropsWithoutRepartialing()
    {
        var assembler = new FragmentAssembler();

        Assert.False(assembler.TryIngest(MakeBorrowed(9, 2, 0, [1]), out _, out _));
        Assert.True(assembler.TryIngest(MakeBorrowed(9, 2, 1, [2]), out _, out _));
        Assert.Equal(0, assembler.PartialCount);

        Assert.False(assembler.TryIngest(MakeBorrowed(9, 2, 0, [1]), out _, out _));
        Assert.Equal(0, assembler.PartialCount);
    }

    [Fact]
    public void Ingest_LateDuplicateOfCompletedMessage_DropsWithoutRepartialing()
    {
        var assembler = new FragmentAssembler();

        Assert.Null(assembler.Ingest(MakeFrag(9, 2, 0, [1]), out _));
        Assert.NotNull(assembler.Ingest(MakeFrag(9, 2, 1, [2]), out _));
        Assert.Equal(0, assembler.PartialCount);

        Assert.Null(assembler.Ingest(MakeFrag(9, 2, 0, [1]), out _));
        Assert.Equal(0, assembler.PartialCount);
    }

    [Fact]
    public void CompletedRing_IsBounded_OldestSequenceIsForgotten()
    {
        var assembler = new FragmentAssembler();

        for (uint seq = 0; seq <= 64; seq++)
        {
            Assert.False(assembler.TryIngest(MakeBorrowed(seq, 2, 0, [1]), out _, out _));
            Assert.True(assembler.TryIngest(MakeBorrowed(seq, 2, 1, [2]), out _, out _));
        }

        // Sequence 0 was pushed out of the 64-entry ring: its late
        // duplicate re-partials (the documented bound — memory stays
        // bounded and the TTL sweep reclaims the stragglers).
        Assert.False(assembler.TryIngest(MakeBorrowed(0, 2, 0, [1]), out _, out _));
        Assert.Equal(1, assembler.PartialCount);

        Assert.False(assembler.TryIngest(MakeBorrowed(64, 2, 0, [1]), out _, out _));
        Assert.Equal(1, assembler.PartialCount);
    }
}
