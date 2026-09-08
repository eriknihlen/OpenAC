using System.Globalization;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Ui;
using AcDream.Core.Properties;

namespace AcDream.Runtime.Gameplay;

public sealed class RuntimeHouseState
{
    private const long PurchaseWaitPeriodSeconds = 0x278d00;

    private readonly ClientObjectTable? _objects;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private bool _hasReceivedNotice;
    private bool _ownsHouse;
    private GameEvents.HouseData? _houseData;
    private IReadOnlyList<string> _lines = Array.Empty<string>();
    private IReadOnlyList<HousePanelLine> _panelLines = Array.Empty<HousePanelLine>();

    public RuntimeHouseState(
        ClientObjectTable? objects = null, TimeProvider? timeProvider = null)
    {
        _objects = objects;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<string> Lines
    {
        get { lock (_gate) return _lines; }
    }

    public IReadOnlyList<HousePanelLine> PanelLines
    {
        get { lock (_gate) return _panelLines; }
    }

    public CreateObject.ServerPosition? Position
    {
        get
        {
            lock (_gate)
            {
                return _houseData is { Type: not 4u, Position: { LandblockId: not 0u } } data
                    ? data.Position
                    : null;
            }
        }
    }

    /// <summary>Whether any of the four House notices (0x0225-0x0228) has
    /// arrived this session.</summary>
    public bool HasReceivedNotice
    {
        get { lock (_gate) return _hasReceivedNotice; }
    }

    public void ApplyHouseData(GameEvents.HouseData data, uint selfGuid)
    {
        lock (_gate)
        {
            _hasReceivedNotice = true;
            _ownsHouse = true;
            _houseData = Copy(data);
            Recompute(selfGuid);
        }
    }

    public void ApplyRentTime(uint rentTime, uint selfGuid)
    {
        lock (_gate)
        {
            if (_houseData is not { } data)
                return;

            GameEvents.HousePayment[] rent = data.Rent
                .Select(static payment => payment with { Paid = 0 })
                .ToArray();
            _houseData = data with { RentTime = rentTime, Rent = rent };
            Recompute(selfGuid);
        }
    }

    public void ApplyRentPayment(
        IReadOnlyList<GameEvents.HousePayment> rent, uint selfGuid)
    {
        ArgumentNullException.ThrowIfNull(rent);
        lock (_gate)
        {
            if (_houseData is not { } data)
                return;

            _houseData = data with { Rent = rent.ToArray() };
            Recompute(selfGuid);
        }
    }

    public void ApplyHouseStatus(uint weenieError, uint selfGuid)
    {
        _ = weenieError;
        lock (_gate)
        {
            _hasReceivedNotice = true;
            _ownsHouse = false;
            _houseData = null;
            Recompute(selfGuid);
        }
    }

    public void ResetSession()
    {
        lock (_gate)
        {
            _hasReceivedNotice = false;
            _ownsHouse = false;
            _houseData = null;
            _lines = Array.Empty<string>();
            _panelLines = Array.Empty<HousePanelLine>();
        }
    }

    private void Recompute(uint selfGuid)
    {
        var lines = new List<HousePanelLine>(_ownsHouse ? 8 : 2);

        if (_houseData is { } data)
        {
            lines.Add(Normal(
                "The purchase price for this dwelling is:\n"
                + ComposePayments(data.Buy, includePaid: false)));
            lines.Add(Normal(
                "Rent:\n" + ComposePayments(data.Rent, includePaid: true)));
            lines.Add(Normal("Bought: " + ConvertTime(data.BuyTime)));

            long period = GetRentPeriodSeconds(data.Type);
            bool paid = data.MaintenanceFree || IsPaidInFull(data.Rent);
            lines.Add(Normal(
                "This maintenance period ends: "
                + ConvertTime((long)data.RentTime + period)));
            lines.Add(Normal(
                "Maintenance is next due: "
                + ConvertTime((long)data.RentTime + (paid ? 2L : 1L) * period)));

            if (data.Type != 4u
                && RadarCoordinates.TryFromCell(
                    data.Position.LandblockId, out RadarCoordinates coordinates))
            {
                lines.Add(Normal(
                    $"Location: {coordinates.YText}, {coordinates.XText}"));
            }

            lines.Add(paid
                ? new HousePanelLine(
                    "The maintenance has already been paid for this period. "
                    + "You may not prepay next period's maintenance.",
                    HousePanelTextColor.RentPaid)
                : new HousePanelLine(
                    "Warning!  You have not paid your maintenance costs for the last "
                    + (period / 86_400L).ToString(CultureInfo.InvariantCulture)
                    + " day maintenance period.  Please pay these costs by this deadline"
                    + " or you will lose your house, and all your items within it.",
                    HousePanelTextColor.RentNotPaid));
        }
        else
        {
            lines.Add(Normal("You do not currently own a house."));
        }

        int timestamp = _objects?.Get(selfGuid)?.Properties
            .GetInt((uint)PropertyInt.HousePurchaseTimestamp) ?? 0;
        long nowEpochSeconds = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        bool expired = (nowEpochSeconds - timestamp) > PurchaseWaitPeriodSeconds;

        if (!expired)
        {
            DateTimeOffset expiryUtc = DateTimeOffset.FromUnixTimeSeconds(
                timestamp + PurchaseWaitPeriodSeconds);
            DateTime expiryLocal = TimeZoneInfo.ConvertTime(
                expiryUtc, _timeProvider.LocalTimeZone).DateTime;
            lines.Add(Normal(
                "You may buy another landscape house at "
                + expiryLocal.ToString(CultureInfo.CurrentCulture)
                + ". This restriction does not apply to apartments."));
        }
        else
        {
            lines.Add(Normal(_ownsHouse
                ? "You may buy another house immediately after you abandon this one."
                : "You may buy another house immediately."));
        }

        _panelLines = lines;
        _lines = lines.Select(static line => line.Text).ToArray();
    }

    private HousePanelLine Normal(string text) =>
        new(text, HousePanelTextColor.Normal);

    private static GameEvents.HouseData Copy(GameEvents.HouseData data) =>
        data with
        {
            Buy = (data.Buy ?? Array.Empty<GameEvents.HousePayment>()).ToArray(),
            Rent = (data.Rent ?? Array.Empty<GameEvents.HousePayment>()).ToArray(),
        };

    private static bool IsPaidInFull(IReadOnlyList<GameEvents.HousePayment> payments)
    {
        for (int i = 0; i < payments.Count; i++)
        {
            if (payments[i].Paid < payments[i].Num)
                return false;
        }
        return true;
    }

    private static string ComposePayments(
        IReadOnlyList<GameEvents.HousePayment> payments, bool includePaid)
    {
        if (payments.Count == 0)
            return string.Empty;

        var parts = new string[payments.Count];
        for (int i = 0; i < payments.Count; i++)
        {
            GameEvents.HousePayment payment = payments[i];
            string quantity = includePaid
                ? $"{payment.Paid.ToString(CultureInfo.InvariantCulture)}/{payment.Num.ToString(CultureInfo.InvariantCulture)}"
                : payment.Num.ToString(CultureInfo.InvariantCulture);
            parts[i] = quantity + " " + PaymentName(payment);
        }
        return string.Join(", ", parts);
    }

    private static string PaymentName(GameEvents.HousePayment payment)
    {
        if (payment.Num == 1)
            return payment.Name;
        if (!string.IsNullOrEmpty(payment.PluralName))
            return payment.PluralName;

        return payment.Name.EndsWith('s') || payment.Name.EndsWith('x')
            ? payment.Name + "es"
            : payment.Name + "s";
    }

    private static long GetRentPeriodSeconds(uint houseType) =>
        houseType == 4u ? 7_776_000L : 2_592_000L;

    private string ConvertTime(long epochSeconds)
    {
        if (epochSeconds == 0L)
            return "N/A";

        DateTimeOffset utc = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
        DateTime local = TimeZoneInfo.ConvertTime(
            utc, _timeProvider.LocalTimeZone).DateTime;
        return local.ToString(CultureInfo.CurrentCulture);
    }
}

public enum HousePanelTextColor
{
    Normal = 0,
    RentPaid = 1,
    RentNotPaid = 2,
}

public readonly record struct HousePanelLine(
    string Text, HousePanelTextColor Color);
