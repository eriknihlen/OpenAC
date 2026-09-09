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

        uint playerPwd = spawns.TryGetValue(playerGuid, out var playerSpawn)
            ? playerSpawn.ObjectDescriptionFlags ?? 0u
            : 0u;
        var playerTraits = RadarObjectTraits.FromPublicWeenieDescription(
            (uint)(_objects.Get(playerGuid)?.Type ?? ItemType.None), playerPwd);
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

        var blips = new List<UiRadarBlip>(Math.Min(_candidateScratch.Count, 64));
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
            uint pwd = spawn.ObjectDescriptionFlags ?? 0u;
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
}
