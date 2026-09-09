using System.Buffers.Binary;
using System.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;
using AcDream.Core.Net.Tests.Transport;

namespace AcDream.Core.Net.Tests;

public sealed class DataCheckTests
{
    [Fact]
    public void PolledConnectionKeepsEventsOnCallingThread()
    {
        var transport = new FakeAceTransport();
        using var session = Create(transport);
        int ownerThread = Environment.CurrentManagedThreadId;
        int rosters = 0;
        session.CharacterListReceived += _ =>
        {
            Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
            rosters++;
        };
        session.BeginConnect("test", "test", TimeSpan.FromSeconds(3));
        Assert.Equal(0, rosters);
        Assert.False(session.PollConnect());
        while (!session.PollConnect()) Thread.Sleep(1);
        Assert.Equal(1, rosters);
        Assert.Equal(ConnectionPhase.Ready, session.ConnectionProgress.Phase);
    }

    [Fact]
    public void ReportsInstalledVersionsWithCompleteResponseEnvelope()
    {
        byte[] response = DddInterrogationResponse.Build(new(3, 1, 0));
        uint[] words = Enumerable.Range(0, response.Length / 4)
            .Select(i => BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(i * 4))).ToArray();
        Assert.Equal(new uint[] { 0xF7E6, 1, 3,
            0, 1, 3, unchecked((uint)-3), 1,
            1, 2, 1, 1,
            1, 3, 0,
            0, 0 }, words);
    }

    [Fact]
    public void RosterDoesNotCompleteDataCheck()
    {
        var transport = new FakeAceTransport();
        using var session = Create(transport);
        bool rosterSeen = false;
        session.CharacterListReceived += _ =>
        {
            rosterSeen = true;
            Assert.Equal(ConnectionPhase.CheckingData, session.ConnectionProgress.Phase);
            Assert.NotEqual(WorldSession.State.InCharacterSelect, session.CurrentState);
        };
        session.Connect("test", "test", TimeSpan.FromSeconds(3));
        Assert.True(rosterSeen);
        Assert.Equal(ConnectionPhase.Ready, session.ConnectionProgress.Phase);
        Assert.Equal(WorldSession.State.InCharacterSelect, session.CurrentState);
        Assert.Single(transport.Model.DispatchedMessages,
            message => BinaryPrimitives.ReadUInt32LittleEndian(message) == 0xF7EAu);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredUpdateCannotBeOverriddenByCompletion(bool purgeOnly)
    {
        var transport = new FakeAceTransport { AutoCompleteDataCheck = false };
        using var session = Create(transport);
        Reply(transport, BuildUpdate(purgeOnly), BitConverter.GetBytes(0xF7EAu));
        Assert.Throws<UnsupportedDataUpdateException>(() =>
            session.Connect("test", "test", TimeSpan.FromSeconds(3)));
        Assert.Equal(ConnectionPhase.Unsupported, session.ConnectionProgress.Phase);
        Assert.Equal(WorldSession.State.Failed, session.CurrentState);
        Assert.Throws<UnsupportedDataUpdateException>(() => session.EnterWorld());
        Assert.DoesNotContain(transport.Model.DispatchedMessages,
            message => BinaryPrimitives.ReadUInt32LittleEndian(message) == 0xF7EAu);
    }

    [Fact]
    public void EmptyBeginWaitsForCompletionAndSucceeds()
    {
        var transport = new FakeAceTransport { AutoCompleteDataCheck = false };
        using var session = Create(transport);
        Reply(transport, Words(0xF7E7, 0, 0), Words(0xF7EA));
        session.Connect("test", "test", TimeSpan.FromSeconds(3));
        Assert.Equal(ConnectionPhase.Ready, session.ConnectionProgress.Phase);
    }

    [Fact]
    public void MalformedUpdateFailsInsteadOfAdvancing()
    {
        var transport = new FakeAceTransport { AutoCompleteDataCheck = false };
        using var session = Create(transport);
        Reply(transport, Words(0xF7E7, 1, 1), Words(0xF7EA));
        Assert.Throws<InvalidDataException>(() => session.Connect("test", "test", TimeSpan.FromSeconds(3)));
        Assert.Equal(ConnectionPhase.Failed, session.ConnectionProgress.Phase);
    }

    [Fact]
    public void MissingCompletionTimesOutEvenWithRoster()
    {
        var transport = new FakeAceTransport { AutoCompleteDataCheck = false };
        using var session = Create(transport);
        var error = Assert.Throws<TimeoutException>(() =>
            session.Connect("test", "test", TimeSpan.FromMilliseconds(500)));
        Assert.Contains("game-data check", error.Message);
        Assert.Equal(WorldSession.State.Failed, session.CurrentState);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void InvalidBeginVectorsAreRejected(uint count)
    {
        Assert.Throws<InvalidDataException>(() => DddBegin.Parse(Words(0xF7E7, 0, count, 0)));
    }

    private static WorldSession Create(FakeAceTransport transport) =>
        new(new IPEndPoint(IPAddress.Loopback, 9000), transport);

    private static void Reply(FakeAceTransport transport, params byte[][] messages)
    {
        transport.Model.MessageDispatched += body =>
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(body) != DddInterrogationResponse.Opcode)
                return;
            foreach (byte[] message in messages)
                transport.Model.EnqueueGameMessage(message, GameMessageGroup.DatabaseQueue);
        };
    }

    private static byte[] BuildUpdate(bool purgeOnly) => purgeOnly
        ? Words(0xF7E7, 0, 1, 1, 2, 1, 0, 1, 123)
        : Words(0xF7E7, 100, 1, 0, 1, 1, 1, 123, 0);

    private static byte[] Words(params uint[] words)
    {
        var writer = new PacketWriter();
        foreach (uint word in words) writer.WriteUInt32(word);
        return writer.ToArray();
    }
}
