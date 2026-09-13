using System.Numerics;
using AcDream.DrakBot.Combat;
using AcDream.DrakBot.Loot;
using AcDream.DrakBot.Spells;
using AcDream.DrakBot.Meta;
using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using ImGuiNET;

namespace AcDream.DrakBot.Ui;

/// <summary>
/// Profile editing, one tab per subsystem. Every widget reads the live
/// profile and writes back through the controller, so a change takes effect
/// on the next tick; "Save" persists it under the profile's name.
/// </summary>
public sealed class BotSettingsWindow(BotController controller)
{
    private static readonly string[] StyleNames = ["Melee", "Missile", "Magic"];
    private static readonly string[] HeightNames = ["High", "Medium", "Low"];
    private static readonly string[] RouteModeNames = ["Loop", "Ping-pong", "Once"];
    private static readonly string[] WarSpellPathNames = ["Straight (bolts, streaks)", "Arc (lobbed)"];

    private bool _open;
    private string _profileName = string.Empty;
    private string _metaName = string.Empty;
    private int _selectedMonster = -1;
    private string _monsterName = string.Empty;
    private string _petDevice = string.Empty;
    private string _metaExpression = string.Empty;
    private string _metaResult = string.Empty;
    private string _newBuff = string.Empty;
    private int _selectedBuff = -1;
    private string _priorityNames = string.Empty;
    private string _ignoreNames = string.Empty;
    private string _lastProfileName = string.Empty;
    private int _selectedRule = -1;

    public bool IsOpen
    {
        get => _open;
        set => _open = value;
    }

