using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.App.World;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Ui;
using AcDream.Core.World;

namespace AcDream.App.Tests.UI.Layout;

public sealed class RadarSnapshotProviderTests
{
    [Fact]
    public void BuildSnapshot_JoinsWorldAndWeenieTables_UsingCoreRetailMath()
    {
        const uint player = 1u;
        const uint monster = 2u;
        var objects = new ClientObjectTable();
        objects.Ingest(Weenie(player, "Player", ItemType.Creature));
        objects.Ingest(Weenie(monster, "Drudge", ItemType.Creature) with
        {
            RadarBehavior = (byte)RadarBehavior.ShowAlways,
        });

        var entities = new Dictionary<uint, WorldEntity>
        {
            [player] = Entity(player, Vector3.Zero, Quaternion.Identity),
            [monster] = Entity(monster, new Vector3(0f, 15f, 6f), Quaternion.Identity),
        };
        var spawns = new Dictionary<uint, WorldSession.EntitySpawn>
        {
            [player] = Spawn(player) with { ObjectDescriptionFlags = 0x00000008u }, // BF_PLAYER
            [monster] = Spawn(monster) with { ObjectDescriptionFlags = 0x00000010u }, // BF_ATTACKABLE
        };

        uint? selected = monster;
        var provider = new RadarSnapshotProvider(
            objects, new RadarEntities(() => entities), () => spawns,
            playerGuid: () => player,
            playerYawRadians: () => MathF.PI / 2f,
            playerCellId: () => 0xA9B40001u,
            selectedGuid: () => selected,
            coordinatesOnRadar: () => true,
            uiLocked: () => false);

        var snapshot = provider.BuildSnapshot();

        Assert.Equal(0f, snapshot.PlayerHeadingDegrees, 3);
        Assert.Equal(RadarCoordinates.TryFromCell(0xA9B40001u, out var coords)
            ? coords.CombinedText : null, snapshot.CoordinatesText);
        var blip = Assert.Single(snapshot.Blips);
        Assert.Equal(monster, blip.ObjectId);
        Assert.Equal("Drudge", blip.Name);
        Assert.Equal(60f, blip.PixelX);
        Assert.Equal(50f, blip.PixelY); // 15 m * 50 px / 75 m
        Assert.Equal(RadarBlipShape.Plus, blip.Shape);
        Assert.True(blip.Selected);
        Assert.Equal(RadarBlipColors.Gold.Red * 0.65f, blip.Color.X, 5);
        Assert.Equal(RadarBlipColors.Gold.Green * 0.65f, blip.Color.Y, 5);
    }

    // OpenAC #47: a fellow's blip and the leader's blip take their own
    // colours; another player at the same range keeps the default colour.
    [Fact]
    public void BuildSnapshot_ColorsFellowsByTheRelationshipItIsGiven()
    {
        const uint player = 1u;
        const uint fellow = 2u;
        const uint leader = 3u;
        const uint stranger = 4u;
        var objects = new ClientObjectTable();
        objects.Ingest(Weenie(player, "Player", ItemType.Creature));
        foreach ((uint guid, string name) in new[] { (fellow, "Fellow"), (leader, "Leader"), (stranger, "Stranger") })
        {
            objects.Ingest(Weenie(guid, name, ItemType.Creature) with
            {
                RadarBehavior = (byte)RadarBehavior.ShowAlways,
            });
        }

        var entities = new Dictionary<uint, WorldEntity>
        {
            [player] = Entity(player, Vector3.Zero, Quaternion.Identity),
            [fellow] = Entity(fellow, new Vector3(0f, 15f, 0f), Quaternion.Identity),
            [leader] = Entity(leader, new Vector3(15f, 0f, 0f), Quaternion.Identity),
            [stranger] = Entity(stranger, new Vector3(-15f, 0f, 0f), Quaternion.Identity),
        };
        var spawns = new Dictionary<uint, WorldSession.EntitySpawn>
        {
            [player] = Spawn(player) with { ObjectDescriptionFlags = 0x00000008u }, // BF_PLAYER
            [fellow] = Spawn(fellow) with { ObjectDescriptionFlags = 0x00000008u },
            [leader] = Spawn(leader) with { ObjectDescriptionFlags = 0x00000008u },
            [stranger] = Spawn(stranger) with { ObjectDescriptionFlags = 0x00000008u },
        };

        var provider = new RadarSnapshotProvider(
            objects, new RadarEntities(() => entities), () => spawns,
            playerGuid: () => player,
            playerYawRadians: () => 0f,
            playerCellId: () => 0xA9B40001u,
            selectedGuid: () => null,
            coordinatesOnRadar: () => true,
            uiLocked: () => false,
            relationshipFor: guid => new RadarRelationshipTraits(
                IsFellowshipMember: guid is fellow or leader,
                IsFellowshipLeader: guid == leader));

        var snapshot = provider.BuildSnapshot();

        Assert.Equal(3, snapshot.Blips.Count);
        UiRadarBlip fellowBlip = Assert.Single(snapshot.Blips, blip => blip.ObjectId == fellow);
        UiRadarBlip leaderBlip = Assert.Single(snapshot.Blips, blip => blip.ObjectId == leader);
        UiRadarBlip strangerBlip = Assert.Single(snapshot.Blips, blip => blip.ObjectId == stranger);

        // The same range dims every blip alike, so the hue is the difference.
        float dim = strangerBlip.Color.Y;
        Assert.Equal(RadarBlipColors.Fellowship.Red * dim, fellowBlip.Color.X, 5);
        Assert.Equal(RadarBlipColors.Fellowship.Green * dim, fellowBlip.Color.Y, 5);
        Assert.Equal(RadarBlipColors.Fellowship.Blue * dim, fellowBlip.Color.Z, 5);
        Assert.Equal(RadarBlipColors.FellowshipLeader.Red * dim, leaderBlip.Color.X, 5);
        Assert.Equal(RadarBlipColors.FellowshipLeader.Green * dim, leaderBlip.Color.Y, 5);
        Assert.Equal(RadarBlipColors.Default.Red * dim, strangerBlip.Color.X, 5);
        Assert.Equal(RadarBlipColors.Default.Blue * dim, strangerBlip.Color.Z, 5);
    }

