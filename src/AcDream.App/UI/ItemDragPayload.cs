using AcDream.Core.Items;

namespace AcDream.App.UI;

public enum ItemDragSource
{
    Inventory,
    ShortcutBar,
    Equipment,
    Ground,
    /// <summary>A vendor selling-list or house purchase window cell: a pack
    /// item staged there. Lifting it takes it off the list, so letting it go
    /// anywhere moves nothing.</summary>
    Vendor,
    /// <summary>A salvage window cell: a pack item staged there. Lifting it
    /// unstages it, so letting it go anywhere moves nothing.</summary>
    Salvage,
}

public static class ItemDragSourceExtensions
{
    /// <summary>
    /// Whether the drag stands for an item rather than carrying it (a
    /// shortcut, or a vendor, house or salvage list cell). The world and
    /// every item window refuse it, so letting it go anywhere moves nothing.
    /// </summary>
    public static bool IsAlias(this ItemDragSource source)
        => source is ItemDragSource.ShortcutBar or ItemDragSource.Vendor or ItemDragSource.Salvage;
}

public sealed record ItemDragPayload(
    uint ObjId,
    ItemDragSource SourceKind,  // what kind of slot it left
    int SourceSlot,
    UiItemSlot? SourceCell,   // null when the lift came from an image-mapped region, not a slot
    ShortcutEntry? Shortcut = null); // lossless raw entry for shortcut-alias mutation
