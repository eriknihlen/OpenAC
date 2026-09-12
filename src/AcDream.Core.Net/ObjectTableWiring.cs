using AcDream.Core.Items;
using AcDream.Core.Player;

namespace AcDream.Core.Net;

public static class ObjectTableWiring
{
    /// <summary>
    /// Subscribe <paramref name="table"/> to quality, inventory, and property
    /// updates whose freshness is independent of the physics timestamp pack.
    /// </summary>
    public static IDisposable Wire(
        WorldSession session,
        ClientObjectTable table,
        Func<uint>? playerGuid = null,
        LocalPlayerState? localPlayer = null,
        Func<bool>? accepting = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(table);
        var subscriptions = new SubscriptionSet();


        // B-Wire: apply EVERY PropertyInt update on a visible object (0x02CE), not just
        // UiEffects — the server is the authority on object properties. UpdateIntProperty
        // stores it in the bundle and still mirrors UiEffects → the typed Effects field.
        Action<WorldSession.ObjectIntPropertyUpdate> objectIntUpdated = u =>
        {
            if (accepting?.Invoke() == false) return;
            table.UpdateIntProperty(u.Guid, u.Property, u.Value);
        };
        session.ObjectIntPropertyUpdated += objectIntUpdated;
        subscriptions.Add(() => session.ObjectIntPropertyUpdated -= objectIntUpdated);

        Action<WorldSession.PlayerIntPropertyUpdate> playerIntUpdated = u =>
        {
            if (accepting?.Invoke() == false) return;
            if (playerGuid is not null)
                table.UpdateIntProperty(playerGuid(), u.Property, u.Value);
        };
        session.PlayerIntPropertyUpdated += playerIntUpdated;
        subscriptions.Add(() => session.PlayerIntPropertyUpdated -= playerIntUpdated);

        // A re-sent description refreshes an object we already hold — the icon
        // underlay a rend adds, the overlay an imbue adds, a revealed Aetheria.
        // It carries the same fields a first-time description does, so the same
        // ingest applies it; it must not run the arrival of a new object.
        Action<WorldSession.EntitySpawn> descriptionRefreshed = spawn =>
        {
            if (accepting?.Invoke() == false) return;
            table.Ingest(ToWeenieData(spawn));
        };
        session.EntityDescriptionRefreshed += descriptionRefreshed;
        subscriptions.Add(
            () => session.EntityDescriptionRefreshed -= descriptionRefreshed);

        Action<WorldSession.ObjectDataIdPropertyUpdate> objectDataIdUpdated = u =>
        {
            if (accepting?.Invoke() == false) return;
            table.UpdateDataIdProperty(u.Guid, u.Property, u.Value);
        };
        session.ObjectDataIdPropertyUpdated += objectDataIdUpdated;
        subscriptions.Add(
            () => session.ObjectDataIdPropertyUpdated -= objectDataIdUpdated);

        Action<WorldSession.PlayerDataIdPropertyUpdate> playerDataIdUpdated = u =>
        {
            if (accepting?.Invoke() == false) return;
            if (playerGuid is not null)
                table.UpdateDataIdProperty(playerGuid(), u.Property, u.Value);
        };
        session.PlayerDataIdPropertyUpdated += playerDataIdUpdated;
        subscriptions.Add(
            () => session.PlayerDataIdPropertyUpdated -= playerDataIdUpdated);

        Action<WorldSession.ObjectInstanceIdPropertyUpdate> objectInstanceIdUpdated = u =>
        {
            if (accepting?.Invoke() == false) return;
            table.UpdateInstanceIdProperty(u.Guid, u.Property, u.Value);
        };
        session.ObjectInstanceIdPropertyUpdated += objectInstanceIdUpdated;
        subscriptions.Add(
            () => session.ObjectInstanceIdPropertyUpdated -= objectInstanceIdUpdated);

        Action<WorldSession.PlayerInstanceIdPropertyUpdate> playerInstanceIdUpdated = u =>
        {
            if (accepting?.Invoke() == false) return;
            if (playerGuid is not null)
                table.UpdateInstanceIdProperty(playerGuid(), u.Property, u.Value);
        };
        session.PlayerInstanceIdPropertyUpdated += playerInstanceIdUpdated;
        subscriptions.Add(
            () => session.PlayerInstanceIdPropertyUpdated -= playerInstanceIdUpdated);

        Action<WorldSession.PlayerInt64PropertyUpdate> playerInt64Updated = u =>
        {
            if (accepting?.Invoke() == false) return;
            ApplyPlayerInt64PropertyUpdate(
                table, localPlayer, playerGuid?.Invoke() ?? 0u, u);
        };
        session.PlayerInt64PropertyUpdated += playerInt64Updated;
        subscriptions.Add(() => session.PlayerInt64PropertyUpdated -= playerInt64Updated);

        // A saved position slot the server rewrites mid-session — the corpse
        // landmark among them. It is the player's own state, not object-table
        // state, so it goes straight to the local-player owner.
        Action<WorldSession.PlayerPositionUpdate> playerPositionUpdated = u =>
        {
            if (accepting?.Invoke() == false) return;
            localPlayer?.OnPosition(u.PositionType, ToPosition(u.Position));
        };
        session.PlayerPositionUpdated += playerPositionUpdated;
        subscriptions.Add(() => session.PlayerPositionUpdated -= playerPositionUpdated);

        Action<WorldSession.StackSizeUpdate> stackSizeUpdated = u =>
        {
            if (accepting?.Invoke() == false) return;
            table.UpdateStackSize(u.Guid, u.StackSize, u.Value);
        };
        session.StackSizeUpdated += stackSizeUpdated;
        subscriptions.Add(() => session.StackSizeUpdated -= stackSizeUpdated);

        Action<uint> inventoryObjectRemoved = guid =>
        {
            if (accepting?.Invoke() == false) return;
            table.Remove(guid);
        };
        session.InventoryObjectRemoved += inventoryObjectRemoved;
        subscriptions.Add(() => session.InventoryObjectRemoved -= inventoryObjectRemoved);

        return subscriptions;
    }

