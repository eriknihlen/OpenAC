using AcDream.Runtime.Gameplay;

namespace AcDream.App.UI;

/// <summary>
/// The drag half of item placement. Where an item came from on screen — an
/// inventory cell, a paperdoll slot, a shortcut alias — is a fact about the
/// window, so unwrapping the dragged payload happens here; the decision of
/// what the placement means is the runtime owner's.
/// </summary>
public static class ItemDragInteractions
{
    public static bool DropToWorld(
        this RuntimeItemInteraction items,
        ItemDragPayload payload)
        => items.PlaceIn3D(payload, targetGuid: 0u);

    public static bool PlaceIn3D(
        this RuntimeItemInteraction items,
        ItemDragPayload payload,
        uint targetGuid)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(payload);
        return items.PlaceIn3D(
            payload.ObjId,
            isAlias: payload.SourceKind.IsAlias(),
            targetGuid);
    }
}
