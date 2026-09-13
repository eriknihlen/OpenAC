using System.Numerics;
using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Combat;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Spells;
using AcDream.Plugin.Abstractions;
using ImGuiNET;

namespace AcDream.DrakBot.Ui;

/// <summary>
/// The monster list as RynthAi lays it out: one row per rule with a strip
/// of toggle lights (the debuffs, then the war spell shapes and ring),
/// the name, priority, element, second vulnerability and weapon, and a
/// delete button; an add row underneath that takes a typed name or the
/// current target. The Default rule is what unmatched monsters get.
/// </summary>
public sealed class MonstersWindow(BotController controller, IAutomationSurface surface)
{
    private static readonly string[] ElementNames = ["Auto", .. WarSpellNames.Elements];
    private static readonly string[] SecondVulnNames = ["None", .. WarSpellNames.Elements];
    private static readonly Vector4 OnColor = new(0.2f, 0.9f, 0.3f, 1f);
    private static readonly Vector4 OffColor = new(0.25f, 0.28f, 0.32f, 1f);
    private static readonly Vector4 DefaultColor = new(1f, 1f, 0f, 1f);

    private bool _open;
    private string _newName = string.Empty;

    public bool IsOpen
    {
        get => _open;
        set => _open = value;
    }

    public void Draw()
    {
        if (!_open)
            return;
        ImGui.SetNextWindowSize(new Vector2(820f, 440f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Monsters##drakbot", ref _open))
        {
            ImGui.End();
            return;
        }
        CombatSettings combat = controller.Profile.Combat;
        ImGui.TextDisabled("Empty list: fight every hostile with the Combat settings' element. With rules, unmatched monsters use Default or are left alone.");
        ImGui.Separator();
        DrawTable(combat);
        ImGui.Separator();
        DrawAddRow(combat);
        ImGui.Separator();
        float ringRange = combat.RingRangeMeters;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Ring range (m)", ref ringRange, 0f, 20f, "%.0f"))
            controller.Update(p => p with { Combat = p.Combat with { RingRangeMeters = ringRange } });
        ImGui.SameLine();
        int minRing = combat.MinRingTargets;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderInt("Ring at this many", ref minRing, 1, 10))
            controller.Update(p => p with { Combat = p.Combat with { MinRingTargets = minRing } });
        ImGui.End();
    }

    private void DrawTable(CombatSettings combat)
    {
        List<MonsterRule> rules = [.. combat.Monsters];
        if (!ImGui.BeginTable("monsters", 16,
                ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(0f, -110f)))
        {
            return;
        }
        ImGui.TableSetupColumn("F", ImGuiTableColumnFlags.WidthFixed, 16f);
        ImGui.TableSetupColumn("B", ImGuiTableColumnFlags.WidthFixed, 16f);
        ImGui.TableSetupColumn("G", ImGuiTableColumnFlags.WidthFixed, 16f);
        ImGui.TableSetupColumn("I", ImGuiTableColumnFlags.WidthFixed, 16f);
        ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthFixed, 16f);
        ImGui.TableSetupColumn("V", ImGuiTableColumnFlags.WidthFixed, 16f);
        ImGui.TableSetupColumn("A", ImGuiTableColumnFlags.WidthFixed, 16f);
        ImGui.TableSetupColumn("Bl", ImGuiTableColumnFlags.WidthFixed, 18f);
        ImGui.TableSetupColumn("R", ImGuiTableColumnFlags.WidthFixed, 16f);
        ImGui.TableSetupColumn("S", ImGuiTableColumnFlags.WidthFixed, 16f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("P", ImGuiTableColumnFlags.WidthFixed, 40f);
        ImGui.TableSetupColumn("Element", ImGuiTableColumnFlags.WidthFixed, 84f);
        ImGui.TableSetupColumn("Ex Vuln", ImGuiTableColumnFlags.WidthFixed, 84f);
        ImGui.TableSetupColumn("Weapon", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Del", ImGuiTableColumnFlags.WidthFixed, 28f);
        ImGui.TableHeadersRow();

        int deleteIndex = -1;
        bool changed = false;
        for (int i = 0; i < rules.Count; i++)
        {
            MonsterRule rule = rules[i];
            ImGui.TableNextRow();
            ImGui.PushID(i);

            ImGui.TableNextColumn();
            if (Light("F", rule.Fester, "F: Fester (Fester Other)"))
            {
                rules[i] = rule with { Fester = !rule.Fester };
                changed = true;
            }
            ImGui.TableNextColumn();
            if (Light("B", rule.Broadside, "B: Broadside (Missile Weapons Ineptitude)"))
            {
                rules[i] = rule with { Broadside = !rule.Broadside };
                changed = true;
            }
            ImGui.TableNextColumn();
            if (Light("G", rule.GravityWell, "G: Gravity Well (Vulnerability Other, the element-matched one)"))
            {
                rules[i] = rule with { GravityWell = !rule.GravityWell };
                changed = true;
            }
            ImGui.TableNextColumn();
            if (Light("I", rule.Imperil, "I: Imperil (Imperil Other)"))
            {
                rules[i] = rule with { Imperil = !rule.Imperil };
                changed = true;
            }
            ImGui.TableNextColumn();
            if (Light("Y", rule.Yield, "Y: Yield (Magic Yield Other)"))
            {
                rules[i] = rule with { Yield = !rule.Yield };
                changed = true;
            }
            ImGui.TableNextColumn();
            if (Light("V", rule.Vulnerability, "V: Vulnerability (element-matched)"))
            {
                rules[i] = rule with { Vulnerability = !rule.Vulnerability };
                changed = true;
            }
            ImGui.TableNextColumn();
            if (Light("A", rule.Shape == SpellShape.Arc, "A: Arc spells (lobbed)"))
            {
                rules[i] = rule with { Shape = SpellShape.Arc };
                changed = true;
            }
            ImGui.TableNextColumn();
            if (Light("Bl", rule.Shape == SpellShape.Bolt, "Bl: Bolt spells (default)"))
            {
                rules[i] = rule with { Shape = SpellShape.Bolt };
                changed = true;
            }
            ImGui.TableNextColumn();
            if (Light("R", rule.UseRing, "R: Ring spells when they crowd in"))
            {
                rules[i] = rule with { UseRing = !rule.UseRing };
                changed = true;
            }
            ImGui.TableNextColumn();
            if (Light("S", rule.Shape == SpellShape.Streak, "S: Streak spells"))
            {
                rules[i] = rule with { Shape = SpellShape.Streak };
                changed = true;
            }

            ImGui.TableNextColumn();
            if (rule.IsDefault)
                ImGui.TextColored(DefaultColor, rule.Name);
            else
                ImGui.TextUnformatted(rule.Name);

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);
            int priority = rule.Priority;
            if (ImGui.InputInt("##p", ref priority, 0))
            {
                rules[i] = rule with { Priority = Math.Clamp(priority, 0, 99) };
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Priority; 0 never fights this monster");

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);
            int element = Math.Max(0, Array.FindIndex(ElementNames, e => e.Equals(rule.Element, StringComparison.OrdinalIgnoreCase)));
            if (ImGui.Combo("##elem", ref element, ElementNames, ElementNames.Length))
            {
                rules[i] = rule with { Element = ElementNames[element] };
                changed = true;
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);
            int extra = Math.Max(0, Array.FindIndex(SecondVulnNames, e => e.Equals(rule.ExtraVulnerability, StringComparison.OrdinalIgnoreCase)));
            if (ImGui.Combo("##exvuln", ref extra, SecondVulnNames, SecondVulnNames.Length))
            {
                rules[i] = rule with { ExtraVulnerability = extra == 0 ? string.Empty : SecondVulnNames[extra] };
                changed = true;
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1f);
            string weapon = rule.Weapon;
            if (ImGui.InputTextWithHint("##weapon", "profile weapon", ref weapon, 64))
            {
                rules[i] = rule with { Weapon = weapon };
                changed = true;
            }

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("X"))
                deleteIndex = i;
            ImGui.PopID();
        }
        ImGui.EndTable();

        if (deleteIndex >= 0)
        {
            rules.RemoveAt(deleteIndex);
            changed = true;
        }
        if (changed)
            Set(rules);
    }

