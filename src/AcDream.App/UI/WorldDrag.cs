using System;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;

namespace AcDream.App.UI;

/// <summary>What a drag lifted off the world carries, and the picture that
/// follows the pointer (null draws none).</summary>
public readonly record struct WorldDragStart(object Payload, (uint tex, int w, int h)? Ghost);

/// <summary>The world side of a drag: what the object under a press point
/// lifts. Nothing lifts in combat, nothing wielded lifts, and nothing with a
/// request already in flight lifts. A loose object lifts unless it is stuck in
/// place or a creature: it carries a ground payload, so a drop on a pack picks
/// it up. Your own character lifts too: it carries your main pack, so it can
/// go on the hotbar as a shortcut that targets yourself (healing kits, mana
/// stones).</summary>
public static class WorldDrag
{
    /// <summary>Where the main pack sits in the pack row: lifting the player
    /// off the world is the same lift as one started on that slot, so every
    /// drop target that knows that payload treats it the same.</summary>
    public const int MainPackSlotIndex = 0;

    /// <summary>The slot a lift of a loose object names: an object in the
    /// world sits in no list.</summary>
    public const int WorldSlotIndex = 0;

    /// <summary>The drag ghost's size in pixels, the paperdoll figure's.</summary>
    public const int GhostSize = 32;

    /// <summary>The lift for the object <paramref name="hit"/> under the press
    /// point, or null when it lifts nothing. <paramref name="objects"/>
    /// describes it; <paramref name="isWaiting"/> says whether an object has
    /// a request in flight, which keeps it where it is.</summary>
    public static WorldDragStart? Build(
        uint hit,
        uint playerGuid,
        bool outOfCombat,
        ClientObjectTable objects,
        Func<uint, bool> isWaiting,
        Func<ItemType, uint, uint, uint, uint, uint> iconIds,
        Func<ItemType, uint, uint, uint, uint, uint>? dragIconIds)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(isWaiting);
        ArgumentNullException.ThrowIfNull(iconIds);
        if (!outOfCombat || hit == 0u || objects.Get(hit) is not { } obj || isWaiting(hit))
            return null;
        var flags = (PublicWeenieFlags)(obj.PublicWeenieBitfield ?? 0u);
        bool stuck = (flags & PublicWeenieFlags.Stuck) != 0;
        bool creature = (obj.Type & ItemType.Creature) != 0;
        if (((stuck || creature) && hit != playerGuid) || obj.WielderId != 0u)
            return null;

        if (hit != playerGuid)
        {
            // A loose object carries itself, as a ground lift, so a drop on a
            // pack picks it up.
            var ground = new ItemDragPayload(
                hit, ItemDragSource.Ground, WorldSlotIndex, SourceCell: null);
            return new WorldDragStart(ground, Ghost(
                iconIds, dragIconIds, obj.Type, obj.IconId,
                obj.IconUnderlayId, obj.IconOverlayId, obj.Effects));
        }

        var payload = new ItemDragPayload(
            hit, ItemDragSource.Inventory, MainPackSlotIndex, SourceCell: null);
        // The player has no icon of their own; the pack row draws the main pack
        // with a fixed one, and this lift shows the same picture.
        return new WorldDragStart(payload, Ghost(
            iconIds, dragIconIds, ItemType.Container,
            InventoryController.PlayerPackBaseIcon, 0u, 0u, 0u));
    }

    /// <summary>The picture a lift shows: the dedicated drag icon, else the
    /// item icon; none when neither resolves.</summary>
    private static (uint tex, int w, int h)? Ghost(
        Func<ItemType, uint, uint, uint, uint, uint> iconIds,
        Func<ItemType, uint, uint, uint, uint, uint>? dragIconIds,
        ItemType type,
        uint icon,
        uint underlay,
        uint overlay,
        uint effects)
    {
        uint tex = dragIconIds?.Invoke(type, icon, underlay, overlay, effects) ?? 0u;
        if (tex == 0u)
            tex = iconIds(type, icon, underlay, overlay, effects);
        return tex != 0u ? (tex, GhostSize, GhostSize) : null;
    }
}
