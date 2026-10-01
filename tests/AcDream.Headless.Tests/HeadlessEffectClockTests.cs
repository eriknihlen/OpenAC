using System.Buffers.Binary;
using System.Net;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Spells;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Tests;

/// <summary>
/// The windowless host stamps an arriving effect with the runtime's effect
/// clock, the one the plugin surface reads time left against.
/// </summary>
public sealed class HeadlessEffectClockTests
{
    /// <summary>
    /// Mutation: bind the session's clock to the simulation clock, which has
    /// not moved here, and the effect is stamped as having started at 0.
    /// </summary>
    [Fact]
    public void AnArrivingEffectIsStampedOnTheEffectClock()
    {
        var time = new ManualTimeProvider();
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations,
            timeProvider: time);
        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);
        time.Advance(TimeSpan.FromSeconds(42));

        operations.Session!.GameEvents.Dispatch(GameEventEnvelope.TryParse(
            WrapEnvelope(
                GameEventType.MagicUpdateEnchantment,
                BuildEnchantment(spellId: 42, duration: 60d)))!.Value);

        ActiveEnchantmentRecord record = Assert.Single(
            host.Runtime.CharacterOwner.Spellbook.ActiveEnchantmentSnapshot);
        Assert.Equal(42d, host.Runtime.EffectClock.NowSeconds, 6);
        Assert.Equal(42d, record.StartTime, 6);
    }

    private static byte[] BuildEnchantment(ushort spellId, double duration)
    {
        byte[] payload = new byte[60];
        int offset = 0;
        WriteU16(spellId); WriteU16(1); WriteU16(3); WriteU16(0);
        WriteU32(8); WriteF64(0d); WriteF64(duration); WriteU32(0u);
        WriteF32(0.1f); WriteF32(-1f); WriteF64(0d);
        WriteU32(0u); WriteU32(0u); WriteF32(0f);
        return payload;

        void WriteU16(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), value); offset += 2; }
        void WriteU32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), value); offset += 4; }
        void WriteF32(float value) { BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset), value); offset += 4; }
        void WriteF64(double value) { BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(offset), value); offset += 8; }
    }

    private static byte[] WrapEnvelope(GameEventType type, byte[] payload)
    {
        byte[] body = new byte[GameEventEnvelope.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, GameEventEnvelope.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)type);
        Array.Copy(payload, 0, body, GameEventEnvelope.HeaderSize, payload.Length);
        return body;
    }

    private static HeadlessSessionDescriptor Descriptor() => new()
    {
        Id = "bot",
        Endpoint = new HeadlessEndpointDescriptor
        {
            Host = "127.0.0.1",
            Port = 9000,
        },
        Account = "account",
        Character = new HeadlessCharacterSelector
        {
            Name = "headless",
        },
        Policy = new HeadlessBotPolicyDescriptor
        {
            Id = "idle",
        },
        Credential = new HeadlessCredentialReference
        {
            Provider = HeadlessCredentialProviderKind.StandardInput,
            Reference = "fixture-password",
        },
    };

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) =>
            _timestamp = checked(_timestamp + duration.Ticks);
    }

    private sealed class FixtureSessionOperations : ILiveSessionOperations
    {
        public WorldSession? Session { get; private set; }

        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint) =>
            Session = new WorldSession(endpoint).TakingItsSends();

        public void Connect(WorldSession session, string user, string password)
        {
        }

        public CharacterList.Parsed GetCharacters(WorldSession session) =>
            new(
                0u,
                [new CharacterList.Character(0x50000001u, "Headless", 0u)],
                [],
                11,
                "account",
                true,
                true);

        public void EnterWorld(WorldSession session, int activeCharacterIndex)
        {
        }

        public void Tick(WorldSession session)
        {
        }

        public void DisposeSession(WorldSession session) => session.Dispose();
    }
}