    public static bool ApplyEntitySpawn(
        ClientObjectTable table,
        WorldSession.EntitySpawn spawn,
        bool replaceGeneration = false,
        Func<bool>? accepting = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (accepting?.Invoke() == false)
            return false;

        WeenieData data = ToWeenieData(spawn);
        if (replaceGeneration)
        {
            return table.ReplaceGeneration(
                data,
                spawn.InstanceSequence,
                accepting) is not null;
        }
        else
            table.Ingest(data);

        return accepting?.Invoke() != false;
    }

    /// <summary>
    /// Applies a true DeleteObject only after the owning runtime accepts the
    /// exact object incarnation. Pickup still removes only the 3-D projection.
    /// </summary>
    public static void ApplyEntityDelete(
        ClientObjectTable table,
        Messages.DeleteObject.Parsed delete)
    {
        table.RemoveLogicalGeneration(delete.Guid, delete.InstanceSequence);
    }

    /// <summary>
    /// The wire carries a saved position as a cell id, an origin, and an
    /// orientation whose real part is written first; the engine's quaternion
    /// takes it last. Both the login snapshot and the mid-session pushes go
    /// through here so they cannot disagree about that ordering.
    /// </summary>
    internal static AcDream.Core.Physics.Position ToPosition(
        Messages.PlayerDescriptionParser.WorldPosition wire) =>
        new(
            wire.LandblockId,
            new System.Numerics.Vector3(wire.X, wire.Y, wire.Z),
            new System.Numerics.Quaternion(wire.Qx, wire.Qy, wire.Qz, wire.Qw));

    internal static void ApplyPlayerInt64PropertyUpdate(
        ClientObjectTable table,
        LocalPlayerState? localPlayer,
        uint playerGuid,
        WorldSession.PlayerInt64PropertyUpdate update)
    {
        if (playerGuid != 0u)
            table.UpdateInt64Property(playerGuid, update.Property, update.Value);
        localPlayer?.OnInt64PropertyUpdate(update.Property, update.Value);
    }

    public static WeenieData ToWeenieData(WorldSession.EntitySpawn s) => new(
        Guid: s.Guid,
        Name: s.Name,
        Type: s.ItemType is { } it ? (ItemType)it : (ItemType?)null,
        WeenieClassId: s.WeenieClassId,
        IconId: s.IconId,
        IconOverlayId: s.IconOverlayId,
        IconUnderlayId: s.IconUnderlayId,
        Effects: s.UiEffects,
        Value: s.Value,
        StackSize: s.StackSize,
        StackSizeMax: s.StackSizeMax,
        Burden: s.Burden,
        ContainerId: s.ContainerId,
        WielderId: s.WielderId,
        ValidLocations: s.ValidLocations,
        CurrentWieldedLocation: s.CurrentWieldedLocation,
        Priority: s.Priority,
        ItemsCapacity: s.ItemsCapacity,
        ContainersCapacity: s.ContainersCapacity,
        HookItemTypes: s.HookItemTypes,
        HookType: s.HookType,
        Structure: s.Structure,
        MaxStructure: s.MaxStructure,
        Workmanship: s.Workmanship,
        Useability: s.Useability,
        TargetType: s.TargetType,
        RadarBlipColor: s.RadarBlipColor,
        RadarBehavior: s.RadarBehavior,
        PublicWeenieBitfield: s.ObjectDescriptionFlags,
        CombatUse: s.CombatUse,
        PluralName: s.PluralName,
        PetOwnerId: s.PetOwnerId,
        AmmoType: s.AmmoType,
        SpellId: s.SpellId,
        CooldownId: s.CooldownId,
        CooldownDuration: s.CooldownDuration,
        MaterialType: s.MaterialType,
        HouseOwnerId: s.HouseOwnerId,
        MonarchId: s.MonarchId,
        Restrictions: s.Restrictions);
}
