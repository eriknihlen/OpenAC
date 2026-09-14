using AcDream.Automation.Items;
using AcDream.Core.Items;

namespace AcDream.App.UI;

public sealed record ItemDragPayload(
    uint ObjId,
    ItemDragSource SourceKind,  // what kind of slot it left
    int SourceSlot,
    UiItemSlot? SourceCell,   // null when the lift came from an image-mapped region, not a slot
    ShortcutEntry? Shortcut = null); // lossless raw entry for shortcut-alias mutation

/// <summary>
/// The drag-payload spellings of the world drop, for the retained UI and
/// the selection controller; the item controller itself takes the id and
/// the source kind so it can live without the UI.
/// </summary>
public static class ItemDragPayloadInteraction
{
    public static bool DropToWorld(
        this ItemInteractionController items,
        ItemDragPayload payload)
        => items.PlaceIn3D(payload, targetGuid: 0u);

    public static bool PlaceIn3D(
        this ItemInteractionController items,
        ItemDragPayload payload,
        uint targetGuid)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(payload);

        return items.PlaceIn3D(payload.ObjId, payload.SourceKind, targetGuid);
    }
}
