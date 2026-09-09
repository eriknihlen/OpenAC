using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Packets;

public class MessageFragmentTests
{
    [Fact]
    public void TryParse_CompleteFragment_ReturnsFragmentAndConsumedBytes()
    {
        // Build a synthetic fragment: 16-byte header + 4-byte payload = totalSize 20
        var header = new MessageFragmentHeader
        {
            Sequence  = 1, Id = 0x80000001u, Count = 1,
            TotalSize = 20, Index = 0, Queue = 5,
        };
        byte[] buf = new byte[20];
        header.Pack(buf);
        buf[16] = 0xAA; buf[17] = 0xBB; buf[18] = 0xCC; buf[19] = 0xDD;

        var (frag, consumed) = MessageFragment.TryParse(buf);

        Assert.NotNull(frag);
        Assert.Equal(20, consumed);
        Assert.Equal(4, frag!.Value.Payload.Length);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, frag.Value.Payload);
        Assert.Equal(5, frag.Value.Header.Queue);
    }

    [Fact]
    public void TryParse_InsufficientSourceForHeader_ReturnsNull()
    {
        var (frag, consumed) = MessageFragment.TryParse(new byte[15]);
        Assert.Null(frag);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryParse_TotalSizeTooLarge_ReturnsNull()
    {
        var header = new MessageFragmentHeader { TotalSize = 500, Count = 1 };
        byte[] buf = new byte[16];
        header.Pack(buf);

        var (frag, consumed) = MessageFragment.TryParse(buf);

        Assert.Null(frag);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryParse_TotalSizeSmallerThanHeader_ReturnsNull()
    {
        var header = new MessageFragmentHeader { TotalSize = 10 };
        byte[] buf = new byte[16];
        header.Pack(buf);

        var (frag, consumed) = MessageFragment.TryParse(buf);

        Assert.Null(frag);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryParse_IncompleteBody_ReturnsNull()
    {
        // Header says 40 total bytes but buffer only has 20.
        var header = new MessageFragmentHeader { TotalSize = 40, Count = 1 };
        byte[] buf = new byte[20];
        header.Pack(buf);

        var (frag, consumed) = MessageFragment.TryParse(buf);

        Assert.Null(frag);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryParse_ZeroFragmentCount_ReturnsNull()
    {
        var header = new MessageFragmentHeader
        {
            TotalSize = MessageFragmentHeader.Size,
            Count = 0,
            Index = 0,
        };
        byte[] buf = new byte[MessageFragmentHeader.Size];
        header.Pack(buf);

        var (frag, consumed) = MessageFragment.TryParse(buf);

        Assert.Null(frag);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryParse_IndexOutsideFragmentCount_ReturnsNull()
    {
        var header = new MessageFragmentHeader
        {
            TotalSize = MessageFragmentHeader.Size,
            Count = 2,
            Index = 2,
        };
        byte[] buf = new byte[MessageFragmentHeader.Size];
        header.Pack(buf);

        var (frag, consumed) = MessageFragment.TryParse(buf);

        Assert.Null(frag);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryParse_ConsumesExactlyOneFragment_LeavesRemainderForCaller()
    {
        var h1 = new MessageFragmentHeader { Id = 1, Count = 1, TotalSize = 18, Index = 0 };
        var h2 = new MessageFragmentHeader { Id = 2, Count = 1, TotalSize = 17, Index = 0 };
        byte[] buf = new byte[35];  // 18 + 17
        h1.Pack(buf.AsSpan(0, 16));
        buf[16] = 0xAA; buf[17] = 0xBB;
        h2.Pack(buf.AsSpan(18, 16));
        buf[34] = 0xCC;

        var (frag1, consumed1) = MessageFragment.TryParse(buf);
        Assert.NotNull(frag1);
        Assert.Equal(18, consumed1);

        var (frag2, consumed2) = MessageFragment.TryParse(buf.AsSpan(consumed1));
        Assert.NotNull(frag2);
        Assert.Equal(17, consumed2);
        Assert.Equal(new byte[] { 0xCC }, frag2!.Value.Payload);
    }
}
