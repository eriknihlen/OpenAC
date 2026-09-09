using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using AcDream.Content;
using AcDream.Content.Vfx;
using AcDream.Core.Vfx;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Lib.IO;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using DatAnimation = DatReaderWriter.DBObjs.Animation;
using DatPhysicsScript = DatReaderWriter.DBObjs.PhysicsScript;

namespace AcDream.Content.Tests.Vfx;

public sealed class RetailDatLoaderTests
{
    private static Task<T[]> InParallel<T>(Func<T> first, Func<T> second) =>
        Task.WhenAll(
            Task.Factory.StartNew(
                first,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default),
            Task.Factory.StartNew(
                second,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default));

    private sealed class RawDatabase : IDatDatabase
    {
        private readonly Dictionary<uint, byte[]> _entries = new();
        private int _activeReads;
        private int _maxConcurrentReads;
        private int _totalReads;
        private Barrier? _concurrencyGate;

        public DatDatabase Db => null!;
        public int Iteration => 0;
        public int ReadDelayMilliseconds { get; set; }
        public int MaxConcurrentReads => Volatile.Read(ref _maxConcurrentReads);
        public int TotalReads => Volatile.Read(ref _totalReads);
        public void Add(uint id, byte[] bytes) => _entries[id] = bytes;

        public void ArmConcurrencyGate(int participantCount) =>
            _concurrencyGate = new Barrier(participantCount);

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj => _entries.Keys;
        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value) where T : IDBObj
        {
            value = default;
            return false;
        }
        public bool TryGetFileBytes(uint fileId, [MaybeNullWhen(false)] out byte[] value)
        {
            Interlocked.Increment(ref _totalReads);
            int active = Interlocked.Increment(ref _activeReads);
            int observed;
            do
            {
                observed = Volatile.Read(ref _maxConcurrentReads);
            }
            while (active > observed
                && Interlocked.CompareExchange(ref _maxConcurrentReads, active, observed) != observed);
            try
            {
                _concurrencyGate?.SignalAndWait();
                if (ReadDelayMilliseconds > 0)
                    Thread.Sleep(ReadDelayMilliseconds);
                return _entries.TryGetValue(fileId, out value);
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }
        public bool TryGetFileBytes(uint fileId, ref byte[] bytes, out int bytesRead)
        {
            if (!_entries.TryGetValue(fileId, out byte[]? value))
            {
                bytesRead = 0;
                return false;
            }
            if (bytes.Length < value.Length)
                bytes = new byte[value.Length];
            value.CopyTo(bytes, 0);
            bytesRead = value.Length;
            return true;
        }
        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj => false;
        public void Dispose() { }
    }

    [Fact]
    public void PhysicsScript_BlockingHookRetainsPayloadAndFollowingCursor()
    {
        DatPhysicsScript script = RetailPhysicsScriptLoader.Parse(
            ProjectileVfxDatFixtures.PhysicsScriptCreateBlockingThenAnimationDone);

        Assert.Equal(0x3300F001u, script.Id);
        Assert.Equal(2, script.ScriptData.Count);
        Assert.Equal(0.0, script.ScriptData[0].StartTime);
        var blocking = Assert.IsType<RetailCreateBlockingParticleHook>(
            script.ScriptData[0].Hook);
        Assert.Equal(0x3200F001u, blocking.EmitterInfoId.DataId);
        Assert.Equal(0xFFFFFFFFu, blocking.PartIndex);
        Assert.Equal(new Vector3(1f, 2f, 3f), blocking.Offset.Origin);
        Assert.Equal(new Quaternion(0.5f, -0.5f, -0.5f, 0.5f),
            blocking.Offset.Orientation);
        Assert.Equal(7u, blocking.EmitterId);
        Assert.Equal(1.25, script.ScriptData[1].StartTime);
        Assert.IsType<AnimationDoneHook>(script.ScriptData[1].Hook);
    }

    [Fact]
    public void Animation_BlockingHookRetainsPayloadAndFollowingCursor()
    {
        DatAnimation animation = RetailAnimationLoader.Parse(
            ProjectileVfxDatFixtures.AnimationCreateBlockingThenAnimationDone);

        Assert.Equal(0x0300F001u, animation.Id);
        AnimationFrame frame = Assert.Single(animation.PartFrames);
        Assert.Equal(2, frame.Hooks.Count);
        var blocking = Assert.IsType<RetailCreateBlockingParticleHook>(frame.Hooks[0]);
        Assert.Equal(0x3200F002u, blocking.EmitterInfoId.DataId);
        Assert.Equal(0u, blocking.PartIndex);
        Assert.Equal(new Vector3(-1f, 4f, 0.25f), blocking.Offset.Origin);
        Assert.Equal(Quaternion.Identity, blocking.Offset.Orientation);
        Assert.Equal(9u, blocking.EmitterId);
        Assert.IsType<AnimationDoneHook>(frame.Hooks[1]);
    }

