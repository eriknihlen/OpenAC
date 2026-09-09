using System.Buffers.Binary;
using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;
using AcDream.Core.Net.Transport;

namespace AcDream.Core.Net;

public sealed partial class WorldSession
{
    private sealed class ConnectionAttempt(DateTime deadline)
    {
        public DateTime Deadline { get; } = deadline;
        public byte[]? Response { get; set; }
        public DateTime ResponseNotBefore { get; set; }
        public bool ResponseSent { get; set; }
        public long ResponseSentTimestamp { get; set; }
    }

    private ConnectionAttempt? _connectionAttempt;

    public void Connect(string account, string password, TimeSpan? timeout = null)
    {
        BeginConnect(account, password, timeout);
        while (!PollConnect())
            Thread.Sleep(1);
    }

    public void BeginConnect(string account, string password, TimeSpan? timeout = null)
    {
        if (_connectionAttempt is not null)
            throw new InvalidOperationException("A connection attempt is already active.");
        _dataCheckComplete = false;
        _dataInterrogationReceived = false;
        _handshakeConfirmed = false;
        Characters = null;
        _connectionAttempt = new(DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10)));
        SetConnectionProgress(new(ConnectionPhase.Connecting));
        Transition(State.Handshaking);
        uint timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        byte[] payload = LoginRequest.Build(account, password, timestamp);
        _net.Send(PacketCodec.Encode(new PacketHeader { Flags = PacketHeaderFlags.LoginRequest }, payload, null));
    }

    /// <summary>Advances connection work without waiting for network input.</summary>
    public bool PollConnect()
    {
        ConnectionAttempt attempt = _connectionAttempt
            ?? throw new InvalidOperationException("No connection attempt is active.");
        try
        {
            for (int received = 0; received < 64; received++)
            {
                if (attempt.Response is not null && !attempt.ResponseSent)
                {
                    if (DateTime.UtcNow < attempt.ResponseNotBefore)
                        break;
                    _net.Send(_connectEndpoint, attempt.Response);
                    attempt.ResponseSent = true;
                    attempt.ResponseSentTimestamp = _transport!.Clock.GetTimestamp();
                }

                PooledInboundDatagram? incoming = ReceiveBlocking(TimeSpan.Zero);
                if (incoming is null)
                    break;
                PooledInboundDatagram datagram = incoming.Value;
                try
                {
                    if (attempt.Response is null)
                        AcceptConnectRequest(datagram.Memory, attempt);
                    else
                        ProcessDatagram(datagram.Memory);
                }
                finally
                {
                    ReturnInboundDatagram(datagram);
                }
                ThrowIfDataCheckFailed();
            }

            if (attempt.ResponseSent)
            {
                TransportClock clock = _transport!.Clock;
                long retryTicks = (long)Math.Round(ConnectResponseRetrySeconds * clock.Frequency);
                if (!_handshakeConfirmed && clock.GetTimestamp() - attempt.ResponseSentTimestamp > retryTicks)
                {
                    _net.Send(_connectEndpoint, attempt.Response!);
                    attempt.ResponseSentTimestamp = clock.GetTimestamp();
                }
                SweepTransport();
            }

            if (Characters is not null && _dataCheckComplete)
            {
                _connectionAttempt = null;
                Transition(State.InCharacterSelect);
                SetConnectionProgress(new(ConnectionPhase.Ready));
                return true;
            }
            if (DateTime.UtcNow >= attempt.Deadline)
            {
                throw new TimeoutException(attempt.Response is null ? "ConnectRequest not received"
                    : Characters is null ? "CharacterList not received"
                    : "The server did not complete the game-data check.");
            }
            return false;
        }
        catch (Exception error)
        {
            _connectionAttempt = null;
            SetConnectionProgress(new(ConnectionPhase.Failed, error.Message));
            Transition(State.Failed);
            throw;
        }
    }

    private void AcceptConnectRequest(ReadOnlyMemory<byte> bytes, ConnectionAttempt attempt)
    {
        if (!PacketCodec.TryParseBorrowed(bytes, out BorrowedPacket parsed,
                out uint headerHash, out uint payloadHash, out _))
            return;
        PacketHeader header = parsed.Header;
        if (header.HasFlag(PacketHeaderFlags.EncryptedChecksum)
            || !PacketCodec.VerifyChecksum(in header, headerHash, payloadHash, isaacKey: null)
            || !header.HasFlag(PacketHeaderFlags.ConnectRequest))
            return;

        BorrowedOptionalHeader opt = parsed.Optional;
        byte[] serverSeed = new byte[4];
        byte[] clientSeed = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(serverSeed, opt.ConnectRequestServerSeed);
        BinaryPrimitives.WriteUInt32LittleEndian(clientSeed, opt.ConnectRequestClientSeed);
        _sessionClientId = (ushort)opt.ConnectRequestClientId;
        _sessionIteration = parsed.Header.Iteration;
        _transport = new ReliableTransport(new IsaacRandom(clientSeed), new IsaacRandom(serverSeed),
            _sessionClientId, _sessionIteration, datagram => _net.Send(datagram),
            clock: TransportClockSource is { } source
                ? new TransportClock(source.GetTimestamp, source.Frequency) : null,
            assembler: _assembler);
        _transportNegotiated = true;
        LastServerTimeTicks = opt.ConnectRequestServerTime;
        ServerTimeUpdated?.Invoke(opt.ConnectRequestServerTime);
        byte[] response = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(response, opt.ConnectRequestCookie);
        attempt.Response = PacketCodec.Encode(new PacketHeader
        {
            Sequence = 1, Flags = PacketHeaderFlags.ConnectResponse, Id = 0,
        }, response, null);
        attempt.ResponseNotBefore = DateTime.UtcNow.AddMilliseconds(200);
    }
}
