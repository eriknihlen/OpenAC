using System.Numerics;
using AcDream.App.Audio;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Meshing;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Vfx;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using DatPhysicsScript = DatReaderWriter.DBObjs.PhysicsScript;

namespace AcDream.App.Tests.Rendering.Vfx;

public sealed class EntityEffectControllerTests
{
    private const uint Guid = 0x70000001u;
    private const uint DirectDid = 0x33000011u;
    private const uint TypedDid = 0x33000022u;
    private const uint TableDid = 0x34000033u;
    private const uint RawType = 0x16u;

    private sealed class NoopResources : ILiveEntityResourceLifecycle
    {
        public void Register(WorldEntity entity) { }
        public void Unregister(WorldEntity entity) { }
    }

    private sealed class RecordingSink : IAnimationHookSink
    {
        public List<(uint EntityId, Vector3 Position, AnimationHook Hook)> Calls { get; } = new();

        public void OnHook(uint entityId, Vector3 worldPosition, AnimationHook hook) =>
            Calls.Add((entityId, worldPosition, hook));
    }

    private sealed class Fixture
    {
        private readonly Dictionary<uint, DatPhysicsScript> _scripts = new();
        private readonly Dictionary<uint, PhysicsScriptTable> _tables = new();
        private EntityEffectController? _controller;

        public Fixture(
            Func<uint, uint, uint?>? childAtPart = null,
            Func<uint, uint?>? parentOfAttachedChild = null,
            Func<uint, PhysicsScriptTable?>? tableLoader = null,
            Action<uint>? ownerUnregistered = null,
            Action<uint, uint?>? ownerSoundTableChanged = null,
            Action<uint, Vector3, uint, float>? playServerSound = null)
        {
            Spatial.AddLandblock(new LoadedLandblock(
                0x0101FFFFu,
                new LandBlock(),
                Array.Empty<WorldEntity>()));
            Runtime = LiveEntityRuntimeFixture.Create(
                Spatial,
                new NoopResources(),
                record => _controller?.OnLiveEntityUnregistered(record));
            Runner = new PhysicsScriptRunner(
                id => _scripts.TryGetValue(id, out DatPhysicsScript? script) ? script : null,
                Router,
                randomUnit: () => 0.5,
                canAdvanceOwner: ownerId => _controller?.CanAdvanceOwner(ownerId) ?? true);
            Controller = new EntityEffectController(
                Runtime,
                Runner,
                new PhysicsScriptTableResolver(
                    tableLoader
                    ?? (id => _tables.TryGetValue(id, out PhysicsScriptTable? table) ? table : null)),
                Poses,
                childAtPart,
                parentOfAttachedChild,
                ownerUnregistered,
                ownerSoundTableChanged,
                playServerSound);
            _controller = Controller;
            Router.Register(Sink);
            Router.Register(Controller);
            AddScript(DirectDid, 1u);
            AddScript(TypedDid, 2u);
            _tables.Add(TableDid, BuildTable(TableDid, RawType, (1f, TypedDid)));
        }

        public GpuWorldState Spatial { get; } = new();
        public LiveEntityRuntime Runtime { get; }
        public RecordingSink Sink { get; } = new();
        public AnimationHookRouter Router { get; } = new();
        public PhysicsScriptRunner Runner { get; }
        public EntityEffectController Controller { get; }
        public EntityEffectPoseRegistry Poses { get; } = new();

        public void AddScript(uint did, uint emitterId, double startTime = 0.0)
        {
            var script = new DatPhysicsScript();
            script.ScriptData.Add(new PhysicsScriptData
            {
                StartTime = startTime,
                Hook = new CreateParticleHook { EmitterInfoId = emitterId },
            });
            _scripts[did] = script;
        }

        public void AddScript(uint did, AnimationHook hook, double startTime = 0.0)
        {
            var script = new DatPhysicsScript();
            script.ScriptData.Add(new PhysicsScriptData
            {
                StartTime = startTime,
                Hook = hook,
            });
            _scripts[did] = script;
        }

        public WorldEntity ReadyLive(
            uint guid = Guid,
            ushort generation = 1,
            EntityEffectProfile? profile = null)
        {
            WorldSession.EntitySpawn spawn = Spawn(guid, generation);
            LiveEntityRecord record = Runtime.RegisterAndMaterializeProjection(
                spawn,
                id => Entity(id, guid),
                initializeProjection: exact =>
                    exact.EffectProfile = profile ?? LiveProfile());
            WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);
            Assert.True(Controller.OnLiveEntityReady(guid));
            return entity;
        }

