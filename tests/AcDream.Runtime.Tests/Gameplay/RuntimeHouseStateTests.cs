using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Properties;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeHouseStateTests
{
    private const uint Self = 0x50000001u;

    [Fact]
    public void EmptyBeforeAnyNoticeArrives()
    {
        var house = new RuntimeHouseState();

        Assert.Empty(house.Lines);
        Assert.False(house.HasReceivedNotice);
    }

    [Fact]
    public void HouseStatus_FreshCharacterWithNoTimestamp_ShowsBothHouselessLines()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Self, Type = ItemType.Creature });
        var house = new RuntimeHouseState(objects);

        house.ApplyHouseStatus(weenieError: 0u, Self);

        Assert.True(house.HasReceivedNotice);
        Assert.Equal(
            [
                "You do not currently own a house.",
                "You may buy another house immediately.",
            ],
            house.Lines);
    }

    [Fact]
    public void HouseStatus_WeenieErrorValueIsDiscarded()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Self, Type = ItemType.Creature });
        var houseA = new RuntimeHouseState(objects);
        var houseB = new RuntimeHouseState(objects);

        houseA.ApplyHouseStatus(weenieError: 0u, Self);
        houseB.ApplyHouseStatus(weenieError: 0x45Fu /* HouseEvicted */, Self);

        Assert.Equal(houseA.Lines, houseB.Lines);
    }

    [Fact]
    public void HouseData_OwnedUnpaidCottage_ComposesEveryRetailBuilderInOrder()
    {
        var clock = new ManualTimeProvider();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Self, Type = ItemType.Creature });
        var house = new RuntimeHouseState(objects, clock);
        const uint buyTime = 1_700_000_000u;
        const uint rentTime = 1_700_086_400u;
        uint location = LandDefs.LcoordToGid(1200, 800);
        var data = new GameEvents.HouseData(
            BuyTime: buyTime,
            RentTime: rentTime,
            Type: 1u,
            MaintenanceFree: false,
            Buy:
            [
                new GameEvents.HousePayment(1, 0, 273u, "Pyreal", "Pyreals"),
                new GameEvents.HousePayment(2, 0, 274u, "Trade Note", string.Empty),
            ],
            Rent:
            [
                new GameEvents.HousePayment(10, 4, 273u, "Pyreal", "Pyreals"),
                new GameEvents.HousePayment(2, 2, 274u, "Box", string.Empty),
            ],
            Position: Position(location));

        house.ApplyHouseData(data, Self);

        Assert.Equal(
            [
                "The purchase price for this dwelling is:\n1 Pyreal, 2 Trade Notes",
                "Rent:\n4/10 Pyreals, 2/2 Boxes",
                "Bought: " + RetailTime(buyTime),
                "This maintenance period ends: " + RetailTime(rentTime + 2_592_000L),
                "Maintenance is next due: " + RetailTime(rentTime + 2_592_000L),
                "Location: 21.9S, 18.1E",
                "Warning!  You have not paid your maintenance costs for the last "
                    + "30 day maintenance period.  Please pay these costs by this deadline"
                    + " or you will lose your house, and all your items within it.",
                "You may buy another house immediately after you abandon this one.",
            ],
            house.Lines);

        Assert.All(house.PanelLines.Take(6),
            line => Assert.Equal(HousePanelTextColor.Normal, line.Color));
        Assert.Equal(HousePanelTextColor.RentNotPaid, house.PanelLines[6].Color);
        Assert.Equal(HousePanelTextColor.Normal, house.PanelLines[7].Color);
        Assert.Equal(data.Position, house.Position);
    }

    [Fact]
    public void HouseData_PaidRent_UsesSecondPeriodDueDateAndPaidColor()
    {
        const uint rentTime = 1_700_086_400u;
        var house = new RuntimeHouseState(timeProvider: new ManualTimeProvider());
        GameEvents.HouseData data = SampleHouseData() with
        {
            RentTime = rentTime,
            Type = 1u,
            Rent = [new GameEvents.HousePayment(5, 5, 1u, "Token", "Tokens")],
        };

        house.ApplyHouseData(data, Self);

        Assert.Equal(
            "Maintenance is next due: " + RetailTime(rentTime + 5_184_000L),
            house.Lines[4]);
        Assert.Equal(
            "The maintenance has already been paid for this period. "
                + "You may not prepay next period's maintenance.",
            house.Lines[^2]);
        Assert.Equal(HousePanelTextColor.RentPaid, house.PanelLines[^2].Color);
    }

    [Fact]
    public void HouseData_Apartment_UsesNinetyDayPeriodAndOmitsLocation()
    {
        const uint rentTime = 1_700_086_400u;
        var house = new RuntimeHouseState(timeProvider: new ManualTimeProvider());
        GameEvents.HouseData data = SampleHouseData() with
        {
            RentTime = rentTime,
            Type = 4u,
            MaintenanceFree = true,
            Position = Position(LandDefs.LcoordToGid(1200, 800)),
        };

        house.ApplyHouseData(data, Self);

        Assert.Equal(
            "This maintenance period ends: " + RetailTime(rentTime + 7_776_000L),
            house.Lines[3]);
        Assert.Equal(
            "Maintenance is next due: " + RetailTime(rentTime + 15_552_000L),
            house.Lines[4]);
        Assert.DoesNotContain(house.Lines, line => line.StartsWith("Location: "));
        Assert.Null(house.Position);
        Assert.Equal(HousePanelTextColor.RentPaid, house.PanelLines[^2].Color);
    }

    [Fact]
    public void RentNotices_UpdateRetainedSnapshotAndRefreshPanel()
    {
        var house = new RuntimeHouseState(timeProvider: new ManualTimeProvider());
        GameEvents.HouseData data = SampleHouseData() with
        {
            RentTime = 1_700_000_000u,
            Rent = [new GameEvents.HousePayment(10, 10, 1u, "Pyreal", "Pyreals")],
        };
        house.ApplyHouseData(data, Self);
        Assert.Equal(HousePanelTextColor.RentPaid, house.PanelLines[^2].Color);

        house.ApplyRentTime(1_710_000_000u, Self);

        Assert.Equal("Rent:\n0/10 Pyreals", house.Lines[1]);
        Assert.Equal(HousePanelTextColor.RentNotPaid, house.PanelLines[^2].Color);
        Assert.Equal(
            "This maintenance period ends: " + RetailTime(1_712_592_000L),
            house.Lines[3]);

        house.ApplyRentPayment(
            [new GameEvents.HousePayment(10, 10, 1u, "Pyreal", "Pyreals")], Self);

        Assert.Equal("Rent:\n10/10 Pyreals", house.Lines[1]);
        Assert.Equal(HousePanelTextColor.RentPaid, house.PanelLines[^2].Color);
    }

    [Fact]
    public void HouseData_DefensivelyCopiesPaymentLists()
    {
        var buy = new List<GameEvents.HousePayment>
        {
            new(1, 0, 1u, "Token", "Tokens"),
        };
        var house = new RuntimeHouseState();
        house.ApplyHouseData(SampleHouseData() with { Buy = buy }, Self);

        buy[0] = new GameEvents.HousePayment(99, 0, 1u, "Changed", "Changed");

        Assert.Equal("The purchase price for this dwelling is:\n1 Token", house.Lines[0]);
    }

    [Fact]
    public void HouseStatus_TimestampWithinThirtyDayWindow_ShowsExpiryDateLine()
    {
        var clock = new ManualTimeProvider();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Self, Type = ItemType.Creature });
        var bundle = new PropertyBundle();
        bundle.Ints[(uint)PropertyInt.HousePurchaseTimestamp] =
            (int)clock.GetUtcNow().ToUnixTimeSeconds();
        objects.UpsertProperties(Self, bundle);
        var house = new RuntimeHouseState(objects, clock);

        clock.Advance(TimeSpan.FromDays(29));
        house.ApplyHouseStatus(weenieError: 0u, Self);

        // Houseless -> DisplayBuyPayment's line precedes the purchase-time
        // line; the not-expired branch supplies the second.
        Assert.Equal(2, house.Lines.Count);
        Assert.Equal("You do not currently own a house.", house.Lines[0]);
        string line = house.Lines[1];
        Assert.StartsWith("You may buy another landscape house at ", line);
        Assert.EndsWith(". This restriction does not apply to apartments.", line);
    }

    [Fact]
    public void HouseStatus_TimestampWithinThirtyDayWindow_ExpiryDateIsTimestampPlusThirtyDays()
    {
        var clock = new ManualTimeProvider();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Self, Type = ItemType.Creature });
        DateTimeOffset purchaseTime = clock.GetUtcNow();
        var bundle = new PropertyBundle();
        bundle.Ints[(uint)PropertyInt.HousePurchaseTimestamp] =
            (int)purchaseTime.ToUnixTimeSeconds();
        objects.UpsertProperties(Self, bundle);
        var house = new RuntimeHouseState(objects, clock);

        clock.Advance(TimeSpan.FromDays(29));
        house.ApplyHouseStatus(weenieError: 0u, Self);

        DateTime expectedExpiry = purchaseTime.AddSeconds(0x278d00).UtcDateTime;
        Assert.Equal(2, house.Lines.Count);
        Assert.Contains(
            expectedExpiry.ToString(System.Globalization.CultureInfo.CurrentCulture),
            house.Lines[1]);
    }

    [Fact]
    public void HouseStatus_TimestampPastThirtyDayWindow_ShowsBothHouselessLines()
    {
        var clock = new ManualTimeProvider();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Self, Type = ItemType.Creature });
        var bundle = new PropertyBundle();
        bundle.Ints[(uint)PropertyInt.HousePurchaseTimestamp] =
            (int)clock.GetUtcNow().ToUnixTimeSeconds();
        objects.UpsertProperties(Self, bundle);
        var house = new RuntimeHouseState(objects, clock);

        clock.Advance(TimeSpan.FromDays(31));
        house.ApplyHouseStatus(weenieError: 0u, Self);

        Assert.Equal(
            [
                "You do not currently own a house.",
                "You may buy another house immediately.",
            ],
            house.Lines);
    }

    [Fact]
    public void ResetSession_RestoresGenuinelyEmptyPreNoticeState()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Self, Type = ItemType.Creature });
        var house = new RuntimeHouseState(objects);
        house.ApplyHouseStatus(weenieError: 0u, Self);
        Assert.NotEmpty(house.Lines);

        house.ResetSession();

        Assert.Empty(house.Lines);
        Assert.Empty(house.PanelLines);
        Assert.Null(house.Position);
        Assert.False(house.HasReceivedNotice);
    }

    [Fact]
    public void MissingObjectTable_DefaultsTimestampToZero()
    {
        var house = new RuntimeHouseState();

        house.ApplyHouseStatus(weenieError: 0u, Self);

        Assert.Equal(
            [
                "You do not currently own a house.",
                "You may buy another house immediately.",
            ],
            house.Lines);
    }

    private static GameEvents.HouseData SampleHouseData() => new(
        BuyTime: 0u,
        RentTime: 0u,
        Type: 0u,
        MaintenanceFree: false,
        Buy: Array.Empty<GameEvents.HousePayment>(),
        Rent: Array.Empty<GameEvents.HousePayment>(),
        Position: new CreateObject.ServerPosition(0u, 0f, 0f, 0f, 1f, 0f, 0f, 0f));

    private static CreateObject.ServerPosition Position(uint cellId) =>
        new(cellId, 0f, 0f, 0f, 1f, 0f, 0f, 0f);

    private static string RetailTime(long epochSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime
            .ToString(System.Globalization.CultureInfo.CurrentCulture);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
