using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace AcDream.Core.Items;

public readonly record struct ContainerContentEntry(uint Guid, uint ContainerType);

/// <summary>One login-time PlayerDescription equipment placement.</summary>
public readonly record struct EquipmentManifestEntry(
    uint Guid,
    EquipMask EquipLocation,
    uint Priority);

public readonly record struct MoveRequestFailure(
    uint ItemId,
    uint WeenieError,
    bool RolledBack);

public readonly record struct ClientObjectPlacement(
    uint ContainerId,
    int ContainerSlot,
    uint WielderId,
    EquipMask EquipLocation)
{
    public static ClientObjectPlacement From(ClientObject item) => new(
        item.ContainerId,
        item.ContainerSlot,
        item.WielderId,
        item.CurrentlyEquippedLocation);
}

public enum ClientObjectMoveOrigin
{
    LocalProjection,
    ServerRollbackProjection,
    AuthoritativeResponse,
}

public readonly record struct ClientObjectMove(
    uint ItemId,
    ClientObject? Item,
    ClientObjectPlacement Previous,
    ClientObjectPlacement Current,
    ClientObjectMoveOrigin Origin);

public enum ClientObjectRemovalReason
{
    Ordinary,
    LogicalDelete,
    GenerationReplacement,
}

public readonly record struct ClientObjectRemoval(
    ClientObject Object,
    ClientObjectRemovalReason Reason,
    ushort Generation);

public sealed class ClientObjectTable
{
    private readonly ConcurrentDictionary<uint, ClientObject> _objects = new();
    private readonly ConcurrentDictionary<uint, Container> _containers = new();
    private readonly Dictionary<uint, List<uint>> _containerIndex = new();
    private readonly Dictionary<uint, List<uint>> _equipmentIndex = new();
    private readonly HashSet<ClientObject> _restrictionObservedObjects =
        new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<uint, (ClientObjectPlacement placement, int outstanding)> _pendingMoves = new();

    private readonly Dictionary<uint, (ClientObjectPlacement Placement, uint? ContainerTypeHint)>
        _pendingUnresolvedPlacements = new();
    private ulong _mutationRevision;

    public ClientObjectTable()
    {
        ObjectAdded += _ => AdvanceMutationRevision();
        ObjectMoved += _ => AdvanceMutationRevision();
        ObjectRemoved += _ => AdvanceMutationRevision();
        ObjectUpdated += _ => AdvanceMutationRevision();
        Cleared += AdvanceMutationRevision;
    }

    /// <summary>
    /// Monotonic authority for live object-table qualities used by physics,
    /// including house ownership/restrictions and the mover's monarch.
    /// </summary>
    internal ulong MutationRevision => _mutationRevision;

    private void AdvanceMutationRevision() =>
        _mutationRevision = checked(_mutationRevision + 1UL);

    private void RetainObject(ClientObject item)
    {
        if (_objects.TryGetValue(item.ObjectId, out ClientObject? prior)
            && !ReferenceEquals(prior, item))
        {
            UnbindRestrictionAuthority(prior);
        }

        _objects[item.ObjectId] = item;
        BindRestrictionAuthority(item);
    }

    private void BindRestrictionAuthority(ClientObject item)
    {
        if (!_restrictionObservedObjects.Add(item)) return;
        item.RestrictionAuthorityChanged += OnRestrictionAuthorityChanged;
    }

    private void UnbindRestrictionAuthority(ClientObject item)
    {
        if (!_restrictionObservedObjects.Remove(item)) return;
        item.RestrictionAuthorityChanged -= OnRestrictionAuthorityChanged;
    }

    private void OnRestrictionAuthorityChanged(ClientObject item)
    {
        if (_objects.TryGetValue(item.ObjectId, out ClientObject? retained)
            && ReferenceEquals(retained, item))
        {
            AdvanceMutationRevision();
        }
    }

    /// <summary>Fires when an object is first added to the session.</summary>
    public event Action<ClientObject>? ObjectAdded;

    public event Action<ClientObjectMove>? ObjectMoved;

    public event Action<uint>? ContainerContentsReplaced;

    public event Action<ClientObject>? MoveRolledBack;

    public event Action<uint>? WieldConfirmed;

    public event Action<MoveRequestFailure>? MoveRequestFailed;

    /// <summary>Fires when an object is removed from the session.</summary>
    public event Action<ClientObject>? ObjectRemoved;

    public event Action<ClientObjectRemoval>? ObjectRemovalClassified;

    /// <summary>Fires when an object's properties are updated (typically after Appraise).</summary>
    public event Action<ClientObject>? ObjectUpdated;

    /// <summary>
    /// Fires only for an authoritative SetStackSize response. Inventory request
    /// owners use this narrower seam instead of treating an unrelated appraisal
    /// or property update as the response to a merge, split, give, or drop.
    /// </summary>
    public event Action<ClientObject>? StackSizeUpdated;

    /// <summary>Fires after all session object and pending-move state is flushed.</summary>
    public event Action? Cleared;

    public const uint UiEffectsPropertyId = 18u;
    public const uint CurrentWieldedLocationPropertyId = 10u;
    public const uint HookTypePropertyId = 151u;
    public const uint HookItemTypesPropertyId = 152u;
    public const uint SharedCooldownPropertyId = 280u;
    public const uint CooldownDurationPropertyId = 167u;
    public const uint PlayerKillerStatusPropertyId = 134u;

    public int ObjectCount => _objects.Count;
    public int ContainerCount => _containers.Count;
    public int ContainerProjectionCount => _containerIndex.Count;
    public int EquipmentOwnerCount => _equipmentIndex.Count;
    public int PendingMoveCount => _pendingMoves.Count;

    public IEnumerable<ClientObject> Objects => _objects.Values;
    public IEnumerable<Container> Containers => _containers.Values;

