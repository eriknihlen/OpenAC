using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace AcDream.Core.Net.Messages;

public static class TurbineChat
{
    /// <summary>Top-level GameMessage opcode.</summary>
    public const uint Opcode = 0xF7DEu;

    private const uint SendToRoomByIdResponseId = 2;

    private const uint SendToRoomByIdMethodId = 2;

    public const int HeaderSize = 36;


    public enum BlobType : uint
    {
        Unknown        = 0,
        EventBinary    = 1,
        EventXmlRpc    = 2,
        RequestBinary  = 3,
        RequestXmlRpc  = 4,
        ResponseBinary = 5,
        ResponseXmlRpc = 6,
    }

    public enum DispatchType : uint
    {
        Unknown                = 0,
        SendToRoomByName       = 1,
        SendToRoomById         = 2,
        CreateRoom             = 3,
        InviteClientToRoomById = 4,
        EjectClientFromRoomById = 5,
    }

    public enum ChatType : uint
    {
        Undef         = 0,
        Allegiance    = 1,
        General       = 2,
        Trade         = 3,
        Lfg           = 4,
        Roleplay      = 5,
        Society       = 6,
        SocietyCelHan = 7,
        SocietyEldWeb = 8,
        SocietyRadBlo = 9,
        Olthoi        = 10,
    }


    /// <summary>Discriminated union over the three known payload shapes.</summary>
    public abstract record Payload
    {
        public sealed record EventSendToRoom(
            uint RoomId,
            string SenderName,
            string Message,
            uint ExtraDataSize,
            uint SenderId,
            int HResult,
            uint ChatType) : Payload;

        public sealed record RequestSendToRoomById(
            uint ContextId,
            uint RoomId,
            string Message,
            uint ExtraDataSize,
            uint SenderId,
            int HResult,
            uint ChatType) : Payload;

        public sealed record Response(
            uint ContextId,
            uint ResponseId,
            uint MethodId,
            int HResult) : Payload;

        public sealed record Unknown(byte[] Bytes) : Payload;
    }

    public readonly record struct Parsed(
        BlobType BlobType,
        DispatchType DispatchType,
        uint TargetType,
        uint TargetId,
        uint TransportType,
        uint TransportId,
        uint Cookie,
        Payload Body);

