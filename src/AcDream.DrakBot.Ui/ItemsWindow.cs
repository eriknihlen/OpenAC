using System.Numerics;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;
using ImGuiNET;

namespace AcDream.DrakBot.Ui;

/// <summary>
/// The items window, after RynthAi's: the weapon each combat style wields,
/// filled in by name or from the item selected in the inventory, whether a
/// bow keeps ammunition wielded, the mana stone tapping switches, and what
/// the character is wielding now for reference.
/// </summary>
public sealed class ItemsWindow(BotController controller, IAutomationSurface surface)
{
    private static readonly Vector4 ColAmber = new(0.91f, 0.70f, 0.20f, 1f);
    private static readonly Vector4 ColMissing = new(1f, 0.35f, 0.35f, 1f);

    private bool _open;

    public bool IsOpen
    {
        get => _open;
        set => _open = value;
    }

    public void Draw()
    {
        if (!_open)
            return;
        ImGui.SetNextWindowSize(new Vector2(480f, 460f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Items##drakbot", ref _open))
        {
            ImGui.End();
            return;
        }
        BotProfile profile = controller.Profile;
        IReadOnlyList<PluginInventoryItem> owned = surface.Items.CaptureOwnedItems();
        string selectedName = SelectedItemName(owned);

        ImGui.TextColored(ColAmber, "Weapons");
        ImGui.Spacing();
        if (ImGui.BeginTable("weapons", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Style", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Item name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableHeadersRow();
            WeaponRow("Melee", profile.Combat.MeleeWeapon, owned, selectedName, value => controller.Update(p => p with { Combat = p.Combat with { MeleeWeapon = value } }));
            WeaponRow("Missile", profile.Combat.MissileWeapon, owned, selectedName, value => controller.Update(p => p with { Combat = p.Combat with { MissileWeapon = value } }));
            WeaponRow("Wand", profile.Combat.Wand, owned, selectedName, value => controller.Update(p => p with { Combat = p.Combat with { Wand = value } }));
            ImGui.EndTable();
        }
        ImGui.TextDisabled(selectedName.Length > 0 ? $"Selected in inventory: {selectedName}" : "(Click an item in the inventory to use 'Use selected')");
        bool ammo = profile.Combat.KeepAmmunition;
        if (ImGui.Checkbox("Keep the bow's ammunition wielded (fletch from bundles when out)", ref ammo))
            controller.Update(p => p with { Combat = p.Combat with { KeepAmmunition = ammo } });
        ImGui.TextDisabled("A monster rule may name another weapon; the Monsters window has that column.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(ColAmber, "Mana Stone Tapping");
        ImGui.Spacing();
        ManaStoneSettings stones = profile.ManaStones;
        bool enabled = stones.Enabled;
        if (ImGui.Checkbox("Enable tapping", ref enabled))
            controller.Update(p => p with { ManaStones = p.ManaStones with { Enabled = enabled } });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use a charged stone on the character when a worn item runs low,\nand drain unworn loot with enough mana into empty stones.");
        ImGui.SameLine(0f, 16f);
        ImGui.Text("Keep up to:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70f);
        int keep = stones.KeepCount;
        if (ImGui.DragInt("##stonekeep", ref keep, 0.2f, 1, 999))
            controller.Update(p => p with { ManaStones = p.ManaStones with { KeepCount = Math.Clamp(keep, 1, 999) } });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Mana stones kept in the pack; stones on corpses are left once this many are carried.");
        if (enabled)
        {
            ImGui.SameLine(0f, 16f);
            ImGui.Text("Min mana to drain:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90f);
            int threshold = stones.TapThresholdMana;
            if (ImGui.DragInt("##tapthresh", ref threshold, 50f, 0, 99999))
                controller.Update(p => p with { ManaStones = p.ManaStones with { TapThresholdMana = Math.Clamp(threshold, 0, 99999) } });
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("An unworn item with at least this much mana is drained into an empty stone (0 never drains).");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(ColAmber, "Wielded now");
        if (ImGui.BeginTable("wielded", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0f, -1f)))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Mana", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Stack", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableHeadersRow();
            foreach (PluginInventoryItem item in owned)
            {
                if (!item.IsEquipped)
                    continue;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.Name);
                ImGui.TableNextColumn();
                if (item.ItemMaximumMana > 0)
                {
                    bool low = item.ItemCurrentMana < item.ItemMaximumMana * 0.25;
                    if (low)
                        ImGui.TextColored(ColMissing, $"{item.ItemCurrentMana}/{item.ItemMaximumMana}");
                    else
                        ImGui.Text($"{item.ItemCurrentMana}/{item.ItemMaximumMana}");
                }
                ImGui.TableNextColumn();
                if (item.StackSize > 1)
                    ImGui.Text(item.StackSize.ToString());
            }
            ImGui.EndTable();
        }
        ImGui.End();
    }

    private static void WeaponRow(string style, string current, IReadOnlyList<PluginInventoryItem> owned, string selectedName, Action<string> set)
    {
        ImGui.TableNextRow();
        ImGui.PushID(style);
        ImGui.TableNextColumn();
        ImGui.Text(style);
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1f);
        string name = current;
        if (ImGui.InputTextWithHint("##name", "(leave the hands alone)", ref name, 64))
            set(name);
        if (current.Length > 0 && !owned.Any(item => item.Name.Contains(current, StringComparison.OrdinalIgnoreCase)))
        {
            ImGui.SameLine();
            ImGui.TextColored(ColMissing, "(not in inventory)");
        }
        ImGui.TableNextColumn();
        ImGui.BeginDisabled(selectedName.Length == 0);
        if (ImGui.Button("Use selected", new Vector2(-1f, 0f)))
            set(selectedName);
        ImGui.EndDisabled();
        ImGui.PopID();
    }

    private string SelectedItemName(IReadOnlyList<PluginInventoryItem> owned)
    {
        uint selected = surface.Combat.Snapshot.SelectedObjectId;
        if (selected == 0u)
            return string.Empty;
        foreach (PluginInventoryItem item in owned)
        {
            if (item.ObjectId == selected)
                return item.Name;
        }
        return string.Empty;
    }
}