        public LiveEntityRecord AwaitInitialPresentation(
            uint guid = Guid,
            ushort generation = 1,
            EntityEffectProfile? profile = null)
        {
            WorldSession.EntitySpawn spawn = Spawn(guid, generation);
            RuntimeEntityRecord canonical = Assert.IsType<RuntimeEntityRecord>(
                Runtime.RegisterLiveEntity(spawn).Canonical);
            WorldEntity entity = Assert.IsType<WorldEntity>(
                Runtime.MaterializeLiveEntity(
                    canonical,
                    spawn.Position!.Value.LandblockId,
                    id => Entity(id, guid),
                    LiveEntityProjectionKind.World,
                    initializeProjection: exact =>
                        exact.EffectProfile = profile ?? LiveProfile(),
                    out LiveEntityRecord? record,
                    LiveEntityMaterializationResidence.AwaitRuntimePlacement));
            Assert.Same(entity, record!.WorldEntity);
            Assert.False(record.IsSpatiallyProjected);
            Assert.False(record.IsSpatiallyVisible);
            Assert.True(Controller.PrepareLiveEntityOwner(guid));
            return record;
        }

        public static EntityEffectProfile LiveProfile(
            uint tableDid = TableDid,
            uint rawDefaultType = RawType,
            float defaultIntensity = 0.5f,
            uint? soundTableDid = null) =>
            EntityEffectProfile.CreateLive(
                new Setup(),
                default(PhysicsSpawnData) with
                {
                    PhysicsScriptTableId = tableDid,
                    SoundTableId = soundTableDid,
                    DefaultScriptType = rawDefaultType,
                    DefaultScriptIntensity = defaultIntensity,
                });
    }


    [Fact]
    public void ServerSound_ForReadyOwner_PlaysImmediatelyWithLocalIdAndWireVolume()
    {
        var played = new List<(uint OwnerId, uint SoundType, float Volume)>();
        var fixture = new Fixture(
            playServerSound: (ownerId, _, soundType, volume) =>
                played.Add((ownerId, soundType, volume)));
        WorldEntity entity = fixture.ReadyLive();

        fixture.Controller.HandleSound(new SoundEvent(Guid, 0x30u, 0.75f));

        var call = Assert.Single(played);
        // Downstream sinks must see the LOCAL id, never the server guid.
        Assert.Equal(entity.Id, call.OwnerId);
        Assert.NotEqual(Guid, call.OwnerId);
        Assert.Equal(0x30u, call.SoundType);
        Assert.Equal(0.75f, call.Volume);
    }

    [Fact]
    public void ServerSound_ForUnknownGuid_IsQueuedAndReplayedOnArrival()
    {
        var played = new List<uint>();
        var fixture = new Fixture(
            playServerSound: (_, _, soundType, _) => played.Add(soundType));

        fixture.Controller.HandleSound(new SoundEvent(Guid, 0x09u, 1f));

        Assert.Equal(1, fixture.Controller.PendingPacketCount);
        Assert.Empty(played);

        fixture.ReadyLive();

        Assert.Equal(0, fixture.Controller.PendingPacketCount);
        Assert.Equal([0x09u], played);
    }

    [Fact]
    public void ServerSound_SharesOneQueueWithScriptPackets_AndAllDrainTogether()
    {
        var sounds = new List<uint>();
        var fixture = new Fixture(
            playServerSound: (_, _, soundType, _) => sounds.Add(soundType));

        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));
        fixture.Controller.HandleSound(new SoundEvent(Guid, 0x0Au, 1f));
        fixture.Controller.HandleTyped(new PlayPhysicsScriptType(Guid, RawType, 0.5f));

        Assert.Equal(3, fixture.Controller.PendingPacketCount);
        Assert.Empty(sounds);

        fixture.ReadyLive();

        Assert.Equal(0, fixture.Controller.PendingPacketCount);
        Assert.Equal([0x0Au], sounds);
        Assert.Equal(2, fixture.Runner.ActiveScriptCount);
    }

    [Fact]
    public void ServerSound_WithZeroGuid_IsIgnored()
    {
        var played = new List<uint>();
        var fixture = new Fixture(
            playServerSound: (_, _, soundType, _) => played.Add(soundType));

        fixture.Controller.HandleSound(new SoundEvent(0u, 0x30u, 1f));

        Assert.Empty(played);
        Assert.Equal(0, fixture.Controller.PendingPacketCount);
    }

    [Fact]
    public void ServerSound_PlaysAtTheOwnersCurrentRootPose_NotTheEntityOrigin()
    {
        var positions = new List<Vector3>();
        var fixture = new Fixture(
            playServerSound: (_, position, _, _) => positions.Add(position));
        fixture.ReadyLive();

        fixture.Controller.HandleSound(new SoundEvent(Guid, 0x30u, 1f));

        // The pose registry is the anchor authority for effects; a sound must
        // use the same one so an animating owner's cue is not left behind.
        Assert.Single(positions);
    }

    [Fact]
    public void PreMaterializationDirectAndTypedPacketsReplayOnceInMixedOrder()
    {
        var fixture = new Fixture();
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));
        fixture.Controller.HandleTyped(new PlayPhysicsScriptType(Guid, RawType, 0.5f));

        Assert.Equal(2, fixture.Controller.PendingPacketCount);
        WorldEntity entity = fixture.ReadyLive();
        Assert.Equal(0, fixture.Controller.PendingPacketCount);
        Assert.Equal(2, fixture.Runner.ActiveScriptCount);

        Assert.True(fixture.Controller.OnLiveEntityReady(Guid));
        fixture.Runner.Tick(0.0);

        Assert.Equal([1u, 2u], EmitterIds(fixture.Sink));
        Assert.All(fixture.Sink.Calls, call => Assert.Equal(entity.Id, call.EntityId));
        Assert.DoesNotContain(fixture.Sink.Calls, call => call.EntityId == Guid);
    }

    [Fact]
    public void PreparedOwner_DoesNotReplayPendingPacketsUntilConstructionBarrierOpens()
    {
        var fixture = new Fixture();
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));
        WorldSession.EntitySpawn spawn = Spawn(Guid, 1);
        fixture.Runtime.RegisterAndMaterializeProjection(
            spawn,
            id => Entity(id, Guid),
            initializeProjection: record =>
                record.EffectProfile = Fixture.LiveProfile());

        Assert.True(fixture.Controller.PrepareLiveEntityOwner(Guid));
        Assert.Equal(1, fixture.Controller.PendingPacketCount);
        Assert.Equal(0, fixture.Runner.ActiveScriptCount);

        Assert.True(fixture.Controller.ReplayPendingForLiveEntity(Guid));
        Assert.Equal(0, fixture.Controller.PendingPacketCount);
        Assert.Equal(1, fixture.Runner.ActiveScriptCount);
    }

    [Fact]
    public void RuntimeInitialPlacementWindow_DefersPacketsUntilPresentationBinds()
    {
        var fixture = new Fixture();
        LiveEntityRecord record = fixture.AwaitInitialPresentation();

        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));
        fixture.Controller.HandleTyped(
            new PlayPhysicsScriptType(Guid, RawType, 0.5f));

        Assert.Equal(2, fixture.Controller.PendingPacketCount);
        Assert.Equal(0, fixture.Runner.ActiveScriptCount);

        record.FullCellId = 0x01010001u;
        record.IsSpatiallyProjected = true;
        record.IsSpatiallyVisible = true;
        Assert.True(fixture.Controller.OnPresentationBound(record));
        Assert.Equal(0, fixture.Controller.PendingPacketCount);
        Assert.Equal(2, fixture.Runner.ActiveScriptCount);

        fixture.Runner.Tick(0.0);
        Assert.Equal([1u, 2u], EmitterIds(fixture.Sink));
    }

    [Fact]
    public void ReadyOwnerReceivesDirectAndTypedPacketsImmediately()
    {
        var fixture = new Fixture();
        fixture.ReadyLive();

        fixture.Controller.HandleTyped(new PlayPhysicsScriptType(Guid, RawType, 0.5f));
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));
        fixture.Runner.Tick(0.0);

        Assert.Equal([2u, 1u], EmitterIds(fixture.Sink));
        Assert.Equal(0, fixture.Controller.PendingPacketCount);
    }

    [Fact]
    public void CelllessOwnerRejectsNewPacketAndPausesExistingQueueUntilReentry()
    {
        var fixture = new Fixture();
        fixture.AddScript(DirectDid, 1u, startTime: 1.0);
        fixture.ReadyLive();
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));
        fixture.Runner.Tick(0.5);

        Assert.True(fixture.Runtime.WithdrawLiveEntityProjection(Guid));
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));
        fixture.Runner.Tick(2.0);
        Assert.Empty(fixture.Sink.Calls);
        Assert.Equal(1, fixture.Runner.ActiveScriptCount);

        Assert.True(fixture.Runtime.RebucketLiveEntity(Guid, 0x01010001u));
        fixture.Runner.Tick(2.0);
        Assert.Equal([1u], EmitterIds(fixture.Sink));
    }

    [Fact]
    public void CelllessOwner_HiddenTransitionQueuesTypedScriptUntilReentry()
    {
        var fixture = new Fixture();
        WorldEntity entity = fixture.ReadyLive();
        Assert.True(fixture.Runtime.WithdrawLiveEntityProjection(Guid));

        Assert.True(fixture.Controller.PlayTypedFromHiddenTransition(
            entity.Id,
            RawType,
            0.5f));
        Assert.Equal(1, fixture.Runner.ActiveScriptCount);

        fixture.Runner.Tick(1.0);
        Assert.Empty(fixture.Sink.Calls);

        Assert.True(fixture.Runtime.RebucketLiveEntity(Guid, 0x01010001u));
        fixture.Runner.Tick(1.0);

        Assert.Equal([2u], EmitterIds(fixture.Sink));
    }

    [Fact]
    public void FrozenOwnerPausesQueueUntilFreshStateUnfreezesIt()
    {
        var fixture = new Fixture();
        fixture.AddScript(DirectDid, 1u, startTime: 1.0);
        fixture.ReadyLive();
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));
        Assert.True(fixture.Runtime.TryApplyState(
            new SetState.Parsed(
                Guid,
                (uint)(PhysicsStateFlags.ReportCollisions | PhysicsStateFlags.Frozen),
                1,
                2),
            out _));
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));

        fixture.Runner.Tick(2.0);
        Assert.Empty(fixture.Sink.Calls);
        Assert.Equal(2, fixture.Runner.ActiveScriptCount);
        Assert.True(fixture.Runtime.TryApplyState(
            new SetState.Parsed(Guid, (uint)PhysicsStateFlags.ReportCollisions, 1, 3),
            out _));
        fixture.Runner.Tick(2.0);

        Assert.Equal([1u, 1u], EmitterIds(fixture.Sink));
    }

    [Fact]
    public void PreCreatePacketWaitsForProjectionButPostCreateCelllessPacketIsDropped()
    {
        var fixture = new Fixture();
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));
        WorldSession.EntitySpawn spawn = Spawn(Guid, 1);
        LiveEntityRecord record = fixture.Runtime.RegisterAndMaterializeProjection(
            spawn,
            id => Entity(id, Guid),
            initializeProjection: exact =>
                exact.EffectProfile = Fixture.LiveProfile());
        WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);
        fixture.Runtime.RebucketLiveEntity(Guid, 0x02020001u);

        Assert.True(fixture.Controller.OnLiveEntityReady(Guid));
        Assert.Equal(1, fixture.Controller.PendingPacketCount);
        fixture.Controller.HandleTyped(new PlayPhysicsScriptType(Guid, RawType, 0.5f));
        Assert.Equal(1, fixture.Controller.PendingPacketCount);
        fixture.Spatial.AddLandblock(new LoadedLandblock(
            0x0202FFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        fixture.Controller.RefreshLiveOwnerPoses();
        fixture.Runner.Tick(0.0);

        Assert.Equal(0, fixture.Controller.PendingPacketCount);
        Assert.Equal([1u], EmitterIds(fixture.Sink));
        Assert.All(fixture.Sink.Calls, call => Assert.Equal(entity.Id, call.EntityId));
    }

    [Fact]
    public void LoadedToPendingMovePausesActiveQueueUntilLandblockLoads()
    {
        var fixture = new Fixture();
        fixture.AddScript(DirectDid, 1u, startTime: 1.0);
        fixture.ReadyLive();
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));
        fixture.Runner.Tick(0.5);

        Assert.True(fixture.Runtime.RebucketLiveEntity(Guid, 0x02020001u));
        fixture.Runner.Tick(2.0);
        Assert.Empty(fixture.Sink.Calls);
        Assert.Equal(1, fixture.Runner.ActiveScriptCount);

        fixture.Spatial.AddLandblock(new LoadedLandblock(
            0x0202FFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        fixture.Runner.Tick(2.0);

        Assert.Equal([1u], EmitterIds(fixture.Sink));
    }

    [Fact]
    public void PendingInvalidTypedPacketDoesNotCorruptFollowingDirectPacket()
    {
        var fixture = new Fixture();
        fixture.Controller.HandleTyped(new PlayPhysicsScriptType(Guid, RawType, float.NaN));
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));

        fixture.ReadyLive();
        fixture.Runner.Tick(0.0);

        Assert.Equal([1u], EmitterIds(fixture.Sink));
        Assert.Equal(0, fixture.Controller.PendingPacketCount);
    }

    [Fact]
    public void PacketNeverUsesServerGuidOrFallbackAnchorBeforeOwnerIsReady()
    {
        var fixture = new Fixture();
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));

        fixture.Runner.Tick(10.0);

        Assert.Empty(fixture.Sink.Calls);
        Assert.Equal(0, fixture.Runner.ActiveScriptCount);
        Assert.Equal(1, fixture.Controller.PendingPacketCount);
    }

    [Fact]
    public void AcceptedDeleteClearsPendingGenerationBeforeGuidReuse()
    {
        var fixture = new Fixture();
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));
        WorldSession.EntitySpawn first = Spawn(Guid, generation: 1);
        fixture.Runtime.RegisterLiveEntity(first);

        fixture.Controller.ForgetUnknownOwner(Guid);
        Assert.True(fixture.Runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(Guid, 1),
            isLocalPlayer: false));
        Assert.Equal(0, fixture.Controller.PendingPacketCount);

        fixture.ReadyLive(Guid, generation: 2);
        fixture.Runner.Tick(0.0);
        Assert.Empty(fixture.Sink.Calls);
    }

    [Fact]
    public void StaleDeleteCannotClearCurrentGenerationQueue()
    {
        var fixture = new Fixture();
        WorldSession.EntitySpawn current = Spawn(Guid, generation: 2);
        fixture.Runtime.RegisterLiveEntity(current);
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));

        Assert.False(fixture.Runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(Guid, 1),
            isLocalPlayer: false));
        Assert.Equal(1, fixture.Controller.PendingPacketCount);

        fixture.Runtime.RegisterAndMaterializeProjection(
            current,
            id => Entity(id, Guid),
            initializeProjection: record =>
                record.EffectProfile = Fixture.LiveProfile());
        Assert.True(fixture.Controller.OnLiveEntityReady(Guid));
        fixture.Runner.Tick(0.0);
        Assert.Equal([1u], EmitterIds(fixture.Sink));
    }

    [Fact]
    public void UnknownDeleteClearsQueueThatNeverReceivedCreateObject()
    {
        var fixture = new Fixture();
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));

        fixture.Controller.ForgetUnknownOwner(Guid);

        Assert.Equal(0, fixture.Controller.PendingPacketCount);
    }

    [Fact]
    public void LogicalTeardownStopsQueuedScriptsAndClearsReadyIdentity()
    {
        var fixture = new Fixture();
        fixture.AddScript(DirectDid, 1u, startTime: 60.0);
        fixture.ReadyLive();
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));
        Assert.Equal(1, fixture.Runner.ActiveScriptCount);

        Assert.True(fixture.Runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(Guid, 1),
            isLocalPlayer: false));

        Assert.Equal(0, fixture.Runner.ActiveScriptCount);
        Assert.Equal(0, fixture.Controller.ReadyOwnerCount);
        Assert.Equal(0, fixture.Controller.PendingPacketCount);
    }

    [Fact]
    public void LogicalTeardownReleasesOwnerScopedResourcesBeforeGuidReuse()
    {
        var removedOwners = new List<uint>();
        var soundTables = new DictionaryEntitySoundTable();
        var fixture = new Fixture(
            ownerUnregistered: ownerId =>
            {
                removedOwners.Add(ownerId);
                soundTables.Remove(ownerId);
            },
            ownerSoundTableChanged: (ownerId, soundTableDid) =>
            {
                soundTables.Remove(ownerId);
                if (soundTableDid is { } did)
                    soundTables.Set(ownerId, did);
            });
        uint firstLocalId = fixture.ReadyLive(
            Guid,
            generation: 1,
            Fixture.LiveProfile(soundTableDid: 0x200000EEu)).Id;
        Assert.Equal(0x200000EEu, soundTables.GetSoundTableId(firstLocalId));

        Assert.True(fixture.Runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(Guid, 1),
            isLocalPlayer: false));
        uint replacementLocalId = fixture.ReadyLive(Guid, generation: 2).Id;

        Assert.Equal([firstLocalId], removedOwners);
        Assert.Equal(0u, soundTables.GetSoundTableId(firstLocalId));
        Assert.Equal(0u, soundTables.GetSoundTableId(replacementLocalId));
        Assert.NotEqual(firstLocalId, replacementLocalId);
    }

    [Fact]
    public void DelayedOldEffectTeardown_DoesNotClearReadyReplacementGeneration()
    {
        var fixture = new Fixture();
        WorldEntity old = fixture.ReadyLive(Guid, generation: 1);
        Assert.True(fixture.Runtime.TryGetRecord(Guid, out LiveEntityRecord oldRecord));

        WorldEntity replacement = fixture.ReadyLive(Guid, generation: 2);
        fixture.Controller.OnLiveEntityUnregistered(oldRecord);

        Assert.NotEqual(old.Id, replacement.Id);
        Assert.Equal(1, fixture.Controller.ReadyOwnerCount);
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));
        fixture.Runner.Tick(0.0);
        Assert.Equal(replacement.Id, Assert.Single(fixture.Sink.Calls).EntityId);
    }

    [Fact]
    public void ReadyStaticRestOwnerPublishesNetworkSoundOverrideWithoutSetupFallback()
    {
        var changed = new List<(uint OwnerId, uint? SoundTableDid)>();
        var fixture = new Fixture(
            ownerSoundTableChanged: (ownerId, soundTableDid) =>
                changed.Add((ownerId, soundTableDid)));
        var setup = new Setup { DefaultSoundTable = 0x200000DDu };
        EntityEffectProfile networkOverride = EntityEffectProfile.CreateLive(
            setup,
            default(PhysicsSpawnData) with { SoundTableId = 0x200000EEu });

        WorldEntity first = fixture.ReadyLive(profile: networkOverride);

        Assert.False(fixture.Runtime.TryGetAnimationRuntime(first.Id, out _));
        Assert.Equal([(first.Id, (uint?)0x200000EEu)], changed);

        changed.Clear();
        EntityEffectProfile networkAbsent = EntityEffectProfile.CreateLive(
            setup,
            default);
        WorldEntity second = fixture.ReadyLive(
            guid: 0x70000002u,
            profile: networkAbsent);

        Assert.Equal([(second.Id, (uint?)null)], changed);
    }

    [Fact]
    public void SameGenerationDescriptionRefreshReplacesAndClearsLiveSoundTable()
    {
        var changed = new List<(uint OwnerId, uint? SoundTableDid)>();
        var fixture = new Fixture(
            ownerSoundTableChanged: (ownerId, soundTableDid) =>
                changed.Add((ownerId, soundTableDid)));
        EntityEffectProfile profile = Fixture.LiveProfile(
            soundTableDid: 0x200000EEu);
        WorldEntity entity = fixture.ReadyLive(profile: profile);
        changed.Clear();

        profile.ApplyNetworkDescription(default(PhysicsSpawnData) with
        {
            PhysicsScriptTableId = TableDid,
            SoundTableId = 0x200000EFu,
        });
        Assert.True(fixture.Controller.OnLiveEntityDescriptionChanged(Guid));
        profile.ApplyNetworkDescription(default(PhysicsSpawnData) with
        {
            PhysicsScriptTableId = TableDid,
            SoundTableId = 0u,
        });
        Assert.True(fixture.Controller.OnLiveEntityDescriptionChanged(Guid));

        Assert.Equal(
            [
                (entity.Id, (uint?)0x200000EFu),
                (entity.Id, (uint?)null),
            ],
            changed);
    }

    [Fact]
    public void CallPesAndDefaultHooksEnqueueThroughOwningProfiles()
    {
        const uint nestedDid = 0x33000044u;
        var fixture = new Fixture();
        fixture.AddScript(nestedDid, 4u);
        WorldEntity entity = fixture.ReadyLive();

        fixture.Controller.OnHook(
            entity.Id,
            entity.Position,
            new DefaultScriptHook());
        fixture.Controller.OnHook(
            entity.Id,
            entity.Position,
            new CallPESHook { PES = nestedDid, Pause = 0f });
        fixture.Runner.Tick(0.0);

        Assert.Equal([2u, 4u], EmitterIds(fixture.Sink));
    }

    [Fact]
    public void RunnerRouterControllerPathExecutesNestedCallPes()
    {
        const uint outerDid = 0x33000043u;
        const uint nestedDid = 0x33000044u;
        var fixture = new Fixture();
        fixture.AddScript(outerDid, new CallPESHook { PES = nestedDid, Pause = 0f });
        fixture.AddScript(nestedDid, 4u);
        fixture.ReadyLive();

        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, outerDid));
        fixture.Runner.Tick(0.0);

        Assert.Collection(
            fixture.Sink.Calls,
            call => Assert.IsType<CallPESHook>(call.Hook),
            call => Assert.Equal(4u, Assert.IsType<CreateParticleHook>(call.Hook).EmitterInfoId.DataId));
    }

    [Fact]
    public void DefaultScriptPartResolvesAttachedChildAndUsesChildProfile()
    {
        uint parentLocalId = 0;
        uint childLocalId = 0;
        var fixture = new Fixture((parent, part) =>
            parent == parentLocalId && part == 7u ? childLocalId : null);
        parentLocalId = fixture.ReadyLive(Guid, generation: 1).Id;
        childLocalId = fixture.ReadyLive(0x70000002u, generation: 1).Id;

        fixture.Controller.OnHook(
            parentLocalId,
            Vector3.Zero,
            new DefaultScriptPartHook { PartIndex = 7u });
        fixture.Runner.Tick(0.0);

        var call = Assert.Single(fixture.Sink.Calls);
        Assert.Equal(childLocalId, call.EntityId);
        Assert.Equal(2u, ((CreateParticleHook)call.Hook).EmitterInfoId.DataId);
    }

    [Fact]
    public void AttachedChildAdvancesThroughEligibleParentWhileCellless()
    {
        uint parentLocalId = 0;
        uint childLocalId = 0;
        var fixture = new Fixture(
            parentOfAttachedChild: child => child == childLocalId ? parentLocalId : null);
        parentLocalId = fixture.ReadyLive(Guid).Id;
        childLocalId = fixture.ReadyLive(0x70000002u).Id;
        fixture.Runtime.WithdrawLiveEntityProjection(0x70000002u);

        Assert.True(fixture.Controller.PlayDefault(childLocalId));
        fixture.Runner.Tick(0.0);
        Assert.Equal(childLocalId, Assert.Single(fixture.Sink.Calls).EntityId);

        fixture.Runtime.WithdrawLiveEntityProjection(Guid);
        Assert.False(fixture.Controller.PlayDefault(childLocalId));
    }

    [Fact]
    public void RefreshLiveOwnerPoses_PreservesComposedAttachedRootAndUsesItAsScriptAnchor()
    {
        const uint childGuid = 0x70000002u;
        var fixture = new Fixture();
        WorldEntity child = fixture.ReadyLive(childGuid);
        WorldSession.EntitySpawn spawn = Spawn(childGuid, generation: 1);
        fixture.Runtime.MaterializeLiveEntity(
            childGuid,
            spawn.Position!.Value.LandblockId,
            _ => throw new InvalidOperationException("existing projection must be reused"),
            LiveEntityProjectionKind.Attached);
        Matrix4x4 attachedRoot = Matrix4x4.CreateTranslation(7, 8, 9);
        fixture.Poses.Publish(
            child.Id,
            attachedRoot,
            Array.Empty<Matrix4x4>(),
            spawn.Position.Value.LandblockId);

        fixture.Controller.RefreshLiveOwnerPoses();
        fixture.Controller.HandleDirect(new PlayPhysicsScript(childGuid, DirectDid));
        fixture.Runner.Tick(0.0);

        Assert.True(fixture.Poses.TryGetRootPose(child.Id, out Matrix4x4 retained));
        Assert.Equal(new Vector3(7, 8, 9), retained.Translation);
        Assert.Equal(new Vector3(7, 8, 9), Assert.Single(fixture.Sink.Calls).Position);
    }

    [Fact]
    public void MissingDirectScriptReportsOwnerAndDid()
    {
        const uint missingDid = 0x3300DEADu;
        var fixture = new Fixture();
        WorldEntity entity = fixture.ReadyLive();
        var diagnostics = new List<string>();
        fixture.Controller.DiagnosticSink = diagnostics.Add;

        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, missingDid));

        string message = Assert.Single(diagnostics);
        Assert.Contains($"0x{entity.Id:X8}", message, StringComparison.Ordinal);
        Assert.Contains($"0x{missingDid:X8}", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedTableLoadFailureReportsOwnerTableAndException()
    {
        var fixture = new Fixture(
            tableLoader: _ => throw new InvalidDataException("fixture corrupt table"));
        WorldEntity entity = fixture.ReadyLive();
        var diagnostics = new List<string>();
        fixture.Controller.DiagnosticSink = diagnostics.Add;

        fixture.Controller.HandleTyped(
            new PlayPhysicsScriptType(Guid, RawType, 0.5f));

        string message = Assert.Single(diagnostics);
        Assert.Contains($"0x{entity.Id:X8}", message, StringComparison.Ordinal);
        Assert.Contains($"0x{TableDid:X8}", message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidDataException), message, StringComparison.Ordinal);
        Assert.Contains("fixture corrupt table", message, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticOwnerRetainsSetupTableAndIsRemovedSymmetrically()
    {
        var changed = new List<(uint OwnerId, uint? SoundTableDid)>();
        var fixture = new Fixture(
            ownerSoundTableChanged: (ownerId, soundTableDid) =>
                changed.Add((ownerId, soundTableDid)));
        var setup = new Setup
        {
            DefaultScriptTable = TableDid,
            DefaultSoundTable = 0x200000DDu,
        };
        WorldEntity entity = Entity(55u, 0u);
        fixture.Controller.OnDatStaticEntityReady(
            entity.Id,
            entity,
            EntityEffectProfile.CreateDatStatic(setup));

        Assert.Equal([(entity.Id, (uint?)0x200000DDu)], changed);
        Assert.True(fixture.Controller.PlayTyped(entity.Id, RawType, 0.5f));
        fixture.Runner.Tick(0.0);
        Assert.Equal([2u], EmitterIds(fixture.Sink));

        fixture.Controller.OnDatStaticEntityRemoved(entity.Id);
        Assert.False(fixture.Controller.PlayTyped(entity.Id, RawType, 0.5f));
    }

    [Fact]
    public void RegisteredSyntheticOwnerAdvancesThroughGlobalRunnerEligibility()
    {
        const uint skyOwnerId = 0xF0000042u;
        var fixture = new Fixture();
        fixture.Controller.RegisterSyntheticOwner(skyOwnerId);

        Assert.True(fixture.Runner.PlayDirect(skyOwnerId, DirectDid));
        fixture.Runner.Tick(0.0);

        Assert.Equal(skyOwnerId, Assert.Single(fixture.Sink.Calls).EntityId);
        fixture.Controller.UnregisterSyntheticOwner(skyOwnerId);
        Assert.False(fixture.Controller.CanAdvanceOwner(skyOwnerId));
    }

    [Fact]
    public void RefreshLiveOwnerPosesUsesCurrentWorldPositionAtDispatch()
    {
        var fixture = new Fixture();
        fixture.AddScript(DirectDid, 1u, startTime: 1.0);
        WorldEntity entity = fixture.ReadyLive();
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));
        entity.SetPosition(new Vector3(10, 20, 30));
        fixture.Controller.MarkLiveOwnerPoseDirty(Guid);

        fixture.Controller.RefreshLiveOwnerPoses();
        fixture.Runner.Tick(1.0);

        Assert.Equal(new Vector3(10, 20, 30), Assert.Single(fixture.Sink.Calls).Position);
    }

    [Fact]
    public void PoseRefreshWorkset_VisitsOnlyDirtyOwnerAcrossDenseReadySet()
    {
        const int ownerCount = 2_000;
        var fixture = new Fixture();
        WorldEntity? changedEntity = null;
        uint changedGuid = 0;
        for (int i = 0; i < ownerCount; i++)
        {
            uint guid = 0x71000000u + (uint)i;
            WorldEntity entity = fixture.ReadyLive(guid);
            if (i == 777)
            {
                changedGuid = guid;
                changedEntity = entity;
            }
        }

        fixture.Controller.RefreshLiveOwnerPoses();
        Assert.Equal(0, fixture.Controller.LastPoseRefreshOwnerVisitCount);

        changedEntity!.SetPosition(new Vector3(7, 8, 9));
        fixture.Controller.MarkLiveOwnerPoseDirty(changedGuid);
        fixture.Controller.RefreshLiveOwnerPoses();
        Assert.Equal(1, fixture.Controller.LastPoseRefreshOwnerVisitCount);

        fixture.Controller.RefreshLiveOwnerPoses();
        Assert.Equal(0, fixture.Controller.LastPoseRefreshOwnerVisitCount);
    }

    [Fact]
    public void PoseRefreshWorkset_RetainsCallbackMutationForNextPass()
    {
        const uint firstGuid = 0x72000001u;
        const uint secondGuid = 0x72000002u;
        var fixture = new Fixture();
        WorldEntity first = fixture.ReadyLive(firstGuid);
        WorldEntity second = fixture.ReadyLive(secondGuid);
        fixture.Poses.Publish(first, Array.Empty<Matrix4x4>());
        fixture.Poses.Publish(second, Array.Empty<Matrix4x4>());
        fixture.Controller.RefreshLiveOwnerPoses();
        first.SetPosition(new Vector3(10, 0, 0));

        bool callbackRan = false;
        fixture.Poses.EffectPoseChanged += localId =>
        {
            if (localId != first.Id || callbackRan)
                return;
            callbackRan = true;
            fixture.Controller.MarkLiveOwnerPoseDirty(secondGuid);
        };
        fixture.Controller.MarkLiveOwnerPoseDirty(firstGuid);

        fixture.Controller.RefreshLiveOwnerPoses();
        Assert.Equal(1, fixture.Controller.LastPoseRefreshOwnerVisitCount);
        fixture.Controller.RefreshLiveOwnerPoses();
        Assert.Equal(1, fixture.Controller.LastPoseRefreshOwnerVisitCount);
        Assert.True(callbackRan);
    }

    [Fact]
    public void PoseRefreshWorkset_RejectsDeletedIncarnationAfterGuidReuse()
    {
        var fixture = new Fixture();
        fixture.ReadyLive(Guid, generation: 1);
        fixture.Controller.MarkLiveOwnerPoseDirty(Guid);
        Assert.True(fixture.Runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(Guid, 1),
            isLocalPlayer: false));

        WorldEntity replacement = fixture.ReadyLive(Guid, generation: 2);
        replacement.SetPosition(new Vector3(20, 30, 40));
        fixture.Controller.MarkLiveOwnerPoseDirty(Guid);
        fixture.Controller.RefreshLiveOwnerPoses();

        Assert.Equal(1, fixture.Controller.LastPoseRefreshOwnerVisitCount);
    }

    [Fact]
    public void ClearNetworkStatePreservesDatStaticProfiles()
    {
        var fixture = new Fixture();
        fixture.ReadyLive();
        WorldEntity staticEntity = Entity(55u, 0u);
        fixture.Controller.OnDatStaticEntityReady(
            staticEntity.Id,
            staticEntity,
            EntityEffectProfile.CreateDatStatic(
                new Setup { DefaultScriptTable = TableDid }));
        fixture.Controller.HandleDirect(new PlayPhysicsScript(Guid, DirectDid));

        fixture.Controller.ClearNetworkState();

        Assert.Equal(0, fixture.Controller.PendingPacketCount);
        Assert.Equal(1, fixture.Controller.ReadyOwnerCount);
        Assert.True(fixture.Controller.PlayTyped(staticEntity.Id, RawType, 0.5f));
    }

    private static PhysicsScriptTable BuildTable(
        uint tableDid,
        uint rawType,
        params (float Mod, uint ScriptDid)[] entries)
    {
        var data = new PhysicsScriptTableData();
        foreach ((float mod, uint scriptDid) in entries)
        {
            data.Scripts.Add(new ScriptAndModData
            {
                Mod = mod,
                ScriptId = scriptDid,
            });
        }

        var table = new PhysicsScriptTable { Id = tableDid };
        table.ScriptTable.Add(unchecked((PlayScript)rawType), data);
        return table;
    }

    private static WorldEntity Entity(uint id, uint guid) => new()
    {
        Id = id,
        ServerGuid = guid,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = new Vector3(1, 2, 3),
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };

    private static WorldSession.EntitySpawn Spawn(uint guid, ushort generation)
    {
        var position = new CreateObject.ServerPosition(
            0x01010001u, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(
            1, 1, 1, 1, 0, 1, 0, 1, generation);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: TableDid,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: RawType,
            DefaultScriptIntensity: 0.5f,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: generation,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private static uint[] EmitterIds(RecordingSink sink) =>
        sink.Calls
            .Select(call => ((CreateParticleHook)call.Hook).EmitterInfoId.DataId)
            .ToArray();
}