    /// <summary>
    /// Look up an object by its server-assigned <c>ObjectId</c>.
    /// </summary>
    public ClientObject? Get(uint objectId) =>
        _objects.TryGetValue(objectId, out var item) ? item : null;

    public bool IsOwnedByObject(uint objectId, uint ownerId)
    {
        if (ownerId == 0u
            || !_objects.TryGetValue(objectId, out ClientObject? item))
        {
            return false;
        }

        if (item.ObjectId == ownerId
            || item.ContainerId == ownerId
            || item.WielderId == ownerId)
        {
            return true;
        }

        if (!_objects.ContainsKey(ownerId))
            return false;

        if (item.ContainerId != 0u
            && _containerIndex.TryGetValue(ownerId, out List<uint>? ownerContents)
            && ownerContents.Contains(item.ContainerId))
        {
            return true;
        }

        return _equipmentIndex.TryGetValue(ownerId, out List<uint>? ownerLocations)
            && ownerLocations.Contains(item.ObjectId);
    }

    public Container? GetContainer(uint objectId) =>
        _containers.TryGetValue(objectId, out var c) ? c : null;

    public void AddOrUpdate(ClientObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        bool existed = _objects.TryGetValue(item.ObjectId, out ClientObject? prior);
        ClientObjectPlacement previous = prior is null
            ? default
            : ClientObjectPlacement.From(prior);
        RetainObject(item);
        UpdateEquipmentIndex(item.ObjectId, previous, ClientObjectPlacement.From(item));
        if (!existed) ObjectAdded?.Invoke(item);
        else ObjectUpdated?.Invoke(item);
    }

    public void AddContainer(Container container)
    {
        ArgumentNullException.ThrowIfNull(container);
        _containers[container.ObjectId] = container;
    }

    public bool MoveItem(uint itemId, uint newContainerId, int newSlot = -1,
        EquipMask newEquipLocation = EquipMask.None, uint? containerTypeHint = null)
    {
        if (!_objects.TryGetValue(itemId, out var item)) return false;
        return ApplyPlacement(
            item,
            new ClientObjectPlacement(
                newContainerId,
                newSlot,
                item.WielderId,
                newEquipLocation),
            containerTypeHint,
            ClientObjectMoveOrigin.LocalProjection);
    }

    public bool ApplyServerMove(
        uint itemId,
        uint newContainerId,
        uint newWielderId,
        int newSlot = -1,
        EquipMask newEquipLocation = EquipMask.None,
        uint? containerTypeHint = null)
    {
        if (!_objects.TryGetValue(itemId, out var item)) return false;
        return ApplyPlacement(
            item,
            new ClientObjectPlacement(
                newContainerId,
                newSlot,
                newWielderId,
                newEquipLocation),
            containerTypeHint,
            ClientObjectMoveOrigin.AuthoritativeResponse,
            retailContainerInsert: true);
    }

    private bool ApplyPlacement(
        ClientObject item,
        ClientObjectPlacement current,
        uint? containerTypeHint,
        ClientObjectMoveOrigin origin,
        bool retailContainerInsert = false)
    {
        ClientObjectPlacement previous = ClientObjectPlacement.From(item);
        bool previousWasContainer = IsContainerListMember(item);
        List<uint>? changedContainers;
        if (retailContainerInsert)
        {
            changedContainers = RemoveFromAllContainerIndexes(
                item.ObjectId,
                current.ContainerId,
                previousWasContainer);
        }
        else
        {
            changedContainers = RemoveFromOtherContainerIndexes(
                item.ObjectId,
                current.ContainerId);
        }
        item.ContainerId = current.ContainerId;
        item.ContainerSlot = current.ContainerSlot;
        item.WielderId = current.WielderId;
        item.CurrentlyEquippedLocation = current.EquipLocation;
        if (containerTypeHint is { } hint)
            item.ContainerTypeHint = hint;
        if (retailContainerInsert && item.ContainerId != 0u)
            InsertContainerMember(item, current.ContainerSlot);
        else
            Reindex(item, previous.ContainerId);
        PublishPlacementChange(item, previous, origin);
        PublishContainerContentsChanges(changedContainers);
        return true;
    }

    private void PublishPlacementChange(
        ClientObject item,
        ClientObjectPlacement previous,
        ClientObjectMoveOrigin origin)
    {
        ClientObjectPlacement current = ClientObjectPlacement.From(item);
        UpdateEquipmentIndex(item.ObjectId, previous, current);
        ObjectMoved?.Invoke(new ClientObjectMove(item.ObjectId, item, previous, current, origin));
    }

    public bool ApplyConfirmedServerMove(
        uint itemId,
        uint newContainerId,
        uint newWielderId,
        int newSlot = -1,
        EquipMask newEquipLocation = EquipMask.None,
        uint? containerTypeHint = null)
    {
        ConfirmMove(itemId);
        if (ApplyServerMove(
            itemId,
            newContainerId,
            newWielderId,
            newSlot,
            newEquipLocation,
            containerTypeHint))
        {
            return true;
        }

        if (itemId != 0u && newContainerId != 0u && !_objects.ContainsKey(itemId))
        {
            _pendingUnresolvedPlacements[itemId] = (
                new ClientObjectPlacement(
                    newContainerId,
                    newSlot,
                    newWielderId,
                    newEquipLocation),
                containerTypeHint);
        }

        ObjectMoved?.Invoke(new ClientObjectMove(
            itemId,
            Item: null,
            Previous: default,
            Current: new ClientObjectPlacement(
                newContainerId,
                newSlot,
                newWielderId,
                newEquipLocation),
            ClientObjectMoveOrigin.AuthoritativeResponse));
        return false;
    }