    [Fact]
    public void BuildSnapshot_RejectsShowNever_AndHidesIndoorCoordinates()
    {
        const uint player = 10u;
        const uint hidden = 11u;
        var objects = new ClientObjectTable();
        objects.Ingest(Weenie(player, "Player", ItemType.Creature));
        objects.Ingest(Weenie(hidden, "Hidden", ItemType.Portal) with
        {
            RadarBehavior = (byte)RadarBehavior.ShowNever,
        });
        var entities = new Dictionary<uint, WorldEntity>
        {
            [player] = Entity(player, Vector3.Zero, Quaternion.Identity),
            [hidden] = Entity(hidden, new Vector3(2f, 2f, 0f), Quaternion.Identity),
        };
        var spawns = new Dictionary<uint, WorldSession.EntitySpawn>
        {
            [player] = Spawn(player),
            [hidden] = Spawn(hidden),
        };
        var provider = new RadarSnapshotProvider(
            objects, new RadarEntities(() => entities), () => spawns,
            playerGuid: () => player,
            playerYawRadians: () => 0f,
            playerCellId: () => 0xA9B40100u,
            selectedGuid: () => null,
            coordinatesOnRadar: () => true,
            uiLocked: () => true);

        var snapshot = provider.BuildSnapshot();

        Assert.Empty(snapshot.Blips);
        Assert.Null(snapshot.CoordinatesText);
        Assert.True(snapshot.UiLocked);
    }

    [Fact]
    public void BuildSnapshot_UsesCanonicalPlayerWhileHiddenTargetsStayExcluded()
    {
        const uint player = 20u;
        const uint hiddenTarget = 21u;
        var objects = new ClientObjectTable();
        objects.Ingest(Weenie(player, "Player", ItemType.Creature));
        objects.Ingest(Weenie(hiddenTarget, "Hidden target", ItemType.Creature) with
        {
            RadarBehavior = (byte)RadarBehavior.ShowAlways,
        });
        var canonical = new Dictionary<uint, WorldEntity>
        {
            [player] = Entity(player, Vector3.Zero, Quaternion.Identity),
            [hiddenTarget] = Entity(hiddenTarget, new Vector3(5f, 0f, 0f), Quaternion.Identity),
        };
        var interactionVisible = new Dictionary<uint, WorldEntity>();
        var spawns = new Dictionary<uint, WorldSession.EntitySpawn>
        {
            [player] = Spawn(player),
            [hiddenTarget] = Spawn(hiddenTarget),
        };
        var provider = new RadarSnapshotProvider(
            objects,
            new RadarEntities(
                () => interactionVisible,
                () => canonical),
            () => spawns,
            playerGuid: () => player,
            playerYawRadians: () => 0f,
            playerCellId: () => 0xA9B40001u,
            selectedGuid: () => hiddenTarget,
            coordinatesOnRadar: () => true,
            uiLocked: () => false);

        var snapshot = provider.BuildSnapshot();

        Assert.NotNull(snapshot.CoordinatesText);
        Assert.Empty(snapshot.Blips);
    }

