using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class ItemManaRechargeTests
{
    [Fact]
    public void PlannerUsesProfiledManaChargeOnMostDepletedWornItem()
    {
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "Mana Charge",
        };
        PluginInventoryItem planTarget = Item(20, "Low Wand") with
        {
            EquippedLocation = 0x01000000u,
            ItemCurrentMana = 10,
            ItemMaximumMana = 100,
        };
        ItemManaRechargePlan plan = Assert.IsType<ItemManaRechargePlan>(
            ItemManaRechargePlanner.Plan(
                [
                    Item(10, "Mana Charge", 0x00080000u) with
                    {
                        ItemCurrentMana = 100,
                    },
                    planTarget,
                    Item(21, "Other Wand") with
                    {
                        EquippedLocation = 0x01000000u,
                        ItemCurrentMana = 20,
                        ItemMaximumMana = 100,
                    },
                ],
                names,
                thresholdPercent: 33));

        Assert.Equal(10u, plan.ChargeObjectId);
        Assert.Equal(20u, plan.TargetObjectId);
        Assert.Equal(10, plan.CurrentMana);
    }

    [Fact]
    public void PlannerRequiresProfileMembershipAndBelowThreshold()
    {
        PluginInventoryItem charge = Item(10, "Mana Charge", 0x00080000u) with
        {
            ItemCurrentMana = 100,
        };
        PluginInventoryItem wand = Item(20, "Wand") with
        {
            EquippedLocation = 0x01000000u,
            ItemCurrentMana = 34,
            ItemMaximumMana = 100,
        };

        Assert.Null(ItemManaRechargePlanner.Plan(
            [charge, wand],
            new HashSet<string>(StringComparer.Ordinal),
            33));
        Assert.Null(ItemManaRechargePlanner.Plan(
            [charge, wand],
            new HashSet<string>(StringComparer.Ordinal) { "Mana Charge" },
            33));
    }

    private static PluginInventoryItem Item(
        uint id,
        string name,
        uint itemType = 0x80u) => new(
            id, 0u, name, itemType, 1u, 0u, 0u, 0u, 0u, 0u, 0u,
            1, 0, 0, 0u, 0, 0, 0u, false, 0d, 0, 0, 0, 0d, 0, 0, 0);
}