    // ──────────────────────────────────────────────────────────────
    // Parse — body INCLUDES the leading 0xF7DE opcode word.
    // ──────────────────────────────────────────────────────────────

    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4 + HeaderSize) return null;
        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (opcode != Opcode) return null;

        try
        {
            int pos = 4;
            uint sizeFirst       = ReadU32(body, ref pos);
            uint blobTypeRaw     = ReadU32(body, ref pos);
            uint dispatchTypeRaw = ReadU32(body, ref pos);
            uint targetType      = ReadU32(body, ref pos);
            uint targetId        = ReadU32(body, ref pos);
            uint transportType   = ReadU32(body, ref pos);
            uint transportId     = ReadU32(body, ref pos);
            uint cookie          = ReadU32(body, ref pos);
            uint sizeSecond      = ReadU32(body, ref pos);   // = 8 + payload.len

            if (sizeSecond < 8) return null;
            int expectedPayload = checked((int)(sizeSecond - 8));
            if (body.Length - pos < expectedPayload) return null;

            // Validate blob_type / dispatch_type discriminants — Rust's
            // from_repr would return None for unknown values; we mirror
            // by treating those as "Unknown payload bytes" rather than
            // hard-rejecting (be permissive on unrecognised servers).
            if (!IsKnownBlobType(blobTypeRaw)) return null;
            if (!IsKnownDispatchType(dispatchTypeRaw)) return null;

            BlobType     blobType     = (BlobType)blobTypeRaw;
            DispatchType dispatchType = (DispatchType)dispatchTypeRaw;
            int payloadStart = pos;

            Payload payload;
            switch ((blobType, dispatchType))
            {
                case (BlobType.EventBinary, DispatchType.SendToRoomByName):
                {
                    uint   channelId   = ReadU32(body, ref pos);
                    string senderName  = ReadTurbineString(body, ref pos);
                    string message     = ReadTurbineString(body, ref pos);
                    if (body.Length - pos < 16) return null;
                    uint extraDataSize = ReadU32(body, ref pos);
                    uint senderId      = ReadU32(body, ref pos);
                    int  hresult       = (int)ReadU32(body, ref pos);
                    uint chatTypeRaw   = ReadU32(body, ref pos);

                    payload = new Payload.EventSendToRoom(
                        RoomId:        channelId,
                        SenderName:    senderName,
                        Message:       message,
                        ExtraDataSize: extraDataSize,
                        SenderId:      senderId,
                        HResult:       hresult,
                        ChatType:      chatTypeRaw);
                    break;
                }

                case (BlobType.RequestBinary, DispatchType.SendToRoomById):
                {
                    if (body.Length - pos < 16) return null;
                    uint contextId  = ReadU32(body, ref pos);
                    uint responseId = ReadU32(body, ref pos);
                    uint methodId   = ReadU32(body, ref pos);
                    uint roomId     = ReadU32(body, ref pos);

                    if (responseId != SendToRoomByIdResponseId
                        || methodId != SendToRoomByIdMethodId)
                        return null;

                    string message = ReadTurbineString(body, ref pos);
                    if (body.Length - pos < 16) return null;
                    uint extraDataSize = ReadU32(body, ref pos);
                    uint senderId      = ReadU32(body, ref pos);
                    int  hresult       = (int)ReadU32(body, ref pos);
                    uint chatTypeRaw   = ReadU32(body, ref pos);

                    payload = new Payload.RequestSendToRoomById(
                        ContextId:     contextId,
                        RoomId:        roomId,
                        Message:       message,
                        ExtraDataSize: extraDataSize,
                        SenderId:      senderId,
                        HResult:       hresult,
                        ChatType:      chatTypeRaw);
                    break;
                }

                case (BlobType.ResponseBinary, _):
                {
                    if (body.Length - pos < 16) return null;
                    uint contextId  = ReadU32(body, ref pos);
                    uint responseId = ReadU32(body, ref pos);
                    uint methodId   = ReadU32(body, ref pos);
                    int  hresult    = (int)ReadU32(body, ref pos);

                    payload = new Payload.Response(
                        ContextId:  contextId,
                        ResponseId: responseId,
                        MethodId:   methodId,
                        HResult:    hresult);
                    break;
                }

                default:
                {
                    int remaining = expectedPayload;
                    if (body.Length - pos < remaining) return null;
                    var bytes = new byte[remaining];
                    body.Slice(pos, remaining).CopyTo(bytes);
                    pos += remaining;
                    payload = new Payload.Unknown(bytes);
                    break;
                }
            }

            int consumed = pos - payloadStart;
            if (consumed < expectedPayload)
            {
                int slack = expectedPayload - consumed;
                if (body.Length - pos < slack) return null;
                pos += slack;
            }

            _ = sizeFirst;

            return new Parsed(
                BlobType:     blobType,
                DispatchType: dispatchType,
                TargetType:   targetType,
                TargetId:     targetId,
                TransportType: transportType,
                TransportId:   transportId,
                Cookie:        cookie,
                Body:          payload);
        }
        catch
        {
            return null;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Build — produces the full GameMessage body (with the opcode).
    // ──────────────────────────────────────────────────────────────

    public static byte[] Build(
        BlobType blobType,
        DispatchType dispatchType,
        uint targetType,
        uint targetId,
        uint transportType,
        uint transportId,
        uint cookie,
        Payload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // Step 1: serialise the payload to its own buffer so we know its size.
        var payloadBuf = new List<byte>(64);
        WritePayload(payloadBuf, payload);

        uint payloadLen = (uint)payloadBuf.Count;
        uint sizeFirst  = 40u + payloadLen;
        uint sizeSecond = 8u  + payloadLen;

        // Step 2: assemble the framed GameMessage: opcode (4) + header (36) + payload.
        var buf = new byte[4 + HeaderSize + payloadBuf.Count];
        var span = buf.AsSpan();
        int pos = 0;

        WriteU32(span, ref pos, Opcode);
        WriteU32(span, ref pos, sizeFirst);
        WriteU32(span, ref pos, (uint)blobType);
        WriteU32(span, ref pos, (uint)dispatchType);
        WriteU32(span, ref pos, targetType);
        WriteU32(span, ref pos, targetId);
        WriteU32(span, ref pos, transportType);
        WriteU32(span, ref pos, transportId);
        WriteU32(span, ref pos, cookie);
        WriteU32(span, ref pos, sizeSecond);

        for (int i = 0; i < payloadBuf.Count; i++)
            buf[pos + i] = payloadBuf[i];

        return buf;
    }

    private static void WritePayload(List<byte> buf, Payload payload)
    {
        switch (payload)
        {
            case Payload.EventSendToRoom e:
                AppendU32(buf, e.RoomId);
                WriteTurbineString(buf, e.SenderName);
                WriteTurbineString(buf, e.Message);
                AppendU32(buf, e.ExtraDataSize);
                AppendU32(buf, e.SenderId);
                AppendU32(buf, unchecked((uint)e.HResult));
                AppendU32(buf, e.ChatType);
                break;

            case Payload.RequestSendToRoomById r:
                AppendU32(buf, r.ContextId);
                AppendU32(buf, SendToRoomByIdResponseId);
                AppendU32(buf, SendToRoomByIdMethodId);
                AppendU32(buf, r.RoomId);
                WriteTurbineString(buf, r.Message);
                AppendU32(buf, r.ExtraDataSize);
                AppendU32(buf, r.SenderId);
                AppendU32(buf, unchecked((uint)r.HResult));
                AppendU32(buf, r.ChatType);
                break;

            case Payload.Response resp:
                AppendU32(buf, resp.ContextId);
                AppendU32(buf, resp.ResponseId);
                AppendU32(buf, resp.MethodId);
                AppendU32(buf, unchecked((uint)resp.HResult));
                break;

            case Payload.Unknown u:
                buf.AddRange(u.Bytes);
                break;
        }
    }


    public static string ReadTurbineString(
        ReadOnlySpan<byte> data,
        ref int pos)
    {
        if (data.Length - pos < 1) throw new FormatException("turbine str: truncated len");
        int chars = data[pos];
        pos += 1;

        if ((chars & 0x80) != 0)
        {
            if (data.Length - pos < 1) throw new FormatException("turbine str: truncated len2");
            chars = ((chars & 0x7F) << 8) | data[pos];
            pos += 1;
        }

        long bytesLen = (long)chars * 2L;
        if (bytesLen > int.MaxValue || data.Length - pos < bytesLen)
            throw new FormatException("turbine str: truncated body");

        // String.from_utf16 in Rust validates surrogate pairs; .NET's
        // Encoding.Unicode.GetString matches that semantics.
        string s = Encoding.Unicode.GetString(
            data.Slice(pos, (int)bytesLen));
        pos += (int)bytesLen;
        return s;
    }

    public static void WriteTurbineString(List<byte> buf, string s)
    {
        ArgumentNullException.ThrowIfNull(buf);
        ArgumentNullException.ThrowIfNull(s);

        int chars = s.Length;
        if (chars >= 0x8000)
            throw new ArgumentException(
                "turbine string exceeds 2-byte length prefix range (max 0x7FFF code units)",
                nameof(s));

        if (chars < 0x80)
        {
            buf.Add((byte)chars);
        }
        else
        {
            buf.Add((byte)(0x80 | ((chars >> 8) & 0x7F)));
            buf.Add((byte)(chars & 0xFF));
        }

        byte[] utf16 = Encoding.Unicode.GetBytes(s);
        buf.AddRange(utf16);
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private static uint ReadU32(
        ReadOnlySpan<byte> data,
        ref int pos)
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(
            data.Slice(pos, 4));
        pos += 4;
        return v;
    }

    private static void WriteU32(Span<byte> buf, ref int pos, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(pos, 4), value);
        pos += 4;
    }

    private static void AppendU32(List<byte> buf, uint value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, value);
        buf.Add(tmp[0]);
        buf.Add(tmp[1]);
        buf.Add(tmp[2]);
        buf.Add(tmp[3]);
    }

    private static bool IsKnownBlobType(uint v) => v <= 6;
    private static bool IsKnownDispatchType(uint v) => v <= 5;
}
