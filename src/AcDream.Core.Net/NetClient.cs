using System.Net;
using System.Net.Sockets;

namespace AcDream.Core.Net;

public sealed class NetClient : IDisposable
{
    private readonly UdpClient _udp;
    private readonly IPEndPoint _remote;
    private readonly IPEndPoint _anyRemote =
        new(IPAddress.Any, 0);

    private int _lastAppliedReceiveTimeoutMs = int.MinValue;

    public NetClient(IPEndPoint remote)
    {
        _remote = remote;
        // Bind to an OS-assigned local port; server will reply to it.
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));

        if (OperatingSystem.IsWindows())
        {
            const int SioUdpConnReset = -1744830452;
            _udp.Client.IOControl((IOControlCode)SioUdpConnReset, new byte[] { 0 }, null);
        }
    }

    /// <summary>The local endpoint the OS assigned us.</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    /// <summary>The remote endpoint we're talking to.</summary>
    public IPEndPoint RemoteEndPoint => _remote;

    public void Send(ReadOnlySpan<byte> datagram)
    {
        _udp.Client.SendTo(
            datagram,
            SocketFlags.None,
            _remote);
    }

    public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram)
    {
        _udp.Client.SendTo(
            datagram,
            SocketFlags.None,
            remote);
    }

    public int Receive(
        Span<byte> destination,
        TimeSpan timeout,
        out IPEndPoint? from)
    {
        if (timeout == TimeSpan.Zero && !_udp.Client.Poll(0, SelectMode.SelectRead))
        {
            from = null;
            return -1;
        }
        int timeoutMs = checked((int)timeout.TotalMilliseconds);
        if (timeoutMs != _lastAppliedReceiveTimeoutMs)
        {
            _udp.Client.ReceiveTimeout = timeoutMs;
            _lastAppliedReceiveTimeoutMs = timeoutMs;
        }

        try
        {
            EndPoint remote = _anyRemote;
            int length = _udp.Client.ReceiveFrom(
                destination,
                SocketFlags.None,
                ref remote);
            from = (IPEndPoint)remote;
            return length;
        }
        catch (SocketException ex)
            when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            from = null;
            return -1;
        }
    }

    public async ValueTask<NetReceiveResult> ReceiveAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        SocketReceiveFromResult result =
            await _udp.Client.ReceiveFromAsync(
                destination,
                SocketFlags.None,
                _anyRemote,
                cancellationToken).ConfigureAwait(false);
        return new NetReceiveResult(
            result.ReceivedBytes,
            (IPEndPoint)result.RemoteEndPoint);
    }

    public byte[]? Receive(TimeSpan timeout, out IPEndPoint? from)
    {
        int timeoutMs = checked((int)timeout.TotalMilliseconds);
        if (timeoutMs != _lastAppliedReceiveTimeoutMs)
        {
            _udp.Client.ReceiveTimeout = timeoutMs;
            _lastAppliedReceiveTimeoutMs = timeoutMs;
        }
        try
        {
            IPEndPoint any = new(IPAddress.Any, 0);
            byte[] bytes = _udp.Receive(ref any);
            from = any;
            return bytes;
        }
        catch (SocketException ex)
            when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            from = null;
            return null;
        }
    }

    public byte[]? TryReceive(out IPEndPoint? from)
    {
        if (_udp.Available == 0)
        {
            from = null;
            return null;
        }

        try
        {
            IPEndPoint any = new(IPAddress.Any, 0);
            var bytes = _udp.Receive(ref any);
            from = any;
            return bytes;
        }
        catch (SocketException)
        {
            from = null;
            return null;
        }
    }

    public void Dispose() => _udp.Dispose();
}

public readonly record struct NetReceiveResult(
    int Length,
    IPEndPoint RemoteEndPoint);
