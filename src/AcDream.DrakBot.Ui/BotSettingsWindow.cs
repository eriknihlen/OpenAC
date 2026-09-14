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
/// Profile editing laid out the way RynthAi's advanced settings are: a
/// list of sections down the left, the chosen section's controls on the
/// right. Every widget reads the live profile and writes back through the
/// controller, so a change takes effect on the next tick; "Save" persists
/// it under the profile's name. Monsters, the meta rules, routes and items
/// have windows of their own.
/// </summary>
public sealed class BotSettingsWindow(BotController controller)
{
    private static readonly string[] Sections =
    [
        "Recharge", "Combat", "Ranges", "Buffing", "Looting", "Navigation", "Pets & Doors", "Priorities",
    ];
    private int _section;
    private static readonly string[] StyleNames = ["Melee", "Missile", "Magic"];
    private static readonly string[] HeightNames = ["High", "Medium", "Low"];
    private static readonly string[] RouteModeNames = ["Loop", "Ping-pong", "Once"];
    private static readonly string[] WarSpellPathNames = ["Straight (bolts, streaks)", "Arc (lobbed)"];

    private bool _open;
    private string _profileName = string.Empty;
    private string _petDevice = string.Empty;

    private static bool TryParseTiers(string text, out int[] tiers)
    {
        string[] parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        tiers = new int[parts.Length];
        for (int index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], out tiers[index]))
                return false;
        }
        return tiers.Length == 8;
    }
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
        ImGui.SetNextWindowSize(new Vector2(640f, 460f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Settings##drakbot", ref _open))
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
        ImGui.Separator();

        ImGui.BeginChild("sections", new Vector2(130f, 0f), ImGuiChildFlags.Borders);
        for (int index = 0; index < Sections.Length; index++)
        {
            if (ImGui.Selectable(Sections[index], _section == index))
                _section = index;
        }
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("content", new Vector2(0f, 0f), ImGuiChildFlags.Borders);
        ImGui.TextColored(new Vector4(0.4f, 0.7f, 1.0f, 1.0f), $"Settings > {Sections[_section]}");
        ImGui.Separator();
        ImGui.Spacing();
        switch (Sections[_section])
        {
            case "Recharge":
                DrawRecharge(profile.Vitals);
                break;
            case "Combat":
                DrawCombat(profile.Combat);
                break;
            case "Ranges":
                DrawRanges(profile.Combat);
                break;
            case "Buffing":
                DrawBuffs(profile.Buffs);
                break;
            case "Looting":
                DrawLoot(profile.Loot);
                break;
            case "Navigation":
                DrawNavigation(profile.Navigation);
                break;
            case "Pets & Doors":
                DrawPets(profile.Pets);
                ImGui.Separator();
                DrawDoors(profile.Doors);
                break;
            case "Priorities":
                DrawPriorities(profile.Priorities);
                break;
        }
        ImGui.EndChild();
        ImGui.End();
    }

    /// <summary>Opens the window on a named section, for the dashboard's right-click shortcuts.</summary>
    public void Open(string section)
    {
        int index = Array.FindIndex(Sections, name => name.Equals(section, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            _section = index;
        _open = true;
    }

    private void DrawPriorities(PrioritySettings priorities)
    {
        ImGui.TextDisabled("Survival and buffing always come first; these lift navigation or looting above combat.");
        bool boostNav = priorities.BoostNavigation;
        if (ImGui.Checkbox("Walk the route before fighting", ref boostNav))
            controller.Update(p => p with { Priorities = p.Priorities with { BoostNavigation = boostNav } });
        bool boostLoot = priorities.BoostLooting;
        if (ImGui.Checkbox("Loot before fighting (unless a fight is already under way)", ref boostLoot))
            controller.Update(p => p with { Priorities = p.Priorities with { BoostLooting = boostLoot } });
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
        ImGui.TextDisabled("Weapons are named in the Items window; the monster list is its own window.");

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

    private void DrawRanges(CombatSettings combat)
    {
        float engage = combat.EngageDistance;
        if (ImGui.SliderFloat("Engage distance (m)", ref engage, 3f, 60f, "%.0f"))
            controller.Update(p => p with { Combat = p.Combat with { EngageDistance = engage } });
        float maxHeight = combat.MaxHeightDifferenceMeters;
        if (ImGui.SliderFloat("Ignore above/below (m)", ref maxHeight, 0f, 15f, "%.1f"))
            controller.Update(p => p with { Combat = p.Combat with { MaxHeightDifferenceMeters = maxHeight } });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A hostile more than this far above or below the character is left alone: the floor overhead, the pit below. 0 fights at any height.");
        float ringRange = combat.RingRangeMeters;
        if (ImGui.SliderFloat("Ring range (m)", ref ringRange, 0f, 20f, "%.0f"))
            controller.Update(p => p with { Combat = p.Combat with { RingRangeMeters = ringRange } });
        int minRing = combat.MinRingTargets;
        if (ImGui.SliderInt("Ring at this many", ref minRing, 1, 10))
            controller.Update(p => p with { Combat = p.Combat with { MinRingTargets = minRing } });

        ImGui.Spacing();
        ImGui.TextDisabled("Reach");
        float meleeRange = combat.MeleeRangeMeters;
        if (ImGui.SliderFloat("Melee reach (m)", ref meleeRange, 1f, 6f, "%.1f"))
            controller.Update(p => p with { Combat = p.Combat with { MeleeRangeMeters = meleeRange } });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A melee target farther than this is walked up to first.");
        float approachRange = combat.ApproachRangeMeters;
        if (ImGui.SliderFloat("Walk up to (m)", ref approachRange, 1f, 25f, "%.0f"))
            controller.Update(p => p with { Combat = p.Combat with { ApproachRangeMeters = approachRange } });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A melee target out of reach is walked up to only when it is this close; farther ones are left alone.\nA ranged target with no line of sight is never walked to.");
        int approachTimeout = (int)Math.Round(combat.ApproachTimeoutSeconds);
        if (ImGui.SliderInt("Give up walking after (s)", ref approachTimeout, 3, 60))
            controller.Update(p => p with { Combat = p.Combat with { ApproachTimeoutSeconds = approachTimeout } });

        ImGui.Spacing();
        DrawLineOfSight(combat.LineOfSight);
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

        ImGui.TextDisabled("Skill needed per tier I..VIII (buffed skill in the spell's school)");
        string buffTiers = string.Join(", ", controller.Profile.SpellTiers.BuffMinimums);
        if (ImGui.InputText("Buff tiers", ref buffTiers, 96, ImGuiInputTextFlags.EnterReturnsTrue) && TryParseTiers(buffTiers, out int[] parsedBuff))
            controller.Update(p => p with { SpellTiers = p.SpellTiers with { BuffMinimums = parsedBuff } });
        string combatTiers = string.Join(", ", controller.Profile.SpellTiers.CombatMinimums);
        if (ImGui.InputText("Combat tiers", ref combatTiers, 96, ImGuiInputTextFlags.EnterReturnsTrue) && TryParseTiers(combatTiers, out int[] parsedCombat))
            controller.Update(p => p with { SpellTiers = p.SpellTiers with { CombatMinimums = parsedCombat } });

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
            ImGui.SetTooltip("A loot profile by name (no extension) from the bot's loot folder, or the client's vtank folder; when set it decides instead of the rules below.");
        ImGui.SameLine();
        if (ImGui.Button("Open folder##loot"))
            controller.Files.TryOpen(BotFiles.LootFolder, out _);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Drop .utl files here:\n{controller.Files.PathOf(BotFiles.LootFolder) ?? "(no file storage on this host)"}");

        ImGui.Spacing();
        ImGui.TextDisabled("Pack");
        bool stack = controller.Profile.Inventory.AutoStack;
        if (ImGui.Checkbox("Merge partial stacks", ref stack))
            controller.Update(p => p with { Inventory = p.Inventory with { AutoStack = stack } });
        ImGui.SameLine();
        bool cram = controller.Profile.Inventory.AutoCram;
        if (ImGui.Checkbox("Move loose items into side packs", ref cram))
            controller.Update(p => p with { Inventory = p.Inventory with { AutoCram = cram } });

        ImGui.Spacing();
        ImGui.TextDisabled("Salvage");
        bool salvage = controller.Profile.Salvage.Enabled;
        if (ImGui.Checkbox("Salvage what the rules say", ref salvage))
            controller.Update(p => p with { Salvage = p.Salvage with { Enabled = salvage } });
        ImGui.SameLine();
        bool combine = controller.Profile.Salvage.CombineBags;
        if (ImGui.Checkbox("Merge partial bags", ref combine))
            controller.Update(p => p with { Salvage = p.Salvage with { CombineBags = combine } });

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
        ImGui.TextDisabled("Markers drawn over the world");
        bool markers = navigation.ShowMarkers;
        if (ImGui.Checkbox("Show route markers", ref markers))
            controller.Update(p => p with { Navigation = p.Navigation with { ShowMarkers = markers } });
        float ring = navigation.MarkerRingMeters;
        if (ImGui.SliderFloat("Ring radius (m)", ref ring, 0.25f, 5f, "%.2f"))
            controller.Update(p => p with { Navigation = p.Navigation with { MarkerRingMeters = ring } });
        float thickness = navigation.MarkerLineThickness;
        if (ImGui.SliderFloat("Line thickness", ref thickness, 1f, 8f, "%.0f"))
            controller.Update(p => p with { Navigation = p.Navigation with { MarkerLineThickness = thickness } });
        float height = navigation.MarkerHeightOffset;
        if (ImGui.SliderFloat("Height offset (m)", ref height, -3f, 3f, "%.2f"))
            controller.Update(p => p with { Navigation = p.Navigation with { MarkerHeightOffset = height } });

        ImGui.Spacing();
        bool patrolOnLogin = navigation.PatrolOnLogin;
        if (ImGui.Checkbox("Patrol on login (start a dungeon patrol when the character appears in a dungeon)", ref patrolOnLogin))
            controller.Update(p => p with { Navigation = p.Navigation with { PatrolOnLogin = patrolOnLogin } });

        ImGui.Spacing();
        ImGui.TextDisabled("Following (a name, or 'leader'; empty walks the route)");
        string follow = navigation.Follow;
        if (ImGui.InputText("Follow", ref follow, 64))
            controller.Update(p => p with { Navigation = p.Navigation with { Follow = follow } });
        float followStop = navigation.FollowStopMeters;
        if (ImGui.SliderFloat("Stop within (m)", ref followStop, 1f, 20f, "%.0f"))
            controller.Update(p => p with { Navigation = p.Navigation with { FollowStopMeters = followStop, FollowResumeMeters = Math.Max(p.Navigation.FollowResumeMeters, followStop + 1f) } });

        ImGui.TextDisabled("A mode change applies the next time a route is loaded; the Navigation window edits routes.");
    }

    private void DrawDoors(DoorSettings doors)
    {
        ImGui.TextDisabled("Doors");
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
