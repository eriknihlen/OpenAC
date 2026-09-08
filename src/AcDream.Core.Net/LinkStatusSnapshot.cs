namespace AcDream.Core.Net;

public readonly record struct LinkStatusSnapshot(
    bool Connected,
    double SecondsSinceLastPacket,
    double PacketLossPercentage = 0d,
    double? RoundTripSeconds = null)
{
    public static LinkStatusSnapshot Disconnected => new(false, 0d);
}