    [Fact]
    public void OrdinaryPhysicsScript_MatchesPackageDecoder()
    {
        byte[] bytes = ProjectileVfxDatFixtures.OrdinaryPhysicsScript;
        DatPhysicsScript retail = RetailPhysicsScriptLoader.Parse(bytes);
        var package = new DatPhysicsScript();
        var reader = new DatBinReader(bytes);
        Assert.True(package.Unpack(reader));

        Assert.Equal(reader.Length, reader.Offset);
        Assert.Equal(package.Id, retail.Id);
        Assert.Equal(package.ScriptData.Count, retail.ScriptData.Count);
        Assert.Equal(package.ScriptData[0].StartTime, retail.ScriptData[0].StartTime);
        Assert.Equal(package.ScriptData[0].Hook.HookType, retail.ScriptData[0].Hook.HookType);
        Assert.Equal(package.ScriptData[0].Hook.Direction, retail.ScriptData[0].Hook.Direction);
    }

    [Fact]
    public void OrdinaryAnimation_MatchesPackageDecoder()
    {
        byte[] bytes = ProjectileVfxDatFixtures.OrdinaryAnimation;
        DatAnimation retail = RetailAnimationLoader.Parse(bytes);
        var package = new DatAnimation();
        var reader = new DatBinReader(bytes);
        Assert.True(package.Unpack(reader));

        Assert.Equal(reader.Length, reader.Offset);
        Assert.Equal(package.Id, retail.Id);
        Assert.Equal(package.Flags, retail.Flags);
        Assert.Equal(package.NumParts, retail.NumParts);
        Assert.Equal(package.PartFrames.Count, retail.PartFrames.Count);
        Assert.Equal(package.PartFrames[0].Hooks[0].HookType,
            retail.PartFrames[0].Hooks[0].HookType);
        Assert.Equal(package.PartFrames[0].Hooks[0].Direction,
            retail.PartFrames[0].Hooks[0].Direction);
    }

    [Fact]
    public void PhysicsScript_SortsHooksByRetailStartTimeAfterDecode()
    {
        byte[] bytes = ProjectileVfxDatFixtures.PhysicsScriptCreateBlockingThenAnimationDone.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(
            bytes.AsSpan(8), 2.0);

        DatPhysicsScript script = RetailPhysicsScriptLoader.Parse(bytes);

        Assert.Equal(1.25, script.ScriptData[0].StartTime);
        Assert.IsType<AnimationDoneHook>(script.ScriptData[0].Hook);
        Assert.Equal(2.0, script.ScriptData[1].StartTime);
        Assert.IsType<RetailCreateBlockingParticleHook>(script.ScriptData[1].Hook);
    }

    [Fact]
    public void AnimationDidValidation_UsesRetailHighByte()
    {
        Assert.True(RetailAnimationLoader.IsAnimationDid(0x03010000u));
        Assert.True(RetailAnimationLoader.IsAnimationDid(0x03FFFFFFu));
        Assert.False(RetailAnimationLoader.IsAnimationDid(0x04010000u));
    }

    [Fact]
    public void Loaders_AcceptHighIndexesCacheIdentityAndRejectEmbeddedIdMismatch()
    {
        const uint scriptDid = 0x33010000u;
        const uint animationDid = 0x03010000u;
        var portal = new RawDatabase();
        byte[] scriptBytes = ProjectileVfxDatFixtures.OrdinaryPhysicsScript.ToArray();
        byte[] animationBytes = ProjectileVfxDatFixtures.OrdinaryAnimation.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(scriptBytes, scriptDid);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(animationBytes, animationDid);
        portal.Add(scriptDid, scriptBytes);
        portal.Add(animationDid, animationBytes);

        var scripts = new RetailPhysicsScriptLoader(portal);
        var animations = new RetailAnimationLoader(portal);
        DatPhysicsScript script = Assert.IsType<DatPhysicsScript>(scripts.LoadPhysicsScript(scriptDid));
        DatAnimation animation = Assert.IsType<DatAnimation>(animations.LoadAnimation(animationDid));
        Assert.Same(script, scripts.LoadPhysicsScript(scriptDid));
        Assert.Same(animation, animations.LoadAnimation(animationDid));

        const uint wrongScriptKey = 0x33010001u;
        const uint wrongAnimationKey = 0x03010001u;
        portal.Add(wrongScriptKey, scriptBytes);
        portal.Add(wrongAnimationKey, animationBytes);
        Assert.Throws<InvalidDataException>(() => scripts.LoadPhysicsScript(wrongScriptKey));
        Assert.Throws<InvalidDataException>(() => animations.LoadAnimation(wrongAnimationKey));
    }

