using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using AcDream.App.Plugins;
using AcDream.Content;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Spells;
using AcDream.Headless.Plugins;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Plugins;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatIDBObj = DatReaderWriter.Lib.IO.IDBObj;

namespace AcDream.HostParity.Tests;

/// <summary>
/// What the two clients really fill in when each is handed the same installed
/// data files. The seam census compares what a host CLAIMS it can supply;
/// this runs the binding pass over the record each host actually builds and
/// compares the seams that came out, which is what a plugin meets.
///
/// The creature name table is the case that made this worth writing: it is a
/// plain read of the installed files, it was done by drawing code, and so a
/// client without a window told every plugin that everything it could see was
/// of no particular kind.
/// </summary>
public sealed class SharedContentSeamParityTests
{
    /// <summary>A creature kind, and the name the data files give it.</summary>
    private const uint OlthoiSpecies = 14u;
    private const string OlthoiRaw = "Olthoi_Soldier";
    private const string OlthoiDisplay = "Olthoi Soldier";

    /// <summary>
    /// Mutation check: make the shared content pass stop binding the species
    /// resolver, or move it back onto one host's capability record, and this
    /// fails on the windowless client.
    /// </summary>
    [Fact]
    public void BothClientsFillTheSameSeamsFromTheSameContent()
    {
        IReadOnlySet<string> windowed = SeamsFilledBy(ParityHost.Windowed);
        IReadOnlySet<string> windowless = SeamsFilledBy(ParityHost.Windowless);

        Assert.Contains("BindSpeciesNameResolver", windowed);
        Assert.Contains("BindSpeciesNameResolver", windowless);
        Assert.Contains("BindTitleNameResolver", windowed);
        Assert.Contains("BindTitleNameResolver", windowless);

        string[] unexplained = windowed.Except(windowless)
            .Where(static seam => !IsAllowListed(seam, ParityHost.Windowless))
            .Concat(windowless.Except(windowed)
                .Where(static seam => !IsAllowListed(seam, ParityHost.Windowed)))
            .OrderBy(static seam => seam, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unexplained.Length == 0,
            "One client fills a plugin seam the other leaves empty, and the "
            + "allow-list does not carry it: "
            + string.Join(", ", unexplained));
    }

    /// <summary>
    /// The name itself, so that "both fill it" cannot be satisfied by two
    /// resolvers that answer differently.
    /// </summary>
    [Fact]
    public void TheCreatureNameTableReadsTheNameAndItsBaseTable()
    {
        using var content = new CreatureNameContent();
        CreatureDisplayNameResolver resolver =
            CreatureDisplayNameResolver.Load(content);

        Assert.Equal(OlthoiDisplay, resolver.Resolve((int)OlthoiSpecies));
        Assert.Equal("Olthoi", resolver.Resolve(1));
        Assert.Equal(string.Empty, resolver.Resolve(0));
    }

    private static bool IsAllowListed(string seam, string missingHost) =>
        HostParityAllowList.Seams.Any(entry =>
            entry.Member == seam && entry.MissingHost == missingHost);

    /// <summary>
    /// Runs the shared binding pass over the record the named host really
    /// builds, with installed content present, and says which seams it filled.
    /// </summary>
    private static IReadOnlySet<string> SeamsFilledBy(string host)
    {
        using var content = new CreatureNameContent();
        using GameRuntime runtime = NewRuntime();
        using var surface = new RuntimeAutomationSurface();
        return RuntimeAutomationBindings.Apply(
            surface, runtime, CapabilitiesFor(host, runtime, content));
    }

    private static RuntimeAutomationHostCapabilities CapabilitiesFor(
        string host,
        GameRuntime runtime,
        IDatReaderWriter content) =>
        host == ParityHost.Windowed
            ? GraphicalAutomationCapabilities.Build(new GraphicalAutomationParts
            {
                Runtime = runtime,
                Content = content,
                DatLock = new object(),
                MagicCatalog = MagicCatalog.Empty,
                NavigationWalk =
                    Part<AcDream.Runtime.Navigation.NavigationWalkController>(),
                Teleport =
                    Part<AcDream.App.Streaming.LocalPlayerTeleportController>(),
                RetainedUi = Part<AcDream.App.UI.RetailUiRuntime>(),
                Selection =
                    Part<AcDream.App.Interaction.SelectionInteractionController>(),
                Input = Part<AcDream.UI.Abstractions.Input.InputDispatcher>(),
            })
            : HeadlessAutomationCapabilities.Build(new HeadlessAutomationParts
            {
                Runtime = runtime,
                Content = content,
                ContentLock = new object(),
                MagicCatalog = MagicCatalog.Empty,
                NavigationWalk =
                    Part<AcDream.Runtime.Navigation.NavigationWalkController>(),
                Logout = Part<AcDream.Headless.Hosting.HeadlessLogoutAutomation>(),
                AnswerConfirmation = static (_, _) => true,
            });

    /// <summary>A part that exists but is never called.</summary>
    private static T Part<T>() where T : class =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static GameRuntime NewRuntime()
    {
        var operations = new UnusedOperations();
        return new GameRuntime(new GameRuntimeDependencies(
            operations, operations, operations, operations));
    }

    private sealed class UnusedOperations :
        IRuntimeCombatAttackOperations,
        IRuntimeCombatTargetOperations,
        IRuntimeCombatModeOperations,
        IRuntimeSpellCastOperations
    {
        public bool CanStartAttack(bool allowAutoTarget) => false;
        public void PrepareAttackRequest() { }
        public bool SendAttack(
            AttackHeight height, float power, bool allowAutoTarget) => false;
        public void SendCancelAttack() { }
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => false;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(CombatMode mode) { }
        public uint LocalPlayerId => 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;
        public bool IsTargetCompatible(
            uint targetId, SpellMetadata spell, bool showMessage) => false;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }

    /// <summary>
    /// Installed files carrying only the creature name table: one mapping
    /// that extends another, which is how the real files are laid out.
    /// </summary>
    private sealed class CreatureNameContent : IDatReaderWriter
    {
        private const uint BaseMapperDid = 0x2200000Fu;
        private readonly EnumMapper _first = BuildFirst();
        private readonly EnumMapper _base = BuildBase();

        public string SourceDirectory => string.Empty;
        public IDatDatabase Portal => throw new NotSupportedException();
        public IDatDatabase Cell => throw new NotSupportedException();
        public ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; } =
            new(new Dictionary<uint, IDatDatabase>());
        public IDatDatabase HighRes => throw new NotSupportedException();
        public IDatDatabase Language => throw new NotSupportedException();
        public IDatDatabase Local => throw new NotSupportedException();
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
        public T Get<T>(uint fileId) where T : DatIDBObj
        {
            if (typeof(T) != typeof(EnumMapper))
                return default;
            if (fileId == CreatureDisplayNameResolver.MapperDid)
                return (T)(object)_first;
            return fileId == BaseMapperDid ? (T)(object)_base : default;
        }

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value)
            where T : DatIDBObj
        {
            value = Get<T>(fileId);
            return value is not null;
        }

        public void Dispose()
        {
        }

        private static EnumMapper BuildFirst()
        {
            var mapper = new EnumMapper { BaseEnumMap = BaseMapperDid };
            mapper.IdToStringMap.Add(OlthoiSpecies, OlthoiRaw);
            return mapper;
        }

        private static EnumMapper BuildBase()
        {
            var mapper = new EnumMapper { BaseEnumMap = 0u };
            mapper.IdToStringMap.Add(1u, "Olthoi");
            return mapper;
        }
    }
}
