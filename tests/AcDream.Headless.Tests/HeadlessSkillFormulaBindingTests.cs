using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using AcDream.Content;
using AcDream.Headless.Configuration;
using AcDream.Core.Chat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Player;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Runtime.Session;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using DatDatabaseImpl = DatReaderWriter.DatDatabase;
using DatIDBObj = DatReaderWriter.Lib.IO.IDBObj;

namespace AcDream.Headless.Tests;

/// <summary>
/// Host parity for the skill numbers a windowless session reports. A skill's
/// level is its ranks plus its starting value plus a bonus derived from the
/// attributes the skill is built on; the windowless host used to leave that
/// last term out entirely, so a maxed skill read a couple of hundred points
/// low and every decision made from a skill — what is castable, how fast the
/// character runs — was made on the wrong number.
/// </summary>
public sealed class HeadlessSkillFormulaBindingTests
{
    private const uint SkillTableId = 0x0E000004u;
    private const uint RunSkillId = 24u;

    [Fact]
    public void TheWindowlessHostBindsASkillFormulaResolver()
    {
        using var fixture = new HostFixture();

        LiveCharacterSessionBindings bindings =
            fixture.Host.CreateCharacterBindings();

        Assert.NotNull(bindings.ResolveSkillFormulaBonus);
    }

