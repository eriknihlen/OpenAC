namespace AcDream.App.UI;

public enum ItemDragAcceptance
{
    None,
    Accept,
    Reject,
}

public interface IItemListDragHandler
{
    void OnDragLift(UiItemList sourceList, UiItemSlot sourceCell, ItemDragPayload payload);

    ItemDragAcceptance OnDragOver(UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload);

    void HandleDropRelease(UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload);
}
