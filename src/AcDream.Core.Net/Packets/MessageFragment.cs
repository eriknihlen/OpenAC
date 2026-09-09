namespace AcDream.Core.Net.Packets;

public readonly record struct MessageFragment(MessageFragmentHeader Header, byte[] Payload)
{
    /// <summary>Total bytes of this fragment on the wire (header + payload).</summary>
    public int WireSize => MessageFragmentHeader.Size + Payload.Length;

    public static (MessageFragment? fragment, int consumed) TryParse(ReadOnlySpan<byte> source)
    {
        if (!TryParseLayout(
                source,
                out MessageFragmentHeader header,
                out int payloadLength,
                out int consumed))
        {
            return (null, 0);
        }

        byte[] payload = source
            .Slice(MessageFragmentHeader.Size, payloadLength)
            .ToArray();
        return (new MessageFragment(header, payload), consumed);
    }

    internal static bool TryParseBorrowed(
        ReadOnlyMemory<byte> source,
        out BorrowedMessageFragment fragment,
        out int consumed)
    {
        if (!TryParseLayout(
                source.Span,
                out MessageFragmentHeader header,
                out int payloadLength,
                out consumed))
        {
            fragment = default;
            return false;
        }

        fragment = new BorrowedMessageFragment(
            header,
            source.Slice(
                MessageFragmentHeader.Size,
                payloadLength));
        return true;
    }

    internal static bool TryParseLayout(
        ReadOnlySpan<byte> source,
        out MessageFragmentHeader header,
        out int payloadLength,
        out int consumed)
    {
        header = default;
        payloadLength = 0;
        consumed = 0;
        if (source.Length < MessageFragmentHeader.Size)
            return false;

        header = MessageFragmentHeader.Unpack(source);

        if (header.TotalSize < MessageFragmentHeader.Size
            || header.TotalSize > MessageFragmentHeader.MaxFragmentSize
            || header.Count == 0
            || header.Index >= header.Count)
        {
            return false;
        }

        payloadLength =
            header.TotalSize - MessageFragmentHeader.Size;
        if (source.Length < header.TotalSize)
            return false;

        consumed = header.TotalSize;
        return true;
    }
}
