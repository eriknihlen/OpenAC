using System;
using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public readonly record struct GameEventEnvelope(
    uint PlayerGuid,
    uint Sequence,
    GameEventType EventType,
    ReadOnlyMemory<byte> Payload)
{
    /// <summary>GameMessage opcode of the outer envelope.</summary>
    public const uint Opcode = 0xF7B0u;

    /// <summary>Header size (4×u32 = 16 bytes before payload).</summary>
    public const int HeaderSize = 16;

    /// <summary>
    /// Parse a raw 0xF7B0 GameMessage body into the header + payload view.
    /// Returns null if the body is shorter than the header or the outer
    /// opcode doesn't match.
    /// </summary>
    public static GameEventEnvelope? TryParse(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return TryParseBorrowed(body);
    }

    /// <summary>
    /// Internal receive-path parser. The returned payload borrows
    /// <paramref name="body"/> and must not escape synchronous dispatch.
    /// </summary>
    internal static GameEventEnvelope? TryParseBorrowed(
        ReadOnlyMemory<byte> body)
    {
        if (body.Length < HeaderSize) return null;

        ReadOnlySpan<byte> span = body.Span;
        uint outer = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (outer != Opcode) return null;

        uint guid      = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4));
        uint sequence  = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8));
        uint eventType = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12));

        ReadOnlyMemory<byte> payload = body.Slice(HeaderSize);
        return new GameEventEnvelope(guid, sequence, (GameEventType)eventType, payload);
    }
}