    [Fact]
    public void APlayerDescriptionCarriesTheAttributeTermIntoTheSkillLevel()
    {
        using var fixture = new HostFixture();
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9));
        LiveCharacterSessionBindings bindings =
            fixture.Host.CreateCharacterBindings();

        var router = new LiveSessionEventRouter(
            session,
            NoOpEntitySink(),
            new LiveEnvironmentSessionSink(_ => { }, _ => { }),
            new LiveInventorySessionBindings(
                new ClientObjectTable(),
                PlayerGuid: () => 0x50000001u,
                OnShortcuts: null,
                OnUseDone: null,
                ItemMana: null,
                ExternalContainers: null),
            bindings,
            new LiveSocialSessionBindings(
                new ChatLog(),
                new TurbineChatState(),
                null,
                null),
            fixture.Host.Runtime.ActionOwner);
        router.Attach();

        session.GameEvents.Dispatch(
            GameEventEnvelope.TryParse(PlayerDescriptionWithRun())!.Value);

        LocalPlayerState.SkillSnapshot? run =
            bindings.Character.LocalPlayer.GetSkill(RunSkillId);
        Assert.NotNull(run);
        // (315 Strength + 315 Coordination) / 2 = 315, plus 208 ranks.
        Assert.Equal(315u, run!.Value.FormulaBonus);
        Assert.Equal(523u, run.Value.BaseLevel);

        router.Dispose();
    }

    /// <summary>
    /// Strength and Coordination both at 315, Run trained with 208 ranks. The
    /// fixture skill table gives Run the (Strength + Coordination) / 2
    /// formula, so the attribute term is 315.
    /// </summary>
    private static byte[] PlayerDescriptionWithRun()
    {
        var payload = new MemoryStream();
        using (var w = new BinaryWriter(
            payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(0u);        // property flags
            w.Write(0x52u);     // player weenie type
            w.Write(0x03u);     // vector flags: attributes | skills
            w.Write(0u);        // has health
            w.Write(0x09u);     // attribute flags: Strength | Coordination
            w.Write(215u);      // Strength ranks
            w.Write(100u);      // Strength start
            w.Write(0u);        // Strength xp
            w.Write(215u);      // Coordination ranks
            w.Write(100u);      // Coordination start
            w.Write(0u);        // Coordination xp

            w.Write((ushort)1);          // one skill
            w.Write((ushort)8);          // buckets
            w.Write(RunSkillId);
            w.Write((ushort)208);        // ranks
            w.Write((ushort)1);          // adjust pp
            w.Write(2u);                 // trained
            w.Write(0u);                 // xp
            w.Write(0u);                 // init
            w.Write(0u);                 // resistance
            w.Write(0d);                 // last used
        }

        byte[] body = payload.ToArray();
        byte[] envelope = new byte[GameEventEnvelope.HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(
            envelope, GameEventEnvelope.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(4), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(8), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(
            envelope.AsSpan(12), (uint)GameEventType.PlayerDescription);
        body.CopyTo(envelope, GameEventEnvelope.HeaderSize);
        return envelope;
    }

    private static LiveEntitySessionSink NoOpEntitySink() => new(
        Spawned: _ => { },
        Deleted: _ => { },
        PickedUp: _ => { },
        MotionUpdated: _ => { },
        PositionUpdated: _ => { },
        VectorUpdated: _ => { },
        StateUpdated: _ => { },
        ParentUpdated: _ => { },
        TeleportStarted: _ => { },
        AppearanceUpdated: _ => { },
        DescriptionRefreshed: _ => { },
        PlayPhysicsScript: _ => { },
        PlayPhysicsScriptType: _ => { },
        SoundEvent: _ => { },
        IsHeld: _ => false);

    // ── Fixture ───────────────────────────────────────────────────────────

    private sealed class HostFixture : IDisposable
    {
        private readonly HeadlessProcessContentOwner _owner;
        private readonly HeadlessCredentialSecret _credential;
        private readonly StringWriter _diagnostics = new();

        internal HostFixture()
        {
            _owner = new HeadlessProcessContentOwner(
                new HeadlessContentDescriptor
                {
                    DatDirectory = "fixture-dats",
                    PreparedAssetPath = "fixture.pak",
                },
                _ => { },
                new SkillTableContentFactory());
            _credential = new HeadlessCredentialSecret("fixture", "password");
            Host = new HeadlessSessionHost(
                HeadlessSessionHostTests.Descriptor(),
                _credential,
                new HeadlessDiagnosticWriter(_diagnostics),
                new UnusedSessionOperations(),
                contentLease: _owner.AcquireLease("skill-formula-fixture"));
        }

        internal HeadlessSessionHost Host { get; }

        public void Dispose()
        {
            Host.Dispose();
            _owner.Dispose();
            _credential.Dispose();
            _diagnostics.Dispose();
        }
    }

    private sealed class SkillTableContentFactory
        : IHeadlessProcessContentFactory
    {
        public HeadlessOpenedProcessContent Open(
            HeadlessContentDescriptor descriptor,
            Action<string> diagnostic) =>
            new(
                new SkillTableDatReaderWriter(),
                DispatchProxy.Create<ITestPreparedSource, TestResourceProxy>(),
                MagicCatalog.Empty,
                ImmutableArray.CreateRange(new float[256]));
    }

    private sealed class SkillTableDatReaderWriter : IDatReaderWriter
    {
        private readonly StubDatabase _db = new();
        private readonly SkillTable _skills = BuildSkillTable();

        public string SourceDirectory => string.Empty;
        public IDatDatabase Portal => _db;
        public IDatDatabase Cell => _db;
        public ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; } =
            new(new Dictionary<uint, IDatDatabase>());
        public IDatDatabase HighRes => _db;
        public IDatDatabase Language => _db;
        public IDatDatabase Local => _db;
        public ReadOnlyDictionary<uint, uint> RegionFileMap { get; } =
            new(new Dictionary<uint, uint>());
        public int PortalIteration => 0;
        public int CellIteration => 0;
        public int HighResIteration => 0;
        public int LanguageIteration => 0;

        public bool TryGetFileBytes(
            uint regionId, uint fileId, ref byte[] bytes, out int bytesRead)
        {
            bytesRead = 0;
            return false;
        }

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : DatIDBObj =>
            Array.Empty<uint>();

        public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) =>
            Array.Empty<IDatReaderWriter.IdResolution>();

        public bool TrySave<T>(T obj, int iteration = 0) where T : DatIDBObj =>
            throw new NotSupportedException();

        public bool TrySave<T>(uint regionId, T obj, int iteration = 0)
            where T : DatIDBObj => throw new NotSupportedException();

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : DatIDBObj =>
            fileId == SkillTableId && typeof(T) == typeof(SkillTable)
                ? (T)(object)_skills
                : default;

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value)
            where T : DatIDBObj
        {
            value = Get<T>(fileId);
            return value is not null;
        }

        public void Dispose()
        {
        }

        private static SkillTable BuildSkillTable()
        {
            var table = new SkillTable();
            table.Skills.Add((SkillId)RunSkillId, new SkillBase
            {
                MinLevel = 1u,
                Formula = new SkillFormula
                {
                    AdditiveBonus = 0,
                    Attribute1Multiplier = 1,
                    Attribute2Multiplier = 1,
                    Divisor = 2,
                    Attribute1 = AttributeId.Strength,
                    Attribute2 = AttributeId.Coordination,
                },
            });
            return table;
        }

        private sealed class StubDatabase : IDatDatabase
        {
            public DatDatabaseImpl Db => null!;
            public int Iteration => 0;

            public IEnumerable<uint> GetAllIdsOfType<T>()
                where T : DatIDBObj => Array.Empty<uint>();

            public bool TryGet<T>(
                uint fileId, [MaybeNullWhen(false)] out T value)
                where T : DatIDBObj
            {
                value = default;
                return false;
            }

            public bool TryGetFileBytes(
                uint fileId, [MaybeNullWhen(false)] out byte[] value)
            {
                value = default;
                return false;
            }

            public bool TryGetFileBytes(
                uint fileId, ref byte[] bytes, out int bytesRead)
            {
                bytesRead = 0;
                return false;
            }

            public bool TrySave<T>(T obj, int iteration = 0)
                where T : DatIDBObj => false;

            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    /// The fixture never connects: the bindings are read straight off the
    /// host, so nothing here is ever called.
    /// </summary>
    private sealed class UnusedSessionOperations : ILiveSessionOperations
    {
        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint) =>
            throw new NotSupportedException();

        public void Connect(
            WorldSession session, string user, string password) =>
            throw new NotSupportedException();

        public CharacterList.Parsed GetCharacters(WorldSession session) =>
            throw new NotSupportedException();

        public void EnterWorld(WorldSession session, int activeCharacterIndex) =>
            throw new NotSupportedException();

        public void Tick(WorldSession session) =>
            throw new NotSupportedException();

        public void DisposeSession(WorldSession session) => session.Dispose();
    }
}
