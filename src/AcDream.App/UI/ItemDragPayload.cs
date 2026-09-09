using AcDream.Core.Items;

namespace AcDream.App.UI;

public enum ItemDragSource { Inventory, ShortcutBar, Equipment, Ground }

public sealed record ItemDragPayload(
    uint ObjId,
    ItemDragSource SourceKind,  // what kind of slot it left
    int SourceSlot,
    UiItemSlot SourceCell,
    ShortcutEntry? Shortcut = null); // lossless raw entry for shortcut-alias mutation