    [Fact]
    public void AnimationCache_CountPressureEvictsLruWithoutInvalidatingLiveReferences()
    {
        const uint firstDid = 0x03010020u;
        const uint secondDid = 0x03010021u;
        const uint thirdDid = 0x03010022u;
        var portal = new RawDatabase();
        portal.Add(firstDid, AnimationBytes(firstDid));
        portal.Add(secondDid, AnimationBytes(secondDid));
        portal.Add(thirdDid, AnimationBytes(thirdDid));
        var loader = new RetailAnimationLoader(
            portal,
            maximumEstimatedBytes: long.MaxValue,
            maximumEntries: 2);

        DatAnimation first = Assert.IsType<DatAnimation>(
            loader.LoadAnimation(firstDid));
        _ = Assert.IsType<DatAnimation>(loader.LoadAnimation(secondDid));
        _ = Assert.IsType<DatAnimation>(loader.LoadAnimation(thirdDid));

        AnimationCacheDiagnostics pressured = loader.Diagnostics;
        Assert.Equal(2, pressured.Count);
        Assert.Equal(1, pressured.Stats.Evictions);
        Assert.Equal(firstDid, first.Id);

        DatAnimation reloaded = Assert.IsType<DatAnimation>(
            loader.LoadAnimation(firstDid));
        Assert.NotSame(first, reloaded);
        Assert.Equal(4, portal.TotalReads);
        Assert.Equal(2, loader.Diagnostics.Stats.Evictions);
    }

    [Fact]
    public void AnimationCache_OversizeEntryIsServedButNeverRetained()
    {
        const uint animationDid = 0x03010023u;
        var portal = new RawDatabase();
        portal.Add(animationDid, AnimationBytes(animationDid));
        var loader = new RetailAnimationLoader(
            portal,
            maximumEstimatedBytes: 1,
            maximumEntries: 2);

        Assert.NotNull(loader.LoadAnimation(animationDid));
        Assert.NotNull(loader.LoadAnimation(animationDid));

        Assert.Equal(0, loader.Diagnostics.Count);
        Assert.Equal(0, loader.Diagnostics.EstimatedBytes);
        Assert.Equal(2, portal.TotalReads);
    }

    [Fact]
    public async Task AnimationCache_CoalescesSameDidAndAllowsUnrelatedReadsInParallel()
    {
        const uint firstDid = 0x03010024u;
        const uint secondDid = 0x03010025u;
        var portal = new RawDatabase();
        portal.Add(firstDid, AnimationBytes(firstDid));
        portal.Add(secondDid, AnimationBytes(secondDid));
        var loader = new RetailAnimationLoader(portal);

        DatAnimation?[] firstPair = await InParallel(
            () => loader.LoadAnimation(firstDid),
            () => loader.LoadAnimation(firstDid));
        Assert.Same(firstPair[0], firstPair[1]);
        Assert.Equal(1, portal.TotalReads);

        portal.ArmConcurrencyGate(2);
        await Task.WhenAll(
            Task.Run(() => loader.LoadAnimation(secondDid)),
            Task.Run(() => loader.LoadAnimation(0x03010026u)));

        Assert.Equal(2, portal.MaxConcurrentReads);
        Assert.Equal(3, portal.TotalReads);
    }

    [Fact]
    public async Task PhysicsScriptLoader_AllowsConcurrentFirstReads()
    {
        const uint firstDid = 0x33010010u;
        const uint secondDid = 0x33010011u;
        var portal = new RawDatabase();
        byte[] first = ProjectileVfxDatFixtures.OrdinaryPhysicsScript.ToArray();
        byte[] second = ProjectileVfxDatFixtures.OrdinaryPhysicsScript.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(first, firstDid);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(second, secondDid);
        portal.Add(firstDid, first);
        portal.Add(secondDid, second);
        portal.ArmConcurrencyGate(2);
        var loader = new RetailPhysicsScriptLoader(portal);

        await InParallel(
            () => loader.LoadPhysicsScript(firstDid),
            () => loader.LoadPhysicsScript(secondDid));

        Assert.Equal(2, portal.MaxConcurrentReads);
        Assert.Equal(2, portal.TotalReads);
    }