    private void DrawAddRow(CombatSettings combat)
    {
        ImGui.SetNextItemWidth(260f);
        bool submitted = ImGui.InputTextWithHint("##newmonster", "Monster name...", ref _newName, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ImGui.Button("Add", new Vector2(60f, 22f)) || submitted)
            TryAdd(combat, _newName);
        ImGui.SameLine();
        if (ImGui.Button("Add Target", new Vector2(90f, 22f)))
        {
            Blackboard? board = controller.Engine.LastBoard;
            uint targetId = controller.Engine.Behaviors.OfType<CombatBehavior>().FirstOrDefault()?.CurrentTargetId ?? 0u;
            if (targetId == 0u && board is not null)
                targetId = board.Combat.SelectedObjectId;
            if (board is not null)
            {
                foreach (PluginCombatTarget hostile in board.Hostiles)
                {
                    if (hostile.ObjectId == targetId)
                        TryAdd(combat, hostile.Name);
                }
            }
            if (targetId != 0u && surface.Objects.TryGet(targetId, out PluginWorldObject selected))
                TryAdd(combat, selected.Name);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Adds the current target, or the selected monster");
        ImGui.SameLine();
        ImGui.BeginDisabled(combat.Monsters.Any(rule => rule.IsDefault));
        if (ImGui.Button("Add Default", new Vector2(90f, 22f)))
            TryAdd(combat, MonsterRule.DefaultName);
        ImGui.EndDisabled();
    }

    private void TryAdd(CombatSettings combat, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || combat.Monsters.Any(rule => rule.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return;
        Set([.. combat.Monsters, new MonsterRule { Name = name }]);
        _newName = string.Empty;
    }

    private void Set(List<MonsterRule> rules)
    {
        MonsterRule[] snapshot = [.. rules];
        controller.Update(p => p with { Combat = p.Combat with { Monsters = snapshot } });
    }

    /// <summary>A small square that lights green when on; returns true when clicked.</summary>
    private static bool Light(string id, bool on, string tooltip)
    {
        Vector2 pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, new Vector2(14f, 14f));
        bool clicked = ImGui.IsItemClicked();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        uint fill = ImGui.ColorConvertFloat4ToU32(on ? OnColor : OffColor);
        draw.AddRectFilled(pos + new Vector2(1f, 1f), pos + new Vector2(13f, 13f), fill, 3f);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        return clicked;
    }
}
