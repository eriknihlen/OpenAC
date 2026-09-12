using System.Numerics;
using AcDream.App.Rendering.Selection;
using AcDream.App.UI.Layout;
using AcDream.App.World;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Physics;
using AcDream.Core.Properties;
using AcDream.Core.Selection;
using AcDream.Core.Ui;
using AcDream.Core.World;

namespace AcDream.App.Interaction;

internal readonly record struct SelectionCameraSnapshot(
    Matrix4x4 View,
    Matrix4x4 Projection,
    Vector2 Viewport);

internal readonly record struct PlayerInteractionPose(uint CellId, Vector3 Position);

internal readonly record struct WorldInteractionTarget(
    uint ServerGuid,
    uint LocalEntityId,
    WorldEntity Entity);

internal readonly record struct ClosestCombatTarget(uint ServerGuid, float DistanceSquared);

internal enum RetailSelectionKind
{
    Item,
    CompassItem,
    Monster,
    Player,
    UnopenedCorpse,
}

internal enum RetailSelectionDirection
{
    Closest,
    Previous,
    Next,
}

internal readonly record struct InteractionApproach(
    WorldInteractionTarget Target,
    PlayerInteractionPose Player,
    float UseRadius,
    bool IsCloseRange,
    bool CanCharge,
    float TargetRadius,
    float TargetHeight);

internal interface IWorldSelectionQuery
{
    uint PlayerGuid => 0u;
    uint? PickAtCursor(bool includeSelf);
    uint? PickAt(float mouseX, float mouseY, bool includeSelf);
    void BeginLightingPulse(uint serverGuid);
    bool TryCaptureIdentity(uint serverGuid, out uint localEntityId);
    bool IsCurrent(uint serverGuid, uint localEntityId);
    string Describe(uint serverGuid);
    bool IsCreature(uint serverGuid);
    bool IsHostileMonster(uint serverGuid);
    bool IsAttackableTarget(uint serverGuid);
    ClosestCombatTarget? FindClosestHostileMonster();
    uint? FindSelectionTarget(
        RetailSelectionKind kind,
        RetailSelectionDirection direction,
        uint? anchor,
        bool excludeOwnedByPlayer = false) =>
        kind == RetailSelectionKind.Monster
            && direction == RetailSelectionDirection.Closest
                ? FindClosestHostileMonster()?.ServerGuid
                : null;
    uint? FindLastAttacker() => null;
    bool IsUseable(uint serverGuid);
    bool IsPickupable(uint serverGuid);

    /// <summary>A loose world object the server flagged as stuck in place.</summary>
    bool IsStuckInWorld(uint serverGuid);
    bool IsWieldedByPlayer(uint serverGuid);
    bool IsWieldedPositionState(uint serverGuid);
    bool TryGetApproach(uint serverGuid, out InteractionApproach approach);
    Vector3? GetCombatCameraTargetPoint(uint serverGuid);
}

internal interface IRetainedUiSelectionQuery
{
    bool ShouldShowHealth(uint serverGuid);
    VividTargetInfo? ResolveVividTargetInfo(uint serverGuid);
    bool IsWithinExternalContainerUseRange(uint serverGuid);
}

internal interface ISelectionViewPlaneSource
{
    AcDream.App.Rendering.ICamera ApplyViewPlane(
        AcDream.App.Rendering.ICamera camera);
}