    public void Draw()
    {
        if (!_open)
            return;
        ImGui.SetNextWindowSize(new Vector2(440f, 420f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("DrakBot settings", ref _open))
        {
            ImGui.End();
            return;
        }

        BotProfile profile = controller.Profile;
        SyncScratch(profile);

        ImGui.SetNextItemWidth(200f);
        ImGui.InputText("Profile name", ref _profileName, 64);
        ImGui.SameLine();
        if (ImGui.Button("Save"))
            controller.SaveProfile(string.IsNullOrWhiteSpace(_profileName) ? null : _profileName.Trim());
        ImGui.SameLine();
        if (ImGui.Button("Reset"))
            controller.ResetProfile();

        if (ImGui.BeginTabBar("settings-tabs"))
        {
            if (ImGui.BeginTabItem("Recharge"))
            {
                DrawRecharge(profile.Vitals);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Combat"))
            {
                DrawCombat(profile.Combat);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Monsters"))
            {
                DrawMonsters(profile.Combat);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Buffs"))
            {
                DrawBuffs(profile.Buffs);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Loot"))
            {
                DrawLoot(profile.Loot);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Navigation"))
            {
                DrawNavigation(profile.Navigation);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Meta"))
            {
                DrawMeta(profile.Meta);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    private void SyncScratch(BotProfile profile)
    {
        if (profile.Name == _lastProfileName)
            return;
        _lastProfileName = profile.Name;
        _profileName = profile.Name;
        _priorityNames = string.Join(", ", profile.Combat.PriorityNames);
        _ignoreNames = string.Join(", ", profile.Combat.IgnoreNames);
    }

    private void DrawRecharge(VitalSettings vitals)
    {
        ImGui.TextDisabled("Self vitals (%)");
        int heal = (int)Math.Round(vitals.HealBelow * 100d);
        if (ImGui.SliderInt("Heal at", ref heal, 1, 99))
            controller.Update(p => p with { Vitals = p.Vitals with { HealBelow = heal / 100d } });
        int stamina = (int)Math.Round(vitals.StaminaBelow * 100d);
        if (ImGui.SliderInt("Re-stam at", ref stamina, 1, 99))
            controller.Update(p => p with { Vitals = p.Vitals with { StaminaBelow = stamina / 100d } });
        int mana = (int)Math.Round(vitals.ManaBelow * 100d);
        if (ImGui.SliderInt("Get mana at", ref mana, 1, 99))
            controller.Update(p => p with { Vitals = p.Vitals with { ManaBelow = mana / 100d } });

        ImGui.Spacing();
        ImGui.TextDisabled("Top off with nothing in range (%; 0 = only the thresholds above)");
        int idleHeal = (int)Math.Round(vitals.IdleHealthBelow * 100d);
        if (ImGui.SliderInt("Health to", ref idleHeal, 0, 100))
            controller.Update(p => p with { Vitals = p.Vitals with { IdleHealthBelow = idleHeal / 100d } });
        int idleStamina = (int)Math.Round(vitals.IdleStaminaBelow * 100d);
        if (ImGui.SliderInt("Stamina to", ref idleStamina, 0, 100))
            controller.Update(p => p with { Vitals = p.Vitals with { IdleStaminaBelow = idleStamina / 100d } });
        int idleMana = (int)Math.Round(vitals.IdleManaBelow * 100d);
        if (ImGui.SliderInt("Mana to", ref idleMana, 0, 100))
            controller.Update(p => p with { Vitals = p.Vitals with { IdleManaBelow = idleMana / 100d } });

        ImGui.Spacing();
        ImGui.TextDisabled("Fellows");
        int healOthers = (int)Math.Round(vitals.HealFellowsBelow * 100d);
        if (ImGui.SliderInt("Heal fellows at (%, 0 = never)", ref healOthers, 0, 99))
            controller.Update(p => p with { Vitals = p.Vitals with { HealFellowsBelow = healOthers / 100d } });
        float fellowRange = vitals.HealFellowsRangeMeters;
        if (ImGui.SliderFloat("Within (m)", ref fellowRange, 3f, 40f, "%.0f"))
            controller.Update(p => p with { Vitals = p.Vitals with { HealFellowsRangeMeters = fellowRange } });
        string healOther = vitals.HealOtherSpell;
        if (ImGui.InputText("Heal fellow spell", ref healOther, 64))
            controller.Update(p => p with { Vitals = p.Vitals with { HealOtherSpell = healOther } });

        ImGui.Spacing();
        ImGui.TextDisabled("Spells (game names, tier picked automatically)");
        string healSpell = vitals.HealSpell;
        if (ImGui.InputText("Heal", ref healSpell, 64))
            controller.Update(p => p with { Vitals = p.Vitals with { HealSpell = healSpell } });
        string staminaSpell = vitals.StaminaSpell;
        if (ImGui.InputText("Stamina", ref staminaSpell, 64))
            controller.Update(p => p with { Vitals = p.Vitals with { StaminaSpell = staminaSpell } });
        string manaSpell = vitals.ManaSpell;
        if (ImGui.InputText("Mana", ref manaSpell, 64))
            controller.Update(p => p with { Vitals = p.Vitals with { ManaSpell = manaSpell } });
        bool kits = vitals.UseHealingKits;
        if (ImGui.Checkbox("Use healing kits when no heal spell is castable", ref kits))
            controller.Update(p => p with { Vitals = p.Vitals with { UseHealingKits = kits } });
    }

    private void DrawCombat(CombatSettings combat)
    {
        bool enabled = combat.Enabled;
        if (ImGui.Checkbox("Enable combat", ref enabled))
            controller.Update(p => p with { Combat = p.Combat with { Enabled = enabled } });

        int style = (int)combat.Style;
        if (ImGui.Combo("Style", ref style, StyleNames, StyleNames.Length))
            controller.Update(p => p with { Combat = p.Combat with { Style = (CombatStyle)style } });

        int height = (int)combat.Height - 1;
        if (ImGui.Combo("Attack height", ref height, HeightNames, HeightNames.Length))
            controller.Update(p => p with { Combat = p.Combat with { Height = (AttackHeight)(height + 1) } });

        int power = (int)Math.Round(combat.Power * 100f);
        if (ImGui.SliderInt("Power %", ref power, 0, 100))
            controller.Update(p => p with { Combat = p.Combat with { Power = power / 100f } });

        float engage = combat.EngageDistance;
        if (ImGui.SliderFloat("Engage distance (m)", ref engage, 3f, 60f, "%.0f"))
            controller.Update(p => p with { Combat = p.Combat with { EngageDistance = engage } });

        string element = combat.ElementKeyword;
        if (ImGui.InputText("War spell element", ref element, 32))
            controller.Update(p => p with { Combat = p.Combat with { ElementKeyword = element } });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Part of the spell name, e.g. Flame or Frost. Empty means any.");

        bool peace = combat.LeaveCombatWhenIdle;
        if (ImGui.Checkbox("Peace mode when idle", ref peace))
            controller.Update(p => p with { Combat = p.Combat with { LeaveCombatWhenIdle = peace } });

        ImGui.Spacing();
        ImGui.TextDisabled("Backing off (ranged styles)");
        float backOffWhen = combat.BackOffWhenWithinMeters;
        if (ImGui.SliderFloat("Step back when a hostile is within (m; 0 = never)", ref backOffWhen, 0f, 8f, "%.1f"))
            controller.Update(p => p with { Combat = p.Combat with { BackOffWhenWithinMeters = backOffWhen } });
        float backOffTo = combat.BackOffToMeters;
        if (ImGui.SliderFloat("Back off to (m)", ref backOffTo, 2f, 20f, "%.0f"))
            controller.Update(p => p with { Combat = p.Combat with { BackOffToMeters = backOffTo } });

        ImGui.Spacing();
        ImGui.TextDisabled("Weapons (by name; empty leaves the hands alone)");
        string melee = combat.MeleeWeapon;
        if (ImGui.InputText("Melee weapon", ref melee, 64))
            controller.Update(p => p with { Combat = p.Combat with { MeleeWeapon = melee } });
        string missile = combat.MissileWeapon;
        if (ImGui.InputText("Missile weapon", ref missile, 64))
            controller.Update(p => p with { Combat = p.Combat with { MissileWeapon = missile } });
        string wand = combat.Wand;
        if (ImGui.InputText("Wand", ref wand, 64))
            controller.Update(p => p with { Combat = p.Combat with { Wand = wand } });
        bool ammo = combat.KeepAmmunition;
        if (ImGui.Checkbox("Keep the bow's ammunition wielded", ref ammo))
            controller.Update(p => p with { Combat = p.Combat with { KeepAmmunition = ammo } });

        ImGui.Spacing();
        ImGui.TextDisabled("Reach");
        float meleeRange = combat.MeleeRangeMeters;
        if (ImGui.SliderFloat("Melee reach (m)", ref meleeRange, 1f, 6f, "%.1f"))
            controller.Update(p => p with { Combat = p.Combat with { MeleeRangeMeters = meleeRange } });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A melee target farther than this is walked up to first.");
        float approachRange = combat.ApproachRangeMeters;
        if (ImGui.SliderFloat("Approach to (m)", ref approachRange, 1f, 25f, "%.0f"))
            controller.Update(p => p with { Combat = p.Combat with { ApproachRangeMeters = approachRange } });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A ranged target with no line of sight is walked toward until it is this close or the path clears.");
        int approachTimeout = (int)Math.Round(combat.ApproachTimeoutSeconds);
        if (ImGui.SliderInt("Give up walking after (s)", ref approachTimeout, 3, 60))
            controller.Update(p => p with { Combat = p.Combat with { ApproachTimeoutSeconds = approachTimeout } });

        ImGui.Spacing();
        DrawLineOfSight(combat.LineOfSight);

        ImGui.Spacing();
        ImGui.TextDisabled("Monsters (comma separated name fragments)");
        if (ImGui.InputText("Attack first", ref _priorityNames, 256, ImGuiInputTextFlags.EnterReturnsTrue)
            || ImGui.IsItemDeactivatedAfterEdit())
        {
            IReadOnlyList<string> names = SplitNames(_priorityNames);
            controller.Update(p => p with { Combat = p.Combat with { PriorityNames = names } });
        }
        if (ImGui.InputText("Never attack", ref _ignoreNames, 256, ImGuiInputTextFlags.EnterReturnsTrue)
            || ImGui.IsItemDeactivatedAfterEdit())
        {
            IReadOnlyList<string> names = SplitNames(_ignoreNames);
            controller.Update(p => p with { Combat = p.Combat with { IgnoreNames = names } });
        }
    }

    private static readonly string[] ElementNames = ["Auto", .. WarSpellNames.Elements];
    private static readonly string[] ShapeNames = ["Bolt", "Arc", "Streak"];

    /// <summary>The VTank-style monster list: one rule per kind of monster, a Default for the rest.</summary>
    private void DrawMonsters(CombatSettings combat)
    {
        ImGui.TextDisabled("Empty list: fight every hostile with the Combat tab's element. With rules, unmatched monsters use Default or are left alone.");
        List<MonsterRule> rules = [.. combat.Monsters];

        ImGui.SetNextItemWidth(200f);
        ImGui.InputText("##newmonster", ref _monsterName, 64);
        ImGui.SameLine();
        if (ImGui.Button("Add") && !string.IsNullOrWhiteSpace(_monsterName))
        {
            rules.Add(new MonsterRule { Name = _monsterName.Trim() });
            _monsterName = string.Empty;
            _selectedMonster = rules.Count - 1;
            SetMonsters(rules);
        }
        ImGui.SameLine();
        if (ImGui.Button("Add Default") && !rules.Any(r => r.IsDefault))
        {
            rules.Add(new MonsterRule { Name = MonsterRule.DefaultName });
            _selectedMonster = rules.Count - 1;
            SetMonsters(rules);
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(_selectedMonster < 0 || _selectedMonster >= rules.Count);
        if (ImGui.Button("Remove"))
        {
            rules.RemoveAt(_selectedMonster);
            _selectedMonster = -1;
            SetMonsters(rules);
        }
        ImGui.EndDisabled();

        if (ImGui.BeginTable("monsters", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY, new Vector2(-1f, 140f)))
        {
            ImGui.TableSetupColumn("Monster", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Pri", ImGuiTableColumnFlags.WidthFixed, 34f);
            ImGui.TableSetupColumn("Element", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Spell", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableHeadersRow();
            for (int index = 0; index < rules.Count; index++)
            {
                MonsterRule rule = rules[index];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                if (ImGui.Selectable($"{rule.Name}##m{index}", _selectedMonster == index, ImGuiSelectableFlags.SpanAllColumns))
                    _selectedMonster = index;
                ImGui.TableSetColumnIndex(1);
                ImGui.Text(rule.Priority == 0 ? "off" : rule.Priority.ToString());
                ImGui.TableSetColumnIndex(2);
                ImGui.Text(rule.Element);
                ImGui.TableSetColumnIndex(3);
                ImGui.Text(rule.Shape + (rule.UseRing ? "/Ring" : string.Empty));
            }
            ImGui.EndTable();
        }

        if (_selectedMonster >= 0 && _selectedMonster < rules.Count)
        {
            MonsterRule rule = rules[_selectedMonster];
            void Set(MonsterRule changed)
            {
                rules[_selectedMonster] = changed;
                SetMonsters(rules);
            }

            ImGui.Separator();
            int priority = rule.Priority;
            if (ImGui.SliderInt("Priority (0 = never fight)", ref priority, 0, 10))
                Set(rule with { Priority = priority });
            int element = Math.Max(0, Array.FindIndex(ElementNames, e => e.Equals(rule.Element, StringComparison.OrdinalIgnoreCase)));
            if (ImGui.Combo("Element", ref element, ElementNames, ElementNames.Length))
                Set(rule with { Element = ElementNames[element] });
            int shape = (int)rule.Shape;
            if (ImGui.Combo("War spell", ref shape, ShapeNames, ShapeNames.Length))
                Set(rule with { Shape = (SpellShape)shape });
            bool ring = rule.UseRing;
            if (ImGui.Checkbox("Ring when they crowd in", ref ring))
                Set(rule with { UseRing = ring });

            ImGui.TextDisabled("Debuffs landed first");
            bool imperil = rule.Imperil;
            if (ImGui.Checkbox("Imperil", ref imperil)) Set(rule with { Imperil = imperil });
            ImGui.SameLine();
            bool vuln = rule.Vulnerability;
            if (ImGui.Checkbox("Vulnerability", ref vuln)) Set(rule with { Vulnerability = vuln });
            ImGui.SameLine();
            bool fester = rule.Fester;
            if (ImGui.Checkbox("Fester", ref fester)) Set(rule with { Fester = fester });
            bool yield = rule.Yield;
            if (ImGui.Checkbox("Yield", ref yield)) Set(rule with { Yield = yield });
            ImGui.SameLine();
            bool broadside = rule.Broadside;
            if (ImGui.Checkbox("Broadside", ref broadside)) Set(rule with { Broadside = broadside });
            ImGui.SameLine();
            bool gravity = rule.GravityWell;
            if (ImGui.Checkbox("Gravity Well", ref gravity)) Set(rule with { GravityWell = gravity });
            int extra = Math.Max(0, Array.FindIndex(ElementNames, e => e.Equals(rule.ExtraVulnerability, StringComparison.OrdinalIgnoreCase)));
            if (ImGui.Combo("Second vulnerability", ref extra, ElementNames, ElementNames.Length))
                Set(rule with { ExtraVulnerability = extra == 0 ? string.Empty : ElementNames[extra] });
            string weapon = rule.Weapon;
            if (ImGui.InputText("Weapon for this monster", ref weapon, 64))
                Set(rule with { Weapon = weapon });
        }

        ImGui.Separator();
        float ringRange = combat.RingRangeMeters;
        if (ImGui.SliderFloat("Ring range (m)", ref ringRange, 0f, 20f, "%.0f"))
            controller.Update(p => p with { Combat = p.Combat with { RingRangeMeters = ringRange } });
        int minRing = combat.MinRingTargets;
        if (ImGui.SliderInt("Ring at this many", ref minRing, 1, 10))
            controller.Update(p => p with { Combat = p.Combat with { MinRingTargets = minRing } });

        ImGui.Separator();
        DrawPets(controller.Profile.Pets);
        ImGui.Separator();
        DrawManaStones(controller.Profile.ManaStones);
    }

    private void DrawManaStones(ManaStoneSettings stones)
    {
        ImGui.TextDisabled("Mana stones");
        bool enabled = stones.Enabled;
        if (ImGui.Checkbox("Recharge worn items from mana stones", ref enabled))
            controller.Update(p => p with { ManaStones = p.ManaStones with { Enabled = enabled } });
        int threshold = stones.TapThresholdMana;
        if (ImGui.SliderInt("Drain loot with at least this mana (0 = never)", ref threshold, 0, 20000))
            controller.Update(p => p with { ManaStones = p.ManaStones with { TapThresholdMana = threshold } });
        int keep = stones.KeepCount;
        if (ImGui.SliderInt("Stones to keep", ref keep, 1, 50))
            controller.Update(p => p with { ManaStones = p.ManaStones with { KeepCount = keep } });
    }

    private void DrawPets(PetSettings pets)
    {
        ImGui.TextDisabled("Combat pets");
        bool enabled = pets.Enabled;
        if (ImGui.Checkbox("Summon pets", ref enabled))
            controller.Update(p => p with { Pets = p.Pets with { Enabled = enabled } });
        ImGui.SameLine();
        bool refill = pets.RefillFromSpirits;
        if (ImGui.Checkbox("Refill from Encapsulated Spirits", ref refill))
            controller.Update(p => p with { Pets = p.Pets with { RefillFromSpirits = refill } });
        int minimum = pets.MinimumHostiles;
        if (ImGui.SliderInt("Summon at this many hostiles", ref minimum, 1, 10))
            controller.Update(p => p with { Pets = p.Pets with { MinimumHostiles = minimum } });
        float range = pets.RangeMeters;
        if (ImGui.SliderFloat("Within (m)", ref range, 3f, 40f, "%.0f"))
            controller.Update(p => p with { Pets = p.Pets with { RangeMeters = range } });
        ImGui.SetNextItemWidth(220f);
        bool submitted = ImGui.InputText("##petdevice", ref _petDevice, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Add essence") || submitted) && !string.IsNullOrWhiteSpace(_petDevice))
        {
            string name = _petDevice.Trim();
            controller.Update(p => p with { Pets = p.Pets with { Devices = [.. p.Pets.Devices, name] } });
            _petDevice = string.Empty;
        }
        for (int index = 0; index < pets.Devices.Count; index++)
        {
            ImGui.Text(pets.Devices[index]);
            ImGui.SameLine();
            if (ImGui.SmallButton($"x##pet{index}"))
            {
                int remove = index;
                controller.Update(p => p with { Pets = p.Pets with { Devices = p.Pets.Devices.Where((_, i) => i != remove).ToArray() } });
                break;
            }
        }
    }

    private void SetMonsters(List<MonsterRule> rules)
    {
        MonsterRule[] snapshot = [.. rules];
        controller.Update(p => p with { Combat = p.Combat with { Monsters = snapshot } });
    }

    private void DrawLineOfSight(LineOfSightSettings los)
    {
        ImGui.TextDisabled("Line of sight and obstacle sense");
        bool enabled = los.Enabled;
        if (ImGui.Checkbox("Check the path before each shot", ref enabled))
            UpdateLineOfSight(p => p with { Enabled = enabled });
        if (!enabled)
            return;

        int path = (int)los.WarSpellPath;
        if (ImGui.Combo("War spells fly", ref path, WarSpellPathNames, WarSpellPathNames.Length))
            UpdateLineOfSight(p => p with { WarSpellPath = (WarSpellPath)path });
        if (los.WarSpellPath == WarSpellPath.Arc)
        {
            bool straightIndoors = los.StraightPathIndoors;
            if (ImGui.Checkbox("Test the flat path indoors (arcs hit ceilings)", ref straightIndoors))
                UpdateLineOfSight(p => p with { StraightPathIndoors = straightIndoors });
            float arcSpeed = los.ArcLaunchSpeed;
            if (ImGui.SliderFloat("Arc launch speed (m/s)", ref arcSpeed, 0f, 60f, arcSpeed <= 0f ? "client default" : "%.0f"))
                UpdateLineOfSight(p => p with { ArcLaunchSpeed = arcSpeed });
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Slower launches lob higher. Zero uses the client's own figure.");
        }
        float missileSpeed = los.MissileLaunchSpeed;
        if (ImGui.SliderFloat("Missile launch speed (m/s)", ref missileSpeed, 0f, 80f, missileSpeed <= 0f ? "client default" : "%.0f"))
            UpdateLineOfSight(p => p with { MissileLaunchSpeed = missileSpeed });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Bows and crossbows shoot flatter than atlatls and thrown weapons. Zero uses the client's own figure.");

        int strikes = los.BlacklistStrikes;
        if (ImGui.SliderInt("Skip a target after N blocked checks", ref strikes, 1, 10))
            UpdateLineOfSight(p => p with { BlacklistStrikes = strikes });
        int blacklist = (int)Math.Round(los.BlacklistSeconds);
        if (ImGui.SliderInt("Skip it for (s)", ref blacklist, 5, 300))
            UpdateLineOfSight(p => p with { BlacklistSeconds = blacklist });

        bool walk = los.CheckWalkPath;
        if (ImGui.Checkbox("Check the ground before walking to a target", ref walk))
            UpdateLineOfSight(p => p with { CheckWalkPath = walk });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Walks the character's own collision toward the target first; steers around what blocks it and skips targets nothing reaches. Melee too.");
        if (walk)
        {
            float lookahead = los.WalkLookaheadMeters;
            if (ImGui.SliderFloat("Steering look-ahead (m)", ref lookahead, 1f, 12f, "%.0f"))
                UpdateLineOfSight(p => p with { WalkLookaheadMeters = lookahead });
        }

        bool debug = los.ShowDebugSamples;
        if (ImGui.Checkbox("Draw the swept path", ref debug))
            UpdateLineOfSight(p => p with { ShowDebugSamples = debug });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Asks the client to mark each collision check in the world, shots and walks alike: green where it passes, red where it stops.");
    }

    private void UpdateLineOfSight(Func<LineOfSightSettings, LineOfSightSettings> change) =>
        controller.Update(p => p with { Combat = p.Combat with { LineOfSight = change(p.Combat.LineOfSight) } });

    private void DrawBuffs(BuffSettings buffs)
    {
        bool enabled = buffs.Enabled;
        if (ImGui.Checkbox("Enable buffing", ref enabled))
            controller.Update(p => p with { Buffs = p.Buffs with { Enabled = enabled } });

        int rebuff = (int)buffs.RebuffWhenRemainingSeconds;
        if (ImGui.SliderInt("Rebuff with (seconds left)", ref rebuff, 5, 600))
            controller.Update(p => p with { Buffs = p.Buffs with { RebuffWhenRemainingSeconds = rebuff } });

        bool weapon = buffs.BuffWeapon;
        if (ImGui.Checkbox("Weapon auras", ref weapon))
            controller.Update(p => p with { Buffs = p.Buffs with { BuffWeapon = weapon } });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join(", ", buffs.WeaponSpells) + " on the wielded weapon");
        ImGui.SameLine();
        bool armor = buffs.BuffArmor;
        if (ImGui.Checkbox("Armor banes", ref armor))
            controller.Update(p => p with { Buffs = p.Buffs with { BuffArmor = armor } });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join(", ", buffs.ArmorSpells) + " on every equipped piece of armor");

        ImGui.Spacing();
        ImGui.TextDisabled("Buffs to keep up, in cast order");
        if (ImGui.BeginListBox("##buffs", new Vector2(-1f, 180f)))
        {
            for (int index = 0; index < buffs.Spells.Count; index++)
            {
                if (ImGui.Selectable($"{buffs.Spells[index]}##{index}", _selectedBuff == index))
                    _selectedBuff = index;
            }
            ImGui.EndListBox();
        }

        ImGui.SetNextItemWidth(220f);
        bool submitted = ImGui.InputText("##newbuff", ref _newBuff, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Add") || submitted) && !string.IsNullOrWhiteSpace(_newBuff))
        {
            string name = _newBuff.Trim();
            controller.Update(p => p with { Buffs = p.Buffs with { Spells = [.. p.Buffs.Spells, name] } });
            _newBuff = string.Empty;
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(_selectedBuff < 0 || _selectedBuff >= buffs.Spells.Count);
        if (ImGui.Button("Remove"))
        {
            int remove = _selectedBuff;
            controller.Update(p => p with
            {
                Buffs = p.Buffs with { Spells = p.Buffs.Spells.Where((_, i) => i != remove).ToArray() },
            });
            _selectedBuff = -1;
        }
        ImGui.SameLine();
        if (ImGui.Button("Up") && _selectedBuff > 0)
        {
            int from = _selectedBuff;
            controller.Update(p => p with { Buffs = p.Buffs with { Spells = Swap(p.Buffs.Spells, from, from - 1) } });
            _selectedBuff--;
        }
        ImGui.SameLine();
        if (ImGui.Button("Down") && _selectedBuff >= 0 && _selectedBuff < buffs.Spells.Count - 1)
        {
            int from = _selectedBuff;
            controller.Update(p => p with { Buffs = p.Buffs with { Spells = Swap(p.Buffs.Spells, from, from + 1) } });
            _selectedBuff++;
        }
        ImGui.EndDisabled();
    }

    private void DrawLoot(LootSettings loot)
    {
        bool enabled = loot.Enabled;
        if (ImGui.Checkbox("Enable looting", ref enabled))
            controller.Update(p => p with { Loot = p.Loot with { Enabled = enabled } });

        float scan = loot.ScanDistance;
        if (ImGui.SliderFloat("Corpse range (m)", ref scan, 2f, 40f, "%.0f"))
            controller.Update(p => p with { Loot = p.Loot with { ScanDistance = scan } });

        float timeout = (float)loot.StepTimeoutSeconds;
        if (ImGui.SliderFloat("Step timeout (s)", ref timeout, 2f, 20f, "%.0f"))
            controller.Update(p => p with { Loot = p.Loot with { StepTimeoutSeconds = timeout } });

        string utl = loot.UtlProfile;
        if (ImGui.InputText("VTank .utl profile", ref utl, 128))
            controller.Update(p => p with { Loot = p.Loot with { UtlProfile = utl } });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A loot profile by name from the VTank profiles folder; when set it decides instead of the rules below.");

        ImGui.Spacing();
        ImGui.TextDisabled("Rules, first match wins");
        IReadOnlyList<LootRule> rules = loot.Rules.Rules;
        if (ImGui.BeginTable("rules", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH, new Vector2(-1f, 160f)))
        {
            ImGui.TableSetupColumn("Rule", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("Needs id", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableHeadersRow();
            for (int index = 0; index < rules.Count; index++)
            {
                LootRule rule = rules[index];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                if (ImGui.Selectable($"{rule.Name}##rule{index}", _selectedRule == index, ImGuiSelectableFlags.SpanAllColumns))
                    _selectedRule = index;
                ImGui.TableSetColumnIndex(1);
                ImGui.Text(rule.Action.ToString());
                ImGui.TableSetColumnIndex(2);
                ImGui.Text(rule.NeedsAppraisal ? "yes" : "no");
            }
            ImGui.EndTable();
        }
        ImGui.BeginDisabled(_selectedRule < 0 || _selectedRule >= rules.Count);
        if (ImGui.Button("Remove rule"))
        {
            int remove = _selectedRule;
            controller.Update(p => p with
            {
                Loot = p.Loot with
                {
                    Rules = p.Loot.Rules with { Rules = p.Loot.Rules.Rules.Where((_, i) => i != remove).ToArray() },
                },
            });
            _selectedRule = -1;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("Edit rules in the profile JSON for now.");
    }

    private void DrawMeta(MetaOptions options)
    {
        MetaEngine? meta = controller.Meta;
        if (meta is null)
        {
            ImGui.TextDisabled("No meta engine on this host.");
            return;
        }

        bool enabled = options.Enabled;
        if (ImGui.Checkbox("Run the meta", ref enabled))
            meta.Enabled = enabled;
        ImGui.SameLine();
        bool debug = options.Debug;
        if (ImGui.Checkbox("Echo fired rules", ref debug))
            meta.Debug = debug;

        if (_metaName.Length == 0 && options.Name.Length > 0)
            _metaName = options.Name;
        ImGui.SetNextItemWidth(220f);
        ImGui.InputText("##metaname", ref _metaName, 128);
        ImGui.SameLine();
        if (ImGui.Button("Load") && !string.IsNullOrWhiteSpace(_metaName))
            meta.LoadByName(_metaName.Trim());
        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            meta.Clear();
            controller.Update(p => p with { Meta = p.Meta with { Name = string.Empty } });
        }
        ImGui.TextDisabled("A .af or .met by name from the VTank profiles folder; the profile remembers it.");

        ImGui.Separator();
        if (meta.Rules.Count == 0)
        {
            ImGui.TextDisabled("No meta loaded.");
        }
        else
        {
            ImGui.Text($"'{meta.MetaName}': {meta.Rules.Count} rules, state {meta.CurrentState} for {meta.SecondsInState:0}s"
                + (meta.StackDepth > 0 ? $", stack {meta.StackDepth}" : string.Empty)
                + (meta.WatchdogActive ? ", watchdog armed" : string.Empty));
            ImGui.SetNextItemWidth(220f);
            if (ImGui.BeginCombo("State", meta.CurrentState))
            {
                foreach (string state in meta.StateNames())
                {
                    if (ImGui.Selectable(state, state.Equals(meta.CurrentState, StringComparison.OrdinalIgnoreCase)))
                        meta.SetState(state);
                }
                ImGui.EndCombo();
            }
            if (meta.LastFired.Length > 0)
                ImGui.TextWrapped($"last fired: {meta.LastFired}");
            if (meta.LastError.Length > 0)
                ImGui.TextColored(new Vector4(0.9f, 0.4f, 0.3f, 1f), $"last error: {meta.LastError}");
        }

        ImGui.Separator();
        ImGui.TextDisabled("Try an expression");
        ImGui.SetNextItemWidth(-60f);
        bool submitted = ImGui.InputText("##metaexpr", ref _metaExpression, 512, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Eval") || submitted) && _metaExpression.Length > 0)
            _metaResult = meta.Expressions.Evaluate(_metaExpression);
        if (_metaResult.Length > 0)
            ImGui.TextWrapped($"= {_metaResult}");
    }

    private void DrawNavigation(NavigationSettings navigation)
    {
        bool enabled = navigation.Enabled;
        if (ImGui.Checkbox("Enable navigation", ref enabled))
            controller.Update(p => p with { Navigation = p.Navigation with { Enabled = enabled } });

        int mode = (int)navigation.Mode;
        if (ImGui.Combo("Route mode", ref mode, RouteModeNames, RouteModeNames.Length))
            controller.Update(p => p with { Navigation = p.Navigation with { Mode = (RouteMode)mode } });

        float arrival = (float)navigation.ArrivalDistanceMeters;
        if (ImGui.SliderFloat("Arrival distance (m)", ref arrival, 0.5f, 5f, "%.1f"))
            controller.Update(p => p with { Navigation = p.Navigation with { ArrivalDistanceMeters = arrival } });

        float turn = navigation.TurnToleranceDegrees;
        if (ImGui.SliderFloat("Turn in place beyond (deg)", ref turn, 10f, 90f, "%.0f"))
            controller.Update(p => p with { Navigation = p.Navigation with { TurnToleranceDegrees = turn } });

        float lookahead = (float)navigation.LookaheadMeters;
        if (ImGui.SliderFloat("Corner lookahead (m)", ref lookahead, 0f, 10f, "%.1f"))
            controller.Update(p => p with { Navigation = p.Navigation with { LookaheadMeters = lookahead } });

        float portalDelay = (float)navigation.PostPortalDelaySeconds;
        if (ImGui.SliderFloat("Settle after a portal (s)", ref portalDelay, 0f, 15f, "%.1f"))
            controller.Update(p => p with { Navigation = p.Navigation with { PostPortalDelaySeconds = portalDelay } });

        ImGui.Spacing();
        ImGui.TextDisabled("Following (a name, or 'leader'; empty walks the route)");
        string follow = navigation.Follow;
        if (ImGui.InputText("Follow", ref follow, 64))
            controller.Update(p => p with { Navigation = p.Navigation with { Follow = follow } });
        float followStop = navigation.FollowStopMeters;
        if (ImGui.SliderFloat("Stop within (m)", ref followStop, 1f, 20f, "%.0f"))
            controller.Update(p => p with { Navigation = p.Navigation with { FollowStopMeters = followStop, FollowResumeMeters = Math.Max(p.Navigation.FollowResumeMeters, followStop + 1f) } });

        ImGui.Spacing();
        ImGui.TextDisabled("Doors");
        DoorSettings doors = controller.Profile.Doors;
        bool openDoors = doors.Enabled;
        if (ImGui.Checkbox("Open doors in the way", ref openDoors))
            controller.Update(p => p with { Doors = p.Doors with { Enabled = openDoors } });
        ImGui.SameLine();
        bool lockpicks = doors.UseLockpicks;
        if (ImGui.Checkbox("Pick locked ones", ref lockpicks))
            controller.Update(p => p with { Doors = p.Doors with { UseLockpicks = lockpicks } });
        float doorRange = doors.RangeMeters;
        if (ImGui.SliderFloat("Door range (m)", ref doorRange, 1f, 10f, "%.0f"))
            controller.Update(p => p with { Doors = p.Doors with { RangeMeters = doorRange } });
        ImGui.TextDisabled("A mode change applies the next time a route is loaded.");
    }

    private static IReadOnlyList<string> SplitNames(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<string> Swap(IReadOnlyList<string> items, int a, int b)
    {
        string[] copy = [.. items];
        (copy[a], copy[b]) = (copy[b], copy[a]);
        return copy;
    }
}
