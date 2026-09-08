using System.Buffers;
using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Transport;

internal sealed class ReliableTransport : IDisposable
{
    public TransportClock Clock { get; }

    public OutboundFlowQueue Outbound { get; }

    /// <summary>N2: the inbound sequence tracker — inbound ISAAC,
    /// <c>highestIDReceived_</c>, and the NAK set. Born beside the outbound
    /// queue at ISAAC-seeding time so both keystreams share one owner.</summary>
    public InboundSequenceTracker Inbound { get; }

    public AckNakScheduler Scheduler { get; }

    public TransportStats Stats { get; }

    public double PacketLossPercentage => _packetLoss.Percentage;

    public const double AssemblerSweepSeconds = 5.0;

    private readonly FragmentAssembler? _assembler;
    private readonly RetailPacketLossAverager _packetLoss;
    private readonly long _assemblerSweepTicks;
    private long _assemblerSweepTimestamp;

    public ReliableTransport(
        IsaacRandom outboundIsaac,
        IsaacRandom inboundIsaac,
        ushort sessionClientId,
        ushort sessionIteration,
        DatagramSendDelegate send,
        TransportClock? clock = null,
        ArrayPool<byte>? pool = null,
        FragmentAssembler? assembler = null)
    {
        Clock = clock ?? new TransportClock();
        Stats = new TransportStats();
        _assembler = assembler;
        // Same defensive rounding as the scheduler gates (N4 review F1).
        _assemblerSweepTicks =
            (long)Math.Round(AssemblerSweepSeconds * Clock.Frequency);
        _assemblerSweepTimestamp = Clock.GetTimestamp();
        void CountedSend(ReadOnlySpan<byte> datagram)
        {
            send(datagram);
            Stats.PacketsSent++;
        }

        Outbound = new OutboundFlowQueue(
            outboundIsaac,
            sessionClientId,
            sessionIteration,
            Clock,
            Stats,
            CountedSend,
            pool);
        Inbound = new InboundSequenceTracker(inboundIsaac, Stats);
        Scheduler = new AckNakScheduler(
            Clock,
            Inbound,
            Outbound,
            sessionClientId,
            sessionIteration,
            Stats,
            CountedSend);
        Stats.CacheDepthSource = () => Outbound.CacheDepth;
        _packetLoss = new RetailPacketLossAverager(Clock, Stats);
    }

    public uint HighestIdSent => Outbound.HighestIdSent;

    public void Sweep()
    {
        Clock.Update();
        long now = Clock.GetTimestamp();
        _packetLoss.Sweep(now, Stats);
        Scheduler.Sweep(now);
        Outbound.TransmitPendingResends();

        if (_assembler is not null
            && now - _assemblerSweepTimestamp >= _assemblerSweepTicks)
        {
            _assemblerSweepTimestamp = now;
            _assembler.SweepExpired();
        }
    }

    public void Dispose() => Outbound.Dispose();
}
