using System.Buffers.Binary;
using System.Text;

namespace AcDream.Core.Net.Packets;

public sealed class PacketWriter
{
    private byte[] _buffer;
    private int _position;

    public PacketWriter(int initialCapacity = 256)
    {
        _buffer = new byte[initialCapacity];
        _position = 0;
    }

    public int Position => _position;

    /// <summary>Bytes written so far, as a freshly-allocated array.</summary>
    public byte[] ToArray()
    {
        var copy = new byte[_position];
        Array.Copy(_buffer, copy, _position);
        return copy;
    }

    /// <summary>Bytes written so far, as a span view over the internal buffer.
    /// Do not hold on to this after more writes — the buffer may be reallocated.</summary>
    public ReadOnlySpan<byte> AsSpan() => _buffer.AsSpan(0, _position);

    private void EnsureCapacity(int additional)
    {
        int required = _position + additional;
        if (required <= _buffer.Length) return;
        int newSize = _buffer.Length;
        while (newSize < required) newSize *= 2;
        var bigger = new byte[newSize];
        Array.Copy(_buffer, bigger, _position);
        _buffer = bigger;
    }

    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_position++] = value;
    }

    public void WriteUInt16(ushort value)
    {
        EnsureCapacity(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_position), value);
        _position += 2;
    }

    public void WriteUInt32(uint value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_position));
        _position += bytes.Length;
    }

    public void WriteFloat(float value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    public void WriteDouble(double value)
    {
        EnsureCapacity(8);
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

    /// <summary>Pad with zeros so the buffer length is a multiple of 4.</summary>
    public void AlignTo4()
    {
        int padding = (4 - (_position & 3)) & 3;
        EnsureCapacity(padding);
        for (int i = 0; i < padding; i++) _buffer[_position++] = 0;
    }

    public void Pad(int count)
    {
        if (count <= 0) return;
        EnsureCapacity(count);
        for (int i = 0; i < count; i++) _buffer[_position++] = 0;
    }

    /// <summary>
    /// Write a String16L: u16 length + ASCII bytes + pad-to-4 from the
    /// start of the length prefix.
    /// </summary>
    public void WriteString16L(string value)
    {
        value ??= string.Empty;
        int asciiLen = value.Length;
        WriteUInt16((ushort)asciiLen);
        if (asciiLen > 0)
        {
            EnsureCapacity(asciiLen);
            // Encoding.ASCII is sufficient for dev account names/passwords.
            int written = Encoding.ASCII.GetBytes(value, 0, asciiLen, _buffer, _position);
            _position += written;
        }
        // Pad from the start of the string record (u16 + asciiLen).
        int recordSize = 2 + asciiLen;
        int padding = (4 - (recordSize & 3)) & 3;
        Pad(padding);
    }

    public void WriteString32L(string value)
    {
        value ??= string.Empty;
        if (value.Length > 255)
            throw new ArgumentException($"String32L only supports short strings (≤255), got {value.Length}", nameof(value));

        int asciiLen = value.Length;
        if (asciiLen == 0)
        {
            WriteUInt32(0);
            return;
        }

        WriteUInt32((uint)(asciiLen + 1));  // outer length includes the marker
        WriteByte(0);                        // marker byte — value is ignored
        if (asciiLen > 0)
        {
            EnsureCapacity(asciiLen);
            int written = Encoding.ASCII.GetBytes(value, 0, asciiLen, _buffer, _position);
            _position += written;
        }
        // Pad from start of the u32 length prefix: 4 + 1 + asciiLen.
        int recordSize = 4 + 1 + asciiLen;
        int padding = (4 - (recordSize & 3)) & 3;
        Pad(padding);
    }
}