    [Fact]
    public void BuildSnapshot_ResolvesLiveViewsAfterRuntimeBootstraps()
    {
        const uint player = 30u;
        const uint monster = 31u;
        var objects = new ClientObjectTable();
        objects.Ingest(Weenie(player, "Player", ItemType.Creature));
        objects.Ingest(Weenie(monster, "Drudge", ItemType.Creature) with
        {
            RadarBehavior = (byte)RadarBehavior.ShowAlways,
        });

        IReadOnlyDictionary<uint, WorldEntity> visible =
            new Dictionary<uint, WorldEntity>();
        IReadOnlyDictionary<uint, WorldEntity> canonical =
            new Dictionary<uint, WorldEntity>();
        IReadOnlyDictionary<uint, WorldSession.EntitySpawn> spawns =
            new Dictionary<uint, WorldSession.EntitySpawn>();
        var provider = new RadarSnapshotProvider(
            objects,
            new RadarEntities(() => visible, () => canonical),
            () => spawns,
            playerGuid: () => player,
            playerYawRadians: () => MathF.PI / 2f,
            playerCellId: () => 0xA9B40001u,
            selectedGuid: () => null,
            coordinatesOnRadar: () => true,
            uiLocked: () => false);

        Assert.Null(provider.BuildSnapshot().CoordinatesText);

        visible = new Dictionary<uint, WorldEntity>
        {
            [player] = Entity(player, Vector3.Zero, Quaternion.Identity),
            [monster] = Entity(monster, new Vector3(0f, 15f, 0f), Quaternion.Identity),
        };
        canonical = visible;
        spawns = new Dictionary<uint, WorldSession.EntitySpawn>
        {
            [player] = Spawn(player),
            [monster] = Spawn(monster) with { ObjectDescriptionFlags = 0x00000010u },
        };

        UiRadarSnapshot snapshot = provider.BuildSnapshot();

        Assert.NotNull(snapshot.CoordinatesText);
        Assert.Equal(monster, Assert.Single(snapshot.Blips).ObjectId);
    }

    [Fact]
    public void BuildSnapshot_UsesBoundedSpatialCandidatesBeforeRetailProjection()
    {
        const uint player = 40u;
        const uint nearby = 41u;
        const uint unrelated = 42u;
        var objects = new ClientObjectTable();
        objects.Ingest(Weenie(player, "Player", ItemType.Creature));
        objects.Ingest(Weenie(nearby, "Nearby", ItemType.Creature) with
        {
            RadarBehavior = (byte)RadarBehavior.ShowAlways,
        });
        objects.Ingest(Weenie(unrelated, "Unrelated", ItemType.Creature) with
        {
            RadarBehavior = (byte)RadarBehavior.ShowAlways,
        });
        var entities = new Dictionary<uint, WorldEntity>
        {
            [player] = Entity(player, Vector3.Zero, Quaternion.Identity),
            [nearby] = Entity(nearby, new Vector3(5f, 0f, 0f), Quaternion.Identity),
            [unrelated] = Entity(unrelated, new Vector3(6f, 0f, 0f), Quaternion.Identity),
        };
        var spawns = entities.Keys.ToDictionary(guid => guid, Spawn);
        var spatialQuery = new RecordingSpatialQuery(
            new KeyValuePair<uint, WorldEntity>(nearby, entities[nearby]));
        var provider = new RadarSnapshotProvider(
            objects,
            new RadarEntities(() => entities),
            () => spawns,
            playerGuid: () => player,
            playerYawRadians: () => 0f,
            playerCellId: () => 0xA9B40001u,
            selectedGuid: () => null,
            coordinatesOnRadar: () => true,
            uiLocked: () => false,
            spatialQuery: () => spatialQuery);

        UiRadarSnapshot snapshot = provider.BuildSnapshot();

        Assert.Equal(0xA9B40001u, spatialQuery.CopiedCell);
        Assert.Equal(1, spatialQuery.CopiedRadius);
        Assert.Equal(nearby, Assert.Single(snapshot.Blips).ObjectId);
    }

