using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Messages;

public static class GameMessageFragment
{
    public const uint OutboundFragmentId = 0x80000000u;

    public static MessageFragment BuildSingleFragment(
        uint fragmentSequence,
        GameMessageGroup queue,
        ReadOnlySpan<byte> gameMessageBytes)
    {
        if (gameMessageBytes.Length > MessageFragmentHeader.MaxFragmentDataSize)
            throw new ArgumentException(
                $"game message body ({gameMessageBytes.Length} bytes) exceeds single-fragment capacity " +
                $"({MessageFragmentHeader.MaxFragmentDataSize} bytes). Multi-fragment split TBD.",
                nameof(gameMessageBytes));

        var header = new MessageFragmentHeader
        {
            Sequence  = fragmentSequence,
            Id        = OutboundFragmentId,
            Count     = 1,
            TotalSize = (ushort)(MessageFragmentHeader.Size + gameMessageBytes.Length),
            Index     = 0,
            Queue     = (ushort)queue,
        };

        return new MessageFragment(header, gameMessageBytes.ToArray());
    }

    internal static int WriteSingleFragment(
        Span<byte> destination,
        uint fragmentSequence,
        GameMessageGroup queue,
        ReadOnlySpan<byte> gameMessageBytes)
    {
        if (gameMessageBytes.Length
            > MessageFragmentHeader.MaxFragmentDataSize)
        {
            throw new ArgumentException(
                $"game message body ({gameMessageBytes.Length} bytes) exceeds single-fragment capacity "
                + $"({MessageFragmentHeader.MaxFragmentDataSize} bytes). Multi-fragment split TBD.",
                nameof(gameMessageBytes));
        }

        int wireSize =
            MessageFragmentHeader.Size + gameMessageBytes.Length;
        if (destination.Length < wireSize)
        {
            throw new ArgumentException(
                $"destination must be at least {wireSize} bytes",
                nameof(destination));
        }

        var header = new MessageFragmentHeader
        {
            Sequence = fragmentSequence,
            Id = OutboundFragmentId,
            Count = 1,
            TotalSize = checked((ushort)wireSize),
            Index = 0,
            Queue = (ushort)queue,
        };
        header.Pack(destination);
        gameMessageBytes.CopyTo(
            destination.Slice(MessageFragmentHeader.Size));
        return wireSize;
    }

    public static byte[] Serialize(in MessageFragment fragment)
    {
        byte[] buffer = new byte[MessageFragmentHeader.Size + fragment.Payload.Length];
        fragment.Header.Pack(buffer);
        fragment.Payload.CopyTo(buffer.AsSpan(MessageFragmentHeader.Size));
        return buffer;
    }
}

public enum GameMessageGroup : ushort
{
    InvalidQueue       = 0x00,
    EventQueue         = 0x01,
    ControlQueue       = 0x02,
    WeenieQueue        = 0x03,
    LoginQueue         = 0x04,
    DatabaseQueue      = 0x05,
    SecureControlQueue = 0x06,
    SecureWeenieQueue  = 0x07,
    SecureLoginQueue   = 0x08,
    UIQueue            = 0x09,
    SmartboxQueue      = 0x0A,
    ObserverQueue      = 0x0B,
}