    public bool ApplyConfirmedServerWield(
        uint itemId,
        uint wielderId,
        EquipMask equipLocation)
    {
        ConfirmMove(itemId);
        var placement = new ClientObjectPlacement(
            ContainerId: 0u,
            ContainerSlot: 0,
            WielderId: wielderId,
            EquipLocation: equipLocation);
        if (!_objects.TryGetValue(itemId, out ClientObject? item))
        {
            ObjectMoved?.Invoke(new ClientObjectMove(
                itemId,
                Item: null,
                Previous: default,
                Current: placement,
                ClientObjectMoveOrigin.AuthoritativeResponse));
            WieldConfirmed?.Invoke(itemId);
            return false;
        }

        if (!ApplyPlacement(
                item,
                placement,
                containerTypeHint: null,
                ClientObjectMoveOrigin.AuthoritativeResponse))
        {
            return false;
        }

        WieldConfirmed?.Invoke(itemId);
        return true;
    }

    private void RecordPending(uint itemId, ClientObject item)
    {
        if (_pendingMoves.TryGetValue(itemId, out var p))
            _pendingMoves[itemId] = (p.placement, p.outstanding + 1);
        else
            _pendingMoves[itemId] = (ClientObjectPlacement.From(item), 1);
    }

    public bool MoveItemOptimistic(uint itemId, uint newContainerId, int newSlot)
    {
        if (!_objects.TryGetValue(itemId, out var item)) return false;
        RecordPending(itemId, item);

        ClientObjectPlacement previous = ClientObjectPlacement.From(item);
        uint oldContainer = previous.ContainerId;
        item.ContainerId = newContainerId;
        item.CurrentlyEquippedLocation = EquipMask.None;
        List<uint>? changedContainers = RemoveFromOtherContainerIndexes(
            itemId,
            newContainerId);

        if (oldContainer != 0 && oldContainer != newContainerId
            && _containerIndex.TryGetValue(oldContainer, out var srcList))
        { srcList.Remove(itemId); RenumberContainer(srcList); }      // keep the source gapless too

        if (newContainerId != 0)
        {
            if (!_containerIndex.TryGetValue(newContainerId, out var dstList))
                _containerIndex[newContainerId] = dstList = new List<uint>();
            dstList.Remove(itemId);
            int idx = (newSlot < 0 || newSlot > dstList.Count) ? dstList.Count : newSlot;
            dstList.Insert(idx, itemId);
            RenumberContainer(dstList);
        }
        else item.ContainerSlot = newSlot;

        PublishPlacementChange(item, previous, ClientObjectMoveOrigin.LocalProjection);
        PublishContainerContentsChanges(changedContainers);
        return true;
    }

    public bool WieldItemOptimistic(uint itemId, uint wielderGuid, EquipMask equipMask)
    {
        if (!_objects.TryGetValue(itemId, out var item)) return false;
        RecordPending(itemId, item);
        return MoveItem(itemId, wielderGuid, newSlot: -1, newEquipLocation: equipMask);
    }

    private void RenumberContainer(List<uint> list)
    {
        for (int i = 0; i < list.Count; i++)
            if (_objects.TryGetValue(list[i], out var o)) o.ContainerSlot = i;
    }

    public void ConfirmMove(uint itemId)
    {
        if (!_pendingMoves.TryGetValue(itemId, out var p)) return;
        if (p.outstanding <= 1)
        {
            _pendingMoves.Remove(itemId);
        }
        else
        {
            _pendingMoves[itemId] = (p.placement, p.outstanding - 1);
        }
    }

    public bool RollbackMove(uint itemId)
    {
        if (!_pendingMoves.TryGetValue(itemId, out var pre)) return false;
        _pendingMoves.Remove(itemId);
        ClientObjectPlacement placement = pre.placement;
        if (!_objects.TryGetValue(itemId, out ClientObject? item)
            || !ApplyPlacement(
                item,
                placement,
                containerTypeHint: null,
                ClientObjectMoveOrigin.ServerRollbackProjection))
            return false;
        MoveRolledBack?.Invoke(_objects[itemId]);
        return true;
    }

    public bool RejectMove(uint itemId, uint weenieError)
    {
        bool rolledBack = RollbackMove(itemId);
        MoveRequestFailed?.Invoke(new MoveRequestFailure(itemId, weenieError, rolledBack));
        return rolledBack;
    }

    /// <summary>
    /// Handle a server-driven remove (destroyed item, dropped into 3D
    /// space, stolen, etc).
    /// </summary>
    public bool Remove(uint itemId)
        => RemoveCore(itemId, ClientObjectRemovalReason.Ordinary, 0, notifyObjectRemoved: true);

    /// <summary>Removes an exact accepted server object incarnation.</summary>
    public bool RemoveLogicalGeneration(uint itemId, ushort generation)
        => RemoveCore(
            itemId,
            ClientObjectRemovalReason.LogicalDelete,
            generation,
            notifyObjectRemoved: true);

    private bool RemoveCore(
        uint itemId,
        ClientObjectRemovalReason reason,
        ushort generation,
        bool notifyObjectRemoved)
    {
        if (!_objects.TryRemove(itemId, out var item)) return false;
        UnbindRestrictionAuthority(item);
        List<uint>? changedContainers = RemoveFromOtherContainerIndexes(
            itemId,
            exceptContainerId: 0u);
        UpdateEquipmentIndex(
            itemId,
            ClientObjectPlacement.From(item),
            default);
        _pendingMoves.Remove(itemId);   // a destroyed item must not leave a snapshot that mis-rolls-back a recycled guid
        if (notifyObjectRemoved)
            ObjectRemoved?.Invoke(item);
        ObjectRemovalClassified?.Invoke(new ClientObjectRemoval(item, reason, generation));
        PublishContainerContentsChanges(changedContainers);
        return true;
    }

