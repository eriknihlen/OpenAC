using System.Numerics;
using AcDream.App.World;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Physics.Motion;
using AcDream.Core.Ui;
using AcDream.Core.World;

namespace AcDream.App.UI.Layout;

public sealed class RadarSnapshotProvider
{
    private static readonly Vector2 ProductionCenter = new(60f, 60f);

    private readonly ClientObjectTable _objects;
    private readonly ILiveEntityRadarSource _liveEntities;
    private readonly Func<IReadOnlyDictionary<uint, WorldSession.EntitySpawn>> _spawns;
    private readonly Func<uint> _playerGuid;
    private readonly Func<float> _playerYawRadians;
    private readonly Func<uint> _playerCellId;
    private readonly Func<uint?> _selectedGuid;
    private readonly Func<bool> _coordinatesOnRadar;
    private readonly Func<bool> _uiLocked;
    private readonly Func<uint, RadarRelationshipTraits>? _relationshipFor;
    private readonly Func<ILiveEntitySpatialQuery?>? _spatialQuery;
    private readonly List<KeyValuePair<uint, WorldEntity>> _candidateScratch = new();
    private readonly List<UiRadarBlip> _blipScratch = new();

    public RadarSnapshotProvider(
        ClientObjectTable objects,
        ILiveEntityRadarSource liveEntities,
        Func<IReadOnlyDictionary<uint, WorldSession.EntitySpawn>> spawns,
        Func<uint> playerGuid,
        Func<float> playerYawRadians,
        Func<uint> playerCellId,
        Func<uint?> selectedGuid,
        Func<bool> coordinatesOnRadar,
        Func<bool> uiLocked,
        Func<uint, RadarRelationshipTraits>? relationshipFor = null,
        Func<ILiveEntitySpatialQuery?>? spatialQuery = null)
    {
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _spawns = spawns ?? throw new ArgumentNullException(nameof(spawns));
        _playerGuid = playerGuid ?? throw new ArgumentNullException(nameof(playerGuid));
        _playerYawRadians = playerYawRadians ?? throw new ArgumentNullException(nameof(playerYawRadians));
        _playerCellId = playerCellId ?? throw new ArgumentNullException(nameof(playerCellId));
        _selectedGuid = selectedGuid ?? throw new ArgumentNullException(nameof(selectedGuid));
        _coordinatesOnRadar = coordinatesOnRadar ?? throw new ArgumentNullException(nameof(coordinatesOnRadar));
        _uiLocked = uiLocked ?? throw new ArgumentNullException(nameof(uiLocked));
        _relationshipFor = relationshipFor;
        _spatialQuery = spatialQuery;
    }

    public UiRadarSnapshot BuildSnapshot()
    {
        IReadOnlyDictionary<uint, WorldSession.EntitySpawn> spawns = _spawns();

        bool uiLocked = _uiLocked();
        uint playerGuid = _playerGuid();
        if (playerGuid == 0u
            || !_liveEntities.TryGetMaterialized(
                playerGuid,
                out WorldEntity playerEntity))
            return UiRadarSnapshot.Empty with { UiLocked = uiLocked };

        float heading = MoveToMath.HeadingFromYaw(_playerYawRadians());
        uint playerCellId = _playerCellId();
        bool isOutside = RadarCoordinates.TryFromCell(playerCellId, out var playerCoordinates);
        string? coordinates = _coordinatesOnRadar() && isOutside
            ? playerCoordinates.CombinedText
            : null;

        spawns.TryGetValue(playerGuid, out var playerSpawn);
        ClientObject? playerObject = _objects.Get(playerGuid);
        var playerTraits = RadarObjectTraits.FromPublicWeenieDescription(
            (uint)(playerObject?.Type ?? ItemType.None),
            DescriptionBits(playerObject, playerSpawn));
        float range = RetailRadar.GetRangeMeters(isOutside);

        _candidateScratch.Clear();
        if (_spatialQuery?.Invoke() is { } spatialQuery)
        {
            spatialQuery.CopyLiveEntitiesNearLandblock(playerCellId, 1, _candidateScratch);
        }
        else
        {
            _liveEntities.CopyVisibleTo(_candidateScratch);
        }

        // The blips are the provider's own list, rewritten on every refresh.
        // A snapshot is read by the radar that asked for it and replaced by
        // the next refresh's, so nothing outlives the rewrite; a list per
        // refresh was one of the last per-frame allocations left.
        List<UiRadarBlip> blips = _blipScratch;
        blips.Clear();
        foreach (var pair in _candidateScratch)
        {
            uint guid = pair.Key;
            if (guid == playerGuid)
                continue;

            var entity = pair.Value;
            if (!_liveEntities.TryGetVisible(guid, out WorldEntity? visibleEntity)
                || !ReferenceEquals(entity, visibleEntity))
            {
                continue;
            }
            var clientObject = _objects.Get(guid);
            spawns.TryGetValue(guid, out var spawn);

            byte? rawBehavior = clientObject?.RadarBehavior ?? spawn.RadarBehavior;
            if (rawBehavior is null
                || !RetailRadar.IsShowable((RadarBehavior)rawBehavior.Value, hasPhysicsObject: true))
            {
                continue;
            }

            uint itemType = (uint)(clientObject?.Type ?? ItemType.None);
            if (itemType == 0u)
                itemType = spawn.ItemType ?? 0u;
            uint pwd = DescriptionBits(clientObject, spawn);
            byte colorOverride = clientObject?.RadarBlipColor ?? spawn.RadarBlipColor ?? 0;
            var traits = RadarObjectTraits.FromPublicWeenieDescription(
                itemType, pwd, colorOverride);

            var relationship = _relationshipFor?.Invoke(guid) ?? default;
            relationship = relationship with
            {
                PlayerIsPlayerKiller = playerTraits.IsPlayerKiller,
                PlayerIsPkLite = playerTraits.IsPkLite,
            };
            var shape = RetailRadar.GetBlipShape(traits, relationship);
            if (shape == RadarBlipShape.Undef)
                continue;

            Vector3 playerSpace = MoveToMath.GlobalToLocalVec(
                playerEntity.Rotation,
                entity.Position - playerEntity.Position);
            if (!RetailRadar.TryProject(
                    playerSpace,
                    ProductionCenter,
                    RadarController.RadarPixelRadius,
                    range,
                    out var projection))
            {
                continue;
            }

            var color = RadarBlipColors.For(traits, relationship)
                .DimRgb(projection.RgbMultiplier);
            string? name = clientObject?.Name;
            if (string.IsNullOrEmpty(name))
                name = spawn.Name ?? $"0x{guid:X8}";

            blips.Add(new UiRadarBlip(
                ObjectId: guid,
                Name: name,
                PixelX: projection.Pixel.X,
                PixelY: projection.Pixel.Y,
                Color: new Vector4(color.Red, color.Green, color.Blue, color.Alpha),
                Shape: shape,
                Selected: _selectedGuid() == guid));
        }

        return new UiRadarSnapshot(
            PlayerHeadingDegrees: heading,
            Blips: blips,
            CoordinatesText: coordinates,
            BlankBlips: false,
            UiLocked: uiLocked);
    }

    /// <summary>
    /// The object's public description bits as they stand now. The object
    /// table follows the server's later changes (hidden from the UI, player
    /// killer status); the bits the object was created with are the fallback.
    /// </summary>
    private static uint DescriptionBits(
        ClientObject? clientObject,
        WorldSession.EntitySpawn spawn)
        => clientObject?.PublicWeenieBitfield ?? spawn.ObjectDescriptionFlags ?? 0u;
}
