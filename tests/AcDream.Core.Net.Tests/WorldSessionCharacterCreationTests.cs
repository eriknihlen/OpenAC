using System.Net;
using System.Reflection;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;
using AcDream.Core.Net.Tests.Messages;

namespace AcDream.Core.Net.Tests;

public sealed class WorldSessionCharacterCreationTests
{
    private sealed class NullTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram) { }
        public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram) { }
        public int Receive(
            Span<byte> destination,
            TimeSpan timeout,
            out IPEndPoint? from)
        {
            from = null;
            return -1;
        }
        public ValueTask<NetReceiveResult> ReceiveAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromCanceled<NetReceiveResult>(cancellationToken);
        public void Dispose() { }
    }

    private static WorldSession CreateSession() =>
        new(
            new IPEndPoint(IPAddress.Loopback, 9000),
            new NullTransport());

    private static CharacterCreate.Request MakeCreateRequest() => new(
        Heritage: 1u,
        Gender: 0u,
        Appearance: default,
        Template: 0u,
        Attributes: default,
        Slot: 0u,
        ClassId: 1u,
        Name: "NewChar",
        StartArea: 0u,
        IsAdmin: false,
        IsEnvoy: false);

    private static byte[] BuildVerificationResponseBody(uint code, uint guid, string name) =>
        code == (uint)CharGenVerificationResponse.Code.Ok
            ? AceWireWriter.GameMessage(CharGenVerificationResponse.ResponseOpcode)
                .Write(code)
                .WriteGuid(guid)
                .WriteString16L(name)
                .Write(0u)
                .ToArray()
            : AceWireWriter.GameMessage(CharGenVerificationResponse.ResponseOpcode)
                .Write(code)
                .ToArray();

    private static byte[] BuildPacket(params byte[][] messages)
    {
        int length = messages.Sum(message =>
            MessageFragmentHeader.Size + message.Length);
        var fragments = new byte[length];
        int position = 0;
        uint sequence = 1u;
        foreach (byte[] message in messages)
        {
            position += GameMessageFragment.WriteSingleFragment(
                fragments.AsSpan(position),
                sequence++,
                GameMessageGroup.UIQueue,
                message);
        }
        return PacketCodec.Encode(
            new PacketHeader
            {
                Sequence = 1u,
                Flags = PacketHeaderFlags.BlobFragments,
            },
            fragments,
            outboundIsaac: null);
    }

    private static void InvokeProcessDatagram(WorldSession session, byte[] datagram)
    {
        MethodInfo method = typeof(WorldSession).GetMethod(
            "ProcessDatagram",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(session, [new ReadOnlyMemory<byte>(datagram), null, true]);
    }

    private static PendingLatch ReadPendingLatch(WorldSession session)
    {
        FieldInfo field = typeof(WorldSession).GetField(
            "_pendingCharGenVerification",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (PendingLatch)field.GetValue(session)!;
    }

    // Mirrors WorldSession's private PendingCharGenVerificationRequest enum
    // by name/ordinal — read via reflection above so the test doesn't need
    // InternalsVisibleTo for a single private enum.
    private enum PendingLatch { None, Restore, Create }

    [Fact]
    public void SendCharacterCreation_ThenOkResponse_RoutesToCreateEventNotRestore()
    {
        using WorldSession session = CreateSession();
        session.GameMessageCapture = (_, _) => { };

        session.SendCharacterCreation(
            "testaccount",
            MakeCreateRequest(),
            new uint[CharacterCreate.SkillAdvancementClassCount]);

        var createEvents = new List<CharGenVerificationResponse.Parsed>();
        var restoreEvents = new List<CharacterRestore.Parsed>();
        session.CharacterCreateResponseReceived += createEvents.Add;
        session.CharacterRestoreReceived += restoreEvents.Add;

        byte[] packet = BuildPacket(
            BuildVerificationResponseBody(
                (uint)CharGenVerificationResponse.Code.Ok,
                0x50000010u,
                "NewChar"));
        InvokeProcessDatagram(session, packet);

        CharGenVerificationResponse.Parsed created = Assert.Single(createEvents);
        Assert.True(created.IsOk);
        Assert.Equal(0x50000010u, created.Guid);
        Assert.Equal("NewChar", created.Name);
        Assert.Empty(restoreEvents);
        Assert.Equal(PendingLatch.None, ReadPendingLatch(session));
    }

    [Fact]
    public void SendCharacterCreation_ThenFailureResponse_RoutesToCreateEventWithNullIdentity()
    {
        using WorldSession session = CreateSession();
        session.GameMessageCapture = (_, _) => { };

        session.SendCharacterCreation(
            "testaccount",
            MakeCreateRequest(),
            new uint[CharacterCreate.SkillAdvancementClassCount]);

        CharGenVerificationResponse.Parsed? created = null;
        session.CharacterCreateResponseReceived += parsed => created = parsed;

        byte[] packet = BuildPacket(
            BuildVerificationResponseBody(
                (uint)CharGenVerificationResponse.Code.NameInUse,
                guid: 0u,
                name: string.Empty));
        InvokeProcessDatagram(session, packet);

        Assert.NotNull(created);
        Assert.Equal(CharGenVerificationResponse.Code.NameInUse, created!.Value.AsCode);
        Assert.False(created.Value.IsOk);
        Assert.Null(created.Value.Guid);
    }

    [Fact]
    public void SendRestoreCharacter_ThenResponse_StillRoutesToRestoreEvent()
    {
        using WorldSession session = CreateSession();
        session.GameMessageCapture = (_, _) => { };

        session.SendRestoreCharacter(0x50000001u);

        var restoreEvents = new List<CharacterRestore.Parsed>();
        var createEvents = new List<CharGenVerificationResponse.Parsed>();
        session.CharacterRestoreReceived += restoreEvents.Add;
        session.CharacterCreateResponseReceived += createEvents.Add;

        byte[] packet = BuildPacket(
            BuildVerificationResponseBody(
                (uint)CharGenVerificationResponse.Code.Ok,
                0x50000001u,
                "Restored"));
        InvokeProcessDatagram(session, packet);

        CharacterRestore.Parsed restored = Assert.Single(restoreEvents);
        Assert.Equal(0x50000001u, restored.Guid);
        Assert.Equal("Restored", restored.Name);
        Assert.Empty(createEvents);
    }

    [Fact]
    public void OverlappingSend_OverwritesTheLatch_ReplyRoutesToNewestRequest()
    {
        using WorldSession session = CreateSession();
        session.GameMessageCapture = (_, _) => { };

        session.SendRestoreCharacter(0x50000001u);
        session.SendCharacterCreation(
            "testaccount",
            MakeCreateRequest(),
            new uint[CharacterCreate.SkillAdvancementClassCount]);
        Assert.Equal(PendingLatch.Create, ReadPendingLatch(session));

        var restoreEvents = new List<CharacterRestore.Parsed>();
        var createEvents = new List<CharGenVerificationResponse.Parsed>();
        session.CharacterRestoreReceived += restoreEvents.Add;
        session.CharacterCreateResponseReceived += createEvents.Add;

        byte[] packet = BuildPacket(
            BuildVerificationResponseBody(
                (uint)CharGenVerificationResponse.Code.Ok,
                0x50000001u,
                "Restored"));
        InvokeProcessDatagram(session, packet);

        Assert.Empty(restoreEvents);
        Assert.Single(createEvents);
        Assert.Equal(PendingLatch.None, ReadPendingLatch(session));
    }

    [Fact]
    public void ResponseWithNoOutstandingRequest_IsDroppedAndNeverMisattributed()
    {
        using WorldSession session = CreateSession();

        var restoreEvents = new List<CharacterRestore.Parsed>();
        var createEvents = new List<CharGenVerificationResponse.Parsed>();
        session.CharacterRestoreReceived += restoreEvents.Add;
        session.CharacterCreateResponseReceived += createEvents.Add;

        byte[] packet = BuildPacket(
            BuildVerificationResponseBody(
                (uint)CharGenVerificationResponse.Code.Ok,
                0x50000099u,
                "Stray"));
        InvokeProcessDatagram(session, packet);

        Assert.Empty(restoreEvents);
        Assert.Empty(createEvents);
        Assert.Equal(PendingLatch.None, ReadPendingLatch(session));
    }

    [Fact]
    public void SecondResponse_AfterFirstAlreadyConsumed_IsDroppedNotMisattributed()
    {
        using WorldSession session = CreateSession();
        session.GameMessageCapture = (_, _) => { };
        session.SendCharacterCreation(
            "testaccount",
            MakeCreateRequest(),
            new uint[CharacterCreate.SkillAdvancementClassCount]);

        var createEvents = new List<CharGenVerificationResponse.Parsed>();
        session.CharacterCreateResponseReceived += createEvents.Add;

        byte[] first = BuildPacket(
            BuildVerificationResponseBody(
                (uint)CharGenVerificationResponse.Code.Ok, 0x50000010u, "NewChar"));
        InvokeProcessDatagram(session, first);
        Assert.Single(createEvents);

        byte[] second = BuildPacket(
            BuildVerificationResponseBody(
                (uint)CharGenVerificationResponse.Code.Ok, 0x50000011u, "Stray"));
        InvokeProcessDatagram(session, second);

        // Still exactly one — the second reply was dropped, not appended.
        Assert.Single(createEvents);
    }

    [Fact]
    public void Dispose_ClearsTheOutstandingLatch()
    {
        WorldSession session = CreateSession();
        session.GameMessageCapture = (_, _) => { };
        session.SendRestoreCharacter(0x50000001u);
        Assert.Equal(PendingLatch.Restore, ReadPendingLatch(session));

        session.Dispose();

        Assert.Equal(PendingLatch.None, ReadPendingLatch(session));
    }

    [Fact]
    public void BuildRequestBody_InvalidSkillCount_DoesNotArmTheLatch()
    {
        using WorldSession session = CreateSession();
        session.GameMessageCapture = (_, _) => { };

        Assert.Throws<ArgumentException>(() =>
            session.SendCharacterCreation(
                "testaccount",
                MakeCreateRequest(),
                new uint[10]));

        Assert.Equal(PendingLatch.None, ReadPendingLatch(session));
    }
}
