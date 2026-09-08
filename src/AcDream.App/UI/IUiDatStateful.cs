namespace AcDream.App.UI;

public interface IUiDatStateful
{
    uint ActiveRetailStateId { get; }
    bool TrySetRetailState(uint stateId);
}

public static class RetailUiStateIds
{
    public const uint Closed = 11u;
    public const uint Open = 12u;
    public const uint HideDetail = 0x10000006u;
    public const uint ShowDetail = 0x10000007u;
    public const uint ObjectSelected = 0x1000000Bu;
    public const uint StackedItemSelected = 0x1000000Cu;
    public const uint ItemSlotEmpty = 0x1000001Cu;
    public const uint Maximized = 0x10000047u;
    public const uint Minimized = 0x10000048u;
    public const uint LockedUi = 0x10000063u;
    public const uint UnlockedUi = 0x10000064u;

    public const uint Unselected = 0x10000016u;
    public const uint Selected = 0x10000017u;

    public static string StateName(uint stateId)
        => stateId switch
        {
            Closed => "Closed",
            Open => "Open",
            HideDetail => "HideDetail",
            ShowDetail => "ShowDetail",
            ObjectSelected => "ObjectSelected",
            StackedItemSelected => "StackedItemSelected",
            ItemSlotEmpty => "ItemSlot_Empty",
            Maximized => "Maximized",
            Minimized => "Minimized",
            LockedUi => "LockedUI",
            UnlockedUi => "UnlockedUI",
            Unselected => "Unselected",
            Selected => "Selected",
            _ => "",
        };

    public static bool TryStateId(string stateName, out uint stateId)
    {
        stateId = stateName switch
        {
            "Closed" => Closed,
            "Open" => Open,
            "HideDetail" => HideDetail,
            "ShowDetail" => ShowDetail,
            "ObjectSelected" => ObjectSelected,
            "StackedItemSelected" => StackedItemSelected,
            "ItemSlot_Empty" => ItemSlotEmpty,
            "Maximized" => Maximized,
            "Minimized" => Minimized,
            "LockedUI" => LockedUi,
            "UnlockedUI" => UnlockedUi,
            "Unselected" => Unselected,
            "Selected" => Selected,
            _ => 0u,
        };
        return stateId != 0;
    }
}
