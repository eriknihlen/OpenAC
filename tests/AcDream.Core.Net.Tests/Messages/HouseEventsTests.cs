using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class HouseEventsTests
{
    [Fact]
    public void BuildHouseQuery_WritesEnvelopeSequenceOpcodeOnly()
    {
        byte[] body = ClientCommandRequests.BuildHouseQuery(9);

        Assert.Equal(12, body.Length);
        Assert.Equal(ClientCommandRequests.HouseQueryOpcode,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
    }

    [Fact]
    public void ParseHouseStatus_ReadsWeenieError()
    {
        byte[] wire = new AceWireWriter().Write(0u).ToArray();

        Assert.Equal(0u, GameEvents.ParseHouseStatus(wire));
    }

    [Fact]
    public void ParseHouseStatus_TruncatedPayload_ReturnsNull()
    {
        Assert.Null(GameEvents.ParseHouseStatus(System.Array.Empty<byte>()));
    }

    [Fact]
    public void ParseUpdateRentTime_ReadsTimestamp()
    {
        byte[] wire = new AceWireWriter().Write(1_700_000_000u).ToArray();

        Assert.Equal(1_700_000_000u, GameEvents.ParseUpdateRentTime(wire));
    }

    [Fact]
    public void ParseUpdateRentPayment_EmptyList_RoundTrips()
    {
        byte[] wire = new AceWireWriter().Write(0).ToArray();

        var payments = GameEvents.ParseUpdateRentPayment(wire);

        Assert.NotNull(payments);
        Assert.Empty(payments);
    }

    [Fact]
    public void ParseUpdateRentPayment_OneEntry_RoundTrips()
    {
        byte[] wire = new AceWireWriter()
            .Write(1)
            .Write(400)      // Num
            .Write(150)      // Paid
            .Write(273u)     // WeenieID (pyreal)
            .WriteString16L("Pyreal")
            .WriteString16L("Pyreals")
            .ToArray();

        var payments = GameEvents.ParseUpdateRentPayment(wire);

        Assert.NotNull(payments);
        GameEvents.HousePayment payment = Assert.Single(payments);
        Assert.Equal(400, payment.Num);
        Assert.Equal(150, payment.Paid);
        Assert.Equal(273u, payment.WeenieID);
        Assert.Equal("Pyreal", payment.Name);
        Assert.Equal("Pyreals", payment.PluralName);
    }

    [Fact]
    public void ParseHouseData_NoHouseOwned_EmptyListsAndZeroFields()
    {
        byte[] wire = new AceWireWriter()
            .Write(0u)  // BuyTime
            .Write(0u)  // RentTime
            .Write(0u)  // Type (Undef)
            .Write(0u)  // MaintenanceFree
            .Write(0)
            .Write(0)
            .Write(0x00120001u)
            .Write(10f).Write(20f).Write(30f)
            .Write(1f).Write(0f).Write(0f).Write(0f)
            .ToArray();

        GameEvents.HouseData? data = GameEvents.ParseHouseData(wire);

        Assert.NotNull(data);
        Assert.Equal(0u, data!.Value.BuyTime);
        Assert.Empty(data.Value.Buy);
        Assert.Empty(data.Value.Rent);
        Assert.Equal(0x00120001u, data.Value.Position.LandblockId);
        Assert.Equal(10f, data.Value.Position.PositionX);
        Assert.Equal(30f, data.Value.Position.PositionZ);
        Assert.Equal(1f, data.Value.Position.RotationW);
    }

    [Fact]
    public void ParseHouseData_OwnedHouse_ReadsBuyAndRentLists()
    {
        byte[] wire = new AceWireWriter()
            .Write(1_650_000_000u)  // BuyTime
            .Write(1_699_000_000u)  // RentTime
            .Write(1u)
            .Write(0u)               // MaintenanceFree = false
            .Write(1)
                .Write(1).Write(1).Write(273u)
                .WriteString16L("Pyreal").WriteString16L("Pyreals")
            .Write(2)
                .Write(300).Write(300).Write(273u)
                .WriteString16L("Pyreal").WriteString16L("Pyreals")
                .Write(1).Write(0).Write(1049u)
                .WriteString16L("Writ of the Chosen").WriteString16L("Writs of the Chosen")
            // Position
            .Write(0x00340002u)
            .Write(-15f).Write(45f).Write(0f)
            .Write(0.7071f).Write(0f).Write(0f).Write(0.7071f)
            .ToArray();

        GameEvents.HouseData? data = GameEvents.ParseHouseData(wire);

        Assert.NotNull(data);
        Assert.Equal(1_650_000_000u, data!.Value.BuyTime);
        Assert.Equal(1_699_000_000u, data.Value.RentTime);
        Assert.Equal(1u, data.Value.Type);
        Assert.False(data.Value.MaintenanceFree);
        Assert.Single(data.Value.Buy);
        Assert.Equal(2, data.Value.Rent.Count);
        Assert.Equal("Writ of the Chosen", data.Value.Rent[1].Name);
        Assert.Equal(0x00340002u, data.Value.Position.LandblockId);
    }

    [Fact]
    public void ParseHouseData_TruncatedPayload_ReturnsNull()
    {
        byte[] wire = new AceWireWriter().Write(0u).Write(0u).ToArray();

        Assert.Null(GameEvents.ParseHouseData(wire));
    }
}
