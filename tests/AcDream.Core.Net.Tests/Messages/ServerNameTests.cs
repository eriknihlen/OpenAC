using System.Buffers.Binary;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class ServerNameTests
{
    [Fact]
    public void Parse_MirrorsAceSerializer_ExactFields()
    {
        var w = AceWireWriter.GameMessage(ServerName.Opcode)
            .Write(123)
            .Write(1000)
            .WriteString16L("sawato");

        ServerName.Parsed parsed = ServerName.Parse(w.ToArray());

        Assert.Equal(123, parsed.CurrentConnections);
        Assert.Equal(1000, parsed.MaxConnections);
        Assert.Equal("sawato", parsed.WorldName);
    }

    [Fact]
    public void Parse_NegativeMaxConnections_PreservesSign()
    {
        var w = AceWireWriter.GameMessage(ServerName.Opcode)
            .Write(0)
            .Write(-1)
            .WriteString16L("Frostfell");

        ServerName.Parsed parsed = ServerName.Parse(w.ToArray());

        Assert.Equal(0, parsed.CurrentConnections);
        Assert.Equal(-1, parsed.MaxConnections);
        Assert.Equal("Frostfell", parsed.WorldName);
    }

    [Fact]
    public void Parse_EmptyWorldName_RoundTrips()
    {
        var w = AceWireWriter.GameMessage(ServerName.Opcode)
            .Write(0)
            .Write(0)
            .WriteString16L(string.Empty);

        ServerName.Parsed parsed = ServerName.Parse(w.ToArray());

        Assert.Equal(string.Empty, parsed.WorldName);
    }

    [Fact]
    public void Parse_WrongOpcode_Throws()
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xDEADBEEFu);

        Assert.Throws<FormatException>(() => ServerName.Parse(bytes));
    }

    [Fact]
    public void Parse_TruncatedAfterCurrentConnections_Throws()
    {
        var w = AceWireWriter.GameMessage(ServerName.Opcode).Write(0);

        Assert.Throws<FormatException>(() => ServerName.Parse(w.ToArray()));
    }

    [Fact]
    public void Parse_TruncatedBeforeWorldName_Throws()
    {
        var w = AceWireWriter.GameMessage(ServerName.Opcode)
            .Write(0)
            .Write(0);

        Assert.Throws<FormatException>(() => ServerName.Parse(w.ToArray()));
    }
}
