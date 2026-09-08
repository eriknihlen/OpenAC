using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Transport;

internal sealed class LossyTransportDecorator : IWorldSessionTransport
{
    private readonly IWorldSessionTransport _inner;
    private readonly int _dropPercent;
    private readonly Random _outboundRandom;
    private readonly Random _inboundRandom;
    private readonly bool _dropOutbound;
    private readonly bool _dropInbound;
    private volatile bool _armed;

    private int _outboundDropped;
    private int _inboundDropped;
    private int _outboundForwarded;
    private int _inboundForwarded;

    /// <summary>Outbound datagrams eaten so far (diagnostic evidence for the
    /// loss gate's "the decorator actually dropped" assertion).</summary>
    public int OutboundDropped => Volatile.Read(ref _outboundDropped);

    /// <summary>Inbound datagrams eaten so far.</summary>
    public int InboundDropped => Volatile.Read(ref _inboundDropped);

    /// <summary>True once the first encrypted outbound datagram has been
    /// forwarded (the arming gate above).</summary>
    public bool IsArmed => _armed;

    public LossyTransportDecorator(
        IWorldSessionTransport inner,
        int dropPercent,
        int seed,
        NetDropDirection direction)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(dropPercent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dropPercent, 100);

        _inner = inner;
        _dropPercent = dropPercent;
        _outboundRandom = new Random(seed);
        _inboundRandom = new Random(~seed);
        _dropOutbound = direction is NetDropDirection.Out or NetDropDirection.Both;
        _dropInbound = direction is NetDropDirection.In or NetDropDirection.Both;
        Console.WriteLine(
            $"[net-loss] active pct={dropPercent} seed={seed} dir={direction}");
    }

    public static IWorldSessionTransport WrapIfConfigured(
        IWorldSessionTransport inner) =>
        NetDiagnostics.NetDropPercent > 0
            ? new LossyTransportDecorator(
                inner,
                NetDiagnostics.NetDropPercent,
                NetDiagnostics.NetDropSeed,
                NetDiagnostics.NetDropDir)
            : inner;

    // ---- outbound ----

    public void Send(ReadOnlySpan<byte> datagram)
    {
        if (DropOutbound())
            return;
        _inner.Send(datagram);
        AfterOutboundForwarded(datagram);
    }

    public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram)
    {
        if (DropOutbound())
            return;
        _inner.Send(remote, datagram);
        AfterOutboundForwarded(datagram);
    }

    private bool DropOutbound()
    {
        if (!_armed || !_dropOutbound)
            return false;
        if (_outboundRandom.Next(100) >= _dropPercent)
            return false;
        Interlocked.Increment(ref _outboundDropped);
        return true;
    }

    private void AfterOutboundForwarded(ReadOnlySpan<byte> datagram)
    {
        Interlocked.Increment(ref _outboundForwarded);
        if (_armed)
            return;
        if (datagram.Length > PacketHeader.Size
            && (BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(4))
                & (uint)PacketHeaderFlags.EncryptedChecksum) != 0)
        {
            _armed = true;
            Console.WriteLine("[net-loss] armed");
        }
    }

    // ---- inbound ----

    public int Receive(
        Span<byte> destination,
        TimeSpan timeout,
        out IPEndPoint? from)
    {
        long deadline = Stopwatch.GetTimestamp()
            + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        TimeSpan next = timeout;
        while (true)
        {
            int length = _inner.Receive(destination, next, out from);
            if (length < 0)
                return length;
            if (!DropInbound())
            {
                Interlocked.Increment(ref _inboundForwarded);
                return length;
            }

            double remainingMs =
                (deadline - Stopwatch.GetTimestamp())
                * 1000.0 / Stopwatch.Frequency;
            if (remainingMs < 1.0)
            {
                // Expired while eating datagrams. NetClient treats a 0 ms
                // socket timeout as INFINITE, so never pass ≤ 0 back down.
                from = null;
                return -1;
            }

            next = TimeSpan.FromMilliseconds(remainingMs);
        }
    }

    public async ValueTask<NetReceiveResult> ReceiveAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            NetReceiveResult result = await _inner
                .ReceiveAsync(destination, cancellationToken)
                .ConfigureAwait(false);
            if (DropInbound())
                continue;
            Interlocked.Increment(ref _inboundForwarded);
            return result;
        }
    }

    private bool DropInbound()
    {
        if (!_armed || !_dropInbound)
            return false;
        if (_inboundRandom.Next(100) >= _dropPercent)
            return false;
        Interlocked.Increment(ref _inboundDropped);
        return true;
    }

    public void Dispose()
    {
        Console.WriteLine(
            $"[net-loss] dropped out={OutboundDropped} in={InboundDropped}"
            + $" forwarded out={Volatile.Read(ref _outboundForwarded)}"
            + $" in={Volatile.Read(ref _inboundForwarded)}"
            + $" armed={_armed}");
        _inner.Dispose();
    }
}