    /// <summary>
    /// Atomically replaces the retained qualities for a newer server object
    /// incarnation while publishing a generation-specific teardown event.
    /// </summary>
    public ClientObject? ReplaceGeneration(
        WeenieData data,
        ushort generation,
        Func<bool>? canInstallReplacement = null)
    {
        RemoveCore(
            data.Guid,
            ClientObjectRemovalReason.GenerationReplacement,
            generation,
            notifyObjectRemoved: true);

        if (canInstallReplacement?.Invoke() == false)
            return null;

        return Ingest(data);
    }

    /// <summary>
    /// Publishes a client-side change on an object (a sale or trade marker)
    /// to every observer, the same way a server property update does.
    /// </summary>
    public bool NotifyObjectUpdated(uint itemId)
    {
        if (!_objects.TryGetValue(itemId, out var item)) return false;
        ObjectUpdated?.Invoke(item);
        return true;
    }

    public bool UpdateProperties(uint itemId, PropertyBundle incoming)
    {
        if (!_objects.TryGetValue(itemId, out var item)) return false;
        MergeProperties(item, incoming);
        ApplyCooldownProperties(item, incoming);
        ObjectUpdated?.Invoke(item);
        return true;
    }

    /// <summary>
    /// Atomically retains every successful item-appraisal result: the typed
    /// property tables and the per-item SpellBook block. Publishing one update
    /// prevents observers from seeing properties without their matching spell
    /// manifest (or the reverse).
    /// </summary>
    public bool UpdateAppraisal(
        uint itemId,
        PropertyBundle incoming,
        IReadOnlyList<uint> spellIds,
        double receivedAtSeconds = 0d)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(spellIds);
        if (!_objects.TryGetValue(itemId, out var item)) return false;
        MergeProperties(item, incoming);
        item.AppraisedSpellIds = spellIds.Count == 0
            ? Array.Empty<uint>()
            : spellIds.ToArray();
        if (double.IsFinite(receivedAtSeconds) && receivedAtSeconds >= 0d)
        {
            long milliseconds = checked((long)Math.Round(
                receivedAtSeconds * 1000d,
                MidpointRounding.AwayFromZero));
            item.LastAppraisalTimeMs = unchecked((int)milliseconds);
        }
        ApplyCooldownProperties(item, incoming);
        ObjectUpdated?.Invoke(item);
        return true;
    }

    private static void MergeProperties(ClientObject item, PropertyBundle incoming)
    {
        foreach (var kv in incoming.Ints)     item.Properties.Ints[kv.Key] = kv.Value;
        foreach (var kv in incoming.Int64s)   item.Properties.Int64s[kv.Key] = kv.Value;
        foreach (var kv in incoming.Bools)    item.Properties.Bools[kv.Key] = kv.Value;
        foreach (var kv in incoming.Floats)   item.Properties.Floats[kv.Key] = kv.Value;
        foreach (var kv in incoming.Strings)  item.Properties.Strings[kv.Key] = kv.Value;
        foreach (var kv in incoming.DataIds)  item.Properties.DataIds[kv.Key] = kv.Value;
        foreach (var kv in incoming.InstanceIds) item.Properties.InstanceIds[kv.Key] = kv.Value;
    }

    public void UpsertProperties(uint guid, PropertyBundle incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        bool existed = _objects.TryGetValue(guid, out var item);
        if (!existed || item is null)
        {
            item = new ClientObject { ObjectId = guid };
            RetainObject(item);
        }
        foreach (var kv in incoming.Ints)        item.Properties.Ints[kv.Key] = kv.Value;
        foreach (var kv in incoming.Int64s)      item.Properties.Int64s[kv.Key] = kv.Value;
        foreach (var kv in incoming.Bools)       item.Properties.Bools[kv.Key] = kv.Value;
        foreach (var kv in incoming.Floats)      item.Properties.Floats[kv.Key] = kv.Value;
        foreach (var kv in incoming.Strings)     item.Properties.Strings[kv.Key] = kv.Value;
        foreach (var kv in incoming.DataIds)     item.Properties.DataIds[kv.Key] = kv.Value;
        foreach (var kv in incoming.InstanceIds) item.Properties.InstanceIds[kv.Key] = kv.Value;
        ApplyCooldownProperties(item, incoming);
        if (!existed) ObjectAdded?.Invoke(item); else ObjectUpdated?.Invoke(item);
    }

    public bool UpdateIntProperty(uint itemId, uint propertyId, int value)
    {
        if (!_objects.TryGetValue(itemId, out var item)) return false;
        ClientObjectPlacement previous = ClientObjectPlacement.From(item);
        item.Properties.Ints[propertyId] = value;
        if (propertyId == UiEffectsPropertyId) item.Effects = (uint)value;
        if (propertyId == SharedCooldownPropertyId) item.CooldownId = (uint)value;
        if (propertyId == CurrentWieldedLocationPropertyId)
            item.CurrentlyEquippedLocation = (EquipMask)(uint)value;
        if (propertyId == HookTypePropertyId) item.HookType = (uint)value;
        if (propertyId == HookItemTypesPropertyId) item.HookItemTypes = (uint)value;
        if (propertyId == PlayerKillerStatusPropertyId)
        {
            item.PublicWeenieBitfield = PlayerKillerStatusBitfield.Apply(
                item.PublicWeenieBitfield ?? 0u, value);
        }
        if (propertyId == CurrentWieldedLocationPropertyId)
            UpdateEquipmentIndex(itemId, previous, ClientObjectPlacement.From(item));
        ObjectUpdated?.Invoke(item);
        return true;
    }

    private static void ApplyCooldownProperties(
        ClientObject item,
        PropertyBundle properties)
    {
        if (properties.Ints.TryGetValue(
                SharedCooldownPropertyId,
                out int cooldownId))
            item.CooldownId = (uint)cooldownId;
        if (properties.Floats.TryGetValue(
                CooldownDurationPropertyId,
                out double cooldownDuration))
            item.CooldownDuration = cooldownDuration;
    }

    /// <summary>
    /// Applies one data-id property the server changed on an object we already
    /// hold. The three icon ids also live in typed fields the item panels read,
    /// so they are mirrored there; everything else stays in the property bag.
    /// </summary>
    public bool UpdateDataIdProperty(uint itemId, uint propertyId, uint value)
    {
        if (!_objects.TryGetValue(itemId, out var item)) return false;
        item.Properties.DataIds[propertyId] = value;
        switch ((Properties.PropertyDataId)propertyId)
        {
            case Properties.PropertyDataId.Icon:
                item.IconId = value;
                break;
            case Properties.PropertyDataId.IconOverlay:
                item.IconOverlayId = value;
                break;
            case Properties.PropertyDataId.IconUnderlay:
                item.IconUnderlayId = value;
                break;
        }
        ObjectUpdated?.Invoke(item);
        return true;
    }

    /// <summary>
    /// Applies one instance-id property the server changed on an object we
    /// already hold. Placement (container, wielder) has its own ordered routes,
    /// so this only records the value and republishes the object.
    /// </summary>
    public bool UpdateInstanceIdProperty(uint itemId, uint propertyId, uint value)
    {
        if (!_objects.TryGetValue(itemId, out var item)) return false;
        item.Properties.InstanceIds[propertyId] = value;
        ObjectUpdated?.Invoke(item);
        return true;
    }

    public bool UpdateInt64Property(uint itemId, uint propertyId, long value)
    {
        if (!_objects.TryGetValue(itemId, out var item)) return false;
        item.Properties.Int64s[propertyId] = value;
        ObjectUpdated?.Invoke(item);
        return true;
    }

    public bool UpdateStackSize(uint guid, int stackSize, int value)
    {
        if (!_objects.TryGetValue(guid, out var item)) return false;
        item.StackSize = stackSize;
        item.Value = value;
        ObjectUpdated?.Invoke(item);
        StackSizeUpdated?.Invoke(item);
        return true;
    }

    public bool UpdateHouseRestrictions(uint guid, HouseRestrictionRecord restrictions)
    {
        ArgumentNullException.ThrowIfNull(restrictions);
        if (!_objects.TryGetValue(guid, out var item)) return false;
        item.Restrictions = restrictions;
        ObjectUpdated?.Invoke(item);
        return true;
    }

    public ClientObject Ingest(WeenieData d)
    {
        bool existed = _objects.TryGetValue(d.Guid, out var obj);
        if (!existed || obj is null) // keep: satisfies nullable flow analysis
        {
            obj = new ClientObject { ObjectId = d.Guid };
            RetainObject(obj);
        }
        uint oldContainer = obj.ContainerId;
        ClientObjectPlacement previous = ClientObjectPlacement.From(obj);

        if (!string.IsNullOrEmpty(d.Name)) obj.Name = d.Name!;
        if (!string.IsNullOrEmpty(d.PluralName)) obj.PluralName = d.PluralName!;
        if (d.Type is { } t) obj.Type = t;
        if (d.WeenieClassId != 0) obj.WeenieClassId = d.WeenieClassId;
        if (d.IconId != 0) obj.IconId = d.IconId;
        if (d.IconOverlayId != 0) obj.IconOverlayId = d.IconOverlayId;
        if (d.IconUnderlayId != 0) obj.IconUnderlayId = d.IconUnderlayId;
        obj.Effects = d.Effects;
        if (d.Value is { } v) obj.Value = v;
        if (d.StackSize is { } s) obj.StackSize = s;
        if (d.StackSizeMax is { } sm) obj.StackSizeMax = sm;
        if (d.Burden is { } b) obj.Burden = b;
        if (d.ContainerId is { } c) obj.ContainerId = c;
        if (d.WielderId is { } w) obj.WielderId = w;
        if (d.ValidLocations is { } vl) obj.ValidLocations = (EquipMask)vl;
        if (d.CurrentWieldedLocation is { } cwl) obj.CurrentlyEquippedLocation = (EquipMask)cwl;
        if (d.Priority is { } pr) obj.Priority = pr;
        if (d.Useability is { } use) obj.Useability = use;
        if (d.TargetType is { } targetType) obj.TargetType = targetType;
        if (d.PublicWeenieBitfield is { } bitfield) obj.PublicWeenieBitfield = bitfield;
        if (d.PetOwnerId is { } petOwnerId) obj.PetOwnerId = petOwnerId;
        if (d.CombatUse is { } combatUse) obj.CombatUse = combatUse;
        if (d.AmmoType is { } ammoType) obj.AmmoType = ammoType;
        if (d.SpellId is { } spellId) obj.SpellId = spellId;
        if (d.CooldownId is { } cooldownId) obj.CooldownId = cooldownId;
        if (d.CooldownDuration is { } cooldownDuration)
            obj.CooldownDuration = cooldownDuration;
        if (d.RadarBlipColor is { } radarBlipColor) obj.RadarBlipColor = radarBlipColor;
        if (d.RadarBehavior is { } radarBehavior) obj.RadarBehavior = radarBehavior;
        if (d.ItemsCapacity is { } ic) obj.ItemsCapacity = ic;
        if (d.ContainersCapacity is { } cc) obj.ContainersCapacity = cc;
        if (d.HookItemTypes is { } hookItemTypes) obj.HookItemTypes = hookItemTypes;
        if (d.HookType is { } hookType) obj.HookType = hookType;
        if (d.Structure is { } st) obj.Structure = st;
        if (d.MaxStructure is { } ms) obj.MaxStructure = ms;
        if (d.Workmanship is { } wm) obj.Workmanship = wm;
        if (d.MaterialType is { } materialType) obj.MaterialType = materialType;
        if (d.HouseOwnerId is { } houseOwnerId) obj.HouseOwnerId = houseOwnerId;
        if (d.MonarchId is { } monarchId) obj.MonarchId = monarchId;
        if (d.Restrictions is { } restrictions) obj.Restrictions = restrictions;

        List<uint>? changedContainers = RemoveFromOtherContainerIndexes(
            obj.ObjectId,
            obj.ContainerId);
        Reindex(obj, oldContainer);
        UpdateEquipmentIndex(obj.ObjectId, previous, ClientObjectPlacement.From(obj));
        if (!existed) ObjectAdded?.Invoke(obj); else ObjectUpdated?.Invoke(obj);
        PublishContainerContentsChanges(changedContainers);

        if (!existed
            && _pendingUnresolvedPlacements.Remove(
                d.Guid, out var pending))
        {
            ApplyServerMove(
                d.Guid,
                pending.Placement.ContainerId,
                pending.Placement.WielderId,
                pending.Placement.ContainerSlot,
                pending.Placement.EquipLocation,
                pending.ContainerTypeHint);
        }
        return obj;
    }

    public ClientObject RecordMembership(uint guid, uint containerId = 0,
        EquipMask equip = EquipMask.None, uint? containerTypeHint = null,
        uint? priority = null)
    {
        bool existed = _objects.TryGetValue(guid, out var obj);
        if (!existed || obj is null) // keep: satisfies nullable flow analysis
        {
            obj = new ClientObject { ObjectId = guid };
            RetainObject(obj);
        }
        uint oldContainer = obj.ContainerId;
        ClientObjectPlacement previous = ClientObjectPlacement.From(obj);
        if (containerId != 0)
            obj.ContainerId = containerId;
        obj.CurrentlyEquippedLocation = equip;
        if (equip != EquipMask.None)
            obj.ContainerSlot = -1;
        if (containerTypeHint is { } hint) obj.ContainerTypeHint = hint;
        if (priority is { } p) obj.Priority = p;
        List<uint>? changedContainers = RemoveFromOtherContainerIndexes(
            obj.ObjectId,
            obj.ContainerId);
        Reindex(obj, oldContainer);
        UpdateEquipmentIndex(obj.ObjectId, previous, ClientObjectPlacement.From(obj));
        if (!existed) ObjectAdded?.Invoke(obj); else ObjectUpdated?.Invoke(obj);
        PublishContainerContentsChanges(changedContainers);
        return obj;
    }

    public void InitializeEquipmentManifest(
        uint wielderId,
        IReadOnlyList<EquipmentManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (wielderId == 0u) return;

        var ordered = new List<uint>(entries.Count);
        var notifications = new List<(ClientObject Item, bool Existed)>(entries.Count);
        var changedContainers = new List<uint>();
        var incoming = new HashSet<uint>();
        for (int i = 0; i < entries.Count; i++)
            incoming.Add(entries[i].Guid);

        if (_equipmentIndex.TryGetValue(wielderId, out List<uint>? priorEquipment))
        {
            foreach (uint priorId in priorEquipment.ToArray())
            {
                if (incoming.Contains(priorId)
                    || !_objects.TryGetValue(priorId, out ClientObject? prior)
                    || prior is null
                    || (prior.WielderId != wielderId
                        && !(prior.ContainerId == wielderId
                             && prior.CurrentlyEquippedLocation != EquipMask.None)))
                    continue;

                prior.ContainerId = 0u;
                prior.ContainerSlot = -1;
                prior.WielderId = 0u;
                prior.CurrentlyEquippedLocation = EquipMask.None;
                prior.Priority = 0u;
                if (RemoveFromOtherContainerIndexes(priorId, exceptContainerId: 0u)
                    is { } removedFrom)
                {
                    changedContainers.AddRange(removedFrom);
                }
                notifications.Add((prior, true));
            }
        }

        for (int i = 0; i < entries.Count; i++)
        {
            EquipmentManifestEntry entry = entries[i];
            ordered.Add(entry.Guid);
            bool existed = _objects.TryGetValue(entry.Guid, out ClientObject? obj);
            if (!existed || obj is null)
            {
                obj = new ClientObject { ObjectId = entry.Guid };
                RetainObject(obj);
            }

            ClientObjectPlacement previous = ClientObjectPlacement.From(obj);
            obj.ContainerId = 0u;
            obj.ContainerSlot = -1;
            obj.WielderId = wielderId;
            obj.CurrentlyEquippedLocation = entry.EquipLocation;
            obj.Priority = entry.Priority;
            if (RemoveFromOtherContainerIndexes(entry.Guid, exceptContainerId: 0u)
                is { } changed)
            {
                changedContainers.AddRange(changed);
            }
            Reindex(obj, previous.ContainerId);
            RemoveEquipmentIndexMembership(entry.Guid);
            notifications.Add((obj, existed));
        }

        _equipmentIndex[wielderId] = ordered;
        foreach ((ClientObject item, bool existed) in notifications)
        {
            if (!existed) ObjectAdded?.Invoke(item);
            else ObjectUpdated?.Invoke(item);
        }
        PublishContainerContentsChanges(changedContainers);
    }

    private List<uint>? RemoveFromOtherContainerIndexes(
        uint itemId,
        uint exceptContainerId)
    {
        List<uint>? changed = null;
        foreach ((uint containerId, List<uint> members) in _containerIndex)
        {
            if (containerId == exceptContainerId || !members.Remove(itemId))
                continue;
            (changed ??= new List<uint>()).Add(containerId);
        }

        return changed;
    }

    private List<uint>? RemoveFromAllContainerIndexes(
        uint itemId,
        uint destinationContainerId,
        bool wasContainer)
    {
        List<uint>? changed = null;
        foreach ((uint containerId, List<uint> members) in _containerIndex)
        {
            if (!members.Remove(itemId))
                continue;

            RenumberContainerCategory(members, wasContainer);
            if (containerId != destinationContainerId)
                (changed ??= new List<uint>()).Add(containerId);
        }

        return changed;
    }

    private void InsertContainerMember(ClientObject item, int requestedSlot)
    {
        if (!_containerIndex.TryGetValue(item.ContainerId, out List<uint>? members))
            _containerIndex[item.ContainerId] = members = new List<uint>();

        bool isContainer = IsContainerListMember(item);
        int categoryCount = 0;
        int insertionIndex = members.Count;
        int requestedIndex = requestedSlot < 0 ? int.MaxValue : requestedSlot;
        for (int i = 0; i < members.Count; i++)
        {
            if (!_objects.TryGetValue(members[i], out ClientObject? existing)
                || IsContainerListMember(existing) != isContainer)
            {
                continue;
            }

            if (categoryCount == requestedIndex)
            {
                insertionIndex = i;
                break;
            }

            categoryCount++;
            insertionIndex = i + 1;
        }

        members.Insert(insertionIndex, item.ObjectId);
        int publishedSlot = requestedSlot < 0 ? categoryCount : requestedSlot;
        RenumberContainerCategory(
            members,
            isContainer,
            preservedItemId: item.ObjectId,
            preservedSlot: publishedSlot);
    }

    private void RenumberContainerCategory(
        List<uint> members,
        bool isContainer,
        uint preservedItemId = 0u,
        int preservedSlot = -1)
    {
        int slot = 0;
        for (int i = 0; i < members.Count; i++)
        {
            if (_objects.TryGetValue(members[i], out ClientObject? member)
                && IsContainerListMember(member) == isContainer)
            {
                member.ContainerSlot = member.ObjectId == preservedItemId
                    ? preservedSlot
                    : slot;
                slot++;
            }
        }
    }

    private static bool IsContainerListMember(ClientObject item) =>
        item.ContainerTypeHint != 0u
        || (item.Type & ItemType.Container) != 0
        || item.ItemsCapacity > 0;

    private void PublishContainerContentsChanges(List<uint>? changed)
    {
        if (changed is null || changed.Count == 0)
            return;
        var published = new HashSet<uint>();
        foreach (uint containerId in changed)
        {
            if (!published.Add(containerId))
                continue;
            ContainerContentsReplaced?.Invoke(containerId);
        }
    }

    private void Reindex(ClientObject obj, uint oldContainerId)
    {
        if (oldContainerId != obj.ContainerId && oldContainerId != 0
            && _containerIndex.TryGetValue(oldContainerId, out var oldList))
            oldList.Remove(obj.ObjectId);

        if (obj.ContainerId != 0)
        {
            if (!_containerIndex.TryGetValue(obj.ContainerId, out var list))
                _containerIndex[obj.ContainerId] = list = new List<uint>();
            if (!list.Contains(obj.ObjectId)) list.Add(obj.ObjectId);
            var priorOrder = new Dictionary<uint, int>(list.Count);
            for (int i = 0; i < list.Count; i++)
                priorOrder[list[i]] = i;
            list.Sort((a, b) =>
            {
                int c = SlotOf(a).CompareTo(SlotOf(b));
                return c != 0 ? c : priorOrder[a].CompareTo(priorOrder[b]);
            });
        }
    }

    private static uint EquipmentOwner(ClientObjectPlacement placement)
    {
        if (placement.EquipLocation == EquipMask.None)
            return 0u;
        return placement.WielderId != 0u
            ? placement.WielderId
            : placement.ContainerId;
    }

    private void RemoveEquipmentIndexMembership(uint itemId)
    {
        List<uint>? emptyOwners = null;
        foreach ((uint ownerId, List<uint> placements) in _equipmentIndex)
        {
            placements.Remove(itemId);
            if (placements.Count == 0)
                (emptyOwners ??= new List<uint>()).Add(ownerId);
        }

        if (emptyOwners is null)
            return;
        foreach (uint ownerId in emptyOwners)
            _equipmentIndex.Remove(ownerId);
    }

    private void UpdateEquipmentIndex(
        uint itemId,
        ClientObjectPlacement previous,
        ClientObjectPlacement current)
    {
        if (previous == current)
            return;

        uint previousOwner = EquipmentOwner(previous);
        if (previousOwner != 0u
            && _equipmentIndex.TryGetValue(previousOwner, out List<uint>? oldList))
        {
            oldList.Remove(itemId);
            if (oldList.Count == 0)
                _equipmentIndex.Remove(previousOwner);
        }

        uint currentOwner = EquipmentOwner(current);
        if (currentOwner == 0u)
            return;
        if (!_equipmentIndex.TryGetValue(currentOwner, out List<uint>? currentList))
            _equipmentIndex[currentOwner] = currentList = new List<uint>();
        currentList.Remove(itemId);
        currentList.Insert(0, itemId);
    }

    private int SlotOf(uint guid) =>
        _objects.TryGetValue(guid, out var o) && o.ContainerSlot >= 0
            ? o.ContainerSlot
            : int.MaxValue;

    public IReadOnlyList<uint> GetContents(uint containerId) =>
        _containerIndex.TryGetValue(containerId, out var l)
            ? l.ToArray() : System.Array.Empty<uint>();

    public IReadOnlyList<ClientObject> GetEquippedBy(uint wielderId) =>
        _equipmentIndex.TryGetValue(wielderId, out List<uint>? placements)
            ? placements
                .Select(Get)
                .Where(item => item is not null
                    && item.CurrentlyEquippedLocation != EquipMask.None
                    && (item.WielderId == wielderId || item.ContainerId == wielderId))
                .Cast<ClientObject>()
                .ToArray()
            : Array.Empty<ClientObject>();

    public void ReplaceContents(uint containerId, IReadOnlyList<uint> guids)
    {
        ArgumentNullException.ThrowIfNull(guids);
        if (containerId == 0) return;

        var entries = new ContainerContentEntry[guids.Count];
        for (int i = 0; i < guids.Count; i++)
            entries[i] = new ContainerContentEntry(guids[i], 0u);
        ReplaceContents(containerId, entries);
    }

    public void ReplaceContents(uint containerId, IReadOnlyList<ContainerContentEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (containerId == 0) return;

        var ordered = new List<uint>(entries.Count);
        var added = new List<ClientObject>();
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            ordered.Add(entry.Guid);
            bool existed = _objects.TryGetValue(entry.Guid, out var obj);
            if (!existed || obj is null)
            {
                obj = new ClientObject { ObjectId = entry.Guid };
                RetainObject(obj);
            }
            obj.ContainerTypeHint = entry.ContainerType;
            if (!existed) added.Add(obj);
        }

        _containerIndex[containerId] = ordered;
        foreach (ClientObject item in added)
            ObjectAdded?.Invoke(item);
        ContainerContentsReplaced?.Invoke(containerId);
    }

    public bool StopViewingContents(uint containerId)
    {
        if (containerId == 0u || !_containerIndex.Remove(containerId))
            return false;
        ContainerContentsReplaced?.Invoke(containerId);
        return true;
    }

    public int StopViewingContentsTree(uint rootContainerId)
    {
        if (rootContainerId == 0u)
            return 0;

        var pending = new Stack<uint>();
        var visited = new HashSet<uint>();
        var removed = new List<uint>();
        pending.Push(rootContainerId);
        while (pending.Count != 0)
        {
            uint containerId = pending.Pop();
            if (!visited.Add(containerId)
                || !_containerIndex.TryGetValue(containerId, out List<uint>? contents))
                continue;

            foreach (uint childId in contents)
            {
                if (_containerIndex.ContainsKey(childId))
                    pending.Push(childId);
            }
            _containerIndex.Remove(containerId);
            removed.Add(containerId);
        }

        foreach (uint containerId in removed)
            ContainerContentsReplaced?.Invoke(containerId);
        return removed.Count;
    }

    public void InitializeInventoryManifest(
        uint ownerId,
        IReadOnlyList<ContainerContentEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (ownerId == 0u) return;

        var ordered = new List<uint>(entries.Count);
        var changedContainers = new List<uint>();
        var notifications = new List<(ClientObject Item, bool Existed)>(entries.Count);
        var incoming = new HashSet<uint>();
        for (int i = 0; i < entries.Count; i++)
            incoming.Add(entries[i].Guid);

        if (_containerIndex.TryGetValue(ownerId, out List<uint>? priorInventory))
        {
            foreach (uint priorId in priorInventory.ToArray())
            {
                if (incoming.Contains(priorId)
                    || !_objects.TryGetValue(priorId, out ClientObject? prior)
                    || prior is null
                    || prior.ContainerId != ownerId)
                    continue;

                ClientObjectPlacement previous = ClientObjectPlacement.From(prior);
                prior.ContainerId = 0u;
                prior.ContainerSlot = -1;
                prior.WielderId = 0u;
                prior.CurrentlyEquippedLocation = EquipMask.None;
                prior.Priority = 0u;
                UpdateEquipmentIndex(
                    prior.ObjectId,
                    previous,
                    ClientObjectPlacement.From(prior));
                notifications.Add((prior, true));
            }
        }

        for (int i = 0; i < entries.Count; i++)
        {
            ContainerContentEntry entry = entries[i];
            ordered.Add(entry.Guid);
            bool existed = _objects.TryGetValue(entry.Guid, out ClientObject? obj);
            if (!existed || obj is null)
            {
                obj = new ClientObject { ObjectId = entry.Guid };
                RetainObject(obj);
            }

            ClientObjectPlacement previous = ClientObjectPlacement.From(obj);
            obj.ContainerId = ownerId;
            obj.ContainerSlot = i;
            obj.WielderId = 0u;
            obj.CurrentlyEquippedLocation = EquipMask.None;
            obj.ContainerTypeHint = entry.ContainerType;
            if (RemoveFromOtherContainerIndexes(entry.Guid, ownerId) is { } changed)
                changedContainers.AddRange(changed);
            UpdateEquipmentIndex(entry.Guid, previous, ClientObjectPlacement.From(obj));
            notifications.Add((obj, existed));
        }

        _containerIndex[ownerId] = ordered;
        foreach ((ClientObject item, bool existed) in notifications)
        {
            if (!existed) ObjectAdded?.Invoke(item);
            else ObjectUpdated?.Invoke(item);
        }
        ContainerContentsReplaced?.Invoke(ownerId);
        PublishContainerContentsChanges(changedContainers);
    }

    public int SumCarriedBurden(uint ownerGuid)
    {
        int total = 0;
        foreach (var o in _objects.Values)
            if (IsCarriedBy(o, ownerGuid))
                total += o.Burden;
        return total;
    }

    private bool IsCarriedBy(ClientObject o, uint ownerGuid)
    {
        if (o.WielderId == ownerGuid) return true;
        uint c = o.ContainerId;
        for (int hops = 0; c != 0 && hops < 8; hops++)
        {
            if (c == ownerGuid) return true;
            c = _objects.TryGetValue(c, out var parent) ? parent.ContainerId : 0u;
        }
        return false;
    }

    public void Clear()
    {
        foreach (ClientObject item in _restrictionObservedObjects.ToArray())
            UnbindRestrictionAuthority(item);
        _objects.Clear();
        _containers.Clear();
        _containerIndex.Clear();
        _equipmentIndex.Clear();
        _pendingMoves.Clear();   // B-Drag: drop in-flight optimistic snapshots (a recycled guid must not mis-rollback)
        _pendingUnresolvedPlacements.Clear();   // G4: drop stashed placements for a session that's ending anyway
        Cleared?.Invoke();
    }
}
