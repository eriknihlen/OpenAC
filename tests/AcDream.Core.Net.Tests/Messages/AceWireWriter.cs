using System;
using System.Collections.Generic;
using System.Text;

namespace AcDream.Core.Net.Tests.Messages;

internal sealed class AceWireWriter
{
    private readonly List<byte> _buffer = new();

    public int Length => _buffer.Count;

    private static uint CalculatePadMultiple(uint length, uint multiple)
        => multiple * ((length + multiple - 1u) / multiple) - length;

    /// <summary>BinaryWriter.Write(uint) — little-endian.</summary>
    public AceWireWriter Write(uint value)
    {
        _buffer.Add((byte)(value & 0xFF));
        _buffer.Add((byte)((value >> 8) & 0xFF));
        _buffer.Add((byte)((value >> 16) & 0xFF));
        _buffer.Add((byte)((value >> 24) & 0xFF));
        return this;
    }

    /// <summary>BinaryWriter.Write(ushort) — little-endian.</summary>
    public AceWireWriter Write(ushort value)
    {
        _buffer.Add((byte)(value & 0xFF));
        _buffer.Add((byte)((value >> 8) & 0xFF));
        return this;
    }

    /// <summary>BinaryWriter.Write(int) — little-endian, same bit pattern as Write(uint).</summary>
    public AceWireWriter Write(int value) => Write(unchecked((uint)value));

    /// <summary>BinaryWriter.Write(byte).</summary>
    public AceWireWriter Write(byte value)
    {
        _buffer.Add(value);
        return this;
    }

    public AceWireWriter Write(bool value) => Write(value ? (byte)1 : (byte)0);

    /// <summary>BinaryWriter.Write(ulong) — little-endian.</summary>
    public AceWireWriter Write(ulong value) =>
        Write((uint)(value & 0xFFFFFFFFu)).Write((uint)(value >> 32));

    /// <summary>BinaryWriter.Write(float) — little-endian IEEE-754.</summary>
    public AceWireWriter Write(float value)
        => Write((uint)BitConverter.SingleToInt32Bits(value));

    public AceWireWriter WriteGuid(uint guid) => Write(guid);

    public AceWireWriter WriteString16L(string? data)
    {
        data ??= "";
        byte[] bytes = Encoding.GetEncoding(1252).GetBytes(data);
        Write((ushort)data.Length);
        _buffer.AddRange(bytes);
        return Pad(CalculatePadMultiple(sizeof(ushort) + (uint)data.Length, 4u));
    }

    public AceWireWriter WritePackedDword(uint value)
    {
        if (value <= 32767)
            return Write((ushort)value);

        uint packed = (value << 16) | ((value >> 16) | 0x8000);
        return Write(packed);
    }

    public AceWireWriter Pad(uint pad)
    {
        for (uint i = 0; i < pad; i++)
            _buffer.Add(0);
        return this;
    }

    public AceWireWriter Align() => Pad(CalculatePadMultiple((uint)_buffer.Count, 4u));

    public byte[] ToArray() => _buffer.ToArray();

    public static AceWireWriter GameMessage(uint opcode)
        => new AceWireWriter().Write(opcode);

    public static AceWireWriter GameEvent(uint guid, uint eventSequence, uint eventType)
        => new AceWireWriter()
            .Write(0xF7B0u)
            .WriteGuid(guid)
            .Write(eventSequence)
            .Write(eventType);
}
