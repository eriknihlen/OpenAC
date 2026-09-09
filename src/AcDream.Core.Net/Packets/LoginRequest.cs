using System.Buffers.Binary;
using System.Text;

namespace AcDream.Core.Net.Packets;

public static class LoginRequest
{
    public enum NetAuthType : uint
    {
        Undefined       = 0x00000000,
        Account         = 0x00000001,
        AccountPassword = 0x00000002,
        GlsTicket       = 0x40000002,
    }

    public const string RetailClientVersion = "1802";

    public static byte[] Build(string account, string password, uint timestamp,
                               string clientVersion = RetailClientVersion)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(password);

        var w = new PacketWriter(128);
        w.WriteString16L(clientVersion);

        int bodyLengthOffset = w.Position;
        w.WriteUInt32(0);  // placeholder, patched below

        int bodyStart = w.Position;
        w.WriteUInt32((uint)NetAuthType.AccountPassword);
        w.WriteUInt32(0);  // AuthFlags
        w.WriteUInt32(timestamp);
        w.WriteString16L(account);
        w.WriteString16L(string.Empty);  // LoginAs (empty for non-admin)
        w.WriteString32L(password);

        // Patch bodyLength: bytes written after the u32 length itself.
        uint bodyLength = (uint)(w.Position - bodyStart);
        var buf = w.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(bodyLengthOffset), bodyLength);
        return buf;
    }

    public static Parsed Parse(ReadOnlySpan<byte> bytes)
    {
        int pos = 0;

        string clientVersion = ReadString16L(bytes, ref pos);
        if (bytes.Length - pos < 4) throw new FormatException("truncated before bodyLength");
        uint bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(pos));
        pos += 4;

        if (bytes.Length - pos < 4) throw new FormatException("truncated before NetAuthType");
        var netAuth = (NetAuthType)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(pos));
        pos += 4;
        uint authFlags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(pos));
        pos += 4;
        uint timestamp = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(pos));
        pos += 4;
        string account  = ReadString16L(bytes, ref pos);
        string loginAs  = ReadString16L(bytes, ref pos);

        string? password = null;
        string? glsTicket = null;
        if (netAuth == NetAuthType.AccountPassword)
            password = ReadString32L(bytes, ref pos);
        else if (netAuth == NetAuthType.GlsTicket)
            glsTicket = ReadString32L(bytes, ref pos);

        return new Parsed(clientVersion, bodyLength, netAuth, authFlags, timestamp, account, loginAs, password, glsTicket);
    }

    public readonly record struct Parsed(
        string ClientVersion,
        uint BodyLength,
        NetAuthType NetAuth,
        uint AuthFlags,
        uint Timestamp,
        string Account,
        string LoginAs,
        string? Password,
        string? GlsTicket);

    private static string ReadString16L(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 2) throw new FormatException("truncated String16L length");
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(pos));
        pos += 2;
        if (source.Length - pos < length) throw new FormatException("truncated String16L body");
        string result = Encoding.ASCII.GetString(source.Slice(pos, length));
        pos += length;
        int recordSize = 2 + length;
        int padding = (4 - (recordSize & 3)) & 3;
        pos += padding;
        return result;
    }

    private static string ReadString32L(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 4) throw new FormatException("truncated String32L length");
        uint outer = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(pos));
        pos += 4;
        if (outer == 0) return string.Empty;

        if (source.Length - pos < 1) throw new FormatException("truncated String32L marker");
        pos += 1;
        uint strLen = outer - 1;
        if (strLen > 255)
        {
            pos += 1;
            strLen -= 1;
        }

        if (source.Length - pos < strLen) throw new FormatException("truncated String32L body");
        string result = Encoding.ASCII.GetString(source.Slice(pos, (int)strLen));
        pos += (int)strLen;

        // Pad from start of u32 + marker(s) + strLen.
        int recordSize = 4 + (int)(outer);
        int padding = (4 - (recordSize & 3)) & 3;
        pos += padding;
        return result;
    }
}