internal sealed class WorldSelectionQuery
    : IWorldSelectionQuery,
      IRetainedUiSelectionQuery
{
    private const uint StuckObjectFlag = 0x0004u;
    private const float DefaultUseRadius = 0.6f;
    private const float AceCanChargeDistance = 7.5f;

    private readonly LiveEntityRuntime _liveEntities;
    private readonly ClientObjectTable _objects;
    private readonly RetailSelectionScene _selectionScene;
    private readonly Func<uint> _playerGuid;
    private readonly Func<SelectionCameraSnapshot> _camera;
    private readonly Func<Vector2> _cursor;
    private readonly Func<PlayerInteractionPose?> _playerPose;
    private readonly Func<uint, WorldEntity, (float Radius, float Height)> _setupCylinder;
    private readonly Func<uint, (Vector3 Origin, float Radius)?> _selectionSphere;
    private readonly Func<uint, Matrix4x4?> _childRootPose;
    private readonly Func<uint, bool> _hasOpenedCorpse;
    private readonly Func<CombatMode> _combatMode;
    private readonly Func<uint, bool> _isFellow;

    public WorldSelectionQuery(
        LiveEntityRuntime liveEntities,
        ClientObjectTable objects,
        RetailSelectionScene selectionScene,
        Func<uint> playerGuid,
        Func<SelectionCameraSnapshot> camera,
        Func<Vector2> cursor,
        Func<PlayerInteractionPose?> playerPose,
        Func<uint, WorldEntity, (float Radius, float Height)> setupCylinder,
        Func<uint, (Vector3 Origin, float Radius)?> selectionSphere,
        Func<uint, Matrix4x4?> childRootPose,
        Func<uint, bool>? hasOpenedCorpse = null,
        Func<CombatMode>? combatMode = null,
        Func<uint, bool>? isFellow = null)
    {
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _selectionScene = selectionScene ?? throw new ArgumentNullException(nameof(selectionScene));
        _playerGuid = playerGuid ?? throw new ArgumentNullException(nameof(playerGuid));
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _cursor = cursor ?? throw new ArgumentNullException(nameof(cursor));
        _playerPose = playerPose ?? throw new ArgumentNullException(nameof(playerPose));
        _setupCylinder = setupCylinder ?? throw new ArgumentNullException(nameof(setupCylinder));
        _selectionSphere = selectionSphere ?? throw new ArgumentNullException(nameof(selectionSphere));
        _childRootPose = childRootPose ?? throw new ArgumentNullException(nameof(childRootPose));
        _hasOpenedCorpse = hasOpenedCorpse ?? (_ => false);
        _combatMode = combatMode ?? (() => CombatMode.NonCombat);
        _isFellow = isFellow ?? (_ => false);
    }

    public uint PlayerGuid => _playerGuid();

    public uint? PickAtCursor(bool includeSelf)
    {
        Vector2 cursor = _cursor();
        return PickAt(cursor.X, cursor.Y, includeSelf);
    }

    public uint? PickAt(float mouseX, float mouseY, bool includeSelf)
    {
        SelectionCameraSnapshot camera = _camera();
        RetailSelectionHit? hit = _selectionScene.Pick(
            mouseX,
            mouseY,
            camera.Viewport,
            camera.View,
            camera.Projection,
            includeSelf ? 0u : _playerGuid());
        return hit is { } found
            && _liveEntities.TryGetPickEligibleRecord(
                found.ServerGuid,
                found.LocalEntityId,
                out _)
            ? found.ServerGuid
            : null;
    }

    public void BeginLightingPulse(uint serverGuid)
    {
        if (TryGetInteractionTarget(serverGuid, out WorldInteractionTarget target))
            _selectionScene.BeginLightingPulse(target.ServerGuid, target.LocalEntityId);
    }

    public bool TryCaptureIdentity(uint serverGuid, out uint localEntityId)
    {
        if (TryGetInteractionTarget(serverGuid, out WorldInteractionTarget target))
        {
            localEntityId = target.LocalEntityId;
            return true;
        }

        localEntityId = 0u;
        return false;
    }

    public bool TryGetInteractionTarget(
        uint serverGuid,
        out WorldInteractionTarget target)
    {
        if (_liveEntities.TryGetPickEligibleRecord(
                serverGuid,
                out LiveEntityRecord record)
            && record.WorldEntity is { } entity)
        {
            target = new WorldInteractionTarget(serverGuid, entity.Id, entity);
            return true;
        }

        target = default;
        return false;
    }

    public bool IsCurrent(WorldInteractionTarget target)
        => IsCurrent(target.ServerGuid, target.LocalEntityId);

    public bool IsCurrent(uint serverGuid, uint localEntityId)
        => _liveEntities.TryGetPickEligibleRecord(serverGuid, localEntityId, out _);

    public ItemType GetItemType(uint serverGuid)
        => _objects.Get(serverGuid)?.Type ?? ItemType.None;

    public string Describe(uint serverGuid)
    {
        string? name = _objects.Get(serverGuid)?.Name;
        return string.IsNullOrWhiteSpace(name) ? $"0x{serverGuid:X8}" : name;
    }

    public bool IsCreature(uint serverGuid)
    {
        if (serverGuid == _playerGuid()
            || !TryGetInteractionTarget(serverGuid, out WorldInteractionTarget target))
        {
            return false;
        }

        if (_liveEntities.TryGetAnimationRuntime(target.LocalEntityId, out var animation)
            && animation.CurrentMotion == MotionCommand.Dead)
        {
            return false;
        }

        return (GetItemType(serverGuid) & ItemType.Creature) != 0;
    }

    public bool IsHostileMonster(uint serverGuid)
        => IsCreature(serverGuid)
            && CombatTargetPolicy.IsHostileMonster(
                _playerGuid(),
                _objects.Get(_playerGuid()),
                _objects.Get(serverGuid));

    public bool IsAttackableTarget(uint serverGuid)
        => IsCreature(serverGuid)
            && SelectedObjectHealthPolicy.ObjectIsAttackable(
                _playerGuid(),
                _objects.Get(_playerGuid()),
                serverGuid,
                _objects.Get(serverGuid));

    public bool ShouldShowHealth(uint serverGuid)
        => SelectedObjectHealthPolicy.ShouldQueryHealth(
            _playerGuid(),
            _objects.Get(_playerGuid()),
            _objects.Get(serverGuid));

    public ClosestCombatTarget? FindClosestHostileMonster()
    {
        if (!_liveEntities.TryGetWorldEntity(_playerGuid(), out WorldEntity player))
            return null;

        ClosestCombatTarget? best = null;
        foreach (LiveEntityRecord record in _liveEntities.VisibleRecords)
        {
            uint guid = record.ServerGuid;
            WorldEntity entity = record.WorldEntity!;
            if (!IsHostileMonster(guid))
                continue;
            float distanceSquared = Vector3.DistanceSquared(entity.Position, player.Position);
            if (best is null || distanceSquared < best.Value.DistanceSquared)
                best = new ClosestCombatTarget(guid, distanceSquared);
        }
        return best;
    }

    public uint? FindSelectionTarget(
        RetailSelectionKind kind,
        RetailSelectionDirection direction,
        uint? anchor,
        bool excludeOwnedByPlayer = false)
    {
        uint playerGuid = _playerGuid();
        if (!_liveEntities.TryGetWorldEntity(playerGuid, out WorldEntity player))
            return null;

        float radarRadius = IsOutdoorCell(player.VisibilityCellId)
            ? RetailRadar.OutdoorRangeMeters
            : RetailRadar.IndoorRangeMeters;
        var candidates = new List<(uint Guid, float Order)>();
        foreach (LiveEntityRecord record in _liveEntities.VisibleRecords)
        {
            uint guid = record.ServerGuid;
            if (guid == 0u
                || guid == playerGuid
                || record.WorldEntity is not { } entity
                || _objects.Get(guid) is not { } obj
                || (excludeOwnedByPlayer
                    && _objects.IsOwnedByObject(guid, playerGuid)))
            {
                continue;
            }

            float order = SelectionOrder(player, entity);
            if (order > radarRadius
                || !MatchesSelectionKind(kind, guid, obj, record.FinalPhysicsState))
                continue;
            candidates.Add((guid, order));
        }

        if (candidates.Count == 0)
            return null;
        candidates.Sort(static (left, right) =>
        {
            int distance = left.Order.CompareTo(right.Order);
            return distance != 0 ? distance : left.Guid.CompareTo(right.Guid);
        });

        if (direction == RetailSelectionDirection.Closest)
            return candidates[0].Guid;

        (float Order, uint Guid)? anchorKey = null;
        if (anchor is { } anchorGuid
            && _liveEntities.TryGetWorldEntity(anchorGuid, out WorldEntity anchorEntity))
        {
            anchorKey = (SelectionOrder(player, anchorEntity), anchorGuid);
        }

        if (anchorKey is null)
        {
            return direction == RetailSelectionDirection.Previous
                ? candidates[^1].Guid
                : candidates[0].Guid;
        }

        if (direction == RetailSelectionDirection.Next)
        {
            foreach ((uint guid, float order) in candidates)
            {
                if (CompareSelectionKey(order, guid, anchorKey.Value.Order, anchorKey.Value.Guid) > 0)
                    return guid;
            }
            return candidates[0].Guid;
        }

        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            (uint guid, float order) = candidates[i];
            if (CompareSelectionKey(order, guid, anchorKey.Value.Order, anchorKey.Value.Guid) < 0)
                return guid;
        }
        return candidates[^1].Guid;
    }

    public uint? FindLastAttacker()
    {
        uint playerGuid = _playerGuid();
        uint attacker = 0u;
        if (_objects.Get(playerGuid) is not { } playerObject
            || !playerObject.Properties.InstanceIds.TryGetValue(
                (uint)PropertyInstanceId.CurrentAttacker,
                out attacker)
            || attacker == 0u
            || !_liveEntities.TryGetWorldEntity(playerGuid, out WorldEntity player)
            || !_liveEntities.TryGetWorldEntity(attacker, out WorldEntity target))
        {
            return null;
        }

        float radarRadius = IsOutdoorCell(player.VisibilityCellId)
            ? RetailRadar.OutdoorRangeMeters
            : RetailRadar.IndoorRangeMeters;
        return SelectionOrder(player, target) <= radarRadius ? attacker : null;
    }

    private bool MatchesSelectionKind(
        RetailSelectionKind kind,
        uint guid,
        ClientObject obj,
        PhysicsStateFlags physicsState)
    {
        bool showableOnRadar = obj.RadarBehavior is { } behavior
            && RetailRadar.IsShowable((RadarBehavior)behavior, hasPhysicsObject: true);
        PublicWeenieFlags flags = (PublicWeenieFlags)(obj.PublicWeenieBitfield ?? 0u);
        bool isFellow = _isFellow(guid);
        bool isCombatCompass = _combatMode() is CombatMode.Melee or CombatMode.Missile;
        bool isSpecialCompassObject = (flags
            & (PublicWeenieFlags.Lifestone
                | PublicWeenieFlags.Portal
                | PublicWeenieFlags.Bindstone)) != 0;

        if (obj.ContainerId != 0u
            || (physicsState & PhysicsStateFlags.Cloaked) != 0
            || (((uint)flags & 0x8000_0000u) != 0))
            return false;

        return kind switch
        {
            RetailSelectionKind.Item =>
                obj.RadarBehavior is null or 0
                || isSpecialCompassObject,
            RetailSelectionKind.CompassItem =>
                (isSpecialCompassObject || showableOnRadar)
                && (!isCombatCompass
                    || (IsAttackableTarget(guid)
                        && !isFellow
                        && (flags & PublicWeenieFlags.Vendor) == 0
                        && (physicsState & PhysicsStateFlags.ReportAsEnvironment) == 0)),
            RetailSelectionKind.Monster =>
                showableOnRadar
                && IsAttackableTarget(guid)
                && !isFellow
                && (flags & PublicWeenieFlags.Vendor) == 0,
            RetailSelectionKind.Player =>
                showableOnRadar && (flags & PublicWeenieFlags.Player) != 0,
            RetailSelectionKind.UnopenedCorpse =>
                (flags & PublicWeenieFlags.Corpse) != 0
                && !_hasOpenedCorpse(guid),
            _ => false,
        };
    }

    private static float SelectionOrder(WorldEntity player, WorldEntity target)
    {
        Vector3 delta = target.Position - player.Position;
        Vector3 local = Vector3.Transform(delta, Quaternion.Inverse(player.Rotation));
        return MathF.Sqrt(local.X * local.X + local.Y * local.Y)
            + MathF.Abs(local.Z) * 1.2f;
    }

    private static int CompareSelectionKey(
        float leftOrder,
        uint leftGuid,
        float rightOrder,
        uint rightGuid)
    {
        int order = leftOrder.CompareTo(rightOrder);
        return order != 0 ? order : leftGuid.CompareTo(rightGuid);
    }

    private static bool IsOutdoorCell(uint? cellId)
        => cellId is null || (cellId.Value & 0xFFFFu) < 0x100u;

    public Vector3? GetCombatCameraTargetPoint(uint serverGuid)
        => IsAttackableTarget(serverGuid)
            && TryGetInteractionTarget(serverGuid, out WorldInteractionTarget target)
                ? target.Entity.Position
                    + Vector3.Transform(new Vector3(0f, 0f, 0.5f), target.Entity.Rotation)
                : null;

    public VividTargetInfo? ResolveVividTargetInfo(uint serverGuid)
    {
        uint playerGuid = _playerGuid();
        ClientObject? target = _objects.Get(serverGuid);

        if (serverGuid == playerGuid
            || target is null
            || _objects.IsOwnedByObject(serverGuid, playerGuid)
            || target.ContainerId != 0u
            || !(_liveEntities.TryGetSpatiallyProjectedRecord(serverGuid, out _)
                || _liveEntities.TryGetAttachedProjectedRecord(serverGuid, out _))
            || !TryGetSelectionSphere(serverGuid, out Vector3 center, out float radius))
        {
            return null;
        }

        uint pwdBits = _liveEntities.TryGetSnapshot(serverGuid, out var spawn)
            ? spawn.ObjectDescriptionFlags ?? 0u
            : 0u;
        return new VividTargetInfo(center, radius, (uint)GetItemType(serverGuid), pwdBits);
    }

    public bool TryGetSelectionSphere(
        uint serverGuid,
        out Vector3 worldCenter,
        out float worldRadius)
    {
        worldCenter = default;
        worldRadius = 0f;
        if (!_liveEntities.TryGetWorldEntity(serverGuid, out WorldEntity entity))
            return false;

        bool attached =
            _liveEntities.TryGetAttachedProjectedRecord(serverGuid, out _);
        Matrix4x4 childRoot = Matrix4x4.Identity;
        if (attached)
        {
            if (_childRootPose(entity.Id) is not { } published)
                return false;
            childRoot = published;
        }

        worldCenter = attached ? childRoot.Translation : entity.Position;
        worldRadius = 0.1f;
        if (!_liveEntities.TryGetSnapshot(serverGuid, out var spawn)
            || spawn.SetupTableId is not uint setupId
            || _selectionSphere(setupId) is not { } sphere
            || sphere.Radius <= 1e-4f)
        {
            return true;
        }

        float scale = attached
            ? (spawn.ObjScale is { } childScale && childScale > 0f ? childScale : 1f)
            : (entity.Scale > 0f ? entity.Scale : 1f);
        Vector3 localCenter = sphere.Origin * scale;
        worldCenter = attached
            ? Vector3.Transform(localCenter, childRoot)
            : entity.Position + Vector3.Transform(localCenter, entity.Rotation);
        worldRadius = sphere.Radius * scale;
        return true;
    }

    public bool IsUseable(uint serverGuid)
    {
        if (_liveEntities.TryGetSnapshot(serverGuid, out var spawn))
            return ItemUseability.IsUseable(
                spawn.Useability ?? ItemUseability.Undef);
        return false;
    }

    public bool IsWieldedByPlayer(uint serverGuid)
    {
        uint playerGuid = _playerGuid();
        return playerGuid != 0u
            && _objects.Get(serverGuid) is { } item
            && item.WielderId == playerGuid;
    }

    public bool IsWieldedPositionState(uint serverGuid)
        => _objects.Get(serverGuid) is { } item
            && item.ContainerId == 0u
            && item.CurrentlyEquippedLocation != EquipMask.None;

    /// <summary>
    /// Whether a world object may be lifted into a pack. There is no list of
    /// item types here: any loose object can be picked up unless it is stuck
    /// in place or is a container that holds other containers (a chest, a
    /// corpse). Creatures and items someone else wields are refused by the
    /// caller with their own messages.
    /// </summary>
    public bool IsPickupable(uint serverGuid)
    {
        if (!_liveEntities.TryGetSnapshot(serverGuid, out var spawn))
            return false;
        if (((spawn.ObjectDescriptionFlags ?? 0u) & StuckObjectFlag) != 0u)
            return false;
        return (spawn.ContainersCapacity ?? 0) == 0;
    }

    public bool IsStuckInWorld(uint serverGuid)
        => _liveEntities.TryGetSnapshot(serverGuid, out var spawn)
            && ((spawn.ObjectDescriptionFlags ?? 0u) & StuckObjectFlag) != 0u;

    public bool TryGetApproach(
        uint serverGuid,
        out InteractionApproach approach)
    {
        if (_playerPose() is not { } player
            || _liveEntities.TryGetAttachedProjectedRecord(serverGuid, out _)
            || !TryGetInteractionTarget(serverGuid, out WorldInteractionTarget target))
        {
            approach = default;
            return false;
        }

        float useRadius = GetUseRadius(serverGuid);
        float dx = target.Entity.Position.X - player.Position.X;
        float dy = target.Entity.Position.Y - player.Position.Y;
        float distanceSquared = dx * dx + dy * dy;
        (float radius, float height) = _setupCylinder(serverGuid, target.Entity);
        approach = new InteractionApproach(
            target,
            player,
            useRadius,
            distanceSquared <= useRadius * useRadius,
            distanceSquared >= AceCanChargeDistance * AceCanChargeDistance,
            radius,
            height);
        return true;
    }

    public bool IsWithinExternalContainerUseRange(uint serverGuid)
    {
        if (_playerPose() is not { } playerPose
            || !_liveEntities.TryGetWorldEntity(_playerGuid(), out WorldEntity player)
            || !TryGetInteractionTarget(serverGuid, out WorldInteractionTarget target)
            || !_liveEntities.TryGetSnapshot(serverGuid, out var spawn)
            || spawn.UseRadius is not > 0f)
        {
            // The server remains authoritative while render projection is absent.
            return true;
        }

        var playerCylinder = _setupCylinder(_playerGuid(), player);
        var targetCylinder = _setupCylinder(serverGuid, target.Entity);
        return ObjectRangeMath.ObjectsInRange(
            playerPose.Position,
            playerCylinder.Radius,
            playerCylinder.Height,
            target.Entity.Position,
            targetCylinder.Radius,
            targetCylinder.Height,
            spawn.UseRadius.Value,
            useRadii: true,
            ignoreZDelta: false);
    }

    private float GetUseRadius(uint serverGuid)
    {
        bool haveSpawn = _liveEntities.TryGetSnapshot(serverGuid, out var spawn);
        bool fromWire = haveSpawn && spawn.UseRadius is > 0f;
        float radius = fromWire ? spawn.UseRadius!.Value : DefaultUseRadius;
        return radius;
    }
}