    private static byte[] AnimationBytes(uint id)
    {
        byte[] bytes =
            ProjectileVfxDatFixtures.OrdinaryAnimation.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            bytes,
            id);
        return bytes;
    }

    [Fact]
    public async Task PhysicsScriptLoader_CoalescesConcurrentReadsForTheSameDid()
    {
        const uint scriptDid = 0x33010012u;
        var portal = new RawDatabase { ReadDelayMilliseconds = 40 };
        byte[] bytes = ProjectileVfxDatFixtures.OrdinaryPhysicsScript.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, scriptDid);
        portal.Add(scriptDid, bytes);
        var loader = new RetailPhysicsScriptLoader(portal);

        DatPhysicsScript?[] loaded = await InParallel(
            () => loader.LoadPhysicsScript(scriptDid),
            () => loader.LoadPhysicsScript(scriptDid));

        Assert.All(loaded, Assert.NotNull);
        Assert.Same(loaded[0], loaded[1]);
        Assert.Equal(1, portal.TotalReads);
    }

    [Fact]
    public void Parsers_RejectTrailingOrTruncatedEntries()
    {
        byte[] script = ProjectileVfxDatFixtures.PhysicsScriptCreateBlockingThenAnimationDone;
        byte[] animation = ProjectileVfxDatFixtures.AnimationCreateBlockingThenAnimationDone;

        Assert.ThrowsAny<Exception>(() =>
            RetailPhysicsScriptLoader.Parse(script.AsMemory(0, script.Length - 1)));
        Assert.ThrowsAny<Exception>(() =>
            RetailAnimationLoader.Parse(animation.AsMemory(0, animation.Length - 1)));
        Assert.Throws<InvalidDataException>(() =>
            RetailPhysicsScriptLoader.Parse(script.Concat(new byte[] { 0 }).ToArray()));
        Assert.Throws<InvalidDataException>(() =>
            RetailAnimationLoader.Parse(animation.Concat(new byte[] { 0 }).ToArray()));
    }

    [Fact]
    public void PhysicsScriptParserRejectsNonFiniteHookTime()
    {
        byte[] script = ProjectileVfxDatFixtures.OrdinaryPhysicsScript.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
            script.AsSpan(8, 8),
            BitConverter.DoubleToInt64Bits(double.NaN));

        Assert.Throws<InvalidDataException>(() => RetailPhysicsScriptLoader.Parse(script));
    }

    [Fact]
    public void Parsers_RejectUnsupportedHookTypesWithoutReturningPartialObjects()
    {
        byte[] script = ProjectileVfxDatFixtures.OrdinaryPhysicsScript.ToArray();
        byte[] animation = ProjectileVfxDatFixtures.OrdinaryAnimation.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            script.AsSpan(16), 0xDEADBEEFu);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            animation.AsSpan(20), 0xDEADBEEFu);

        Assert.ThrowsAny<Exception>(() => RetailPhysicsScriptLoader.Parse(script));
        Assert.ThrowsAny<Exception>(() => RetailAnimationLoader.Parse(animation));
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void InstalledBlockingScripts_ParseThroughRetailShape_WhenDatsAvailable()
    {
        string? datDir = ResolveDatDirectory();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var boundedDats = new DatCollectionAdapter(dats);
        var loader = new RetailPhysicsScriptLoader(boundedDats);

        foreach (uint id in new[] { 0x33000AEAu, 0x33000AF8u })
        {
            DatPhysicsScript script = Assert.IsType<DatPhysicsScript>(
                loader.LoadPhysicsScript(id));
            Assert.Contains(script.ScriptData,
                item => item.Hook is RetailCreateBlockingParticleHook);
        }
    }

    private static string? ResolveDatDirectory()
    {
        string? configured = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(configured)
            && File.Exists(Path.Combine(configured, "client_portal.dat")))
        {
            return configured;
        }

        const string installed = @"C:\Turbine\Asheron's Call";
        return File.Exists(Path.Combine(installed, "client_portal.dat"))
            ? installed
            : null;
    }
}
