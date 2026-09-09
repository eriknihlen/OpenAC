using System;
using System.Collections.Generic;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class TurbineChatTests
{
    // ──────────────────────────────────────────────────────────────
    // Round-trip fixtures (parse, then re-serialise, then parse again)
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_EventSendToRoom_GoldenFixture()
    {
        byte[] fixture = HexDecode(
            "DEF700005E000000010000000100000001000000" +
            "B5000B0001000000B5000B00000000003E000000" +
            "020000000541006C006900630065000B68006500" +
            "6C006C006F00200077006F0072006C0064000C00" +
            "0000010000500000000002000000");

        var parsed = TurbineChat.TryParse(fixture);
        Assert.NotNull(parsed);
        Assert.Equal(TurbineChat.BlobType.EventBinary, parsed!.Value.BlobType);
        Assert.Equal(TurbineChat.DispatchType.SendToRoomByName, parsed.Value.DispatchType);
        Assert.Equal(1u, parsed.Value.TargetType);
        Assert.Equal(0x000B00B5u, parsed.Value.TargetId);
        Assert.Equal(1u, parsed.Value.TransportType);
        Assert.Equal(0x000B00B5u, parsed.Value.TransportId);
        Assert.Equal(0u, parsed.Value.Cookie);

        var ev = Assert.IsType<TurbineChat.Payload.EventSendToRoom>(parsed.Value.Body);
        Assert.Equal(2u, ev.RoomId);            // TurbineChatChannel.General
        Assert.Equal("Alice", ev.SenderName);
        Assert.Equal("hello world", ev.Message);
        Assert.Equal(0x0Cu, ev.ExtraDataSize);
        Assert.Equal(0x50000001u, ev.SenderId);
        Assert.Equal(0, ev.HResult);
        Assert.Equal(2u, ev.ChatType);          // TurbineChatType.General
    }

    [Fact]
    public void Build_EventSendToRoom_RoundTripsThroughTryParse()
    {
        byte[] built = TurbineChat.Build(
            blobType: TurbineChat.BlobType.EventBinary,
            dispatchType: TurbineChat.DispatchType.SendToRoomByName,
            targetType: 1u,
            targetId: 0x000B00B5u,
            transportType: 1u,
            transportId: 0x000B00B5u,
            cookie: 0u,
            payload: new TurbineChat.Payload.EventSendToRoom(
                RoomId: 2u,
                SenderName: "Alice",
                Message: "hello world",
                ExtraDataSize: 0x0Cu,
                SenderId: 0x50000001u,
                HResult: 0,
                ChatType: 2u));

        var parsed = TurbineChat.TryParse(built);
        Assert.NotNull(parsed);
        var ev = Assert.IsType<TurbineChat.Payload.EventSendToRoom>(parsed!.Value.Body);
        Assert.Equal("Alice", ev.SenderName);
        Assert.Equal("hello world", ev.Message);
    }

    [Fact]
    public void TryParse_Response_GoldenFixture()
    {
        byte[] fixture = HexDecode(
            "DEF7000038000000050000000100000001000000" +
            "B5000B0001000000B5000B00000000001800000007000000" +
            "020000000200000000000000");

        var parsed = TurbineChat.TryParse(fixture);
        Assert.NotNull(parsed);
        Assert.Equal(TurbineChat.BlobType.ResponseBinary, parsed!.Value.BlobType);
        Assert.Equal(TurbineChat.DispatchType.SendToRoomByName, parsed.Value.DispatchType);
        var resp = Assert.IsType<TurbineChat.Payload.Response>(parsed.Value.Body);
        Assert.Equal(7u, resp.ContextId);
        Assert.Equal(2u, resp.ResponseId);
        Assert.Equal(2u, resp.MethodId);
        Assert.Equal(0, resp.HResult);
    }

    [Fact]
    public void Build_Response_RoundTripsThroughTryParse()
    {
        byte[] built = TurbineChat.Build(
            blobType: TurbineChat.BlobType.ResponseBinary,
            dispatchType: TurbineChat.DispatchType.SendToRoomByName,
            targetType: 1u, targetId: 0x000B00B5u,
            transportType: 1u, transportId: 0x000B00B5u,
            cookie: 0u,
            payload: new TurbineChat.Payload.Response(
                ContextId: 7u, ResponseId: 2u, MethodId: 2u, HResult: 0));

        var parsed = TurbineChat.TryParse(built);
        Assert.NotNull(parsed);
        var resp = Assert.IsType<TurbineChat.Payload.Response>(parsed!.Value.Body);
        Assert.Equal(7u, resp.ContextId);
    }

    [Fact]
    public void TryParse_RequestSendToRoomById_GoldenFixture()
    {
        byte[] fixture = HexDecode(
            "DEF700005D000000030000000200000001000000" +
            "00000000000000000000000000000000" +
            "3D000000" +
            "07000000020000000200000002000000" +
            "0A7400720061006400650020007300700061006D00" +
            "0C000000010000500000000003000000");

        var parsed = TurbineChat.TryParse(fixture);
        Assert.NotNull(parsed);
        Assert.Equal(TurbineChat.BlobType.RequestBinary, parsed!.Value.BlobType);
        Assert.Equal(TurbineChat.DispatchType.SendToRoomById, parsed.Value.DispatchType);
        var req = Assert.IsType<TurbineChat.Payload.RequestSendToRoomById>(parsed.Value.Body);
        Assert.Equal(7u, req.ContextId);
        Assert.Equal(2u, req.RoomId);                  // TurbineChatChannel.General
        Assert.Equal("trade spam", req.Message);
        Assert.Equal(0x0Cu, req.ExtraDataSize);
        Assert.Equal(0x50000001u, req.SenderId);
        Assert.Equal(0, req.HResult);
        Assert.Equal(3u, req.ChatType);                // TurbineChatType.Trade
    }

    [Fact]
    public void Build_RequestSendToRoomById_RoundTripsThroughTryParse()
    {
        byte[] built = TurbineChat.Build(
            blobType: TurbineChat.BlobType.RequestBinary,
            dispatchType: TurbineChat.DispatchType.SendToRoomById,
            targetType: 1u, targetId: 0u,
            transportType: 0u, transportId: 0u,
            cookie: 0u,
            payload: new TurbineChat.Payload.RequestSendToRoomById(
                ContextId: 7u,
                RoomId: 2u,
                Message: "trade spam",
                ExtraDataSize: 0x0Cu,
                SenderId: 0x50000001u,
                HResult: 0,
                ChatType: 3u));

        var parsed = TurbineChat.TryParse(built);
        Assert.NotNull(parsed);
        var req = Assert.IsType<TurbineChat.Payload.RequestSendToRoomById>(parsed!.Value.Body);
        Assert.Equal("trade spam", req.Message);
        Assert.Equal(2u, req.RoomId);
    }

    [Fact]
    public void TryParse_RequestRejectsNonAceRequestIds()
    {
        byte[] fixture = HexDecode(
            "DEF700005D000000030000000200000001000000" +
            "00000000000000000000000000000000" +
            "3D000000" +
            "07000000020000000200000002000000" +
            "0A7400720061006400650020007300700061006D00" +
            "0C000000010000500000000003000000");

        // Mutate response_id (offset 4 + 36 + 4 = 44) to 3 — must reject.
        BitConverter.GetBytes(3u).CopyTo(fixture, 44);
        Assert.Null(TurbineChat.TryParse(fixture));
    }


    [Fact]
    public void TurbineString_ShortPrefix_RoundTrips()
    {
        var buf = new List<byte>();
        TurbineChat.WriteTurbineString(buf, "Hello");
        Assert.Equal(11, buf.Count);
        Assert.Equal(5, buf[0]);                  // length prefix
        Assert.Equal(0, 0x80 & buf[0]);

        byte[] data = buf.ToArray();
        int pos = 0;
        string roundTrip = TurbineChat.ReadTurbineString(data, ref pos);
        Assert.Equal("Hello", roundTrip);
        Assert.Equal(data.Length, pos);
    }

    [Fact]
    public void TurbineString_LongPrefix_RoundTrips()
    {
        string s = new string('a', 200);
        var buf = new List<byte>();
        TurbineChat.WriteTurbineString(buf, s);

        // Prefix: 2 bytes; high bit of first byte set.
        Assert.NotEqual(0, buf[0] & 0x80);
        // Decoded length = ((b0 & 0x7F) << 8) | b1
        int decodedLen = ((buf[0] & 0x7F) << 8) | buf[1];
        Assert.Equal(200, decodedLen);
        Assert.Equal(2 + 200 * 2, buf.Count);

        byte[] data = buf.ToArray();
        int pos = 0;
        string roundTrip = TurbineChat.ReadTurbineString(data, ref pos);
        Assert.Equal(s, roundTrip);
        Assert.Equal(data.Length, pos);
    }

    [Fact]
    public void TurbineString_NonAscii_RoundTripsAsUtf16LE()
    {
        const string s = "Café";
        var buf = new List<byte>();
        TurbineChat.WriteTurbineString(buf, s);
        // Bytes: prefix(1) + UTF-16LE C-a-f-é = 1 + 8 = 9 bytes.
        Assert.Equal(9, buf.Count);
        Assert.Equal(4, buf[0]);
        // 'é' = 0xE9 0x00 in UTF-16LE.
        Assert.Equal(0xE9, buf[1 + 6]);
        Assert.Equal(0x00, buf[1 + 7]);

        byte[] data = buf.ToArray();
        int pos = 0;
        string roundTrip = TurbineChat.ReadTurbineString(data, ref pos);
        Assert.Equal(s, roundTrip);
    }

    [Fact]
    public void TurbineString_EmptyString_RoundTrips()
    {
        var buf = new List<byte>();
        TurbineChat.WriteTurbineString(buf, "");
        Assert.Single(buf);
        Assert.Equal(0, buf[0]);

        byte[] data = buf.ToArray();
        int pos = 0;
        Assert.Equal("", TurbineChat.ReadTurbineString(data, ref pos));
        Assert.Equal(1, pos);
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private static byte[] HexDecode(string hex)
    {
        if ((hex.Length & 1) != 0) throw new ArgumentException("hex length must be even");
        byte[] result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return result;
    }
}
