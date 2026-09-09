using System.Buffers.Binary;
using System.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Transport;

internal sealed class FakeAceTransport : IWorldSessionTransport
{
    public const uint DefaultClientSeed = 0x2B6D6F87u;
    public const uint DefaultServerSeed = 0x9A3C51E4u;
    public const uint DefaultClientId = 0x1234u;
    public const ulong DefaultCookie = 0xFEEDFACECAFEBABEUL;
    public const uint DefaultCharacterId = 0x50000001u;
    public const string DefaultCharacterName = "+Acdream";
    public const string DefaultAccountName = "testaccount";

    private readonly object _gate = new();
    private readonly SemaphoreSlim _deliverable = new(0);
    private readonly Queue<byte[]> _toClient = new();
    private readonly IPEndPoint _serverEndpoint = new(IPAddress.Loopback, 9000);

    public VirtualClock Clock { get; }
    public LossyLink Link { get; }
    public AceSessionModel Model { get; }

    public TimeSpan AutoAdvanceOnBlockingReceive { get; set; }

    public bool AutoReplyServerReady { get; set; } = true;
    public bool AutoCompleteDataCheck { get; set; } = true;

    public FakeAceTransport(VirtualClock? clock = null, LossyLink? link = null)
    {
        Clock = clock ?? new VirtualClock();
        Link = link ?? new LossyLink();
        Model = new AceSessionModel(
            Clock,
            DefaultClientSeed,
            DefaultServerSeed,
            DefaultClientId,
            DefaultCookie);

        Model.LoginRequestReceived += () => Model.SendConnectRequest();
        Model.ConnectResponseAccepted += () =>
        {
            Model.EnqueueGameMessage(BuildCharacterListBody(), GameMessageGroup.UIQueue);
            Model.EnqueueGameMessage(BuildOpcodeOnlyBody(0xF7E5), GameMessageGroup.DatabaseQueue);
        };
        Model.MessageDispatched += OnClientMessage;
    }

    private void OnClientMessage(byte[] body)
    {
        if (body.Length < 4)
            return;
        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body);
        switch (opcode)
        {
            case DddInterrogationResponse.Opcode:
                if (AutoCompleteDataCheck)
                    Model.EnqueueGameMessage(BuildOpcodeOnlyBody(0xF7EA), GameMessageGroup.DatabaseQueue);
                break;
            case CharacterEnterWorld.EnterWorldRequestOpcode: // 0xF7C8
                if (AutoReplyServerReady)
                {
                    Model.EnqueueGameMessage(
                        BuildOpcodeOnlyBody(0xF7DFu),
                        GameMessageGroup.UIQueue);
                }
                break;
            case CharacterLogOff.Opcode:
                Model.EnqueueGameMessage(BuildOpcodeOnlyBody(CharacterLogOff.Opcode), GameMessageGroup.UIQueue);
                break;
        }
    }

    // ---- IWorldSessionTransport ----

    public void Send(ReadOnlySpan<byte> datagram) => SendCore(datagram);

    public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram) => SendCore(datagram);

    private void SendCore(ReadOnlySpan<byte> datagram)
    {
        lock (_gate)
        {
            foreach (byte[] delivered in Link.Transmit(LinkDirection.ClientToServer, datagram))
                Model.Receive(delivered);
            PumpServerLocked();
        }
    }

    public void PumpServer()
    {
        lock (_gate)
        {
            PumpServerLocked();
        }
    }

    /// <summary>
    /// Enqueue and flush one model game message under the transport's model
    /// lock. This is the race-free test seam for a server follower emitted
    /// while the real WorldSession background receiver is active.
    /// </summary>
    public void EnqueueServerGameMessage(
        byte[] body,
        GameMessageGroup group)
    {
        lock (_gate)
        {
            Model.EnqueueGameMessage(body, group);
            PumpServerLocked();
        }
    }

    public void InjectServerDatagram(byte[] datagram)
    {
        lock (_gate)
        {
            _toClient.Enqueue((byte[])datagram.Clone());
            _deliverable.Release();
        }
    }

    private void PumpServerLocked()
    {
        Model.Update();
        foreach (byte[] outbound in Model.TakePendingDatagrams())
        {
            foreach (byte[] delivered in Link.Transmit(LinkDirection.ServerToClient, outbound))
            {
                _toClient.Enqueue(delivered);
                _deliverable.Release();
            }
        }
    }

    public int Receive(Span<byte> destination, TimeSpan timeout, out IPEndPoint? from)
    {
        if (AutoAdvanceOnBlockingReceive > TimeSpan.Zero)
            Clock.Advance(AutoAdvanceOnBlockingReceive);
        lock (_gate)
        {
            PumpServerLocked();
        }

        if (timeout < TimeSpan.Zero)
            timeout = TimeSpan.Zero;
        if (!_deliverable.Wait(timeout))
        {
            from = null;
            return -1;
        }

        from = _serverEndpoint;
        lock (_gate)
        {
            byte[] datagram = _toClient.Dequeue();
            datagram.CopyTo(destination);
            return datagram.Length;
        }
    }

    public async ValueTask<NetReceiveResult> ReceiveAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        await _deliverable.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            byte[] datagram = _toClient.Dequeue();
            datagram.CopyTo(destination);
            return new NetReceiveResult(datagram.Length, _serverEndpoint);
        }
    }

    public void Dispose()
    {
    }


    private static byte[] BuildCharacterListBody()
    {
        var writer = new PacketWriter(96);
        writer.WriteUInt32(CharacterList.Opcode);
        writer.WriteUInt32(0);                    // status
        writer.WriteUInt32(1);
        writer.WriteUInt32(DefaultCharacterId);
        writer.WriteString16L(DefaultCharacterName);
        writer.WriteUInt32(0);                    // secondsGreyedOut
        writer.WriteUInt32(0);
        writer.WriteUInt32(11);
        writer.WriteString16L(DefaultAccountName);
        writer.WriteUInt32(1);                    // useTurbineChat
        writer.WriteUInt32(1);                    // hasThroneOfDestiny
        return writer.ToArray();
    }

    private static byte[] BuildOpcodeOnlyBody(uint opcode)
    {
        byte[] body = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, opcode);
        return body;
    }
}
