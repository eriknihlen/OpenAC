using System;
using System.Collections.Generic;
using System.Globalization;
using AcDream.Core.Net;

namespace AcDream.App.UI.Layout;

public sealed class LinkStatusUiController : IRetainedPanelController
{
    public const uint LayoutId = 0x2100001Du;
    public const uint RootId = 0x10000167u;
    public const uint MainTextId = 0x10000169u;
    public const uint CloseId = 0x100000FCu;

    private const double UpdateIntervalSeconds = 5d;
    private const double PingIntervalSeconds = 120d;

    private readonly UiText _mainText;
    private readonly UiButton? _close;
    private readonly Func<LinkStatusSnapshot> _snapshot;
    private readonly Func<double> _currentTime;
    private readonly Action _requestPing;
    private readonly LinkStatusStrings _strings;
    private IReadOnlyList<UiText.Line> _lines = Array.Empty<UiText.Line>();
    private double _nextUpdateTime = -1d;
    private double _lastPingRequestTime = -1d;
    private bool _pleaseRequestPing;
    private bool _visible;

    private LinkStatusUiController(
        ImportedLayout layout,
        Func<LinkStatusSnapshot> snapshot,
        Func<double> currentTime,
        Action requestPing,
        LinkStatusStrings strings,
        Action? close)
    {
        _mainText = (UiText)layout.FindElement(MainTextId)!;
        _close = layout.FindElement(CloseId) as UiButton;
        _snapshot = snapshot;
        _currentTime = currentTime;
        _requestPing = requestPing;
        _strings = strings;
        _mainText.LinesProvider = () => _lines;
        if (_close is not null) _close.OnClick = close;
    }

    public static LinkStatusUiController? Bind(
        ImportedLayout layout,
        Func<LinkStatusSnapshot> snapshot,
        Func<double> currentTime,
        Action requestPing,
        LinkStatusStrings strings,
        Action? close = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(currentTime);
        ArgumentNullException.ThrowIfNull(requestPing);
        ArgumentNullException.ThrowIfNull(strings);
        return layout.FindElement(MainTextId) is UiText
            ? new LinkStatusUiController(
                layout, snapshot, currentTime, requestPing, strings, close)
            : null;
    }

    public void Tick()
    {
        if (!_visible) return;
        double now = _currentTime();
        if (!double.IsFinite(now) || now < _nextUpdateTime) return;
        _nextUpdateTime = now + UpdateIntervalSeconds;
        Update(now);
    }

    public void OnShown()
    {
        _visible = true;
        _pleaseRequestPing = true;
        Update(_currentTime());
    }

    public void OnHidden()
    {
        _visible = false;
        _pleaseRequestPing = false;
    }

    private void Update(double now)
    {
        LinkStatusSnapshot value = _snapshot();
        string ping = value.RoundTripSeconds is double seconds
                      && double.IsFinite(seconds)
                      && seconds >= 0d
            ? (seconds * 1000d).ToString("F0", CultureInfo.InvariantCulture)
            : "????";
        string body = _strings.Description
                      + _strings.Legend
                      + _strings.DisconnectWarning
                      + _strings.PacketLossPrefix
                      + value.PacketLossPercentage.ToString("F2", CultureInfo.InvariantCulture)
                      + _strings.PingPrefix
                      + ping;
        _lines = IndicatorDetailText.Shape(_mainText, body);

        if (!double.IsFinite(now)) return;
        bool periodicPing = _lastPingRequestTime >= 0d
            && now - _lastPingRequestTime >= PingIntervalSeconds;
        if (!_pleaseRequestPing && !periodicPing) return;
        _lastPingRequestTime = now;
        _pleaseRequestPing = false;
        _requestPing();
    }

    public void Dispose()
    {
        if (_close is not null) _close.OnClick = null;
    }
}

public sealed record LinkStatusStrings(
    string Description,
    string Legend,
    string DisconnectWarning,
    string PacketLossPrefix,
    string PingPrefix)
{
    public static LinkStatusStrings English { get; } = new(
        "The Link Indicator shows the current status of your connection to the game servers.",
        "\n\nGREEN = your link is good.\nYELLOW = no packets for at least 5 sec.\nRED = no packets for at least 20 sec.",
        "\n\nIf approximately forty seconds pass without receiving a packet, you will be disconnected from the server.",
        "\n\n\nPacket loss for the last 10 sec: ",
        "\n\nRoundtrip Ping time to Server: ");
}
