using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Packets;

public class LoginRequestTests
{
    [Fact]
    public void Build_Then_Parse_RoundTripsAllFields()
    {
        var bytes = LoginRequest.Build(
            account: "testaccount",
            password: "testpassword",
            timestamp: 0x12345678u);

        var parsed = LoginRequest.Parse(bytes);

        Assert.Equal(LoginRequest.RetailClientVersion, parsed.ClientVersion);
        Assert.Equal(LoginRequest.NetAuthType.AccountPassword, parsed.NetAuth);
        Assert.Equal(0u, parsed.AuthFlags);
        Assert.Equal(0x12345678u, parsed.Timestamp);
        Assert.Equal("testaccount", parsed.Account);
        Assert.Equal(string.Empty, parsed.LoginAs);
        Assert.Equal("testpassword", parsed.Password);
        Assert.Null(parsed.GlsTicket);
    }

    [Fact]
    public void Build_Then_Parse_EmptyAccountAndPassword()
    {
        var bytes = LoginRequest.Build(
            account: string.Empty,
            password: string.Empty,
            timestamp: 1);

        var parsed = LoginRequest.Parse(bytes);

        Assert.Equal(string.Empty, parsed.Account);
        Assert.Equal(string.Empty, parsed.Password);
    }

    [Fact]
    public void Build_BodyLength_ReflectsActualRemainingBytes()
    {
        var bytes = LoginRequest.Build("u", "p", 0);
        var parsed = LoginRequest.Parse(bytes);

        int clientVersionAndLengthPrefix = 8 + 4;
        int expectedBodyLength = bytes.Length - clientVersionAndLengthPrefix;
        Assert.Equal((uint)expectedBodyLength, parsed.BodyLength);
    }

    [Fact]
    public void Build_TotalWireSizeIsMultipleOfFour()
    {
        // Each field pads to a 4-byte boundary, so the total should land on one.
        var bytes = LoginRequest.Build("testaccount", "testpassword", 42);
        Assert.Equal(0, bytes.Length % 4);
    }

    [Fact]
    public void Build_DifferentCredentials_ProduceDifferentBytes()
    {
        var a = LoginRequest.Build("alice", "alicepw", 1);
        var b = LoginRequest.Build("bob",   "bobpw",   1);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void LoginRequest_EmbeddedInPacket_DecodableByPacketCodec()
    {
        var payload = LoginRequest.Build("testaccount", "testpassword", 1);

        var header = new PacketHeader
        {
            Flags = PacketHeaderFlags.LoginRequest,
            DataSize = (ushort)payload.Length,
        };
        uint headerHash = header.CalculateHeaderHash32();
        uint optionalHash = AcDream.Core.Net.Cryptography.Hash32.Calculate(payload);
        header.Checksum = headerHash + optionalHash;

        byte[] datagram = new byte[PacketHeader.Size + payload.Length];
        header.Pack(datagram);
        payload.CopyTo(datagram.AsSpan(PacketHeader.Size));

        var result = PacketCodec.TryDecode(datagram, inboundIsaac: null);
        Assert.Equal(PacketCodec.DecodeError.None, result.Error);
        Assert.NotNull(result.Packet);
        Assert.Equal(payload, result.Packet!.BodyBytes);

        var parsed = LoginRequest.Parse(result.Packet.BodyBytes);
        Assert.Equal("testaccount", parsed.Account);
        Assert.Equal("testpassword", parsed.Password);
    }
}