    [Fact]
    public void BuildSnapshot_ResolvesSpatialOwnerAfterBootstrapReplacement()
    {
        const uint player = 50u;
        const uint monster = 51u;
        var objects = new ClientObjectTable();
        objects.Ingest(Weenie(player, "Player", ItemType.Creature));
        objects.Ingest(Weenie(monster, "Drudge", ItemType.Creature) with
        {
            RadarBehavior = (byte)RadarBehavior.ShowAlways,
        });
        var entities = new Dictionary<uint, WorldEntity>
        {
            [player] = Entity(player, Vector3.Zero, Quaternion.Identity),
            [monster] = Entity(monster, new Vector3(5f, 0f, 0f), Quaternion.Identity),
        };
        var spawns = entities.Keys.ToDictionary(guid => guid, Spawn);
        ILiveEntitySpatialQuery currentSpatialQuery = new RecordingSpatialQuery();
        var provider = new RadarSnapshotProvider(
            objects,
            new RadarEntities(() => entities),
            () => spawns,
            playerGuid: () => player,
            playerYawRadians: () => 0f,
            playerCellId: () => 0xA9B40001u,
            selectedGuid: () => null,
            coordinatesOnRadar: () => true,
            uiLocked: () => false,
            spatialQuery: () => currentSpatialQuery);

        Assert.Empty(provider.BuildSnapshot().Blips);

        currentSpatialQuery = new RecordingSpatialQuery(
            new KeyValuePair<uint, WorldEntity>(monster, entities[monster]));

        Assert.Equal(monster, Assert.Single(provider.BuildSnapshot().Blips).ObjectId);
    }

    private sealed class RecordingSpatialQuery(
        params KeyValuePair<uint, WorldEntity>[] candidates) : ILiveEntitySpatialQuery
    {
        public uint CopiedCell { get; private set; }
        public int CopiedRadius { get; private set; } = -1;

        public void CopyLiveEntitiesNearLandblock(
            uint centerCellOrLandblockId,
            int landblockRadius,
            List<KeyValuePair<uint, WorldEntity>> destination)
        {
            CopiedCell = centerCellOrLandblockId;
            CopiedRadius = landblockRadius;
            destination.AddRange(candidates);
        }
    }

    private sealed class RadarEntities(
        Func<IReadOnlyDictionary<uint, WorldEntity>> visible,
        Func<IReadOnlyDictionary<uint, WorldEntity>>? materialized = null)
        : ILiveEntityRadarSource
    {
        private readonly Func<IReadOnlyDictionary<uint, WorldEntity>> _visible =
            visible;
        private readonly Func<IReadOnlyDictionary<uint, WorldEntity>> _materialized =
            materialized ?? visible;

        public bool TryGetMaterialized(
            uint serverGuid,
            out WorldEntity entity) =>
            _materialized().TryGetValue(serverGuid, out entity!);

        public bool TryGetVisible(
            uint serverGuid,
            out WorldEntity entity) =>
            _visible().TryGetValue(serverGuid, out entity!);

        public void CopyVisibleTo(
            List<KeyValuePair<uint, WorldEntity>> destination)
        {
            destination.Clear();
            foreach (KeyValuePair<uint, WorldEntity> pair in _visible())
                destination.Add(pair);
        }
    }

    private static WorldEntity Entity(uint guid, Vector3 position, Quaternion rotation) => new()
    {
        Id = guid,
        ServerGuid = guid,
        SourceGfxObjOrSetupId = 0u,
        Position = position,
        Rotation = rotation,
        MeshRefs = Array.Empty<MeshRef>(),
    };

    private static WorldSession.EntitySpawn Spawn(uint guid) => new(
        Guid: guid,
        Position: null,
        SetupTableId: null,
        AnimPartChanges: Array.Empty<CreateObject.AnimPartChange>(),
        TextureChanges: Array.Empty<CreateObject.TextureChange>(),
        SubPalettes: Array.Empty<CreateObject.SubPaletteSwap>(),
        BasePaletteId: null,
        ObjScale: null,
        Name: null,
        ItemType: null,
        MotionState: null,
        MotionTableId: null);

    private static WeenieData Weenie(uint guid, string name, ItemType type) => new(
        Guid: guid,
        Name: name,
        Type: type,
        WeenieClassId: 1u,
        IconId: 0u,
        IconOverlayId: 0u,
        IconUnderlayId: 0u,
        Effects: 0u,
        Value: null,
        StackSize: null,
        StackSizeMax: null,
        Burden: null,
        ContainerId: null,
        WielderId: null,
        ValidLocations: null,
        CurrentWieldedLocation: null,
        Priority: null,
        ItemsCapacity: null,
        ContainersCapacity: null,
        Structure: null,
        MaxStructure: null,
        Workmanship: null);
}
