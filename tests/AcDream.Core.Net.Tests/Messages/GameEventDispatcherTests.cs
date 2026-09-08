using System;
using System.Buffers.Binary;
using System.Text;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class GameEventDispatcherTests
{
    // Helper: build a 0xF7B0 envelope with given sub-opcode + payload bytes.
    private static byte[] MakeEnvelope(GameEventType type, ReadOnlySpan<byte> payload,
        uint playerGuid = 0x12345678u, uint sequence = 0)
    {
        byte[] body = new byte[GameEventEnvelope.HeaderSize + payload.Length];
        var span = body.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, GameEventEnvelope.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), playerGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), (uint)type);
        payload.CopyTo(span.Slice(GameEventEnvelope.HeaderSize));
        return body;
    }

    [Fact]
    public void TryParse_ValidEnvelope_ReturnsFields()
    {
        byte[] body = MakeEnvelope(GameEventType.PlayerDescription,
            payload: new byte[8],
            playerGuid: 0xAABBCCDD, sequence: 42);

        var env = GameEventEnvelope.TryParse(body);
        Assert.NotNull(env);
        Assert.Equal(0xAABBCCDDu, env!.Value.PlayerGuid);
        Assert.Equal(42u, env.Value.Sequence);
        Assert.Equal(GameEventType.PlayerDescription, env.Value.EventType);
        Assert.Equal(8, env.Value.Payload.Length);
    }

    [Fact]
    public void TryParse_WrongOuterOpcode_ReturnsNull()
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0xDEADBEEFu);
        Assert.Null(GameEventEnvelope.TryParse(body));
    }

    [Fact]
    public void TryParse_TooShort_ReturnsNull()
    {
        Assert.Null(GameEventEnvelope.TryParse(new byte[10]));
    }

    [Fact]
    public void Dispatch_CallsHandler_ForRegisteredType()
    {
        var dispatcher = new GameEventDispatcher();
        GameEventEnvelope received = default;
        int calls = 0;
        dispatcher.Register(GameEventType.Tell, env => { received = env; calls++; });

        var body = MakeEnvelope(GameEventType.Tell, new byte[4]);
        var env = GameEventEnvelope.TryParse(body);
        dispatcher.Dispatch(env!.Value);

        Assert.Equal(1, calls);
        Assert.Equal(GameEventType.Tell, received.EventType);
    }

    [Fact]
    public void Dispatch_Unhandled_CountedButNoThrow()
    {
        var dispatcher = new GameEventDispatcher();
        var body = MakeEnvelope(GameEventType.HouseProfile, Array.Empty<byte>());
        var env = GameEventEnvelope.TryParse(body);
        dispatcher.Dispatch(env!.Value);
        dispatcher.Dispatch(env.Value);

        Assert.Equal(2, dispatcher.GetUnhandledCount(GameEventType.HouseProfile));
    }

    [Fact]
    public void Dispatch_HandlerThrows_DoesNotPropagate()
    {
        var dispatcher = new GameEventDispatcher();
        dispatcher.Register(GameEventType.Tell, _ => throw new InvalidOperationException("bad handler"));
        var body = MakeEnvelope(GameEventType.Tell, Array.Empty<byte>());

        // Should not throw; error is logged instead.
        dispatcher.Dispatch(GameEventEnvelope.TryParse(body)!.Value);
    }

    [Fact]
    public void Unregister_RemovesHandler_AndMovesToUnhandled()
    {
        var dispatcher = new GameEventDispatcher();
        int calls = 0;
        dispatcher.Register(GameEventType.Tell, _ => calls++);
        dispatcher.Unregister(GameEventType.Tell);

        var body = MakeEnvelope(GameEventType.Tell, Array.Empty<byte>());
        dispatcher.Dispatch(GameEventEnvelope.TryParse(body)!.Value);

        Assert.Equal(0, calls);
        Assert.Equal(1, dispatcher.GetUnhandledCount(GameEventType.Tell));
    }

    [Fact]
    public void OwnedRegistration_DisposeRestoresUnownedPredecessor()
    {
        var dispatcher = new GameEventDispatcher();
        var calls = new List<string>();
        dispatcher.Register(GameEventType.Tell, _ => calls.Add("baseline"));
        IDisposable owned = dispatcher.RegisterOwned(
            GameEventType.Tell,
            _ => calls.Add("owned"));

        dispatcher.Dispatch(new GameEventEnvelope(0, 0, GameEventType.Tell, default));
        owned.Dispose();
        dispatcher.Dispatch(new GameEventEnvelope(0, 0, GameEventType.Tell, default));

        Assert.Equal(["owned", "baseline"], calls);
        Assert.Equal(1, dispatcher.RegisteredHandlerCount);
    }

    [Fact]
    public void NestedOwnedRegistrations_DisposeOlderFirst_SkipsRetiredPredecessor()
    {
        var dispatcher = new GameEventDispatcher();
        var calls = new List<string>();
        dispatcher.Register(GameEventType.Tell, _ => calls.Add("baseline"));
        IDisposable ownerA = dispatcher.RegisterOwned(
            GameEventType.Tell,
            _ => calls.Add("A"));
        IDisposable ownerB = dispatcher.RegisterOwned(
            GameEventType.Tell,
            _ => calls.Add("B"));

        ownerA.Dispose();
        dispatcher.Dispatch(new GameEventEnvelope(0, 0, GameEventType.Tell, default));
        ownerB.Dispose();
        dispatcher.Dispatch(new GameEventEnvelope(0, 0, GameEventType.Tell, default));

        Assert.Equal(["B", "baseline"], calls);
    }

    [Fact]
    public void NestedOwnedRegistrations_DisposeNewestFirst_RestoresLiveOlderOwner()
    {
        var dispatcher = new GameEventDispatcher();
        var calls = new List<string>();
        dispatcher.Register(GameEventType.Tell, _ => calls.Add("baseline"));
        IDisposable ownerA = dispatcher.RegisterOwned(
            GameEventType.Tell,
            _ => calls.Add("A"));
        IDisposable ownerB = dispatcher.RegisterOwned(
            GameEventType.Tell,
            _ => calls.Add("B"));

        ownerB.Dispose();
        dispatcher.Dispatch(new GameEventEnvelope(0, 0, GameEventType.Tell, default));
        ownerA.Dispose();
        ownerA.Dispose();
        ownerB.Dispose();
        dispatcher.Dispatch(new GameEventEnvelope(0, 0, GameEventType.Tell, default));

        Assert.Equal(["A", "baseline"], calls);
    }

    [Fact]
    public void LaterUnownedReplacement_SurvivesOwnedTokenDisposal()
    {
        var dispatcher = new GameEventDispatcher();
        var calls = new List<string>();
        IDisposable owned = dispatcher.RegisterOwned(
            GameEventType.Tell,
            _ => calls.Add("owned"));
        dispatcher.Register(GameEventType.Tell, _ => calls.Add("replacement"));

        owned.Dispose();
        dispatcher.Dispatch(new GameEventEnvelope(0, 0, GameEventType.Tell, default));

        Assert.Equal(["replacement"], calls);
    }

    [Fact]
    public void OwnedRegistrationWithoutPredecessor_BecomesUnhandledAfterDispose()
    {
        var dispatcher = new GameEventDispatcher();
        IDisposable owned = dispatcher.RegisterOwned(GameEventType.Tell, _ => { });

        owned.Dispose();
        owned.Dispose();
        dispatcher.Dispatch(new GameEventEnvelope(0, 0, GameEventType.Tell, default));

        Assert.Equal(0, dispatcher.RegisteredHandlerCount);
        Assert.Equal(1, dispatcher.GetUnhandledCount(GameEventType.Tell));
    }

    [Fact]
    public void ExplicitUnregister_RetiresCurrentChainWithoutResurrection()
    {
        var dispatcher = new GameEventDispatcher();
        dispatcher.Register(GameEventType.Tell, _ => { });
        IDisposable ownerA = dispatcher.RegisterOwned(GameEventType.Tell, _ => { });
        IDisposable ownerB = dispatcher.RegisterOwned(GameEventType.Tell, _ => { });

        dispatcher.Unregister(GameEventType.Tell);
        ownerA.Dispose();
        ownerB.Dispose();
        dispatcher.Dispatch(new GameEventEnvelope(0, 0, GameEventType.Tell, default));

        Assert.Equal(0, dispatcher.RegisteredHandlerCount);
        Assert.Equal(1, dispatcher.GetUnhandledCount(GameEventType.Tell));
    }

    [Fact]
    public void HandlerCanDisposeItselfAndInstallRawReplacementDuringDispatch()
    {
        var dispatcher = new GameEventDispatcher();
        var calls = new List<string>();
        IDisposable? owned = null;
        owned = dispatcher.RegisterOwned(GameEventType.Tell, _ =>
        {
            calls.Add("owned");
            owned!.Dispose();
            dispatcher.Register(GameEventType.Tell, _ => calls.Add("replacement"));
        });

        dispatcher.Dispatch(new GameEventEnvelope(0, 0, GameEventType.Tell, default));
        dispatcher.Dispatch(new GameEventEnvelope(0, 0, GameEventType.Tell, default));

        Assert.Equal(["owned", "replacement"], calls);
        Assert.Equal(1, dispatcher.RegisteredHandlerCount);
    }

    [Fact]
    public void RawReplacementAfterNestedOwners_SurvivesEveryOwnedDisposeOrder()
    {
        var dispatcher = new GameEventDispatcher();
        var calls = new List<string>();
        IDisposable ownerA = dispatcher.RegisterOwned(GameEventType.Tell, _ => calls.Add("A"));
        IDisposable ownerB = dispatcher.RegisterOwned(GameEventType.Tell, _ => calls.Add("B"));
        dispatcher.Register(GameEventType.Tell, _ => calls.Add("raw"));

        ownerA.Dispose();
        ownerB.Dispose();
        dispatcher.Dispatch(new GameEventEnvelope(0, 0, GameEventType.Tell, default));

        Assert.Equal(["raw"], calls);
    }

    [Fact]
    public void OwnedRegistration_ConcurrentDisposeRetiresExactlyOnce()
    {
        var dispatcher = new GameEventDispatcher();
        IDisposable owned = dispatcher.RegisterOwned(GameEventType.Tell, _ => { });

        Parallel.For(0, 64, _ => owned.Dispose());

        Assert.Equal(0, dispatcher.RegisteredHandlerCount);
    }

    // ── Per-event parser tests ───────────────────────────────────────────────

    private static byte[] MakeString16L(string s)
    {
        byte[] data = Encoding.ASCII.GetBytes(s);
        int recordSize = 2 + data.Length;
        int padding = (4 - (recordSize & 3)) & 3;
        byte[] result = new byte[recordSize + padding];
        BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)data.Length);
        Array.Copy(data, 0, result, 2, data.Length);
        return result;
    }

    [Fact]
    public void ParseChannelBroadcast_RoundTrip()
    {
        byte[] chan = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(chan, 42);
        byte[] sender = MakeString16L("Hero");
        byte[] msg = MakeString16L("hello allegiance");

        byte[] payload = new byte[chan.Length + sender.Length + msg.Length];
        Buffer.BlockCopy(chan, 0, payload, 0, chan.Length);
        Buffer.BlockCopy(sender, 0, payload, chan.Length, sender.Length);
        Buffer.BlockCopy(msg, 0, payload, chan.Length + sender.Length, msg.Length);

        var parsed = GameEvents.ParseChannelBroadcast(payload);
        Assert.NotNull(parsed);
        Assert.Equal(42u, parsed!.Value.ChannelId);
        Assert.Equal("Hero", parsed.Value.SenderName);
        Assert.Equal("hello allegiance", parsed.Value.Message);
    }

    [Fact]
    public void ParseTell_RoundTrip()
    {
        byte[] msg = MakeString16L("hi");
        byte[] sender = MakeString16L("Alice");
        byte[] tail = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(tail, 0xAAu);
        BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(4), 0xBBu);
        BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(8), 1u);

        byte[] payload = new byte[msg.Length + sender.Length + tail.Length];
        Buffer.BlockCopy(msg, 0, payload, 0, msg.Length);
        Buffer.BlockCopy(sender, 0, payload, msg.Length, sender.Length);
        Buffer.BlockCopy(tail, 0, payload, msg.Length + sender.Length, tail.Length);

        var parsed = GameEvents.ParseTell(payload);
        Assert.NotNull(parsed);
        Assert.Equal("hi", parsed!.Value.Message);
        Assert.Equal("Alice", parsed.Value.SenderName);
        Assert.Equal(0xAAu, parsed.Value.SenderGuid);
        Assert.Equal(0xBBu, parsed.Value.TargetGuid);
        Assert.Equal(1u, parsed.Value.ChatType);
    }

    [Fact]
    public void ParseUpdateHealth_RoundTrip()
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0xC0DEu);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(4), 0.42f);

        var parsed = GameEvents.ParseUpdateHealth(payload);
        Assert.NotNull(parsed);
        Assert.Equal(0xC0DEu, parsed!.Value.TargetGuid);
        Assert.Equal(0.42f, parsed.Value.HealthPercent, 4);
    }

    [Fact]
    public void ParseQueryItemManaResponse_RoundTripIncludingValidity()
    {
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x50000A01u);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(4), 0.625f);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 1u);

        var parsed = GameEvents.ParseQueryItemManaResponse(payload);

        Assert.NotNull(parsed);
        Assert.Equal(0x50000A01u, parsed!.Value.ItemGuid);
        Assert.Equal(0.625f, parsed.Value.ManaPercent);
        Assert.True(parsed.Value.Valid);
        Assert.Null(GameEvents.ParseQueryItemManaResponse(payload.AsSpan(0, 8)));
    }

    [Fact]
    public void ParseWieldObject_acceptsAceEightBytePayload()
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x50000A01u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)EquipMask.ChestArmor);

        var parsed = GameEvents.ParseWieldObject(payload);

        Assert.NotNull(parsed);
        Assert.Equal(0x50000A01u, parsed!.Value.ItemGuid);
        Assert.Equal((uint)EquipMask.ChestArmor, parsed.Value.EquipLoc);
    }

    [Fact]
    public void ParseWeenieError_RoundTrip()
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 42u);
        Assert.Equal(42u, GameEvents.ParseWeenieError(payload));
    }

    [Fact]
    public void ParseTransient_RoundTrip()
    {
        byte[] payload = MakeString16L("Your spell fizzled!");

        string? parsed = GameEvents.ParseTransient(payload);

        Assert.Equal("Your spell fizzled!", parsed);
    }

    [Fact]
    public void ParseTransient_ExactAcePayload_IsNotDropped()
    {
        foreach (string text in new[] { "", "a", "bb", "ccc", "dddd", "Your spell fizzled!" })
        {
            byte[] payload = MakeString16L(text);
            Assert.Equal(text, GameEvents.ParseTransient(payload));
        }
    }

    [Fact]
    public void ParseMagicUpdateSpell_RoundTrip()
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x123403E1u);
        var parsed = GameEvents.ParseMagicUpdateSpell(payload);
        Assert.Equal(0x123403E1u, parsed);
    }
}
